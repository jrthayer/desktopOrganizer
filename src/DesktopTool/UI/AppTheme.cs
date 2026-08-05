namespace DesktopTool.UI;

/// <summary>Shared dark palette for chrome that isn't tied to any one fence's own tint - the Snap
/// Lines panel (and its own ComboButton/DarkNumericField controls) and the tray icon's context menu
/// both draw from this same handful of grays instead of each hard-coding their own copy. Distinct
/// from FenceForm's own DefaultBodyColor/DefaultBorderColor/DefaultAccentColor, which are a fence's
/// fallback when it has no per-fence tint - those still need to flow through FenceForm's live
/// Tint()/CurrentTint machinery, so they stay put rather than folding in here.</summary>
internal static class AppTheme
{
    public static readonly Color Body = Color.FromArgb(255, 32, 32, 36);
    public static readonly Color Field = Color.FromArgb(255, 45, 45, 50);
    public static readonly Color Border = Color.FromArgb(255, 70, 70, 78);
    public static readonly Color Hover = Color.FromArgb(255, 55, 55, 62);
    public static readonly Color Accent = Color.FromArgb(255, 190, 190, 195);
    public static readonly Color Text = Color.WhiteSmoke;
    public static readonly Color DisabledText = Color.FromArgb(255, 130, 130, 138);
    public static readonly Color Warning = Color.FromArgb(255, 235, 190, 60);
    public static readonly Color WarningText = Color.FromArgb(255, 40, 32, 10);
    public static readonly Font Font = new("Segoe UI", 9f);

    /// <summary>Flat/dark to match the rest of the app's chrome instead of a stock Button's raised,
    /// system-colored 3D face - FlatStyle alone only swaps the border rendering, so
    /// FlatAppearance's own colors still need setting explicitly for the hover/press states to
    /// actually go dark too. Deliberately doesn't set BackColor - a widget that tints itself
    /// (LayoutLauncherWidget) needs that to track its own live tint rather than a fixed color, so it
    /// sets BackColor itself after calling this; SnapLinePanel (not tinted) sets a fixed BackColor of
    /// its own the same way, right after this call.</summary>
    public static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.ForeColor = Text;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Hover;
        button.FlatAppearance.MouseDownBackColor = Accent;
    }
}
