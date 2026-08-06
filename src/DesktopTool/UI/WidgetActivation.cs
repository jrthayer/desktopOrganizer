namespace DesktopTool.UI;

/// <summary>Shared "only show engagement chrome while actually engaged" state machine behind
/// FenceForm's settings/new/delete buttons and LayoutLauncherWidget's gear/close buttons - visible
/// only while explicitly activated (right-click anywhere, or a title-bar click with either button -
/// never a plain left-click on content, which should just act on whatever's under it, the same way
/// clicking a fence's shortcut icon or a layout's list row doesn't activate its own widget) or while
/// a menu that belongs to it is still open. That second condition needs to be tracked separately
/// from IsActive rather than folded into it: a settings dropdown is a real, separate top-level Form,
/// so showing one steals OS activation from the widget that opened it, which would otherwise
/// deactivate (and hide) the very button that dropdown hangs off of while it's still open.
///
/// Deliberately not driven by a Control's own Activated event - that fires for any click that gives
/// a window OS focus, including a plain click just to use whatever's inside it. Callers activate
/// explicitly instead, from whichever specific handlers count as "engaging" the widget (see each
/// caller's own WM_NCLBUTTONDOWN/WM_NCRBUTTONDOWN or MouseDown wiring), and call Deactivate() from
/// their own OnDeactivate override, which - unlike activation - fires unconditionally on any focus
/// loss and needs no per-widget judgment call about what counts.</summary>
internal sealed class WidgetActivation
{
    private bool _isActive;
    private bool _menuOpen;

    public bool ShouldShow => _isActive || _menuOpen;

    /// <summary>Fires whenever either underlying flag changes, even on ticks where ShouldShow's own
    /// answer doesn't flip (e.g. Activate() while MenuOpen is already true) - a caller's repaint/
    /// visibility sync is cheap enough that it's not worth tracking ShouldShow's own edges
    /// separately just to skip a handful of redundant calls.</summary>
    public event Action? Changed;

    public void Activate()
    {
        if (_isActive)
            return;
        _isActive = true;
        Changed?.Invoke();
    }

    public void Deactivate()
    {
        if (!_isActive)
            return;
        _isActive = false;
        Changed?.Invoke();
    }

    public bool MenuOpen
    {
        get => _menuOpen;
        set
        {
            if (_menuOpen == value)
                return;
            _menuOpen = value;
            Changed?.Invoke();
        }
    }
}
