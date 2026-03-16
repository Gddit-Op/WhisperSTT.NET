using Avalonia;
using Avalonia.Logging;
using WhisperSTT.App.Services;

namespace WhisperSTT.App;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\WhisperSTT.App";

    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            NativeMethods.MessageBox(IntPtr.Zero, "WhisperSTT is already running.", "WhisperSTT", NativeMethods.MbOk | NativeMethods.MbIconInformation);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace(LogEventLevel.Error);
    }
}
