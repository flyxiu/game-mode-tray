using System.Drawing.Drawing2D;

namespace GameMode.App;

/// <summary>
/// 深色菜单渲染器。只用于系统托盘图标上的右键菜单——
/// 那个菜单必须由 WinForms 原生控件承载（托盘菜单无法自绘），
/// 默认渲染器跟随系统浅色主题，在深色任务栏旁会突兀。
///
/// 应用内的右键菜单不用这个，那是完全自绘的（见 MainForm.DrawMenu）。
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Back = Color.FromArgb(30, 32, 40);
    private static readonly Color Hover = Color.FromArgb(52, 55, 64);
    private static readonly Color Border = Color.FromArgb(48, 255, 255, 255);
    private static readonly Color TextColor = Color.FromArgb(240, 240, 244);
    private static readonly Color SeparatorColor = Color.FromArgb(40, 255, 255, 255);

    public DarkMenuRenderer() : base(new DarkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Back);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var pen = new Pen(Border, 1f);
        e.Graphics.DrawRectangle(pen, rect);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;

        var rect = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
        using var path = Rounded(rect, 6);
        using var brush = new SolidBrush(Hover);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? TextColor
            : Color.FromArgb(105, 105, 115);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(SeparatorColor, 1f);
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuBorder => Border;
        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color SeparatorDark => SeparatorColor;
        public override Color SeparatorLight => SeparatorColor;
    }
}
