using Avalonia;
using Avalonia.Logging;
using WhisperSTT.App.Services;

namespace WhisperSTT.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var singleInstanceMutexName = PlatformServices.GetSingleInstanceMutexName();
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, singleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            PlatformServices.NotifyAlreadyRunning();
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
