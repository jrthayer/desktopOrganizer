using DesktopTool.Features.Fences;
using DesktopTool.Features.Layouts;
using DesktopTool.Features.Layouts.UI;
using DesktopTool.UI;

namespace DesktopTool;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly FenceManager _fenceManager = new();
    private readonly LayoutManager _layoutManager = new();
    private readonly LayoutLauncherStore _layoutLauncherStore = new();
    private bool _allVisible = true;

    // At most one editor open at a time - OnManageLayouts activates this instead of opening a
    // second copy, the same "don't duplicate, just surface the existing one" idea FenceManager's
    // own SnapLines edit mode already follows for its overlay/panel pair.
    private LayoutEditorForm? _layoutEditor;

    // Unlike _layoutEditor, created once up front (see the constructor) and never recreated for the
    // rest of the process - this is meant to be a persistent desktop element like a Fence, not a
    // window opened fresh each time from the tray. "Layout Launcher" in the Widgets menu toggles its
    // Visible state (LayoutLauncherWidget.ToggleVisible) rather than creating/disposing it.
    private readonly LayoutLauncherWidget _layoutLauncher;

    public TrayApplicationContext()
    {
        _layoutManager.Load();

        var layoutLauncherModel = _layoutLauncherStore.Load();
        _layoutLauncher = new LayoutLauncherWidget(_layoutManager, _fenceManager, layoutLauncherModel, _layoutLauncherStore);
        _layoutLauncher.ManageLayoutsRequested += (_, _) => OpenLayoutEditor(null);

        var menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            BackColor = AppTheme.Body,
            ForeColor = AppTheme.Text,
            Font = AppTheme.Font,
            // No item carries an icon, so the reserved image-margin strip down the left edge would
            // otherwise just be an empty light-gray band next to every row. ShowCheckMargin defaults
            // to false, not true - without turning it on explicitly here, suppressing the image
            // margin left no margin at all for a checkmark to render into, silently dropping the
            // checkmark on the two toggles below entirely instead of just moving it.
            ShowImageMargin = false,
            ShowCheckMargin = true,
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
        // Rebuilt fresh on every open (profile names/count can change from the editor while this
        // menu is closed) rather than kept in sync incrementally - same "just re-derive from the
        // live source of truth" approach the checked-state toggles above already use.
        var layoutsItem = new ToolStripMenuItem("Layouts");
        menu.Opening += (_, _) => RebuildLayoutsMenu(layoutsItem);
        menu.Items.Add(layoutsItem);
        menu.Items.Add(new ToolStripSeparator());
        // Its own category, separate from "Layouts" above - that submenu is the no-window quick-run
        // list; this one is for on-screen panels that stay open, currently just the launcher widget
        // but named/grouped so a future widget has somewhere to go without another top-level entry.
        // CheckOnClick would fight ToggleVisible's own idea of the current state (it flips Checked
        // itself before the Click handler runs, same as startupItem/hiddenFilesItem above already
        // avoid) - toggled and reflected explicitly instead, still "read fresh every open" like those.
        var widgetsItem = new ToolStripMenuItem("Widgets");
        var layoutLauncherItem = new ToolStripMenuItem("Layout Launcher");
        layoutLauncherItem.Click += (_, _) => _layoutLauncher.ToggleVisible();
        menu.Opening += (_, _) => layoutLauncherItem.Checked = _layoutLauncher.Visible;
        widgetsItem.DropDownItems.Add(layoutLauncherItem);
        menu.Items.Add(widgetsItem);
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

        // Only actually shown if it was left visible last session - Show() itself doesn't touch
        // Visible in the model the way ToggleVisible does, so this doesn't re-persist a value that's
        // already exactly what was just loaded.
        if (layoutLauncherModel.Visible)
            _layoutLauncher.Show();
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

    private void RebuildLayoutsMenu(ToolStripMenuItem layoutsItem)
    {
        layoutsItem.DropDownItems.Clear();
        layoutsItem.DropDownItems.Add("Save Current Layout", null, OnSaveCurrentLayout);

        if (_layoutManager.Profiles.Count > 0)
            layoutsItem.DropDownItems.Add(new ToolStripSeparator());
        foreach (var profile in _layoutManager.Profiles)
        {
            var item = new ToolStripMenuItem(profile.Name);
            item.Click += async (_, _) => await _layoutManager.RunLayoutAsync(profile.Id);
            layoutsItem.DropDownItems.Add(item);
        }

        layoutsItem.DropDownItems.Add(new ToolStripSeparator());
        layoutsItem.DropDownItems.Add("Manage Layouts...", null, (_, _) => OpenLayoutEditor(null));
    }

    // "Save Current Layout" is the primary way to build a profile now - arrange windows the way
    // you want them, then capture, instead of picking each program/monitor/placement by hand
    // through the editor. Opens straight into the editor on the new profile so it's immediately
    // visible (and renamable) rather than just silently appearing in the submenu next time it opens.
    private void OnSaveCurrentLayout(object? sender, EventArgs e)
    {
        var profile = _layoutManager.CaptureCurrentLayout($"Layout {_layoutManager.Profiles.Count + 1}");
        OpenLayoutEditor(profile.Id);
    }

    private void OpenLayoutEditor(Guid? initialProfileId)
    {
        if (_layoutEditor is { IsDisposed: false })
        {
            if (initialProfileId is { } id)
                _layoutEditor.SelectProfileById(id);
            _layoutEditor.Activate();
            return;
        }

        _layoutEditor = new LayoutEditorForm(_layoutManager, initialProfileId);
        _layoutEditor.FormClosed += (_, _) => _layoutEditor = null;
        _layoutEditor.Show();
    }

    private void OnShowHideAll(object? sender, EventArgs e)
    {
        _allVisible = !_allVisible;
        _fenceManager.SetAllVisible(_allVisible);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _layoutEditor?.Dispose();
        _layoutLauncher.Shutdown();
        _fenceManager.Dispose();
        ExitThread();
    }
}
