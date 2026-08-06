using System.Drawing.Drawing2D;

namespace DesktopTool.UI;

/// <summary>Paints a ContextMenuStrip in the same dark palette (AppTheme) as everything else -
/// DropdownMenu's rows, the Snap Lines panel - instead of Windows' stock light system-menu look.
/// Unlike ComboBox/NumericUpDown (see SnapLinePanel's own top-of-class comment), a
/// ToolStripDropDownMenu's items are already painted through this exact Renderer hook rather than
/// baked-in visual-styles chrome, so a full custom renderer is all that's needed here - no
/// SetWindowTheme/DWM escape hatch required.
///
/// Colors default to the plain, untinted AppTheme palette (what the tray icon's own menu -
/// TrayApplicationContext, this renderer's original and still most common caller - wants, since
/// there's no per-widget tint to reflect at the app-global tray level). A caller styling a specific
/// tinted widget's own menu (LayoutLauncherWidget's header-rename ContextMenuStrip, so it actually
/// matches the header it hangs off of, the same way FenceForm's native rename menu already tints
/// itself via ChromeFill/DrawMenuItem) can instead pass live Func&lt;Color&gt; getters, the same
/// "reads current state at paint time, not a snapshot from construction" approach DropdownMenu's
/// own Func&lt;Color&gt; constructor params already use.</summary>
internal sealed class TrayMenuRenderer : ToolStripRenderer
{
    private readonly Func<Color> _body;
    private readonly Func<Color> _hover;
    private readonly Func<Color> _text;

    public TrayMenuRenderer(Func<Color>? body = null, Func<Color>? hover = null, Func<Color>? text = null)
    {
        _body = body ?? (() => AppTheme.Body);
        _hover = hover ?? (() => AppTheme.Hover);
        _text = text ?? (() => AppTheme.Text);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var background = new SolidBrush(_body());
        e.Graphics.FillRectangle(background, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // Same near-black 1px outline DropdownMenu draws around its own popup - a hair darker than
        // AppTheme.Border so the popup's edge still reads against the body fill sitting right next
        // to it.
        using var pen = new Pen(Color.FromArgb(255, 20, 20, 24));
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    // No item icons anywhere in this menu (see ContextMenuStrip.ShowImageMargin = false in
    // TrayApplicationContext) - suppressing this outright avoids owner-drawing a light-gray margin
    // strip that would otherwise sit unused down the left edge.
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var color = e.Item.Selected ? _hover() : _body();
        using var background = new SolidBrush(color);
        e.Graphics.FillRectangle(background, new Rectangle(Point.Empty, e.Item.Size));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = _text();
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using (var background = new SolidBrush(_body()))
            e.Graphics.FillRectangle(background, new Rectangle(Point.Empty, e.Item.Size));

        // Same faint white-on-dark divider DropdownMenu draws for its own separator rows.
        using var pen = new Pen(Color.FromArgb(60, 255, 255, 255));
        var midY = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 4, midY, e.Item.Width - 4, midY);
    }

    /// <summary>A hand-drawn checkmark instead of the default renderer's own glyph - like every
    /// other themed checkbox in this app (see DropdownMenu.DrawRow), the stock one is a small bitmap
    /// baked in for a light background and reads as a washed-out box on a dark one. No surrounding
    /// box - unlike DropdownMenu's own checkboxes, these two toggles have no unchecked state to show
    /// a box for in the first place (this only ever paints when Checked is true - see
    /// ToolStripMenuItem's own paint logic), so a box would just be dead chrome.</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var rect = e.ImageRectangle.Width > 0 ? e.ImageRectangle : new Rectangle(4, (e.Item.Height - 12) / 2, 12, 12);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var checkPen = new Pen(AppTheme.Accent, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLine(checkPen, rect.X + 2, rect.Y + 6, rect.X + 5, rect.Y + 9);
        e.Graphics.DrawLine(checkPen, rect.X + 5, rect.Y + 9, rect.X + 10, rect.Y + 2);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = AppTheme.Text;
        base.OnRenderArrow(e);
    }
}
