using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

internal static class PlatformServices
{
    public static string GetSingleInstanceMutexName()
    {
        return OperatingSystem.IsWindows()
            ? @"Local\WhisperSTT.App"
            : "WhisperSTT.App";
    }

    public static void NotifyAlreadyRunning()
    {
        Console.Error.WriteLine("WhisperSTT is already running.");
    }

    public static IGlobalHotkeyService CreateGlobalHotkeyService(IntPtr windowHandle)
    {
        if (OperatingSystem.IsWindows() && windowHandle != IntPtr.Zero)
        {
            return new WindowsGlobalHotkeyService(windowHandle);
        }

        return NoOpGlobalHotkeyService.Instance;
    }

    public static IPasteService CreatePasteService(IActivityLogService? activityLogService = null)
    {
        IPasteAutomationService pasteAutomationService = OperatingSystem.IsWindows()
            ? new WindowsPasteAutomationService(activityLogService)
            : new NoOpPasteAutomationService();

        return new ClipboardPasteService(pasteAutomationService);
    }

    public static IAudioPreviewService CreateAudioPreviewService()
    {
        return OperatingSystem.IsWindows()
            ? new MediaPlayerAudioPreviewService()
            : new NoOpAudioPreviewService();
    }
}

internal interface IGlobalHotkeyService : IDisposable
{
    event EventHandler<HotkeyEventArgs>? HotkeyPressed;

    void ApplySettings(HotkeySettings settings);
}

internal sealed class NoOpGlobalHotkeyService : IGlobalHotkeyService
{
    public static NoOpGlobalHotkeyService Instance { get; } = new();

    private NoOpGlobalHotkeyService()
    {
    }

    public event EventHandler<HotkeyEventArgs>? HotkeyPressed
    {
        add { }
        remove { }
    }

    public void ApplySettings(HotkeySettings settings)
    {
    }

    public void Dispose()
    {
    }
}

internal interface IPasteAutomationService
{
    PasteAutomationTarget CaptureTarget();

    void PasteFromClipboard(PasteAutomationTarget target);
}

internal sealed class NoOpPasteAutomationService : IPasteAutomationService
{
    public PasteAutomationTarget CaptureTarget()
    {
        return PasteAutomationTarget.Empty;
    }

    public void PasteFromClipboard(PasteAutomationTarget target)
    {
        throw new InvalidOperationException(
            "Automatic paste is not available on this platform. The transcript was copied to the clipboard.");
    }
}

internal readonly record struct PasteAutomationTarget(IntPtr ForegroundWindow)
{
    public static PasteAutomationTarget Empty { get; } = new(IntPtr.Zero);
}
