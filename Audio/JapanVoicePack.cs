namespace KardsVox.Audio;

internal sealed record JapanVoiceLine(string Id, string Voice, string Event, string Take, string Japanese, string Chinese, string Path);

internal sealed class JapanVoicePack
{
    public const string InfantryPool = "INFANTRY";
    public const string TankPool = "TANK";
    public const string AirPool = "AIR";
    public const string NavyPool = "NAVY";
    public const string OrderPool = "ORDER";

    public static readonly string[] DeploymentPoolNames = [InfantryPool, TankPool, AirPool, NavyPool];

    private static readonly HashSet<string> InfantryEvents =
        new(["DEPLOY", "SELECT", "MOVE", "CHARGE", "HOLD", "RALLY", "BARRAGE"], StringComparer.Ordinal);
    private static readonly HashSet<string> TankEvents =
        new(["DEPLOY", "SELECT", "MOVE", "ATTACK", "FRONT_TAKEN", "REPAIR"], StringComparer.Ordinal);
    private static readonly HashSet<string> AirEvents =
        new(["DEPLOY", "SELECT", "ATTACK", "COVER", "RETURN"], StringComparer.Ordinal);
    private static readonly HashSet<string> NavyEvents =
        new(["DEPLOY", "SELECT", "ATTACK", "CARRIER"], StringComparer.Ordinal);

    private readonly Dictionary<string, JapanVoiceLine> byId;
    private readonly Dictionary<(string Voice, string Event), JapanVoiceLine[]> byEvent;
    private readonly Dictionary<string, JapanVoiceLine[]> deploymentPools;
    private readonly Dictionary<string, JapanVoiceLine[]> hurtPools;
    private readonly JapanVoiceLine[] orderPool;
    private readonly string[] hurtEffects;
    private readonly Dictionary<string, ShuffleBag> bags = new(StringComparer.Ordinal);
    private readonly object pickGate = new();
    private readonly OrderVoiceProfiles orderProfiles;
    private readonly NationCardCatalog cardCatalog;

    public int Count => byId.Count;
    public string CommanderIntroPath { get; }
    public int OrderPoolSize => orderPool.Length;
    public int HurtSfxCount => hurtEffects.Length;
    public int JapanCardCount => cardCatalog.Count;

    public JapanVoicePack(string runtimeRoot)
    {
        string manifest = System.IO.Path.Combine(runtimeRoot, "Data", "Japan", "voice_lines.tsv");
        if (!File.Exists(manifest)) throw new FileNotFoundException("Japan voice manifest is missing", manifest);
        orderProfiles = new OrderVoiceProfiles(runtimeRoot);
        cardCatalog = new NationCardCatalog(runtimeRoot, "Japan", 312);

        var lines = new List<JapanVoiceLine>();
        foreach (string row in File.ReadLines(manifest).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            string[] columns = row.Split('\t');
            if (columns.Length != 6) throw new InvalidDataException($"Invalid Japan voice manifest row: {row}");
            string id = columns[0];
            if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                throw new InvalidDataException($"Invalid Japan voice ID: {id}");
            string path = System.IO.Path.Combine(runtimeRoot, "Audio", "Japan", id + ".wav");
            if (!File.Exists(path)) throw new FileNotFoundException($"Japan voice clip is missing: {id}", path);
            lines.Add(new JapanVoiceLine(id, columns[1], columns[2], columns[3], columns[4], columns[5], path));
        }

        byId = new Dictionary<string, JapanVoiceLine>(StringComparer.Ordinal);
        foreach (var line in lines)
            if (!byId.TryAdd(line.Id, line)) throw new InvalidDataException($"Duplicate Japan voice ID: {line.Id}");

        byEvent = lines.GroupBy(line => (line.Voice, line.Event))
            .ToDictionary(group => group.Key, group => group.ToArray());

        deploymentPools = new Dictionary<string, JapanVoiceLine[]>(StringComparer.Ordinal)
        {
            [InfantryPool] = lines.Where(line =>
                (line.Voice is "INF_Y" or "INF_V" or "ARTY") && InfantryEvents.Contains(line.Event)).ToArray(),
            [TankPool] = lines.Where(line => line.Voice == "TANK" && TankEvents.Contains(line.Event)).ToArray(),
            [AirPool] = lines.Where(line =>
                (line.Voice is "FTR" or "BMB") && AirEvents.Contains(line.Event)).ToArray(),
            [NavyPool] = lines.Where(line => line.Voice == "NAV" && NavyEvents.Contains(line.Event)).ToArray()
        };
        hurtPools = new Dictionary<string, JapanVoiceLine[]>(StringComparer.Ordinal)
        {
            [InfantryPool] = lines.Where(line =>
                (line.Voice is "INF_Y" or "INF_V" or "ARTY") && line.Event == "HURT").ToArray(),
            [TankPool] = lines.Where(line => line.Voice == "TANK" && line.Event == "HURT").ToArray(),
            [AirPool] = lines.Where(line =>
                (line.Voice is "FTR" or "BMB") && line.Event == "HURT").ToArray(),
            [NavyPool] = lines.Where(line => line.Voice == "NAV" && line.Event == "HURT").ToArray()
        };
        orderPool = lines.Where(line => line.Voice == "OPS" && line.Event.StartsWith("ORDER_", StringComparison.Ordinal)).ToArray();
        string hurtDirectory = System.IO.Path.Combine(runtimeRoot, "Audio", "Sfx", "Hurt");
        hurtEffects = Directory.Exists(hurtDirectory)
            ? Directory.GetFiles(hurtDirectory, "CC0_HURT_*.mp3").OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : [];

        CommanderIntroPath = System.IO.Path.Combine(runtimeRoot, "Audio", "Test", "intro.wav");
        if (!File.Exists(CommanderIntroPath))
            throw new FileNotFoundException("Commander intro clip is missing", CommanderIntroPath);
        if (lines.Count != 144 || !byEvent.ContainsKey(("CMD", "INTRO")))
            throw new InvalidDataException($"Incomplete Japan voice pack: {lines.Count}/144 clips");
        if (deploymentPools.Any(pair => pair.Value.Length < 4) || hurtPools.Any(pair => pair.Value.Length == 0) ||
            orderPool.Length < 8 || hurtEffects.Length < 4)
            throw new InvalidDataException("One or more category voice pools are incomplete.");
    }

    public JapanVoiceLine ById(string id) => byId.TryGetValue(id, out var line)
        ? line : throw new KeyNotFoundException($"Unknown Japan voice ID: {id}");

    public OrderVoiceProfile ResolveOrder(params string?[] names) => orderProfiles.Resolve(names);

    public string? ClassifyJapanCard(params string?[] names) => cardCatalog.Classify(names);

    public int GetDeploymentPoolSize(string category) => deploymentPools.TryGetValue(category, out var pool)
        ? pool.Length : throw new KeyNotFoundException($"Unknown Japan deployment pool: {category}");

    public JapanVoiceLine Pick(string voice, string eventName)
    {
        var key = (voice, eventName);
        if (!byEvent.TryGetValue(key, out var candidates))
            throw new KeyNotFoundException($"Unknown Japan voice event: {voice}/{eventName}");
        return PickFrom($"EVENT:{voice}:{eventName}", candidates);
    }

    public JapanVoiceLine PickDeployment(string category)
    {
        if (!deploymentPools.TryGetValue(category, out var candidates))
            candidates = deploymentPools[InfantryPool];
        return PickFrom($"DEPLOY:{category}", candidates);
    }

    public JapanVoiceLine PickOrder(string? preferredEvent = null)
    {
        // Order identity controls the subtitle and telegraph pattern. Spoken
        // lines rotate through the whole operations pool so one frequently
        // used order cannot get stuck on one or two clips.
        _ = preferredEvent;
        return PickFrom("ORDER:ALL", orderPool);
    }

    public (string EffectPath, JapanVoiceLine Line) PickHurt(string category)
    {
        if (!hurtPools.TryGetValue(category, out JapanVoiceLine[]? candidates))
            candidates = hurtPools[InfantryPool];
        JapanVoiceLine line = PickFrom($"HURT:{category}", candidates);
        lock (pickGate)
        {
            if (!bags.TryGetValue("HURT:SFX", out var bag) || bag.Size != hurtEffects.Length)
                bags["HURT:SFX"] = bag = new ShuffleBag(hurtEffects.Length);
            return (hurtEffects[bag.Next()], line);
        }
    }

    public int GetHurtPoolSize(string category) => hurtPools.TryGetValue(category, out var pool)
        ? pool.Length : throw new KeyNotFoundException($"Unknown hurt pool: {category}");

    private JapanVoiceLine PickFrom(string key, JapanVoiceLine[] candidates)
    {
        if (candidates.Length == 0) throw new InvalidDataException($"Empty voice pool: {key}");
        lock (pickGate)
        {
            if (!bags.TryGetValue(key, out var bag) || bag.Size != candidates.Length)
                bags[key] = bag = new ShuffleBag(candidates.Length);
            return candidates[bag.Next()];
        }
    }

    private sealed class ShuffleBag(int size)
    {
        private readonly int[] order = new int[size];
        private int position = size;
        private int previous = -1;

        public int Size => order.Length;

        public int Next()
        {
            if (position >= order.Length) Refill();
            int value = order[position++];
            previous = value;
            return value;
        }

        private void Refill()
        {
            for (int index = 0; index < order.Length; index++) order[index] = index;
            for (int index = order.Length - 1; index > 0; index--)
            {
                int swap = Random.Shared.Next(index + 1);
                (order[index], order[swap]) = (order[swap], order[index]);
            }
            if (order.Length > 1 && order[0] == previous)
            {
                int swap = Random.Shared.Next(1, order.Length);
                (order[0], order[swap]) = (order[swap], order[0]);
            }
            position = 0;
        }
    }
}
