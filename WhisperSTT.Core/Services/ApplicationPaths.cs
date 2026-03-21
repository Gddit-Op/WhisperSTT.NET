namespace WhisperSTT.Core.Services;

public sealed class ApplicationPaths
{
    public ApplicationPaths(
        string? rootDirectory = null,
        string? configPath = null,
        string? historyPath = null,
        string? logPath = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WhisperNET");

        ModelsDirectory = Path.Combine(RootDirectory, "models");
        TempDirectory = Path.Combine(RootDirectory, "temp");
        ConfigPath = configPath ?? Path.Combine(RootDirectory, "config.json");
        HistoryPath = historyPath ?? Path.Combine(RootDirectory, "history.log");
        LogPath = logPath ?? Path.Combine(RootDirectory, "app.log");
    }

    public string RootDirectory { get; }

    public string ModelsDirectory { get; }

    public string TempDirectory { get; }

    public string ConfigPath { get; }

    public string HistoryPath { get; }

    public string LogPath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(TempDirectory);
        EnsureParentDirectoryExists(ConfigPath);
        EnsureParentDirectoryExists(HistoryPath);
        EnsureParentDirectoryExists(LogPath);
    }

    private static void EnsureParentDirectoryExists(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
