using System.Runtime.InteropServices;
using System.Text;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class MediaPlayerAudioPreviewService : IAudioPreviewService
{
    private const string Alias = "whisper_preview";
    private string? _loadedFilePath;

    public bool IsLoaded => !string.IsNullOrWhiteSpace(_loadedFilePath);

    public string? LoadedFilePath => _loadedFilePath;

    public void Load(string filePath)
    {
        if (string.Equals(_loadedFilePath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Unload();
        ExecuteMciCommand($"open \"{filePath}\" alias {Alias}");
        _loadedFilePath = filePath;
    }

    public void Play()
    {
        EnsureLoaded();
        ExecuteMciCommand($"play {Alias}");
    }

    public void Unload()
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            ExecuteMciCommand($"close {Alias}");
        }
        catch
        {
            // Ignore cleanup failures while resetting preview state.
        }
        finally
        {
            _loadedFilePath = null;
        }
    }

    public void Pause()
    {
        EnsureLoaded();
        ExecuteMciCommand($"pause {Alias}");
    }

    public void Stop()
    {
        EnsureLoaded();
        ExecuteMciCommand($"stop {Alias}");
        ExecuteMciCommand($"seek {Alias} to start");
    }

    public void Dispose()
    {
        Unload();
    }

    private static void ExecuteMciCommand(string command)
    {
        var errorCode = mciSendString(command, null, 0, IntPtr.Zero);
        if (errorCode == 0)
        {
            return;
        }

        var errorBuilder = new StringBuilder(256);
        var errorResolved = mciGetErrorString(errorCode, errorBuilder, errorBuilder.Capacity);
        var errorText = errorResolved
            ? errorBuilder.ToString()
            : $"MCI error code {errorCode}.";

        throw new InvalidOperationException($"Audio preview command failed: {errorText}");
    }

    private void EnsureLoaded()
    {
        if (!IsLoaded)
        {
            throw new InvalidOperationException("No audio file is loaded for preview.");
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool mciGetErrorString(int errorCode, StringBuilder errorText, int errorTextSize);
}
