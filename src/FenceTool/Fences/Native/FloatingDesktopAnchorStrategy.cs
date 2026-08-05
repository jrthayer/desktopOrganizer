namespace FenceTool.Fences.Native;

/// <summary>
/// No-op fallback: leaves the fence as an ordinary top-level window (visually on top of icons,
/// not interleaved behind them). Used if embedding onto Progman/WorkerW proves unreliable.
/// </summary>
public sealed class FloatingDesktopAnchorStrategy : IDesktopAnchorStrategy
{
    public void Apply(IntPtr formHandle, Rectangle desiredScreenBounds)
    {
        // Intentionally does nothing - the form stays exactly as WinForms created it.
    }
}
