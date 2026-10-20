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
    private readonly Label packTitle;
    private readonly Label packDescription;
    private readonly ToggleSwitch voiceToggle;
    private readonly ToggleSwitch subtitleToggle;
    private readonly Button testButton;
    private readonly Dictionary<string, Panel> packCards = new(StringComparer.Ordinal);
    private readonly System.Windows.Forms.Timer statusTimer;
    private bool updatingControls;

    public TrainerForm(MemoryMatchMonitor monitor, SubtitleOverlay overlay)
    {
        this.monitor = monitor;
        this.overlay = overlay;
        Text = "KARDS VOX";
        ClientSize = new Size(920, 658);
        MinimumSize = MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.None;
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
        titleBar.Controls.Add(new Label
        {
            Text = "NATION VOICE PACKS  ·  1.13", Font = new Font("Bahnschrift", 9f),
            ForeColor = Color.FromArgb(150, 147, 132), AutoSize = true, Location = new Point(260, 26)
        });
        var close = HeaderButton("×", 864);
        close.Click += (_, _) => Close();
        titleBar.Controls.Add(close);
        var minimize = HeaderButton("—", 810);
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        titleBar.Controls.Add(minimize);

        var heading = PaperPanel(new Point(34, 82), new Size(852, 103));
        Controls.Add(heading);
        heading.Controls.Add(new Label
        {
            Text = "国家语音卡包", Font = new Font("Microsoft YaHei UI", 22f, FontStyle.Bold),
            ForeColor = Ink, AutoSize = true, Location = new Point(27, 15)
        });
        heading.Controls.Add(new Label
        {
            Text = "选择国家，为对局加入对应的语音和中文字幕。", ForeColor = Color.FromArgb(87, 82, 66),
            AutoSize = true, Location = new Point(30, 64)
        });
        statusDot = new Label
        {
            Text = "●", AutoSize = true, Location = new Point(620, 34), Font = new Font("Segoe UI", 11f)
        };
        statusLabel = new Label
        {
            AutoSize = false, Location = new Point(644, 27), Size = new Size(176, 34),
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold), ForeColor = Ink
        };
        heading.Controls.Add(statusDot);
        heading.Controls.Add(statusLabel);

        var nationBand = new Panel { Location = new Point(34, 197), Size = new Size(852, 42), BackColor = DarkMetal };
        nationBand.Controls.Add(new Label
        {
            Text = "01  /  NATION PACKS", Font = new Font("Bahnschrift SemiCondensed", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(238, 232, 210), AutoSize = true, Location = new Point(22, 8)
        });
        Controls.Add(nationBand);
        var packList = new FlowLayoutPanel
        {
            Location = new Point(34, 239), Size = new Size(852, 138), BackColor = Color.FromArgb(44, 45, 40),
            AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4)
        };
        foreach (VoicePackDefinition pack in VoicePackRegistry.All)
        {
            Panel card = MakePackCard(pack);
            packCards.Add(pack.Id, card);
            packList.Controls.Add(card);
        }
        Controls.Add(packList);

        var module = PaperPanel(new Point(34, 389), new Size(852, 139));
        Controls.Add(module);
        var moduleBand = new Panel { Dock = DockStyle.Top, Height = 37, BackColor = DarkMetal };
        moduleBand.Controls.Add(new Label
        {
            Text = "02  /  SELECTED PACK", Font = new Font("Bahnschrift SemiCondensed", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(238, 232, 210), AutoSize = true, Location = new Point(22, 7)
        });
        module.Controls.Add(moduleBand);
        packTitle = new Label
        {
            Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Bold), ForeColor = Ink,
            AutoSize = true, Location = new Point(27, 50)
        };
        module.Controls.Add(packTitle);
        packDescription = new Label
        {
            ForeColor = Color.FromArgb(91, 86, 70), AutoSize = true, Location = new Point(30, 99)
        };
        module.Controls.Add(packDescription);
        module.Controls.Add(new Label
        {
            Text = "F9", Font = new Font("Bahnschrift", 10f, FontStyle.Bold),
            ForeColor = Accent, AutoSize = true, Location = new Point(663, 70)
        });
        voiceToggle = new ToggleSwitch { Location = new Point(716, 55) };
        voiceToggle.CheckedChanged += (_, _) =>
        {
            if (!updatingControls) monitor.SetAutoEnabled(voiceToggle.Checked);
        };
        module.Controls.Add(voiceToggle);
        module.Controls.Add(new Label
        {
            Text = "自动语音", ForeColor = Ink, AutoSize = true, Location = new Point(715, 99)
        });

        var options = PaperPanel(new Point(34, 539), new Size(852, 68));
        Controls.Add(options);
        options.Controls.Add(new Label
        {
            Text = "中文字幕", Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
            ForeColor = Ink, AutoSize = true, Location = new Point(28, 9)
        });
        options.Controls.Add(new Label
        {
            Text = "随语音显示，台词结束后自动收起", ForeColor = Color.FromArgb(91, 86, 70),
            AutoSize = true, Location = new Point(30, 40)
        });
        subtitleToggle = new ToggleSwitch { Location = new Point(716, 17), Checked = monitor.SubtitlesEnabled };
        subtitleToggle.CheckedChanged += (_, _) =>
        {
            monitor.SetSubtitlesEnabled(subtitleToggle.Checked);
            if (!subtitleToggle.Checked) overlay.HideCue();
        };
        options.Controls.Add(subtitleToggle);

        testButton = new Button
        {
            Text = "F6  试听指挥官", FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(211, 203, 174), ForeColor = Ink,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Location = new Point(34, 615), Size = new Size(192, 37), Cursor = Cursors.Hand
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
        var card = new Panel { Size = new Size(406, 116), Margin = new Padding(5), BackColor = Paper, Cursor = Cursors.Hand };
        Color marker = pack.Id == VoicePackRegistry.Japan ? Accent : Color.FromArgb(159, 111, 50);
        card.Paint += (_, e) =>
        {
            bool selected = monitor.SelectedVoicePack == pack.Id;
            using var border = new Pen(selected ? marker : Color.FromArgb(74, 72, 61), selected ? 3f : 1f);
            e.Graphics.DrawRectangle(border, selected ? 1 : 0, selected ? 1 : 0,
                card.Width - (selected ? 3 : 1), card.Height - (selected ? 3 : 1));
            using var stripe = new SolidBrush(marker);
            e.Graphics.FillRectangle(stripe, 15, 16, 5, 81);
        };
        card.Controls.Add(new Label
        {
            Text = pack.EnglishName, Font = new Font("Bahnschrift", 9f, FontStyle.Bold),
            ForeColor = marker, AutoSize = true, Location = new Point(31, 13), Cursor = Cursors.Hand
        });
        card.Controls.Add(new Label
        {
            Text = pack.Name, Font = new Font("Microsoft YaHei UI", 19f, FontStyle.Bold),
            ForeColor = Ink, AutoSize = true, Location = new Point(29, 32), Cursor = Cursors.Hand
        });
        card.Controls.Add(new Label
        {
            Text = pack.Playable ? "可使用" : "待录音", Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            ForeColor = marker, AutoSize = true, Location = new Point(318, 22), Cursor = Cursors.Hand
        });
        card.Controls.Add(new Label
        {
            Text = $"{pack.CardCount} 张卡牌资源  ·  {pack.Description.Split('·')[0].Trim()}",
            Font = new Font("Microsoft YaHei UI", 8.5f), ForeColor = Color.FromArgb(91, 86, 70),
            AutoSize = true, Location = new Point(31, 85), Cursor = Cursors.Hand
        });
        void select(object? _, EventArgs __)
        {
            monitor.SetVoicePack(pack.Id);
            RefreshSelection();
        }
        card.Click += select;
        foreach (Control child in card.Controls) child.Click += select;
        return card;
    }

    private void RefreshSelection()
    {
        VoicePackDefinition pack = VoicePackRegistry.Get(monitor.SelectedVoicePack);
        packTitle.Text = pack.Playable ? $"{pack.Name}语音模组" : $"{pack.Name}卡包  ·  语音待录制";
        packDescription.Text = pack.Playable
            ? "开场指挥官  ·  单位出牌  ·  战况提示"
            : "13 个卡牌批次已索引；语音模型与录音待制作";
        updatingControls = true;
        voiceToggle.Checked = monitor.AutoEnabled;
        voiceToggle.Enabled = pack.Playable;
        subtitleToggle.Enabled = pack.Playable;
        updatingControls = false;
        testButton.Enabled = pack.Playable;
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
        statusLabel.Text = monitor.Connected
            ? (monitor.InMatch ? "已读取当前对局" : "已连接 KARDS")
            : online ? "等待进入对局" : "等待 KARDS";
        RefreshSelection();
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
        using var scratch = new Pen(Color.FromArgb(25, 122, 119, 100), 1f);
        for (int i = 0; i < 18; i++)
        {
            int x = (i * 113 + 41) % ClientSize.Width;
            int y = 70 + (i * 47 + 23) % Math.Max(1, ClientSize.Height - 82);
            e.Graphics.DrawLine(scratch, x, y, Math.Min(ClientSize.Width, x + 23), y + (i % 3 - 1));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) statusTimer.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);
}
