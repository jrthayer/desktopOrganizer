using System.Drawing.Drawing2D;
using FenceTool.Fences;
using FenceTool.Native;
using FenceTool.Snapping;
using FenceTool.UI;

namespace FenceTool.Fences.UI;

/// <summary>
/// Small corner panel shown alongside SnapLineEditOverlay during "snap line edit mode" - lets a new
/// custom line be added by typed orientation+position+screen, or an existing one (selected by
/// clicking it directly on the overlay - see SnapLineEditOverlay.LineSelected) be repositioned,
/// re-homed to a different monitor, or removed.
///
/// Every field here is either a plain owner-drawable control (RadioButton/Button, both switched to
/// FlatStyle.Flat) or one of this file's own nested replacements (ComboButton, DarkNumericField) for
/// the two stock controls whose native chrome fights a dark theme: a plain ComboBox's dropdown
/// popup and a NumericUpDown's spin-button pair are both rendered by the OS using visual styles that
/// bake in a light-theme background no BackColor/ForeColor override reaches (DropdownMenu ran into
/// the exact same wall with its own tooltip - see its own field comment). Reusing DropdownMenu
/// itself for the two option pickers keeps their popup pixel-identical to every other dropdown in
/// the app instead of a second, slightly-different-looking implementation.
/// </summary>
internal sealed class SnapLinePanel : Form
{
    private enum Reference
    {
        Top,
        Bottom,
        Left,
        Right,
    }

    private readonly RadioButton _horizontalRadio;
    private readonly RadioButton _verticalRadio;
    private readonly ComboButton _screenCombo;
    private readonly ComboButton _referenceCombo;
    private readonly DarkNumericField _positionInput;
    private readonly Button _addUpdateButton;
    private readonly Button _deleteButton;

    // Backing values for _screenCombo/_referenceCombo - ComboButton only knows about display strings
    // and a selected index, so the actual ScreenOption/Reference a given index means lives here
    // instead (mirrors what ComboBox.Items/SelectedItem used to hold directly).
    private readonly List<ScreenOption> _screenOptions = new();
    private readonly List<Reference> _referenceOptions = new();

    private Guid? _selectedId;
    private Reference _lastReference = Reference.Top;

    // Set while this panel is itself rewriting its own controls (PopulateFrom, orientation
    // switches) - without this, resetting _referenceCombo's items/selection to reflect the new
    // orientation would re-trigger the "preserve absolute position across a reference change"
    // conversion in OnReferenceChanged, corrupting the value being displayed rather than just
    // showing it under a new reference.
    private bool _isPopulating;

    public event Action<SnapOrientation, int, Rectangle>? AddRequested;
    public event Action<Guid, SnapOrientation, int, Rectangle>? UpdateRequested;
    public event Action<Guid>? DeleteRequested;
    public event Action? NewLineRequested;
    public event Action? CloseRequested;

    public SnapLinePanel()
    {
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "Snap Lines";
        ClientSize = new Size(220, 354);
        MaximizeBox = false;
        MinimizeBox = false;
        Font = AppTheme.Font;
        BackColor = AppTheme.Body;
        ForeColor = AppTheme.Text;

        // Screen (which monitor) leads, since it's the coarsest choice and frames how Horizontal/
        // Vertical and the position rows below even read; a separator brackets the position rows
        // specifically (reference edge + numeric offset together are "where exactly", one unit) to
        // set that block apart from the orientation/screen choices above it and the New/Add/Delete/
        // Close actions below.
        var screenLabel = new Label { Text = "Screen", Location = new Point(12, 12), AutoSize = true };
        _screenCombo = new ComboButton { Location = new Point(12, 32), Width = 196, Height = 24 };
        PopulateScreens();

        // FlatStyle.Flat, not just BackColor/ForeColor - a themed RadioButton's glyph is a small
        // bitmap that bakes in its own light-theme background regardless of the control's own colors
        // (the same reason ComboBox/NumericUpDown needed replacing outright below, just cheaper to
        // fix here since Flat still renders a real - if plainer - glyph instead of needing a full
        // owner-drawn replacement).
        _horizontalRadio = new RadioButton { Text = "Horizontal", Checked = true, Location = new Point(12, 64), AutoSize = true, FlatStyle = FlatStyle.Flat };
        _verticalRadio = new RadioButton { Text = "Vertical", Location = new Point(12, 88), AutoSize = true, FlatStyle = FlatStyle.Flat };
        _horizontalRadio.CheckedChanged += (_, _) => PopulateReferenceOptions();
        _verticalRadio.CheckedChanged += (_, _) => PopulateReferenceOptions();

        var topSeparator = CreateSeparator(122);

        var positionLabel = new Label { Text = "Position", Location = new Point(12, 136), AutoSize = true };
        _referenceCombo = new ComboButton { Location = new Point(12, 156), Width = 196, Height = 24 };
        _referenceCombo.SelectedIndexChanged += _ => OnReferenceChanged();
        PopulateReferenceOptions();

        _positionInput = new DarkNumericField
        {
            Location = new Point(12, 184),
            Width = 196,
            Height = 24,
            // Distances are always measured inward from an edge (never negative) - the working
            // area itself is what bounds how large a sensible value is, not this field.
            Minimum = 0,
            Maximum = 32000,
        };

        var bottomSeparator = CreateSeparator(222);

        // Lets a line that's currently selected/being edited be abandoned in favor of starting a
        // fresh one, without deleting it - the only other way back to "Add" mode was deleting
        // whatever was selected first.
        var newButton = new Button { Text = "New Line", Location = new Point(12, 236), Width = 196 };
        newButton.Click += (_, _) =>
        {
            ClearSelection();
            NewLineRequested?.Invoke();
        };

        _addUpdateButton = new Button { Text = "Add", Location = new Point(12, 268), Width = 90 };
        _addUpdateButton.Click += (_, _) => CommitClicked();

        _deleteButton = new Button { Text = "Delete", Location = new Point(118, 268), Width = 90, Enabled = false };
        _deleteButton.Click += (_, _) => DeleteClicked();

        // Sets the Close button apart from Add/Delete the same way the two separators above set the
        // position rows apart from what's above/below them - it's not part of "editing this line",
        // just "leave edit mode entirely".
        var closeSeparator = CreateSeparator(305);

        var closeButton = new Button { Text = "Close", Location = new Point(12, 319), Width = 196 };
        closeButton.Click += (_, _) => Close();

        foreach (var button in new[] { newButton, _addUpdateButton, _deleteButton, closeButton })
            StyleButton(button);

        Controls.AddRange(new Control[]
        {
            screenLabel, _screenCombo, _horizontalRadio, _verticalRadio, topSeparator, positionLabel,
            _referenceCombo, _positionInput, bottomSeparator, newButton, _addUpdateButton, _deleteButton,
            closeSeparator, closeButton,
        });
    }

    /// <summary>A flat 1px divider line, inset to match every field's own left/right margin - plain
    /// BackColor fill rather than an owner-painted control, since a static line has nothing to hover/
    /// click/redraw for.</summary>
    private static Panel CreateSeparator(int y) =>
        new() { Location = new Point(12, y), Width = 196, Height = 1, BackColor = AppTheme.Border };

    /// <summary>Flat/dark to match the rest of the app's chrome (DropdownMenu's rows, FenceForm's
    /// settings button) instead of a stock Button's raised, system-colored 3D face - FlatStyle alone
    /// only swaps the border rendering, so BackColor/FlatAppearance still need setting explicitly for
    /// the face and hover/press states to actually go dark too.</summary>
    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = AppTheme.Field;
        button.ForeColor = AppTheme.Text;
        button.FlatAppearance.BorderColor = AppTheme.Border;
        button.FlatAppearance.MouseOverBackColor = AppTheme.Hover;
        button.FlatAppearance.MouseDownBackColor = AppTheme.Accent;
    }

    /// <summary>Follows Windows' own dark-mode caption/border chrome - without this the native title
    /// bar this FixedToolWindow still has would render in light-theme white no matter how dark the
    /// client area underneath is, reading as two mismatched halves of one window.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var useDarkMode = 1;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
    }

    /// <summary>Pinned to the primary screen's top-right working-area corner - fixed, not
    /// anchor-relative, since edit mode spans every monitor and there's no single natural anchor
    /// point to follow.</summary>
    public void PositionTopRight()
    {
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(workingArea.Right - Width - 16, workingArea.Top + 16);
    }

    public void PopulateFrom(SnapLineModel line)
    {
        _isPopulating = true;
        _selectedId = line.Id;
        _horizontalRadio.Checked = line.Orientation == SnapOrientation.Horizontal;
        _verticalRadio.Checked = line.Orientation == SnapOrientation.Vertical;
        PopulateReferenceOptions(); // orientation may not have actually changed, so CheckedChanged wouldn't have fired
        SelectScreen(line.MonitorBounds);
        _isPopulating = false;

        SetDisplayedPosition(line.Position);
        _addUpdateButton.Text = "Update";
        _deleteButton.Enabled = true;
    }

    /// <summary>Deselects whatever line was populating the form (called after a delete, or from the
    /// New Line button) and resets every field to a blank-slate default rather than leaving stale
    /// values behind from whatever was just being edited.</summary>
    public void ClearSelection()
    {
        _selectedId = null;
        _addUpdateButton.Text = "Add";
        _deleteButton.Enabled = false;

        _horizontalRadio.Checked = true;
        PopulateReferenceOptions(); // orientation may already have been Horizontal, so CheckedChanged wouldn't fire
        var primaryIndex = Array.FindIndex(Screen.AllScreens, s => s.Primary);
        _screenCombo.SetSelectedIndex(Math.Max(primaryIndex, 0));
        _positionInput.Value = 0;
    }

    private void PopulateScreens()
    {
        _screenOptions.Clear();
        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
            _screenOptions.Add(new ScreenOption(i, screens[i].Bounds, screens[i].WorkingArea));

        var primaryIndex = Array.FindIndex(screens, s => s.Primary);
        _screenCombo.SetItems(_screenOptions.Select(o => o.ToString()).ToList(), Math.Max(primaryIndex, 0));
    }

    /// <summary>Selects whichever combo entry's bounds match - a legacy/unscoped line (zero-size
    /// MonitorBounds) or a monitor that's no longer connected leaves the combo showing whatever it
    /// already had rather than guessing.</summary>
    private void SelectScreen(Rectangle bounds)
    {
        for (var i = 0; i < _screenOptions.Count; i++)
        {
            if (_screenOptions[i].Bounds == bounds)
            {
                _screenCombo.SetSelectedIndex(i);
                return;
            }
        }
    }

    /// <summary>Horizontal lines measure from Top/Bottom, vertical from Left/Right - repopulated
    /// (rather than just filtered) whenever orientation changes, always resetting to the first
    /// option of the new pair since the old reference has no equivalent on the other axis.</summary>
    private void PopulateReferenceOptions()
    {
        var wasPopulating = _isPopulating;
        _isPopulating = true;

        _referenceOptions.Clear();
        if (_horizontalRadio.Checked)
        {
            _referenceOptions.Add(Reference.Top);
            _referenceOptions.Add(Reference.Bottom);
        }
        else
        {
            _referenceOptions.Add(Reference.Left);
            _referenceOptions.Add(Reference.Right);
        }
        _referenceCombo.SetItems(_referenceOptions.Select(r => r.ToString()).ToList(), 0);
        _lastReference = _referenceOptions[0];

        _isPopulating = wasPopulating;
    }

    /// <summary>Keeps the same physical line position when the user flips e.g. "From Top" to "From
    /// Bottom" - the displayed number changes to match, rather than now meaning a completely
    /// different position under the new reference.</summary>
    private void OnReferenceChanged()
    {
        if (_isPopulating)
            return;

        var absolute = ToAbsolute(_lastReference, _positionInput.Value);
        _lastReference = _referenceOptions[_referenceCombo.SelectedIndex];
        SetDisplayedPosition(absolute);
    }

    private void SetDisplayedPosition(int absolutePosition)
    {
        _isPopulating = true;
        _positionInput.Value = Math.Clamp(ToDisplayed(_lastReference, absolutePosition), _positionInput.Minimum, _positionInput.Maximum);
        _isPopulating = false;
    }

    /// <summary>The working area (not the raw monitor bounds) is the anchor for every edge -
    /// excludes the taskbar (or any other reserved space) regardless of which side it's docked to,
    /// so "50 from the bottom" always means 50px above the usable desktop's own bottom edge, never
    /// underneath/behind the taskbar.</summary>
    private Rectangle CurrentWorkingArea => _screenOptions[_screenCombo.SelectedIndex].WorkingArea;

    private int ToAbsolute(Reference reference, int displayed)
    {
        var area = CurrentWorkingArea;
        return reference switch
        {
            Reference.Top => area.Top + displayed,
            Reference.Bottom => area.Bottom - displayed,
            Reference.Left => area.Left + displayed,
            Reference.Right => area.Right - displayed,
            _ => displayed,
        };
    }

    private int ToDisplayed(Reference reference, int absolute)
    {
        var area = CurrentWorkingArea;
        return reference switch
        {
            Reference.Top => absolute - area.Top,
            Reference.Bottom => area.Bottom - absolute,
            Reference.Left => absolute - area.Left,
            Reference.Right => area.Right - absolute,
            _ => absolute,
        };
    }

    private void CommitClicked()
    {
        var position = ToAbsolute(_lastReference, _positionInput.Value);
        var monitorBounds = _screenOptions[_screenCombo.SelectedIndex].Bounds;
        var orientation = _horizontalRadio.Checked ? SnapOrientation.Horizontal : SnapOrientation.Vertical;
        if (_selectedId is { } id)
            UpdateRequested?.Invoke(id, orientation, position, monitorBounds);
        else
            AddRequested?.Invoke(orientation, position, monitorBounds);
    }

    private void DeleteClicked()
    {
        if (_selectedId is { } id)
            DeleteRequested?.Invoke(id);
    }

    // Covers both the explicit Close button (which calls Close() directly) and the tool window's
    // own native caption close button, so either one exits edit mode the same way.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        CloseRequested?.Invoke();
    }

    private readonly record struct ScreenOption(int Index, Rectangle Bounds, Rectangle WorkingArea)
    {
        public override string ToString() => $"Screen {Index + 1} ({Bounds.Width}x{Bounds.Height})";
    }

    /// <summary>A closed-state combo box face (current selection + dropdown arrow) that opens this
    /// app's own DropdownMenu instead of a native combo popup - see this file's own top-of-class
    /// comment for why the native popup couldn't just be recolored. Only ever holds plain strings;
    /// SnapLinePanel keeps its own parallel list (_screenOptions/_referenceOptions) mapping a
    /// SelectedIndex back to the real value, the same relationship ComboBox.Items/SelectedItem used
    /// to have.</summary>
    private sealed class ComboButton : Control
    {
        private const int ArrowSize = 8;
        private List<string> _items = new();

        public int SelectedIndex { get; private set; } = -1;

        /// <summary>Fired both for a real user pick (OpenDropdown's ItemClicked) and for a
        /// programmatic SetSelectedIndex call (SelectScreen) - same as ComboBox.SelectedIndexChanged,
        /// which fires either way too; SnapLinePanel already guards the one handler that cares
        /// (OnReferenceChanged) with _isPopulating for the bulk-repopulate case.</summary>
        public event Action<int>? SelectedIndexChanged;

        public ComboButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = AppTheme.Field;
            ForeColor = AppTheme.Text;
            Cursor = Cursors.Hand;
        }

        /// <summary>A full repopulate (new item list is a different set of choices, not just a new
        /// pick among the existing ones) - deliberately silent, unlike SetSelectedIndex, so callers
        /// that rebuild the whole list (PopulateScreens/PopulateReferenceOptions) don't have to
        /// re-guard against their own repopulation the way _isPopulating exists for.</summary>
        public void SetItems(IReadOnlyList<string> items, int selectedIndex)
        {
            _items = items.ToList();
            SelectedIndex = selectedIndex;
            Invalidate();
        }

        public void SetSelectedIndex(int index)
        {
            SelectedIndex = index;
            Invalidate();
            SelectedIndexChanged?.Invoke(index);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new SolidBrush(AppTheme.Field))
                e.Graphics.FillRectangle(background, ClientRectangle);
            using (var borderPen = new Pen(AppTheme.Border))
                e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

            var text = SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex] : string.Empty;
            var textRect = new Rectangle(8, 0, Math.Max(0, Width - 8 - ArrowSize - 16), Height);
            TextRenderer.DrawText(e.Graphics, text, Font, textRect, AppTheme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            var cx = Width - 8 - ArrowSize / 2;
            var cy = Height / 2;
            using var arrowBrush = new SolidBrush(Color.FromArgb(255, 190, 190, 196));
            e.Graphics.FillPolygon(arrowBrush, new[]
            {
                new Point(cx - ArrowSize / 2, cy - 2),
                new Point(cx + ArrowSize / 2, cy - 2),
                new Point(cx, cy + 3),
            });
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
                OpenDropdown();
        }

        private void OpenDropdown()
        {
            if (_items.Count == 0)
                return;

            var rows = new List<DropdownMenu.Row>();
            for (var i = 0; i < _items.Count; i++)
            {
                var index = i; // captured per-row, not the loop variable itself
                rows.Add(new DropdownMenu.Row(index, _items[index], HasCheckbox: true, IsChecked: () => index == SelectedIndex));
            }

            var menu = new DropdownMenu(rows, RectangleToScreen(ClientRectangle), preferLeft: false, Font,
                () => AppTheme.Field, () => AppTheme.Hover, () => AppTheme.Accent, () => AppTheme.Border, () => AppTheme.Field);
            menu.ItemClicked += id =>
            {
                SetSelectedIndex(id);
                menu.Close();
            };
            menu.Show(FindForm());
        }
    }

    /// <summary>A numeric field with the same "- [value] + " shape as DropdownMenu's own IsStepper
    /// rows, but with a real editable TextBox in the middle instead of read-only text - unlike a menu
    /// row (which only ever needs click-to-step), this field's value also needs to be typed directly.
    /// The two step buttons are painted straight onto this control (not child controls of their own),
    /// same reasoning as DropdownMenu drawing its own stepper - they're too small/plain to need a
    /// whole separate Control each.</summary>
    private sealed class DarkNumericField : Control
    {
        private readonly TextBox _textBox;
        private Rectangle _minusRect;
        private Rectangle _plusRect;

        public int Minimum { get; set; }
        public int Maximum { get; set; } = int.MaxValue;

        public int Value
        {
            get => int.TryParse(_textBox.Text, out var value) ? Math.Clamp(value, Minimum, Maximum) : Minimum;
            set
            {
                var clamped = Math.Clamp(value, Minimum, Maximum).ToString();
                if (_textBox.Text != clamped)
                    _textBox.Text = clamped;
            }
        }

        public DarkNumericField()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = AppTheme.Field;
            ForeColor = AppTheme.Text;

            _textBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = AppTheme.Field,
                ForeColor = AppTheme.Text,
                Text = "0",
                TextAlign = HorizontalAlignment.Center,
            };
            // Digits only - this field only ever represents a non-negative pixel distance (see
            // SnapLinePanel's own Minimum comment), so there's no sign/decimal to allow through.
            _textBox.KeyPress += (_, e) =>
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
                    e.Handled = true;
            };
            _textBox.Leave += (_, _) => Value = Value; // re-clamp and reformat once typing is done
            _textBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter)
                    return;
                Value = Value;
                e.Handled = true;
                e.SuppressKeyPress = true; // swallow the beep a plain-text Enter would otherwise trigger
            };
            Controls.Add(_textBox);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            _textBox.Font = Font;
            LayoutChildren();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        private void LayoutChildren()
        {
            var buttonSize = Math.Max(0, Height - 4);
            _minusRect = new Rectangle(2, 2, buttonSize, buttonSize);
            _plusRect = new Rectangle(Math.Max(_minusRect.Right, Width - buttonSize - 2), 2, buttonSize, buttonSize);

            var textLeft = _minusRect.Right + 4;
            var textWidth = Math.Max(10, _plusRect.Left - textLeft - 4);
            _textBox.Width = textWidth;
            _textBox.Location = new Point(textLeft, (Height - _textBox.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var background = new SolidBrush(AppTheme.Field))
                e.Graphics.FillRectangle(background, ClientRectangle);
            using (var borderPen = new Pen(AppTheme.Border))
                e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

            DrawSpinnerButton(e.Graphics, _minusRect, isPlus: false);
            DrawSpinnerButton(e.Graphics, _plusRect, isPlus: true);
        }

        /// <summary>Same outlined-square-plus-glyph construction as DropdownMenu.DrawStepperButton -
        /// kept as a copy rather than a shared helper since the two live in otherwise-unrelated
        /// classes with no natural common base to hang a shared method off of.</summary>
        private void DrawSpinnerButton(Graphics g, Rectangle rect, bool isPlus)
        {
            using (var pen = new Pen(AppTheme.Border))
                g.DrawRectangle(pen, rect);

            var cx = rect.X + rect.Width / 2f;
            var cy = rect.Y + rect.Height / 2f;
            const float halfLength = 4f;
            using var glyphPen = new Pen(AppTheme.Accent, 1.5f);
            g.DrawLine(glyphPen, cx - halfLength, cy, cx + halfLength, cy);
            if (isPlus)
                g.DrawLine(glyphPen, cx, cy - halfLength, cx, cy + halfLength);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;

            if (_minusRect.Contains(e.Location))
                Step(-1);
            else if (_plusRect.Contains(e.Location))
                Step(1);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Step(Math.Sign(e.Delta));
        }

        private void Step(int direction)
        {
            Value += direction;
            Invalidate();
        }
    }
}
