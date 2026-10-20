using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using KardsVox.Audio;

namespace KardsVox.Core;

internal sealed class MatchMonitor : IDisposable
{
    private const uint DesktopAccess = 0x41; // READOBJECTS | ENUMERATE
    private readonly object gate = new();
    private readonly string root;
    private readonly string settingsPath;
    private readonly string boardPath;
    private readonly string lobbyPath;
    private readonly IntroPlayer player;
    private readonly JapanVoicePack voices;
    private readonly bool recordOnly;
    private readonly string? recordDirectory;
    private readonly DetectionSettings settings;
    private readonly MatchStateMachine machine;
    private readonly JapaneseUnitDetector units = new();
    private readonly CancellationTokenSource stop = new();
    private readonly ManualResetEventSlim ready = new(false);
    private ScreenTemplate? boardTemplate;
    private ScreenTemplate? lobbyTemplate;
    private Thread? thread;
    private Exception? startError;
    private string? lastCaptureError;
    private DateTimeOffset nextRecordedFrame;
    private int recordedFrames;

    public MatchMonitor(string root, IntroPlayer player, JapanVoicePack voices, bool recordOnly = false)
    {
        this.root = root;
        this.player = player;
        this.voices = voices;
        this.recordOnly = recordOnly;
        if (recordOnly)
        {
            recordDirectory = Path.Combine(root, "Debug", $"session-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(recordDirectory);
            File.WriteAllText(Path.Combine(recordDirectory, "frames.tsv"),
                "frame\tutc\twidth\theight\tboard\tprematch\tturn\tupper_hq\tlower_hq\tupper_fill\tlower_fill\tcutoff\tjapanese_units\n");
            Console.WriteLine($"Silent KARDS visual recording: {recordDirectory}");
        }
        settingsPath = Path.Combine(root, "Data", "detection.json");
        boardPath = Path.Combine(root, "Data", "Templates", "board.png");
        lobbyPath = Path.Combine(root, "Data", "Templates", "lobby.png");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        settings = DetectionSettings.Load(settingsPath);
        machine = new MatchStateMachine(settings);
        if (File.Exists(boardPath)) boardTemplate = new ScreenTemplate(boardPath);
        if (File.Exists(lobbyPath)) lobbyTemplate = new ScreenTemplate(lobbyPath);
        Console.WriteLine($"Calibration files: {Path.Combine(root, "Data")}");
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "KardsVox picture monitor" };
        thread.Start();
        ready.Wait();
        if (startError is not null) { thread.Join(); throw startError; }
        Console.WriteLine(recordOnly ? "Visual recorder: silent (no audio)" :
            $"Auto intro: {(settings.AutoEnabled ? "ON" : "OFF")}; picture detection: {(boardTemplate is null ? "built-in" : "custom template")}");
        Console.WriteLine($"Capture interval: {settings.PollMilliseconds} ms; state: {machine.State}");
    }

    public void Hotkey(VoxHotkey key)
    {
        lock (gate)
        {
            switch (key)
            {
                case VoxHotkey.Play:
                    PlayIntro("F6");
                    break;
                case VoxHotkey.CaptureBoard:
                    CaptureTemplate(boardPath, settings.BoardRoi, true);
                    break;
                case VoxHotkey.CaptureLobby:
                    CaptureTemplate(lobbyPath, settings.LobbyRoi, false);
                    break;
                case VoxHotkey.ToggleAuto:
                    settings.AutoEnabled = !settings.AutoEnabled;
                    settings.Save(settingsPath);
                    Console.WriteLine($"Auto intro: {(settings.AutoEnabled ? "ON" : "OFF")}");
                    break;
                case VoxHotkey.Debug:
                    SaveDebug();
                    break;
            }
        }
    }

    private void Run()
    {
        IntPtr desktop = IntPtr.Zero;
        try
        {
            desktop = OpenInputDesktop(0, false, DesktopAccess);
            if (desktop == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open input desktop for screenshot detection");
            if (!SetThreadDesktop(desktop))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach screenshot detector to input desktop");
            using var dpi = DpiAwareness.UsePhysicalPixels();
            ready.Set();

            while (!stop.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception ex)
                {
                    if (lastCaptureError != ex.Message) Console.Error.WriteLine($"Capture failed: {ex.Message}");
                    lastCaptureError = ex.Message;
                }
                stop.Token.WaitHandle.WaitOne(settings.PollMilliseconds);
            }
        }
        catch (Exception ex)
        {
            if (!ready.IsSet) { startError = ex; ready.Set(); }
            else Console.Error.WriteLine($"Screenshot detector stopped: {ex.Message}");
        }
        finally
        {
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            ready.Set();
        }
    }

    private void Tick()
    {
        lock (gate)
        {
            if (!GameWindowDetector.TryGetForegroundClient(out var client))
            {
                if (!GameWindowDetector.Exists() && machine.State != MatchState.OutsideMatch)
                {
                    machine.WindowClosed();
                    units.Reset();
                    Console.WriteLine($"MatchState: {machine.State} (game window closed)");
                }
                return; // Alt-tab does not count as leaving a match.
            }

            if (recordOnly)
            {
                RecordFrame(client);
                return;
            }

            using var screen = ScreenCapture.Client(client);
            var evidence = BuiltInBoardDetector.Analyze(screen);
            bool boardSeen = evidence.IsBoard;
            string evidenceText = $"turn={evidence.TurnButtonRun:F2}, HQ={evidence.UpperHqContrast:F0}/{evidence.LowerHqContrast:F0}, card={evidence.UpperHqFill:F2}/{evidence.LowerHqFill:F2}, prematch={evidence.IsPreMatch}";
            if (boardTemplate is not null && boardSeen)
            {
                using var board = ScreenCapture.Roi(client, settings.BoardRoi);
                double score = boardTemplate.Compare(board);
                boardSeen = score >= settings.BoardThreshold;
                evidenceText += $", board score={score:F3}";
            }
            bool lobbySeen = false;
            if (lobbyTemplate is not null && !boardSeen)
            {
                using var lobby = ScreenCapture.Roi(client, settings.LobbyRoi);
                lobbySeen = lobbyTemplate.Compare(lobby) >= settings.LobbyThreshold;
            }
            MatchState before = machine.State;
            var now = DateTimeOffset.UtcNow;
            bool intro = machine.Observe(boardSeen, evidence.IsPreMatch || lobbySeen, now);
            if (machine.State != before)
            {
                Console.WriteLine($"MatchState: {before} -> {machine.State}; {evidenceText}");
                if (machine.State is MatchState.MatchEnded or MatchState.OutsideMatch)
                    units.Reset();
                if (machine.State == MatchState.InMatch)
                    units.Reset();
            }
            if (intro && settings.AutoEnabled)
                PlayIntro("New match");
            if (boardSeen && machine.State == MatchState.InMatch && units.Observe(screen, now) && settings.AutoEnabled)
                PlayDeployment();
            lastCaptureError = null;
        }
    }

    private void RecordFrame(Rectangle client)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextRecordedFrame) return;
        nextRecordedFrame = now.AddSeconds(1);
        using var screen = ScreenCapture.Client(client);
        var evidence = BuiltInBoardDetector.Analyze(screen);
        int width = Math.Min(1280, screen.Width);
        int height = (int)Math.Round(screen.Height * (double)width / screen.Width);
        using var scaled = new Bitmap(screen, new Size(width, height));
        int number = ++recordedFrames;
        scaled.Save(Path.Combine(recordDirectory!, $"frame-{number:0000}.jpg"), ImageFormat.Jpeg);
        File.AppendAllText(Path.Combine(recordDirectory!, "frames.tsv"),
            FormattableString.Invariant($"{number}\t{now:O}\t{client.Width}\t{client.Height}\t{evidence.IsBoard}\t{evidence.IsPreMatch}\t{evidence.TurnButtonRun:F3}\t{evidence.UpperHqContrast:F1}\t{evidence.LowerHqContrast:F1}\t{evidence.UpperHqFill:F3}\t{evidence.LowerHqFill:F3}\t{evidence.BrightCutoff:F1}\t{JapaneseUnitDetector.CountMarkers(screen)}\n"));
    }

    private void PlayIntro(string reason)
    {
        var line = voices.Pick("CMD", "INTRO");
        player.Play(line.Path);
        Console.WriteLine($"{reason}: playing {line.Id}");
    }

    private void PlayDeployment()
    {
        var line = voices.Pick("INF_Y", "DEPLOY");
        player.Play(line.Path);
        Console.WriteLine($"Japanese unit deployed: playing {line.Id}; settled units={units.ConfirmedCount}");
    }

    private void CaptureTemplate(string path, NormalizedRoi roi, bool board)
    {
        if (!GameWindowDetector.TryGetForegroundClient(out var client))
        {
            Console.WriteLine("Bring the KARDS window to the foreground before capturing a template.");
            return;
        }
        using var bitmap = ScreenCapture.Roi(client, roi);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path, ImageFormat.Png);
        try
        {
            var template = new ScreenTemplate(path);
            if (board) { boardTemplate = template; machine.WindowClosed(); units.Reset(); }
            else lobbyTemplate = template;
            Console.WriteLine($"Saved {(board ? "board" : "lobby")} template: {path}");
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private void SaveDebug()
    {
        if (!GameWindowDetector.TryGetForegroundClient(out var client))
        {
            Console.WriteLine("Bring the KARDS window to the foreground before taking a debug screenshot.");
            return;
        }
        using var bitmap = ScreenCapture.Client(client);
        var builtIn = BuiltInBoardDetector.Analyze(bitmap);
        int japaneseUnits = JapaneseUnitDetector.CountMarkers(bitmap);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var boardPen = new Pen(Color.Red, 4))
        using (var lobbyPen = new Pen(Color.DeepSkyBlue, 4))
        {
            graphics.DrawRectangle(boardPen, settings.BoardRoi.ToPixels(client.Size));
            graphics.DrawRectangle(lobbyPen, settings.LobbyRoi.ToPixels(client.Size));
            BuiltInBoardDetector.DrawRegions(graphics, client.Size);
            JapaneseUnitDetector.DrawRegion(graphics, client.Size);
        }
        string directory = Path.Combine(root, "Debug");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        bitmap.Save(path, ImageFormat.Png);
        double boardScore = ScoreCurrent(client, boardTemplate, settings.BoardRoi);
        double lobbyScore = ScoreCurrent(client, lobbyTemplate, settings.LobbyRoi);
        Console.WriteLine($"Debug: {path}; state={machine.State}; built-in={(builtIn.IsBoard ? "board" : builtIn.IsPreMatch ? "prematch" : "other")}, turn={builtIn.TurnButtonRun:F2}, HQ={builtIn.UpperHqContrast:F0}/{builtIn.LowerHqContrast:F0}, card={builtIn.UpperHqFill:F2}/{builtIn.LowerHqFill:F2}, Japanese units={japaneseUnits}; custom board={boardScore:F3}/{settings.BoardThreshold:F2}; lobby={lobbyScore:F3}/{settings.LobbyThreshold:F2}");
    }

    private static double ScoreCurrent(Rectangle client, ScreenTemplate? template, NormalizedRoi roi)
    {
        if (template is null) return -1;
        using var bitmap = ScreenCapture.Roi(client, roi);
        return template.Compare(bitmap);
    }

    public void Dispose()
    {
        stop.Cancel();
        thread?.Join();
        ready.Dispose();
        stop.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
