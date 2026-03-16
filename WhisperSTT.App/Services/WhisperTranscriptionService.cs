using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
    private readonly IActivityLogService? _activityLogService;

    public WhisperTranscriptionService(IActivityLogService? activityLogService = null)
    {
        _activityLogService = activityLogService;
    }

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
        var sampleProvider = NormalizeToMono(audioReader.ToSampleProvider());

        if (sampleProvider.WaveFormat.SampleRate != 16000)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 16000);
        }

        var pcmProvider = new SampleToWaveProvider16(sampleProvider);
        WaveFileWriter.WriteWavFileToStream(waveStream, pcmProvider);

        waveStream.Position = 0;
        RuntimeOptions.LibraryPath = Path.Combine(AppContext.BaseDirectory, "runtimes");
        var runtimeOrder = GetRuntimeOrder(request.RuntimePreference);
        RuntimeOptions.RuntimeLibraryOrder = runtimeOrder;

        await WriteRuntimeDiagnosticsAsync(
            request,
            runtimeOrder,
            cancellationToken).ConfigureAwait(false);

        WhisperFactory whisperFactory;
        try
        {
            whisperFactory = WhisperFactory.FromPath(request.ModelPath);
        }
        catch (Exception exception)
        {
            await TryWriteDiagnosticLineAsync(
                $"WhisperFactory.FromPath failed: {exception.GetType().Name}: {exception.Message}",
                request.EnableDiagnosticLogging,
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        using (whisperFactory)
        {
            await TryWriteDiagnosticLineAsync(
                $"Whisper runtime after WhisperFactory.FromPath: {RuntimeOptions.LoadedLibrary?.ToString() ?? WhisperFactory.GetRuntimeInfo()?.ToString() ?? "unknown"}.",
                request.EnableDiagnosticLogging,
                cancellationToken).ConfigureAwait(false);

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

    private static ISampleProvider NormalizeToMono(ISampleProvider sampleProvider)
    {
        return sampleProvider.WaveFormat.Channels switch
        {
            <= 1 => sampleProvider,
            2 => new StereoToMonoSampleProvider(sampleProvider),
            _ => new MultiChannelToMonoSampleProvider(sampleProvider)
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

    private async Task WriteRuntimeDiagnosticsAsync(
        TranscriptionRequest request,
        IReadOnlyList<RuntimeLibrary> runtimeOrder,
        CancellationToken cancellationToken)
    {
        if (!request.EnableDiagnosticLogging || _activityLogService is null)
        {
            return;
        }

        var runtimeLibraryPath = RuntimeOptions.LibraryPath ?? string.Empty;
        var runtimeIdentifier = GetRuntimeIdentifier();
        var lines = new List<string>
        {
            $"Whisper diagnostics: base directory = {AppContext.BaseDirectory}",
            $"Whisper diagnostics: runtime library path = {runtimeLibraryPath}",
            $"Whisper diagnostics: runtime identifier = {runtimeIdentifier}",
            $"Whisper diagnostics: process architecture = {RuntimeInformation.ProcessArchitecture}",
            $"Whisper diagnostics: framework = {RuntimeInformation.FrameworkDescription}",
            $"Whisper diagnostics: configured runtime preference = {request.RuntimePreference}",
            $"Whisper diagnostics: runtime order = {string.Join(", ", runtimeOrder)}",
            $"Whisper diagnostics: model path = {request.ModelPath}",
            $"Whisper diagnostics: model file exists = {File.Exists(request.ModelPath)}",
            $"Whisper diagnostics: audio path = {request.AudioFilePath}",
            $"Whisper diagnostics: audio file exists = {File.Exists(request.AudioFilePath)}",
            $"Whisper diagnostics: loaded runtime before WhisperFactory.FromPath = {RuntimeOptions.LoadedLibrary?.ToString() ?? "null"}"
        };

        foreach (var runtimeLibrary in runtimeOrder)
        {
            lines.Add(DescribeRuntimeCandidate(runtimeLibrary, runtimeLibraryPath, runtimeIdentifier));
        }

        foreach (var line in lines)
        {
            await TryWriteDiagnosticLineAsync(line, enabled: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryWriteDiagnosticLineAsync(
        string line,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!enabled || _activityLogService is null)
        {
            return;
        }

        try
        {
            await _activityLogService.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostic logging must never block transcription.
        }
    }

    private static string DescribeRuntimeCandidate(
        RuntimeLibrary runtimeLibrary,
        string runtimeLibraryPath,
        string runtimeIdentifier)
    {
        var candidateDirectory = GetRuntimeCandidateDirectory(runtimeLibrary, runtimeLibraryPath, runtimeIdentifier);
        var whisperDllPath = Path.Combine(candidateDirectory, "whisper.dll");
        var whisperFiles = Directory.Exists(candidateDirectory)
            ? Directory.EnumerateFiles(candidateDirectory, "*whisper.dll")
                .Select(Path.GetFileName)
                .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
                .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        var whisperFilesText = whisperFiles.Length == 0
            ? "none"
            : string.Join(", ", whisperFiles);

        return $"Whisper diagnostics: candidate {runtimeLibrary} directory = {candidateDirectory}; exists = {Directory.Exists(candidateDirectory)}; whisper.dll exists = {File.Exists(whisperDllPath)}; whisper-related files = [{whisperFilesText}]";
    }

    private static string GetRuntimeCandidateDirectory(
        RuntimeLibrary runtimeLibrary,
        string runtimeLibraryPath,
        string runtimeIdentifier)
    {
        return runtimeLibrary switch
        {
            RuntimeLibrary.Cpu => Path.Combine(runtimeLibraryPath, runtimeIdentifier),
            RuntimeLibrary.Cuda => Path.Combine(runtimeLibraryPath, "cuda", runtimeIdentifier),
            RuntimeLibrary.OpenVino => Path.Combine(runtimeLibraryPath, "openvino", runtimeIdentifier),
            RuntimeLibrary.Vulkan => Path.Combine(runtimeLibraryPath, "vulkan", runtimeIdentifier),
            _ => runtimeLibraryPath
        };
    }

    private static string GetRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        return $"win-{architecture}";
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

    private sealed class MultiChannelToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;

        public MultiChannelToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var sourceBuffer = new float[count * _sourceChannels];
            var samplesRead = _source.Read(sourceBuffer, 0, sourceBuffer.Length);
            if (samplesRead <= 0)
            {
                return 0;
            }

            var framesRead = samplesRead / _sourceChannels;
            for (var frameIndex = 0; frameIndex < framesRead; frameIndex++)
            {
                float sum = 0;
                var sourceOffset = frameIndex * _sourceChannels;
                for (var channelIndex = 0; channelIndex < _sourceChannels; channelIndex++)
                {
                    sum += sourceBuffer[sourceOffset + channelIndex];
                }

                buffer[offset + frameIndex] = sum / _sourceChannels;
            }

            return framesRead;
        }
    }
}
