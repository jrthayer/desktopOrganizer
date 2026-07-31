namespace FenceTool.Native;

/// <summary>
/// An invisible top-level window whose only purpose is to receive the "TaskbarCreated"
/// broadcast Explorer sends after it (re)starts. Message-only windows (HWND_MESSAGE) are
/// excluded from broadcast delivery, so this has to be a real (if invisible) top-level form.
/// </summary>
internal sealed class BackgroundMessageWindow : Form
{
    private readonly int _taskbarCreatedMessage;

    public event EventHandler? TaskbarCreated;

    public BackgroundMessageWindow()
    {
        _taskbarCreatedMessage = (int)NativeMethods.RegisterWindowMessage("TaskbarCreated");
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        Size = new Size(0, 0);
        Opacity = 0;
        Show();
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == _taskbarCreatedMessage)
            TaskbarCreated?.Invoke(this, EventArgs.Empty);

        base.WndProc(ref m);
    }
}
