using DesktopTool.UI;

namespace DesktopTool.Features.WidgetManager;

/// <summary>Persisted state for the on-screen Widget Manager widget (see
/// UI.WidgetManagerWidget) - inherits WidgetStyleModel for the same shape LayoutLauncherModel gets
/// (every IWidgetStyle knob), adding position/size/title/visibility. This widget's own row list is
/// fixed (Fences/Snap Lines/Layout Launcher, always exactly three) rather than a user-editable
/// collection like LayoutLauncherModel's saved profiles, but it's still painted through the same
/// generic list mechanism (GetListArea/PaintListRow) - RowsShown/AlwaysMaxRows exist here for the
/// same reason they do there: to size the list CONTAINER by row count without resizing the widget's
/// own body just to fit it (see WidgetManagerWidget.GetListArea).</summary>
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

    /// <summary>How many of the (always exactly three) rows the list reserves body space for at
    /// most (see WidgetManagerWidget.GetListArea) - same idea as LayoutLauncherModel.RowsShown, just
    /// against a fixed row count instead of a variable one. Defaults to all three, so an existing
    /// widget's on-screen look doesn't change until this is actually touched.</summary>
    public int RowsShown { get; set; } = 3;

    /// <summary>While on, RowsShown is kept pinned to the current (fixed) row count - see
    /// WidgetManagerWidget.SyncRowsShownToMax. Same knob as LayoutLauncherModel.AlwaysMaxRows;
    /// here it only ever matters right after being turned on, since the row count this widget's own
    /// list shows never actually changes on its own the way a saved-layout count can.</summary>
    public bool AlwaysMaxRows { get; set; }
}
