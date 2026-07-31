using FenceTool.Fences;

namespace FenceTool;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly FenceManager _fenceManager = new();
    private bool _allVisible = true;
    private bool _accessDeniedShown;

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("New Fence", null, OnNewFence);
        menu.Items.Add("Show/Hide All", null, OnShowHideAll);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Fence Tool",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += OnShowHideAll;

        _fenceManager.DesktopAccessDenied += OnDesktopAccessDenied;
        _fenceManager.LoadAndShowAll();
    }

    private void OnDesktopAccessDenied(object? sender, EventArgs e)
    {
        if (_accessDeniedShown)
            return;
        _accessDeniedShown = true;

        _trayIcon.ShowBalloonTip(10000, "Fence Tool",
            "Explorer is running with different privileges than Fence Tool (e.g. elevated), " +
            "so desktop icons can't be managed until that's resolved.", ToolTipIcon.Warning);
    }

    private void OnNewFence(object? sender, EventArgs e) => _fenceManager.CreateFence();

    private void OnShowHideAll(object? sender, EventArgs e)
    {
        _allVisible = !_allVisible;
        _fenceManager.SetAllVisible(_allVisible);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _fenceManager.Dispose();
        ExitThread();
    }
}
