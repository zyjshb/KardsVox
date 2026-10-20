namespace KardsVox.Core;

internal sealed partial class MemoryMatchMonitor
{
    internal void VerifyOpeningCancellation()
    {
        bool oldVerification = routingVerification;
        bool oldCancelled = introCancelledForMatch, oldPending = introPending;
        bool oldAudio = settings.AutoEnabled, oldSubtitles = settings.SubtitlesEnabled;
        bool oldInMatch = inMatch, oldPhase = playPhaseActive;
        byte? oldSide = ownSide;
        string? oldNation = matchNation;
        var oldLastCard = lastCardVoiceAt;
        var oldClock = presentationNow;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<string> shown = [];
        List<string> audio = [];
        void collect(SubtitleCue cue) => shown.Add(cue.Text);
        void emit(string text, bool intro, bool preview = false) => Present(
            new SubtitleCue(text, TimeSpan.FromSeconds(intro ? 23 : 2), intro, Sequenced: intro),
            () => { audio.Add(text); return TimeSpan.FromSeconds(intro ? 23 : 2); },
            true, false, "opening cancellation check", force: preview, intro: intro);
        void require(bool passed, string reason)
        {
            if (!passed) throw new InvalidDataException(reason);
        }
        void reset()
        {
            StopAllPlayback("Opening test baseline");
            ResetOpeningForMatch(pending: true);
            pendingCards.Clear(); consumedPlayIds.Clear();
            previousOrderAnimationId = previousOrdersFlying = 0;
            shown.Clear(); audio.Clear();
            settings.AutoEnabled = false; settings.SubtitlesEnabled = true;
            ownSide = 1; inMatch = true; playPhaseActive = true;
        }
        SubtitleRequested += collect;
        try
        {
            routingVerification = false;
            presentationNow = () => now;
            reset();
            emit("opening", true);
            now += TimeSpan.FromSeconds(1);
            emit("first card", false);
            if (!shown.SequenceEqual(new[] { "opening", "first card" }))
                throw new InvalidDataException("First card did not replace the opening immediately.");
            // Late nation detection / delayed opening dispatch must not requeue
            // the opening behind the card and resume it after the card finishes.
            emit("late opening", true);
            now += TimeSpan.FromSeconds(30);
            RunQueuedPresentations();
            if (!shown.SequenceEqual(new[] { "opening", "first card" }) ||
                !audio.SequenceEqual(new[] { "opening", "first card" }))
                throw new InvalidDataException("Opening resumed after the first card: " + string.Join(" -> ", shown));
            emit("opening after card finished", true);
            require(shown.Count == 2 && audio.Count == 2 && !introPending,
                "A late opening restarted after the card had already finished.");
            StopAllPlayback("Switches toggled while in the same match");
            emit("opening after toggles", true);
            require(shown.Count == 2 && audio.Count == 2,
                "Stopping playback reset the per-match opening cancellation.");

            // Explicit user preview remains available, but cannot re-enable the
            // automatic opening and is itself cancelled by the next own play.
            emit("manual F6 preview", true, preview: true);
            require(activePresentationIntro && shown.Count == 3, "F6 preview was incorrectly disabled.");
            RegisterPlay(100, "own card after preview");
            require(!activePresentationIntro && activePresentationUntil == DateTimeOffset.MinValue,
                "A subsequent own play left the explicit preview running.");
            emit("automatic opening after preview", true);
            require(shown.Count == 3, "Manual preview reopened the automatic introduction.");
            ResetOpeningForMatch(pending: true);
            emit("next match opening", true);
            require(shown[^1] == "next match opening", "A new match did not restore its opening.");

            // Cancel before identity/audio resolution, including a silent card
            // and an opening that is still queued behind another presentation.
            reset();
            Present(new SubtitleCue("HQ warning", TimeSpan.FromSeconds(4), true), null,
                true, true, "opening queue fixture");
            emit("queued opening", true);
            require(pendingPresentations.Any(p => p.Intro), "Queued opening fixture did not queue.");
            settings.AutoEnabled = settings.SubtitlesEnabled = false;
            RegisterPlay(101, "own play awaiting identity");
            require(introCancelledForMatch && !introPending && !pendingPresentations.Any(p => p.Intro),
                "A confirmed silent/unresolved card failed to cancel queued or pending opening.");
            now += TimeSpan.FromSeconds(30); RunQueuedPresentations();
            require(shown.SequenceEqual(new[] { "HQ warning" }) && audio.Count == 0,
                "A cancelled queued opening later started its audio or subtitles.");

            reset();
            emit("opening before invalid signals", true);
            RegisterPlay(0, "invalid ID");
            playPhaseActive = false;
            RegisterPlay(102, "not in play phase");
            playPhaseActive = true;
            ObserveOrderAnimationState(1, 103, enemy: true);
            CardDescription unit = new(10001, "INFANTRY", "DEPLOY", 1, 1, 1, 104, null,
                "card_unit_mito_regiment_C", null, true, VoicePackRegistry.Japan);
            PlayVisualCard(unit, "idle hand reference");
            require(activePresentationIntro && !introCancelledForMatch,
                "An enemy, invalid or unconfirmed card incorrectly cancelled the opening.");
            RegisterPlay(unit.CardId, "confirmed own unit");
            require(!activePresentationIntro && introCancelledForMatch && !introPending,
                "A real unit action did not stop the opening before card identity resolution.");
            PlayVisualCard(unit, "matched own unit");
            require(shown.Count == 2 && !activePresentationIntro, "Unit subtitle did not replace the cancelled opening.");

            // All installed countries share the lifecycle, and allied unit/order
            // cues must never allow the HQ country's opening to resume.
            foreach (var definition in VoicePackRegistry.All.Where(pack => pack.Playable))
            {
                foreach (bool order in new[] { false, true })
                {
                    reset();
                    matchNation = definition.Id;
                    PlayCommanderIntro("country opening", definition.Id);
                    require(shown.Count == 1 && activePresentationIntro,
                        "Country opening fixture failed: " + definition.Id);
                    CardDescription card = order
                        ? unit with { Category = "ORDER", SourceName = "card_event_reserves_C", Nation = VoicePackRegistry.Soviet }
                        : unit;
                    RegisterPlay(card.CardId, "confirmed allied play");
                    PlayVisualCard(card, "allied card replaces country opening");
                    require(shown.Count == 2 && !activePresentationIntro,
                        "Allied card did not replace " + definition.Id + " opening.");
                    PlayCommanderIntro("late country opening", definition.Id);
                    now += TimeSpan.FromSeconds(60); RunQueuedPresentations();
                    PlayCommanderIntro("country opening after card end", definition.Id);
                    require(shown.Count == 2 && pendingPresentations.Count == 0,
                        "Country opening resumed after allied card: " + definition.Id);
                }
            }
            Console.WriteLine("Opening cancellation self-test passed: delayed/queued openings never resume; confirmed silent/unresolved plays cancel immediately; enemy/stale signals ignored; F6 and new matches work; all installed country unit/order routes passed (simulation).");
        }
        finally
        {
            StopAllPlayback("Opening test complete");
            SubtitleRequested -= collect;
            routingVerification = oldVerification;
            presentationNow = oldClock;
            introCancelledForMatch = oldCancelled; introPending = oldPending;
            settings.AutoEnabled = oldAudio; settings.SubtitlesEnabled = oldSubtitles;
            ownSide = oldSide; inMatch = oldInMatch; playPhaseActive = oldPhase;
            matchNation = oldNation; lastCardVoiceAt = oldLastCard;
            pendingCards.Clear(); consumedPlayIds.Clear();
            previousOrderAnimationId = previousOrdersFlying = 0;
        }
    }
}
