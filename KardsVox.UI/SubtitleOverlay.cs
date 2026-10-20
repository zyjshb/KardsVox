using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KardsVox.Core;

namespace KardsVox.UI;

internal sealed class SubtitleOverlay : Form
{
	private static readonly IntPtr HwndTopMost = new IntPtr(-1);

	private const uint SwpNoSize = 0x0001;

	private const uint SwpNoMove = 0x0002;

	private const uint SwpNoActivate = 0x0010;

	private const uint SwpShowWindow = 0x0040;

	private const int WmNcHitTest = 0x0084;

	private static readonly IntPtr HtTransparent = new IntPtr(-1);

	private static readonly string[] CommanderIntroSegments = new string[4] { "同胞们，用各位的勇气与自信，为敌人带去恐惧吧，", "坚持战斗，直至最后一人！", "为敌人带去恐惧吧，屹立于此的勇士们啊！", "粉碎敌人的诡计阴谋吧！" };

	private readonly Font commanderTextFont = new Font("Microsoft YaHei UI", 14.5f, FontStyle.Bold, GraphicsUnit.Point);

	private readonly Font soldierTextFont = new Font("Microsoft YaHei UI", 15.5f, FontStyle.Bold, GraphicsUnit.Point);

	private readonly Font nameFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);

	private readonly Font originalTextFont = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold, GraphicsUnit.Point);

	private readonly Font translationTextFont = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Regular, GraphicsUnit.Point);

	private readonly System.Windows.Forms.Timer timer;

	private MemoryMatchMonitor.SubtitleLine[] segments = Array.Empty<MemoryMatchMonitor.SubtitleLine>();
	private TimeSpan[] segmentDurations = Array.Empty<TimeSpan>();

	private string currentText = string.Empty;

	private string currentOriginal = string.Empty;

	private string displayOriginal = string.Empty;

	private DateTimeOffset shownAt;

	private DateTimeOffset hideAt;

	private TimeSpan displayDuration;

	private int segmentIndex = -1;

	private bool commander;

	private string currentSpeaker = "前线部队";
	private double textOpacity = 1;
	private double fadeFrom = 1;
	private double fadeTo = 1;
	private DateTimeOffset fadeStarted;
	private TimeSpan fadeDuration;
	private bool fadingText;
	private Size transitionMinimumSize;
	private readonly string? niriStateFile = Environment.GetEnvironmentVariable("KARDS_VOX_NIRI_STATE");
	private bool positionAvailable = true;

	protected override bool ShowWithoutActivation => true;
	protected override CreateParams CreateParams
	{
		get
		{
			var value = base.CreateParams;
			value.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020;
			// Wine must expose a managed toplevel so niri IPC can identify it.
			if (!string.IsNullOrEmpty(niriStateFile)) value.ExStyle |= 0x00040000;
			return value;
		}
	}

	public SubtitleOverlay(float textScale = 1)
	{
		Text = Environment.GetEnvironmentVariable("KARDS_VOX_OVERLAY_TITLE") ?? "KARDS VOX Subtitles";
		if (textScale != 1)
		{
			Font scaled(Font font) { var result = new Font(font.FontFamily, font.SizeInPoints * textScale, font.Style); font.Dispose(); return result; }
			commanderTextFont = scaled(commanderTextFont);
			soldierTextFont = scaled(soldierTextFont);
			nameFont = scaled(nameFont);
			originalTextFont = scaled(originalTextFont);
			translationTextFont = scaled(translationTextFont);
		}
		base.FormBorderStyle = FormBorderStyle.None;
		base.ShowInTaskbar = false;
		base.TopMost = true;
		// Keep this as a normal opaque top-level window. Layered/alpha windows can be
		// omitted by Windows fullscreen optimizations even though WinForms reports
		// them as visible.
		base.Opacity = 1.0;
		base.StartPosition = FormStartPosition.Manual;
		BackColor = Color.FromArgb(221, 214, 187);
		DoubleBuffered = true;
		base.Width = 520;
		base.Height = 98;
		timer = new System.Windows.Forms.Timer
		{
			Interval = 100
		};
		timer.Tick += delegate
		{
			UpdateCue();
		};
		UpdateWindowRegion();
	}

	protected override void OnSizeChanged(EventArgs e)
	{
		base.OnSizeChanged(e);
		UpdateWindowRegion();
	}

	private void UpdateWindowRegion()
	{
		if (Width < 100 || Height < 60) return;
		using GraphicsPath paperPath = PaperPanelPath(Width, Height - 18, Height);
		using GraphicsPath shadowPath = Offset(paperPath, 3, 3);
		Region shapedWindow = new Region(paperPath);
		shapedWindow.Union(shadowPath);
		Region? previous = Region;
		Region = shapedWindow;
		previous?.Dispose();
	}

	public void ShowCue(MemoryMatchMonitor.SubtitleCue cue)
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(delegate
			{
				ShowCue(cue);
			});
			return;
		}
		transitionMinimumSize = cue.FadeIn && Visible ? Size : Size.Empty;
		textOpacity = cue.FadeIn ? 0 : 1;
		fadingText = false;
		if (cue.FadeIn) BeginContentFade(1, TimeSpan.FromMilliseconds(180));
		else timer.Interval = 100;
		commander = cue.Commander;
		currentSpeaker = ((!string.IsNullOrWhiteSpace(cue.Speaker)) ? cue.Speaker : (cue.Commander ? "指挥官" : "前线部队"));
		segments = cue.Lines?.ToArray() ?? (cue.Sequenced
			? CommanderIntroSegments.Select(text => new MemoryMatchMonitor.SubtitleLine(null, text)).ToArray()
			: [new MemoryMatchMonitor.SubtitleLine(cue.Original, cue.Text)]);
		segmentDurations = cue.SegmentDurations?.ToArray() ?? Array.Empty<TimeSpan>();
		shownAt = DateTimeOffset.UtcNow;
		displayDuration = cue.Duration + TimeSpan.FromMilliseconds(cue.Sequenced ? 750 : 400);
		if (displayDuration < TimeSpan.FromSeconds(4))
		{
			displayDuration = TimeSpan.FromSeconds(4);
		}
		hideAt = shownAt + displayDuration;
		segmentIndex = -1;
		UpdateCue();
		if (!base.Visible && positionAvailable)
		{
			Show();
		}
		if (Visible) KeepAboveGame();
		Invalidate(invalidateChildren: true);
		Update();
		Console.WriteLine($"Subtitle shown: speaker={currentSpeaker}, visible={Visible}, bounds={Bounds}, text={currentText}");
		timer.Start();
	}

	public void BeginTransition(TimeSpan duration)
	{
		if (InvokeRequired)
		{
			BeginInvoke(() => BeginTransition(duration));
			return;
		}
		if (!Visible) return;
		// Fade the ink within the opaque paper window; layered window opacity
		// would make this overlay disappear under fullscreen optimizations.
		BeginContentFade(0, duration);
		DateTimeOffset minimumHide = DateTimeOffset.UtcNow + duration + TimeSpan.FromMilliseconds(250);
		if (hideAt < minimumHide) hideAt = minimumHide;
		timer.Start();
	}

	private void BeginContentFade(double target, TimeSpan duration)
	{
		fadeFrom = textOpacity;
		fadeTo = target;
		fadeDuration = duration;
		fadeStarted = DateTimeOffset.UtcNow;
		fadingText = true;
		timer.Interval = 16;
	}

	public void HideCue()
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(HideCue);
			return;
		}
		timer.Stop();
		fadingText = false;
		textOpacity = 1;
		transitionMinimumSize = Size.Empty;
		Hide();
	}

	private void UpdateCue()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		if (fadingText)
		{
			double progress = Math.Clamp((now - fadeStarted).TotalMilliseconds / Math.Max(1, fadeDuration.TotalMilliseconds), 0, 1);
			textOpacity = fadeFrom + (fadeTo - fadeFrom) * progress;
			if (progress >= 1) { fadingText = false; timer.Interval = 100; }
			Invalidate();
		}
		if (now >= hideAt)
		{
			HideCue();
			return;
		}
		int next = ((segments.Length > 1) ? SegmentAt(now) : 0);
		if (next != segmentIndex)
		{
			segmentIndex = next;
			currentText = segments[next].Chinese;
			currentOriginal = segments[next].Original ?? string.Empty;
			PositionOverGame();
			Invalidate();
		}
		else
		{
			PositionOverGame();
		}
		if (!positionAvailable) Hide();
		else if (!Visible) Show();
	}

	private int SegmentAt(DateTimeOffset now)
	{
		if (segmentDurations.Length == segments.Length && segmentDurations.All(duration => duration > TimeSpan.Zero))
		{
			TimeSpan elapsed = now - shownAt;
			TimeSpan boundary = TimeSpan.Zero;
			for (int index = 0; index < segmentDurations.Length - 1; index++)
			{
				boundary += segmentDurations[index];
				if (elapsed < boundary) return index;
			}
			return segments.Length - 1;
		}
		double num = Math.Clamp((now - shownAt).TotalMilliseconds / displayDuration.TotalMilliseconds, 0.0, 0.999);
		return Math.Min(segments.Length - 1, (int)(num * segments.Length));
	}

	private void PositionOverGame()
	{
		Rectangle game;
		Rectangle bounds = (GameWindowDetector.TryGetForegroundClient(out game) || GameWindowDetector.TryGetAnyGameClient(out game))
			? game
			: Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
		positionAvailable = true;
		if (!string.IsNullOrEmpty(niriStateFile))
		{
			positionAvailable = TryReadNiriBounds(niriStateFile, out bounds);
			if (!positionAvailable) return;
		}
		Font textFont = currentOriginal.Length > 0 ? translationTextFont : (commander ? commanderTextFont : soldierTextFont);
		Size oneLine = TextRenderer.MeasureText(currentText, textFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
		int maximumWidth = Math.Min(Math.Max(160, bounds.Width - 36), Math.Max(560, Math.Min(760, bounds.Width / 2)));
		string previewOriginal = WrapOriginal(maximumWidth - 70);
		int originalLineWidth = previewOriginal.Split('\n').Select(line =>
			TextRenderer.MeasureText(line.TrimEnd('\r'), originalTextFont, Size.Empty,
				TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width).DefaultIfEmpty(0).Max();
		int desiredWidth = Math.Max(oneLine.Width + (currentOriginal.Length > 0 ? 30 : 0), originalLineWidth) + 88;
		int width = Math.Clamp(desiredWidth, Math.Min(maximumWidth, currentOriginal.Length > 0 ? 500 : commander ? 510 : 390), maximumWidth);
		width = Math.Min(maximumWidth, Math.Max(width, transitionMinimumSize.Width));
		int availableTextWidth = width - 70;
		displayOriginal = WrapOriginal(availableTextWidth);
		int chineseHeight = TextRenderer.MeasureText(currentText, textFont,
			new Size(width - 70, int.MaxValue),
			TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
		int originalHeight = currentOriginal.Length == 0 ? 0 : TextRenderer.MeasureText(displayOriginal, originalTextFont,
			new Size(availableTextWidth, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
		int height = currentOriginal.Length == 0 ? Math.Max(chineseHeight + OriginalTop + 27, 98)
			: Math.Max(chineseHeight + originalHeight + OriginalTop + 41, 126);
		height = Math.Min(bounds.Height - 40, Math.Max(height, transitionMinimumSize.Height));
		// The player's command-point counter is fixed near the lower-left edge on
		// every board skin. Keep the paper card above that counter and align their
		// left edges so the subtitle no longer covers the avatar or player name.
		int left = bounds.Left + Math.Max(18, (int)((double)bounds.Width * 0.018));
		int commandPointTop = bounds.Bottom - Math.Max(190, (int)((double)bounds.Height * 0.205));
		int top = Math.Max(bounds.Top + 20, commandPointTop - height - 10);
		left = Math.Clamp(left, bounds.Left, bounds.Right - width);
		top = Math.Clamp(top, bounds.Top, bounds.Bottom - height);
		// niri owns the real screen position; the helper follows the actual game
		// tile via IPC. Xwayland's reported Win32 origin can remain (0,0).
		if (string.IsNullOrEmpty(niriStateFile)) SetBounds(left, top, width, height);
		else Size = new Size(width, height);
		if (Visible)
		{
			KeepAboveGame();
		}
	}

	internal static bool TryReadNiriBounds(string path, out Rectangle bounds)
	{
		bounds = Rectangle.Empty;
		try
		{
			if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(2)) return false;
			string[] fields = File.ReadAllText(path).Trim().Split(' ');
			if (fields.Length != 3 || fields[0] != "1" || !int.TryParse(fields[1], out int width) ||
				!int.TryParse(fields[2], out int height) || width < 320 || height < 240 || width > 16384 || height > 16384) return false;
			bounds = new Rectangle(0, 0, width, height);
			return true;
		}
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return false; }
	}

	private string WrapOriginal(int maxWidth)
	{
		int measure(string value) => TextRenderer.MeasureText(value, originalTextFont, Size.Empty,
			TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
		if (measure(currentOriginal) <= maxWidth) return currentOriginal;
		int sentenceBreak = currentOriginal.IndexOf(". ", StringComparison.Ordinal);
		if (sentenceBreak >= 0)
		{
			string first = currentOriginal[..(sentenceBreak + 1)];
			string second = currentOriginal[(sentenceBreak + 2)..];
			if (measure(first) <= maxWidth && measure(second) <= maxWidth)
				return first + Environment.NewLine + second;
		}
		string[] words = currentOriginal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		string? balanced = null;
		int bestWidth = int.MaxValue;
		for (int split = 1; split < words.Length; split++)
		{
			string left = string.Join(' ', words[..split]);
			string right = string.Join(' ', words[split..]);
			int width = Math.Max(measure(left), measure(right));
			if (width <= maxWidth && width < bestWidth)
			{
				bestWidth = width;
				balanced = left + Environment.NewLine + right;
			}
		}
		if (balanced != null) return balanced;
		List<string> lines = new();
		string current = string.Empty;
		foreach (string word in words)
		{
			// Split an overlong word by Unicode text elements; this also covers
			// German compounds, accents and CJK text without splitting surrogates.
			if (measure(word) > maxWidth)
			{
				if (current.Length > 0) { lines.Add(current); current = string.Empty; }
				var elements = System.Globalization.StringInfo.GetTextElementEnumerator(word);
				while (elements.MoveNext())
				{
					string element = elements.GetTextElement();
					if (current.Length > 0 && measure(current + element) > maxWidth)
					{ lines.Add(current); current = string.Empty; }
					current += element;
				}
				continue;
			}
			string candidate = current.Length == 0 ? word : current + " " + word;
			if (current.Length > 0 && measure(candidate) > maxWidth)
			{
				lines.Add(current);
				current = word;
			}
			else current = candidate;
		}
		if (current.Length > 0) lines.Add(current);
		return string.Join(Environment.NewLine, lines);
	}

	private int CaptionHeight => Math.Max(23, TextRenderer.MeasureText(currentSpeaker, nameFont, Size.Empty,
		TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height + 4);
	private int OriginalTop => Math.Max(39, 10 + CaptionHeight + 6);

	internal static void VerifyNationLayout(string root, string output)
	{
		Directory.CreateDirectory(output);
		foreach (var definition in VoicePackRegistry.All.Where(p => p.FileBased))
		{
			var pack = new KardsVox.Audio.NationSubtitlePack(root, definition);
			foreach (float scale in new[] { 1f, 1.25f, 1.5f })
			{
				using var overlay = new SubtitleOverlay(scale);
				_ = overlay.Handle;
				int maxHeight = 0;
				foreach (var line in pack.AllLines)
				{
					overlay.currentOriginal = line.Original;
					overlay.currentText = line.Chinese;
					overlay.currentSpeaker = line.Speaker;
					overlay.PositionOverGame();
					int width = overlay.Width - 60;
					int measure(string text, Font font) => TextRenderer.MeasureText(text, font,
						new Size(width, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
					int bottom = overlay.OriginalTop + measure(overlay.displayOriginal, overlay.originalTextFont) + 2 + 7 +
						measure(line.Chinese, overlay.translationTextFont);
					if (bottom > overlay.Height - 26)
						throw new InvalidDataException($"Subtitle clipped at {scale}: {line.Key}");
					foreach (string originalLine in overlay.displayOriginal.Split('\n'))
						if (TextRenderer.MeasureText(originalLine.TrimEnd('\r'), overlay.originalTextFont, Size.Empty,
							TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width > width)
							throw new InvalidDataException($"Original too wide at {scale}: {line.Key}");
					if (overlay.Height > maxHeight)
					{
						maxHeight = overlay.Height;
						using Bitmap bitmap = new(overlay.Width, overlay.Height);
						overlay.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
						if (overlay.Region is Region region)
							for (int y = 0; y < bitmap.Height; y++)
							for (int x = 0; x < bitmap.Width; x++)
								if (!region.IsVisible(x, y)) bitmap.SetPixel(x, y, Color.Transparent);
						bitmap.Save(Path.Combine(output, $"{definition.Id.ToLowerInvariant()}-layout-{scale * 100:0}.png"), System.Drawing.Imaging.ImageFormat.Png);
					}
				}
				Console.WriteLine($"Layout OK: {pack.AllLines.Count()} {definition.Id} lines, text scale {scale * 100:0}%, maximum card height {maxHeight}px. Font-scale simulation, not an OS DPI-switch test.");
			}
		}
	}

	private void KeepAboveGame()
	{
		SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0,
			SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
		Refresh();
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		base.OnPaint(e);
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		int bodyBottom = base.Height - 18;
		using GraphicsPath panelPath = PaperPanelPath(base.Width, bodyBottom, base.Height);
		using GraphicsPath shadowPath = Offset(panelPath, 3, 3);
		using SolidBrush shadow = new SolidBrush(Color.FromArgb(35, 24, 22, 18));
		using SolidBrush paper = new SolidBrush(Color.FromArgb(221, 214, 187));
		using Pen border = new Pen(Color.FromArgb(58, 55, 45), 1.8f)
		{
			LineJoin = LineJoin.Bevel
		};
		e.Graphics.FillPath(shadow, shadowPath);
		e.Graphics.FillPath(paper, panelPath);
		e.Graphics.DrawPath(border, panelPath);
		DrawPaperTexture(e.Graphics, base.Width, bodyBottom);
		string speaker = currentSpeaker;
		int plaqueWidth = Math.Clamp(TextRenderer.MeasureText(speaker, nameFont, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 24, 76, Math.Max(112, Width - 180));
		Rectangle plaque = new Rectangle(30, 10, plaqueWidth, CaptionHeight);
		using SolidBrush plaqueBrush = new SolidBrush(InkColor(Color.FromArgb(55, 54, 47)));
		using Pen plaqueBorder = new Pen(InkColor(Color.FromArgb(32, 31, 27)), 1f);
		e.Graphics.FillRectangle(plaqueBrush, plaque);
		e.Graphics.DrawRectangle(plaqueBorder, plaque);
		TextRenderer.DrawText(e.Graphics, speaker, nameFont, plaque, InkColor(Color.FromArgb(237, 231, 208)), TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		using SolidBrush japanRed = new SolidBrush(InkColor(Color.FromArgb(169, 58, 43)));
		e.Graphics.FillEllipse(japanRed, plaque.Right + 11, 16, 11, 11);
		using Pen rule = new Pen(Color.FromArgb(122, 111, 82), 1f);
		e.Graphics.DrawLine(rule, plaque.Right + 32, 21, base.Width - 28, 21);
		Font textFont = (commander ? commanderTextFont : soldierTextFont);
		if (currentOriginal.Length > 0)
		{
			int textWidth = base.Width - 60;
			int originalHeight = TextRenderer.MeasureText(displayOriginal, originalTextFont,
				new Size(textWidth, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
			Rectangle originalRect = new Rectangle(31, OriginalTop, textWidth, originalHeight + 2);
			TextRenderer.DrawText(e.Graphics, displayOriginal, originalTextFont, originalRect,
				InkColor(Color.FromArgb(39, 38, 33)), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			Rectangle chineseRect = new Rectangle(31, originalRect.Bottom + 7, textWidth,
				bodyBottom - originalRect.Bottom - 15);
			TextRenderer.DrawText(e.Graphics, currentText, translationTextFont, chineseRect,
				InkColor(Color.FromArgb(99, 88, 67)), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
		}
		else
		{
			Rectangle textRect = new Rectangle(31, 40, base.Width - 60, bodyBottom - 46);
			TextRenderer.DrawText(e.Graphics, currentText, textFont, textRect, InkColor(Color.FromArgb(39, 38, 33)),
				TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
		}
		using Pen stamp = new Pen(Color.FromArgb(86, 91, 82, 62), 1f);
		e.Graphics.DrawLine(stamp, base.Width - 45, bodyBottom - 10, base.Width - 27, bodyBottom - 10);
		e.Graphics.DrawLine(stamp, base.Width - 41, bodyBottom - 7, base.Width - 27, bodyBottom - 7);
	}

	private Color InkColor(Color ink) => Color.FromArgb(
		(int)Math.Round(221 + (ink.R - 221) * textOpacity),
		(int)Math.Round(214 + (ink.G - 214) * textOpacity),
		(int)Math.Round(187 + (ink.B - 187) * textOpacity));

	private static GraphicsPath PaperPanelPath(int width, int bodyBottom, int height)
	{
		GraphicsPath path = new GraphicsPath();
		Point[] points = new Point[10]
		{
			new Point(24, 1),
			new Point(width - 16, 1),
			new Point(width - 5, 12),
			new Point(width - 5, bodyBottom - 10),
			new Point(width - 15, bodyBottom),
			new Point(70, bodyBottom),
			new Point(24, height - 5),
			new Point(36, bodyBottom),
			new Point(13, bodyBottom),
			new Point(13, 12)
		};
		path.AddPolygon(points);
		return path;
	}

	private static void DrawPaperTexture(Graphics graphics, int width, int bodyBottom)
	{
		using SolidBrush fleck = new SolidBrush(Color.FromArgb(22, 76, 70, 54));
		for (int index = 0; index < 34; index++)
		{
			int x = 25 + (index * 97 + 31) % Math.Max(1, width - 55);
			int y = 7 + (index * 43 + 19) % Math.Max(1, bodyBottom - 18);
			graphics.FillRectangle(fleck, x, y, (index % 4 != 0) ? 1 : 2, 1);
		}
		using Pen scratch = new Pen(Color.FromArgb(18, 69, 65, 51), 1f);
		graphics.DrawLine(scratch, (float)width * 0.56f, 7f, (float)width * 0.66f, 8f);
		graphics.DrawLine(scratch, (float)width * 0.18f, bodyBottom - 9, (float)width * 0.28f, bodyBottom - 8);
	}

	private static GraphicsPath Offset(GraphicsPath source, int x, int y)
	{
		GraphicsPath path = (GraphicsPath)source.Clone();
		using Matrix matrix = new Matrix();
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
			originalTextFont.Dispose();
			translationTextFont.Dispose();
		}
		base.Dispose(disposing);
	}

	protected override void WndProc(ref Message message)
	{
		if (message.Msg == WmNcHitTest)
		{
			message.Result = HtTransparent;
			return;
		}
		base.WndProc(ref message);
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
		int x, int y, int cx, int cy, uint flags);

}
