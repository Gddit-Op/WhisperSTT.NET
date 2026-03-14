using System.Runtime.InteropServices;
using WhisperSTT.Core.Services;
using Forms = System.Windows.Forms;

namespace WhisperSTT.App.Services;

public sealed class ClipboardPasteService : IPasteService
{
    private const int ClipboardBusyHResult = unchecked((int)0x800401D0);
    private const int ClipboardRetryCount = 8;
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(75);

    public async Task CopyTextToClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await RunClipboardActionAsync(
            () => Forms.Clipboard.SetText(text),
            cancellationToken,
            throwOnFailure: true).ConfigureAwait(true);
    }

    public async Task PasteTextAsync(string text, bool restoreClipboard, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var snapshot = restoreClipboard
            ? await TryGetClipboardSnapshotAsync(cancellationToken).ConfigureAwait(true)
            : null;

        await RunClipboardActionAsync(
            () => Forms.Clipboard.SetText(text),
            cancellationToken,
            throwOnFailure: true).ConfigureAwait(true);

        Forms.SendKeys.SendWait("^v");
        await Task.Delay(100, cancellationToken).ConfigureAwait(true);

        if (!restoreClipboard)
        {
            return;
        }

        if (snapshot is null)
        {
            await RunClipboardActionAsync(
                Forms.Clipboard.Clear,
                cancellationToken,
                throwOnFailure: false).ConfigureAwait(true);
            return;
        }

        await RunClipboardActionAsync(
            () => Forms.Clipboard.SetDataObject(snapshot, false),
            cancellationToken,
            throwOnFailure: false).ConfigureAwait(true);
    }

    private static async Task<Forms.IDataObject?> TryGetClipboardSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunClipboardFunctionAsync(
                Forms.Clipboard.GetDataObject,
                cancellationToken,
                throwOnFailure: false).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task RunClipboardActionAsync(
        Action action,
        CancellationToken cancellationToken,
        bool throwOnFailure)
    {
        _ = await RunClipboardFunctionAsync(
            () =>
            {
                action();
                return true;
            },
            cancellationToken,
            throwOnFailure).ConfigureAwait(true);
    }

    private static async Task<T?> RunClipboardFunctionAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken,
        bool throwOnFailure)
    {
        for (var attempt = 1; attempt <= ClipboardRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return action();
            }
            catch (COMException exception) when (exception.HResult == ClipboardBusyHResult)
            {
                if (attempt == ClipboardRetryCount)
                {
                    if (!throwOnFailure)
                    {
                        return default;
                    }

                    throw new InvalidOperationException(
                        "Clipboard is busy. Close clipboard managers or other apps using the clipboard and try again.",
                        exception);
                }

                await Task.Delay(ClipboardRetryDelay, cancellationToken).ConfigureAwait(true);
            }
        }

        return default;
    }
}
