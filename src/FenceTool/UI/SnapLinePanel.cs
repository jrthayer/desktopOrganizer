using FenceTool.Fences;
using FenceTool.Snapping;

namespace FenceTool.UI;

/// <summary>
/// Small corner panel shown alongside SnapLineEditOverlay during "snap line edit mode" - lets a new
/// custom line be added by typed orientation+position+screen, or an existing one (selected by
/// clicking it directly on the overlay - see SnapLineEditOverlay.LineSelected) be repositioned,
/// re-homed to a different monitor, or removed. Plain stock WinForms controls are enough here;
/// unlike FenceForm this doesn't need any layered/rounded custom painting.
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
    private readonly ComboBox _screenCombo;
    private readonly ComboBox _referenceCombo;
    private readonly NumericUpDown _positionInput;
    private readonly Button _addUpdateButton;
    private readonly Button _deleteButton;

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
        ClientSize = new Size(220, 288);
        MaximizeBox = false;
        MinimizeBox = false;

        _horizontalRadio = new RadioButton { Text = "Horizontal", Checked = true, Location = new Point(12, 12), AutoSize = true };
        _verticalRadio = new RadioButton { Text = "Vertical", Location = new Point(12, 36), AutoSize = true };
        _horizontalRadio.CheckedChanged += (_, _) => PopulateReferenceOptions();
        _verticalRadio.CheckedChanged += (_, _) => PopulateReferenceOptions();

        var screenLabel = new Label { Text = "Screen", Location = new Point(12, 64), AutoSize = true };
        _screenCombo = new ComboBox { Location = new Point(12, 84), Width = 196, DropDownStyle = ComboBoxStyle.DropDownList };
        PopulateScreens();

        var positionLabel = new Label { Text = "Position", Location = new Point(12, 112), AutoSize = true };
        _referenceCombo = new ComboBox { Location = new Point(12, 132), Width = 196, DropDownStyle = ComboBoxStyle.DropDownList };
        _referenceCombo.SelectedIndexChanged += (_, _) => OnReferenceChanged();
        PopulateReferenceOptions();

        _positionInput = new NumericUpDown
        {
            Location = new Point(12, 160),
            Width = 196,
            // Distances are always measured inward from an edge (never negative) - the working
            // area itself is what bounds how large a sensible value is, not this field.
            Minimum = 0,
            Maximum = 32000,
        };

        // Lets a line that's currently selected/being edited be abandoned in favor of starting a
        // fresh one, without deleting it - the only other way back to "Add" mode was deleting
        // whatever was selected first.
        var newButton = new Button { Text = "New Line", Location = new Point(12, 192), Width = 196 };
        newButton.Click += (_, _) =>
        {
            ClearSelection();
            NewLineRequested?.Invoke();
        };

        _addUpdateButton = new Button { Text = "Add", Location = new Point(12, 224), Width = 90 };
        _addUpdateButton.Click += (_, _) => CommitClicked();

        _deleteButton = new Button { Text = "Delete", Location = new Point(118, 224), Width = 90, Enabled = false };
        _deleteButton.Click += (_, _) => DeleteClicked();

        var closeButton = new Button { Text = "Close", Location = new Point(12, 256), Width = 196 };
        closeButton.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
        {
            _horizontalRadio, _verticalRadio, screenLabel, _screenCombo, positionLabel, _referenceCombo,
            _positionInput, newButton, _addUpdateButton, _deleteButton, closeButton,
        });
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
        _screenCombo.SelectedIndex = Math.Max(primaryIndex, 0);
        _positionInput.Value = 0;
    }

    private void PopulateScreens()
    {
        _screenCombo.Items.Clear();
        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
            _screenCombo.Items.Add(new ScreenOption(i, screens[i].Bounds, screens[i].WorkingArea));

        var primaryIndex = Array.FindIndex(screens, s => s.Primary);
        _screenCombo.SelectedIndex = Math.Max(primaryIndex, 0);
    }

    /// <summary>Selects whichever combo entry's bounds match - a legacy/unscoped line (zero-size
    /// MonitorBounds) or a monitor that's no longer connected leaves the combo showing whatever it
    /// already had rather than guessing.</summary>
    private void SelectScreen(Rectangle bounds)
    {
        for (var i = 0; i < _screenCombo.Items.Count; i++)
        {
            if (((ScreenOption)_screenCombo.Items[i]!).Bounds == bounds)
            {
                _screenCombo.SelectedIndex = i;
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

        _referenceCombo.Items.Clear();
        if (_horizontalRadio.Checked)
        {
            _referenceCombo.Items.Add(Reference.Top);
            _referenceCombo.Items.Add(Reference.Bottom);
        }
        else
        {
            _referenceCombo.Items.Add(Reference.Left);
            _referenceCombo.Items.Add(Reference.Right);
        }
        _referenceCombo.SelectedIndex = 0;
        _lastReference = (Reference)_referenceCombo.SelectedItem!;

        _isPopulating = wasPopulating;
    }

    /// <summary>Keeps the same physical line position when the user flips e.g. "From Top" to "From
    /// Bottom" - the displayed number changes to match, rather than now meaning a completely
    /// different position under the new reference.</summary>
    private void OnReferenceChanged()
    {
        if (_isPopulating)
            return;

        var absolute = ToAbsolute(_lastReference, (int)_positionInput.Value);
        _lastReference = (Reference)_referenceCombo.SelectedItem!;
        SetDisplayedPosition(absolute);
    }

    private void SetDisplayedPosition(int absolutePosition)
    {
        _isPopulating = true;
        _positionInput.Value = Math.Clamp(ToDisplayed(_lastReference, absolutePosition), (int)_positionInput.Minimum, (int)_positionInput.Maximum);
        _isPopulating = false;
    }

    /// <summary>The working area (not the raw monitor bounds) is the anchor for every edge -
    /// excludes the taskbar (or any other reserved space) regardless of which side it's docked to,
    /// so "50 from the bottom" always means 50px above the usable desktop's own bottom edge, never
    /// underneath/behind the taskbar.</summary>
    private Rectangle CurrentWorkingArea => ((ScreenOption)_screenCombo.SelectedItem!).WorkingArea;

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
        var position = ToAbsolute(_lastReference, (int)_positionInput.Value);
        var monitorBounds = ((ScreenOption)_screenCombo.SelectedItem!).Bounds;
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
}
