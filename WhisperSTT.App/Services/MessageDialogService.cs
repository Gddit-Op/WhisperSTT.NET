using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace WhisperSTT.App.Services;

public interface IMessageDialogService
{
    Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default);
}

internal sealed class AvaloniaMessageDialogService : IMessageDialogService
{
    public async Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Dispatcher.UIThread.CheckAccess())
        {
            await ShowCoreAsync(title, message, cancellationToken).ConfigureAwait(true);
            return;
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await ShowCoreAsync(title, message, cancellationToken).ConfigureAwait(true);
                completionSource.TrySetResult();
            }
            catch (Exception exception)
            {
                completionSource.TrySetException(exception);
            }
        });
        await completionSource.Task.ConfigureAwait(false);
    }

    private static async Task ShowCoreAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var closeButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 96
        };

        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = 240,
            MinWidth = 420,
            MinHeight = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 20,
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap
                    },
                    closeButton
                }
            }
        };

        closeButton.Click += (_, _) => dialog.Close();

        var owner = ResolveOwnerWindow();
        if (owner is not null)
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            return;
        }

        var closeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => closeTcs.TrySetResult();
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Show();
        await closeTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    private static Window? ResolveOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            return null;
        }

        var activeWindow = desktopLifetime.Windows.FirstOrDefault(window => window.IsActive && window.IsVisible);
        if (activeWindow is not null)
        {
            return activeWindow;
        }

        if (desktopLifetime.MainWindow is { IsVisible: true } mainWindow)
        {
            return mainWindow;
        }

        return null;
    }
}
