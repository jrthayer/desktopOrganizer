using System.Runtime.InteropServices;
using DesktopTool.Features.Fences;
using DesktopTool.Native;
using DesktopTool.UI;

namespace DesktopTool.Features.Layouts.UI;

/// <summary>
/// "Layout Launcher" widget - a persistent on-screen panel listing every saved layout, styled and
/// behaving like a Fence: tint color, header darkness, opacity, tint strength, "full opacity when
/// active", a hideable title, and the same drag-to-snap-against-other-fences behavior (see
/// FenceForm's own WM_MOVING handling, which this mirrors). This is the only entry point into the
/// Layouts feature now - quick-run (click a row), Save Current Layout, and Manage Layouts... all
/// live here rather than a separate tray submenu. Separate from LayoutEditorForm (which edits a
/// layout's programs/placements in detail) - this is the one that stays parked on the desktop.
///
/// Unlike FenceForm this is a perfectly ordinary WinForms Form with real child controls, not a
/// layered/hand-painted window - there's no icon grid, file drag/drop, or desktop-icon interop to
/// justify that machinery here, just a list and a few buttons. Borderless (FormBorderStyle.None, no
/// native caption) to match a Fence's own chrome-less look; dragging still rides the same OS-native
/// interactive-move loop a real caption would (via the classic WM_NCLBUTTONDOWN/HTCAPTION trick off
/// the header panel - see OnHeaderMouseDown), with WM_MOVING intercepted in WndProc to inject
/// snapping exactly the way FenceForm does. There's deliberately no resize support - height is
/// always derived from the current layout count (see UpdateContentSize) and width never changes at
/// runtime, so there's no drag-a-corner scenario to hit-test for.
///
/// Persistent in the sense that mirrors a Fence: created once at startup (TrayApplicationContext),
/// remembers its position/title/styling/visibility across restarts (LayoutLauncherModel via
/// LayoutLauncherStore), and the "x" button/tray toggle only hide it rather than destroying it - the
/// same instance keeps living for the rest of the process, exactly like a Fence isn't recreated every
/// time "Show/Hide All" brings it back.
/// </summary>
internal sealed class LayoutLauncherWidget : Form
{
    private const int HTCAPTION = 0x2;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private const int HeaderHeight = 28;
    private const int RowHeight = 24;
    private const int MaxVisibleRows = 8;
    private const int EmptyStateHeight = 100;
    private const int ScrollBarWidth = 8;
    private const int ScrollBarGap = 2;

    private const int CmdToggleFullOpacityOnHover = 1;
    private const int CmdColorDefault = 2;
    private const int CmdColorCustom = 3;
    private const int CmdToggleHideTitle = 4;
    private const int CmdColorPresetBase = 10;

    private readonly LayoutManager _manager;
    private readonly FenceManager _fenceManager;
    private readonly LayoutLauncherModel _model;
    private readonly LayoutLauncherStore _store;

    private readonly Panel _headerPanel;
    private readonly Panel _headerSeparator;
    private readonly Label _titleLabel;
    private readonly TextBox _titleBox;
    private readonly DarkButton _settingsButton;
    private readonly DarkButton _closeButton;
    private readonly ContextMenuStrip _headerContextMenu;
    private readonly Panel _listBorder;
    private readonly ThemedListBox _list;
    private readonly ThemedScrollBar _scrollBar;
    private readonly Label _emptyLabel;
    private readonly DarkButton _saveButton;
    private readonly DarkButton _manageButton;

    private bool _dropdownOpen;
    private bool _dragging;
    private bool _allowClose;

    // Same trigger set as FenceForm's own _isActive (see that class's own comment on
    // ActivateFence/OnDeactivate): right-click anywhere on the widget, or a title-bar click with
    // either button - never a plain left-click on a list row, which just runs that layout the same
    // way clicking a fence's own shortcut icon doesn't activate the fence either.
    private bool _isActive;

    // Fixed anchor a drag measures against every WM_MOVING tick, instead of trusting the OS's own
    // incrementally-proposed rect - see FenceForm.WndProc's WM_MOVING case for why (drift/stickiness
    // otherwise).
    private Point _leftDragStartScreenPoint;
    private Rectangle _dragStartBounds;

    // Guid? carries the freshly-captured profile's Id up from OnSaveCurrentLayout (null from
    // _manageButton, which just wants whichever profile was already selected/none) so
    // TrayApplicationContext.OpenLayoutEditor can land straight on it, same as the old tray
    // "Save Current Layout" command used to.
    public event EventHandler<Guid?>? ManageLayoutsRequested;

    public LayoutLauncherWidget(LayoutManager manager, FenceManager fenceManager, LayoutLauncherModel model, LayoutLauncherStore store)
    {
        _manager = manager;
        _fenceManager = fenceManager;
        _model = model;
        _store = store;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        // Placeholder - UpdateContentSize (called at the end of this constructor) immediately
        // corrects the height to match the current layout count; only the width here is final.
        ClientSize = new Size(_model.Width, 200);
        Font = AppTheme.Font;

        if (_model.X is { } x && _model.Y is { } y)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }

        _headerPanel = new Panel { Location = new Point(0, 0), Size = new Size(ClientSize.Width, HeaderHeight) };
        _headerPanel.MouseDown += OnHeaderMouseDown;
        _headerPanel.MouseUp += OnHeaderMouseUp;

        _titleLabel = new Label
        {
            Location = new Point(8, 0),
            Size = new Size(ClientSize.Width - 8 - 56, HeaderHeight),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        _titleLabel.MouseDown += OnHeaderMouseDown;
        _titleLabel.MouseUp += OnHeaderMouseUp;
        _titleLabel.DoubleClick += (_, _) => StartRenaming();

        _titleBox = new TextBox
        {
            Location = new Point(6, 3),
            Size = new Size(ClientSize.Width - 8 - 56, 22),
            BorderStyle = BorderStyle.FixedSingle,
            // Never themed otherwise - a plain TextBox defaults to SystemColors.Window/WindowText
            // (a white box with black text), which sat directly on top of the tinted header behind
            // it as a jarring, unstyled rectangle. ApplyTint keeps this in sync with the header's
            // own color afterward (it's replacing the title label in the exact same strip, not
            // sitting in the plain body, so it matches the header specifically, not AppTheme.Field).
            BackColor = AppTheme.Field,
            ForeColor = AppTheme.Text,
            Visible = false,
        };
        _titleBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { CommitRename(); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { CancelRename(); e.Handled = true; e.SuppressKeyPress = true; }
        };
        _titleBox.Leave += (_, _) => CommitRename();

        // Live Func<Color> getters, not a one-time snapshot - same reason OpenSettingsMenu passes
        // its own DropdownMenu instances the same way. Without this the "Rename" right-click menu
        // stayed the plain, untinted AppTheme.Body/Hover TrayMenuRenderer defaults regardless of
        // whatever tint this widget's own header was showing, unlike FenceForm's equivalent native
        // rename menu (see FenceForm.ChromeFill/DrawMenuItem), which already tints itself.
        _headerContextMenu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(() => EffectiveField, () => EffectiveHover, () => AppTheme.Text),
            Font = AppTheme.Font,
        };
        _headerContextMenu.Items.Add("Rename", null, (_, _) => StartRenaming());

        _settingsButton = new DarkButton { Text = "⚙", Location = new Point(ClientSize.Width - 52, 3), Size = new Size(22, 22) };
        _settingsButton.Click += (_, _) => OpenSettingsMenu();

        _closeButton = new DarkButton { Text = "×", Location = new Point(ClientSize.Width - 26, 3), Size = new Size(22, 22) };
        _closeButton.Click += (_, _) => HideAndPersist();

        _headerSeparator = new Panel { Location = new Point(0, HeaderHeight), Size = new Size(ClientSize.Width, 1) };

        _emptyLabel = new Label
        {
            Text = "No layouts saved yet.\nUse \"Save Current Layout\"\nor \"Manage Layouts...\" below\nto create one.",
            Location = new Point(12, HeaderHeight + 13),
            Size = new Size(ClientSize.Width - 24, EmptyStateHeight),
            ForeColor = AppTheme.DisabledText,
            Visible = false,
        };

        // A 1px BackColor-filled frame around a borderless _list, rather than trusting a native
        // control's own border - a themed native border color can't be pushed to this widget's own
        // arbitrary tint RGB the way a plain WinForms BackColor can.
        _listBorder = new Panel
        {
            Location = new Point(12, HeaderHeight + 13),
            Size = new Size(ClientSize.Width - 24, EmptyStateHeight),
        };

        _list = new ThemedListBox
        {
            Location = new Point(1, 1),
            Size = new Size(_listBorder.Width - 2, _listBorder.Height - 2),
            ItemHeight = RowHeight,
        };
        _list.DrawItem += DrawRow;
        _list.MouseDown += OnListMouseDown;
        _listBorder.Controls.Add(_list);

        _scrollBar = new ThemedScrollBar
        {
            TrackColor = () => EffectiveField,
            ThumbColor = () => EffectiveHover,
        };
        _scrollBar.ValueChanged += v => _list.TopIndex = v;
        // ThemedListBox raises this for every reason its own TopIndex can change (mouse wheel,
        // arrow keys, or the ValueChanged handler just above setting it directly) - pulling the
        // thumb's drawn position back into agreement after all of them from a single event, rather
        // than a native ListBox's own scrolling paths each needing their own separate "resync
        // afterward" handler the way SyncValue used to be wired up here.
        _list.TopIndexChanged += v => _scrollBar.SyncValue(v);
        _listBorder.Controls.Add(_scrollBar);

        _saveButton = new DarkButton
        {
            Text = "Save Current Layout",
            Location = new Point(12, 0),
            Size = new Size(ClientSize.Width - 24, 28),
        };
        _saveButton.Click += (_, _) => OnSaveCurrentLayout();

        _manageButton = new DarkButton
        {
            Text = "Manage Layouts...",
            Location = new Point(12, 0),
            Size = new Size(ClientSize.Width - 24, 28),
        };
        _manageButton.Click += (_, _) => ManageLayoutsRequested?.Invoke(this, null);

        foreach (var button in new[] { _settingsButton, _closeButton, _saveButton, _manageButton })
            AppTheme.StyleButton(button);

        Controls.AddRange(new Control[]
        {
            _headerPanel, _titleLabel, _titleBox, _settingsButton, _closeButton, _headerSeparator,
            _emptyLabel, _listBorder, _saveButton, _manageButton,
        });
        // A control added earlier ends up in FRONT of one added later (Control.Controls.Add puts
        // each new child at z-order index 0, ahead of every previous sibling) - without this,
        // _headerPanel (added first, above, so its own drag/rename handlers would still work) sits
        // in front of and completely hides _titleLabel/_settingsButton/_closeButton, which are added
        // after it but occupy the exact same rectangle. They'd exist and be wired up correctly, just
        // invisible and unable to receive their own mouse events - clicks landing on _headerPanel
        // instead. SendToBack (not reordering the AddRange list itself) keeps that list readable in
        // its natural left-to-right visual order instead of the z-order's reversed one.
        _headerPanel.SendToBack();

        Activated += (_, _) => RefreshList(); // profiles may have changed via the editor while this was in the background
        AttachHoverTracking(this);
        AttachActivationTracking(this);

        ApplyTint();
        UpdateOpacity();
        RefreshTitleLabel();
        _titleLabel.Visible = !_model.HideTitle;
        RefreshList();
        RepositionHeaderButtons();
        UpdateHeaderButtonsVisibility();
    }

    /// <summary>Shows (persisting Visible) if currently hidden, hides (persisting Visible) if
    /// currently shown - what the tray's "Layout Launcher" checkbox toggles.</summary>
    public void ToggleVisible()
    {
        if (Visible)
            HideAndPersist();
        else
            ShowAndPersist();
    }

    private void ShowAndPersist()
    {
        Show();
        Activate();
        _model.Visible = true;
        Persist();
    }

    private void HideAndPersist()
    {
        Hide();
        _model.Visible = false;
        Persist();
    }

    /// <summary>Real disposal, for actual app shutdown (TrayApplicationContext.OnExit) - the only
    /// caller allowed to bypass OnFormClosing's cancel-and-hide below.</summary>
    public void Shutdown()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>Covers both the "x" button (which calls HideAndPersist directly and never reaches
    /// here) and anything else that might ask this window to close - Alt+F4 while it has focus,
    /// chiefly - so the widget survives everything except an explicit Shutdown() the same way a
    /// Fence survives everything except "Delete Fence". Windows logging off/shutting down (or Task
    /// Manager ending the process) is the one other case this must NOT cancel - e.Cancel = true
    /// there answers the OS's WM_QUERYENDSESSION with "this app refuses to close", which is exactly
    /// what left this window blocking shutdown until it got force-killed, corrupting whatever store
    /// write was in flight at the time instead of exiting (and saving) cleanly.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing
            or CloseReason.ApplicationExitCall or CloseReason.FormOwnerClosing))
        {
            e.Cancel = true;
            HideAndPersist();
            return;
        }

        base.OnFormClosing(e);
    }

    /// <summary>Same "losing focus always deactivates" rule as FenceForm.OnDeactivate - clears
    /// _isActive unconditionally, even though opening the settings dropdown (a separate top-level
    /// Form) also fires this. UpdateHeaderButtonsVisibility ORs in _dropdownOpen separately for
    /// exactly that case, so the buttons stay visible while the dropdown they belong to is still
    /// open, the same way ShowsSettingsButton ORs in FenceForm's own _dropdown.</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _isActive = false;
        UpdateHeaderButtonsVisibility();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        _model.X = Location.X;
        _model.Y = Location.Y;
        Persist();
    }

    private void Persist() => _store.Save(_model);

    /// <summary>Intercepts the OS's own interactive-move loop (already running by the time this
    /// arrives - see OnHeaderMouseDown, which kicks it off the same way a real caption drag would)
    /// to snap against other fences' edges and the app's custom snap lines, exactly mirroring
    /// FenceForm's own WM_MOVING handling - see that class's own comment on why the proposed rect is
    /// recomputed fresh from a fixed drag-start anchor every tick instead of trusting the RECT the
    /// OS proposes. There's no WM_SIZING equivalent here - this widget doesn't support resizing.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_MOVING)
        {
            var currentScreenPoint = Cursor.Position;
            var body = new Rectangle(
                _dragStartBounds.X + (currentScreenPoint.X - _leftDragStartScreenPoint.X),
                _dragStartBounds.Y + (currentScreenPoint.Y - _leftDragStartScreenPoint.Y),
                _dragStartBounds.Width, _dragStartBounds.Height);

            IReadOnlyList<int> vCandidates = Array.Empty<int>();
            IReadOnlyList<int> hCandidates = Array.Empty<int>();
            if ((MouseButtons & MouseButtons.Right) == 0)
                (vCandidates, hCandidates) = _fenceManager.GetOtherFenceEdges(Guid.Empty, _model.Margin);
            var result = _fenceManager.SnapLines.SnapMove(body, vCandidates, hCandidates, _model.Margin);
            WriteBackWindowRect(m.LParam, result.Rect);
            m.Result = (IntPtr)1;
            return;
        }

        base.WndProc(ref m);

        switch (m.Msg)
        {
            case WM_ENTERSIZEMOVE:
                _dragStartBounds = Bounds;
                _leftDragStartScreenPoint = Cursor.Position;
                // Same "right already held at the very first frame" check WM_MOVING itself does on
                // every later tick (see FenceForm.WndProc's own WM_ENTERSIZEMOVE comment) - without
                // this the drag-start guide overlay would show fence edges for one frame even when
                // right was already down before the drag began.
                if ((MouseButtons & MouseButtons.Right) == 0)
                {
                    var (vGuides, hGuides) = _fenceManager.GetOtherFenceEdges(Guid.Empty, _model.Margin);
                    var monitor = Screen.FromRectangle(Bounds).Bounds;
                    _fenceManager.SnapLines.BeginDrag(includeCustomLines: true, vGuides, hGuides, monitor);
                }
                else
                {
                    _fenceManager.SnapLines.BeginDrag();
                }
                break;

            case WM_EXITSIZEMOVE:
                _fenceManager.SnapLines.EndDrag();
                RepositionHeaderButtons();
                break;
        }
    }

    /// <summary>This widget has no FenceForm-style OuterMargin/TopBand padding between its window
    /// rect and its visible body - the client area is the whole window - so unlike
    /// FenceForm.WriteBackWindowRect this is a direct, unpadded copy into the RECT at lParam.</summary>
    private static void WriteBackWindowRect(IntPtr lParam, Rectangle body)
    {
        var rect = new RECT { Left = body.Left, Top = body.Top, Right = body.Right, Bottom = body.Bottom };
        Marshal.StructureToPtr(rect, lParam, false);
    }

    /// <summary>Same corner-flipping idea as FenceForm.GetSettingsButtonRect/
    /// ShouldSettingsButtonOpenLeft: the settings/close buttons default to the header's right
    /// corner, but flip to the left corner (with the title label reflowing to make room) whenever
    /// the options dropdown wouldn't fit opening rightward from there - measured against the actual
    /// screen the widget is currently on, using DropdownMenu.Measure the same way FenceForm does, so
    /// the button and the menu it opens always agree on which side before either one is drawn.
    /// Called once at startup and again after every drag (WM_EXITSIZEMOVE) - width never changes at
    /// runtime, so only moving across a monitor boundary can ever change the answer.</summary>
    private void RepositionHeaderButtons()
    {
        var onLeft = ShouldSettingsButtonOpenLeft();
        if (onLeft)
        {
            _settingsButton.Location = new Point(8, 3);
            _closeButton.Location = new Point(34, 3);
            _titleLabel.Location = new Point(60, 0);
            _titleLabel.Size = new Size(Math.Max(0, ClientSize.Width - 60 - 8), HeaderHeight);
            _titleBox.Location = new Point(58, 3);
            _titleBox.Size = new Size(Math.Max(0, ClientSize.Width - 60 - 8), 22);
        }
        else
        {
            _settingsButton.Location = new Point(ClientSize.Width - 52, 3);
            _closeButton.Location = new Point(ClientSize.Width - 26, 3);
            _titleLabel.Location = new Point(8, 0);
            _titleLabel.Size = new Size(Math.Max(0, ClientSize.Width - 8 - 56), HeaderHeight);
            _titleBox.Location = new Point(6, 3);
            _titleBox.Size = new Size(Math.Max(0, ClientSize.Width - 8 - 56), 22);
        }
    }

    /// <summary>Measures the actual options menu (BuildSettingsRows) against the screen this widget
    /// is currently on, using the button's default top-right placement as the anchor - i.e. "would
    /// the menu fit opening to the right of a right-corner button". Now just this widget's own call
    /// into StyleMenuRows.ShouldOpenLeft, the same shared overflow math FenceForm.
    /// ShouldSettingsButtonOpenLeft uses too.</summary>
    private bool ShouldSettingsButtonOpenLeft()
    {
        var rightAligned = new Rectangle(ClientSize.Width - 52, 3, 22, 22);
        var buttonScreenRect = new Rectangle(PointToScreen(rightAligned.Location), rightAligned.Size);
        return StyleMenuRows.ShouldOpenLeft(buttonScreenRect, BuildSettingsRows(), Font);
    }

    // Standard "drag a borderless window by some child control instead of a real title bar" trick -
    // handing HTCAPTION to the OS's own WM_NCLBUTTONDOWN handling gets a real OS-native interactive
    // move loop (which WndProc's WM_MOVING case above then hooks for snapping) for free.
    private void OnHeaderMouseDown(object? sender, MouseEventArgs e)
    {
        ActivateWidget();
        if (e.Button != MouseButtons.Left)
            return;

        _dragging = true;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, (uint)NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        _dragging = false;
        UpdateOpacity();
    }

    private void OnHeaderMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
            _headerContextMenu.Show((Control)sender!, e.Location);
    }

    private void StartRenaming()
    {
        _titleBox.Text = _model.Title;
        _titleBox.Visible = true;
        _titleLabel.Visible = false;
        _titleBox.Focus();
        _titleBox.SelectAll();
    }

    private void CommitRename()
    {
        if (!_titleBox.Visible)
            return;

        var name = _titleBox.Text.Trim();
        if (!string.IsNullOrEmpty(name) && name != _model.Title)
        {
            _model.Title = name;
            Persist();
        }

        RefreshTitleLabel();
        _titleBox.Visible = false;
        if (!_model.HideTitle)
            _titleLabel.Visible = true;
    }

    private void CancelRename()
    {
        _titleBox.Visible = false;
        if (!_model.HideTitle)
            _titleLabel.Visible = true;
    }

    private void RefreshTitleLabel() => _titleLabel.Text = _model.Title;

    /// <summary>"Save Current Layout" - a new profile pre-populated from whatever's actually open
    /// and where it's sitting right now (see LayoutManager.CaptureCurrentLayout), instead of
    /// building one program-by-program through the editor. Opens straight into the editor on the
    /// new profile afterward (via ManageLayoutsRequested) so it's immediately visible and
    /// renamable rather than just silently appearing in this list.</summary>
    private void OnSaveCurrentLayout()
    {
        var profile = _manager.CaptureCurrentLayout($"Layout {_manager.Profiles.Count + 1}");
        RefreshList();
        ManageLayoutsRequested?.Invoke(this, profile.Id);
    }

    private void RefreshList()
    {
        var previouslySelected = _list.SelectedIndex;

        _list.SetItems(_manager.Profiles.Select(p => p.Name));

        _emptyLabel.Visible = _manager.Profiles.Count == 0;
        _listBorder.Visible = _manager.Profiles.Count > 0;

        if (previouslySelected >= 0 && previouslySelected < _list.Items.Count)
            _list.SelectedIndex = previouslySelected;

        UpdateContentSize();
    }

    /// <summary>Resizes the window so its height always matches how many layouts there currently
    /// are (capped at MaxVisibleRows, past which the list scrolls instead of the window growing
    /// forever) - called after every RefreshList, so adding/removing/copying a layout resizes this
    /// immediately rather than leaving dead space or a cramped scrollbar.</summary>
    private void UpdateContentSize()
    {
        var count = _manager.Profiles.Count;
        var visibleRows = Math.Min(count, MaxVisibleRows);
        // _listBorder wraps _list in a 1px frame on every side (see _listBorder's own comment), so
        // sizing _listBorder to exactly visibleRows*RowHeight would leave _list itself 2px short of
        // a whole multiple of RowHeight - ThemedListBox would still draw correctly (it just clips
        // whatever height it's given, no native scrollbar to confuse), but the last row would show
        // 2px shorter than every row above it. The +2 here keeps _list's own height an exact
        // multiple of RowHeight so every visible row looks the same.
        var contentHeight = count == 0 ? EmptyStateHeight : visibleRows * RowHeight + 2;

        _listBorder.Size = new Size(_model.Width - 24, contentHeight);
        _emptyLabel.Size = new Size(_model.Width - 24, contentHeight);

        // Configure first - it's what decides _scrollBar.Visible, which _list's own width then
        // reads below to reclaim the reserved strip when nothing actually needs to scroll (the
        // same "only take the space when it's needed" call FenceForm's own scrollbar makes for its
        // fence's outer width - see its own comment at the ScrollbarWidth/ScrollbarMargin add).
        _scrollBar.Configure(count, visibleRows);
        var scrollBarReserve = _scrollBar.Visible ? ScrollBarWidth + ScrollBarGap : 0;
        _list.Size = new Size(_listBorder.Width - 2 - scrollBarReserve, _listBorder.Height - 2);
        _scrollBar.Location = new Point(_listBorder.Width - 1 - ScrollBarWidth, 1);
        _scrollBar.Size = new Size(ScrollBarWidth, _listBorder.Height - 2);

        var saveButtonY = HeaderHeight + 13 + contentHeight + 6;
        _saveButton.Location = new Point(12, saveButtonY);

        var manageButtonY = saveButtonY + 28 + 6;
        _manageButton.Location = new Point(12, manageButtonY);

        ClientSize = new Size(_model.Width, manageButtonY + 28 + 12);
    }

    /// <summary>Every row reserves two glyph strips at its right edge (delete, then copy, working
    /// inward from the edge) ahead of the profile name - same DrawRemovableListItem-style hit-testable
    /// glyph approach LayoutEditorForm's Programs/URLs lists already use, extended to two glyphs
    /// instead of one since a row here needs both actions.</summary>
    private void DrawRow(object? sender, DrawItemEventArgs e)
    {
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using (var background = new SolidBrush(selected ? EffectiveHover : EffectiveField))
            e.Graphics.FillRectangle(background, e.Bounds);

        if (e.Index < 0 || e.Index >= _list.Items.Count)
            return;

        var deleteRect = GetDeleteGlyphRect(e.Bounds);
        var copyRect = GetCopyGlyphRect(e.Bounds);
        var textRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, copyRect.X - e.Bounds.X - 8, e.Bounds.Height);

        TextRenderer.DrawText(e.Graphics, _list.Items[e.Index], _list.Font, textRect, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, "Copy", _list.Font, copyRect, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, "×", _list.Font, deleteRect, AppTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
    }

    private static Rectangle GetDeleteGlyphRect(Rectangle itemBounds) =>
        new(itemBounds.Right - 24, itemBounds.Top, 24, itemBounds.Height);

    private static Rectangle GetCopyGlyphRect(Rectangle itemBounds) =>
        new(itemBounds.Right - 24 - 40, itemBounds.Top, 40, itemBounds.Height);

    /// <summary>Delete/Copy glyphs are hit-tested first (same right-to-left priority order they're
    /// drawn in) - anything else on the row runs that layout immediately, no confirmation. Delete
    /// is the one exception that does confirm (see ConfirmAndDelete) - unlike removing a program
    /// from inside the editor, this throws away an entire saved layout.</summary>
    private void OnListMouseDown(object? sender, MouseEventArgs e)
    {
        var index = _list.IndexFromPoint(e.Location);
        if (index < 0 || index >= _manager.Profiles.Count)
            return;

        var profile = _manager.Profiles[index];
        var itemBounds = _list.GetItemRectangle(index);

        if (GetDeleteGlyphRect(itemBounds).Contains(e.Location))
        {
            ConfirmAndDelete(profile);
            return;
        }

        if (GetCopyGlyphRect(itemBounds).Contains(e.Location))
        {
            _manager.DuplicateLayout(profile.Id);
            RefreshList();
            return;
        }

        _ = _manager.RunLayoutAsync(profile.Id);
    }

    private void ConfirmAndDelete(LayoutProfile profile)
    {
        var result = MessageBox.Show(this, $"Delete \"{profile.Name}\"?", "Delete Layout",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
            return;

        _manager.DeleteLayout(profile.Id);
        RefreshList();
    }

    /// <summary>Same rows/shape as FenceForm.BuildOptionsMenuRows minus everything specific to an
    /// icon-grid fence (Hide Shortcut Names, OCD Fence Sizing, the OCD dimensions flyout) - Hide
    /// Title, the color grid, and all three sliders (Header Darkness/Opacity/Tint Strength) plus the
    /// Margin stepper all carry over with the same meaning (see this class's own ColorPresets copy
    /// and LayoutLauncherModel's own doc comments).</summary>
    private List<DropdownMenu.Row> BuildSettingsRows()
    {
        var rows = new List<DropdownMenu.Row>
        {
            new(CmdToggleHideTitle, "Hide Title", HasCheckbox: true, IsChecked: () => _model.HideTitle),
            new(CmdToggleFullOpacityOnHover, "Full Opacity When Active", HasCheckbox: true,
                IsChecked: () => _model.FullOpacityOnHover,
                Tooltip: "Full opacity while hovered, dragged, or this menu is open"),
            new(0, string.Empty, IsSeparator: true),
        };
        // The color grid + Header Darkness/Opacity/Tint Strength sliders + Margin stepper - same
        // shared block FenceForm's own options menu builds from, so this widget (and any future one)
        // never has its own slightly-different copy to drift out of sync or re-debug.
        rows.AddRange(StyleMenuRows.Build(_model, AppTheme.Body, CmdColorDefault, CmdColorCustom, CmdColorPresetBase,
            SetHeaderDarkness, SetOpacity, SetTintStrength, SetMargin));
        return rows;
    }

    private void OpenSettingsMenu()
    {
        var buttonScreenRect = _settingsButton.RectangleToScreen(_settingsButton.ClientRectangle);
        // Same rule FenceForm.ShowFenceOptionsMenu uses: the button and the menu it opens always
        // agree on which side, keyed off which corner RepositionHeaderButtons actually put the
        // button in (X==8 means it's on the flipped-to-left corner) rather than re-deriving it here.
        var preferLeft = _settingsButton.Location.X == 8;
        var dropdown = new DropdownMenu(BuildSettingsRows(), buttonScreenRect, preferLeft, Font,
            () => EffectiveField, () => EffectiveHover, () => EffectiveAccent, () => EffectiveBorder, () => EffectiveField);
        dropdown.ItemClicked += id =>
        {
            HandleCommand(id);
            dropdown.RefreshChecks();
        };
        _dropdownOpen = true;
        dropdown.FormClosed += (_, _) =>
        {
            _dropdownOpen = false;
            UpdateOpacity();
            UpdateHeaderButtonsVisibility();
        };
        dropdown.Show(this);
        UpdateOpacity();
    }

    /// <summary>Same trigger set as FenceForm.ActivateFence - see _isActive's own comment for why
    /// this is called explicitly from specific handlers instead of piggybacking on Activated, which
    /// fires for any click that gives this window OS focus, including a plain click on a list row
    /// just to run that layout.</summary>
    private void ActivateWidget()
    {
        if (_isActive)
            return;
        _isActive = true;
        UpdateHeaderButtonsVisibility();
    }

    /// <summary>_dropdownOpen ORs in separately from _isActive - see OnDeactivate's own comment for
    /// why (opening the settings dropdown steals OS focus from this widget, which would otherwise
    /// hide the very button that dropdown belongs to while it's still open).</summary>
    private void UpdateHeaderButtonsVisibility()
    {
        var show = _isActive || _dropdownOpen;
        _settingsButton.Visible = show;
        _closeButton.Visible = show;
    }

    /// <summary>Right-click anywhere on the widget activates it (see _isActive's own comment) - a
    /// title-bar click activates too, but that's wired directly into OnHeaderMouseDown instead of
    /// here since it also has to fire for a left-click, which this recursive walk deliberately
    /// leaves alone everywhere else (a left-click on a list row just runs that layout). Same
    /// recursive-attach shape as AttachHoverTracking, for the same reason - each child control is
    /// its own HWND, so there's no single event on the Form itself that would see every click.</summary>
    private void AttachActivationTracking(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                ActivateWidget();
        };
        foreach (Control child in control.Controls)
            AttachActivationTracking(child);
    }

    private void HandleCommand(int id)
    {
        switch (id)
        {
            case CmdToggleFullOpacityOnHover:
                _model.FullOpacityOnHover = !_model.FullOpacityOnHover;
                Persist();
                UpdateOpacity();
                break;
            case CmdToggleHideTitle:
                _model.HideTitle = !_model.HideTitle;
                Persist();
                _titleLabel.Visible = !_model.HideTitle && !_titleBox.Visible;
                break;
            default:
                // Default/preset/Custom... - same shared handling FenceForm's own color rows would
                // use if it were routed through this too (see StyleMenuRows' own doc comment).
                StyleMenuRows.TryHandleColorCommand(id, CmdColorDefault, CmdColorCustom, CmdColorPresetBase,
                    this, TintColorOrNull, SetTintColor);
                break;
        }
    }

    private void SetTintColor(Color? color)
    {
        _model.TintColor = color?.ToArgb();
        Persist();
        ApplyTint();
    }

    private void SetHeaderDarkness(int darkness)
    {
        _model.HeaderDarkness = Math.Clamp(darkness, 0, 100);
        Persist();
        ApplyTint();
    }

    private void SetOpacity(int opacity)
    {
        _model.Opacity = Math.Clamp(opacity, 15, 100);
        Persist();
        UpdateOpacity();
    }

    private void SetTintStrength(int strength)
    {
        _model.TintStrength = Math.Clamp(strength, 0, 100);
        Persist();
        ApplyTint();
    }

    private void SetMargin(int margin)
    {
        _model.Margin = Math.Clamp(margin, 0, 100);
        Persist();
    }

    private Color? TintColorOrNull => _model.TintColor is { } argb ? Color.FromArgb(argb) : null;
    private double TintFraction => _model.TintStrength / 100.0;

    private Color EffectiveBody => StyleTint.Tint(AppTheme.Body, TintColorOrNull, TintFraction);
    private Color EffectiveField => StyleTint.Tint(AppTheme.Field, TintColorOrNull, TintFraction);
    private Color EffectiveBorder => StyleTint.Tint(AppTheme.Border, TintColorOrNull, TintFraction);
    private Color EffectiveHover => StyleTint.Tint(AppTheme.Hover, TintColorOrNull, TintFraction);

    // Same "goes the exact chosen color, not just a diluted shift toward it" rule
    // FenceForm.Accent uses for its own glyphs/pressed-button state - a blended-toward-grey accent
    // reads muddy at small glyph sizes (the row's Copy/× text, a button's press flash), so this
    // skips TintFraction and either is the tint outright or, with no tint picked, the same neutral
    // gray AppTheme.Accent every other untinted control already uses.
    private Color EffectiveAccent => TintColorOrNull ?? AppTheme.Accent;

    // Same relationship as FenceForm.HeaderBaseColor/ThemedTitle - darkened toward black by
    // HeaderDarkness first, and tint blends into what's left of that at a fraction that shrinks
    // toward zero as darkness approaches 100% (a fully-blackened header has nothing left for a tint
    // to visibly shift).
    private Color HeaderBaseColor => StyleTint.DarkenTowardBlack(AppTheme.Body, _model.HeaderDarkness / 100.0);
    private Color EffectiveHeader => StyleTint.Tint(HeaderBaseColor, TintColorOrNull, TintFraction * (1 - _model.HeaderDarkness / 100.0));

    private void ApplyTint()
    {
        var body = EffectiveBody;
        var field = EffectiveField;
        var header = EffectiveHeader;
        var border = EffectiveBorder;
        var hover = EffectiveHover;
        var accent = EffectiveAccent;

        BackColor = body;
        ForeColor = AppTheme.Text;
        _headerPanel.BackColor = header;
        // A Label's default-transparent BackColor only composites against its actual parent's own
        // painted background (this Form's BackColor, i.e. "body") - it has no idea _headerPanel is
        // sitting visually behind it as a sibling, so left alone it shows the wrong (body) color
        // through itself anywhere its own bounding box overlaps the header strip. Matching
        // _headerPanel's color explicitly here is what actually makes the two look seamless.
        _titleLabel.BackColor = header;
        _titleLabel.ForeColor = AppTheme.Text;
        _titleBox.BackColor = header;
        _titleBox.ForeColor = AppTheme.Text;
        _headerSeparator.BackColor = border;
        _listBorder.BackColor = border;
        _list.BackColor = field;
        _list.ForeColor = AppTheme.Text;
        _emptyLabel.BackColor = body;
        foreach (var button in new[] { _settingsButton, _closeButton, _saveButton, _manageButton })
        {
            button.BackColor = field;
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.MouseOverBackColor = hover;
            button.FlatAppearance.MouseDownBackColor = accent;
        }

        Invalidate(true);
    }

    /// <summary>Recomputes Opacity from scratch on every call rather than nudging it incrementally -
    /// cheap enough (a handful of controls, not a per-frame render loop like FenceForm's own
    /// animation) that there's no reason to track "is it currently easing" state at all.</summary>
    private void UpdateOpacity()
    {
        if (!_model.FullOpacityOnHover)
        {
            Opacity = _model.Opacity / 100.0;
            return;
        }

        var active = ClientRectangle.Contains(PointToClient(Cursor.Position)) || _dropdownOpen || _dragging;
        Opacity = active ? 1.0 : _model.Opacity / 100.0;
    }

    /// <summary>MouseEnter/MouseLeave on child controls (each its own HWND) fire whenever the cursor
    /// crosses between the form's own client area and a child - including a child fully inside the
    /// form's bounds - not just at the form's outer edge. Attaching the same geometric recheck to
    /// every control instead of trusting which one fired sidesteps that, since UpdateOpacity's own
    /// Cursor.Position/ClientRectangle check is authoritative regardless of which HWND the OS
    /// actually routed the event to.</summary>
    private void AttachHoverTracking(Control control)
    {
        control.MouseEnter += (_, _) => UpdateOpacity();
        control.MouseLeave += (_, _) => UpdateOpacity();
        foreach (Control child in control.Controls)
            AttachHoverTracking(child);
    }

}
