namespace DesktopTool.UI;

/// <summary>Plain Button subclass that only overrides painting for the disabled case - normal
/// (enabled) rendering is left to the base FlatStyle.Flat implementation, which already looks right
/// once AppTheme.StyleButton has been applied. Needed because WinForms' own disabled-button painting
/// substitutes a fixed system color for the label regardless of the button's own ForeColor (a
/// long-standing WinForms limitation) - so without this override, a disabled Button here would
/// render its label unreadable-near-black no matter what ForeColor said. Originally duplicated once
/// each in LayoutEditorForm and LayoutLauncherWidget; promoted here once a third widget wanting the
/// exact same fix would have meant a third copy.</summary>
internal sealed class DarkButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled)
        {
            base.OnPaint(e);
            return;
        }

        using (var background = new SolidBrush(BackColor))
            e.Graphics.FillRectangle(background, ClientRectangle);
        using (var borderPen = new Pen(AppTheme.Border))
            e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, AppTheme.DisabledText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}
