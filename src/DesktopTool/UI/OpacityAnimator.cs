namespace DesktopTool.UI;

/// <summary>Eases a hand-painted layered window's own render opacity toward a live target value
/// (FenceForm's "Full Opacity When Active" easing, now shared) - a plain WinForms Form.Opacity
/// doesn't apply here, since these windows push their own bitmap via UpdateLayeredWindow
/// (LayeredWindowPresenter) instead of letting the OS composite a whole-window alpha. Value is what
/// each render should actually use; target is read fresh on every tick (and on BeginIfNeeded/
/// SnapToTarget) rather than captured once, since hover/drag/menu-open state - and so the target
/// itself - can change at any moment.</summary>
internal sealed class OpacityAnimator : IDisposable
{
    private const float AnimationStep = 0.06f;

    private readonly System.Windows.Forms.Timer _timer;
    private readonly Func<float> _target;
    private readonly Action _onStep;

    public float Value { get; private set; }

    public OpacityAnimator(float initial, Func<float> target, Action onStep)
    {
        Value = initial;
        _target = target;
        _onStep = onStep;
        _timer = new System.Windows.Forms.Timer { Interval = 15 };
        _timer.Tick += (_, _) => Advance();
    }

    /// <summary>Starts (if not already running) the tick loop that eases Value toward the target -
    /// a no-op if they already match, so this can be called unconditionally from every hover/drag/
    /// menu-open state change without the caller checking whether animation is actually needed
    /// first.</summary>
    public void BeginIfNeeded()
    {
        if (!_timer.Enabled && Math.Abs(Value - _target()) > 0.001f)
            _timer.Start();
    }

    /// <summary>Jumps straight to the current target instead of easing - for changes that need to
    /// track immediately (a slider drag) or where an animated lag would repaint using a stale value
    /// (a color/opacity change made from somewhere other than hover/drag).</summary>
    public void SnapToTarget()
    {
        _timer.Stop();
        Value = _target();
    }

    private void Advance()
    {
        var target = _target();
        var delta = target - Value;
        if (Math.Abs(delta) <= AnimationStep)
        {
            Value = target;
            _timer.Stop();
        }
        else
        {
            Value += Math.Sign(delta) * AnimationStep;
        }
        _onStep();
    }

    public void Dispose() => _timer.Dispose();
}
