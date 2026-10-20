using System.Runtime.InteropServices;
using KardsVox.Core;

namespace KardsVox.UI;

internal sealed class TrainerForm : Form
{
    private const int WmNclButtonDown = 0xA1;
    private const int HtCaption = 0x2;
    private static readonly Color Ink = Color.FromArgb(43, 42, 36);
    private static readonly Color Paper = Color.FromArgb(221, 214, 187);
    private static readonly Color DarkMetal = Color.FromArgb(35, 37, 34);
    private static readonly Color Accent = Color.FromArgb(169, 58, 43);

    private readonly MemoryMatchMonitor monitor;
    private readonly SubtitleOverlay overlay;
    private readonly Label statusLabel;
    private readonly Label statusDot;
    private readonly Label matchTitle;
    private readonly Label matchDescription;
    private readonly Label matchBandLabel;
    private readonly Label voiceHotkeyLabel;
    private readonly Label voiceHintLabel;
    private readonly ToggleSwitch voiceToggle;
    private readonly ToggleSwitch subtitleToggle;
    private readonly Button testButton;
    private readonly Dictionary<string, Panel> packCards = new(StringComparer.Ordinal);
    private readonly System.Windows.Forms.Timer statusTimer;
    private readonly Icon appIcon;
    private bool updatingControls;
    private bool wasInMatch;

    public TrainerForm(MemoryMatchMonitor monitor, SubtitleOverlay overlay)
    {
        this.monitor = monitor;
        this.overlay = overlay;
        appIcon = CreateAppIcon();
        Icon = appIcon;
        Text = "KARDS VOX";
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(920, 690);
        MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(51, 52, 46);
        ForeColor = Color.FromArgb(239, 234, 215);
        Font = new Font("Microsoft YaHei UI", 10f);

        var titleBar = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = DarkMetal };
        titleBar.Paint += (_, e) =>
        {
            using var line = new Pen(Color.FromArgb(122, 108, 76), 1f);
            e.Graphics.DrawLine(line, 0, titleBar.Height - 1, titleBar.Width, titleBar.Height - 1);
        };
        titleBar.MouseDown += DragWindow;
        Controls.Add(titleBar);
        var logo = new Label
        {
            Text = "KARDS", Font = new Font("Bahnschrift SemiCondensed", 21f, FontStyle.Bold),
            ForeColor = Color.FromArgb(235, 228, 202), AutoSize = true, Location = new Point(27, 15)
        };
        logo.MouseDown += DragWindow;
        titleBar.Controls.Add(logo);
        var vox = new Label
        {
            Text = "//  VOX", Font = new Font("Bahnschrift SemiCondensed", 16f, FontStyle.Bold),
            ForeColor = Accent, AutoSize = true, Location = new Point(158, 21)
        };
        vox.MouseDown += DragWindow;
        titleBar.Controls.Add(vox);
        var tagline = new Label
        {
            Text = "NATION VOICE PACKS", Font = new Font("Bahnschrift", 10f),
            ForeColor = Color.FromArgb(166, 160, 143), AutoSize = true, Location = new Point(266, 25)
        };
        tagline.MouseDown += DragWindow;
        titleBar.Controls.Add(tagline);
        var close = HeaderButton("×", 864);
        close.Click += (_, _) => Close();
        titleBar.Controls.Add(close);
        var minimize = HeaderButton("—", 810);
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        titleBar.Controls.Add(minimize);

        var heading = PaperPanel(new Point(26, 78), new Size(868, 100));
        Controls.Add(heading);
        heading.Controls.Add(new Label
        {
            Text = "国家语音卡包", Font = new Font("Microsoft YaHei UI", 22f, FontStyle.Bold),
            ForeColor = Ink, AutoSize = true, Location = new Point(34, 14)
        });
        heading.Controls.Add(new Label
        {
            Text = "对局自动识别国家；点击卡包选择 F6 预览。",
            Font = new Font("Microsoft YaHei UI", 10f), ForeColor = Color.FromArgb(90, 85, 72),
            AutoSize = true, Location = new Point(35, 65)
        });
        statusDot = new Label
        {
            Text = "●", AutoSize = true, Location = new Point(627, 34), Font = new Font("Segoe UI", 11f)
        };
        statusLabel = new Label
        {
            AutoSize = false, Location = new Point(652, 28), Size = new Size(184, 34),
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold), ForeColor = Ink
        };
        heading.Controls.Add(statusDot);
        heading.Controls.Add(statusLabel);

        var nationBand = new Panel { Location = new Point(26, 192), Size = new Size(868, 40), BackColor = DarkMetal };
        nationBand.Controls.Add(new Label
        {
            Text = "01 / NATION PACKS", Font = new Font("Bahnschrift SemiCondensed", 15f, FontStyle.Bold),
            ForeColor = Color.FromArgb(238, 232, 210), AutoSize = true, Location = new Point(27, 5)
        });
        Controls.Add(nationBand);
        var packList = new FlowLayoutPanel
        {
            Location = new Point(26, 232), Size = new Size(868, 154), BackColor = Color.FromArgb(44, 45, 40),
            AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4)
        };
        foreach (VoicePackDefinition pack in VoicePackRegistry.All)
        {
            Panel card = MakePackCard(pack);
            packCards.Add(pack.Id, card);
            packList.Controls.Add(card);
        }
        Controls.Add(packList);

        var matchBand = new Panel { Location = new Point(26, 400), Size = new Size(868, 39), BackColor = DarkMetal };
        matchBandLabel = new Label
        {
            Font = new Font("Bahnschrift SemiCondensed", 15f, FontStyle.Bold),
            ForeColor = Color.FromArgb(238, 232, 210), AutoSize = true, Location = new Point(27, 4)
        };
        matchBand.Controls.Add(matchBandLabel);
        Controls.Add(matchBand);
        var matchPanel = PaperPanel(new Point(26, 439), new Size(868, 102));
        Controls.Add(matchPanel);
        matchTitle = new Label
        {
            Font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold),
            ForeColor = Ink, AutoSize = true, Location = new Point(33, 11)
        };
        matchPanel.Controls.Add(matchTitle);
        matchDescription = new Label
        {
            Font = new Font("Microsoft YaHei UI", 10f), ForeColor = Color.FromArgb(92, 87, 73),
            AutoSize = true, Location = new Point(34, 66)
        };
        matchPanel.Controls.Add(matchDescription);
        voiceHotkeyLabel = new Label
        {
            Text = "F9", Font = new Font("Bahnschrift SemiCondensed", 11f, FontStyle.Bold),
            ForeColor = Accent, AutoSize = true, Location = new Point(689, 30)
        };
        matchPanel.Controls.Add(voiceHotkeyLabel);
        voiceToggle = new ToggleSwitch { Location = new Point(744, 18) };
        voiceToggle.CheckedChanged += (_, _) =>
        {
            if (!updatingControls) monitor.SetAutoEnabled(voiceToggle.Checked);
        };
        matchPanel.Controls.Add(voiceToggle);
        voiceHintLabel = new Label
        {
            Text = "日本语音", Font = new Font("Microsoft YaHei UI", 10f),
            ForeColor = Ink, AutoSize = true, Location = new Point(744, 58)
        };
        matchPanel.Controls.Add(voiceHintLabel);

        var subtitlePanel = PaperPanel(new Point(26, 554), new Size(868, 66));
        Controls.Add(subtitlePanel);
        subtitlePanel.Controls.Add(new Label
        {
            Text = "双语字幕", Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),
            ForeColor = Ink, AutoSize = true, Location = new Point(32, 15)
        });
        subtitleToggle = new ToggleSwitch { Location = new Point(744, 16), Checked = monitor.SubtitlesEnabled };
        subtitleToggle.CheckedChanged += (_, _) =>
        {
            monitor.SetSubtitlesEnabled(subtitleToggle.Checked);
            if (!subtitleToggle.Checked) overlay.HideCue();
        };
        subtitlePanel.Controls.Add(subtitleToggle);

        testButton = new Button
        {
            Text = "F6  指挥官预览", FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(211, 203, 174), ForeColor = Ink,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Location = new Point(26, 631), Size = new Size(192, 37), Cursor = Cursors.Hand
        };
        testButton.FlatAppearance.BorderColor = Color.FromArgb(48, 47, 40);
        testButton.FlatAppearance.BorderSize = 2;
        testButton.Click += (_, _) => monitor.Hotkey(VoxHotkey.Play);
        Controls.Add(testButton);

        statusTimer = new System.Windows.Forms.Timer { Interval = 750 };
        statusTimer.Tick += (_, _) => RefreshStatus();
        statusTimer.Start();
        RefreshStatus();
    }

    private Panel MakePackCard(VoicePackDefinition pack)
    {
        int cardWidth = VoicePackRegistry.All.Count >= 4 ? 205 : 276;
        var card = new Panel { Size = new Size(cardWidth, 128), Margin = new Padding(5), BackColor = Paper, Cursor = Cursors.Hand };
        Color marker = pack.Id == VoicePackRegistry.Japan ? Accent :
            pack.Id == VoicePackRegistry.Soviet ? Color.FromArgb(159, 111, 50) :
            pack.Id == VoicePackRegistry.Finland ? Color.FromArgb(62, 88, 114) : Color.FromArgb(67, 89, 80);
        card.Paint += (_, e) =>
        {
            bool selected = monitor.SelectedVoicePack == pack.Id;
            using var border = new Pen(selected ? marker : Color.FromArgb(74, 72, 61), selected ? 3f : 1f);
            e.Graphics.DrawRectangle(border, selected ? 1 : 0, selected ? 1 : 0,
                card.Width - (selected ? 3 : 1), card.Height - (selected ? 3 : 1));
            using var stripe = new SolidBrush(marker);
            e.Graphics.FillRectangle(stripe, 14, 17, 5, 94);
        };
        card.Controls.Add(new Label
        {
            Text = pack.EnglishName, Font = new Font("Bahnschrift SemiCondensed", 10f, FontStyle.Bold),
            ForeColor = marker, AutoSize = true, Location = new Point(34, 12), Cursor = Cursors.Hand
        });
        card.Controls.Add(new Label
        {
            Text = pack.Name, Font = new Font("Microsoft YaHei UI", 22f, FontStyle.Bold),
            ForeColor = Ink, AutoSize = true, Location = new Point(34, 32), Cursor = Cursors.Hand
        });
        string cardDescription = monitor.PackDescription(pack.Id);
        if (VoicePackRegistry.All.Count >= 4)
            cardDescription = cardDescription.Replace("字幕 · ", " · ").Replace(" 条配音", "");
        card.Controls.Add(new Label
        {
            Text = $"{pack.CardCount} 个卡牌资源\n{cardDescription}",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(92, 87, 73), AutoSize = false, Size = new Size(cardWidth - 43, 49),
            Location = new Point(35, 76), Cursor = Cursors.Hand
        });
        void select(object? _, EventArgs __)
        {
            monitor.SetVoicePack(pack.Id);
            RefreshStatus();
        }
        card.Click += select;
        foreach (Control child in card.Controls) child.Click += select;
        return card;
    }

    private void RefreshSelection()
    {
        string currentPackId = monitor.InMatch
            ? monitor.DetectedNation ?? monitor.SelectedVoicePack
            : monitor.SelectedVoicePack;
        bool playable = true;
        updatingControls = true;
        voiceToggle.Checked = monitor.AutoEnabled;
        voiceToggle.Enabled = playable;
        voiceToggle.Visible = playable;
        voiceHotkeyLabel.Visible = playable;
        voiceHintLabel.Text = "自动语音";
        voiceHintLabel.Location = new Point(744, 58);
        subtitleToggle.Enabled = true;
        updatingControls = false;
        testButton.Enabled = true;
        foreach (Panel card in packCards.Values) card.Invalidate();
    }

    private static Panel PaperPanel(Point location, Size size)
    {
        var panel = new Panel { Location = location, Size = size, BackColor = Paper };
        panel.Paint += (_, e) =>
        {
            using var border = new Pen(Color.FromArgb(55, 53, 45), 1.5f);
            e.Graphics.DrawRectangle(border, 0, 0, panel.Width - 1, panel.Height - 1);
            using var fleck = new SolidBrush(Color.FromArgb(18, 71, 67, 52));
            for (int i = 0; i < 22; i++)
            {
                int x = 9 + (i * 79 + 17) % Math.Max(1, panel.Width - 18);
                int y = 7 + (i * 37 + 11) % Math.Max(1, panel.Height - 14);
                e.Graphics.FillRectangle(fleck, x, y, i % 5 == 0 ? 2 : 1, 1);
            }
        };
        return panel;
    }

    private void RefreshStatus()
    {
        bool online = GameWindowDetector.Exists();
        statusDot.ForeColor = monitor.Connected ? Color.FromArgb(69, 121, 66) :
            online ? Color.FromArgb(177, 126, 47) : Accent;
        string? nation = monitor.DetectedNation;
        statusLabel.Text = monitor.InMatch
            ? nation == null ? "识别对局中" : VoicePackRegistry.NationName(nation) + "对局"
            : monitor.Connected ? "已连接 KARDS" : online ? "等待进入对局" : "等待 KARDS";
        string currentPackId = monitor.InMatch ? nation ?? monitor.SelectedVoicePack : monitor.SelectedVoicePack;
        matchBandLabel.Text = monitor.InMatch ? "02 / CURRENT MATCH" : "02 / SELECTED PACK";
        matchTitle.Text = VoicePackRegistry.Contains(currentPackId)
            ? VoicePackRegistry.Get(currentPackId).Name + "国家卡包" : "同盟国卡牌字幕";
        matchDescription.Text = VoicePackRegistry.Contains(currentPackId)
            ? monitor.PackDescription(currentPackId) + " · 按实际出牌国家播报"
            : "按每张牌识别国家 · 使用对应的语音或双语字幕";
        testButton.Text = "F6  " + VoicePackRegistry.Get(monitor.SelectedVoicePack).Name + "指挥官预览";
        RefreshSelection();
        bool currentMatch = monitor.InMatch;
        if (currentMatch && !wasInMatch && WindowState != FormWindowState.Minimized)
            WindowState = FormWindowState.Minimized;
        wasInMatch = currentMatch;
    }

    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(DarkMetal);
        using var accent = new SolidBrush(Accent);
        graphics.FillEllipse(accent, 8, 8, 48, 48);
        using var line = new Pen(Color.FromArgb(235, 228, 202), 6f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };
        graphics.DrawLine(line, 19, 20, 32, 46);
        graphics.DrawLine(line, 32, 46, 47, 18);
        IntPtr handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static Button HeaderButton(string text, int x)
    {
        var button = new Button
        {
            Text = text, Location = new Point(x, 0), Size = new Size(54, 61), FlatStyle = FlatStyle.Flat,
            BackColor = DarkMetal, ForeColor = Color.FromArgb(205, 199, 179),
            Font = new Font("Segoe UI", 16f), Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 61, 54);
        return button;
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, WmNclButtonDown, HtCaption, 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var edge = new Pen(Color.FromArgb(28, 29, 27), 2f);
        e.Graphics.DrawRectangle(edge, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            statusTimer.Dispose();
            appIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
