using System;
using System.Drawing;
using System.Windows;

namespace STool.Modules.Screenshot;

internal sealed class VirtualDesktopCoordinateMapper
{
    public VirtualDesktopCoordinateMapper(Rectangle physicalBounds, System.Windows.Size canvasSize)
    {
        if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(physicalBounds));
        if (canvasSize.Width <= 0 || canvasSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvasSize));

        PhysicalBounds = physicalBounds;
        CanvasSize = canvasSize;
    }

    public Rectangle PhysicalBounds { get; }
    public System.Windows.Size CanvasSize { get; }
    public double PixelsPerDipX => PhysicalBounds.Width / CanvasSize.Width;
    public double PixelsPerDipY => PhysicalBounds.Height / CanvasSize.Height;

    public Rect PhysicalToCanvas(Rectangle rectangle) => new(
        (rectangle.Left - PhysicalBounds.Left) / PixelsPerDipX,
        (rectangle.Top - PhysicalBounds.Top) / PixelsPerDipY,
        rectangle.Width / PixelsPerDipX,
        rectangle.Height / PixelsPerDipY);

    public Rectangle CanvasToPhysical(Rect rectangle) => MapCanvasRect(
        rectangle,
        PhysicalBounds.Width,
        PhysicalBounds.Height,
        PhysicalBounds.Left,
        PhysicalBounds.Top);

    public Rectangle CanvasToBitmap(Rect rectangle, int bitmapWidth, int bitmapHeight) =>
        MapCanvasRect(rectangle, bitmapWidth, bitmapHeight, 0, 0);

    public (int Width, int Height) CanvasToBitmapSize(double width, double height, int bitmapWidth, int bitmapHeight) =>
        (
            Math.Max(1, (int)Math.Round(width * bitmapWidth / CanvasSize.Width)),
            Math.Max(1, (int)Math.Round(height * bitmapHeight / CanvasSize.Height))
        );

    private Rectangle MapCanvasRect(Rect rectangle, int targetWidth, int targetHeight, int offsetX, int offsetY)
    {
        var left = (int)Math.Round(rectangle.Left * targetWidth / CanvasSize.Width);
        var top = (int)Math.Round(rectangle.Top * targetHeight / CanvasSize.Height);
        var right = (int)Math.Round(rectangle.Right * targetWidth / CanvasSize.Width);
        var bottom = (int)Math.Round(rectangle.Bottom * targetHeight / CanvasSize.Height);

        left = Math.Clamp(left, 0, targetWidth - 1);
        top = Math.Clamp(top, 0, targetHeight - 1);
        right = Math.Clamp(right, left + 1, targetWidth);
        bottom = Math.Clamp(bottom, top + 1, targetHeight);

        return new Rectangle(
            offsetX + left,
            offsetY + top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
    }
}
