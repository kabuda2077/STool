using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace STool.Core;

public static class AppIcons
{
    public static Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "STool.ico");
        if (File.Exists(iconPath))
        {
            return new Icon(iconPath);
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            var extractedIcon = Icon.ExtractAssociatedIcon(processPath);
            if (extractedIcon != null)
            {
                return extractedIcon;
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public static Bitmap CreateMenuIcon(TrayMenuIconKind kind, int size = 20)
    {
        var bitmap = new Bitmap(size, size);
        bitmap.SetResolution(96, 96);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var accent = Color.FromArgb(37, 99, 235);
        var accentDark = Color.FromArgb(29, 78, 216);
        var muted = Color.FromArgb(100, 116, 139);
        var danger = Color.FromArgb(220, 38, 38);

        using var accentPen = new Pen(accent, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var accentDarkPen = new Pen(accentDark, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var mutedPen = new Pen(muted, 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var dangerPen = new Pen(danger, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var accentBrush = new SolidBrush(accent);
        using var softBrush = new SolidBrush(Color.FromArgb(232, 240, 255));

        switch (kind)
        {
            case TrayMenuIconKind.Screenshot:
                DrawRoundedRect(graphics, accentPen, 3.5f, 4.5f, 13f, 11f, 3f);
                graphics.FillEllipse(accentBrush, 8.1f, 7.6f, 3.8f, 3.8f);
                graphics.DrawLine(accentPen, 6.2f, 4.5f, 7.2f, 2.8f);
                graphics.DrawLine(accentPen, 12.8f, 4.5f, 13.8f, 2.8f);
                break;

            case TrayMenuIconKind.Translate:
                graphics.FillEllipse(softBrush, 2.5f, 3f, 15f, 14f);
                graphics.DrawLine(accentDarkPen, 5f, 6.5f, 12.5f, 6.5f);
                graphics.DrawLine(accentDarkPen, 8.8f, 4.7f, 8.8f, 12.5f);
                graphics.DrawBezier(accentPen, 5.6f, 12.8f, 8.2f, 9.2f, 11.1f, 9.2f, 14.2f, 13.3f);
                graphics.DrawLine(accentPen, 12.4f, 13.2f, 14.2f, 13.2f);
                break;

            case TrayMenuIconKind.Clipboard:
                DrawRoundedRect(graphics, mutedPen, 4.5f, 4.5f, 11f, 12.5f, 2.5f);
                DrawRoundedRect(graphics, accentPen, 7f, 2.8f, 6f, 3.5f, 1.5f);
                graphics.DrawLine(mutedPen, 7f, 9f, 13f, 9f);
                graphics.DrawLine(mutedPen, 7f, 12f, 12f, 12f);
                break;

            case TrayMenuIconKind.Settings:
                graphics.DrawEllipse(accentPen, 5.4f, 5.4f, 9.2f, 9.2f);
                graphics.FillEllipse(accentBrush, 8.2f, 8.2f, 3.6f, 3.6f);
                for (var i = 0; i < 8; i++)
                {
                    var angle = Math.PI * 2 * i / 8;
                    var x1 = 10 + Math.Cos(angle) * 6.2;
                    var y1 = 10 + Math.Sin(angle) * 6.2;
                    var x2 = 10 + Math.Cos(angle) * 7.7;
                    var y2 = 10 + Math.Sin(angle) * 7.7;
                    graphics.DrawLine(accentDarkPen, (float)x1, (float)y1, (float)x2, (float)y2);
                }
                break;

            case TrayMenuIconKind.Exit:
                graphics.DrawLine(dangerPen, 6f, 6f, 14f, 14f);
                graphics.DrawLine(dangerPen, 14f, 6f, 6f, 14f);
                break;
        }

        return bitmap;
    }

    private static void DrawRoundedRect(Graphics graphics, Pen pen, float x, float y, float width, float height, float radius)
    {
        using var path = CreateRoundedRectPath(x, y, width, height, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectPath(float x, float y, float width, float height, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

public enum TrayMenuIconKind
{
    Screenshot,
    Translate,
    Clipboard,
    Settings,
    Exit
}
