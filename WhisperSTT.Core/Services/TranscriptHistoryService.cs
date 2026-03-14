namespace WhisperSTT.Core.Services;

public sealed class TranscriptHistoryService : ITranscriptHistoryService
{
    private readonly ApplicationPaths _paths;

    public TranscriptHistoryService(ApplicationPaths paths)
    {
        _paths = paths;
        _paths.EnsureCreated();
    }

    public string HistoryPath => _paths.HistoryPath;

    public async Task AppendAsync(string transcript, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        var entry = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {transcript}{Environment.NewLine}";
        await File.AppendAllTextAsync(HistoryPath, entry, cancellationToken).ConfigureAwait(false);
    }
}
