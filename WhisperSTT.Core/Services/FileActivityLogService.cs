namespace WhisperSTT.Core.Services;

public sealed class FileActivityLogService : IActivityLogService
{
    private readonly ApplicationPaths _paths;

    public FileActivityLogService(ApplicationPaths paths)
    {
        _paths = paths;
        _paths.EnsureCreated();
    }

    public string LogPath => _paths.LogPath;

    public async Task WriteAsync(string message, CancellationToken cancellationToken = default)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        await File.AppendAllTextAsync(LogPath, line, cancellationToken).ConfigureAwait(false);
    }
}
