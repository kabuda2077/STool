using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace STool.Core;

public sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color MenuBackground = Color.FromArgb(255, 255, 255);
    private static readonly Color HoverBackground = Color.FromArgb(239, 246, 255);
    private static readonly Color TextColor = Color.FromArgb(17, 24, 39);
    private static readonly Color MutedTextColor = Color.FromArgb(100, 116, 139);
    private static readonly Color BorderColor = Color.FromArgb(217, 224, 234);

    public TrayMenuRenderer()
        : base(new TrayMenuColorTable())
    {
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(MenuBackground);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, new Size(e.ToolStrip.Width - 1, e.ToolStrip.Height - 1));
        using var pen = new Pen(BorderColor);
        using var path = CreateRoundedRectPath(bounds, 8);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        var bounds = new Rectangle(6, 2, e.Item.Width - 12, e.Item.Height - 4);
        using var brush = new SolidBrush(HoverBackground);
        using var path = CreateRoundedRectPath(bounds, 6);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TextColor : MutedTextColor;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(MenuBackground);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    private static GraphicsPath CreateRoundedRectPath(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class TrayMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => MenuBackground;
        public override Color ImageMarginGradientBegin => MenuBackground;
        public override Color ImageMarginGradientMiddle => MenuBackground;
        public override Color ImageMarginGradientEnd => MenuBackground;
        public override Color MenuItemSelected => HoverBackground;
        public override Color MenuItemBorder => HoverBackground;
        public override Color SeparatorDark => BorderColor;
        public override Color SeparatorLight => MenuBackground;
    }
}
