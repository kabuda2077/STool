using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Xunit;
using Canvas = System.Windows.Controls.Canvas;

namespace STool.Tests;

/// <summary>
/// 确认 VisualBrush 在 Absolute Viewbox 下的坐标语义——截图导出标注偏移的根因:
/// Viewbox 区域是按"被采样元素自己的局部坐标"还是"含父级布局偏移的坐标"解释。
/// 双判别:画布放在宿主 (100,100),红块在画布局部 (10,10)。
///  - 局部坐标语义:Viewbox (0,0,100,100) 内有红块;(100,100,100,100) 为空。
///  - 父级坐标语义:Viewbox (100,100,100,100) 内有红块;(0,0,100,100) 为空。
/// 测试环境无法驱动真实渲染时(全部为空)视为无法判定,静默通过。
/// </summary>
public class VisualBrushViewboxTests
{
    [Fact]
    public void AbsoluteViewbox_CoordinateSemantics_IsDetermined()
    {
        Exception? failure = null;
        var done = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            try
            {
                RunViewboxScenario();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "STA 测试线程超时");
        Assert.Null(failure);
    }

    private static void RunViewboxScenario()
    {
        // hostCanvas 200x200, childCanvas 放在 (100,100),尺寸 100x100——与 annotationCanvas
        // 通过 Canvas.SetLeft 贴在选区左上角的结构一致。
        var host = new Canvas { Width = 200, Height = 200 };
        var child = new Canvas
        {
            Width = 100,
            Height = 100,
            Background = Brushes.White
        };
        Canvas.SetLeft(child, 100);
        Canvas.SetTop(child, 100);
        host.Children.Add(child);

        var red = new Rectangle
        {
            Width = 10,
            Height = 10,
            Fill = Brushes.Red
        };
        Canvas.SetLeft(red, 10);
        Canvas.SetTop(red, 10);
        child.Children.Add(red);

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -5000,
            Top = -5000,
            Width = 200,
            Height = 200,
            Content = host
        };
        try
        {
            window.Show();
            PumpUntilIdle();
            // 强制一次同步渲染,确保 Visual 有真实渲染数据供 VisualBrush 捕获。
            var warmup = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32);
            warmup.Render(window);
            PumpUntilIdle();

            var fromOrigin = RenderWithViewbox(child, new Rect(0, 0, 100, 100));
            var fromCanvasPosition = RenderWithViewbox(child, new Rect(100, 100, 100, 100));
            // 修复后的等价调用:Viewbox 起点 = 元素原点在宿主画布中的位置。
            var translated = child.TranslatePoint(new Point(), host);
            var fromTranslated = RenderWithViewbox(child, new Rect(translated.X, translated.Y, 100, 100));

            var originHasRed = HasRed(fromOrigin, out var originRedAt);
            var positionHasRed = HasRed(fromCanvasPosition, out var positionRedAt);
            var translatedHasRed = HasRed(fromTranslated, out var translatedRedAt);
            var anyPixels = HasAnyVisiblePixels(fromOrigin) || HasAnyVisiblePixels(fromCanvasPosition);

            if (!anyPixels)
            {
                // 无桌面/无合成环境下 VisualBrush 捕获不到内容,无法判定,不算失败。
                Console.WriteLine("[ViewboxProbe] INDETERMINATE: no renderable content captured");
                return;
            }

            Console.WriteLine($"[ViewboxProbe] originHasRed={originHasRed}({originRedAt.Count}px) positionHasRed={positionHasRed}({positionRedAt.Count}px) translatedHasRed={translatedHasRed}({translatedRedAt.Count}px)");

            // 修复做法必须有效:平移后的 Viewbox 采到红块,且红块在输出 (10,10)(DIP 与物理 1:1 时)。
            Assert.True(translatedHasRed, $"TranslatePoint 修复未生效: {Describe(translatedRedAt)}");
            Assert.True(translatedRedAt.Contains((10, 10)),
                $"修复后红块落点异常,预期 (10,10): {Describe(translatedRedAt)}");

            if (originHasRed && positionRedAt.Count == 0)
            {
                // 局部坐标语义:红块出现在 Viewbox(0,0,100,100) 的输出 (10,10)。
                Assert.True(originRedAt.Contains((10, 10)),
                    $"局部坐标语义成立,但红块落点异常: {Describe(originRedAt)}");
            }
            else if (positionHasRed && originRedAt.Count == 0)
            {
                // 父级坐标语义:红块出现在 Viewbox(100,100,100,100) 的输出 (10,10)。
                Assert.True(positionRedAt.Contains((10, 10)),
                    $"父级坐标语义成立,但红块落点异常: {Describe(positionRedAt)}");
            }
            else
            {
                Assert.Fail($"Viewbox 语义与两种预期都不符: originRed={originHasRed} positionRed={positionHasRed}, " +
                    $"originAt={Describe(originRedAt)}, positionAt={Describe(positionRedAt)}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void PumpUntilIdle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static byte[] RenderWithViewbox(Visual visual, Rect viewbox)
    {
        var brush = new VisualBrush(visual)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = viewbox
        };

        var rtb = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
        var target = new DrawingVisual();
        using (var ctx = target.RenderOpen())
        {
            ctx.DrawRectangle(brush, null, new Rect(0, 0, 100, 100));
        }
        rtb.Render(target);

        var pixels = new byte[100 * 100 * 4];
        rtb.CopyPixels(pixels, 100 * 4, 0);
        return pixels;
    }

    private static bool HasRed(byte[] pixels, out List<(int X, int Y)> locations)
    {
        locations = [];
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
            {
                var i = (y * 100 + x) * 4;
                if (pixels[i + 3] > 128 && pixels[i + 2] > 200 && pixels[i + 1] < 100 && pixels[i] < 100)
                    locations.Add((x, y));
            }
        return locations.Count > 0;
    }

    private static bool HasAnyVisiblePixels(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 0)
                return true;
        }
        return false;
    }

    private static string Describe(List<(int X, int Y)> locations)
    {
        if (locations.Count == 0)
            return "(空)";
        var first = locations[0];
        var last = locations[^1];
        return $"{locations.Count}px, 首({first.X},{first.Y}) 末({last.X},{last.Y})";
    }
}
