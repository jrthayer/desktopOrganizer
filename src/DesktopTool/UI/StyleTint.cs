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

    /// <summary>A fixed blend amount, deliberately NOT tied to a widget's own adjustable Tint
    /// Strength - for anywhere fixed WhiteSmoke/AppTheme.Text is drawn on top of a tinted fill (menu
    /// chrome, tooltips, secondary button/panel fills), as opposed to a widget's own dominant body/
    /// header fill, which blends at the user's own adjustable strength instead (see each caller's
    /// own Effective*/Themed* properties). If a chrome fill moved with an adjustable strength, an
    /// Eyedropper pick (which resets that strength to 0 so its dominant fill starts pixel-exact -
    /// see IWidgetStyle.TintIsExact) would leave every OTHER surface - buttons, the settings menu,
    /// a list's own row background - looking completely untinted, contradicting the very swatch that
    /// was just picked. Pinning chrome to this fixed level instead keeps it visibly tinted
    /// regardless of what the strength slider (or a fresh Eyedropper reset) currently reads.</summary>
    public const double SafeChromeBlend = 0.55;

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

    /// <summary>Only meaningful for an IWidgetStyle.TintIsExact pick (see that property's own doc
    /// comment) - dilutes an exact Eyedropper sample back toward untinted by amount, the *reverse*
    /// direction from the regular Tint(base, tint, amount) call above (there, amount=0 means "ignore
    /// the pick"; here, amount=0 means "keep the pick exact"). Same underlying blend math as Tint,
    /// just with the two colors swapped - named separately (not just called as Tint(exact, untinted,
    /// amount) at each call site) so that reversal reads as a deliberate, distinct concept rather
    /// than a same-looking call that happens to pass its arguments in the opposite order.</summary>
    public static Color DilutedExact(Color exact, Color untinted, double amount) => Tint(exact, untinted, amount);

    /// <summary>Blends color toward black by amount (0-1) - the basis for a header/title band's own
    /// darker shade before Tint blends a pick into what's left of it (see FenceForm.HeaderBaseColor
    /// and LayoutLauncherWidget.HeaderBaseColor, both now just callers of this).</summary>
    public static Color DarkenTowardBlack(Color color, double amount) => Color.FromArgb(255,
        (int)Math.Round(color.R * (1 - amount)),
        (int)Math.Round(color.G * (1 - amount)),
        (int)Math.Round(color.B * (1 - amount)));

    /// <summary>Blends color toward white by amount (0-1) - the mirror image of DarkenTowardBlack,
    /// for a shade that needs to read as raised against a base color rather than sunken (see
    /// LayeredWidgetForm.ThemedField, lightened off DefaultBodyColor itself rather than off a fixed
    /// AppTheme gray, so it stays lighter than whatever body color a widget actually has).</summary>
    public static Color LightenTowardWhite(Color color, double amount) => Color.FromArgb(255,
        (int)Math.Round(color.R + (255 - color.R) * amount),
        (int)Math.Round(color.G + (255 - color.G) * amount),
        (int)Math.Round(color.B + (255 - color.B) * amount));
}
