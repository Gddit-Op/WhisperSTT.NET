namespace WhisperSTT.Core.Services;

public sealed class ApplicationPaths
{
    public ApplicationPaths(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WhisperNET");

        ModelsDirectory = Path.Combine(RootDirectory, "models");
        TempDirectory = Path.Combine(RootDirectory, "temp");
        ConfigPath = Path.Combine(RootDirectory, "config.json");
        HistoryPath = Path.Combine(RootDirectory, "history.log");
        LogPath = Path.Combine(RootDirectory, "app.log");
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
    }
}
