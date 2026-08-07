using DesktopTool.UI;

namespace DesktopTool.Features.WidgetManager;

/// <summary>Persisted state for the on-screen Widget Manager widget (see
/// UI.WidgetManagerWidget) - inherits WidgetStyleModel for the same shape LayoutLauncherModel gets
/// (every IWidgetStyle knob), adding position/size/title/visibility, minus anything list-specific:
/// this widget's own row list is fixed (Fences/Snap Lines/Layout Launcher, always exactly three),
/// not a user-editable collection, so there's no Files/Entries/RowsShown equivalent here.</summary>
public sealed class WidgetManagerModel : WidgetStyleModel
{
    /// <summary>Null until the widget has actually been moved/resized once - see
    /// WidgetManagerWidget's own CreateParams, which centers on the primary screen at a default size
    /// instead of guessing a fixed default that might not exist on every monitor layout.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; } = 260;
    public int? Height { get; set; }

    public string Title { get; set; } = "Widget Manager";

    /// <summary>Whether the widget should currently be showing - persisted so the tray's "Widget
    /// Manager" toggle survives a restart instead of always defaulting back to shown.</summary>
    public bool Visible { get; set; } = true;
}
