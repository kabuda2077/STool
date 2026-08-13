using System.Drawing;
using System.Windows;
using STool.Modules.Screenshot;
using Xunit;

namespace STool.Tests;

public class VirtualDesktopCoordinateMapperTests
{
    [Fact]
    public void PhysicalToCanvas_HandlesNegativeVirtualDesktopOrigin()
    {
        var mapper = new VirtualDesktopCoordinateMapper(
            new Rectangle(-1920, 0, 5760, 2160),
            new System.Windows.Size(3840, 1440));

        var result = mapper.PhysicalToCanvas(new Rectangle(-1920, 0, 1920, 1080));

        Assert.Equal(new Rect(0, 0, 1280, 720), result);
    }

    [Theory]
    [InlineData(1.25, 3072, 1728)]
    [InlineData(1.50, 2560, 1440)]
    public void CanvasToBitmap_PreservesSelectionAtCommonDpiScales(
        double scale,
        double canvasWidth,
        double canvasHeight)
    {
        var mapper = new VirtualDesktopCoordinateMapper(
            new Rectangle(0, 0, 3840, 2160),
            new System.Windows.Size(canvasWidth, canvasHeight));

        var result = mapper.CanvasToBitmap(
            new Rect(160, 120, 640, 360),
            3840,
            2160);

        Assert.Equal(
            new Rectangle(
                (int)Math.Round(160 * scale),
                (int)Math.Round(120 * scale),
                (int)Math.Round(640 * scale),
                (int)Math.Round(360 * scale)),
            result);
    }

    [Fact]
    public void RoundTrip_PreservesCrossMonitorSelection()
    {
        var physicalBounds = new Rectangle(-1920, -240, 5760, 2400);
        var mapper = new VirtualDesktopCoordinateMapper(physicalBounds, new System.Windows.Size(3840, 1600));
        var physicalSelection = new Rectangle(-320, 120, 2240, 1200);

        var canvasSelection = mapper.PhysicalToCanvas(physicalSelection);
        var result = mapper.CanvasToPhysical(canvasSelection);

        Assert.Equal(physicalSelection, result);
    }

    [Fact]
    public void CanvasToBitmap_ClampsSelectionAtBitmapEdge()
    {
        var mapper = new VirtualDesktopCoordinateMapper(
            new Rectangle(0, 0, 3840, 2160),
            new System.Windows.Size(2560, 1440));

        var result = mapper.CanvasToBitmap(new Rect(2559.6, 1439.6, 20, 20), 3840, 2160);

        Assert.Equal(new Rectangle(3839, 2159, 1, 1), result);
    }
}
