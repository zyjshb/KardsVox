using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using KardsVox.Audio;

namespace KardsVox.Core;

internal sealed partial class MemoryMatchMonitor : IDisposable
{
	private enum CombatReaction { None, Attack, Hurt }
	private sealed record PendingCard(int Id, DateTimeOffset Added);
	private sealed record PendingHurt(CardDescription Card, string Reason, DateTimeOffset Added);
	private sealed record PendingPresentation(SubtitleCue Cue, Func<TimeSpan>? StartAudio,
		bool ShowSubtitle, bool Combat, bool Transient, bool Intro, DateTimeOffset Added, string Reason,
		CombatReaction Reaction = CombatReaction.None);

	public sealed record SubtitleLine(string? Original, string Chinese);

	public sealed record SubtitleCue(string Text, TimeSpan Duration, bool Commander, bool Sequenced = false,
		string? Speaker = null, string? Original = null, IReadOnlyList<SubtitleLine>? Lines = null,
		IReadOnlyList<TimeSpan>? SegmentDurations = null, bool FadeIn = false);

	private sealed record CardDescription(long Instance, string Category, string Event, byte Side, int Cost, int Defense, int CardId, string? SoundName, string? SourceName, OrderVoiceProfile? OrderProfile, bool Identified, string? Nation);

	public const string CommanderSubtitle = "同胞们，用各位的勇气与自信，为敌人带去恐惧吧，坚持战斗，直至最后一人！为敌人带去恐惧吧，屹立于此的勇士们啊！粉碎敌人的诡计阴谋吧！";
	private static readonly SubtitleLine[] JapanCommanderLines =
	[
		new("諸君の勇気と自信で、敵に恐怖を与えよ。", "同胞们，用各位的勇气与自信，为敌人带去恐惧吧。"),
		new("最後の一人となるまで戦い抜け！", "坚持战斗，直至最后一人！"),
		new("ここに立つ勇士たちよ、敵に恐怖を与えよ！", "屹立于此的勇士们啊，让敌人心生恐惧！"),
		new("敵の謀略を打ち砕け！", "粉碎敌人的诡计阴谋吧！")
	];

	private const int PlayedCardIdsOffset = 1280;

	private const int PlayFromHandRunningOffset = 1312;

	private const int PlayFromHandCardIdOffset = 1316;

	private const int PlayedFromEnemyOffset = 1648;

	private const int PlayFromHandEnemyOffset = 1664;

	private const int HqLeftOffset = 712;

	private const int HqRightOffset = 720;

	private const int SelfBaseCardRefOffset = 2056;

	private const int EnterPlaySoundOffset = 832;

	private const int CardIdOffset = 612;

	private const int CardSideOffset = 630;

	private const int CardCostOffset = 128;

	private const int CardDefenseOffset = 116;

	private const int CardLocationOffset = 944;
	private const int CardModelLocationOffset = 629;
	private const int CardTypeOffset = 104;

	private const int MyLocationCardOffset = 2024;

	private const int EnemyLocationCardOffset = 2032;

	private const int HandCardRefOffset = 2536;

	private const int BoardBattleHudShownOffset = 3720;

	private const byte OwnHandLocation = 3;

	private const byte OwnBoardLocation = 5;
	// Verified from KARDS ECardLocationEnum: Hand_Right=4, Board_Frontline=7.
	// The frontline is shared; Card.Side determines which player owns a unit.
	private const byte FrontlineLocation = 7;

	private const byte EnemyBoardLocation = 6;
	private static readonly HashSet<string> JapaneseArtilleryCards = new(StringComparer.OrdinalIgnoreCase)
	{
		"card_unit_28cm_coasttal_howitzer", "card_unit_75mm_field_artillery",
		"card_unit_75mm_mountain_gun", "card_unit_type_1_37mm", "card_unit_type_4",
		"card_unit_type_88_aa_gun", "card_unit_type_92_105mm_field_gun",
		"card_unit_type_96_25mm_aa_gun"
	};

	private readonly object gate = new object();
	private readonly ConcurrentQueue<Action> commands = new();
	private DateTimeOffset nextPerformanceLog;
	private double maximumTickMs;
	private int sampledTicks;
	private long sampledReads;
	private bool initialRegistryBaseline;

	private readonly string settingsPath;

	private readonly DetectionSettings settings;

	private readonly IntroPlayer player;

	private readonly JapanVoicePack voices;

	private readonly NationCardCatalog sovietCards;
	private readonly NationCardCatalog japanCards;
	private readonly AlliedOrderCatalog alliedOrders;
	private readonly AlliedCardCatalog alliedCards;
	private readonly SovietSubtitlePack sovietSubtitles;
	private readonly Dictionary<string, NationSubtitlePack> nationPacks;
	private string? matchNation;

	private readonly CancellationTokenSource stop = new CancellationTokenSource();

	private readonly ManualResetEventSlim ready = new ManualResetEventSlim(initialState: false);

	private Thread? thread;

	private UnrealRuntimeReader? runtime;

	private long board;

	private long battleMonitor;

	private bool inMatch;

	private int monitoringTrueSamples;

	private int monitoringFalseSamples;

	private int previousQueueCount;

	private int lastAnimatedCardId;

	private bool playAnimationRunning;

	private readonly HashSet<int> currentQueueIds = new HashSet<int>();

	private DateTimeOffset nextRefresh;

	private DateTimeOffset nextCardSnapshot;

	private DateTimeOffset nextHqCheck;

	private DateTimeOffset nextPendingResolve;

	private DateTimeOffset candidateMatchSince;

	private DateTimeOffset reusedBoardOpeningSince;

	private DateTimeOffset lastCardVoiceAt;

	private DateTimeOffset lastHurtVoiceAt;

	private string? lastError;

	private readonly List<PendingCard> pendingCards = new List<PendingCard>();

	private readonly Dictionary<long, CardDescription> knownHandCards = new Dictionary<long, CardDescription>();
	private readonly Dictionary<long, (CardDescription Card, DateTimeOffset Seen)> deploymentCandidates = new();

	private readonly Dictionary<long, long> knownOrderHandVisuals = new Dictionary<long, long>();

	private readonly Dictionary<long, CardDescription> knownBoardCards = new Dictionary<long, CardDescription>();

	private readonly Dictionary<long, long> knownOwnBoardVisuals = new Dictionary<long, long>();
	private readonly Dictionary<long, byte> knownOwnBoardLocations = new Dictionary<long, byte>();
	private DateTimeOffset nextMovementSample;
	private readonly HashSet<int> consumedPlayIds = new();
	private int previousOrdersFlying;
	private int previousOrderAnimationId;

	private readonly HashSet<long> activeDamageVisuals = new HashSet<long>();
	private readonly Dictionary<long, long> lastAttackVisuals = new Dictionary<long, long>();
	private PendingHurt? pendingHurt;
	private readonly List<PendingPresentation> pendingPresentations = new List<PendingPresentation>();
	private readonly List<PendingPresentation> pendingReactions = new(2);
	private static readonly TimeSpan ReactionFade = TimeSpan.FromMilliseconds(180);
	private static readonly TimeSpan ReactionLifetime = TimeSpan.FromSeconds(3.5);
	private Func<DateTimeOffset> presentationNow = () => DateTimeOffset.UtcNow;
	private CombatReaction activeReaction;
	private DateTimeOffset activePresentationStarted;
	private TimeSpan activeAudioDuration;
	private DateTimeOffset? reactionSwitchAt;
	private PendingPresentation? reactionTransitionTarget;
	private DateTimeOffset reactionFadeUntil;
	private DateTimeOffset activePresentationUntil;
	private bool activePresentationTransient;
	private bool activePresentationIntro;
	private DateTimeOffset lastAttackCueAt;
	private bool routingVerification;

	private readonly Dictionary<long, CardDescription> visibleOwnHand = new Dictionary<long, CardDescription>();

	private readonly Dictionary<long, long> visibleOwnHandVisuals = new Dictionary<long, long>();

	private readonly Dictionary<long, CardDescription> visibleOwnBoard = new Dictionary<long, CardDescription>();

	private readonly Dictionary<long, long> visibleOwnBoardVisuals = new Dictionary<long, long>();
	private readonly Dictionary<long, byte> visibleOwnBoardLocations = new Dictionary<long, byte>();

	private readonly Dictionary<long, CardDescription> visibleEnemyBoard = new Dictionary<long, CardDescription>();

	private readonly Dictionary<long, CardDescription> visibleBoard = new Dictionary<long, CardDescription>();

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
	private bool introPending;
	private bool introCancelledForMatch;

	private DateTimeOffset matchBeganAt;

	private readonly Dictionary<string, DateTimeOffset> recentCardVoices = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

	public bool AutoEnabled => settings.AutoEnabled;

	public bool SubtitlesEnabled => settings.SubtitlesEnabled;

	public bool Connected => runtime != null;

	public string SelectedVoicePack
	{
		get => settings.SelectedVoicePack;
	}
	public string? DetectedNation => inMatch ? matchNation : null;
	public string PackDescription(string id)
	{
		if (!nationPacks.TryGetValue(id, out var pack)) return VoicePackRegistry.Get(id).Description;
		string subtitles = pack.Definition.Description.Split('·')[0].Trim();
		return pack.AudioCount == 0 ? subtitles + " · 语音待录制"
			: $"{subtitles} · {pack.AudioCount}/{pack.AllLines.Count()} 条配音";
	}

	private bool CuesEnabled => settings.AutoEnabled || settings.SubtitlesEnabled;

	public void SetVoicePack(string id)
	{
		if (Dispatch(() => SetVoicePack(id))) return;
		lock (gate)
		{
			string next = VoicePackRegistry.Get(id).Id;
			if (settings.SelectedVoicePack == next) return;
			settings.SelectedVoicePack = next;
			settings.Save(settingsPath);
			Console.WriteLine("F6 preview nation: " + next);
		}
	}

	public bool InMatch => inMatch;

	public event Action<SubtitleCue>? SubtitleRequested;
	public event Action<TimeSpan>? SubtitleTransitionRequested;

	public event Action? SubtitleCleared;

	public void SetAutoEnabled(bool enabled)
	{
		if (Dispatch(() => SetAutoEnabled(enabled))) return;
		lock (gate)
		{
			settings.AutoEnabled = enabled;
			settings.Save(settingsPath);
			Console.WriteLine("Nation voice audio: " + (enabled ? "ON" : "OFF"));
		}
	}

	public void SetSubtitlesEnabled(bool enabled)
	{
		if (Dispatch(() => SetSubtitlesEnabled(enabled))) return;
		lock (gate)
		{
			settings.SubtitlesEnabled = enabled;
			settings.Save(settingsPath);
			Console.WriteLine("Subtitles: " + (enabled ? "ON" : "OFF"));
		}
	}

	public MemoryMatchMonitor(string dataRoot, IntroPlayer player, JapanVoicePack voices)
	{
		settingsPath = Path.Combine(dataRoot, "Data", "detection.json");
		Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
		settings = DetectionSettings.Load(settingsPath);
		this.player = player;
		this.voices = voices;
		sovietCards = new NationCardCatalog(AppContext.BaseDirectory, VoicePackRegistry.Soviet, 319);
		japanCards = new NationCardCatalog(AppContext.BaseDirectory, VoicePackRegistry.Japan, 312);
		alliedOrders = new AlliedOrderCatalog(AppContext.BaseDirectory);
		alliedCards = new AlliedCardCatalog(AppContext.BaseDirectory);
		sovietSubtitles = new SovietSubtitlePack(AppContext.BaseDirectory);
		nationPacks = VoicePackRegistry.All.Where(pack => pack.FileBased)
			.ToDictionary(pack => pack.Id, pack => new NationSubtitlePack(AppContext.BaseDirectory, pack), StringComparer.OrdinalIgnoreCase);
		foreach (var pack in nationPacks.Values)
			Console.WriteLine($"{pack.Definition.Id} pack: {pack.Cards.Count} resources; audio ready={pack.AudioCount}, pending={pack.MissingCount}, invalid={pack.InvalidCount}");
	}

	public void Start()
	{
		thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "KardsVox read-only game monitor"
		};
		thread.Start();
		ready.Wait();
		Console.WriteLine("Nation voice audio: " + (AutoEnabled ? "ON" : "OFF") + "; automatic nation detection; source: KARDS read-only battle events");
	}

	public void Hotkey(VoxHotkey key)
	{
		if (Dispatch(() => Hotkey(key))) return;
		lock (gate)
		{
			switch (key)
			{
			case VoxHotkey.Play:
				PlayCommanderIntro("F6 test", settings.SelectedVoicePack, forcePreview: true);
				break;
			case VoxHotkey.ToggleAuto:
				SetAutoEnabled(!settings.AutoEnabled);
				break;
			case VoxHotkey.Debug:
				Console.WriteLine($"Runtime state: connected={runtime != null}, inMatch={inMatch}, board=0x{board:X}, monitor=0x{battleMonitor:X}, queue={previousQueueCount}");
				break;
			case VoxHotkey.CaptureBoard:
			case VoxHotkey.CaptureLobby:
				Console.WriteLine("F7/F8 calibration is no longer needed; detection now uses read-only game events.");
				break;
			}
		}
	}

	private bool Dispatch(Action action)
	{
		if (thread?.IsAlive != true || Thread.CurrentThread == thread) return false;
		commands.Enqueue(action);
		return true;
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
						while (commands.TryDequeue(out var command)) command();
						long started = Stopwatch.GetTimestamp();
						Tick();
						maximumTickMs = Math.Max(maximumTickMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
						sampledTicks++;
						if (runtime != null && DateTimeOffset.UtcNow >= nextPerformanceLog)
						{
							Console.WriteLine($"Monitor timing: ticks={sampledTicks}, max={maximumTickMs:F1}ms, reads={runtime.ReadCalls - sampledReads}, indexReady={runtime.IndexReady}");
							sampledReads = runtime.ReadCalls;
							sampledTicks = 0; maximumTickMs = 0;
							nextPerformanceLog = DateTimeOffset.UtcNow.AddSeconds(10);
						}
						delayMilliseconds = ((runtime == null) ? 500 : 5);
					}
					lastError = null;
				}
				catch (Exception ex)
				{
					if (lastError != ex.Message)
					{
						Console.Error.WriteLine("Read-only monitor reconnecting: " + ex.Message);
					}
					lastError = ex.Message;
					Disconnect();
				}
				stop.Token.WaitHandle.WaitOne(delayMilliseconds);
			}
		}
		catch (Exception ex2)
		{
			Console.Error.WriteLine("Read-only monitor stopped: " + ex2.Message);
		}
		finally
		{
			Disconnect();
			ready.Set();
		}
	}

	private void Connect()
	{
		runtime = new UnrealRuntimeReader { IncrementalInstances = true };
		initialRegistryBaseline = true;
		runtime.UpdateInstanceIndex();
		RefreshInstances(force: true);
		RefreshVisibleCards(force: true);
		Console.WriteLine($"Connected read-only to KARDS pid {runtime.ProcessId}; UObject count {runtime.ObjectCount:N0}.");
		if (IsBoardReady())
		{
			inMatch = true;
			matchBeganAt = DateTimeOffset.UtcNow;
			matchSignature = visibleTableSignature;
			ResetOpeningForMatch(CuesEnabled && runtime.ReadByte(board + BoardBattleHudShownOffset) == 0 && visibleOwnHand.Count >= 4);
			SetOwnSideFromVisibleCards();
			DetectMatchNation();
			BaselineVisualCards();
			Console.WriteLine("Attached during an existing match; visual card baseline captured.");
		}
	}

	private void Tick()
	{
		if (runtime == null)
		{
			if (!GameWindowDetector.Exists())
			{
				return;
			}
			Connect();
		}
		UnrealRuntimeReader activeRuntime = runtime;
		if (!activeRuntime.IsAlive)
		{
			Disconnect();
			return;
		}
		// Sample stable hand models before registry maintenance and visual refresh.
		if (inMatch && playPhaseActive) ObserveHandDeployments();
		activeRuntime.UpdateInstanceIndex();
		if (DateTimeOffset.UtcNow >= nextRefresh || (board != 0L && !activeRuntime.IsLiveInstance(board, activeRuntime.BoardClass)) || (battleMonitor != 0L && !activeRuntime.IsLiveInstance(battleMonitor, activeRuntime.BattleMonitorClass)))
		{
			RefreshInstances(force: false);
		}
		bool cardSnapshotUpdated = RefreshVisibleCards(force: false);
		if (initialRegistryBaseline && activeRuntime.IndexReady)
		{
			initialRegistryBaseline = false;
			if (inMatch) BaselineVisualCards();
			Console.WriteLine("Runtime registry baseline ready; incremental monitoring active.");
		}
		if (inMatch && matchNation == null && cardSnapshotUpdated) DetectMatchNation();
		if (IsBoardReady())
		{
			if (candidateMatchSignature != visibleTableSignature)
			{
				candidateMatchSignature = visibleTableSignature;
				candidateMatchSince = DateTimeOffset.UtcNow;
			}
			monitoringTrueSamples++;
			monitoringFalseSamples = 0;
			if (!inMatch && monitoringTrueSamples >= 2 && DateTimeOffset.UtcNow - candidateMatchSince >= TimeSpan.FromMilliseconds(200.0))
			{
				BeginMatch();
			}
			else if (inMatch && matchSignature != 0L && visibleTableSignature != matchSignature && DateTimeOffset.UtcNow - candidateMatchSince >= TimeSpan.FromMilliseconds(200.0))
			{
				BeginMatch();
			}
		}
		else
		{
			candidateMatchSignature = 0L;
			monitoringFalseSamples++;
			monitoringTrueSamples = 0;
			if (inMatch && monitoringFalseSamples >= 10)
			{
				EndMatch();
			}
		}
		if (inMatch && board != 0L)
		{
			if (DetectReusedBoardOpening())
			{
				return;
			}
			bool phaseJustBegan = UpdatePlayPhase();
			ObservePlayedCards();
			ObservePlayAnimation();
			ObserveOrderAnimations();
			ObserveDirectPlayRef();
			ResolvePendingCards();
			if (cardSnapshotUpdated && !phaseJustBegan)
			{
				ObserveVisualCards();
			}
			ObserveMovementAnimations();
			ObserveAttackAnimations();
			ObserveDamageAnimations();
			FlushPendingHurt();
			ObserveHeadquarters();
			RunQueuedPresentations();
		}
	}

	private void RefreshInstances(bool force)
	{
		nextRefresh = DateTimeOffset.UtcNow.AddMilliseconds(250);
		long oldBoard = board;
		long oldMonitor = battleMonitor;
		long[] boards = (from obj in runtime.FindInstances(runtime.BoardClass)
			where !string.Equals(runtime.GetObjectName(obj), "Default__BP_Board_C", StringComparison.Ordinal)
			select obj).ToArray();
		board = boards.FirstOrDefault((long obj) => runtime.IsPointer(runtime.ReadPointer(obj + 712)) && runtime.IsPointer(runtime.ReadPointer(obj + 720)));
		if (board == 0L)
		{
			board = boards.FirstOrDefault();
		}
		battleMonitor = runtime.FindInstances(runtime.BattleMonitorClass).FirstOrDefault(delegate(long obj)
		{
			string? objectName = runtime.GetObjectName(obj);
			return objectName != null && !objectName.StartsWith("Default__", StringComparison.Ordinal);
		});
		if (battleMonitor == 0L)
		{
			battleMonitor = runtime.FindInstances(runtime.BattleMonitorClass).FirstOrDefault((long obj) => runtime.GetObjectName(obj) != "Default__BP_BattleStateMonitor_C");
		}
		if (force || board != oldBoard || battleMonitor != oldMonitor)
		{
			tableReadyCached = false;
			nextCardSnapshot = DateTimeOffset.MinValue;
			Console.WriteLine($"Runtime objects: board=0x{board:X}; battle monitor=0x{battleMonitor:X}");
		}
	}

	private void BeginMatch()
	{
		deploymentCandidates.Clear();
		StopAllPlayback("New match");
		inMatch = true;
		matchSignature = visibleTableSignature;
		previousQueueCount = 0;
		currentQueueIds.Clear();
		pendingCards.Clear();
		consumedPlayIds.Clear();
		lastDirectHandCard = 0L;
		lastAnimatedCardId = 0;
		playAnimationRunning = false;
		playPhaseActive = false;
		ResetOpeningForMatch(CuesEnabled && runtime.ReadByte(board + BoardBattleHudShownOffset) == 0);
		matchBeganAt = DateTimeOffset.UtcNow;
		lastHurtVoiceAt = DateTimeOffset.MinValue;
		reusedBoardOpeningSince = DateTimeOffset.MinValue;
		recentCardVoices.Clear();
		ownSide = null;
		matchNation = null;
		hqLowPlayed = (hqCriticalPlayed = (enemyHqLowPlayed = false));
		SetOwnSideFromVisibleCards();
		DetectMatchNation();
		BaselineVisualCards();
		Console.WriteLine("MatchState: OutsideMatch -> InMatch (active battle board and both HQ objects ready)");
	}

	private bool DetectReusedBoardOpening()
	{
		if (!playPhaseActive || runtime == null || runtime.ReadByte(board + BoardBattleHudShownOffset) != 0 ||
			visibleOwnHand.Count < 4)
		{
			reusedBoardOpeningSince = DateTimeOffset.MinValue;
			return false;
		}
		if (reusedBoardOpeningSince == DateTimeOffset.MinValue)
		{
			reusedBoardOpeningSince = DateTimeOffset.UtcNow;
			return false;
		}
		if (DateTimeOffset.UtcNow - reusedBoardOpeningSince < TimeSpan.FromMilliseconds(250))
		{
			return false;
		}
		Console.WriteLine("New opening hand detected on a reused battle board.");
		inMatch = false;
		BeginMatch();
		return true;
	}

	private void EndMatch()
	{
		deploymentCandidates.Clear();
		StopAllPlayback("Match ended");
		inMatch = false;
		matchSignature = 0L;
		previousQueueCount = 0;
		currentQueueIds.Clear();
		pendingCards.Clear();
		consumedPlayIds.Clear();
		lastDirectHandCard = 0L;
		lastAnimatedCardId = 0;
		playAnimationRunning = false;
		playPhaseActive = false;
		introPending = false;
		matchBeganAt = DateTimeOffset.MinValue;
		lastHurtVoiceAt = DateTimeOffset.MinValue;
		reusedBoardOpeningSince = DateTimeOffset.MinValue;
		recentCardVoices.Clear();
		knownHandCards.Clear();
		knownOrderHandVisuals.Clear();
		knownBoardCards.Clear();
		knownOwnBoardVisuals.Clear();
		knownOwnBoardLocations.Clear();
		activeDamageVisuals.Clear();
		lastAttackVisuals.Clear();
		pendingHurt = null;
		visibleOwnHand.Clear();
		visibleOwnHandVisuals.Clear();
		visibleOwnBoard.Clear();
		visibleOwnBoardVisuals.Clear();
		visibleOwnBoardLocations.Clear();
		visibleEnemyBoard.Clear();
		visibleBoard.Clear();
		matchNation = null;
		Console.WriteLine("MatchState: InMatch -> MatchEnded (active battle board ended)");
	}

	private bool UpdatePlayPhase()
	{
		if (playPhaseActive || runtime == null || board == 0L)
		{
			return false;
		}
		if (runtime.ReadByte(board + 3720) == 0)
		{
			return false;
		}
		playPhaseActive = true;
		BaselineVisualCards();
		Console.WriteLine($"Play phase active after {(DateTimeOffset.UtcNow - matchBeganAt).TotalSeconds:F1}s; order fallback armed.");
		return true;
	}

	private void ObserveHandDeployments()
	{
		foreach (var candidate in deploymentCandidates.ToArray())
		{
			CardDescription before = candidate.Value.Card;
			if (DateTimeOffset.UtcNow - candidate.Value.Seen > TimeSpan.FromSeconds(2))
			{
				deploymentCandidates.Remove(candidate.Key);
				continue;
			}
			if (!IsSupportedUnit(before) || consumedPlayIds.Contains(before.CardId)) continue;
			byte location = runtime.ReadByte(before.Instance + CardModelLocationOffset);
			if (!IsOwnBoardLocation(location, before.Side)) continue;
			ConfirmHandDeployment(before, DescribeCard(before.Instance), location);
		}
	}

	private void ConfirmHandDeployment(CardDescription before, CardDescription after, byte location)
	{
		// A disappearing visual alone is insufficient. Require the same identified
		// card model and ID, our side, and a real transition into a friendly board row.
		if (!playPhaseActive || before.Instance != after.Instance || before.CardId != after.CardId ||
			before.Side != after.Side || before.SourceName != after.SourceName || !after.Identified ||
			!IsOwnBoardLocation(location, after.Side) || !IsSupportedUnit(after)) return;
		RegisterPlay(after.CardId, "Own hand-to-board transition");
		PlayVisualCard(after, "Confirmed unit deployment");
	}

	private void ObservePlayedCards()
	{
		int[] ids = runtime.ReadIntArray(board, 1280);
		if (ids.Length == 0)
		{
			previousQueueCount = 0;
			currentQueueIds.Clear();
			return;
		}
		bool[] fromEnemy = runtime.ReadBoolArray(board, 1648);
		if (fromEnemy.Length < ids.Length)
		{
			return;
		}
		HashSet<int> nextQueueIds = new HashSet<int>(ids);
		int i;
		for (i = 0; i < ids.Length; i++)
		{
			if (currentQueueIds.Contains(ids[i]))
			{
				continue;
			}
			if (fromEnemy[i])
			{
				Console.WriteLine($"Enemy card played: id={ids[i]} (ignored)");
				continue;
			}
			Console.WriteLine($"Own card played: id={ids[i]}");
			RegisterPlay(ids[i], "Own queued play");
		}
		currentQueueIds.Clear();
		currentQueueIds.UnionWith(nextQueueIds);
		previousQueueCount = ids.Length;
	}

	private void ObservePlayAnimation()
	{
		if (runtime.ReadByte(board + 1312) == 0)
		{
			playAnimationRunning = false;
			lastAnimatedCardId = 0;
			return;
		}
		int cardId = runtime.ReadInt(board + 1316);
		if (cardId == 0 || (playAnimationRunning && cardId == lastAnimatedCardId))
		{
			return;
		}
		playAnimationRunning = true;
		lastAnimatedCardId = cardId;
		if (runtime.ReadByte(board + 1664) != 0)
		{
			Console.WriteLine($"Enemy play animation: id={cardId} (ignored)");
			return;
		}
		Console.WriteLine($"Own play animation: id={cardId}");
		RegisterPlay(cardId, "Own unit animation");
	}

	private void ObserveDirectPlayRef()
	{
		if (!playPhaseActive || runtime == null || pendingCards.Count == 0) return;
		long visual = runtime.ReadPointer(board + HandCardRefOffset);
		long card = ResolveCardRef(visual);
		if (runtime.IsPointer(card))
			PlayVisualCard(DescribeCard(card), "Matching play-from-hand reference");
	}

	private void ObserveOrderAnimations()
	{
		int flying = runtime.ReadInt(board + 1916);
		int id = runtime.ReadInt(board + PlayFromHandCardIdOffset);
		ObserveOrderAnimationState(flying, id, runtime.ReadByte(board + PlayFromHandEnemyOffset) != 0);
	}

	private void ObserveOrderAnimationState(int flying, int id, bool enemy)
	{
		bool newOrder = flying > 0 && (flying > previousOrdersFlying || id != previousOrderAnimationId);
		previousOrdersFlying = flying;
		previousOrderAnimationId = id;
		if (playPhaseActive && newOrder && !enemy)
			RegisterPlay(id, "Own order animation");
	}

	private void RegisterPlay(int id, string reason)
	{
		if (!playPhaseActive || id <= 0 || consumedPlayIds.Contains(id) || pendingCards.Any(p => p.Id == id)) return;
		// The own-player action is already confirmed. End the opening even if
		// resolving this card or loading its nation/voice must wait until later.
		CancelOpeningForCard(reason);
		pendingCards.Add(new PendingCard(id, DateTimeOffset.UtcNow));
		Console.WriteLine($"{reason}: id={id}");
	}

	private void ResolvePendingCards()
	{
		if (runtime == null || pendingCards.Count == 0) return;
		// Use the exact CardID from the game's action. A disappearing hand card,
		// a stale HandCardRef or an unrelated new unit cannot consume this action.
		foreach (PendingCard action in pendingCards.ToArray())
		{
			if (DateTimeOffset.UtcNow - action.Added > TimeSpan.FromSeconds(2))
			{
				Console.WriteLine($"Play action {action.Id} expired without a matching card identity.");
				pendingCards.Remove(action);
				continue;
			}
			CardDescription? card = knownHandCards.Values.Concat(visibleOwnHand.Values)
				.Concat(visibleBoard.Values).FirstOrDefault(c => c.CardId == action.Id && c.Side == ownSide);
			if (card != null)
			{
				CardDescription fresh = DescribeCard(card.Instance);
				if (fresh.CardId == action.Id) PlayVisualCard(fresh, "Confirmed game card ID");
			}
		}
	}


	private string ClassifyCategory(params string?[] names)
	{
		string? nation = IdentifyNation(names);
		string? catalogCategory = nation == VoicePackRegistry.Soviet
			? sovietCards.Classify(names) : nation == VoicePackRegistry.Japan ? japanCards.Classify(names) : null;
		if (nation != null && nationPacks.TryGetValue(nation, out var pack))
			catalogCategory = pack.Cards.Lookup(names);
		if (!string.IsNullOrWhiteSpace(catalogCategory))
		{
			return catalogCategory;
		}
		if (alliedCards.TryGet(out AlliedCardLabel allied, names))
			return allied.Kind switch { "EVENT" => "ORDER", "UNIT" => "UNIT", "LOCATION" => "LOCATION", _ => "" };
		string text = string.Join(' ', names.Where((string value) => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
		if (text.Length == 0)
		{
			return "";
		}
		if (text.Contains("order") || text.Contains("event"))
		{
			return "ORDER";
		}
		if (text.Contains("naval") || text.Contains("carrier") || text.Contains("battleship") || text.Contains("destroyer") || text.Contains("cruiser") || text.Contains("ijn_"))
		{
			return "NAVY";
		}
		if (text.Contains("bomber") || text.Contains("fighter") || text.Contains("zero") || text.Contains("a6m") || text.Contains("invader") || text.Contains("aircraft"))
		{
			return "AIR";
		}
		if (text.Contains("tank") || text.Contains("panzer") || text.Contains("t26") || text.Contains("t_26") || text.Contains("t-26") || text.Contains("ha_go") || text.Contains("chi_ha") || text.Contains("chi_he") || text.Contains("chi_nu"))
		{
			return "TANK";
		}
		if (text.Contains("katyusha") || text.Contains("artillery") || text.Contains("howitzer") || text.Contains("field_gun") || text.Contains("mortar") || text.Contains("cannon") || text.Contains("_gun"))
		{
			return nation == VoicePackRegistry.Soviet ? "ARTILLERY" : "INFANTRY";
		}
		if (nation == VoicePackRegistry.Soviet && text.Contains("cavalry"))
		{
			return "CAVALRY";
		}
		if (text.Contains("infantry") || text.Contains("regiment") || text.Contains("brigade") || text.Contains("guards") || text.Contains("card_unit"))
		{
			return "INFANTRY";
		}
		return "";
	}

	private static string RuntimeCategory(byte type, string catalogCategory) => type switch
	{
		1 => "LOCATION", 2 or 11 => "ORDER", 4 or 5 => "AIR",
		7 or 8 or 9 => "ARTILLERY", 3 or 10 => "TANK",
		6 => catalogCategory is "CAVALRY" or "NAVY" ? catalogCategory : "INFANTRY",
		_ => catalogCategory
	};

	private string? IdentifyNation(params string?[] names)
	{
		foreach (string? name in names)
		{
			if (sovietCards.Contains(name)) return VoicePackRegistry.Soviet;
			if (japanCards.Contains(name)) return VoicePackRegistry.Japan;
			foreach (var pack in nationPacks.Values)
				if (pack.Cards.Contains(name)) return pack.Definition.Id;
			if (alliedCards.TryGet(out AlliedCardLabel allied, name)) return allied.Origin;
		}
		return null;
	}

	private bool IsSupportedUnit(CardDescription card) => card.Nation switch
	{
		VoicePackRegistry.Japan => JapanVoicePack.DeploymentPoolNames.Contains(card.Category, StringComparer.Ordinal),
		VoicePackRegistry.Soviet => card.Category is "INFANTRY" or "TANK" or "AIR" or "ARTILLERY" or "CAVALRY" or "NAVY",
		null => false,
		_ when nationPacks.TryGetValue(card.Nation, out var pack) => pack.Supports(card.Category),
		_ => alliedCards.TryGet(out AlliedCardLabel label, card.SourceName, card.SoundName) &&
			label.Kind == "UNIT" && (!string.IsNullOrWhiteSpace(label.OriginalTitle) || !string.IsNullOrWhiteSpace(label.ChineseTitle))
	};

	private void DetectMatchNation()
	{
		if (runtime == null || board == 0L) return;
		long ownHq = ResolveCardRef(runtime.ReadPointer(board + MyLocationCardOffset));
		string? nation = runtime.IsPointer(ownHq)
			? IdentifyNation(runtime.GetObjectName(ownHq), runtime.GetClassName(ownHq)) : null;
		// An ally's hand may initially contain only one nation's cards. The HQ is
		// the sole reliable source of the main nation in a mixed deck.
		if (nation != matchNation && nation != null)
		{
			matchNation = nation;
			Console.WriteLine("Detected own nation: " + nation);
			if (inMatch && introPending)
			{
				introPending = false;
				if (lastCardVoiceAt < matchBeganAt)
					PlayCommanderIntro("Opening hand ready");
			}
		}
	}

	private bool IsBoardReady()
	{
		if (runtime == null || board == 0L)
		{
			tableReadyCached = false;
			visibleTableSignature = 0L;
			return false;
		}
		long ownVisual = runtime.ReadPointer(board + 2024);
		long enemyVisual = runtime.ReadPointer(board + 2032);
		long ownHq = ResolveCardRef(ownVisual);
		long enemyHq = ResolveCardRef(enemyVisual);
		tableReadyCached = runtime.IsPointer(ownVisual) && runtime.IsPointer(enemyVisual) && runtime.ReadByte(ownVisual + 944) == 5 && runtime.ReadByte(enemyVisual + 944) == 6 && runtime.IsPointer(ownHq) && runtime.IsPointer(enemyHq);
		visibleTableSignature = (tableReadyCached ? (board ^ ownHq ^ long.RotateLeft(enemyHq, 17)) : 0);
		return tableReadyCached;
	}

	private void SetOwnSideFromVisibleCards()
	{
		if (runtime == null)
		{
			return;
		}
		long ownHq = ResolveCardRef(runtime.ReadPointer(board + 2024));
		if (runtime.IsPointer(ownHq))
		{
			ownSide = runtime.ReadByte(ownHq + 630);
			return;
		}
		using (Dictionary<long, CardDescription>.Enumerator enumerator = visibleOwnHand.GetEnumerator())
		{
			if (enumerator.MoveNext())
			{
				ownSide = enumerator.Current.Value.Side;
				return;
			}
		}
		using Dictionary<long, CardDescription>.Enumerator enumerator2 = visibleOwnBoard.GetEnumerator();
		if (enumerator2.MoveNext())
		{
			ownSide = enumerator2.Current.Value.Side;
		}
	}

	private long ResolveCardRef(long visual)
	{
		if (runtime == null || !runtime.IsPointer(visual))
		{
			return 0L;
		}
		long card = runtime.ReadPointer(visual + 2056);
		if (!runtime.IsPointer(card))
		{
			return visual;
		}
		return card;
	}

	private string? ResolveCardSource(string? objectName, string? className)
	{
		bool known(string? name) => sovietCards.Contains(name) || japanCards.Contains(name) ||
			nationPacks.Values.Any(pack => pack.Cards.Contains(name)) ||
			alliedCards.TryGet(out _, name) || alliedOrders.TryGet(out _, name);
		return known(objectName) ? objectName : known(className) ? className
			: !string.IsNullOrWhiteSpace(objectName) ? objectName : className;
	}

	private CardDescription DescribeCard(long card)
	{
		long sound = runtime.ReadPointer(card + 832);
		string soundName = (runtime.IsPointer(sound) ? runtime.GetObjectName(sound) : null);
		string objectName = runtime.GetObjectName(card);
		string className = runtime.GetClassName(card);
		string? source = ResolveCardSource(objectName, className);
		string? nation = IdentifyNation(source, className, soundName);
		string category = ClassifyCategory(source, className, soundName);
		category = RuntimeCategory(runtime.ReadByte(card + CardTypeOffset), category);
		int defense = runtime.ReadInt(card + CardDefenseOffset);
		bool num = !string.IsNullOrWhiteSpace(source) || !string.IsNullOrWhiteSpace(className) || !string.IsNullOrWhiteSpace(soundName);
		if (category.Length == 0)
		{
			category = ((defense <= 0 || (source != null && source.Contains("event", StringComparison.OrdinalIgnoreCase))) ? "ORDER" : "INFANTRY");
		}
		bool identified = num || defense <= 0;
		OrderVoiceProfile profile = ((category == "ORDER" && nation == VoicePackRegistry.Japan) ? voices.ResolveOrder(soundName, className, source) : null);
		return new CardDescription(card, category, profile?.Event ?? "DEPLOY", runtime.ReadByte(card + 630), runtime.ReadInt(card + CardCostOffset), defense, runtime.ReadInt(card + CardIdOffset), soundName, source, profile, identified, nation);
	}

	private IEnumerable<(long Visual, long Card, CardDescription Description, byte Location)> ScanVisualCards(string className)
	{
		long? klass = runtime.GetBlueprintClass(className);
		if (!klass.HasValue)
		{
			yield break;
		}
		foreach (long visual in runtime.FindInstances(klass.Value))
		{
			if (!(runtime.GetObjectName(visual)?.StartsWith("Default__", StringComparison.Ordinal) ?? false))
			{
				byte location = runtime.ReadByte(visual + 944);
				long card = ResolveCardRef(visual);
				if (runtime.IsPointer(card))
				{
					yield return (Visual: visual, Card: card, Description: DescribeCard(card), Location: location);
				}
			}
		}
	}

	private bool RefreshVisibleCards(bool force)
	{
		if (runtime == null)
		{
			return false;
		}
		if (!force && DateTimeOffset.UtcNow < nextCardSnapshot)
		{
			return false;
		}
		nextCardSnapshot = DateTimeOffset.UtcNow.AddMilliseconds(200);
		visibleOwnHand.Clear();
		visibleOwnHandVisuals.Clear();
		visibleOwnBoard.Clear();
		visibleOwnBoardVisuals.Clear();
		visibleOwnBoardLocations.Clear();
		visibleEnemyBoard.Clear();
		visibleBoard.Clear();
		foreach (var item in ScanVisualCards("BP_HandCard_C"))
		{
			if (item.Location == OwnHandLocation && runtime.ReadByte(item.Card + CardModelLocationOffset) == OwnHandLocation &&
				(!ownSide.HasValue || item.Description.Side == ownSide.Value))
			{
				visibleOwnHand[item.Card] = item.Description;
				deploymentCandidates[item.Card] = (item.Description, DateTimeOffset.UtcNow);
				consumedPlayIds.Remove(item.Description.CardId);
				visibleOwnHandVisuals[item.Card] = item.Visual;
			}
		}
		foreach (var item2 in ScanVisualCards("BP_BoardCard_C"))
		{
			visibleBoard[item2.Card] = item2.Description;
			if (IsOwnBoardLocation(item2.Location, item2.Description.Side))
			{
				visibleOwnBoard[item2.Card] = item2.Description;
				visibleOwnBoardVisuals[item2.Card] = item2.Visual;
				visibleOwnBoardLocations[item2.Card] = item2.Location;
			}
			else if (item2.Location == EnemyBoardLocation ||
				(item2.Location == FrontlineLocation && ownSide.HasValue && item2.Description.Side != ownSide.Value))
			{
				visibleEnemyBoard[item2.Card] = item2.Description;
			}
		}
		return true;
	}

	private void BaselineVisualCards()
	{
		deploymentCandidates.Clear();
		knownHandCards.Clear();
		knownOrderHandVisuals.Clear();
		knownBoardCards.Clear();
		knownOwnBoardVisuals.Clear();
		knownOwnBoardLocations.Clear();
		activeDamageVisuals.Clear();
		lastAttackVisuals.Clear();
		pendingHurt = null;
		if (runtime == null)
		{
			return;
		}
		RefreshVisibleCards(force: true);
		byte? b = ownSide;
		if (!b.HasValue)
		{
			CardDescription? cardDescription = visibleOwnHand.Values.FirstOrDefault();
			ownSide = (((object)cardDescription != null) ? new byte?(cardDescription.Side) : visibleOwnBoard.Values.FirstOrDefault()?.Side);
		}
		foreach (KeyValuePair<long, CardDescription> pair in visibleOwnHand)
		{
			knownHandCards[pair.Key] = pair.Value;
			if (IsConfirmedOrder(pair.Value) && visibleOwnHandVisuals.TryGetValue(pair.Key, out long visual))
			{
				knownOrderHandVisuals[pair.Key] = visual;
			}
		}
		foreach (KeyValuePair<long, CardDescription> pair2 in visibleOwnBoard)
		{
			knownBoardCards[pair2.Key] = pair2.Value;
			if (visibleOwnBoardVisuals.TryGetValue(pair2.Key, out long visual))
			{
				knownOwnBoardVisuals[pair2.Key] = visual;
			}
			if (visibleOwnBoardLocations.TryGetValue(pair2.Key, out byte location))
				knownOwnBoardLocations[pair2.Key] = location;
		}
		previousOrdersFlying = runtime.ReadInt(board + 1916);
		previousOrderAnimationId = runtime.ReadInt(board + PlayFromHandCardIdOffset);
		Console.WriteLine($"Card baseline: side={ownSide}; hand={knownHandCards.Count}; board={knownBoardCards.Count}");
	}

	private void ObserveVisualCards()
	{
		Dictionary<long, CardDescription> hands = visibleOwnHand;
		Dictionary<long, CardDescription> boards = visibleOwnBoard;
		byte? b = ownSide;
		if (!b.HasValue)
		{
			CardDescription? cardDescription = hands.Values.FirstOrDefault();
			ownSide = (((object)cardDescription != null) ? new byte?(cardDescription.Side) : boards.Values.FirstOrDefault()?.Side);
			b = ownSide;
			if (!b.HasValue)
			{
				return;
			}
		}
		// Snapshots maintain combat baselines only. Removal may mean discard,
		// mulligan, preview cleanup or visual recreation, never proof of a play.
		if (playPhaseActive)
		{
			ObserveMovedUnits(boards);
			ObserveDamagedUnits(boards);
		}
		knownHandCards.Clear();
		knownOrderHandVisuals.Clear();
		foreach (KeyValuePair<long, CardDescription> pair in hands)
		{
			knownHandCards[pair.Key] = pair.Value;
			if (IsConfirmedOrder(pair.Value) && visibleOwnHandVisuals.TryGetValue(pair.Key, out long visual))
			{
				knownOrderHandVisuals[pair.Key] = visual;
			}
		}
		knownBoardCards.Clear();
		knownOwnBoardVisuals.Clear();
		knownOwnBoardLocations.Clear();
		foreach (KeyValuePair<long, CardDescription> pair2 in boards)
		{
			knownBoardCards[pair2.Key] = pair2.Value;
			if (visibleOwnBoardVisuals.TryGetValue(pair2.Key, out long visual))
			{
				knownOwnBoardVisuals[pair2.Key] = visual;
			}
			if (visibleOwnBoardLocations.TryGetValue(pair2.Key, out byte location))
				knownOwnBoardLocations[pair2.Key] = location;
		}
	}

	private void ObserveMovedUnits(Dictionary<long, CardDescription> current)
	{
		foreach (KeyValuePair<long, CardDescription> pair in current)
		{
			if (visibleOwnBoardLocations.TryGetValue(pair.Key, out byte after))
				ObserveUnitLocation(pair.Value, after);
		}
	}

	private bool IsOwnBoardLocation(byte location, byte side) =>
		location is OwnBoardLocation or FrontlineLocation &&
		(ownSide.HasValue ? side == ownSide.Value : location == OwnBoardLocation);

	private void ObserveMovementAnimations()
	{
		if (runtime == null || !playPhaseActive || DateTimeOffset.UtcNow < nextMovementSample) return;
		nextMovementSample = DateTimeOffset.UtcNow.AddMilliseconds(20);
		foreach (KeyValuePair<long, long> pair in knownOwnBoardVisuals)
		{
			// A destroyed/replaced visual must not transfer its old location to another card.
			if (!runtime.IsPointer(pair.Value) || ResolveCardRef(pair.Value) != pair.Key ||
				!knownBoardCards.TryGetValue(pair.Key, out CardDescription? card)) continue;
			byte side = runtime.ReadByte(pair.Key + CardSideOffset);
			ObserveUnitLocation(card with { Side = side }, runtime.ReadByte(pair.Value + CardLocationOffset));
		}
	}

	private void ObserveUnitLocation(CardDescription card, byte after)
	{
		// NotAvailable can occur while a visual is being rebuilt; retain the last board row.
		if (after == 0) return;
		if (!IsOwnBoardLocation(after, card.Side))
		{
			knownOwnBoardLocations.Remove(card.Instance);
			return;
		}
		bool known = knownOwnBoardLocations.TryGetValue(card.Instance, out byte before);
		// Commit immediately so the fast poll and the full snapshot cannot announce twice.
		knownOwnBoardLocations[card.Instance] = after;
		if (!known || before != OwnBoardLocation || after != FrontlineLocation ||
			!CuesEnabled || !IsSupportedUnit(card)) return;
		Console.WriteLine($"Own unit moved to frontline: {card.Nation}/{card.SourceName}; location={before}->{after}");
		PlayMove(card, "Own unit advanced to frontline");
	}

	private void ObserveDamageAnimations()
	{
		if (!CuesEnabled || runtime == null || knownOwnBoardVisuals.Count == 0)
		{
			return;
		}
		HashSet<long> stillKnown = new HashSet<long>();
		foreach (KeyValuePair<long, long> pair in knownOwnBoardVisuals.ToArray())
		{
			long card = pair.Key;
			long visual = pair.Value;
			if (!runtime.IsPointer(visual) ||
				runtime.ReadByte(visual + CardLocationOffset) is not (OwnBoardLocation or FrontlineLocation))
			{
				continue;
			}
			stillKnown.Add(visual);
			(long data, int count, int capacity) = runtime.ReadArray(visual, 0xA68);
			bool queueActive = count > 0 && count <= capacity && count < 64 && runtime.IsPointer(data);
			bool active = runtime.ReadByte(visual + 0xB98) != 0 || queueActive;
			if (!active)
			{
				activeDamageVisuals.Remove(visual);
				continue;
			}
			if (!activeDamageVisuals.Add(visual) || DateTimeOffset.UtcNow - lastHurtVoiceAt < TimeSpan.FromMilliseconds(450))
			{
				continue;
			}
			CardDescription description = knownBoardCards.TryGetValue(card, out CardDescription known)
				? known : DescribeCard(card);
			if (!IsSupportedUnit(description)) continue;
			lastHurtVoiceAt = DateTimeOffset.UtcNow;
			QueueHurt(description, $"Own {description.Category} damage animation");
		}
		activeDamageVisuals.RemoveWhere(visual => !stillKnown.Contains(visual));
	}

	private void ObserveAttackAnimations()
	{
		if (!CuesEnabled || runtime == null || !playPhaseActive || knownOwnBoardVisuals.Count == 0)
			return;
		HashSet<long> stillKnown = new HashSet<long>();
		foreach (KeyValuePair<long, long> pair in knownOwnBoardVisuals.ToArray())
		{
			long visual = pair.Value;
			if (!runtime.IsPointer(visual) ||
				runtime.ReadByte(visual + CardLocationOffset) is not (OwnBoardLocation or FrontlineLocation))
				continue;
			stillKnown.Add(visual);
			// BP_BaseCard_C.lastAttackVisual records this card's most recent attack visual marker.
			// Baseline a newly seen card so an old effect cannot be announced on attach.
			long effect = runtime.ReadPointer(visual + 0x558);
			if (!lastAttackVisuals.TryGetValue(visual, out long previous))
			{
				lastAttackVisuals[visual] = effect;
				continue;
			}
			if (effect == previous) continue;
			lastAttackVisuals[visual] = effect;
			if (!runtime.IsPointer(effect) || DateTimeOffset.UtcNow - lastAttackCueAt < TimeSpan.FromMilliseconds(450))
				continue;
			CardDescription card = knownBoardCards.TryGetValue(pair.Key, out CardDescription known)
				? known : DescribeCard(pair.Key);
			if (!IsSupportedUnit(card)) continue;
			lastAttackCueAt = DateTimeOffset.UtcNow;
			PlayAttack(card, $"Own {card.Category} attack visual");
		}
		foreach (long visual in lastAttackVisuals.Keys.Where(visual => !stillKnown.Contains(visual)).ToArray())
			lastAttackVisuals.Remove(visual);
	}

	private void ObserveDamagedUnits(Dictionary<long, CardDescription> current)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		if (!CuesEnabled || now - lastHurtVoiceAt < TimeSpan.FromMilliseconds(900.0))
		{
			return;
		}
		HashSet<long> usedPrevious = new HashSet<long>();
		List<(CardDescription Card, int Loss)> damaged = new List<(CardDescription Card, int Loss)>();
		foreach (KeyValuePair<long, CardDescription> pair in current)
		{
			CardDescription after = pair.Value;
			if (after.SourceName?.Contains("card_location", StringComparison.OrdinalIgnoreCase) == true)
			{
				continue;
			}
			CardDescription before = null;
			if (knownBoardCards.TryGetValue(pair.Key, out CardDescription sameInstance))
			{
				before = sameInstance;
				usedPrevious.Add(pair.Key);
			}
			else
			{
				string key = DamageCardKey(after);
				KeyValuePair<long, CardDescription> match = knownBoardCards
					.Where((KeyValuePair<long, CardDescription> item) => !usedPrevious.Contains(item.Key) && DamageCardKey(item.Value) == key)
					.OrderBy((KeyValuePair<long, CardDescription> item) => Math.Abs(item.Value.Defense - after.Defense))
					.FirstOrDefault();
				if (match.Key != 0L)
				{
					before = match.Value;
					usedPrevious.Add(match.Key);
				}
			}
			if ((object)before != null && before.Defense > after.Defense && after.Defense > 0 &&
				IsSupportedUnit(after))
			{
				damaged.Add((after, before.Defense - after.Defense));
			}
		}
		if (damaged.Count == 0)
		{
			return;
		}
		(CardDescription Card, int Loss) hit = damaged.OrderByDescending(((CardDescription Card, int Loss) item) => item.Loss).First();
		lastHurtVoiceAt = now;
		QueueHurt(hit.Card, $"Own {hit.Card.Category} damaged (-{hit.Loss}, defense {hit.Card.Defense})");
	}

	private void QueueHurt(CardDescription card, string reason)
	{
		pendingHurt = new PendingHurt(card, reason, pendingHurt?.Added ?? DateTimeOffset.UtcNow);
	}

	private void FlushPendingHurt()
	{
		if (pendingHurt is not PendingHurt hurt ||
			DateTimeOffset.UtcNow - hurt.Added < TimeSpan.FromMilliseconds(550)) return;
		pendingHurt = null;
		PlayHurt(hurt.Card, hurt.Reason);
	}

	private static string DamageCardKey(CardDescription card)
	{
		if (!string.IsNullOrWhiteSpace(card.SourceName))
		{
			return "source:" + card.SourceName.ToLowerInvariant();
		}
		return $"sound:{card.SoundName?.ToLowerInvariant()}:{card.Category}:{card.Side}:{card.Cost}";
	}

	private static List<CardDescription> SemanticDifference(IEnumerable<CardDescription> source, IEnumerable<CardDescription> subtract)
	{
		Dictionary<string, int> remaining = subtract.GroupBy(StableCardKey).ToDictionary<IGrouping<string, CardDescription>, string, int>((IGrouping<string, CardDescription> group) => group.Key, (IGrouping<string, CardDescription> group) => group.Count(), StringComparer.Ordinal);
		List<CardDescription> difference = new List<CardDescription>();
		foreach (CardDescription card in source)
		{
			string key = StableCardKey(card);
			if (remaining.TryGetValue(key, out var count) && count > 0)
			{
				remaining[key] = count - 1;
			}
			else
			{
				difference.Add(card);
			}
		}
		return difference;
	}

	private static string StableCardKey(CardDescription card)
	{
		if (!string.IsNullOrWhiteSpace(card.SourceName))
		{
			return "source:" + card.SourceName.ToLowerInvariant();
		}
		if (string.IsNullOrWhiteSpace(card.SoundName))
		{
			return $"anonymous:{card.Category}:{card.Side}:{card.Cost}:{card.Defense}";
		}
		return $"sound:{card.SoundName.ToLowerInvariant()}:{card.Side}:{card.Cost}:{card.Defense}";
	}

	private void PlayVisualCard(CardDescription card, string reason)
	{
		if (card.Category == "ORDER" && !IsConfirmedOrder(card))
		{
			Console.WriteLine($"{reason}: unverified order ignored; source={card.SourceName ?? "none"}, sound={card.SoundName ?? "none"}");
			return;
		}
		if (!playPhaseActive || !ownSide.HasValue || card.Side != ownSide.Value || card.CardId <= 0 || !card.Identified)
			return;
		PendingCard? action = pendingCards.FirstOrDefault(item => item.Id == card.CardId);
		if (action == null || consumedPlayIds.Contains(card.CardId)) return;
		pendingCards.Remove(action);
		consumedPlayIds.Add(card.CardId);
		CancelOpeningForCard(reason);
		Console.WriteLine($"{reason}: cardId={card.CardId}, cost={card.Cost}, defense={card.Defense}, pool={card.Category}, side={card.Side}, source={card.SourceName}");
		knownHandCards.Remove(card.Instance);
		knownOrderHandVisuals.Remove(card.Instance);
		if (CuesEnabled)
		{
			PlayCard(card, reason);
		}
	}



	private void PlayCard(CardDescription card, string reason)
	{
		if (card.Category == "ORDER" && !IsConfirmedOrder(card))
		{
			Console.WriteLine(reason + ": unverified order subtitle suppressed.");
			return;
		}
		DateTimeOffset now = DateTimeOffset.UtcNow;
		if (card.Identified)
		{
			string? nation = card.Nation ?? IdentifyNation(card.SourceName, card.SoundName);
			if (nation == null) return;
			lastCardVoiceAt = now;
			if (nationPacks.TryGetValue(nation, out var pack))
			{
				string? resource = pack.Cards.Contains(card.SourceName) ? card.SourceName : card.SoundName;
				ShowNationSubtitle(pack, card.Category, card.Event, reason, resource);
				return;
			}
			if (card.Category == "ORDER" && alliedOrders.TryGet(out AlliedOrderLabel allied,
				card.SourceName, card.SoundName))
			{
				PlayAlliedOrder(allied, reason);
				return;
			}
			if (nation == VoicePackRegistry.Soviet)
			{
				string? resource = card.Category == "ORDER" && sovietCards.Classify(card.SourceName) != "ORDER"
					? card.SoundName : card.SourceName;
				ShowSovietSubtitle(card.Category, card.Event, reason, resource);
				return;
			}
			if (nation is not (VoicePackRegistry.Japan or VoicePackRegistry.Soviet))
			{
				ShowAlliedCard(card, "DEPLOY", reason);
			}
			else if (card.Category == "ORDER")
			{
				PlayOrder(reason, card.OrderProfile ?? OrderVoiceProfile.Generic,
					voices.ResolveOrderSubtitle(card.SourceName, card.SoundName));
			}
			else if (JapanVoicePack.DeploymentPoolNames.Contains<string>(card.Category, StringComparer.Ordinal))
			{
				PlayDeployment(card.Category, reason);
			}
			else
			{
				Play(card.Category, card.Event, reason);
			}
		}
	}

	private void ObserveHeadquarters()
	{
		if (DateTimeOffset.UtcNow < nextHqCheck)
		{
			return;
		}
		nextHqCheck = DateTimeOffset.UtcNow.AddMilliseconds(250.0);
		long ownCard = ResolveCardRef(runtime.ReadPointer(board + 2024));
		long enemyCard = ResolveCardRef(runtime.ReadPointer(board + 2032));
		if (!runtime.IsPointer(ownCard) || !runtime.IsPointer(enemyCard))
		{
			return;
		}
		if (matchNation == null) DetectMatchNation();
		if (matchNation == null) return;
		(byte, int) own = (runtime.ReadByte(ownCard + 630), runtime.ReadInt(ownCard + 116));
		(byte, int) enemy = (runtime.ReadByte(enemyCard + 630), runtime.ReadInt(enemyCard + 116));
		if (own.Item2 <= 0 && enemy.Item2 <= 0)
		{
			return;
		}
		int item;
		if (!hqCriticalPlayed)
		{
			item = own.Item2;
			if (item > 0 && item <= 5)
			{
				hqCriticalPlayed = true;
				if (CuesEnabled)
				{
					PlayNationEvent("CMD", "HQ_CRITICAL", $"Own HQ critical ({own.Item2})");
				}
				goto IL_01e0;
			}
		}
		if (!hqLowPlayed)
		{
			item = own.Item2;
			if (item > 5 && item <= 10)
			{
				hqLowPlayed = true;
				if (CuesEnabled)
				{
					PlayNationEvent("CMD", "HQ_LOW", $"Own HQ low ({own.Item2})");
				}
			}
		}
		goto IL_01e0;
		IL_01e0:
		if (enemyHqLowPlayed)
		{
			return;
		}
		item = enemy.Item2;
		if (item > 0 && item <= 5)
		{
			enemyHqLowPlayed = true;
			if (CuesEnabled)
			{
				PlayNationEvent("CMD", "ENEMY_HQ_LOW", $"Enemy HQ low ({enemy.Item2})");
			}
		}
	}

	private bool IsConfirmedOrder(CardDescription card) => card.Category == "ORDER" &&
		(sovietCards.Classify(card.SourceName, card.SoundName) == "ORDER" ||
		 japanCards.Classify(card.SourceName, card.SoundName) == "ORDER" ||
		 nationPacks.Values.Any(pack => pack.Cards.Classify(card.SourceName, card.SoundName) == "ORDER") ||
		 alliedOrders.TryGet(out _, card.SourceName, card.SoundName));

	private void PlayAlliedOrder(AlliedOrderLabel order, string reason)
	{
		if (!settings.SubtitlesEnabled) return;
		if (!alliedCards.TryGet(out AlliedCardLabel card, order.Resource)) return;
		string original = string.IsNullOrWhiteSpace(card.OriginalTitle) ? order.ChineseTitle : card.OriginalTitle;
		string effect = order.ChineseEffect;
		if (effect.Length > 66) effect = effect[..63].TrimEnd() + "…";
		string chinese = effect.Length == 0 ? order.ChineseTitle : order.ChineseTitle + "｜" + effect;
		TimeSpan duration = TimeSpan.FromSeconds(Math.Clamp(3.2 + original.Length * 0.045, 4, 6.5));
		Present(new SubtitleCue(chinese, duration, Commander: false,
			Speaker: VoicePackRegistry.NationName(card.Origin) + "指令", Original: original),
			null, true, false, reason);
		Console.WriteLine($"{reason}: allied order={card.Origin}/{order.Resource}");
	}

	private void ShowAlliedCard(CardDescription card, string action, string reason)
	{
		if (!settings.SubtitlesEnabled ||
			!alliedCards.TryGet(out AlliedCardLabel label, card.SourceName, card.SoundName) ||
			label.Kind != "UNIT") return;
		string original = string.IsNullOrWhiteSpace(label.OriginalTitle) ? label.ChineseTitle : label.OriginalTitle;
		string chinese = string.IsNullOrWhiteSpace(label.ChineseTitle) ? original : label.ChineseTitle;
		string suffix = action switch { "ATTACK" => "发动攻击", "HURT" => "遭到攻击",
			"MOVE" => "推进至前线", _ => "已部署" };
		Present(new SubtitleCue(chinese + " · " + suffix, TimeSpan.FromSeconds(3.4), false,
			Speaker: VoicePackRegistry.NationName(label.Origin) + "单位", Original: original),
			null, true, action is "ATTACK" or "HURT" or "MOVE", reason,
			transient: action is "ATTACK" or "HURT" or "MOVE", reaction: ReactionFor(action));
	}

	// Exercises the same card routing used during a match without attaching to KARDS.
	internal void VerifyAutomaticRouting()
	{
		bool oldAudio = settings.AutoEnabled;
		bool oldSubtitles = settings.SubtitlesEnabled;
		string oldPreview = settings.SelectedVoicePack;
		string? oldNation = matchNation;
		DateTimeOffset oldLast = lastCardVoiceAt;
		List<SubtitleCue> shown = new();
		void collect(SubtitleCue cue) => shown.Add(cue);
		SubtitleRequested += collect;
		try
		{
			routingVerification = true;
			settings.SubtitlesEnabled = true;
			settings.AutoEnabled = true;
			settings.SelectedVoicePack = VoicePackRegistry.Japan;
			matchNation = VoicePackRegistry.Japan;
			string soviet = IdentifyNation("card_unit_t_34_C") ?? throw new InvalidDataException("Soviet card not recognized");
			PlayCard(new CardDescription(1, "TANK", "DEPLOY", 0, 0, 1, 0, null, "card_unit_t_34_C", null, true, soviet), "route check");
			if (shown.Count != 1 || shown[0].Speaker != "苏联装甲部队" ||
				string.IsNullOrWhiteSpace(shown[0].Original) ||
				player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
				throw new InvalidDataException("Soviet card must play its own Russian audio even when Japan is selected for F6 preview.");
			player.Stop();
			settings.SubtitlesEnabled = false;
			lastCardVoiceAt = DateTimeOffset.MinValue;
			PlayCard(new CardDescription(9, "TANK", "DEPLOY", 0, 0, 1, 0, null,
				"card_unit_t_34_C", null, true, soviet), "audio-only check");
			if (shown.Count != 1 || player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
				throw new InvalidDataException("Soviet voice audio must play when subtitles are disabled.");
			player.Stop();
			settings.SubtitlesEnabled = true;
			settings.AutoEnabled = false;
			settings.SelectedVoicePack = VoicePackRegistry.Soviet;
			matchNation = VoicePackRegistry.Soviet;
			lastCardVoiceAt = DateTimeOffset.MinValue;
			string japan = IdentifyNation("card_unit_2nd_raiding_brigade_C") ?? throw new InvalidDataException("Japan card not recognized");
			PlayCard(new CardDescription(2, "INFANTRY", "DEPLOY", 0, 0, 1, 0, null, "card_unit_2nd_raiding_brigade_C", null, true, japan), "route check");
			if (shown.Count != 2 || shown[1].Speaker != "陆军部队" ||
				string.IsNullOrWhiteSpace(shown[1].Original))
				throw new InvalidDataException("Japan card subtitle must work when Soviet is selected for F6 preview.");
			lastCardVoiceAt = DateTimeOffset.MinValue;
			CardDescription unknownOrder = new(3, "ORDER", "DEPLOY", 0, 3, 0, 0, null, null,
				null, true, japan);
			pendingCards.Add(new PendingCard(-1, DateTimeOffset.UtcNow));
			PlayVisualCard(unknownOrder, "route check");
			if (shown.Count != 2 || lastCardVoiceAt != DateTimeOffset.MinValue ||
				!pendingCards.Any(action => action.Id == -1))
				throw new InvalidDataException("An unidentified card must not produce or consume an order cue.");
			if (!IsConfirmedOrder(new CardDescription(4, "ORDER", "DEPLOY", 0, 1, 0, 0, null,
				"card_event_7075_aluminium_C", null, true, japan)))
				throw new InvalidDataException("A catalogued Japanese order must remain eligible.");
			pendingCards.RemoveAll(action => action.Id == -1);
			PlayCommanderIntro("route check", VoicePackRegistry.Japan);
			if (shown.Count != 3 || shown[2].Lines?.Count != 4 ||
				shown[2].Lines?.Any(line => string.IsNullOrWhiteSpace(line.Original) || string.IsNullOrWhiteSpace(line.Chinese)) != false)
				throw new InvalidDataException("Japanese commander intro must have four bilingual segments.");
			PlayCommanderIntro("route check", VoicePackRegistry.Soviet);
			if (shown.Count != 4 || shown[3].Lines?.Count != 5 ||
				shown[3].Duration < TimeSpan.FromSeconds(18) ||
				player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
				throw new InvalidDataException("Soviet commander intro must have five bilingual segments when voice audio is disabled.");
			lastCardVoiceAt = DateTimeOffset.MinValue;
			PlayCard(new CardDescription(5, "ORDER", "DEPLOY", 0, 2, 0, 0, null,
				"card_event_assault_C", null, true, soviet), "route check");
			if (shown.Count != 5 || shown[4].Speaker != "通信兵" ||
				shown[4].Original != sovietSubtitles.Pick("ORDER", "DEPLOY", "card_event_assault_C").Original)
				throw new InvalidDataException("Soviet order must show its own Russian and Chinese lines.");
			if (ClassifyCategory("card_unit_152mm_howitzer_gun_C") != "ARTILLERY" ||
				ClassifyCategory("card_unit_katyusha_C") != "ARTILLERY" ||
				ClassifyCategory("card_unit_cossack_cavalry_C") != "CAVALRY")
				throw new InvalidDataException("Soviet artillery or cavalry was classified as infantry.");
			lastCardVoiceAt = DateTimeOffset.MinValue;
			PlayCard(new CardDescription(6, "ARTILLERY", "DEPLOY", 0, 0, 3, 0, null,
				"card_unit_152mm_howitzer_gun_C", null, true, soviet), "route check");
			if (shown.Count != 6 || shown[5].Speaker != "苏联炮兵" ||
				string.IsNullOrWhiteSpace(shown[5].Original) ||
				player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
				throw new InvalidDataException("Soviet howitzer must use the artillery subtitle pool when voice audio is disabled.");
			if (sovietSubtitles.Pick("ARTILLERY", "ATTACK").Speaker != "苏联炮兵" ||
				voices.PickAttack("TANK").Event != "ATTACK")
				throw new InvalidDataException("Attack subtitles must use the unit's own category.");
			lastCardVoiceAt = DateTimeOffset.MinValue;
			PlayCard(new CardDescription(7, "ORDER", "DEPLOY", 0, 0, 0, 0, null,
				"card_event_unity_is_strength_C", null, true, null), "allied order check");
			if (shown.Count != 7 || !shown[6].Text.Contains("团结就是力量", StringComparison.Ordinal) ||
				shown[6].Original != "UNITY IS STRENGTH" || shown[6].Speaker != "美国指令" ||
				IdentifyNation("card_event_unity_is_strength_C") != "USA")
				throw new InvalidDataException("A USA allied order in a Soviet deck must show its own card label.");
			settings.AutoEnabled = false;
			matchNation = VoicePackRegistry.Japan;
			lastCardVoiceAt = DateTimeOffset.MinValue;
			PlayCard(new CardDescription(8, "ORDER", "DEPLOY", 0, 0, 0, 0, null,
				"card_event_unity_is_strength_cam1_C", null, true, null), "allied order check");
			if (shown.Count != 8 || shown[7].Original != "UNITY IS STRENGTH" ||
				shown[7].Speaker != "美国指令")
				throw new InvalidDataException("An allied order in a Japanese deck must retain its own national label.");
			settings.AutoEnabled = true;
			matchNation = VoicePackRegistry.Soviet;
			lastCardVoiceAt = DateTimeOffset.MinValue;
			PlayCard(new CardDescription(10, "ORDER", "DEPLOY", 0, 0, 0, 0, null,
				"card_event_reserves_C", null, true, VoicePackRegistry.Soviet), "order audio check");
			if (shown.Count != 9 || shown[8].Original !=
				sovietSubtitles.Pick("ORDER", "DEPLOY", "card_event_reserves_C").Original ||
				player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
				throw new InvalidDataException("A Soviet order did not play its individual Russian voice line.");
			player.Stop();
			PlayCommanderIntro("intro audio check", VoicePackRegistry.Soviet);
			SubtitleCue spokenIntro = shown[^1];
			if (spokenIntro.SegmentDurations?.Count != 5 ||
				Math.Abs(spokenIntro.SegmentDurations.Sum(duration => duration.TotalSeconds) - spokenIntro.Duration.TotalSeconds) > 0.1 ||
				player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
				throw new InvalidDataException("Soviet opening audio and bilingual subtitle segment times do not match.");
			player.Stop();
			settings.AutoEnabled = false;
			shown.Clear();
			matchNation = VoicePackRegistry.Japan;
			lastCardVoiceAt = DateTimeOffset.MinValue;
			PlayCard(new CardDescription(15, "ORDER", "DEPLOY", 0, 0, 0, 0, null,
				"card_event_reserves_C", null, true, VoicePackRegistry.Soviet), "mixed order check");
			if (shown.Count != 1 || shown[0].Original !=
				sovietSubtitles.Pick("ORDER", "DEPLOY", "card_event_reserves_C").Original)
				throw new InvalidDataException("A Soviet order in a Japanese deck must keep its Russian line.");
			shown.Clear();
			matchNation = VoicePackRegistry.Soviet;
			lastCardVoiceAt = DateTimeOffset.MinValue;
			PlayCard(new CardDescription(16, "ORDER", "DEPLOY", 0, 0, 0, 0, null,
				"card_event_7075_aluminium_C", null, true, VoicePackRegistry.Japan), "mixed order check");
			if (shown.Count != 1 || shown[0].Speaker != "通信兵" ||
				string.IsNullOrWhiteSpace(shown[0].Original) ||
				shown[0].Original == "UNITY IS STRENGTH")
				throw new InvalidDataException("A Japanese order in a Soviet deck must keep its Japanese line.");
			shown.Clear();
			if (IdentifyNation("card_unit_101_infantry_regiment_C") != "Germany" ||
				IdentifyNation("card_unit_104th_infantry_regiment_C") != "USA" ||
				ClassifyCategory("card_unit_104th_infantry_regiment_C") != "UNIT" ||
				IdentifyNation("card_unit_104th_infantry_regiment_C", "card_unit_t_34_C") != "USA")
				throw new InvalidDataException("An ally unit's resource must determine its own nation and type.");
			matchNation = VoicePackRegistry.Soviet;
			lastCardVoiceAt = DateTimeOffset.MinValue;
			CardDescription american = new(11, "UNIT", "DEPLOY", 0, 0, 3, 0, null,
				"card_unit_104th_infantry_regiment_C", null, true, "USA");
			PlayCard(american, "ally unit check");
			if (shown.Count != 1 || shown[0].Speaker != "美国单位" ||
				shown[0].Original != "104th INFANTRY REGIMENT" ||
				player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
				throw new InvalidDataException("An allied unit must not borrow Soviet or Japanese speech.");
			shown.Clear();
			settings.AutoEnabled = true;
			PlayMove(new CardDescription(12, "INFANTRY", "DEPLOY", 0, 0, 3, 0, null,
				"card_unit_2nd_raiding_brigade_C", null, true, VoicePackRegistry.Japan), "move check");
			if (shown.Count != 1 || shown[0].Speaker != "陆军部队" ||
				player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
				throw new InvalidDataException("Japanese frontline movement must use a move voice line.");
			player.Stop();
			PlayMove(new CardDescription(13, "TANK", "DEPLOY", 0, 0, 3, 0, null,
				"card_unit_t_34_C", null, true, VoicePackRegistry.Soviet), "move check");
			if (shown.Count != 2 || shown[1].Speaker != "苏联装甲部队" ||
				player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
				throw new InvalidDataException("Soviet frontline movement must use a Russian voice line.");
			player.Stop();
			PlayMove(american, "move check");
			if (shown.Count != 3 || !shown[2].Text.Contains("推进至前线", StringComparison.Ordinal) ||
				player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
				throw new InvalidDataException("Other allied movement must show only its own silent card subtitle.");
			shown.Clear();
			VerifyFrontlineMovement();
			VerifyCardPlayEvidence();
			settings.AutoEnabled = false;

			// A hit reaction waits for the card line and starts its audio only
			// when the matching subtitle becomes visible.
			routingVerification = false;
			ResetCombatPresentations();
			pendingPresentations.Clear();
			activePresentationUntil = DateTimeOffset.MinValue;
			shown.Clear();
			Present(new SubtitleCue("card", TimeSpan.FromSeconds(4), false), null,
				true, false, "combat order check");
			pendingHurt = new PendingHurt(new CardDescription(14, "INFANTRY", "DEPLOY", 0,
				0, 3, 0, null, "card_unit_t_34_C", null, true, VoicePackRegistry.Soviet),
				"combat order check", DateTimeOffset.UtcNow.AddSeconds(-1));
			Present(new SubtitleCue("attack", TimeSpan.FromSeconds(3), false), null,
				true, true, "combat order check", transient: true, reaction: CombatReaction.Attack);
			FlushPendingHurt();
			if (pendingReactions.Count != 2 || pendingReactions[0].Cue.Text != "attack" ||
				pendingReactions[1].Cue.Text == "attack")
				throw new InvalidDataException("An attack command must precede the delayed hurt reaction.");
			ResetCombatPresentations();
			pendingPresentations.Clear();
			activePresentationUntil = DateTimeOffset.MinValue;
			shown.Clear();
			int hurtAudioStarts = 0;
			Present(new SubtitleCue("card one", TimeSpan.FromSeconds(4), false), null, true, false, "queue check");
			Present(new SubtitleCue("hurt", TimeSpan.FromSeconds(3), false), () =>
			{
				hurtAudioStarts++;
				return TimeSpan.FromSeconds(3);
			}, true, true, "queue check", transient: true);
			if (shown.Count != 1 || hurtAudioStarts != 0 || pendingPresentations.Count != 1)
				throw new InvalidDataException("Combat cue interrupted a card subtitle or started audio while queued.");
			activePresentationUntil = DateTimeOffset.MinValue;
			RunQueuedPresentations();
			if (shown.Count != 2 || shown[1].Text != "hurt" || hurtAudioStarts != 1)
				throw new InvalidDataException("Queued combat subtitle and audio did not start together.");

			// A subsequent card makes an older pending hit report stale.
			pendingPresentations.Clear();
			activePresentationUntil = DateTimeOffset.MinValue;
			shown.Clear();
			Present(new SubtitleCue("card one", TimeSpan.FromSeconds(4), false), null, true, false, "queue check");
			Present(new SubtitleCue("old hurt", TimeSpan.FromSeconds(3), false), () =>
			{
				hurtAudioStarts++;
				return TimeSpan.FromSeconds(3);
			}, true, true, "queue check", transient: true);
			Present(new SubtitleCue("card two", TimeSpan.FromSeconds(4), false), null, true, false, "queue check");
			if (shown.Count != 1 || pendingPresentations.Count != 0 || hurtAudioStarts != 1 ||
				reactionTransitionTarget?.Cue.Text != "card two")
				throw new InvalidDataException("New card did not retire a stale hit reaction.");
			reactionFadeUntil = DateTimeOffset.MinValue;
			RunQueuedPresentations();
			if (shown.Count != 2 || shown[1].Text != "card two" || hurtAudioStarts != 1)
				throw new InvalidDataException("New card subtitle did not follow the previous card.");

			pendingPresentations.Clear();
			activePresentationUntil = DateTimeOffset.MinValue;
			shown.Clear();
			Present(new SubtitleCue("active hurt", TimeSpan.FromSeconds(3), false), () =>
			{
				hurtAudioStarts++;
				return TimeSpan.FromSeconds(3);
			}, true, true, "queue check", transient: true);
			Present(new SubtitleCue("played card", TimeSpan.FromSeconds(4), false), null, true, false, "queue check");
			reactionFadeUntil = DateTimeOffset.MinValue;
			RunQueuedPresentations();
			if (shown.Count != 2 || shown[1].Text != "played card" || hurtAudioStarts != 2 ||
				activePresentationTransient)
				throw new InvalidDataException("A new card did not supersede the active hit subtitle.");

			// The first card ends the opening speech and starts its own line at once.
			pendingPresentations.Clear();
			activePresentationUntil = DateTimeOffset.MinValue;
			ResetOpeningForMatch(pending: true);
			shown.Clear();
			int introAudioStarts = 0;
			int cardAudioStarts = 0;
			Present(new SubtitleCue("opening", TimeSpan.FromSeconds(19), true), () =>
			{
				introAudioStarts++;
				return TimeSpan.FromSeconds(19);
			}, true, false, "intro check", intro: true);
			Present(new SubtitleCue("first card", TimeSpan.FromSeconds(4), false), () =>
			{
				cardAudioStarts++;
				return TimeSpan.FromSeconds(4);
			}, true, false, "intro check");
			if (shown.Count != 2 || shown[1].Text != "first card" || introAudioStarts != 1 ||
				cardAudioStarts != 1 || activePresentationIntro || pendingPresentations.Count != 0)
				throw new InvalidDataException("A first card did not interrupt the opening subtitle and audio.");

			// An opening speech queued behind a card must never play later.
			Present(new SubtitleCue("late opening", TimeSpan.FromSeconds(19), true), null,
				true, false, "intro check", intro: true);
			Present(new SubtitleCue("second card", TimeSpan.FromSeconds(4), false), null,
				true, false, "intro check");
			if (pendingPresentations.Count != 0 || reactionTransitionTarget?.Cue.Text != "second card")
				throw new InvalidDataException("A queued opening speech survived a card play.");
			reactionFadeUntil = DateTimeOffset.MinValue;
			RunQueuedPresentations();
			if (shown.Count != 3 || shown[2].Text != "second card")
				throw new InvalidDataException("Card playback did not resume after retiring the opening speech.");
			VerifyCombatInterruption();
			VerifyRapidCardPlayback();
		}
		finally
		{
			ResetCombatPresentations();
			routingVerification = false;
			pendingPresentations.Clear();
			activePresentationUntil = DateTimeOffset.MinValue;
			activePresentationTransient = false;
			activePresentationIntro = false;
			pendingHurt = null;
			knownOwnBoardLocations.Clear();
			visibleOwnBoardLocations.Clear();
			pendingCards.RemoveAll(action => action.Id == -1);
			SubtitleRequested -= collect;
			settings.AutoEnabled = oldAudio;
			settings.SubtitlesEnabled = oldSubtitles;
			settings.SelectedVoicePack = oldPreview;
			matchNation = oldNation;
			lastCardVoiceAt = oldLast;
		}
	}

	private void VerifyFrontlineMovement()
	{
		byte? savedSide = ownSide;
		List<SubtitleCue> cues = new();
		void collect(SubtitleCue cue) => cues.Add(cue);
		SubtitleRequested += collect;
		try
		{
			ownSide = 1;
			// These numbers come from the live ECardLocationEnum, not the constants under test.
			if (IsOwnBoardLocation(4, 1) || !IsOwnBoardLocation(7, 1) || IsOwnBoardLocation(7, 2))
				throw new InvalidDataException("Frontline=7 must include our units and exclude enemy units; hand=4 is not frontline.");
			CardDescription japan = new(21, "INFANTRY", "DEPLOY", 1, 2, 3, 0, null,
				"card_unit_mito_regiment_C", null, true, VoicePackRegistry.Japan);
			CardDescription soviet = new(22, "TANK", "DEPLOY", 1, 5, 5, 0, null,
				"card_unit_t_34_C", null, true, VoicePackRegistry.Soviet);
			foreach (CardDescription card in new[] { japan, soviet })
			{
				int count = cues.Count;
				ObserveUnitLocation(card, 5);
				if (cues.Count != count) throw new InvalidDataException("A support-row baseline triggered movement.");
				ObserveUnitLocation(card, 7);
				string speaker = card.Nation == VoicePackRegistry.Japan ? "陆军部队" : "苏联装甲部队";
				if (cues.Count != count + 1 || cues[^1].Speaker != speaker ||
					player.PlaybackState != NAudio.Wave.PlaybackState.Playing)
					throw new InvalidDataException("Live 5->7 movement did not start this unit's own national voice and subtitle.");
				player.Stop();
				ObserveUnitLocation(card, 7);
				visibleOwnBoardLocations[card.Instance] = 7;
				ObserveMovedUnits(new() { [card.Instance] = card });
				if (cues.Count != count + 1) throw new InvalidDataException("Fast polling and full scan repeated the same movement.");
				ObserveUnitLocation(card, 5);
				ObserveUnitLocation(card, 0);
				ObserveUnitLocation(card, 7);
				if (cues.Count != count + 2) throw new InvalidDataException("Re-entry after retreat or a transient visual state lost movement.");
				player.Stop();
			}
			int previousCount = cues.Count;
			ObserveUnitLocation(japan with { Instance = 23 }, 7);
			ObserveUnitLocation(japan with { Instance = 24, Side = 2 }, 6);
			ObserveUnitLocation(japan with { Instance = 24, Side = 2 }, 7);
			ObserveUnitLocation(japan with { Instance = 25 }, 3);
			ObserveUnitLocation(japan with { Instance = 25 }, 7);
			if (cues.Count != previousCount)
				throw new InvalidDataException("An existing frontline unit, enemy move or direct deployment was falsely announced as movement.");
			Console.WriteLine("Frontline movement OK: live enum 5->7, Japan/Soviet audio, shared-front ownership, poll deduplication, re-entry and attach baseline.");
		}
		finally
		{
			ownSide = savedSide;
			SubtitleRequested -= collect;
			knownOwnBoardLocations.Clear();
			visibleOwnBoardLocations.Clear();
			player.Stop();
		}
	}

	public void VerifyLinuxMonitoring()
	{
		bool auto = settings.AutoEnabled, subtitles = settings.SubtitlesEnabled;
		settings.AutoEnabled = false; settings.SubtitlesEnabled = true;
		routingVerification = true;
		try
		{
			VerifyCardPlayEvidence();
			routingVerification = false;
			VerifyRapidCardPlayback(); VerifyCombatInterruption();
		}
		finally { settings.AutoEnabled = auto; settings.SubtitlesEnabled = subtitles; routingVerification = false; }
	}

	private void VerifyCardPlayEvidence()
	{
		byte? savedSide = ownSide;
		bool savedPhase = playPhaseActive;
		List<SubtitleCue> cues = new();
		void collect(SubtitleCue cue) => cues.Add(cue);
		SubtitleRequested += collect;
		try
		{
			ownSide = 1;
			playPhaseActive = true;
			pendingCards.Clear();
			consumedPlayIds.Clear();
			previousOrdersFlying = previousOrderAnimationId = 0;
			CardDescription convoy = new(101, "ORDER", "DEPLOY", 1, 4, 0, 29, null,
				"card_event_arctic_convoy_C", null, true, VoicePackRegistry.Soviet);
			CardDescription japan = new(102, "INFANTRY", "DEPLOY", 1, 2, 3, 18, null,
				"card_unit_mito_regiment_C", null, true, VoicePackRegistry.Japan);
			knownHandCards[convoy.Instance] = convoy;
			ObserveVisualCards(); // The hand visual disappeared without a play: no cue.
			PlayVisualCard(convoy, "stale reference check");
			if (cues.Count != 0) throw new InvalidDataException("Hand removal or an idle reference falsely played Arctic Convoy.");
			RegisterPlay(18, "recorded unit animation");
			PlayVisualCard(convoy, "unrelated order check");
			PlayVisualCard(japan with { Side = 2 }, "enemy reference check");
			if (cues.Count != 0 || pendingCards.Count != 1)
				throw new InvalidDataException("An unrelated or enemy card consumed another card's action.");
			PlayVisualCard(japan, "exact ID check");
			RegisterPlay(18, "duplicate queue event");
			PlayVisualCard(japan with { Instance = 999 }, "recreated visual check");
			if (cues.Count != 1 || cues[0].Speaker != "陆军部队" || pendingCards.Count != 0)
				throw new InvalidDataException("One actual play must produce one cue even if its visual is recreated.");
			// Recorded at 12:24:58.620: run=0, tmpCardID=35, ordersFlying=1,
			// card_event_from_the_people moved 3->8. Orders bypass the unit animation flag.
			CardDescription order = convoy with { Instance = 103, CardId = 35, SourceName = "card_event_from_the_people_C" };
			ObserveOrderAnimationState(1, 35, false);
			PlayVisualCard(order, "recorded order animation");
			ObserveOrderAnimationState(1, 35, false);
			ObserveOrderAnimationState(0, 35, false);
			ObserveOrderAnimationState(1, 69, true);
			if (cues.Count != 2 || cues[^1].Text != sovietSubtitles.Pick("ORDER", "DEPLOY", order.SourceName).Chinese || pendingCards.Count != 0)
				throw new InvalidDataException("Order flight detection missed a real order, repeated it or accepted an enemy order.");
			ObserveOrderAnimationState(0, 69, true);
			ObserveOrderAnimationState(1, 3, false); // Recorded Japanese rising_sun order.
			PlayVisualCard(japan with { Instance = 104, CardId = 3, Category = "ORDER", SourceName = "card_event_rising_sun_C" }, "recorded Japanese order");
			if (cues.Count != 3 || cues[^1].Speaker != "通信兵")
				throw new InvalidDataException("Japanese order animation did not route to its telegraph/subtitle.");
			if (RuntimeCategory(4, "INFANTRY") != "AIR" || RuntimeCategory(7, "INFANTRY") != "ARTILLERY" ||
				RuntimeCategory(6, "INFANTRY") != "INFANTRY" || RuntimeCategory(6, "CAVALRY") != "CAVALRY")
				throw new InvalidDataException("The native card type did not correct the voice pool.");
			for (int i = 0; i < voices.GetDeploymentPoolSize("INFANTRY"); i++)
				if (voices.PickDeployment("INFANTRY").Voice == "ARTY" || voices.PickHurt("INFANTRY").Voice == "ARTY")
					throw new InvalidDataException("Infantry borrowed an artillery voice line.");
			CardDescription missed = japan with { CardId = 91, Instance = 991 };
			int countBefore = cues.Count;
			ConfirmHandDeployment(missed, missed, 0); // Animation gap / discard isn't a deployment.
			ConfirmHandDeployment(missed, missed with { CardId = 92 }, OwnBoardLocation);
			ConfirmHandDeployment(missed, missed with { Side = 2 }, OwnBoardLocation);
			ConfirmHandDeployment(missed, missed with { Instance = 992 }, OwnBoardLocation);
			ConfirmHandDeployment(convoy, convoy, OwnBoardLocation);
			if (cues.Count != countBefore) throw new InvalidDataException("Invalid hand transition produced a deployment.");
			ConfirmHandDeployment(missed, missed, OwnBoardLocation);
			ConfirmHandDeployment(missed, missed, OwnBoardLocation);
			RegisterPlay(missed.CardId, "late duplicate animation");
			PlayVisualCard(missed, "late duplicate animation");
			if (cues.Count != countBefore + 1 || pendingCards.Count != 0)
				throw new InvalidDataException("Missed animation fallback lost or repeated a unit deployment.");
			Console.WriteLine("Play evidence OK: unit/order IDs, stable hand-to-board fallback, no discard/enemy/identity-reuse false positives, animation deduplication.");
		}
		finally
		{
			ownSide = savedSide;
			playPhaseActive = savedPhase;
			SubtitleRequested -= collect;
			pendingCards.Clear();
			consumedPlayIds.Clear();
			previousOrdersFlying = previousOrderAnimationId = 0;
			player.Stop();
		}
	}

	private void VerifyRapidCardPlayback()
	{
		Func<DateTimeOffset> savedClock = presentationNow;
		DateTimeOffset now = DateTimeOffset.UtcNow;
		List<string> starts = new();
		int fades = 0;
		void fade(TimeSpan _) => fades++;
		void emit(string name) => Present(new SubtitleCue(name, TimeSpan.FromSeconds(8), false),
			() => { starts.Add(name); return TimeSpan.FromSeconds(8); }, true, false, name);
		try
		{
			ResetCombatPresentations();
			pendingPresentations.Clear();
			activePresentationUntil = DateTimeOffset.MinValue;
			presentationNow = () => now;
			SubtitleTransitionRequested += fade;
			emit("Japanese card");
			now += TimeSpan.FromMilliseconds(300);
			emit("Soviet light infantry");
			now += TimeSpan.FromMilliseconds(179);
			RunQueuedPresentations();
			if (starts.Count != 1 || fades != 1) throw new InvalidDataException("A new card skipped the short fade.");
			now += TimeSpan.FromMilliseconds(2);
			RunQueuedPresentations();
			if (starts.Count != 2 || starts[^1] != "Soviet light infantry")
				throw new InvalidDataException("A Soviet play waited for the previous Japanese line instead of 180 ms.");
			emit("superseded card");
			now += TimeSpan.FromMilliseconds(50);
			emit("latest card");
			now += TimeSpan.FromMilliseconds(131);
			RunQueuedPresentations();
			if (starts.Count != 3 || starts[^1] != "latest card" || pendingPresentations.Count != 0 || fades != 2)
				throw new InvalidDataException("Rapid cards accumulated a queue or extended the fade indefinitely.");
			Console.WriteLine("Rapid cards OK: cross-nation audio starts after 180 ms, latest card replaces stale speech, no queued backlog.");
		}
		finally
		{
			presentationNow = savedClock;
			SubtitleTransitionRequested -= fade;
			ResetCombatPresentations();
			pendingPresentations.Clear();
			activePresentationUntil = DateTimeOffset.MinValue;
		}
	}

	private void VerifyCombatInterruption()
	{
		Func<DateTimeOffset> oldClock = presentationNow;
		DateTimeOffset now = DateTimeOffset.UtcNow;
		List<SubtitleCue> shown = [];
		int fades = 0;
		int audioStarts = 0;
		void collect(SubtitleCue cue) => shown.Add(cue);
		void fade(TimeSpan duration) => fades++;
		void reset()
		{
			ResetCombatPresentations();
			pendingPresentations.Clear();
			activePresentationUntil = DateTimeOffset.MinValue;
			activePresentationTransient = activePresentationIntro = false;
			shown.Clear();
			fades = audioStarts = 0;
		}
		void emit(string text, CombatReaction reaction, bool audio = true) => Present(
			new SubtitleCue(text, TimeSpan.FromSeconds(4), false),
			audio ? () => { audioStarts++; return TimeSpan.FromSeconds(4); } : null,
			true, reaction != CombatReaction.None, "soft combat check",
			transient: reaction != CombatReaction.None, reaction: reaction);
		void advance(double seconds) { now += TimeSpan.FromSeconds(seconds); RunQueuedPresentations(); }
		SubtitleRequested += collect;
		SubtitleTransitionRequested += fade;
		presentationNow = () => now;
		player.Stop();
		try
		{
			reset();
			emit("hurt", CombatReaction.Hurt);
			advance(.15);
			emit("attack", CombatReaction.Attack);
			emit("duplicate hurt", CombatReaction.Hurt);
			advance(.9);
			if (shown.Count != 1 || fades != 0 || pendingReactions.Count != 1)
				throw new InvalidDataException("A combat line was cut before its reading window, or a hit burst restarted it.");
			advance(.41);
			if (shown.Count != 1 || fades != 1 || audioStarts != 1)
				throw new InvalidDataException("The old reaction must fade before new audio starts.");
			advance(.1);
			if (shown.Count != 1) throw new InvalidDataException("Combat fade was skipped.");
			advance(.081);
			if (shown.Count != 2 || shown[1].Text != "attack" || !shown[1].FadeIn || audioStarts != 2)
				throw new InvalidDataException("Attack text and audio did not start together after the fade.");
			emit("new hurt", CombatReaction.Hurt);
			advance(1.6);
			if (fades != 1) throw new InvalidDataException("A hit cut an attack command too early.");
			advance(.61);
			advance(.181);
			if (shown.Count != 3 || shown[2].Text != "new hurt" || audioStarts != 3)
				throw new InvalidDataException("An attack did not hand over smoothly to its hit reaction.");

			reset();
			emit("played card", CombatReaction.None);
			advance(.1);
			emit("hurt old", CombatReaction.Hurt);
			emit("hurt latest", CombatReaction.Hurt);
			emit("attack old", CombatReaction.Attack);
			emit("attack latest", CombatReaction.Attack);
			if (shown.Count != 1 || pendingReactions.Count != 2 || fades != 0 ||
				pendingReactions.Any(item => item.Cue.Text.EndsWith("old", StringComparison.Ordinal)))
				throw new InvalidDataException("Combat bursts were not coalesced or interrupted a card line.");
			advance(4.5);
			if (shown.Count != 1 || pendingReactions.Count != 0)
				throw new InvalidDataException("Old combat events replayed after a card finished.");

			reset();
			emit("hurt", CombatReaction.Hurt, audio: false);
			emit("attack", CombatReaction.Attack, audio: false);
			advance(1.5);
			if (fades != 1 || audioStarts != 0) throw new InvalidDataException("Silent combat subtitles must also transition smoothly.");
			emit("new card", CombatReaction.None, audio: false);
			advance(.3);
			if (shown.Count != 2 || shown[^1].Text != "new card" || reactionTransitionTarget != null || pendingReactions.Count != 0)
				throw new InvalidDataException("An in-progress combat transition survived a new card.");
			reset();
			emit("hurt", CombatReaction.Hurt, audio: false);
			emit("attack", CombatReaction.Attack, audio: false);
			advance(1.5);
			StopAllPlayback("combat cancellation check");
			advance(.3);
			if (shown.Count != 1 || reactionTransitionTarget != null || pendingReactions.Count != 0)
				throw new InvalidDataException("A combat transition survived leaving the match.");
			Console.WriteLine("Combat interruption OK: protected reading time, fade synchronization, burst coalescing, stale expiry, and card priority.");
		}
		finally
		{
			presentationNow = oldClock;
			SubtitleRequested -= collect;
			SubtitleTransitionRequested -= fade;
			reset();
		}
	}

	private void PlayNationEvent(string category, string eventName, string reason)
	{
		if (matchNation != null && nationPacks.TryGetValue(matchNation, out var pack))
		{
			ShowNationSubtitle(pack, category, eventName, reason);
			return;
		}
		if (matchNation == VoicePackRegistry.Soviet)
			ShowSovietSubtitle(category, eventName, reason);
		else if (matchNation == VoicePackRegistry.Japan)
			Play(category, eventName, reason);
	}

	private void Present(SubtitleCue cue, Func<TimeSpan>? startAudio, bool showSubtitle,
		bool combat, string reason, bool force = false, bool transient = false, bool intro = false,
		CombatReaction reaction = CombatReaction.None)
	{
		if (intro && introCancelledForMatch && !force && !routingVerification)
		{
			Console.WriteLine(reason + ": opening suppressed after own card play");
			return;
		}
		DateTimeOffset now = presentationNow();
		PendingPresentation next = new(cue, startAudio, showSubtitle, combat, transient, intro, now, reason, reaction);
		if (force)
		{
			ResetCombatPresentations();
			pendingPresentations.Clear();
			activePresentationUntil = DateTimeOffset.MinValue;
			activePresentationTransient = false;
			activePresentationIntro = false;
			player.Stop();
		}
		else if (!combat && !intro && !routingVerification)
		{
			CancelOpeningForCard(reason);
			bool cardFadeRunning = reactionTransitionTarget is { Combat: false, Intro: false };
			DateTimeOffset existingFadeEnd = reactionFadeUntil;
			ResetCombatPresentations();
			// A new card supersedes old cards and reactions. Keep only current HQ warnings.
			pendingPresentations.RemoveAll(item => item.Intro || item.Transient || !item.Combat);
			if (now < activePresentationUntil && !activePresentationIntro)
			{
				reactionTransitionTarget = next;
				reactionFadeUntil = cardFadeRunning ? existingFadeEnd : now + ReactionFade;
				if (!cardFadeRunning)
				{
					player.FadeOut(ReactionFade);
					SubtitleTransitionRequested?.Invoke(ReactionFade);
				}
				Console.WriteLine($"New card takes over after {ReactionFade.TotalMilliseconds:0}ms fade: {reason}");
				return;
			}
			StartPresentation(next);
			return;
		}
		if (reaction != CombatReaction.None && !routingVerification)
		{
			if (reactionTransitionTarget?.Reaction == reaction)
			{
				reactionTransitionTarget = next;
				return;
			}
			// Bursts update a single pending line per reaction; they never restart
			// the beginning of the line that is already on screen.
			if (activeReaction == reaction && now - activePresentationStarted < TimeSpan.FromMilliseconds(900))
				return;
			pendingReactions.RemoveAll(item => item.Reaction == reaction);
			pendingReactions.Add(next);
			RunQueuedPresentations();
			return;
		}
		if (routingVerification ||
			(now >= activePresentationUntil && pendingPresentations.Count == 0))
		{
			StartPresentation(next);
			return;
		}
		// Ordinary card lines stay in order; combat reactions wait behind them.
		if (combat && pendingPresentations.Count(item => item.Combat) >= 6)
			return;
		int index = combat ? pendingPresentations.Count : pendingPresentations.FindIndex(item => item.Combat);
		if (index < 0) index = pendingPresentations.Count;
		pendingPresentations.Insert(index, next);
		Console.WriteLine(reason + ": queued until current subtitle finishes");
		if (now >= activePresentationUntil)
			RunQueuedPresentations();
	}

	private void RunQueuedPresentations()
	{
		DateTimeOffset now = presentationNow();
		pendingReactions.RemoveAll(item => now - item.Added > ReactionLifetime);
		if (reactionTransitionTarget is PendingPresentation target)
		{
			if (now < reactionFadeUntil) return;
			reactionTransitionTarget = null;
			player.Stop();
			StartPresentation(target, softEntry: true);
			return;
		}
		if (now < activePresentationUntil)
		{
			PendingPresentation? nextReaction = pendingReactions
				.Where(item => item.Reaction != activeReaction)
				.OrderBy(item => item.Reaction).FirstOrDefault();
			if (activeReaction == CombatReaction.None || pendingPresentations.Count != 0 || nextReaction == null)
			{
				reactionSwitchAt = null;
				return;
			}
			// An attack command gets more time before a hit report can replace it.
			double protectedSeconds = activeReaction == CombatReaction.Attack
				? Math.Clamp(activeAudioDuration.TotalSeconds * .65, 1.45, 2.2)
				: Math.Clamp(activeAudioDuration.TotalSeconds * .4, 1.05, 1.45);
			if (now - activePresentationStarted < TimeSpan.FromSeconds(protectedSeconds)) return;
			reactionSwitchAt ??= now + player.GetInterruptionDelay(TimeSpan.FromMilliseconds(450));
			if (now < reactionSwitchAt.Value) return;
			pendingReactions.Remove(nextReaction);
			reactionTransitionTarget = nextReaction;
			reactionFadeUntil = now + ReactionFade;
			Console.WriteLine($"Combat transition: {activeReaction} -> {nextReaction.Reaction}, fade={ReactionFade.TotalMilliseconds:0}ms");
			player.FadeOut(ReactionFade);
			SubtitleTransitionRequested?.Invoke(ReactionFade);
			return;
		}
		while (pendingPresentations.Count > 0)
		{
			PendingPresentation next = pendingPresentations[0];
			pendingPresentations.RemoveAt(0);
			if (next.Intro && introCancelledForMatch) continue;
			if (next.Combat && now - next.Added > TimeSpan.FromSeconds(10))
				continue;
			StartPresentation(next);
			return;
		}
		PendingPresentation? reaction = pendingReactions.OrderBy(item => item.Reaction).FirstOrDefault();
		if (reaction != null)
		{
			pendingReactions.Remove(reaction);
			StartPresentation(reaction, softEntry: activeReaction != CombatReaction.None);
		}
	}

	private void StartPresentation(PendingPresentation presentation, bool softEntry = false)
	{
		Console.WriteLine($"Presentation started: {presentation.Reason}; delay={(presentationNow() - presentation.Added).TotalMilliseconds:F0}ms");
		if (presentation.StartAudio == null) player.Stop();
		TimeSpan duration = presentation.StartAudio?.Invoke() ?? presentation.Cue.Duration;
		if (softEntry && presentation.StartAudio != null) player.FadeIn(ReactionFade);
		SubtitleCue cue = presentation.Cue with { Duration = duration, FadeIn = softEntry };
		if (presentation.ShowSubtitle)
			SubtitleRequested?.Invoke(cue);
		else SubtitleCleared?.Invoke();
		TimeSpan visibleFor = presentation.ShowSubtitle
			? TimeSpan.FromMilliseconds(Math.Max(4000,
				(duration + TimeSpan.FromMilliseconds(cue.Sequenced ? 750 : 400)).TotalMilliseconds))
			: duration;
		activePresentationStarted = presentationNow();
		activePresentationUntil = activePresentationStarted + visibleFor;
		activeAudioDuration = duration;
		activeReaction = presentation.Reaction;
		reactionSwitchAt = null;
		activePresentationTransient = presentation.Transient;
		activePresentationIntro = presentation.Intro;
	}

	private void ResetCombatPresentations()
	{
		pendingHurt = null;
		pendingReactions.Clear();
		reactionTransitionTarget = null;
		reactionSwitchAt = null;
		activeReaction = CombatReaction.None;
	}

	private void ShowNationSubtitle(NationSubtitlePack pack, string category, string eventName, string reason,
		string? resource = null, bool forcePreview = false)
	{
		bool audio = settings.AutoEnabled || forcePreview;
		bool subtitles = settings.SubtitlesEnabled || forcePreview;
		if (!audio && !subtitles) return;
		if (category == "CMD" && eventName == "INTRO")
		{
			var intro = pack.IntroLines;
			var durations = intro.Select(line => line.Duration(audio)).ToArray();
			var lines = intro.Select(line => new SubtitleLine(line.Original, line.Chinese)).ToArray();
			bool anyAudio = audio && intro.Any(line => line.HasAudio);
			if (!subtitles && !anyAudio) return;
			Present(new SubtitleCue(lines[0].Chinese, TimeSpan.FromTicks(durations.Sum(d => d.Ticks)),
				true, Sequenced: true, Speaker: intro[0].Speaker, Lines: lines, SegmentDurations: durations),
				anyAudio ? () => player.PlayTimedSequence(intro) : null,
				subtitles, false, reason, forcePreview, intro: true);
			return;
		}
		if (category != "CMD" && category != "ORDER" && !pack.Supports(category)) return;
		var selected = pack.Pick(category, eventName, resource);
		bool hasAudio = audio && selected.HasAudio;
		if (!subtitles && !hasAudio)
		{
			// A confirmed card still supersedes the old intro/card when this
			// nation has no recording and the user has subtitles switched off.
			if (eventName == "DEPLOY")
				Present(new SubtitleCue(string.Empty, TimeSpan.Zero, false), null, false, false, reason);
			return;
		}
		Present(new SubtitleCue(selected.Chinese, selected.Duration(audio), category == "CMD",
			Speaker: selected.Speaker, Original: selected.Original),
			hasAudio ? () => player.Play(selected.AudioPath) : null, subtitles,
			eventName is "HURT" or "ATTACK" or "MOVE" or "HQ_LOW" or "HQ_CRITICAL" or "ENEMY_HQ_LOW",
			reason, forcePreview, eventName is "HURT" or "ATTACK" or "MOVE", reaction: ReactionFor(eventName));
		Console.WriteLine($"{reason}: {pack.Definition.Id} {category}/{eventName} key={selected.Key}; {(hasAudio ? "audio" : "subtitle only")}");
	}

	private void ShowSovietSubtitle(string category, string eventName, string reason, string? resource = null, bool forcePreview = false)
	{
		bool playAudio = settings.AutoEnabled || forcePreview;
		bool showSubtitle = settings.SubtitlesEnabled || forcePreview;
		if (!playAudio && !showSubtitle) return;
		if (category == "CMD" && eventName == "INTRO")
		{
			IReadOnlyList<SovietSubtitleLine> intro = sovietSubtitles.IntroLines;
			SubtitleLine[] lines = intro
				.Select(line => new SubtitleLine(line.Original, line.Chinese)).ToArray();
			TimeSpan duration = playAudio
				? TimeSpan.FromTicks(intro.Sum(line => line.AudioDuration.Ticks)) : TimeSpan.FromSeconds(19);
			Present(new SubtitleCue(lines[0].Chinese, duration,
				Commander: true, Sequenced: true, Speaker: "苏联指挥官", Lines: lines,
				SegmentDurations: playAudio ? intro.Select(line => line.AudioDuration).ToArray() : null),
				playAudio ? () => player.PlaySequence(intro.Select(line => line.AudioPath).ToArray()) : null,
				showSubtitle, false, reason, forcePreview, intro: true);
			Console.WriteLine($"{reason}: Soviet commander intro ({lines.Length} segments), {(playAudio ? "playing" : "subtitle only")}");
			return;
		}
		string lineEvent = eventName == "MOVE" ? category switch
		{
			"AIR" => "DEPLOY_2", "ARTILLERY" => "DEPLOY_4", "NAVY" => "DEPLOY",
			_ => "ATTACK_2"
		} : eventName;
		SovietSubtitleLine line = sovietSubtitles.Pick(category, lineEvent, resource);
		double seconds = category == "ORDER" ? Math.Clamp(2.2 + line.Original.Length * 0.045, 3.8, 6.5) : 3.6;
		Present(new SubtitleCue(line.Chinese, TimeSpan.FromSeconds(seconds), category == "CMD",
			Speaker: line.Speaker, Original: line.Original),
			playAudio ? () => player.Play(line.AudioPath) : null, showSubtitle,
			eventName is "HURT" or "ATTACK" or "MOVE" or "HQ_LOW" or "HQ_CRITICAL" or "ENEMY_HQ_LOW",
			reason, forcePreview, eventName is "HURT" or "ATTACK" or "MOVE", reaction: ReactionFor(eventName));
		Console.WriteLine($"{reason}: Soviet {category}/{eventName} {(playAudio ? "playing " + Path.GetFileName(line.AudioPath) : "subtitle only")}");
	}

	private void Play(string voice, string eventName, string reason)
	{
		JapanVoiceLine line = voices.Pick(voice, eventName);
		Present(new SubtitleCue(line.Chinese, TimeSpan.FromSeconds(3.2), voice == "CMD", Sequenced: false,
			(voice == "CMD") ? "指挥官" : null, Original: line.Japanese),
			settings.AutoEnabled ? () => player.Play(line.Path) : null, settings.SubtitlesEnabled,
			eventName is "HURT" or "ATTACK" or "HQ_LOW" or "HQ_CRITICAL" or "ENEMY_HQ_LOW",
			reason, transient: eventName is "HURT" or "ATTACK", reaction: ReactionFor(eventName));
		Console.WriteLine(reason + (settings.AutoEnabled ? ": playing " : ": subtitle only ") + line.Id);
	}

	private void PlayDeployment(string category, string reason)
	{
		JapanVoiceLine line = voices.PickDeployment(category);
		Present(new SubtitleCue(line.Chinese, TimeSpan.FromSeconds(3.2), Commander: false, Sequenced: false,
			CategorySpeaker(category), Original: line.Japanese),
			settings.AutoEnabled ? () => player.Play(line.Path) : null, settings.SubtitlesEnabled, false, reason);
		Console.WriteLine($"{reason}: pool={category}, {(settings.AutoEnabled ? "playing" : "subtitle only")} {line.Id}");
	}

	private void PlayOrder(string reason, OrderVoiceProfile profile, JapanOrderSubtitle order)
	{
		string subtitle = order.Title + "｜" + order.Line;
		Present(new SubtitleCue(subtitle, TimeSpan.FromSeconds(3.2), Commander: false,
			Sequenced: false, Speaker: "通信兵", Original: order.Japanese),
			settings.AutoEnabled ? () => player.PlayTelegraph(profile.Telegraph) : null,
			settings.SubtitlesEnabled, false, reason);
		Console.WriteLine($"{reason}: order={order.Resource}, {(settings.AutoEnabled ? "playing telegraph" : "subtitle only")}");
	}

	private void PlayMove(CardDescription card, string reason)
	{
		if (card.Nation != null && nationPacks.TryGetValue(card.Nation, out var pack))
		{
			ShowNationSubtitle(pack, card.Category, "MOVE", reason);
			return;
		}
		if (card.Nation == VoicePackRegistry.Soviet &&
			card.Category is "INFANTRY" or "TANK" or "CAVALRY" or "AIR" or "ARTILLERY" or "NAVY")
		{
			ShowSovietSubtitle(card.Category, "MOVE", reason);
			return;
		}
		if (card.Nation == VoicePackRegistry.Japan &&
			JapanVoicePack.DeploymentPoolNames.Contains(card.Category, StringComparer.Ordinal))
		{
			JapanVoiceLine line = voices.PickMove(card.Category);
			Present(new SubtitleCue(line.Chinese, TimeSpan.FromSeconds(3.2), false,
				Speaker: CategorySpeaker(card.Category), Original: line.Japanese),
				settings.AutoEnabled ? () => player.Play(line.Path) : null,
				settings.SubtitlesEnabled, true, reason, transient: true);
			Console.WriteLine($"{reason}: Japan {card.Category}/MOVE {(settings.AutoEnabled ? "playing" : "subtitle only")} {line.Id}");
			return;
		}
		if (card.Nation is not (VoicePackRegistry.Japan or VoicePackRegistry.Soviet))
			ShowAlliedCard(card, "MOVE", reason);
	}

	private void PlayHurt(CardDescription card, string reason)
	{
		string category = card.Category;
		string? nation = card.Nation;
		if (nation != null && nationPacks.TryGetValue(nation, out var pack))
		{
			ShowNationSubtitle(pack, category, "HURT", reason);
			return;
		}
		if (nation == VoicePackRegistry.Soviet)
		{
			ShowSovietSubtitle(category, "HURT", reason);
			return;
		}
		if (nation != VoicePackRegistry.Japan)
		{
			ShowAlliedCard(card, "HURT", reason);
			return;
		}
		JapanVoiceLine line = voices.PickHurt(category);
		Present(new SubtitleCue(line.Chinese, TimeSpan.FromSeconds(3.2), Commander: false, Sequenced: false,
			CategorySpeaker(category), Original: line.Japanese),
			settings.AutoEnabled ? () => player.Play(line.Path) : null, settings.SubtitlesEnabled, true, reason,
			transient: true, reaction: CombatReaction.Hurt);
		Console.WriteLine($"{reason}: hurt pool={category}, {(settings.AutoEnabled ? "playing " : "subtitle only ")}{line.Id}");
	}

	private void PlayAttack(CardDescription card, string reason)
	{
		string category = card.Category;
		string? nation = card.Nation;
		string? resource = card.SourceName;
		if (nation != null && nationPacks.TryGetValue(nation, out var pack))
		{
			ShowNationSubtitle(pack, category, "ATTACK", reason);
			return;
		}
		if (nation == VoicePackRegistry.Soviet)
		{
			ShowSovietSubtitle(category, "ATTACK", reason);
			return;
		}
		if (nation != VoicePackRegistry.Japan)
		{
			ShowAlliedCard(card, "ATTACK", reason);
			return;
		}
		if (category == "INFANTRY" && resource != null &&
			JapaneseArtilleryCards.Contains(resource.EndsWith("_C", StringComparison.OrdinalIgnoreCase) ? resource[..^2] : resource))
			category = "ARTILLERY";
		JapanVoiceLine line = voices.PickAttack(category);
		Present(new SubtitleCue(line.Chinese, TimeSpan.FromSeconds(3.2), Commander: false,
			Speaker: CategorySpeaker(category), Original: line.Japanese),
			settings.AutoEnabled ? () => player.Play(line.Path) : null, settings.SubtitlesEnabled, true, reason,
			transient: true, reaction: CombatReaction.Attack);
		Console.WriteLine($"{reason}: attack pool={category}, {line.Id}");
	}

	private static CombatReaction ReactionFor(string eventName) => eventName switch
	{
		"ATTACK" => CombatReaction.Attack,
		"HURT" => CombatReaction.Hurt,
		_ => CombatReaction.None
	};

	private static string CategorySpeaker(string category)
	{
		return category switch
		{
			"INFANTRY" => "陆军部队", 
			"TANK" => "装甲部队", 
			"AIR" => "航空队", 
			"NAVY" => "海军部队", 
			"ARTILLERY" => "炮兵部队",
			_ => "前线部队", 
		};
	}

	private void PlayCommanderIntro(string reason, string? nation = null, bool forcePreview = false)
	{
		if (introCancelledForMatch && !forcePreview && !routingVerification)
		{
			Console.WriteLine(reason + ": opening suppressed after own card play");
			return;
		}
		nation ??= matchNation;
		if (nation != null && nationPacks.TryGetValue(nation, out var pack))
		{
			ShowNationSubtitle(pack, "CMD", "INTRO", reason, forcePreview: forcePreview);
			return;
		}
		if (nation == VoicePackRegistry.Soviet)
		{
			ShowSovietSubtitle("CMD", "INTRO", reason, forcePreview: forcePreview);
			return;
		}
		if (nation != VoicePackRegistry.Japan) return;
		Present(new SubtitleCue(CommanderSubtitle, TimeSpan.FromSeconds(10), Commander: true,
			Sequenced: true, Speaker: "指挥官", Lines: JapanCommanderLines),
			settings.AutoEnabled || forcePreview ? () => player.Play(voices.CommanderIntroPath) : null,
			settings.SubtitlesEnabled || forcePreview, false, reason, forcePreview, intro: true);
		Console.WriteLine(reason + (settings.AutoEnabled || forcePreview ? ": playing commander_intro.wav" : ": commander subtitle only"));
	}

	private void ResetOpeningForMatch(bool pending)
	{
		introPending = pending;
		introCancelledForMatch = false;
	}

	private void CancelOpeningForCard(string reason)
	{
		if (routingVerification) return;
		bool firstCard = !introCancelledForMatch;
		introCancelledForMatch = true;
		introPending = false;
		pendingPresentations.RemoveAll(item => item.Intro);
		if (reactionTransitionTarget is { Intro: true }) reactionTransitionTarget = null;
		if (activePresentationIntro)
		{
			player.Stop();
			activePresentationUntil = DateTimeOffset.MinValue;
			activePresentationIntro = false;
			activePresentationTransient = false;
			activeAudioDuration = TimeSpan.Zero;
			activeReaction = CombatReaction.None;
			reactionSwitchAt = null;
			SubtitleCleared?.Invoke();
		}
		if (firstCard) Console.WriteLine(reason + ": opening cancelled for the rest of this match");
	}

	private void Disconnect()
	{
		deploymentCandidates.Clear();
		StopAllPlayback("KARDS disconnected");
		runtime?.Dispose();
		runtime = null;
		board = (battleMonitor = 0L);
		inMatch = false;
		tableReadyCached = false;
		visibleTableSignature = (candidateMatchSignature = (matchSignature = 0L));
		monitoringTrueSamples = (monitoringFalseSamples = (previousQueueCount = 0));
		currentQueueIds.Clear();
		pendingCards.Clear();
		consumedPlayIds.Clear();
		lastDirectHandCard = 0L;
		lastAnimatedCardId = 0;
		playAnimationRunning = false;
		playPhaseActive = false;
		introPending = false;
		matchBeganAt = DateTimeOffset.MinValue;
		lastHurtVoiceAt = DateTimeOffset.MinValue;
		reusedBoardOpeningSince = DateTimeOffset.MinValue;
		recentCardVoices.Clear();
		knownHandCards.Clear();
		knownOrderHandVisuals.Clear();
		knownBoardCards.Clear();
		knownOwnBoardVisuals.Clear();
		knownOwnBoardLocations.Clear();
		activeDamageVisuals.Clear();
		lastAttackVisuals.Clear();
		pendingHurt = null;
		visibleOwnHand.Clear();
		visibleOwnHandVisuals.Clear();
		visibleOwnBoard.Clear();
		visibleOwnBoardVisuals.Clear();
		visibleOwnBoardLocations.Clear();
		visibleEnemyBoard.Clear();
		visibleBoard.Clear();
	}

	private void StopAllPlayback(string reason)
	{
		ResetCombatPresentations();
		pendingPresentations.Clear();
		activePresentationUntil = DateTimeOffset.MinValue;
		activePresentationTransient = false;
		activePresentationIntro = false;
		player.Stop();
		this.SubtitleCleared?.Invoke();
		Console.WriteLine(reason + ": audio stopped and subtitle cleared");
	}

	public void Dispose()
	{
		stop.Cancel();
		thread?.Join();
		Disconnect();
		ready.Dispose();
		stop.Dispose();
	}
}
