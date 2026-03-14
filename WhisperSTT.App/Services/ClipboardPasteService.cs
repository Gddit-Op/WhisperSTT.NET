using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class ClipboardPasteService : IPasteService
{
    private const int ClipboardRetryCount = 8;
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(75);

    public async Task CopyTextToClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await RunClipboardActionAsync(
            clipboard => clipboard.SetTextAsync(text),
            cancellationToken,
            throwOnFailure: true).ConfigureAwait(true);
    }

    public async Task PasteTextAsync(string text, bool restoreClipboard, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        IAsyncDataTransfer? snapshot = null;
        if (restoreClipboard)
        {
            snapshot = await TryGetClipboardSnapshotAsync(cancellationToken).ConfigureAwait(true);
        }

        try
        {
            await RunClipboardActionAsync(
                clipboard => clipboard.SetTextAsync(text),
                cancellationToken,
                throwOnFailure: true).ConfigureAwait(true);

            SendPasteShortcut();
            await Task.Delay(100, cancellationToken).ConfigureAwait(true);

            if (!restoreClipboard)
            {
                return;
            }

            await RunClipboardActionAsync(
                snapshot is null
                    ? clipboard => clipboard.ClearAsync()
                    : clipboard => clipboard.SetDataAsync(snapshot),
                cancellationToken,
                throwOnFailure: false).ConfigureAwait(true);
        }
        finally
        {
            if (snapshot is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static async Task<IAsyncDataTransfer?> TryGetClipboardSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunClipboardFunctionAsync(
                clipboard => clipboard.TryGetDataAsync(),
                cancellationToken,
                throwOnFailure: false).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task RunClipboardActionAsync(
        Func<IClipboard, Task> action,
        CancellationToken cancellationToken,
        bool throwOnFailure)
    {
        _ = await RunClipboardFunctionAsync(
            async clipboard =>
            {
                await action(clipboard).ConfigureAwait(true);
                return true;
            },
            cancellationToken,
            throwOnFailure).ConfigureAwait(true);
    }

    private static async Task<T?> RunClipboardFunctionAsync<T>(
        Func<IClipboard, Task<T>> action,
        CancellationToken cancellationToken,
        bool throwOnFailure)
    {
        for (var attempt = 1; attempt <= ClipboardRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var clipboard = GetClipboard();
                return await action(clipboard).ConfigureAwait(true);
            }
            catch (Exception exception) when (IsTransientClipboardException(exception))
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

    private static IClipboard GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window } &&
            window.Clipboard is { } clipboard)
        {
            return clipboard;
        }

        throw new InvalidOperationException("Clipboard is not available because the main window is not initialized.");
    }

    private static bool IsTransientClipboardException(Exception exception)
    {
        return exception is COMException or ExternalException or InvalidOperationException;
    }

    private static void SendPasteShortcut()
    {
        var inputs = new[]
        {
            CreateKeyInput(NativeMethods.VkControl, keyUp: false),
            CreateKeyInput(NativeMethods.VkV, keyUp: false),
            CreateKeyInput(NativeMethods.VkV, keyUp: true),
            CreateKeyInput(NativeMethods.VkControl, keyUp: true)
        };

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException("Sending Ctrl+V with SendInput failed.");
        }
    }

    private static NativeMethods.INPUT CreateKeyInput(ushort virtualKey, bool keyUp)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? NativeMethods.KeyEventFKeyUp : 0
                }
            }
        };
    }
}
