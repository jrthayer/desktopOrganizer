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
        // Checked reflects the registry Run key's actual current state (see StartupManager) rather
        // than a separately-persisted flag - read fresh every time the menu opens so an external
        // change (e.g. a user manually editing the Run key) never leaves this showing stale.
        var startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        startupItem.Click += OnToggleStartup;
        menu.Opening += (_, _) => startupItem.Checked = StartupManager.IsEnabled;
        menu.Items.Add(startupItem);
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

    // CheckOnClick already flipped the item's own Checked before this fires - just persist
    // whatever it now shows.
    private void OnToggleStartup(object? sender, EventArgs e) =>
        StartupManager.SetEnabled(((ToolStripMenuItem)sender!).Checked);

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
