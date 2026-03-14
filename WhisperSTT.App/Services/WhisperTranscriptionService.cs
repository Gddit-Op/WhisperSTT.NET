using System.IO;
using NAudio.Wave;
using Whisper.net;
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

        using var whisperFactory = WhisperFactory.FromPath(request.ModelPath);
        var builder = whisperFactory.CreateBuilder()
            .WithThreads(Math.Max(1, request.ThreadCount));

        if (request.LanguageMode != LanguageMode.Auto)
        {
            builder = builder.WithLanguage(request.LanguageMode == LanguageMode.De ? "de" : "en");
        }

        using var processor = builder.Build();

        var segments = new List<TranscriptionSegment>();
        await foreach (var segment in processor.ProcessAsync(waveStream))
        {
            cancellationToken.ThrowIfCancellationRequested();
            segments.Add(new TranscriptionSegment(segment.Start, segment.End, segment.Text));
        }

        var text = string.Join(" ", segments.Select(segment => segment.Text).Where(segment => !string.IsNullOrWhiteSpace(segment))).Trim();
        var duration = segments.Count == 0 ? TimeSpan.Zero : segments[^1].End;
        return new TranscriptionResult(text, segments, duration, request.ModelPath);
    }
}
