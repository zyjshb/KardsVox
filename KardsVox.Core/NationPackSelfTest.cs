using KardsVox.Audio;
using NAudio.Wave;

namespace KardsVox.Core;

internal sealed partial class MemoryMatchMonitor
{
    // Simulation of the production routes and queue; never claims a live game test.
    internal void VerifyFileBasedPacks()
    {
        static void Require(bool ok, string message)
        { if (!ok) throw new InvalidDataException(message); }
        bool oldAudio = settings.AutoEnabled, oldSubtitles = settings.SubtitlesEnabled;
        string oldSelected = settings.SelectedVoicePack;
        string? oldNation = matchNation;
        bool oldInMatch = inMatch, oldPlayPhase = playPhaseActive, oldVerification = routingVerification;
        byte? oldSide = ownSide;
        var oldNow = presentationNow;
        List<SubtitleCue> shown = [];
        void collect(SubtitleCue cue) => shown.Add(cue);
        SubtitleRequested += collect;
        string temp = Path.Combine(Path.GetTempPath(), "KardsVox-pack-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            Require(nationPacks.ContainsKey(VoicePackRegistry.Germany), "Germany is not registered.");
            Require(nationPacks.ContainsKey(VoicePackRegistry.Finland), "Finland is not registered.");
            foreach (var pack in nationPacks.Values.ToArray())
            {
                var def = pack.Definition;
                Require(pack.InvalidCount == 0, def.Id + " has corrupt voice files.");
                foreach (var resource in pack.Cards.Resources)
                {
                    Require(ResolveCardSource(resource.Key + "_C_12345", resource.Key + "_C") == resource.Key + "_C",
                        "An instanced object hid its full-pack class identity: " + resource.Key);
                    Require(IdentifyNation("Default__" + resource.Key + "_C") == def.Id,
                        "Incorrect full-pack nation: " + resource.Key);
                    Require(ClassifyCategory(resource.Key + "_C") == resource.Value,
                        "Incorrect card category: " + resource.Key);
                    if (resource.Value == "ORDER")
                        Require(pack.Pick("ORDER", "DEPLOY", resource.Key + "_C").Key == resource.Key,
                            "Order identity collapsed: " + resource.Key);
                }
                foreach (string category in pack.UnitCategories)
                foreach (string action in new[] { "DEPLOY", "ATTACK", "HURT", "MOVE" })
                {
                    int n = pack.PoolSize(category, action);
                    var cycle = Enumerable.Range(0, n).Select(_ => pack.Pick(category, action).Key).ToArray();
                    Require(cycle.Distinct().Count() == n && pack.Pick(category, action).Key != cycle[^1],
                        "Pool repeated before cycling: " + category + "/" + action);
                }
                routingVerification = true;
                settings.AutoEnabled = false;
                settings.SubtitlesEnabled = true;
                player.Stop();
                foreach (string main in VoicePackRegistry.All.Select(p => p.Id))
                {
                    matchNation = main;
                    foreach (string category in pack.UnitCategories)
                    {
                        string resource = pack.Cards.Resources.First(p => p.Value == category).Key;
                        CardDescription card = new(901, category, "DEPLOY", 0, 1, 3, 91, null, resource + "_C", null, true, def.Id);
                        shown.Clear();
                        PlayCard(card, "new-nation deployment check");
                        PlayAttack(card, "new-nation attack check");
                        PlayHurt(card, "new-nation hurt check");
                        PlayMove(card, "new-nation movement check");
                        Require(shown.Count == 4 && shown.All(cue => cue.Speaker?.StartsWith(def.Name) == true &&
                            pack.AllLines.Any(line => line.Original == cue.Original && line.Chinese == cue.Text)),
                            "Cross-nation unit events failed: " + main + "/" + category);
                    }
                    foreach (var order in pack.Cards.Resources.Where(p => p.Value == "ORDER"))
                    {
                        shown.Clear();
                        PlayCard(new(902, "ORDER", "DEPLOY", 0, 1, 0, 92, null, order.Key + "_C", null, true, def.Id),
                            "new-nation exact order check");
                        Require(shown.Count == 1 && shown[0].Original == pack.Pick("ORDER", "DEPLOY", order.Key).Original,
                            "Full order pack lost to allied fallback: " + order.Key);
                    }
                }
                matchNation = def.Id;
                shown.Clear();
                PlayCard(new(903, "TANK", "DEPLOY", 0, 1, 3, 93, null, "card_unit_t_34_C", null, true, VoicePackRegistry.Soviet), "German HQ Soviet ally check");
                PlayCard(new(904, "INFANTRY", "DEPLOY", 0, 1, 3, 94, null, "card_unit_mito_regiment_C", null, true, VoicePackRegistry.Japan), "German HQ Japanese ally check");
                Require(shown.Count == 2 && shown[0].Speaker == "苏联装甲部队" && shown[1].Speaker == "陆军部队", "German HQ changed allied identities.");
                shown.Clear();
                PlayCommanderIntro("new-nation intro check", def.Id);
                Require(shown.Count == 1 && shown[0].Lines?.Count == pack.IntroLines.Count &&
                    shown[0].SegmentDurations?.SequenceEqual(pack.IntroLines.Select(l => l.ReadingDuration)) == true,
                    "Silent intro reading intervals missing.");
                foreach (string action in new[] { "HQ_LOW", "HQ_CRITICAL", "ENEMY_HQ_LOW" })
                    PlayNationEvent("CMD", action, "new-nation HQ check");
                Require(shown.Count == 4 && shown.All(c => c.Speaker == def.Name + "指挥官"), "HQ nation routing failed.");
                inMatch = true;
                matchNation = VoicePackRegistry.Japan;
                settings.SelectedVoicePack = def.Id;
                shown.Clear();
                Hotkey(VoxHotkey.Play);
                Require(shown.Count == 1 && shown[0].Speaker == def.Name + "指挥官" && matchNation == VoicePackRegistry.Japan,
                    "F6 selection must be independent of the live HQ nation.");

                // Same resource, different CardIDs; an old reference and an enemy
                // cannot consume the queued action. Exercise the real 180 ms path.
                StopAllPlayback("new-nation evidence baseline");
                ResetOpeningForMatch(pending: true);
                shown.Clear(); routingVerification = false; playPhaseActive = true; ownSide = 0;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                presentationNow = () => now;
                string inf = pack.Cards.Resources.First(p => p.Value == "INFANTRY").Key;
                CardDescription first = new(911, "INFANTRY", "DEPLOY", 0, 1, 3, 991, null, inf + "_C", null, true, def.Id);
                settings.AutoEnabled = true; settings.SubtitlesEnabled = false;
                PlayCommanderIntro("intro before a silent German card", VoicePackRegistry.Japan);
                Require(player.PlaybackState == PlaybackState.Playing, "Intro fixture did not start.");
                // An audio-less new card must stop the previous nation's intro
                // even in audio-only mode (there is no German WAV to replace it).
                if (pack.AudioCount == 0)
                {
                    PlayCard(first, "silent German card interrupts intro");
                    Require(player.PlaybackState == PlaybackState.Stopped && !activePresentationIntro && shown.Count == 0,
                        "A German card without audio left the old intro playing.");
                }
                StopAllPlayback("German evidence reset");
                settings.AutoEnabled = false; settings.SubtitlesEnabled = true;
                RegisterPlay(991, "German queued action");
                PlayVisualCard(first with { CardId = 992 }, "stale reference check");
                PlayVisualCard(first with { Side = 1 }, "enemy reference check");
                Require(shown.Count == 0 && pendingCards.Count == 1, "An unrelated/enemy reference consumed a play.");
                PlayVisualCard(first, "German first unit");
                RegisterPlay(992, "German same-name second action");
                PlayVisualCard(first with { Instance = 912, CardId = 992 }, "German second unit");
                now += TimeSpan.FromMilliseconds(181); RunQueuedPresentations();
                Require(shown.Count == 2 && consumedPlayIds.Contains(991) && consumedPlayIds.Contains(992),
                    "Two same-name German cards did not produce two cues.");
                PlayVisualCard(first with { Instance = 913 }, "recreated object check");
                Require(shown.Count == 2, "Recreated object replayed a consumed action.");
                StopAllPlayback("new-nation cancellation check");
                Require(pendingPresentations.Count == 0 && pendingReactions.Count == 0 && reactionTransitionTarget == null,
                    "Queued German presentation survived cancellation.");
                pendingCards.Clear(); consumedPlayIds.Clear();
                presentationNow = oldNow; routingVerification = true;

                // Test fixtures are generated only in a unique temp directory and
                // deleted afterwards. They are never delivered as German recordings.
                string data = Path.Combine(temp, "Data", def.Id), audio = Path.Combine(temp, "Audio", def.Id);
                Directory.CreateDirectory(data); Directory.CreateDirectory(audio);
                foreach (string file in new[] { "card_catalog.tsv", "subtitle_lines.tsv", "order_lines.tsv" })
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", def.Id, file), Path.Combine(data, file), true);
                string orderKey = pack.Cards.Resources.First(p => p.Value == "ORDER").Key;
                string fixture = Path.Combine(audio, def.AudioPrefix + "_ORDER_" + orderKey + ".wav");
                using (var wav = new WaveFileWriter(fixture, new WaveFormat(48000, 16, 1))) wav.Write(new byte[48000], 0, 48000);
                using (var wav = new WaveFileWriter(Path.Combine(audio, def.AudioPrefix + "_SUB_CMD_INTRO_1.wav"), new WaveFormat(48000, 16, 1))) wav.Write(new byte[48000], 0, 48000);
                string bad = Path.Combine(audio, def.AudioPrefix + "_SUB_CMD_HQ_LOW.wav");
                File.WriteAllText(bad, "corrupt test fixture");
                var partial = new NationSubtitlePack(temp, def);
                Require(partial.AudioCount == 2 && partial.InvalidCount == 1 && partial.MissingCount == partial.AllLines.Count() - 3,
                    "Missing, invalid and usable audio were not distinguished.");
                nationPacks[def.Id] = partial;
                try
                {
                    settings.AutoEnabled = true; settings.SubtitlesEnabled = false; shown.Clear();
                    PlayCard(new(921, "ORDER", "DEPLOY", 0, 1, 0, 99, null, orderKey + "_C", null, true, def.Id), "new-nation audio-only check");
                    Require(shown.Count == 0 && player.PlaybackState == PlaybackState.Playing, "Available order audio requires subtitles.");
                    player.Stop(); settings.SubtitlesEnabled = true;
                    PlayCommanderIntro("new-nation partial intro check", def.Id);
                    Require(shown.Count == 1 && shown[0].SegmentDurations?[0] == TimeSpan.FromSeconds(.5) &&
                        shown[0].SegmentDurations?[1] == partial.IntroLines[1].ReadingDuration && player.PlaybackState == PlaybackState.Playing,
                        "Partial intro audio/subtitle segment timing differs.");
                    player.Stop(); settings.AutoEnabled = false; shown.Clear();
                    PlayCard(new(921, "ORDER", "DEPLOY", 0, 1, 0, 99, null, orderKey + "_C", null, true, def.Id), "new-nation subtitle-only check");
                    Require(shown.Count == 1 && shown[0].Duration > TimeSpan.FromSeconds(.5) && player.PlaybackState == PlaybackState.Stopped,
                        "Muted voice did not use subtitle reading time.");
                    settings.AutoEnabled = true; shown.Clear();
                    matchNation = def.Id;
                    PlayNationEvent("CMD", "HQ_LOW", "new-nation corrupt audio fallback");
                    Require(shown.Count == 1 && player.PlaybackState == PlaybackState.Stopped, "Corrupt audio disabled subtitles.");
                }
                finally { nationPacks[def.Id] = pack; player.Stop(); }
                Console.WriteLine($"{def.Id} OK: {pack.Cards.Count} resources, {pack.OrderCount} exact orders, {pack.AllLines.Count()} lines; audio ready={pack.AudioCount}, pending={pack.MissingCount}. Cross-nation events, F6, same-name CardIDs, optional/corrupt/partial audio and independent switches passed (simulation).");
            }
        }
        finally
        {
            StopAllPlayback("new-nation checks complete");
            SubtitleRequested -= collect;
            settings.AutoEnabled = oldAudio; settings.SubtitlesEnabled = oldSubtitles; settings.SelectedVoicePack = oldSelected;
            matchNation = oldNation; inMatch = oldInMatch; ownSide = oldSide; playPhaseActive = oldPlayPhase;
            routingVerification = oldVerification; presentationNow = oldNow;
            pendingCards.Clear(); consumedPlayIds.Clear();
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }
}
