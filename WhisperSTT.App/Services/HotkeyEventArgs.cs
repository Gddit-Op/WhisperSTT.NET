namespace WhisperSTT.App.Services;

public sealed class HotkeyEventArgs : EventArgs
{
    public HotkeyEventArgs(string actionName)
    {
        ActionName = actionName;
    }

    public string ActionName { get; }
}
