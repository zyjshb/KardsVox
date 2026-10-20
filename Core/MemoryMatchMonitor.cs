using KardsVox.Audio;

namespace KardsVox.Core;

/// <summary>
/// Listens to KARDS' own Unreal battle state and play-from-hand queue.
/// The process handle is read-only; this class never calls game functions or writes game memory.
/// </summary>
internal sealed class MemoryMatchMonitor : IDisposable
{
    public const string CommanderSubtitle = "同胞们，用各位的勇气与自信，为敌人带去恐惧吧，坚持战斗，直至最后一人！为敌人带去恐惧吧，屹立于此的勇士们啊！粉碎敌人的诡计阴谋吧！";
    private const int PlayedCardIdsOffset = 0x500;
    private const int PlayFromHandRunningOffset = 0x520;
    private const int PlayFromHandCardIdOffset = 0x524;
    private const int CurrentCardRefOffset = 0x3F8;
    private const int TemporaryCurrentCardOffset = 0x4F0;
    private const int PlayedFromEnemyOffset = 0x670;
    private const int PlayFromHandEnemyOffset = 0x680;
    private const int HqLeftOffset = 0x2C8;
    private const int HqRightOffset = 0x2D0;
    private const int SelfBaseCardRefOffset = 0x808;
    private const int EnterPlaySoundOffset = 0x340;
    private const int CardDefinitionIdOffset = 0x88;
    private const int CardSideOffset = 0x276;
    private const int CardCostOffset = 0x278;
    private const int CardDefenseOffset = 0x74;
    private const int CardLocationOffset = 0x3B0;
    private const int MyLocationCardOffset = 0x7E8;
    private const int EnemyLocationCardOffset = 0x7F0;
    private const int HandCardRefOffset = 0x9E8;
    private const int BoardBattleHudShownOffset = 0xE88;
    private const byte OwnHandLocation = 3;
    private const byte OwnBoardLocation = 5;
    private const byte EnemyBoardLocation = 6;

    private readonly object gate = new();
    private readonly string settingsPath;
    private readonly DetectionSettings settings;
    private readonly IntroPlayer player;
    private readonly JapanVoicePack voices;
    private readonly NationCardCatalog sovietCards;
    private readonly CancellationTokenSource stop = new();
    private readonly ManualResetEventSlim ready = new(false);
    private Thread? thread;
    private Thread? visualThread;
    private UnrealRuntimeReader? runtime;
    private long board;
    private long battleMonitor;
    private int lastObjectCount;
    private bool inMatch;
    private int monitoringTrueSamples;
    private int monitoringFalseSamples;
    private int previousQueueCount;
    private int lastAnimatedCardId;
    private bool playAnimationRunning;
    private bool playAnimationVoiceHandled;
    private readonly HashSet<int> currentQueueIds = [];
    private DateTimeOffset nextRefresh;
    private DateTimeOffset nextCardSnapshot;
    private DateTimeOffset nextHqCheck;
    private DateTimeOffset nextPendingResolve;
    private DateTimeOffset candidateMatchSince;
    private DateTimeOffset lastCardVoiceAt;
    private DateTimeOffset lastHurtVoiceAt;
    private string? lastError;
    private readonly List<PendingCard> pendingCards = [];
    private readonly Dictionary<long, CardDescription> knownHandCards = [];
    private readonly Dictionary<long, long> knownHandVisuals = [];
    private readonly Dictionary<long, CardDescription> knownBoardCards = [];
    private readonly Dictionary<long, CardDescription> visibleOwnHand = [];
    private readonly Dictionary<long, long> visibleOwnHandVisuals = [];
    private readonly Dictionary<long, CardDescription> visibleOwnBoard = [];
    private readonly Dictionary<long, CardDescription> visibleEnemyBoard = [];
    private readonly Dictionary<long, CardDescription> visibleBoard = [];
    private byte? ownSide;
    private bool tableReadyCached;
    private long visibleTableSignature;
    private long candidateMatchSignature;
    private long matchSignature;
    private long lastDirectHandCard;
    private bool hqLowPlayed;
    private bool hqCriticalPlayed;
    private bool enemyHqLowPlayed;
    private bool playPhaseActive;
    private bool visualOpeningArmed;
    private bool openingIntroPreplayed;
    private int visualNonBoardSamples;
    private DateTimeOffset matchBeganAt;
    private readonly Dictionary<string, DateTimeOffset> recentCardVoices = new(StringComparer.Ordinal);

    private sealed record PendingCard(int Id, DateTimeOffset Added);
    public sealed record SubtitleCue(string Text, TimeSpan Duration, bool Commander, bool Sequenced = false, string? Speaker = null);

    public event Action<SubtitleCue>? SubtitleRequested;
    public event Action? SubtitleCleared;

    public bool AutoEnabled
    {
        get { lock (gate) return AutoPlaybackEnabled; }
    }

    public string SelectedVoicePack
    {
        get { lock (gate) return settings.SelectedVoicePack; }
    }

    private bool AutoPlaybackEnabled => settings.AutoEnabled &&
        VoicePackRegistry.Get(settings.SelectedVoicePack).Playable;

    public void SetVoicePack(string id)
    {
        lock (gate)
        {
            string next = VoicePackRegistry.Get(id).Id;
            if (settings.SelectedVoicePack == next) return;
            player.Stop();
            SubtitleCleared?.Invoke();
            settings.SelectedVoicePack = next;
            settings.Save(settingsPath);
            Console.WriteLine($"Selected voice pack: {next}");
        }
    }

    public bool SubtitlesEnabled
    {
        get { lock (gate) return settings.SubtitlesEnabled; }
    }

    public bool Connected => runtime is not null;
    public bool InMatch => inMatch;

    public void SetAutoEnabled(bool enabled)
    {
        lock (gate)
        {
            if (!VoicePackRegistry.Get(settings.SelectedVoicePack).Playable) return;
            settings.AutoEnabled = enabled;
            settings.Save(settingsPath);
            Console.WriteLine($"Auto voice: {(enabled ? "ON" : "OFF")}");
        }
    }

    public void SetSubtitlesEnabled(bool enabled)
    {
        lock (gate)
        {
            settings.SubtitlesEnabled = enabled;
            settings.Save(settingsPath);
            Console.WriteLine($"Subtitles: {(enabled ? "ON" : "OFF")}");
        }
    }

    public MemoryMatchMonitor(string dataRoot, IntroPlayer player, JapanVoicePack voices)
    {
        settingsPath = Path.Combine(dataRoot, "Data", "detection.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        settings = DetectionSettings.Load(settingsPath);
        this.player = player;
        this.voices = voices;
        sovietCards = new NationCardCatalog(AppContext.BaseDirectory, VoicePackRegistry.Soviet, 319);
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "KardsVox read-only game monitor" };
        visualThread = new Thread(RunVisibleOpeningMonitor) { IsBackground = true, Name = "KardsVox visible opening monitor" };
        thread.Start();
        visualThread.Start();
        ready.Wait();
        Console.WriteLine($"Auto voice: {(AutoEnabled ? "ON" : "OFF")}; pack: {SelectedVoicePack}; source: KARDS read-only battle events");
    }

    public void Hotkey(VoxHotkey key)
    {
        lock (gate)
        {
            switch (key)
            {
                case VoxHotkey.Play:
                    if (VoicePackRegistry.Get(settings.SelectedVoicePack).Playable)
                        PlayCommanderIntro("F6 test");
                    break;
                case VoxHotkey.ToggleAuto:
                    SetAutoEnabled(!settings.AutoEnabled);
                    break;
                case VoxHotkey.Debug:
                    Console.WriteLine($"Runtime state: connected={runtime is not null}, inMatch={inMatch}, board=0x{board:X}, monitor=0x{battleMonitor:X}, queue={previousQueueCount}");
                    break;
                case VoxHotkey.CaptureBoard:
                case VoxHotkey.CaptureLobby:
                    Console.WriteLine("F7/F8 calibration is no longer needed; detection now uses read-only game events.");
                    break;
            }
        }
    }

    private void Run()
    {
        ready.Set();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int delayMilliseconds = 500;
                try
                {
                    lock (gate)
                    {
                        Tick();
                        delayMilliseconds = runtime is null ? 500 : 5;
                    }
                    lastError = null;
                }
                catch (Exception ex)
                {
                    if (lastError != ex.Message)
                    {
                        if (ex.Message.Contains("battle objects are not loaded", StringComparison.Ordinal))
                            Console.WriteLine("KARDS detected; waiting for an active battle table.");
                        else
                            Console.Error.WriteLine($"Read-only monitor reconnecting: {ex.Message}");
                    }
                    lastError = ex.Message;
                    Disconnect();
                    delayMilliseconds = 1000;
                }
                // The Blueprint play queue may live for only one rendered frame.
                // Poll slowly while waiting for KARDS, then read the two tiny play arrays at 5 ms.
                stop.Token.WaitHandle.WaitOne(delayMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Read-only monitor stopped: {ex.Message}");
        }
        finally
        {
            Disconnect();
            ready.Set();
        }
    }

    private void RunVisibleOpeningMonitor()
    {
        string? lastVisualError = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (GameWindowDetector.TryGetForegroundClient(out var client))
                {
                    using var screen = ScreenCapture.Client(client);
                    var evidence = BuiltInBoardDetector.Analyze(screen);
                    lock (gate) ObserveVisibleOpeningEdge(evidence);
                }
                lastVisualError = null;
            }
            catch (Exception ex)
            {
                if (lastVisualError != ex.Message)
                    Console.WriteLine($"Visible opening check skipped: {ex.Message}");
                lastVisualError = ex.Message;
            }
            stop.Token.WaitHandle.WaitOne(100);
        }
    }

    private void Connect()
    {
        runtime = new UnrealRuntimeReader();
        RefreshInstances(force: true);
        RefreshVisibleCards(force: true);
        Console.WriteLine($"Connected read-only to KARDS pid {runtime.ProcessId}; UObject count {runtime.ObjectCount:N0}.");
        bool boardReady = IsBoardReady();
        if (boardReady)
        {
            inMatch = true;
            matchBeganAt = DateTimeOffset.UtcNow;
            matchSignature = visibleTableSignature;
            SetOwnSideFromVisibleCards();
            BaselineVisualCards();
            if (IsOpeningHandReady() && AutoPlaybackEnabled)
            {
                Console.WriteLine("Attached during the opening hand; commander intro will play now.");
                openingIntroPreplayed = true;
                PlayCommanderIntro("Opening hand ready");
            }
            else
            {
                Console.WriteLine("Attached during an existing match; visual card baseline captured.");
            }
        }
    }

    private void Tick()
    {
        if (runtime is null)
        {
            if (!GameWindowDetector.Exists()) return;
            Connect();
        }

        UnrealRuntimeReader activeRuntime = runtime!;
        if (!activeRuntime.IsAlive)
        {
            Disconnect();
            return;
        }
        RefreshNewRuntimeObjects();
        if (DateTimeOffset.UtcNow >= nextRefresh ||
            (board != 0 && !activeRuntime.IsLiveInstance(board, activeRuntime.BoardClass)) ||
            (battleMonitor != 0 && !activeRuntime.IsLiveInstance(battleMonitor, activeRuntime.BattleMonitorClass)))
            RefreshInstances(force: false);

        bool cardSnapshotUpdated = RefreshVisibleCards(force: false);

        bool boardReady = IsBoardReady();
        if (boardReady)
        {
            if (candidateMatchSignature != visibleTableSignature)
            {
                candidateMatchSignature = visibleTableSignature;
                candidateMatchSince = DateTimeOffset.UtcNow;
            }
            monitoringTrueSamples++;
            monitoringFalseSamples = 0;
            if (!inMatch && monitoringTrueSamples >= 2 &&
                DateTimeOffset.UtcNow - candidateMatchSince >= TimeSpan.FromMilliseconds(200)) BeginMatch();
            else if (inMatch && matchSignature != 0 && visibleTableSignature != matchSignature &&
                     DateTimeOffset.UtcNow - candidateMatchSince >= TimeSpan.FromMilliseconds(200))
                BeginMatch();
        }
        else
        {
            candidateMatchSignature = 0;
            monitoringFalseSamples++;
            monitoringTrueSamples = 0;
            if (inMatch && monitoringFalseSamples >= 10) EndMatch();
        }

        if (inMatch && board != 0)
        {
            bool phaseJustBegan = UpdatePlayPhase();
            ObservePlayedCards();
            ObservePlayAnimation();
            ObserveDirectPlayRef();
            ObserveKnownHandDepartures();
            ResolvePendingCards();
            if (cardSnapshotUpdated && !phaseJustBegan)
            {
                ObserveVisualCards();
            }
            ObserveHeadquarters();
        }
    }

    private void RefreshInstances(bool force)
    {
        nextRefresh = DateTimeOffset.UtcNow.AddSeconds(inMatch ? 5 : 1);
        long oldBoard = board, oldMonitor = battleMonitor;
        long[] boards = runtime!.FindExactInstances(runtime.BoardClass)
            .Where(obj => !string.Equals(runtime.GetObjectName(obj), "Default__BP_Board_C", StringComparison.Ordinal))
            .ToArray();
        board = boards.FirstOrDefault(obj =>
            runtime.IsPointer(runtime.ReadPointer(obj + HqLeftOffset)) &&
            runtime.IsPointer(runtime.ReadPointer(obj + HqRightOffset)));
        if (board == 0) board = boards.FirstOrDefault();
        if (force || battleMonitor == 0 || !runtime.IsLiveInstance(battleMonitor, runtime.BattleMonitorClass))
        {
            battleMonitor = runtime.FindInstances(runtime.BattleMonitorClass)
                .FirstOrDefault(obj => !runtime.GetObjectName(obj)?.StartsWith("Default__", StringComparison.Ordinal) == true);
            if (battleMonitor == 0)
                battleMonitor = runtime.FindInstances(runtime.BattleMonitorClass)
                    .FirstOrDefault(obj => runtime.GetObjectName(obj) != "Default__BP_BattleStateMonitor_C");
        }
        lastObjectCount = runtime.ObjectCount;
        if (force || board != oldBoard || battleMonitor != oldMonitor)
        {
            tableReadyCached = false;
            nextCardSnapshot = DateTimeOffset.MinValue;
            Console.WriteLine($"Runtime objects: board=0x{board:X}; battle monitor=0x{battleMonitor:X}");
        }
    }

    private void RefreshNewRuntimeObjects()
    {
        if (runtime is null) return;
        int currentCount = runtime.ObjectCount;
        if (currentCount <= lastObjectCount)
        {
            if (currentCount < lastObjectCount) lastObjectCount = currentCount;
            return;
        }

        int firstNewIndex = Math.Max(0, lastObjectCount - 32);
        long newBoard = runtime.FindInstancesFromIndex(runtime.BoardClass, firstNewIndex)
            .Where(obj => runtime.GetObjectName(obj) != "Default__BP_Board_C")
            .LastOrDefault();
        long newMonitor = runtime.FindInstancesFromIndex(runtime.BattleMonitorClass, firstNewIndex)
            .Where(obj => runtime.GetObjectName(obj) != "Default__BP_BattleStateMonitor_C")
            .LastOrDefault();
        lastObjectCount = currentCount;

        if (newBoard != 0 && newBoard != board)
        {
            board = newBoard;
            tableReadyCached = false;
            nextCardSnapshot = DateTimeOffset.MinValue;
            monitoringTrueSamples = monitoringFalseSamples = 0;
            candidateMatchSignature = 0;
            Console.WriteLine($"New battle board discovered immediately: 0x{board:X}");
        }
        if (newMonitor != 0) battleMonitor = newMonitor;
    }

    private void BeginMatch()
    {
        bool replacingPreviousMatch = inMatch;
        inMatch = true;
        matchSignature = visibleTableSignature;
        previousQueueCount = 0;
        currentQueueIds.Clear();
        pendingCards.Clear();
        lastDirectHandCard = 0;
        lastAnimatedCardId = 0;
        playAnimationRunning = false;
        playAnimationVoiceHandled = false;
        playPhaseActive = false;
        matchBeganAt = DateTimeOffset.UtcNow;
        lastHurtVoiceAt = DateTimeOffset.MinValue;
        recentCardVoices.Clear();
        ownSide = null;
        hqLowPlayed = hqCriticalPlayed = enemyHqLowPlayed = false;
        SetOwnSideFromVisibleCards();
        BaselineVisualCards();
        Console.WriteLine(replacingPreviousMatch
            ? "MatchState: InMatch -> InMatch (new battle board replaced the previous match)"
            : "MatchState: OutsideMatch -> InMatch (active battle board and both HQ objects ready)");
        if (AutoPlaybackEnabled)
        {
            if (openingIntroPreplayed)
                Console.WriteLine("Opening hand confirmed; visible-screen intro was already started.");
            else
            {
                openingIntroPreplayed = true;
                PlayCommanderIntro("Opening hand ready");
            }
        }
    }

    private void EndMatch()
    {
        StopAllPlayback("Match ended");
        inMatch = false;
        matchSignature = 0;
        previousQueueCount = 0;
        currentQueueIds.Clear();
        pendingCards.Clear();
        lastDirectHandCard = 0;
        lastAnimatedCardId = 0;
        playAnimationRunning = false;
        playAnimationVoiceHandled = false;
        playPhaseActive = false;
        openingIntroPreplayed = false;
        visualOpeningArmed = false;
        visualNonBoardSamples = 0;
        matchBeganAt = DateTimeOffset.MinValue;
        lastHurtVoiceAt = DateTimeOffset.MinValue;
        recentCardVoices.Clear();
        knownHandCards.Clear();
        knownHandVisuals.Clear();
        knownBoardCards.Clear();
        visibleOwnHand.Clear();
        visibleOwnHandVisuals.Clear();
        visibleOwnBoard.Clear();
        visibleEnemyBoard.Clear();
        visibleBoard.Clear();
        Console.WriteLine("MatchState: InMatch -> MatchEnded (active battle board ended)");
    }

    private void ObserveVisibleOpeningEdge(BuiltInBoardDetector.Evidence evidence)
    {
        bool openingVisible = evidence.IsBoard && !evidence.IsPreMatch;
        if (!openingVisible)
        {
            if (++visualNonBoardSamples >= 2)
            {
                visualOpeningArmed = true;
                // KARDS can keep the old Board alive after surrender. A stable
                // loading/matching interval is still a real boundary, so reset
                // the one-shot without waiting for the old UObject to disappear.
                openingIntroPreplayed = false;
            }
            return;
        }

        visualNonBoardSamples = 0;
        if (!visualOpeningArmed || openingIntroPreplayed) return;
        visualOpeningArmed = false;
        if (!AutoPlaybackEnabled) return;

        openingIntroPreplayed = true;
        PlayCommanderIntro("Opening hand visible on screen");
    }

    private bool UpdatePlayPhase()
    {
        if (playPhaseActive || runtime is null || board == 0) return false;
        if (runtime.ReadByte(board + BoardBattleHudShownOffset) == 0) return false;

        playPhaseActive = true;
        // Discard the opening-hand baseline when KARDS changes from the mulligan
        // into live play. Otherwise a replaced order could look like it was used.
        BaselineVisualCards();
        Console.WriteLine($"Play phase active after {(DateTimeOffset.UtcNow - matchBeganAt).TotalSeconds:F1}s; order fallback armed.");
        return true;
    }

    private void ObservePlayedCards()
    {
        int[] ids = runtime!.ReadIntArray(board, PlayedCardIdsOffset);
        if (ids.Length == 0)
        {
            previousQueueCount = 0;
            currentQueueIds.Clear();
            return;
        }
        bool[] fromEnemy = runtime.ReadBoolArray(board, PlayedFromEnemyOffset);
        if (fromEnemy.Length < ids.Length) return; // Blueprint is still filling the parallel arrays this frame.

        var nextQueueIds = new HashSet<int>(ids);
        for (int i = 0; i < ids.Length; i++)
        {
            if (currentQueueIds.Contains(ids[i])) continue;
            if (fromEnemy[i])
            {
                Console.WriteLine($"Enemy card played: id={ids[i]} (ignored)");
                continue;
            }
            Console.WriteLine($"Own card played: id={ids[i]}");
            if (pendingCards.All(card => card.Id != ids[i]))
                pendingCards.Add(new PendingCard(ids[i], DateTimeOffset.UtcNow));
        }
        currentQueueIds.Clear();
        currentQueueIds.UnionWith(nextQueueIds);
        previousQueueCount = ids.Length;
    }

    private void ObservePlayAnimation()
    {
        bool running = runtime!.ReadByte(board + PlayFromHandRunningOffset) != 0;
        if (!running)
        {
            playAnimationRunning = false;
            playAnimationVoiceHandled = false;
            lastAnimatedCardId = 0;
            return;
        }

        int cardId = runtime.ReadInt(board + PlayFromHandCardIdOffset);
        bool newAction = !playAnimationRunning || (cardId != 0 && cardId != lastAnimatedCardId);
        if (newAction) playAnimationVoiceHandled = false;
        playAnimationRunning = true;
        if (cardId != 0) lastAnimatedCardId = cardId;

        bool fromEnemy = runtime.ReadByte(board + PlayFromHandEnemyOffset) != 0;
        if (fromEnemy)
        {
            if (newAction) Console.WriteLine($"Enemy play animation: id={cardId} (ignored)");
            return;
        }
        if (newAction) Console.WriteLine($"Own play animation: id={cardId}");
        if (cardId != 0 && pendingCards.All(card => card.Id != cardId))
            pendingCards.Add(new PendingCard(cardId, DateTimeOffset.UtcNow));

        // KARDS fills these references when its own play animation starts,
        // before the card reaches the table or the low-frequency snapshot sees
        // a hand change. Probe every 5 ms until the reference becomes valid.
        if (playPhaseActive && !playAnimationVoiceHandled)
            playAnimationVoiceHandled = TryPlayAnimationCardReference();
    }

    private bool TryPlayAnimationCardReference()
    {
        long[] references =
        [
            runtime!.ReadPointer(board + HandCardRefOffset),
            runtime.ReadPointer(board + CurrentCardRefOffset),
            runtime.ReadPointer(board + TemporaryCurrentCardOffset)
        ];

        foreach (long reference in references.Where(runtime.IsPointer).Distinct())
        {
            long resolved = ResolveCardRef(reference);
            long[] candidates = resolved == reference ? [reference] : [resolved, reference];
            foreach (long candidate in candidates.Where(runtime.IsPointer).Distinct())
            {
                if (knownHandCards.TryGetValue(candidate, out CardDescription? known))
                {
                    PlayVisualCard(known, "Play animation started");
                    return true;
                }

                CardDescription description = DescribeCard(candidate);
                if (!description.Identified || ownSide is not null && description.Side != ownSide.Value)
                    continue;
                if (description.SourceName?.Contains("card_", StringComparison.OrdinalIgnoreCase) != true &&
                    string.IsNullOrWhiteSpace(description.SoundName))
                    continue;

                PlayVisualCard(description, "Play animation started");
                return true;
            }
        }
        return false;
    }

    private void ObserveDirectPlayRef()
    {
        long handVisual = runtime!.ReadPointer(board + HandCardRefOffset);
        if (!runtime.IsPointer(handVisual))
        {
            lastDirectHandCard = 0;
            return;
        }

        long card = ResolveCardRef(handVisual);
        if (!runtime.IsPointer(card) || card == lastDirectHandCard) return;
        lastDirectHandCard = card;

        CardDescription description = DescribeCard(card);
        if (ownSide is not null && description.Side != ownSide.Value)
        {
            Console.WriteLine($"Enemy direct hand card ignored: side={description.Side}, source={description.SourceName}");
            return;
        }
        if (!description.Identified)
        {
            Console.WriteLine("Direct hand reference has no card identity; waiting for the hand/board snapshot.");
            return;
        }
        PlayVisualCard(description, "Game play-from-hand reference");
    }

    private void ObserveKnownHandDepartures()
    {
        if (!playPhaseActive || !tableReadyCached || runtime is null || knownHandVisuals.Count == 0) return;

        // The complete UObject scan is intentionally low frequency. These are only
        // the 4-10 visual objects captured in the local hand, so their CardLocation
        // can be sampled with the 5 ms play queue without measurable load. A unit
        // changes from hand (3) to board (5), while an order's visual is destroyed
        // and reads as 0. Both happen at the start of KARDS' own play animation.
        foreach (var pair in knownHandVisuals.ToArray())
        {
            long card = pair.Key;
            long visual = pair.Value;
            if (runtime.ReadByte(visual + CardLocationOffset) == OwnHandLocation) continue;
            if (!knownHandCards.TryGetValue(card, out CardDescription? description))
            {
                knownHandVisuals.Remove(card);
                continue;
            }

            PlayVisualCard(description, description.Category == JapanVoicePack.OrderPool
                ? "Order left hand at animation start"
                : "Unit left hand at animation start");
            return;
        }
    }

    private void ResolvePendingCards()
    {
        if (pendingCards.Count == 0) return;
        if (DateTimeOffset.UtcNow < nextPendingResolve) return;
        nextPendingResolve = DateTimeOffset.UtcNow.AddMilliseconds(100);
        for (int pendingIndex = pendingCards.Count - 1; pendingIndex >= 0; pendingIndex--)
        {
            PendingCard pending = pendingCards[pendingIndex];
            if (DateTimeOffset.UtcNow - pending.Added <= TimeSpan.FromSeconds(3)) continue;
            Console.WriteLine($"Play action {pending.Id} expired without a stable hand/board change; ignored instead of guessing a card.");
            pendingCards.RemoveAt(pendingIndex);
        }
    }

    private sealed record CardDescription(long Instance, string Category, string Event, byte Side, int Cost, int Defense,
        int DefinitionId, string? SoundName, string? SourceName, OrderVoiceProfile? OrderProfile, bool Identified);

    private string ClassifyCategory(params string?[] names)
    {
        string? catalogCategory = settings.SelectedVoicePack == VoicePackRegistry.Soviet
            ? sovietCards.Classify(names) : voices.ClassifyJapanCard(names);
        if (!string.IsNullOrWhiteSpace(catalogCategory)) return catalogCategory;
        string text = string.Join(' ', names.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
        if (text.Length == 0) return "";
        if (text.Contains("order") || text.Contains("event")) return JapanVoicePack.OrderPool;
        if (text.Contains("naval") || text.Contains("carrier") || text.Contains("battleship") || text.Contains("destroyer") || text.Contains("cruiser") || text.Contains("ijn_")) return JapanVoicePack.NavyPool;
        if (text.Contains("bomber") || text.Contains("fighter") || text.Contains("zero") || text.Contains("a6m") || text.Contains("invader") || text.Contains("aircraft")) return JapanVoicePack.AirPool;
        if (text.Contains("tank") || text.Contains("panzer") || text.Contains("t26") || text.Contains("t_26") || text.Contains("t-26") ||
            text.Contains("ha_go") || text.Contains("chi_ha") || text.Contains("chi_he") || text.Contains("chi_nu")) return JapanVoicePack.TankPool;
        if (text.Contains("katyusha") || text.Contains("artillery") || text.Contains("howitzer") || text.Contains("field_gun") ||
            text.Contains("mortar") || text.Contains("cannon") || text.Contains("_gun")) return JapanVoicePack.InfantryPool;
        if (text.Contains("infantry") || text.Contains("regiment") || text.Contains("brigade") || text.Contains("guards") || text.Contains("card_unit")) return JapanVoicePack.InfantryPool;
        return "";
    }

    private bool IsBoardReady()
    {
        if (runtime is null || board == 0)
        {
            tableReadyCached = false;
            visibleTableSignature = 0;
            return false;
        }

        long ownVisual = runtime.ReadPointer(board + MyLocationCardOffset);
        long enemyVisual = runtime.ReadPointer(board + EnemyLocationCardOffset);
        long ownHq = ResolveCardRef(ownVisual);
        long enemyHq = ResolveCardRef(enemyVisual);
        bool fullTable = runtime.IsPointer(ownVisual) && runtime.IsPointer(enemyVisual) &&
            runtime.ReadByte(ownVisual + CardLocationOffset) == OwnBoardLocation &&
            runtime.ReadByte(enemyVisual + CardLocationOffset) == EnemyBoardLocation &&
            runtime.IsPointer(ownHq) && runtime.IsPointer(enemyHq);

        // During the mulligan KARDS has already created both HQ wrappers and the
        // local hand, but one BP location-card reference can remain null until the
        // confirm animation finishes. Treat that exact object state as the start of
        // the match so the commander speaks on the card-selection screen.
        long leftHq = runtime.ReadPointer(board + HqLeftOffset);
        long rightHq = runtime.ReadPointer(board + HqRightOffset);
        int liveOpeningCards = visibleOwnHandVisuals.Count(pair =>
            runtime.ReadByte(pair.Value + CardLocationOffset) == OwnHandLocation &&
            ResolveCardRef(pair.Value) == pair.Key);
        bool openingObjects = runtime.IsPointer(leftHq) && runtime.IsPointer(rightHq) && liveOpeningCards >= 4;
        tableReadyCached = fullTable || openingObjects;
        long signatureLeft = runtime.IsPointer(ownHq) ? ownHq : leftHq;
        long signatureRight = runtime.IsPointer(enemyHq) ? enemyHq : rightHq;
        visibleTableSignature = tableReadyCached ? board ^ signatureLeft ^ long.RotateLeft(signatureRight, 17) : 0;
        return tableReadyCached;
    }

    private bool IsOpeningHandReady()
    {
        if (runtime is null || board == 0 || visibleOwnHand.Count < 4) return false;
        long ownVisual = runtime.ReadPointer(board + MyLocationCardOffset);
        return !runtime.IsPointer(ownVisual) || runtime.ReadByte(ownVisual + CardLocationOffset) != OwnBoardLocation;
    }

    private void SetOwnSideFromVisibleCards()
    {
        if (runtime is null) return;

        long ownHq = ResolveCardRef(runtime.ReadPointer(board + MyLocationCardOffset));
        if (runtime.IsPointer(ownHq))
        {
            ownSide = runtime.ReadByte(ownHq + CardSideOffset);
            return;
        }

        // CardLocation is assigned by KARDS itself: 3 is the local hand and 5 is
        // the local half of the board.  This is more reliable than Board_HQLeft,
        // whose value is a UI wrapper rather than a BP_BaseCard instance.
        foreach (var pair in visibleOwnHand)
        {
            ownSide = pair.Value.Side;
            return;
        }
        foreach (var pair in visibleOwnBoard)
        {
            ownSide = pair.Value.Side;
            return;
        }
    }

    private long ResolveCardRef(long visual)
    {
        if (runtime is null || !runtime.IsPointer(visual)) return 0;
        long card = runtime.ReadPointer(visual + SelfBaseCardRefOffset);
        return runtime.IsPointer(card) ? card : visual;
    }

    private CardDescription DescribeCard(long card)
    {
        long sound = runtime!.ReadPointer(card + EnterPlaySoundOffset);
        string? soundName = runtime.IsPointer(sound) ? runtime.GetObjectName(sound) : null;
        string? objectName = runtime.GetObjectName(card);
        string? source = !string.IsNullOrWhiteSpace(objectName) ? objectName : runtime.GetClassName(card);
        string? className = runtime.GetClassName(card);
        string category = ClassifyCategory(soundName, className, source);
        int defense = runtime.ReadInt(card + CardDefenseOffset);
        bool hasName = !string.IsNullOrWhiteSpace(source) || !string.IsNullOrWhiteSpace(className) ||
            !string.IsNullOrWhiteSpace(soundName);
        if (category.Length == 0)
            category = defense <= 0 || source?.Contains("event", StringComparison.OrdinalIgnoreCase) == true
                ? JapanVoicePack.OrderPool : JapanVoicePack.InfantryPool;
        bool identified = hasName || defense <= 0;
        OrderVoiceProfile? profile = category == JapanVoicePack.OrderPool
            ? voices.ResolveOrder(soundName, className, source) : null;
        return new CardDescription(card, category, profile?.Event ?? "DEPLOY",
            runtime.ReadByte(card + CardSideOffset), runtime.ReadInt(card + CardCostOffset), defense,
            runtime.ReadInt(card + CardDefinitionIdOffset), soundName, source, profile, identified);
    }

    private IEnumerable<(long Visual, long Card, CardDescription Description, byte Location)> ScanVisualCards(string className)
    {
        long? klass = runtime!.GetBlueprintClass(className);
        if (klass is null) yield break;
        foreach (long visual in runtime.FindInstances(klass.Value))
        {
            if (runtime.GetObjectName(visual)?.StartsWith("Default__", StringComparison.Ordinal) == true) continue;
            byte location = runtime.ReadByte(visual + CardLocationOffset);
            long card = ResolveCardRef(visual);
            if (!runtime.IsPointer(card)) continue;
            yield return (visual, card, DescribeCard(card), location);
        }
    }

    private bool RefreshVisibleCards(bool force)
    {
        if (runtime is null) return false;
        if (!force && DateTimeOffset.UtcNow < nextCardSnapshot) return false;
        nextCardSnapshot = DateTimeOffset.UtcNow.AddSeconds(1);

        visibleOwnHand.Clear();
        visibleOwnHandVisuals.Clear();
        visibleOwnBoard.Clear();
        visibleEnemyBoard.Clear();
        visibleBoard.Clear();
        foreach (var item in ScanVisualCards("BP_HandCard_C"))
            if (item.Location == OwnHandLocation)
            {
                visibleOwnHand[item.Card] = item.Description;
                visibleOwnHandVisuals[item.Card] = item.Visual;
            }
        foreach (var item in ScanVisualCards("BP_BoardCard_C"))
        {
            visibleBoard[item.Card] = item.Description;
            if (item.Location == OwnBoardLocation) visibleOwnBoard[item.Card] = item.Description;
            else if (item.Location == EnemyBoardLocation) visibleEnemyBoard[item.Card] = item.Description;
        }
        return true;
    }

    private void BaselineVisualCards()
    {
        knownHandCards.Clear();
        knownHandVisuals.Clear();
        knownBoardCards.Clear();
        if (runtime is null) return;
        RefreshVisibleCards(force: true);
        ownSide ??= visibleOwnHand.Values.FirstOrDefault()?.Side ?? visibleOwnBoard.Values.FirstOrDefault()?.Side;
        foreach (var pair in visibleOwnHand)
        {
            knownHandCards[pair.Key] = pair.Value;
            if (visibleOwnHandVisuals.TryGetValue(pair.Key, out long visual)) knownHandVisuals[pair.Key] = visual;
        }
        foreach (var pair in visibleOwnBoard) knownBoardCards[pair.Key] = pair.Value;
        Console.WriteLine($"Card baseline: side={ownSide}; hand={knownHandCards.Count}; board={knownBoardCards.Count}");
    }

    private void ObserveVisualCards()
    {
        var hands = visibleOwnHand;
        var boards = visibleOwnBoard;
        if (ownSide is null)
        {
            ownSide = hands.Values.FirstOrDefault()?.Side ?? boards.Values.FirstOrDefault()?.Side;
            if (ownSide is null) return;
        }
        List<CardDescription> removed = SemanticDifference(knownHandCards.Values, hands.Values);
        List<CardDescription> deployed = SemanticDifference(boards.Values, knownBoardCards.Values)
            .Where(card => card.SourceName?.Contains("card_location", StringComparison.OrdinalIgnoreCase) != true)
            .ToList();

        bool playedCard = false;
        PendingCard? action = pendingCards
            .Where(item => DateTimeOffset.UtcNow - item.Added <= TimeSpan.FromSeconds(3))
            .OrderBy(item => item.Added)
            .FirstOrDefault();
        if (action is not null)
        {
            // One game play animation represents exactly one played card. Prefer
            // a removed order; otherwise use the unit that actually appeared on
            // our board. This prevents a rebuilt hand UObject from producing a
            // second, unrelated voice for the same action.
            CardDescription? card = removed.FirstOrDefault(item => item.Category == JapanVoicePack.OrderPool && item.Identified)
                ?? deployed.FirstOrDefault(item => item.Identified)
                ?? removed.FirstOrDefault(item => item.Identified)
                ?? deployed.FirstOrDefault()
                ?? removed.FirstOrDefault();
            if (card is not null)
            {
                string reason = card.Category == JapanVoicePack.OrderPool
                    ? "Confirmed order play" : "Confirmed unit deployment";
                PlayVisualCard(card, reason);
                playedCard = true;
            }
        }
        else if (playPhaseActive)
        {
            // Some short order animations never remain in the Blueprint queues
            // for a complete 5 ms sample. A stable semantic removal is still
            // conclusive after the mulligan baseline has been reset.
            CardDescription? order = removed.FirstOrDefault(item =>
                item.Category == JapanVoicePack.OrderPool && item.Identified);
            CardDescription? unit = deployed.FirstOrDefault(item => item.Identified);
            CardDescription? card = order ?? unit;
            if (card is not null)
            {
                PlayVisualCard(card, order is not null
                    ? "Confirmed order removal (animation queue elapsed)"
                    : "Confirmed unit deployment (animation queue elapsed)");
                playedCard = true;
            }
        }

        if (!playedCard && playPhaseActive) ObserveDamagedUnits(boards);

        knownHandCards.Clear();
        knownHandVisuals.Clear();
        foreach (var pair in hands)
        {
            knownHandCards[pair.Key] = pair.Value;
            if (visibleOwnHandVisuals.TryGetValue(pair.Key, out long visual)) knownHandVisuals[pair.Key] = visual;
        }
        knownBoardCards.Clear();
        foreach (var pair in boards) knownBoardCards[pair.Key] = pair.Value;
    }

    private void ObserveDamagedUnits(Dictionary<long, CardDescription> current)
    {
        if (DateTimeOffset.UtcNow - lastHurtVoiceAt < TimeSpan.FromMilliseconds(900)) return;
        var usedPrevious = new HashSet<long>();
        var damaged = new List<(CardDescription Card, int Loss)>();
        foreach (var pair in current)
        {
            CardDescription after = pair.Value;
            if (after.SourceName?.Contains("card_location", StringComparison.OrdinalIgnoreCase) == true) continue;
            CardDescription? before = null;
            if (knownBoardCards.TryGetValue(pair.Key, out CardDescription? sameInstance))
            {
                before = sameInstance;
                usedPrevious.Add(pair.Key);
            }
            else
            {
                string key = StableCardKey(after);
                KeyValuePair<long, CardDescription> match = knownBoardCards
                    .Where(item => !usedPrevious.Contains(item.Key) && StableCardKey(item.Value) == key)
                    .OrderBy(item => Math.Abs(item.Value.Defense - after.Defense))
                    .FirstOrDefault();
                if (match.Key != 0)
                {
                    before = match.Value;
                    usedPrevious.Add(match.Key);
                }
            }
            if (before is not null && before.Defense > after.Defense && after.Defense > 0 &&
                JapanVoicePack.DeploymentPoolNames.Contains(after.Category, StringComparer.Ordinal))
                damaged.Add((after, before.Defense - after.Defense));
        }
        if (damaged.Count == 0) return;
        var hit = damaged.OrderByDescending(item => item.Loss).First();
        lastHurtVoiceAt = DateTimeOffset.UtcNow;
        if (AutoPlaybackEnabled)
            PlayHurt(hit.Card.Category, $"Own {hit.Card.Category} damaged (-{hit.Loss}, defense {hit.Card.Defense})");
    }

    private static List<CardDescription> SemanticDifference(
        IEnumerable<CardDescription> source, IEnumerable<CardDescription> subtract)
    {
        var remaining = subtract.GroupBy(StableCardKey)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var difference = new List<CardDescription>();
        foreach (CardDescription card in source)
        {
            string key = StableCardKey(card);
            if (remaining.TryGetValue(key, out int count) && count > 0)
            {
                remaining[key] = count - 1;
                continue;
            }
            difference.Add(card);
        }
        return difference;
    }

    private static string StableCardKey(CardDescription card)
    {
        if (!string.IsNullOrWhiteSpace(card.SourceName))
            return $"source:{card.SourceName.ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(card.SoundName))
            return $"sound:{card.SoundName.ToLowerInvariant()}:{card.Side}:{card.Cost}:{card.Defense}";
        return $"anonymous:{card.Category}:{card.Side}:{card.Cost}:{card.Defense}";
    }

    private void PlayVisualCard(CardDescription card, string reason)
    {
        Console.WriteLine($"{reason}: definition={card.DefinitionId}, cost={card.Cost}, defense={card.Defense}, original sound={card.SoundName ?? "none"}, pool={card.Category}, side={card.Side}, source={card.SourceName}");
        PendingCard? action = pendingCards.OrderBy(item => item.Added).FirstOrDefault();
        if (action is not null) pendingCards.Remove(action);
        knownHandCards.Remove(card.Instance);
        knownHandVisuals.Remove(card.Instance);
        if (AutoPlaybackEnabled) PlayDetectedCard(card, reason);
    }

    private void PlayDetectedCard(CardDescription card, string reason)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string identity = card.Instance != 0
            ? $"instance:{card.Instance:X}"
            : card.DefinitionId != 0
            ? $"id:{card.DefinitionId}"
            : $"source:{card.SourceName ?? card.SoundName ?? card.Event}";
        foreach (var stale in recentCardVoices.Where(pair => now - pair.Value > TimeSpan.FromSeconds(4)).ToArray())
            recentCardVoices.Remove(stale.Key);
        if (recentCardVoices.TryGetValue(identity, out DateTimeOffset previous) &&
            now - previous < TimeSpan.FromSeconds(2.5))
        {
            Console.WriteLine($"{reason}: duplicate card report ignored ({identity})");
            return;
        }
        recentCardVoices[identity] = now;
        PlayCard(card.Category, card.Event, reason, card.OrderProfile);
    }

    private void PlayCard(string category, string eventName, string reason, OrderVoiceProfile? orderProfile = null)
    {
        var now = DateTimeOffset.UtcNow;
        // Exact card-instance de-duplication happens in PlayDetectedCard. Keep
        // only a tiny guard here for anonymous Blueprint reports so two real
        // cards played in quick succession both receive a voice.
        if (now - lastCardVoiceAt < TimeSpan.FromMilliseconds(180)) return;
        lastCardVoiceAt = now;
        if (category == JapanVoicePack.OrderPool)
            PlayOrder(eventName, reason, orderProfile ?? OrderVoiceProfile.Generic);
        else if (JapanVoicePack.DeploymentPoolNames.Contains(category, StringComparer.Ordinal))
            PlayDeployment(category, reason);
        else
            Play(category, eventName, reason);
    }

    private void ObserveHeadquarters()
    {
        if (DateTimeOffset.UtcNow < nextHqCheck) return;
        nextHqCheck = DateTimeOffset.UtcNow.AddMilliseconds(250);
        long ownCard = ResolveCardRef(runtime!.ReadPointer(board + MyLocationCardOffset));
        long enemyCard = ResolveCardRef(runtime.ReadPointer(board + EnemyLocationCardOffset));
        if (!runtime.IsPointer(ownCard) || !runtime.IsPointer(enemyCard)) return;
        var own = (Side: runtime.ReadByte(ownCard + CardSideOffset), Defense: runtime.ReadInt(ownCard + CardDefenseOffset));
        var enemy = (Side: runtime.ReadByte(enemyCard + CardSideOffset), Defense: runtime.ReadInt(enemyCard + CardDefenseOffset));
        if (own.Defense <= 0 && enemy.Defense <= 0) return;

        if (!hqCriticalPlayed && own.Defense is > 0 and <= 5)
        {
            hqCriticalPlayed = true;
            if (AutoPlaybackEnabled) Play("CMD", "HQ_CRITICAL", $"Own HQ critical ({own.Defense})");
        }
        else if (!hqLowPlayed && own.Defense is > 5 and <= 10)
        {
            hqLowPlayed = true;
            if (AutoPlaybackEnabled) Play("CMD", "HQ_LOW", $"Own HQ low ({own.Defense})");
        }
        if (!enemyHqLowPlayed && enemy.Defense is > 0 and <= 5)
        {
            enemyHqLowPlayed = true;
            if (AutoPlaybackEnabled) Play("CMD", "ENEMY_HQ_LOW", $"Enemy HQ low ({enemy.Defense})");
        }
    }

    private void Play(string voice, string eventName, string reason)
    {
        if (!VoicePackRegistry.Get(settings.SelectedVoicePack).Playable) return;
        var line = voices.Pick(voice, eventName);
        TimeSpan duration = player.Play(line.Path);
        if (settings.SubtitlesEnabled)
            SubtitleRequested?.Invoke(new SubtitleCue(line.Chinese, duration, Commander: voice == "CMD",
                Speaker: voice == "CMD" ? "指挥官" : null));
        Console.WriteLine($"{reason}: playing {line.Id}");
    }

    private void PlayDeployment(string category, string reason)
    {
        if (!VoicePackRegistry.Get(settings.SelectedVoicePack).Playable) return;
        var line = voices.PickDeployment(category);
        TimeSpan duration = player.Play(line.Path);
        if (settings.SubtitlesEnabled)
            SubtitleRequested?.Invoke(new SubtitleCue(line.Chinese, duration, Commander: false,
                Speaker: CategorySpeaker(category)));
        Console.WriteLine($"{reason}: pool={category}, playing {line.Id}");
    }

    private void PlayOrder(string eventName, string reason, OrderVoiceProfile profile)
    {
        if (!VoicePackRegistry.Get(settings.SelectedVoicePack).Playable) return;
        var line = voices.PickOrder(eventName);
        TimeSpan duration = player.PlayWithTelegraph(line.Path, profile.Telegraph);
        string subtitle = profile == OrderVoiceProfile.Generic
            ? $"急电｜{line.Chinese}"
            : $"{profile.Name}｜{line.Chinese}";
        if (settings.SubtitlesEnabled)
            SubtitleRequested?.Invoke(new SubtitleCue(subtitle, duration, Commander: false, Speaker: "通信兵"));
        Console.WriteLine($"{reason}: order={profile.Name}, playing telegraph + {line.Id}");
    }

    private void PlayHurt(string category, string reason)
    {
        if (!VoicePackRegistry.Get(settings.SelectedVoicePack).Playable) return;
        (string effectPath, JapanVoiceLine line) = voices.PickHurt(category);
        TimeSpan duration = player.PlaySequence(effectPath, line.Path);
        if (settings.SubtitlesEnabled)
            SubtitleRequested?.Invoke(new SubtitleCue(line.Chinese, duration, Commander: false,
                Speaker: CategorySpeaker(category)));
        Console.WriteLine($"{reason}: hurt pool={category}, playing {Path.GetFileName(effectPath)} + {line.Id}");
    }

    private static string CategorySpeaker(string category) => category switch
    {
        JapanVoicePack.InfantryPool => "陆军部队",
        JapanVoicePack.TankPool => "装甲部队",
        JapanVoicePack.AirPool => "航空队",
        JapanVoicePack.NavyPool => "海军部队",
        _ => "前线部队"
    };

    private void PlayCommanderIntro(string reason)
    {
        if (!VoicePackRegistry.Get(settings.SelectedVoicePack).Playable) return;
        TimeSpan duration = player.Play(voices.CommanderIntroPath);
        if (settings.SubtitlesEnabled)
            SubtitleRequested?.Invoke(new SubtitleCue(CommanderSubtitle, duration, Commander: true, Sequenced: true, Speaker: "指挥官"));
        Console.WriteLine($"{reason}: playing commander_intro.wav");
    }

    private void Disconnect()
    {
        bool hadConnection = runtime is not null || inMatch;
        if (hadConnection) StopAllPlayback("KARDS disconnected");
        else
        {
            player.Stop();
            SubtitleCleared?.Invoke();
        }
        runtime?.Dispose();
        runtime = null;
        board = battleMonitor = 0;
        inMatch = false;
        tableReadyCached = false;
        visibleTableSignature = candidateMatchSignature = matchSignature = 0;
        lastObjectCount = 0;
        monitoringTrueSamples = monitoringFalseSamples = previousQueueCount = 0;
        currentQueueIds.Clear();
        pendingCards.Clear();
        lastDirectHandCard = 0;
        lastAnimatedCardId = 0;
        playAnimationRunning = false;
        playAnimationVoiceHandled = false;
        playPhaseActive = false;
        visualOpeningArmed = false;
        openingIntroPreplayed = false;
        visualNonBoardSamples = 0;
        matchBeganAt = DateTimeOffset.MinValue;
        lastHurtVoiceAt = DateTimeOffset.MinValue;
        recentCardVoices.Clear();
        knownHandCards.Clear();
        knownHandVisuals.Clear();
        knownBoardCards.Clear();
        visibleOwnHand.Clear();
        visibleOwnHandVisuals.Clear();
        visibleOwnBoard.Clear();
        visibleEnemyBoard.Clear();
        visibleBoard.Clear();
    }

    private void StopAllPlayback(string reason)
    {
        player.Stop();
        SubtitleCleared?.Invoke();
        Console.WriteLine($"{reason}: audio stopped and subtitle cleared");
    }

    public void Dispose()
    {
        stop.Cancel();
        thread?.Join();
        visualThread?.Join();
        Disconnect();
        ready.Dispose();
        stop.Dispose();
    }
}
