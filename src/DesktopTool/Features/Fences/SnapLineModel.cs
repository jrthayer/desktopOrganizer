using System.Text.Json.Serialization;
using DesktopTool.Features.Snapping;

namespace DesktopTool.Features.Fences;

/// <summary>A user-placed guide line that fence edges (and later, other widgets) can snap onto -
/// see SnapLineManager. Spans only the monitor it was created (or last dragged) onto, rather than
/// the full virtual screen, the same way Photoshop/Illustrator ruler guides are scoped to one
/// canvas rather than bleeding across unrelated ones.</summary>
public sealed class SnapLineModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SnapOrientation Orientation { get; set; }

    /// <summary>Absolute physical-screen pixel coordinate - Y for Horizontal, X for Vertical, same
    /// convention as FenceModel.Bounds.</summary>
    public int Position { get; set; }

    // Flattened rather than a Rectangle directly - same reasoning as FenceModel.Bounds below for
    // why (Rectangle doesn't round-trip through System.Text.Json cleanly). Zero (MonitorWidth/
    // Height both 0) means "not yet assigned" - lines saved before this field existed load this
    // way, and are treated as unscoped/global rather than an invisible zero-size rect - see
    // SnapLineManager's own fallback handling.
    public int MonitorX { get; set; }
    public int MonitorY { get; set; }
    public int MonitorWidth { get; set; }
    public int MonitorHeight { get; set; }

    [JsonIgnore]
    public Rectangle MonitorBounds
    {
        get => new(MonitorX, MonitorY, MonitorWidth, MonitorHeight);
        set
        {
            MonitorX = value.X;
            MonitorY = value.Y;
            MonitorWidth = value.Width;
            MonitorHeight = value.Height;
        }
    }
}
