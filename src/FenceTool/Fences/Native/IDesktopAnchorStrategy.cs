namespace FenceTool.Fences.Native;

/// <summary>
/// Controls how a fence window sits relative to the desktop. Kept swappable so the riskier
/// embedded behavior can fall back to a plain floating window if desktop-embedding ever proves
/// unreliable on some Windows build.
/// </summary>
public interface IDesktopAnchorStrategy
{
    void Apply(IntPtr formHandle, Rectangle desiredScreenBounds);
}
