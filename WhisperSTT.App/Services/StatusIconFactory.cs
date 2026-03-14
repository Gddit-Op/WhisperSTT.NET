using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using WhisperSTT.Core.Models;

namespace WhisperSTT.App.Services;

public sealed class StatusIconSet : IDisposable
{
    public required Icon TrayIcon { get; init; }

    public required WindowIcon WindowIcon { get; init; }

    public void Dispose()
    {
        TrayIcon.Dispose();
    }
}

public static class StatusIconFactory
{
    public static StatusIconSet Create(AppStatus status)
    {
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var shadowBrush = new SolidBrush(Color.FromArgb(50, 0, 0, 0));
        graphics.FillEllipse(shadowBrush, 7, 9, 52, 52);

        using var fillBrush = new SolidBrush(GetStatusColor(status));
        graphics.FillEllipse(fillBrush, 4, 4, 56, 56);

        using var outlinePen = new Pen(Color.FromArgb(160, 255, 255, 255), 2f);
        graphics.DrawEllipse(outlinePen, 4, 4, 56, 56);

        DrawGlyph(graphics, status);

        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream, ImageFormat.Png);
        pngStream.Position = 0;
        var windowIcon = new WindowIcon(pngStream);

        var handle = bitmap.GetHicon();
        try
        {
            var trayIcon = (Icon)Icon.FromHandle(handle).Clone();
            return new StatusIconSet
            {
                TrayIcon = trayIcon,
                WindowIcon = windowIcon
            };
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Color GetStatusColor(AppStatus status) => status switch
    {
        AppStatus.Idle => Color.FromArgb(255, 46, 155, 79),
        AppStatus.Recording => Color.FromArgb(255, 211, 58, 44),
        AppStatus.Transcribing => Color.FromArgb(255, 43, 120, 228),
        AppStatus.Error => Color.FromArgb(255, 179, 74, 18),
        _ => Color.FromArgb(255, 106, 106, 106)
    };

    private static void DrawGlyph(Graphics graphics, AppStatus status)
    {
        switch (status)
        {
            case AppStatus.Idle:
                using (var pen = new Pen(Color.White, 6f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                })
                {
                    graphics.DrawLines(pen, new[]
                    {
                        new PointF(18f, 31f),
                        new PointF(28f, 41f),
                        new PointF(46f, 21f)
                    });
                }

                break;
            case AppStatus.Recording:
                using (var recordBrush = new SolidBrush(Color.White))
                {
                    graphics.FillEllipse(recordBrush, 19, 19, 26, 26);
                }

                break;
            case AppStatus.Transcribing:
                using (var barBrush = new SolidBrush(Color.White))
                {
                    FillRoundedRectangle(graphics, barBrush, 14, 29, 7, 12, 3);
                    FillRoundedRectangle(graphics, barBrush, 24, 21, 7, 26, 3);
                    FillRoundedRectangle(graphics, barBrush, 34, 15, 7, 36, 3);
                    FillRoundedRectangle(graphics, barBrush, 44, 25, 7, 16, 3);
                }

                break;
            default:
                using (var pen = new Pen(Color.White, 6f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                })
                {
                    graphics.DrawLine(pen, 19, 18, 45, 18);
                    graphics.DrawLine(pen, 19, 32, 45, 32);
                    graphics.DrawLine(pen, 19, 46, 45, 46);
                }

                break;
        }
    }

    private static void FillRoundedRectangle(Graphics graphics, Brush brush, float x, float y, float width, float height, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;

        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        graphics.FillPath(brush, path);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
