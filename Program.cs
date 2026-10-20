using System.Text;
using System.Drawing;
using KardsVox.Audio;
using KardsVox.Core;
using KardsVox.UI;
using NAudio.Wave;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("KardsVox requires Windows.");
    return 1;
}

if (args.Length > 0)
{
    try
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "KardsVox";
    }
    catch (IOException) { }
}
string runtimeRoot = AppContext.BaseDirectory;
string dataRoot = FindDataRoot(runtimeRoot);
using var sessionLog = SessionLog.TryStart(runtimeRoot);

if (args is ["--status"])
{
    Console.WriteLine($"KARDS {(GameWindowDetector.Exists() ? "Online" : "Offline")}");
    return 0;
}
if (args is ["--self-test"])
{
    StateMachineSelfTest.Run();
    return 0;
}
if (args is ["--scan-kards-key"])
{
    try
    {
        var keys = ReadOnlyAesKeyScanner.ScanKards(Console.WriteLine);
        if (keys.Count == 0) { Console.Error.WriteLine("No AES-256 schedule found. Enter the KARDS main menu and try again."); return 2; }
        foreach (string key in keys) Console.WriteLine($"AES-256 candidate: {key}");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
if (args is ["--probe-unreal"])
{
    try { using var probe = new UnrealReadOnlyProbe(); probe.DumpCandidates(Console.Out); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
if (args is ["--find-namepool"])
{
    try { using var probe = new UnrealReadOnlyProbe(); probe.FindNamePool(Console.Out); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
if (args is ["--find-kards-objects"])
{
    try { using var probe = new UnrealReadOnlyProbe(); probe.FindKardsObjects(Console.Out); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (args is ["--find-kards-fields"])
{
    try { using var probe = new UnrealReadOnlyProbe(); probe.FindKardsFields(Console.Out); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (args is ["--search-unreal-names", var searchText])
{
    try { using var probe = new UnrealReadOnlyProbe(); probe.SearchNames(searchText, Console.Out); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (args is ["--find-object-registry"])
{
    try { using var probe = new UnrealReadOnlyProbe(); probe.FindObjectRegistry(Console.Out); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (args is ["--dump-class-props", var className])
{
    try { using var probe = new UnrealReadOnlyProbe(); probe.DumpClassProperties(className, Console.Out); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (args is ["--dump-struct-address", var addressText])
{
    try
    {
        string clean = addressText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? addressText[2..] : addressText;
        long address = Convert.ToInt64(clean, 16);
        using var probe = new UnrealReadOnlyProbe();
        probe.DumpStructAddress(address, Console.Out);
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (args is ["--runtime-snapshot"])
{
    try
    {
        using var runtime = new UnrealRuntimeReader();
        Console.WriteLine($"KARDS pid={runtime.ProcessId}; objects={runtime.ObjectCount:N0}");
        var boards = runtime.FindInstances(runtime.BoardClass).ToArray();
        var monitors = runtime.FindInstances(runtime.BattleMonitorClass).ToArray();
        Console.WriteLine($"BP_Board instances: {string.Join(", ", boards.Select(x => $"0x{x:X}({runtime.GetObjectName(x)})"))}");
        Console.WriteLine($"BP_BattleStateMonitor instances: {string.Join(", ", monitors.Select(x => $"0x{x:X}({runtime.GetObjectName(x)}, monitoring={runtime.ReadByte(x + 0x2D0)})"))}");
        foreach (long board in boards)
        {
            var ids = runtime.ReadArray(board, 0x500);
            var enemies = runtime.ReadArray(board, 0x670);
            long hqLeft = runtime.ReadPointer(board + 0x2C8), hqRight = runtime.ReadPointer(board + 0x2D0);
            Console.WriteLine($"Board 0x{board:X}: HQ=0x{hqLeft:X}/0x{hqRight:X}; playedIDs={ids.Count}/{ids.Capacity} data=0x{ids.Data:X}; enemy={enemies.Count}/{enemies.Capacity}");
            Console.WriteLine($"  directHQ=0x{runtime.ReadPointer(board + 0x7E8):X}/0x{runtime.ReadPointer(board + 0x7F0):X}; " +
                $"cardRef=0x{runtime.ReadPointer(board + 0x3F8):X}; tmpCurrent=0x{runtime.ReadPointer(board + 0x4F0):X}; " +
                $"handRef=0x{runtime.ReadPointer(board + 0x9E8):X}; placing={runtime.ReadByte(board + 0xB40)}; clicked={runtime.ReadByte(board + 0xC40)}; faction={runtime.ReadByte(board + 0x7C3)}");
            long ownHqVisual = runtime.ReadPointer(board + 0x7E8), enemyHqVisual = runtime.ReadPointer(board + 0x7F0);
            Console.WriteLine($"  directHQLocation={runtime.ReadByte(ownHqVisual + 0x3B0)}/{runtime.ReadByte(enemyHqVisual + 0x3B0)}; " +
                $"directHQSelf=0x{runtime.ReadPointer(ownHqVisual + 0x808):X}/0x{runtime.ReadPointer(enemyHqVisual + 0x808):X}");
            long sampleHqVisual = ownHqVisual != 0 ? ownHqVisual : enemyHqVisual;
            if (sampleHqVisual != 0)
            {
                long visualClass = runtime.GetClassAddress(sampleHqVisual);
                var chain = new List<string>();
                for (int depth = 0; depth < 12 && visualClass != 0; depth++)
                {
                    chain.Add($"0x{visualClass:X}({runtime.GetObjectName(visualClass)})");
                    visualClass = runtime.GetSuperClass(visualClass);
                }
                Console.WriteLine($"  directHQClass={string.Join(" <- ", chain)}");
            }
            Console.WriteLine($"  introBack=0x{runtime.ReadPointer(board + 0xB00):X}/0x{runtime.ReadPointer(board + 0xB08):X}; " +
                $"introLocationHand=0x{runtime.ReadPointer(board + 0xB48):X}/0x{runtime.ReadPointer(board + 0xB50):X}; showHQ={runtime.ReadSingle(board + 0x318):F3}/{runtime.ReadByte(board + 0x31C)}; " +
                $"battleHudShown={runtime.ReadByte(board + 0xE88)}; drawVisible={runtime.ReadByte(board + 0x780)}; ordersFlying={runtime.ReadInt(board + 0x77C)}; playRunning={runtime.ReadByte(board + 0x520)}; tmpId={runtime.ReadInt(board + 0x524)}; tmpEnemy={runtime.ReadByte(board + 0x680)}");
        }
        foreach (string visualClassName in new[] { "BP_BaseCard_C", "BP_HandCard_C", "BP_BoardCard_C" })
        {
            long? visualClass = runtime.GetBlueprintClass(visualClassName);
            if (visualClass is null) continue;
            var visuals = runtime.FindInstances(visualClass.Value)
                .Where(x => runtime.GetObjectName(x)?.StartsWith("Default__", StringComparison.Ordinal) != true)
                .Take(40).ToArray();
            Console.WriteLine($"{visualClassName} instances: {visuals.Length}");
            foreach (long visual in visuals)
            {
                long cardRef = runtime.ReadPointer(visual + 0x808);
                long sound = runtime.ReadPointer(cardRef + 0x340);
                Console.WriteLine($"  0x{visual:X} name={runtime.GetObjectName(visual)} selfRef=0x{cardRef:X}({runtime.GetObjectName(cardRef)}/{runtime.GetClassName(cardRef)}) " +
                    $"sound=0x{sound:X}({runtime.GetObjectName(sound)}), side={runtime.ReadByte(cardRef + 0x276)}, def={runtime.ReadInt(cardRef + 0x74)}, " +
                    $"loc={runtime.ReadByte(visual + 0x3B0)}, hiddenBits=0x{runtime.ReadByte(visual + 0x58):X2}, role={runtime.ReadByte(visual + 0x168)}, " +
                    $"hoverable={runtime.ReadByte(visual + 0xA20)}, blockDrag={runtime.ReadByte(visual + 0x9A0)}, root=0x{runtime.ReadPointer(visual + 0x1B8):X}, " +
                    $"i84={runtime.ReadInt(cardRef + 0x84)}, i88={runtime.ReadInt(cardRef + 0x88)}, i278={runtime.ReadInt(cardRef + 0x278)}, i310={runtime.ReadInt(cardRef + 0x310)}, i33c={runtime.ReadInt(cardRef + 0x33C)}");
            }
        }
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (args is ["--analyze-image", var imagePath])
{
    using var image = new Bitmap(imagePath);
    var evidence = BuiltInBoardDetector.Analyze(image);
    var transition = EmptyTableTransitionDetector.Analyze(image);
    Console.WriteLine($"Picture: {(evidence.IsBoard ? "battlefield" : evidence.IsPreMatch ? "prematch" : "other")}; turn={evidence.TurnButtonRun:F2}, HQ={evidence.UpperHqContrast:F0}/{evidence.LowerHqContrast:F0}, card={evidence.UpperHqFill:F2}/{evidence.LowerHqFill:F2}, Japanese units={JapaneseUnitDetector.CountMarkers(image)}, cutoff={evidence.BrightCutoff:F0}");
    Console.WriteLine($"Empty-table transition={transition.IsEmptyTable}; global={transition.GlobalLuma:F1}, center={transition.CenterLuma:F1}, wood={transition.CenterRed:F0}/{transition.CenterGreen:F0}/{transition.CenterBlue:F0}");
    return 0;
}
if ((args.Length is 2 or 3) && args[0] == "--replay-frames")
{
    string framesDirectory = args[1];
    int repeats = 1;
    if (args.Length == 3 && !int.TryParse(args[2], out repeats) || repeats is < 1 or > 10)
    {
        Console.Error.WriteLine("Replay repeat count must be 1 to 10.");
        return 1;
    }
    string[] frames = Directory.Exists(framesDirectory)
        ? Directory.GetFiles(framesDirectory, "sample-*.jpg").OrderBy(path => path, StringComparer.Ordinal).ToArray()
        : [];
    if (frames.Length == 0)
    {
        Console.Error.WriteLine("--replay-frames requires a directory containing sample-*.jpg screenshots.");
        return 1;
    }
    var machine = new MatchStateMachine(new DetectionSettings());
    var units = new JapaneseUnitDetector();
    var start = DateTimeOffset.UtcNow;
    for (int index = 0; index < frames.Length * repeats; index++)
    {
        using var frame = new Bitmap(frames[index % frames.Length]);
        var evidence = BuiltInBoardDetector.Analyze(frame);
        var before = machine.State;
        var now = start.AddMilliseconds(index * 400);
        bool intro = machine.Observe(evidence.IsBoard, evidence.IsPreMatch, now);
        if (machine.State != before)
        {
            if (machine.State is MatchState.MatchEnded or MatchState.OutsideMatch or MatchState.InMatch)
                units.Reset();
            Console.WriteLine($"{index * .4:F1}s {before} -> {machine.State}");
        }
        if (intro) Console.WriteLine($"{index * .4:F1}s INTRO");
        if (evidence.IsBoard && machine.State == MatchState.InMatch && units.Observe(frame, now))
            Console.WriteLine($"{index * .4:F1}s DEPLOY (Japanese units: {units.ConfirmedCount})");
    }
    return 0;
}
if (args is ["--record-visual", var secondsValue])
{
    if (!int.TryParse(secondsValue, out int seconds) || seconds is < 10 or > 900)
    {
        Console.Error.WriteLine("--record-visual requires 10 to 900 seconds.");
        return 1;
    }
    try
    {
        var voices = new JapanVoicePack(runtimeRoot);
        using var player = new IntroPlayer();
        using var monitor = new MatchMonitor(dataRoot, player, voices, recordOnly: true);
        monitor.Start();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        Console.WriteLine("Visual recording complete.");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
if (args is ["--voice-check"])
{
    try
    {
        var voices = new JapanVoicePack(runtimeRoot);
        double totalSeconds = 0;
        foreach (var line in File.ReadLines(Path.Combine(runtimeRoot, "Data", "Japan", "voice_lines.tsv")).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entry = voices.ById(line.Split('\t')[0]);
            using var wav = new WaveFileReader(entry.Path);
            if (wav.WaveFormat.Encoding != WaveFormatEncoding.Pcm || wav.WaveFormat.SampleRate != 48000 ||
                wav.WaveFormat.Channels != 1 || wav.WaveFormat.BitsPerSample != 16 || wav.TotalTime.TotalSeconds < 0.3)
                throw new InvalidDataException($"Invalid voice clip: {entry.Path}");
            totalSeconds += wav.TotalTime.TotalSeconds;
        }
        foreach (string category in JapanVoicePack.DeploymentPoolNames)
        {
            int size = voices.GetDeploymentPoolSize(category);
            var cycle = new HashSet<string>(StringComparer.Ordinal);
            string previous = string.Empty;
            for (int index = 0; index < size; index++)
            {
                previous = voices.PickDeployment(category).Id;
                if (!cycle.Add(previous))
                    throw new InvalidDataException($"Shuffle bag repeated before exhausting {category}.");
            }
            string next = voices.PickDeployment(category).Id;
            if (size > 1 && next == previous)
                throw new InvalidDataException($"Shuffle bag repeated at the cycle boundary for {category}.");
            Console.WriteLine($"  {category}: {size} exclusive deployment lines, shuffle bag OK");
            int hurtSize = voices.GetHurtPoolSize(category);
            if (hurtSize == 0) throw new InvalidDataException($"Empty hurt pool: {category}");
            Console.WriteLine($"    hurt: {hurtSize} category lines");
        }
        if (voices.HurtSfxCount != 6)
            throw new InvalidDataException($"CC0 hurt SFX pack mismatch: {voices.HurtSfxCount}/6");
        foreach (string effect in Directory.GetFiles(Path.Combine(runtimeRoot, "Audio", "Sfx", "Hurt"), "CC0_HURT_*.mp3"))
        {
            using var audio = new AudioFileReader(effect);
            if (audio.TotalTime.TotalSeconds < .1)
                throw new InvalidDataException($"Invalid hurt effect: {effect}");
        }
        var orderCycle = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < voices.OrderPoolSize; index++)
            if (!orderCycle.Add(voices.PickOrder().Id))
                throw new InvalidDataException("Order shuffle bag repeated before exhaustion.");
        var expectedOrders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["card_event_desperate_measures_C"] = "玉碎",
            ["card_event_surprise_attack_C"] = "奇袭命令",
            ["card_event_sneak_attack_C"] = "奇袭命令",
            ["card_event_air_blitz_C"] = "航空命令",
            ["card_event_lightning_conquest_new_C"] = "航空命令",
            ["card_event_imperial_order_C"] = "帝国指令",
            ["card_event_blade_of_the_samurai_C"] = "武士之刃",
            ["card_event_great_expanse_C"] = "扩张",
            ["card_event_for_prosperety_C"] = "为了繁荣"
        };
        foreach (var expected in expectedOrders)
        {
            OrderVoiceProfile actual = voices.ResolveOrder(expected.Key);
            if (actual.Name != expected.Value)
                throw new InvalidDataException($"Order profile mismatch: {expected.Key} -> {actual.Name}");
        }
        Console.WriteLine($"  ORDER: {voices.OrderPoolSize} lines, shuffle bag OK, synthesized telegraph enabled");
        Console.WriteLine($"  HURT: {voices.HurtSfxCount} CC0 effects + category Japanese voice pools enabled");
        if (voices.JapanCardCount != 312)
            throw new InvalidDataException($"Japan card catalog mismatch: {voices.JapanCardCount}/312");
        var expectedCategories = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["card_event_imperial_order_C"] = JapanVoicePack.OrderPool,
            ["card_event_blade_of_the_samurai_C"] = JapanVoicePack.OrderPool,
            ["card_event_great_expanse_C"] = JapanVoicePack.OrderPool,
            ["card_unit_zero_a6m2_21_C"] = JapanVoicePack.AirPool,
            ["card_unit_type_97_kai_C"] = JapanVoicePack.TankPool,
            ["card_unit_2nd_raiding_brigade_C"] = JapanVoicePack.InfantryPool
        };
        foreach (var expected in expectedCategories)
            if (voices.ClassifyJapanCard(expected.Key) != expected.Value)
                throw new InvalidDataException($"Japan card category mismatch: {expected.Key}");
        var sovietCards = new NationCardCatalog(runtimeRoot, VoicePackRegistry.Soviet, 319);
        if (sovietCards.Classify("card_unit_t_34_C") != JapanVoicePack.TankPool ||
            sovietCards.Classify("card_unit_ilyushin_il_2_C") != JapanVoicePack.AirPool ||
            sovietCards.Classify("card_event_assault_C") != JapanVoicePack.OrderPool ||
            sovietCards.Classify("card_location_soviet_scen1_C") is not null)
            throw new InvalidDataException("Soviet card category mismatch.");
        Console.WriteLine($"Soviet card pack indexed: {sovietCards.Count} resources, voice recording pending.");
        Console.WriteLine($"Japan voice pack OK: {voices.Count} clips, {totalSeconds:F1} seconds, {voices.JapanCardCount} Japan card resources mapped.");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
if (args is ["--ui-snapshot", var snapshotPath])
{
    try
    {
        ApplicationConfiguration.Initialize();
        var voices = new JapanVoicePack(runtimeRoot);
        using var player = new IntroPlayer();
        using var monitor = new MemoryMatchMonitor(dataRoot, player, voices);
        using var overlay = new SubtitleOverlay();
        using var form = new TrainerForm(monitor, overlay);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-10000, -10000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        string output = Path.GetFullPath(snapshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
        Console.WriteLine($"UI snapshot saved: {output}");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (args is ["--subtitle-snapshot", var subtitleSnapshotPath])
{
    try
    {
        ApplicationConfiguration.Initialize();
        using var overlay = new SubtitleOverlay { Opacity = 0.01, TransparencyKey = Color.Empty };
        _ = overlay.Handle;
        overlay.ShowCue(new MemoryMatchMonitor.SubtitleCue("跟随军旗！出发！", TimeSpan.FromSeconds(3), Commander: false));
        Application.DoEvents();
        using var bitmap = new Bitmap(overlay.Width, overlay.Height);
        overlay.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        string output = Path.GetFullPath(subtitleSnapshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        overlay.HideCue();
        Console.WriteLine($"Subtitle snapshot saved: {output}");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (args is ["--play-jp", var id])
{
    try
    {
        var line = new JapanVoicePack(runtimeRoot).ById(id);
        using var wav = new WaveFileReader(line.Path);
        using var player = new IntroPlayer();
        Console.WriteLine($"Playing {line.Id}: {line.Japanese}");
        player.Play(line.Path);
        Thread.Sleep(wav.TotalTime + TimeSpan.FromMilliseconds(250));
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
if (args is ["--play-order-test"])
{
    try
    {
        var voices = new JapanVoicePack(runtimeRoot);
        var line = voices.PickOrder();
        using var player = new IntroPlayer();
        Console.WriteLine($"Playing synthesized telegraph + {line.Id}: {line.Chinese}");
        TimeSpan duration = player.PlayWithTelegraph(line.Path, "... --- ...");
        Thread.Sleep(300);
        if (player.PlaybackState != PlaybackState.Playing)
            throw new InvalidOperationException("Order audio device stopped before the telegraph completed.");
        Console.WriteLine("Order audio device is actively playing PCM telegraph + voice.");
        Thread.Sleep(duration);
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (args is ["--play-hurt-test"])
{
    try
    {
        var voices = new JapanVoicePack(runtimeRoot);
        (string effect, JapanVoiceLine line) = voices.PickHurt(JapanVoicePack.InfantryPool);
        using var player = new IntroPlayer();
        Console.WriteLine($"Playing {Path.GetFileName(effect)} + {line.Id}: {line.Chinese}");
        TimeSpan duration = player.PlaySequence(effect, line.Path);
        Thread.Sleep(300);
        if (player.PlaybackState != PlaybackState.Playing)
            throw new InvalidOperationException("Hurt audio device stopped before the sequence completed.");
        Console.WriteLine("Hurt audio device is actively playing CC0 effect + Japanese hurt voice.");
        Thread.Sleep(duration);
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (args.Length > 0 && args is not ["--smoke"])
{
    Console.Error.WriteLine("Usage: dotnet run [--status|--self-test|--scan-kards-key|--probe-unreal|--find-namepool|--find-kards-objects|--find-kards-fields|--search-unreal-names text|--smoke|--voice-check|--ui-snapshot path|--subtitle-snapshot path|--play-jp ID|--play-order-test|--play-hurt-test|--analyze-image path|--replay-frames directory [repeat]|--record-visual seconds]");
    return 1;
}

if (args is ["--smoke"])
{
    try
    {
        var voices = new JapanVoicePack(runtimeRoot);
        using var player = new IntroPlayer();
        using var monitor = new MemoryMatchMonitor(dataRoot, player, voices);
        using var hotkey = new InputDesktopHotkey(monitor.Hotkey);
        hotkey.Start();
        monitor.Start();
        Console.WriteLine($"KardsVox started with {voices.Count} Japan voice clips.");
        Thread.Sleep(2000);
        hotkey.Stop();
        hotkey.WaitForExit();
        Console.WriteLine("Startup smoke test passed.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"KardsVox could not start: {ex.Message}");
        return 1;
    }
}

try
{
    using var singleInstance = new Mutex(true, "Local\\KardsVox-Japan-Voice-Module", out bool firstInstance);
    if (!firstInstance)
    {
        MessageBox.Show("KARDS VOX 已经在运行。", "KARDS VOX", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    ApplicationConfiguration.Initialize();
    var voices = new JapanVoicePack(runtimeRoot);
    using var player = new IntroPlayer();
    using var monitor = new MemoryMatchMonitor(dataRoot, player, voices);
    using var hotkey = new InputDesktopHotkey(monitor.Hotkey);
    using var overlay = new SubtitleOverlay();
    using var form = new TrainerForm(monitor, overlay);

    Action<MemoryMatchMonitor.SubtitleCue> showSubtitle = overlay.ShowCue;
    Action clearSubtitle = overlay.HideCue;
    monitor.SubtitleRequested += showSubtitle;
    monitor.SubtitleCleared += clearSubtitle;
    Task? startup = null;
    form.Shown += (_, _) =>
    {
        _ = overlay.Handle;
        startup = Task.Run(() =>
        {
            try
            {
                hotkey.Start();
                monitor.Start();
            }
            catch (Exception ex)
            {
                if (!form.IsDisposed)
                    form.BeginInvoke(() => MessageBox.Show(form, $"连接 KARDS 失败：\n{ex.Message}", "KARDS VOX",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning));
            }
        });
    };
    Application.Run(form);
    monitor.SubtitleRequested -= showSubtitle;
    monitor.SubtitleCleared -= clearSubtitle;
    if (startup is not null) startup.GetAwaiter().GetResult();
    hotkey.Stop();
    return 0;
}
catch (Exception ex)
{
    MessageBox.Show($"KARDS VOX 无法启动：\n{ex.Message}", "KARDS VOX", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return 1;
}

static string FindDataRoot(string runtimeRoot)
{
    if (File.Exists(Path.Combine(runtimeRoot, "Data", "Japan", "voice_lines.tsv")))
        return runtimeRoot;

    var directory = new DirectoryInfo(runtimeRoot);
    for (int i = 0; directory is not null && i < 6; i++, directory = directory.Parent)
    {
        if (directory.Name.Equals("KardsVox.build", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
    }
    return runtimeRoot;
}
