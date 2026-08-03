using FenceTool.Fences;

namespace FenceTool;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly FenceManager _fenceManager = new();
    private bool _allVisible = true;

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
        // Same "checked reflects the actual current state, read fresh every open" approach as
        // Start with Windows above - this is a system-wide Explorer setting, not something Fence
        // Tool owns, so a user (or another app) changing it outside this menu should never leave
        // the checkbox showing stale.
        var hiddenFilesItem = new ToolStripMenuItem("Show Hidden Files") { CheckOnClick = true };
        hiddenFilesItem.Click += OnToggleHiddenFiles;
        menu.Opening += (_, _) => hiddenFilesItem.Checked = HiddenFilesManager.IsEnabled;
        menu.Items.Add(hiddenFilesItem);
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

        _fenceManager.LoadAndShowAll();
    }

    private void OnNewFence(object? sender, EventArgs e) => _fenceManager.CreateFence();

    // CheckOnClick already flipped the item's own Checked before this fires - just persist
    // whatever it now shows.
    private void OnToggleStartup(object? sender, EventArgs e) =>
        StartupManager.SetEnabled(((ToolStripMenuItem)sender!).Checked);

    // Doesn't force the desktop to visually pick this up - see README's Tray menu limitations.
    private void OnToggleHiddenFiles(object? sender, EventArgs e) =>
        HiddenFilesManager.SetEnabled(((ToolStripMenuItem)sender!).Checked);

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
