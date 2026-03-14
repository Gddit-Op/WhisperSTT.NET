using System.IO;
using NAudio.Wave;
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
        using (var audioReader = new AudioFileReader(request.AudioFilePath))
        using (var resampler = new MediaFoundationResampler(audioReader, new WaveFormat(16000, 16, 1)))
        {
            resampler.ResamplerQuality = 60;
            WaveFileWriter.WriteWavFileToStream(waveStream, resampler);
        }

        waveStream.Position = 0;
        RuntimeOptions.RuntimeLibraryOrder = GetRuntimeOrder(request.RuntimePreference);

        using var whisperFactory = WhisperFactory.FromPath(request.ModelPath);
        var usedRuntime = RuntimeOptions.LoadedLibrary?.ToString()
            ?? WhisperFactory.GetRuntimeInfo()?.ToString()
            ?? "unknown";
        var builder = whisperFactory.CreateBuilder()
            .WithThreads(Math.Max(1, request.ThreadCount));

        if (request.LanguageMode == LanguageMode.Auto)
        {
            builder = builder.WithLanguageDetection();
        }
        else
        {
            builder = builder.WithLanguage(request.LanguageMode == LanguageMode.De ? "de" : "en");
        }

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

        return new TranscriptionResult(text, segments, duration, request.ModelPath, detectedLanguage, usedRuntime);
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
}
