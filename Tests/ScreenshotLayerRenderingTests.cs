using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using STool.Modules.Screenshot;
using Xunit;

namespace STool.Tests;

public class ScreenshotLayerRenderingTests
{
    [Theory]
    [InlineData(0, 0, 1, true)]
    [InlineData(120, 90, 1, true)]
    [InlineData(120, 90, 1.25, true)]
    [InlineData(120, 90, 1.5, true)]
    [InlineData(120, 90, 1.5, false)]
    public void DrawSelectionLayer_PreservesLocalPositionAfterLayout(
        double selectionX, double selectionY, double scale, bool transparentBackground)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var parent = new Canvas { Width = 1000, Height = 800 };
                var layer = new Canvas
                {
                    Width = 400,
                    Height = 300,
                    Background = transparentBackground ? Brushes.Transparent : null,
                    ClipToBounds = true
                };
                Canvas.SetLeft(layer, selectionX);
                Canvas.SetTop(layer, selectionY);
                parent.Children.Add(layer);

                var annotation = new Rectangle { Width = 40, Height = 30, Fill = Brushes.Red };
                Canvas.SetLeft(annotation, 100);
                Canvas.SetTop(annotation, 80);
                layer.Children.Add(annotation);
                parent.Measure(new Size(1000, 800));
                parent.Arrange(new Rect(0, 0, 1000, 800));
                parent.UpdateLayout();

                var width = (int)(400 * scale);
                var height = (int)(300 * scale);
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                    CaptureOverlay.DrawSelectionLayer(context, layer, new Rect(0, 0, width, height));
                var output = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                output.Render(visual);
                var pixels = new byte[width * height * 4];
                output.CopyPixels(pixels, width * 4, 0);

                int left = width, top = height, right = -1, bottom = -1;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        if (pixels[(y * width + x) * 4 + 3] < 128)
                            continue;
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x);
                        bottom = Math.Max(bottom, y);
                    }
                }

                Assert.Equal((int)(100 * scale), left);
                Assert.Equal((int)(80 * scale), top);
                Assert.Equal((int)(140 * scale) - 1, right);
                Assert.InRange(bottom, (int)(110 * scale) - 1, (int)Math.Ceiling(110 * scale) - 1);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
