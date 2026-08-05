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
    public static readonly Font Font = new("Segoe UI", 9f);
}
