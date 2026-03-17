using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Structs;
using SoundFlow.Utils;
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
        var waveSamples = LoadWhisperInputSamples(request.AudioFilePath);
        WaveFileUtility.WritePcm16WaveFile(waveStream, waveSamples, sampleRate: 16000, channels: 1);

        waveStream.Position = 0;
        RuntimeOptions.LibraryPath = Path.Combine(AppContext.BaseDirectory, "runtimes");
        var runtimeOrder = GetRuntimeOrder(request.RuntimePreference);
        RuntimeOptions.RuntimeLibraryOrder = runtimeOrder;
        EnsureOpenVinoRuntimePathOnProcessPath(request.OpenVinoRuntimePath, runtimeOrder);
        var openVinoEncoderPath = TryResolveOpenVinoEncoderPath(request.ModelPath);

        await WriteRuntimeDiagnosticsAsync(
            request,
            runtimeOrder,
            openVinoEncoderPath,
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
                openVinoEncoderPath,
                cancellationToken).ConfigureAwait(false);

            return result with
            {
                ModelPath = request.ModelPath,
                UsedRuntime = usedRuntime
            };
        }
    }

    private static float[] LoadWhisperInputSamples(string audioFilePath)
    {
        using var audioEngine = new MiniAudioEngine(Array.Empty<MiniAudioBackend>());
        using var inputStream = File.OpenRead(audioFilePath);
        using var decoder = audioEngine.CreateDecoder(inputStream, out var detectedFormat, hintFormat: null);

        var sourceChannels = decoder.Channels > 0 ? decoder.Channels : detectedFormat.Channels;
        var sourceSampleRate = decoder.SampleRate > 0 ? decoder.SampleRate : detectedFormat.SampleRate;
        if (sourceChannels <= 0 || sourceSampleRate <= 0)
        {
            throw new InvalidOperationException($"Unable to determine audio format for '{audioFilePath}'.");
        }

        var readBuffer = new float[Math.Max(4096, sourceChannels * 4096)];
        var samples = new List<float>(readBuffer.Length * 4);
        while (true)
        {
            var samplesRead = decoder.Decode(readBuffer);
            if (samplesRead <= 0)
            {
                break;
            }

            for (var sampleIndex = 0; sampleIndex < samplesRead; sampleIndex++)
            {
                samples.Add(readBuffer[sampleIndex]);
            }
        }

        var monoSamples = NormalizeToMono(samples.ToArray(), sourceChannels);
        return sourceSampleRate == 16000
            ? monoSamples
            : MathHelper.ResampleLinear(monoSamples, channels: 1, sourceRate: sourceSampleRate, targetRate: 16000);
    }

    private static float[] NormalizeToMono(float[] samples, int channels)
    {
        if (channels <= 1)
        {
            return samples;
        }

        return ChannelMixer.Mix(samples, channels, targetChannels: 1);
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
        string? openVinoEncoderPath,
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
            $"Whisper diagnostics: configured OpenVINO runtime path = {request.OpenVinoRuntimePath}",
            $"Whisper diagnostics: OpenVINO runtime path exists = {!string.IsNullOrWhiteSpace(request.OpenVinoRuntimePath) && Directory.Exists(request.OpenVinoRuntimePath)}",
            $"Whisper diagnostics: process PATH contains OpenVINO runtime path = {ContainsPathEntry(Environment.GetEnvironmentVariable("PATH"), request.OpenVinoRuntimePath)}",
            $"Whisper diagnostics: model path = {request.ModelPath}",
            $"Whisper diagnostics: model file exists = {File.Exists(request.ModelPath)}",
            $"Whisper diagnostics: OpenVINO encoder xml path = {openVinoEncoderPath ?? "<auto>"}",
            $"Whisper diagnostics: OpenVINO encoder xml exists = {!string.IsNullOrWhiteSpace(openVinoEncoderPath) && File.Exists(openVinoEncoderPath)}",
            $"Whisper diagnostics: OpenVINO encoder bin exists = {HasOpenVinoWeights(openVinoEncoderPath)}",
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
        string? openVinoEncoderPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ProcessOnceAsync(
                whisperFactory,
                waveStream,
                request.LanguageMode,
                openVinoEncoderPath,
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
                        openVinoEncoderPath,
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
        string? openVinoEncoderPath,
        int threadCount,
        CancellationToken cancellationToken)
    {
        waveStream.Position = 0;

        var builder = whisperFactory.CreateBuilder()
            .WithThreads(Math.Max(1, threadCount));

        if (!string.IsNullOrWhiteSpace(openVinoEncoderPath) && File.Exists(openVinoEncoderPath))
        {
            builder = builder.WithOpenVinoEncoder(openVinoEncoderPath, "GPU", null!);
        }

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

    private static string? TryResolveOpenVinoEncoderPath(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(modelPath);
        var modelFileNameWithoutExtension = Path.GetFileNameWithoutExtension(modelPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(modelFileNameWithoutExtension))
        {
            return null;
        }

        var candidatePaths = new[]
        {
            Path.Combine(directory, $"{modelFileNameWithoutExtension}-encoder.xml"),
            Path.Combine(directory, $"{modelFileNameWithoutExtension}-encoder-openvino.xml")
        };

        return candidatePaths.FirstOrDefault(File.Exists);
    }

    private static void EnsureOpenVinoRuntimePathOnProcessPath(
        string? openVinoRuntimePath,
        IReadOnlyCollection<RuntimeLibrary> runtimeOrder)
    {
        if (string.IsNullOrWhiteSpace(openVinoRuntimePath) ||
            !runtimeOrder.Contains(RuntimeLibrary.OpenVino) ||
            !Directory.Exists(openVinoRuntimePath))
        {
            return;
        }

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (ContainsPathEntry(currentPath, openVinoRuntimePath))
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            "PATH",
            $"{openVinoRuntimePath}{Path.PathSeparator}{currentPath}",
            EnvironmentVariableTarget.Process);
    }

    private static bool ContainsPathEntry(string? currentPath, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(currentPath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidatePath);
        return currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => string.Equals(
                Path.TrimEndingDirectorySeparator(entry),
                normalizedCandidate,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasOpenVinoWeights(string? openVinoEncoderPath)
    {
        if (string.IsNullOrWhiteSpace(openVinoEncoderPath))
        {
            return false;
        }

        var weightsPath = Path.ChangeExtension(openVinoEncoderPath, ".bin");
        return !string.IsNullOrWhiteSpace(weightsPath) && File.Exists(weightsPath);
    }

}
