using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WhisperSTT.Core.Models;

namespace WhisperSTT.App.Services;

public sealed class StatusIconSet : IDisposable
{
    public required WindowIcon TrayIcon { get; init; }

    public required WindowIcon WindowIcon { get; init; }

    public void Dispose()
    {
        // WindowIcon does not expose disposal; the icon streams are copied on construction.
    }
}

public static class StatusIconFactory
{
    private const int IconSize = 64;

    public static StatusIconSet Create(AppStatus status)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(IconSize, IconSize), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext(true))
        {
            var shadowBrush = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0));
            var fillBrush = new SolidColorBrush(GetStatusColor(status));
            var outlinePen = CreateRoundedPen(Color.FromArgb(160, 255, 255, 255), 2d);

            context.DrawEllipse(shadowBrush, null, new Rect(7, 9, 52, 52));
            context.DrawEllipse(fillBrush, null, new Rect(4, 4, 56, 56));
            context.DrawEllipse(null, outlinePen, new Rect(4, 4, 56, 56));

            DrawGlyph(context, status);
        }

        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream);
        pngStream.Position = 0;
        var iconBytes = pngStream.ToArray();

        return new StatusIconSet
        {
            TrayIcon = new WindowIcon(new MemoryStream(iconBytes, writable: false)),
            WindowIcon = new WindowIcon(new MemoryStream(iconBytes, writable: false))
        };
    }

    private static Color GetStatusColor(AppStatus status) => status switch
    {
        AppStatus.Idle => Color.FromArgb(255, 46, 155, 79),
        AppStatus.Recording => Color.FromArgb(255, 211, 58, 44),
        AppStatus.Transcribing => Color.FromArgb(255, 43, 120, 228),
        AppStatus.Error => Color.FromArgb(255, 179, 74, 18),
        _ => Color.FromArgb(255, 106, 106, 106)
    };

    private static void DrawGlyph(DrawingContext context, AppStatus status)
    {
        switch (status)
        {
            case AppStatus.Idle:
                var checkPen = CreateRoundedPen(Colors.White, 6d);
                context.DrawLine(checkPen, new Point(18, 31), new Point(28, 41));
                context.DrawLine(checkPen, new Point(28, 41), new Point(46, 21));
                break;
            case AppStatus.Recording:
                context.DrawEllipse(Brushes.White, null, new Rect(19, 19, 26, 26));
                break;
            case AppStatus.Transcribing:
                FillRoundedRectangle(context, Brushes.White, 14, 29, 7, 12, 3);
                FillRoundedRectangle(context, Brushes.White, 24, 21, 7, 26, 3);
                FillRoundedRectangle(context, Brushes.White, 34, 15, 7, 36, 3);
                FillRoundedRectangle(context, Brushes.White, 44, 25, 7, 16, 3);
                break;
            default:
                var menuPen = CreateRoundedPen(Colors.White, 6d);
                context.DrawLine(menuPen, new Point(19, 18), new Point(45, 18));
                context.DrawLine(menuPen, new Point(19, 32), new Point(45, 32));
                context.DrawLine(menuPen, new Point(19, 46), new Point(45, 46));
                break;
        }
    }

    private static void FillRoundedRectangle(
        DrawingContext context,
        IBrush brush,
        double x,
        double y,
        double width,
        double height,
        double radius)
    {
        context.DrawRectangle(
            brush,
            null,
            new Rect(x, y, width, height),
            radius,
            radius,
            default);
    }

    private static Pen CreateRoundedPen(Color color, double thickness)
    {
        return new Pen(
            new SolidColorBrush(color),
            thickness,
            null,
            PenLineCap.Round,
            PenLineJoin.Round,
            10d);
    }
}
