namespace DesktopTool.UI;

/// <summary>
/// "Copy Settings To" - a full-virtual-screen click-catcher (same TransparencyKey/Cursors.Cross/
/// WS_EX_TOOLWINDOW technique as EyedropperOverlay, base-level and feature-agnostic the same way
/// that one is, unlike Features/Layouts/UI's own WindowPickerOverlay). As the cursor moves,
/// whichever other LayeredWidgetForm it's currently over (if any - see LayeredWidgetForm.FindAt)
/// shows its own engaged chrome (LayeredWidgetForm.SetPickTargetActive), so it's clear what a click
/// would target - only ever one at a time, deactivated the moment the cursor leaves it, moves onto a
/// different widget, or the whole pick is cancelled. Left-clicking a currently-hovered target applies
/// source's own settings onto it (LayeredWidgetForm.CopySettingsFrom) but deliberately stays open
/// afterward, so several widgets can be painted with the same source's settings one click after
/// another without reopening the picker each time. Closes only when a left click lands on nothing
/// (no valid target under the cursor), or on right-click/Escape - both cancel with nothing (further)
/// applied, same convention as every other picker overlay in this app (EyedropperOverlay,
/// Features/Layouts/UI's own WindowPickerOverlay).
/// </summary>
internal sealed class CopySettingsOverlay : Form
{
    private static readonly Color KeyColor = Color.FromArgb(255, 1, 2, 3);

    private readonly LayeredWidgetForm _source;
    private LayeredWidgetForm? _hoveredTarget;

    public CopySettingsOverlay(LayeredWidgetForm source)
    {
        _source = source;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        // Same "eyedropper" cursor EyedropperOverlay's own color-pick uses - this app has no
        // dedicated "pick a widget" cursor of its own, and the two gestures (point at something on
        // screen, click to apply) read the same way to a user either way.
        Cursor = Cursors.Cross;
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        KeyPreview = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var target = LayeredWidgetForm.FindAt(PointToScreen(e.Location));
        // Never targets its own source - copying settings onto themselves is meaningless, and
        // highlighting the source's own engaged chrome while the cursor just happens to be over its
        // own body would read as if it were a valid, clickable target.
        if (ReferenceEquals(target, _source))
            target = null;

        if (ReferenceEquals(target, _hoveredTarget))
            return;

        _hoveredTarget?.SetPickTargetActive(false);
        _hoveredTarget = target;
        _hoveredTarget?.SetPickTargetActive(true);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left)
        {
            if (_hoveredTarget is { } target)
            {
                // Deliberately stays open (and target stays the hovered/active one) instead of
                // closing - lets several widgets in a row get the same source's settings applied one
                // click after another, without having to click the paint-brush button again for each
                // one. Closes only on a click that lands on nothing (below) or a right-click/Escape.
                target.CopySettingsFrom(_source);
            }
            // A left click with nothing currently hovered (empty desktop, say) has no valid target
            // to copy onto - reads the same as an explicit cancel rather than staying open waiting
            // for a click that already happened.
            else
            {
                Close();
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            Cancel();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Cancel();
    }

    private void Cancel()
    {
        _hoveredTarget?.SetPickTargetActive(false);
        _hoveredTarget = null;
        Close();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW - keep it out of the taskbar/alt-tab
            return cp;
        }
    }
}
