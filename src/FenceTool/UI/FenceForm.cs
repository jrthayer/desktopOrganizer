using System.Drawing.Drawing2D;
using FenceTool.Fences;

namespace FenceTool.UI;

public sealed class FenceForm : Form
{
    internal const int TitleBarHeight = 26;
    private const int ResizeMargin = 6;
    private const int CornerRadius = 10;

    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_NCHITTEST = 0x0084;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private readonly FenceManager _manager;
    private readonly FenceModel _model;
    private TextBox? _renameBox;

    public Guid FenceId => _model.Id;

    public FenceForm(FenceModel model, FenceManager manager)
    {
        _model = model;
        _manager = manager;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        MinimumSize = new Size(120, 80);
        Bounds = model.Bounds;
        Opacity = 0.85;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9f);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Rename", null, (_, _) => BeginRename());
        menu.Items.Add("Arrange Icons Now", null, (_, _) => _manager.ArrangeFence(FenceId));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete Fence", null, (_, _) => ConfirmDelete());
        ContextMenuStrip = menu;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRoundedRegion();
        if (_renameBox is not null)
            _renameBox.Width = Width - 12;
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        _manager.NotifyBoundsChanged(FenceId, Bounds);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var body = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var bodyFill = new SolidBrush(Color.FromArgb(255, 32, 32, 36));
        g.FillPath(bodyFill, body);

        using var titleFill = new SolidBrush(Color.FromArgb(255, 20, 20, 24));
        using var titlePath = RoundedRectTop(new Rectangle(0, 0, Width - 1, TitleBarHeight), CornerRadius);
        g.FillPath(titleFill, titlePath);

        using var borderPen = new Pen(Color.FromArgb(255, 70, 70, 78));
        g.DrawPath(borderPen, body);

        if (_renameBox is null)
        {
            TextRenderer.DrawText(g, _model.Name, Font, new Rectangle(8, 0, Width - 16, TitleBarHeight),
                Color.WhiteSmoke, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Y <= TitleBarHeight)
            BeginRename();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            long lParam = m.LParam.ToInt64();
            short x = (short)(lParam & 0xFFFF);
            short y = (short)((lParam >> 16) & 0xFFFF);
            var pt = PointToClient(new Point(x, y));
            m.Result = (IntPtr)HitTest(pt);
            return;
        }

        base.WndProc(ref m);
    }

    private int HitTest(Point pt)
    {
        bool left = pt.X <= ResizeMargin;
        bool right = pt.X >= Width - ResizeMargin;
        bool top = pt.Y <= ResizeMargin;
        bool bottom = pt.Y >= Height - ResizeMargin;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        if (pt.Y <= TitleBarHeight) return HTCAPTION;
        return HTCLIENT;
    }

    private void ApplyRoundedRegion()
    {
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), CornerRadius);
        Region?.Dispose();
        Region = new Region(path);
    }

    private void BeginRename()
    {
        if (_renameBox is not null)
            return;

        _renameBox = new TextBox
        {
            Text = _model.Name,
            Location = new Point(6, 3),
            Width = Width - 12,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _renameBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                CommitRename();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                CancelRename();
            }
        };
        _renameBox.LostFocus += (_, _) => CommitRename();

        Controls.Add(_renameBox);
        _renameBox.BringToFront();
        _renameBox.Focus();
        _renameBox.SelectAll();
    }

    private void CommitRename()
    {
        if (_renameBox is null)
            return;

        var newName = _renameBox.Text.Trim();
        Controls.Remove(_renameBox);
        _renameBox.Dispose();
        _renameBox = null;

        if (!string.IsNullOrEmpty(newName) && newName != _model.Name)
            _manager.NotifyRenamed(FenceId, newName);

        Invalidate();
    }

    private void CancelRename()
    {
        if (_renameBox is null)
            return;

        Controls.Remove(_renameBox);
        _renameBox.Dispose();
        _renameBox = null;
        Invalidate();
    }

    private void ConfirmDelete()
    {
        var result = MessageBox.Show(this,
            $"Delete fence \"{_model.Name}\"? Icons inside it will remain on the desktop.",
            "Delete Fence", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
            _manager.DeleteFence(FenceId);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath RoundedRectTop(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
        path.CloseFigure();
        return path;
    }
}
