using Avalonia.Threading;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace WhisperSTT.App.Services;

public interface IMessageDialogService
{
    Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default);

    Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default);
}

internal sealed class AvaloniaMessageDialogService : IMessageDialogService
{
    public async Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Dispatcher.UIThread.CheckAccess())
        {
            await ShowCoreAsync(title, message, Icon.Error, cancellationToken).ConfigureAwait(true);
            return;
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await ShowCoreAsync(title, message, Icon.Error, cancellationToken).ConfigureAwait(true);
                completionSource.TrySetResult();
            }
            catch (Exception exception)
            {
                completionSource.TrySetException(exception);
            }
        });
        await completionSource.Task.ConfigureAwait(false);
    }

    public async Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Dispatcher.UIThread.CheckAccess())
        {
            await ShowCoreAsync(title, message, Icon.Info, cancellationToken).ConfigureAwait(true);
            return;
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await ShowCoreAsync(title, message, Icon.Info, cancellationToken).ConfigureAwait(true);
                completionSource.TrySetResult();
            }
            catch (Exception exception)
            {
                completionSource.TrySetException(exception);
            }
        });
        await completionSource.Task.ConfigureAwait(false);
    }

    private static async Task ShowCoreAsync(string title, string message, Icon icon, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dialog = MessageBoxManager.GetMessageBoxStandard(
            title,
            message,
            ButtonEnum.Ok,
            icon);
        await dialog.ShowAsync().WaitAsync(cancellationToken).ConfigureAwait(true);
    }
}
