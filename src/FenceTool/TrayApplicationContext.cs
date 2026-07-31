namespace FenceTool;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;

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
    }

    private void OnNewFence(object? sender, EventArgs e)
    {
        // TODO: wired up to FenceManager.CreateFence() in step 2.
        MessageBox.Show("New Fence isn't implemented yet.", "Fence Tool");
    }

    private void OnArrangeAll(object? sender, EventArgs e)
    {
        // TODO: wired up in step 6 once icon arrangement is implemented.
        MessageBox.Show("Arrange All isn't implemented yet.", "Fence Tool");
    }

    private void OnShowHideAll(object? sender, EventArgs e)
    {
        // TODO: wired up in step 10.
        MessageBox.Show("Show/Hide All isn't implemented yet.", "Fence Tool");
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        ExitThread();
    }
}
