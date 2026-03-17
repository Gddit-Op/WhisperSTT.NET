using System.Text;

namespace WhisperSTT.App.Services;

internal static class WaveFileUtility
{
    private const short PcmFormatTag = 1;
    private const short BitsPerSample = 16;
    private const int WaveHeaderSizeBytes = 44;

    public static Pcm16WaveFileWriter CreatePcm16Writer(string filePath, int sampleRate, int channels)
    {
        return new Pcm16WaveFileWriter(filePath, sampleRate, channels);
    }

    public static void WritePcm16WaveFile(Stream stream, ReadOnlySpan<float> samples, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(stream);

        WriteWaveHeader(stream, sampleRate, channels, dataLengthBytes: 0);
        var dataLengthBytes = WritePcm16Samples(stream, samples);
        FinalizeWaveHeader(stream, sampleRate, channels, dataLengthBytes);
        stream.Position = 0;
    }

    private static void WriteWaveHeader(Stream stream, int sampleRate, int channels, int dataLengthBytes)
    {
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var blockAlign = (short)(channels * BitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;

        writer.Write("RIFF"u8.ToArray());
        writer.Write(dataLengthBytes + WaveHeaderSizeBytes - 8);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write(PcmFormatTag);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(BitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLengthBytes);
    }

    private static void FinalizeWaveHeader(Stream stream, int sampleRate, int channels, int dataLengthBytes)
    {
        if (!stream.CanSeek)
        {
            return;
        }

        var currentPosition = stream.Position;
        stream.Position = 0;
        WriteWaveHeader(stream, sampleRate, channels, dataLengthBytes);
        stream.Position = currentPosition;
    }

    private static int WritePcm16Samples(Stream stream, ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var pcmBytes = new byte[samples.Length * sizeof(short)];
        for (var sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            var clamped = Math.Clamp(samples[sampleIndex], -1f, 1f);
            var sample = (short)Math.Round(clamped * short.MaxValue);
            BitConverter.TryWriteBytes(pcmBytes.AsSpan(sampleIndex * sizeof(short), sizeof(short)), sample);
        }

        stream.Write(pcmBytes, 0, pcmBytes.Length);
        return pcmBytes.Length;
    }

    internal sealed class Pcm16WaveFileWriter : IDisposable
    {
        private readonly FileStream _stream;
        private readonly int _sampleRate;
        private readonly int _channels;
        private bool _disposed;
        private int _dataLengthBytes;

        public Pcm16WaveFileWriter(string filePath, int sampleRate, int channels)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            _stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _sampleRate = sampleRate;
            _channels = channels;

            WriteWaveHeader(_stream, _sampleRate, _channels, dataLengthBytes: 0);
        }

        public void WriteSamples(ReadOnlySpan<float> samples)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _dataLengthBytes += WritePcm16Samples(_stream, samples);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            FinalizeWaveHeader(_stream, _sampleRate, _channels, _dataLengthBytes);
            _stream.Dispose();
        }
    }
}
