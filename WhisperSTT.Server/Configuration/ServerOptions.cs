namespace WhisperSTT.Server.Configuration;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string DataRoot { get; set; } = @"%APPDATA%\WhisperNET.Server";

    public string LogFilePath { get; set; } = @"%APPDATA%\WhisperNET.Server\server.log";

    public bool PreferServerWhisperConfiguration { get; set; } = true;
}
