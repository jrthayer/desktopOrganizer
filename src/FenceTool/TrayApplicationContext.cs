using FenceTool.Fences;
using FenceTool.UI;

namespace FenceTool;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly FenceManager _fenceManager = new();
    private bool _allVisible = true;

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            BackColor = AppTheme.Body,
            ForeColor = AppTheme.Text,
            Font = AppTheme.Font,
            // No item carries an icon, so the reserved image-margin strip down the left edge would
            // otherwise just be an empty light-gray band next to every row - only the check-mark
            // margin (ShowCheckMargin, left at its default) is actually used, by the two toggles
            // below.
            ShowImageMargin = false,
        };
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
        menu.Items.Add("Manage Snap Lines...", null, OnManageSnapLines);
        menu.Items.Add(new ToolStripSeparator());
        // Only one Recycle Bin item is allowed across every fence at once (see
        // FenceManager.HasRecycleBin) - hidden entirely once one exists anywhere, re-checked fresh
        // on every open same as the toggles above.
        var addRecycleBinItem = new ToolStripMenuItem("Add Recycle Bin", null, OnAddRecycleBin);
        menu.Opening += (_, _) => addRecycleBinItem.Visible = !_fenceManager.HasRecycleBin;
        menu.Items.Add(addRecycleBinItem);
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

    private void OnManageSnapLines(object? sender, EventArgs e) =>
        _fenceManager.SnapLines.EnterEditMode();

    private void OnAddRecycleBin(object? sender, EventArgs e) =>
        _fenceManager.AddRecycleBin();

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
