using WhisperSTT.Core.Services;
using Forms = System.Windows.Forms;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataObject = System.Windows.IDataObject;

namespace WhisperSTT.App.Services;

public sealed class ClipboardPasteService : IPasteService
{
    public async Task PasteTextAsync(string text, bool restoreClipboard, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        WpfDataObject? snapshot = null;
        if (restoreClipboard)
        {
            try
            {
                snapshot = WpfClipboard.GetDataObject();
            }
            catch
            {
                snapshot = null;
            }
        }

        WpfClipboard.SetText(text);
        Forms.SendKeys.SendWait("^v");
        await Task.Delay(100, cancellationToken).ConfigureAwait(true);

        if (!restoreClipboard)
        {
            return;
        }

        if (snapshot is null)
        {
            WpfClipboard.Clear();
        }
        else
        {
            WpfClipboard.SetDataObject(snapshot, true);
        }
    }
}
