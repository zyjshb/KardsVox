using System.Drawing.Drawing2D;

namespace KardsVox.UI;

internal sealed class ToggleSwitch : Control
{
    private bool isChecked;
    private readonly Font switchFont = new("Bahnschrift", 8.5f, FontStyle.Bold);
    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ToggleSwitch()
    {
        Size = new Size(82, 34);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle frame = new(1, 2, Width - 3, Height - 5);
        Point[] shape =
        [
            new(frame.Left + 5, frame.Top), new(frame.Right - 4, frame.Top), new(frame.Right, frame.Top + 4),
            new(frame.Right, frame.Bottom - 4), new(frame.Right - 4, frame.Bottom), new(frame.Left + 5, frame.Bottom),
            new(frame.Left, frame.Bottom - 5), new(frame.Left, frame.Top + 5)
        ];
        using var path = new GraphicsPath();
        path.AddPolygon(shape);
        using var fill = new SolidBrush(!Enabled ? Color.FromArgb(128, 124, 108) :
            Checked ? Color.FromArgb(75, 86, 62) : Color.FromArgb(88, 86, 74));
        using var border = new Pen(Color.FromArgb(45, 43, 36), 1.5f);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        int knobX = Checked ? Width - 29 : 6;
        var knob = new Rectangle(knobX, 6, 23, 22);
        using var paper = new SolidBrush(Color.FromArgb(224, 217, 190));
        using var knobBorder = new Pen(Color.FromArgb(52, 50, 42), 1f);
        e.Graphics.FillRectangle(paper, knob);
        e.Graphics.DrawRectangle(knobBorder, knob);

        string text = Checked ? "ON" : "OFF";
        var textArea = Checked ? new Rectangle(7, 5, 40, 23) : new Rectangle(34, 5, 42, 23);
        TextRenderer.DrawText(e.Graphics, text, switchFont, textArea,
            Enabled ? Color.FromArgb(237, 231, 209) : Color.FromArgb(206, 199, 177),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) switchFont.Dispose();
        base.Dispose(disposing);
    }
}
