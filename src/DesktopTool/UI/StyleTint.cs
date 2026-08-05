namespace DesktopTool.UI;

/// <summary>Shared color math and preset palette for anything with a Fence-style "tint this dark
/// chrome toward a pick" and "darken the header band toward black" pair of controls - originally
/// FenceForm's own private Tint/DarkenTowardBlack/ColorPresets, now the single copy both FenceForm
/// and LayoutLauncherWidget blend against, so a third widget wanting the same look never has to
/// re-derive (or subtly mis-copy) this math again.</summary>
internal static class StyleTint
{
    /// <summary>Muted rather than fully saturated so a tinted body/title still reads as part of the
    /// same dark theme - see Tint. Order matters: index 0 is a widget's default "Red" pick, etc. -
    /// StyleMenuRows.Build renders them in this order and IWidgetStyle.TintColor stores a plain
    /// Color.ToArgb() int, not an index, so reordering this array doesn't break anything already
    /// saved, but the preset names below must stay lined up with it.</summary>
    public static readonly Color[] Presets =
    {
        Color.FromArgb(200, 80, 80),   // Red
        Color.FromArgb(210, 140, 70),  // Orange
        Color.FromArgb(200, 180, 70),  // Yellow
        Color.FromArgb(90, 170, 100),  // Green
        Color.FromArgb(70, 170, 170),  // Teal
        Color.FromArgb(90, 140, 210),  // Blue
        Color.FromArgb(150, 110, 210), // Purple
        Color.FromArgb(210, 110, 160), // Pink
    };

    public static readonly string[] PresetNames = { "Red", "Orange", "Yellow", "Green", "Teal", "Blue", "Purple", "Pink" };

    public static Color GetPreset(int index) => index >= 0 && index < Presets.Length ? Presets[index] : Color.Empty;
    public static string GetPresetName(int index) => index >= 0 && index < PresetNames.Length ? PresetNames[index] : string.Empty;

    /// <summary>Blends baseColor toward tint by amount (0-1) - null tint (no pick made) returns
    /// baseColor unchanged. amount has no default on purpose (see FenceForm's own original comment
    /// on this) - every call site deliberately picks whichever blend fraction applies (a widget's
    /// adjustable Tint Strength, or a fixed chrome-safe amount for text/icon contrast).</summary>
    public static Color Tint(Color baseColor, Color? tint, double amount) =>
        tint is not { } t
            ? baseColor
            : Color.FromArgb(255,
                (int)Math.Round(baseColor.R + (t.R - baseColor.R) * amount),
                (int)Math.Round(baseColor.G + (t.G - baseColor.G) * amount),
                (int)Math.Round(baseColor.B + (t.B - baseColor.B) * amount));

    /// <summary>Blends color toward black by amount (0-1) - the basis for a header/title band's own
    /// darker shade before Tint blends a pick into what's left of it (see FenceForm.HeaderBaseColor
    /// and LayoutLauncherWidget.HeaderBaseColor, both now just callers of this).</summary>
    public static Color DarkenTowardBlack(Color color, double amount) => Color.FromArgb(255,
        (int)Math.Round(color.R * (1 - amount)),
        (int)Math.Round(color.G * (1 - amount)),
        (int)Math.Round(color.B * (1 - amount)));
}
