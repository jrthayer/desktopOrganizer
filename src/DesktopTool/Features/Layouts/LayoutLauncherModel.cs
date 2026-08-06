using DesktopTool.UI;

namespace DesktopTool.Features.Layouts;

/// <summary>Persisted state for the on-screen Layout Launcher widget (see
/// UI.LayoutLauncherWidget) - the same per-element styling knobs FenceModel carries for a fence
/// (TintColor/HeaderDarkness/Opacity/FullOpacityOnHover/TintStrength/Margin - see IWidgetStyle),
/// plus this widget's own position/title/visibility. Unlike FenceModel there's no Id or Files list -
/// only one of these ever exists, so LayoutLauncherStore persists a single object rather than a
/// collection the way FenceStore/LayoutStore do. Height is intentionally absent - see
/// LayoutLauncherWidget's own UpdateContentSize, which always derives it fresh from the current
/// layout count instead.</summary>
public sealed class LayoutLauncherModel : IWidgetStyle
{
    /// <summary>Null until the widget has actually been moved once - see LayoutLauncherWidget's
    /// constructor, which centers on the primary screen instead of guessing a fixed default X/Y
    /// that might not exist on every monitor layout.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 280;

    public string Title { get; set; } = "Layout Launcher";

    /// <summary>Hides the header's title text (not the header band/buttons themselves, which stay -
    /// they're still needed as the drag handle and to reach Settings) - same "Hide Title" wording
    /// and same "the row itself stays, just the label goes" meaning as FenceModel.HideTitle.</summary>
    public bool HideTitle { get; set; }

    // Same values as FenceModel's own DefaultHeaderDarkness/DefaultOpacity/DefaultTintStrength -
    // named here too (rather than left as bare property initializers) since LayoutLauncherWidget.
    // SetTintColor needs to reset back to these same three whenever a preset/Custom... color is
    // picked, mirroring FenceManager.SetTintColor's own reset-on-pick behavior.
    public const int DefaultHeaderDarkness = 65;
    public const int DefaultOpacity = 85;
    public const int DefaultTintStrength = 55;

    // IWidgetStyle - see that interface's own doc comments for what each one actually does.
    public int? TintColor { get; set; }
    public bool TintIsExact { get; set; }
    public int HeaderDarkness { get; set; } = DefaultHeaderDarkness;
    public int Opacity { get; set; } = DefaultOpacity;
    public bool FullOpacityOnHover { get; set; }
    public int TintStrength { get; set; } = DefaultTintStrength;
    public int Margin { get; set; }

    /// <summary>Whether the widget should currently be showing - persisted so the tray's "Layout
    /// Launcher" toggle survives a restart instead of always defaulting back to shown.</summary>
    public bool Visible { get; set; } = true;
}
