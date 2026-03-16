using System.Globalization;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLayer.NAudioSupport;
using Whisper.net;
using Whisper.net.LibraryLoader;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class WhisperTranscriptionService : ITranscriptionService
{
    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(request.AudioFilePath))
        {
            throw new FileNotFoundException("Audio file not found.", request.AudioFilePath);
        }

        if (!File.Exists(request.ModelPath))
        {
            throw new FileNotFoundException("Whisper model file not found.", request.ModelPath);
        }

        await using var waveStream = new MemoryStream();
        using var audioReader = OpenAudioReader(request.AudioFilePath);
        var sampleProvider = audioReader.ToSampleProvider();
        if (sampleProvider.WaveFormat.Channels > 1)
        {
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider);
        }

        if (sampleProvider.WaveFormat.SampleRate != 16000)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 16000);
        }

        var pcmProvider = new SampleToWaveProvider16(sampleProvider);
        WaveFileWriter.WriteWavFileToStream(waveStream, pcmProvider);

        waveStream.Position = 0;
        RuntimeOptions.LibraryPath = Path.Combine(AppContext.BaseDirectory, "runtimes");
        RuntimeOptions.RuntimeLibraryOrder = GetRuntimeOrder(request.RuntimePreference);

        using var whisperFactory = WhisperFactory.FromPath(request.ModelPath);
        var usedRuntime = RuntimeOptions.LoadedLibrary?.ToString()
            ?? WhisperFactory.GetRuntimeInfo()?.ToString()
            ?? "unknown";
        var result = await ProcessWithFallbackAsync(
            whisperFactory,
            waveStream,
            request,
            cancellationToken).ConfigureAwait(false);

        return result with
        {
            ModelPath = request.ModelPath,
            UsedRuntime = usedRuntime
        };
    }

    private static WaveStream OpenAudioReader(string audioFilePath)
    {
        var extension = Path.GetExtension(audioFilePath);
        return extension.ToLowerInvariant() switch
        {
            ".wav" => new WaveFileReader(audioFilePath),
            ".mp3" => new Mp3FileReaderBase(
                audioFilePath,
                new Mp3FileReaderBase.FrameDecompressorBuilder(format => new Mp3FrameDecompressor(format))),
            _ => new AudioFileReader(audioFilePath)
        };
    }

    private static List<RuntimeLibrary> GetRuntimeOrder(WhisperRuntimePreference preference)
    {
        return preference switch
        {
            WhisperRuntimePreference.Cpu =>
            [
                RuntimeLibrary.Cpu
            ],
            WhisperRuntimePreference.OpenVino =>
            [
                RuntimeLibrary.OpenVino,
                RuntimeLibrary.Cpu
            ],
            WhisperRuntimePreference.Vulkan =>
            [
                RuntimeLibrary.Vulkan,
                RuntimeLibrary.Cpu
            ],
            WhisperRuntimePreference.Cuda =>
            [
                RuntimeLibrary.Cuda,
                RuntimeLibrary.Cpu
            ],
            _ =>
            [
                RuntimeLibrary.Cuda,
                RuntimeLibrary.Vulkan,
                RuntimeLibrary.OpenVino,
                RuntimeLibrary.Cpu
            ]
        };
    }

    private static async Task<TranscriptionResult> ProcessWithFallbackAsync(
        WhisperFactory whisperFactory,
        MemoryStream waveStream,
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ProcessOnceAsync(
                whisperFactory,
                waveStream,
                request.LanguageMode,
                request.ThreadCount,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WhisperProcessingException exception) when (ShouldRetryWithoutLanguageDetection(request.LanguageMode, exception))
        {
            foreach (var fallbackLanguage in GetFallbackLanguages())
            {
                try
                {
                    var fallbackResult = await ProcessOnceAsync(
                        whisperFactory,
                        waveStream,
                        fallbackLanguage,
                        request.ThreadCount,
                        cancellationToken).ConfigureAwait(false);

                    return string.IsNullOrWhiteSpace(fallbackResult.DetectedLanguage)
                        ? fallbackResult with { DetectedLanguage = ToLanguageCode(fallbackLanguage) }
                        : fallbackResult;
                }
                catch (WhisperProcessingException)
                {
                    // Try the next explicit language fallback.
                }
            }

            throw;
        }
    }

    private static async Task<TranscriptionResult> ProcessOnceAsync(
        WhisperFactory whisperFactory,
        MemoryStream waveStream,
        LanguageMode languageMode,
        int threadCount,
        CancellationToken cancellationToken)
    {
        waveStream.Position = 0;

        var builder = whisperFactory.CreateBuilder()
            .WithThreads(Math.Max(1, threadCount));

        builder = languageMode switch
        {
            LanguageMode.Auto => builder.WithLanguageDetection(),
            _ => builder.WithLanguage(ToLanguageCode(languageMode))
        };

        using var processor = builder.Build();

        var segments = new List<TranscriptionSegment>();
        var detectedLanguages = new List<string>();
        await foreach (var segment in processor.ProcessAsync(waveStream))
        {
            cancellationToken.ThrowIfCancellationRequested();
            segments.Add(new TranscriptionSegment(segment.Start, segment.End, segment.Text));

            if (!string.IsNullOrWhiteSpace(segment.Language))
            {
                detectedLanguages.Add(segment.Language);
            }
        }

        var text = string.Join(" ", segments.Select(segment => segment.Text).Where(segment => !string.IsNullOrWhiteSpace(segment))).Trim();
        var duration = segments.Count == 0 ? TimeSpan.Zero : segments[^1].End;
        var detectedLanguage = detectedLanguages
            .GroupBy(language => language, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();

        return new TranscriptionResult(text, segments, duration, string.Empty, detectedLanguage, null);
    }

    private static bool ShouldRetryWithoutLanguageDetection(LanguageMode languageMode, WhisperProcessingException exception)
    {
        return languageMode == LanguageMode.Auto &&
               exception.Message.Contains("auto-detect language", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<LanguageMode> GetFallbackLanguages()
    {
        var preferred = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase)
            ? LanguageMode.De
            : LanguageMode.En;
        var secondary = preferred == LanguageMode.De ? LanguageMode.En : LanguageMode.De;
        return [preferred, secondary];
    }

    private static string ToLanguageCode(LanguageMode languageMode)
    {
        return languageMode == LanguageMode.De ? "de" : "en";
    }
}
