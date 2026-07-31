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
        menu.Items.Add("Arrange All", null, OnArrangeAll);
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

        _fenceManager.LoadAndShowAll();
    }

    private void OnNewFence(object? sender, EventArgs e) => _fenceManager.CreateFence();

    private void OnArrangeAll(object? sender, EventArgs e) => _fenceManager.ArrangeAll();

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
