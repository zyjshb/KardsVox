using System.Drawing.Drawing2D;
using KardsVox.Core;

namespace KardsVox.UI;

internal sealed class SubtitleOverlay : Form
{
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    private static readonly string[] CommanderIntroSegments =
    [
        "同胞们，用各位的勇气与自信，为敌人带去恐惧吧，",
        "坚持战斗，直至最后一人！",
        "为敌人带去恐惧吧，屹立于此的勇士们啊！",
        "粉碎敌人的诡计阴谋吧！"
    ];

    private readonly Font commanderTextFont = new("Microsoft YaHei UI", 14.5f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font soldierTextFont = new("Microsoft YaHei UI", 15.5f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font nameFont = new("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly System.Windows.Forms.Timer timer;
    private string[] segments = [];
    private string currentText = string.Empty;
    private DateTimeOffset shownAt;
    private DateTimeOffset hideAt;
    private TimeSpan displayDuration;
    private int segmentIndex = -1;
    private bool commander;
    private string currentSpeaker = "前线部队";

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    public SubtitleOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        TransparencyKey = Color.Black;
        DoubleBuffered = true;
        Width = 520;
        Height = 98;

        timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) => UpdateCue();
    }

    public void ShowCue(MemoryMatchMonitor.SubtitleCue cue)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowCue(cue));
            return;
        }

        commander = cue.Commander;
        currentSpeaker = string.IsNullOrWhiteSpace(cue.Speaker)
            ? (cue.Commander ? "指挥官" : "前线部队") : cue.Speaker;
        segments = cue.Sequenced ? CommanderIntroSegments : [cue.Text];
        shownAt = DateTimeOffset.UtcNow;
        displayDuration = cue.Duration + TimeSpan.FromMilliseconds(cue.Sequenced ? 750 : 400);
        hideAt = shownAt + displayDuration;
        segmentIndex = -1;
        UpdateCue();
        if (!Visible) Show();
        timer.Start();
    }

    public void HideCue()
    {
        if (InvokeRequired)
        {
            BeginInvoke(HideCue);
            return;
        }
        timer.Stop();
        Hide();
    }

    private void UpdateCue()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now >= hideAt)
        {
            HideCue();
            return;
        }

        int next = segments.Length > 1 ? SegmentAt(now) : 0;
        if (next != segmentIndex)
        {
            segmentIndex = next;
            currentText = segments[next];
            PositionOverGame();
            Invalidate();
        }
        else
        {
            PositionOverGame();
        }
    }

    private int SegmentAt(DateTimeOffset now)
    {
        double progress = Math.Clamp((now - shownAt).TotalMilliseconds / displayDuration.TotalMilliseconds, 0, .999);
        int totalCharacters = segments.Sum(text => text.Length);
        double target = progress * totalCharacters;
        int cumulative = 0;
        for (int index = 0; index < segments.Length; index++)
        {
            cumulative += segments[index].Length;
            if (target < cumulative) return index;
        }
        return segments.Length - 1;
    }

    private void PositionOverGame()
    {
        Rectangle bounds = GameWindowDetector.TryGetForegroundClient(out var game)
            ? game : Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Font textFont = commander ? commanderTextFont : soldierTextFont;
        Size oneLine = TextRenderer.MeasureText(currentText, textFont, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        int maximumWidth = Math.Max(510, (int)(bounds.Width * .46));
        int desiredWidth = (int)Math.Ceiling(oneLine.Width * 1.28) + 78;
        int width = Math.Clamp(desiredWidth, commander ? 510 : 390, maximumWidth);
        int availableTextWidth = width - 70;
        Size wrapped = TextRenderer.MeasureText(currentText, textFont,
            new Size(availableTextWidth, int.MaxValue), TextFormatFlags.WordBreak |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        int height = Math.Clamp(wrapped.Height + 66, 98, 132);

        int left = bounds.Left + Math.Max(26, (int)(bounds.Width * .04));
        int top = bounds.Top + (int)(bounds.Height * .70);
        SetBounds(left, top, width, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        int bodyBottom = Height - 18;
        using var panelPath = PaperPanelPath(Width, bodyBottom, Height);
        using var shadowPath = Offset(panelPath, 3, 3);
        using var shadow = new SolidBrush(Color.FromArgb(35, 24, 22, 18));
        using var paper = new SolidBrush(Color.FromArgb(221, 214, 187));
        using var border = new Pen(Color.FromArgb(58, 55, 45), 1.8f) { LineJoin = LineJoin.Bevel };
        e.Graphics.FillPath(shadow, shadowPath);
        e.Graphics.FillPath(paper, panelPath);
        e.Graphics.DrawPath(border, panelPath);

        DrawPaperTexture(e.Graphics, Width, bodyBottom);

        string speaker = currentSpeaker;
        int plaqueWidth = Math.Clamp(TextRenderer.MeasureText(speaker, nameFont,
            Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 24, 76, 112);
        var plaque = new Rectangle(30, 10, plaqueWidth, 23);
        using var plaqueBrush = new SolidBrush(Color.FromArgb(55, 54, 47));
        using var plaqueBorder = new Pen(Color.FromArgb(32, 31, 27), 1f);
        e.Graphics.FillRectangle(plaqueBrush, plaque);
        e.Graphics.DrawRectangle(plaqueBorder, plaque);
        TextRenderer.DrawText(e.Graphics, speaker, nameFont, plaque, Color.FromArgb(237, 231, 208),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        using var japanRed = new SolidBrush(Color.FromArgb(169, 58, 43));
        e.Graphics.FillEllipse(japanRed, plaque.Right + 11, 16, 11, 11);
        using var rule = new Pen(Color.FromArgb(122, 111, 82), 1f);
        e.Graphics.DrawLine(rule, plaque.Right + 32, 21, Width - 28, 21);

        Font textFont = commander ? commanderTextFont : soldierTextFont;
        var textRect = new Rectangle(31, 40, Width - 60, bodyBottom - 46);
        TextRenderer.DrawText(e.Graphics, currentText, textFont, textRect, Color.FromArgb(39, 38, 33),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        using var stamp = new Pen(Color.FromArgb(86, 91, 82, 62), 1f);
        e.Graphics.DrawLine(stamp, Width - 45, bodyBottom - 10, Width - 27, bodyBottom - 10);
        e.Graphics.DrawLine(stamp, Width - 41, bodyBottom - 7, Width - 27, bodyBottom - 7);
    }

    private static GraphicsPath PaperPanelPath(int width, int bodyBottom, int height)
    {
        var path = new GraphicsPath();
        Point[] points =
        [
            new(24, 1), new(width - 13, 1), new(width - 2, 12),
            new(width - 2, bodyBottom - 10), new(width - 12, bodyBottom),
            new(70, bodyBottom), new(24, height - 2), new(36, bodyBottom),
            new(13, bodyBottom), new(13, 12)
        ];
        path.AddPolygon(points);
        return path;
    }

    private static void DrawPaperTexture(Graphics graphics, int width, int bodyBottom)
    {
        using var fleck = new SolidBrush(Color.FromArgb(22, 76, 70, 54));
        for (int index = 0; index < 34; index++)
        {
            int x = 25 + (index * 97 + 31) % Math.Max(1, width - 55);
            int y = 7 + (index * 43 + 19) % Math.Max(1, bodyBottom - 18);
            graphics.FillRectangle(fleck, x, y, index % 4 == 0 ? 2 : 1, 1);
        }
        using var scratch = new Pen(Color.FromArgb(18, 69, 65, 51), 1f);
        graphics.DrawLine(scratch, width * .56f, 7, width * .66f, 8);
        graphics.DrawLine(scratch, width * .18f, bodyBottom - 9, width * .28f, bodyBottom - 8);
    }

    private static GraphicsPath Offset(GraphicsPath source, int x, int y)
    {
        var path = (GraphicsPath)source.Clone();
        using var matrix = new Matrix();
        matrix.Translate(x, y);
        path.Transform(matrix);
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            commanderTextFont.Dispose();
            soldierTextFont.Dispose();
            nameFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
