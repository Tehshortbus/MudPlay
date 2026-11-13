using MudPlay.Game.Map;
using MudPlay.Terminal;

namespace MudPlay.Services.Api;

// Builds the read payloads. Every method here runs ON THE UI THREAD — the
// server marshals before calling, because RoomTracker, the engines and the
// view-model state are all UI-thread-confined. Keep the work here to reading
// fields and shaping records; anything slow blocks the render loop.
//
// The curated Snapshot is deliberately small and hand-picked rather than
// generated. It answers the question that kept being unanswerable from a bug
// report: "what does the client believe right now, and what is holding it?" —
// in particular the gate history, which records WHO asserted a pause and WHEN.
// GET /state/full delegates to BugReportBuilder for the exhaustive version, so
// there is one collector for deep state, not two that drift.
public static class LocalApiState
{
    public static object Snapshot(AppServices svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        RoomState tracker = svc.RoomTracker.State;
        return new
        {
            capturedAt = DateTimeOffset.Now,
            version = AppInfo.Version,
            character = svc.PlayerStats.Name,
            realm = svc.GameData.ActiveRealm.ToString(),
            // First thing to check on a client that looks stuck. Without it a
            // consumer has to infer a dropped session from the log going quiet,
            // which is indistinguishable from a client that's merely idle.
            connection = new
            {
                connected = svc.Connection.Connected,
                connecting = svc.Connection.Connecting,
                reconnectPending = svc.Connection.ReconnectPending,
            },
            player = new
            {
                hp = svc.PlayerState.Hp,
                maxHp = svc.PlayerState.MaxHp,
                ma = svc.PlayerState.Ma,
                maxMa = svc.PlayerState.MaxMa,
                position = svc.PlayerState.Position,
                encumbrance = svc.PlayerState.Encumbrance,
                inCombat = svc.PlayerState.InCombat,
                mortallyWounded = svc.PlayerState.IsMortallyWounded,
                hidden = svc.PlayerState.IsHidden,
                sneaking = svc.PlayerState.IsSneaking,
                hasPromptData = svc.PlayerState.HasPromptData,
                level = svc.PlayerStats.Level,
                lives = svc.PlayerStats.Lives,
            },
            room = new
            {
                key = tracker.CurrentRoom?.Key.ToString(),
                name = tracker.CurrentRoom?.Name,
                confidence = tracker.Confidence,
                exits = tracker.ObservedExitDirections?.Select(d => d.ToString()).ToArray(),
                dark = svc.RoomTracker.IsInDarkRoom,
            },
            movement = new
            {
                paused = svc.MovementCoordinator.IsPaused,
                // The field that was missing when a two-hour pause had to be
                // explained: the gate NAMES, and below them who set them.
                assertedGates = svc.MovementCoordinator.AssertedGates.ToArray(),
                walker = svc.Walker.State.ToString(),
                loop = svc.LoopRunner.State.ToString(),
                autoLairPhase = svc.AutoLair.Phase.ToString(),
                autoLairActive = svc.AutoLair.IsActive,
                recoveryTier = svc.Recovery.CurrentTier.ToString(),
            },
            combat = new
            {
                target = svc.Combat.CurrentTarget,
                hostilesPresent = svc.CombatTracker.HasHostileMonster,
                engageable = svc.CombatTracker.HasEngageableHostiles,
            },
            log = new
            {
                // The cursor to hand back to GET /log?since= — included here so a
                // consumer can snapshot state and start tailing with no gap.
                newestSeq = svc.Log.TotalAppended,
                retained = svc.Log.Count,
            },
        };
    }

    // Session Stats as numbers: the figures the bug report's Session activity and
    // Session combat stats sections print as sentences, for a program to read
    // without parsing wording that can change. Durations are seconds, rates per
    // hour over the rate window.
    public static object Stats(AppServices svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        (Game.Calculators.TimeToLevelEstimator.Result estimate, TimeSpan? levelIn) = svc.SelfTimeToLevel();
        return Stats(svc.PlayerStats.Name, svc.PlayerStats.Level, svc.SessionActivity.Snapshot(), estimate, levelIn,
            svc.TimeAnalysis.Snapshot(), svc.CombatSession.Snapshot(), svc.CombatSession.RecognisedSpells);
    }

    public static object Stats(string? character, int level, Game.Combat.SessionActivityStats a,
        Game.Calculators.TimeToLevelEstimator.Result estimate, TimeSpan? levelIn,
        Game.Combat.TimeAnalysisStats t, Game.Combat.CombatSessionStats c, IReadOnlyList<string> recognisedSpells)
    {
        ArgumentNullException.ThrowIfNull(recognisedSpells);
        static double R(double v) => Math.Round(v, 2);
        static double S(TimeSpan span) => Math.Round(span.TotalSeconds, 1);
        bool levelKnown = estimate.TargetLevel > 0;
        return new
        {
            capturedAt = DateTimeOffset.Now,
            character,
            level,
            session = new
            {
                rateWindowSeconds = S(a.TimeOnline),
                kills = a.MonstersKilled,
                killsPerHour = R(a.KillsPerHour),
                experience = a.ExperienceEarned,
                experiencePerHour = R(a.ExperiencePerHour),
                // Null when the exp table can't say what the next level costs.
                experienceNeeded = levelKnown ? estimate.ExpNeeded : (long?)null,
                nextLevel = levelKnown ? estimate.TargetLevel : (int?)null,
                levelsBanked = estimate.BankableLevels,
                // Null while there is no exp rate to project from.
                levelInSeconds = levelIn is { } eta ? S(eta) : (double?)null,
                copperCollected = a.CurrencyCollected,
                copperPerHour = R(a.CurrencyPerHour),
                coinsCollected = a.CoinsCollected,
                coinsPerHour = R(a.CoinsPerHour),
                itemsCollected = a.ItemsCollected,
                copperDeposited = a.CurrencyDeposited,
                itemsSold = a.ItemsSold,
                copperStashed = a.CurrencyStashed,
                coinsStashed = a.CoinsStashed,
                itemsStashed = a.ItemsStashed,
                sneakEntries = a.SneakEntries,
                sneakHeld = a.SneakHeld,
                disarmAttempts = a.DisarmAttempts,
                trapsDisarmed = a.TrapsDisarmed,
                stepsTimed = a.Steps,
                averageStepSeconds = a.AverageStep is { } step ? Math.Round(step.TotalSeconds, 2) : (double?)null,
            },
            time = new
            {
                onSeconds = S(t.TimeOn),
                movingSeconds = S(t.Moving),
                attackingSeconds = S(t.Attacking),
                restingHpSeconds = S(t.RestingHp),
                restingMaSeconds = S(t.RestingMa),
                waitingSeconds = S(t.Waiting),
                waitingBy = t.WaitingBy.Select(w => new { cause = w.Cause, seconds = S(w.Time) }).ToArray(),
                blindedSeconds = S(t.Blinded),
                poisonedSeconds = S(t.Poisoned),
                diseasedSeconds = S(t.Diseased),
                confusedSeconds = S(t.Confused),
                heldSeconds = S(t.Held),
            },
            combat = new
            {
                attacks = c.TotalSwings,
                hits = c.Hits,
                crits = c.Crits,
                misses = c.Misses,
                hitPercent = R(c.HitPercent),
                critPercent = R(c.CritPercent),
                backstabs = new { attempts = c.BackstabAttempts, landed = c.Backstabs, failed = c.BackstabFails, percent = R(c.BackstabPercent) },
                swingDamage = new { min = c.PhysicalMinDamage, max = c.PhysicalMaxDamage, average = R(c.PhysicalAvgDamage), total = c.PhysicalTotalDamage },
                procs = new { hits = c.ProcHits, min = c.ProcMinDamage, max = c.ProcMaxDamage, total = c.ProcTotalDamage },
                spells = c.Spells.Select(s => new
                {
                    name = s.Name, landed = s.Landed, resisted = s.Misses,
                    min = s.MinDamage, max = s.MaxDamage, total = s.TotalDamage,
                }).ToArray(),
                hitBy = new
                {
                    hits = c.MobHits, min = c.HitTakenMinDamage, max = c.HitTakenMaxDamage, average = R(c.HitTakenAvgDamage),
                    avoided = c.AvoidedAttacks, incoming = c.IncomingAttacks,
                },
                roundsWithDamage = new { count = c.RoundsWithDamage, min = c.RoundMinDamage, max = c.RoundMaxDamage, average = R(c.RoundAvgDamage) },
                roundsToKill = new { kills = c.TimedKills, min = c.RoundsToKillMin, max = c.RoundsToKillMax, average = R(c.AvgRoundsToKill) },
                recognisedSpells,
            },
        };
    }

    // Recent gate transitions, newest last, straight from the coordinator's own
    // 200-entry history. This is the answer to "who paused movement, and when" —
    // which no bug report could previously produce.
    // A transition's reason is free text from whoever raised the gate: masked
    // with the scrubber the caller built for this response, as GET /events masks
    // the same field.
    public static object Gates(MovementCoordinator coordinator, BugReportScrub.Scrubber scrubber)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(scrubber);
        return new
        {
            asserted = coordinator.AssertedGates.ToArray(),
            history = coordinator.History.Select(h => new
            {
                at = h.Timestamp,
                gate = h.Gate,
                action = h.Asserted ? "assert" : "clear",
                asserter = h.Asserter,
                reason = h.Reason is { } reason ? scrubber.Apply(reason) : null,
            }).ToArray(),
        };
    }

    // severities: set membership, NOT a minimum. LogSeverity puts Combat after
    // Error, so a ">= Warn" threshold would silently pull in every combat trace —
    // which is why the enum itself says consumers must not compare numerically.
    // Empty set means no severity filtering.
    //
    // scrubber masks the saved login name and passwords in each entry's text, as a
    // bug report does: this is the same log leaving the client by another door.
    // The caller builds it for this one response and drops it.
    public static object Log(LogService log, BugReportScrub.Scrubber scrubber, long since, int limit,
        IReadOnlySet<LogSeverity> severities, string? source)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(scrubber);

        // Deliberately UNLIMITED at the ring, then limited after filtering. The
        // ring holds a few thousand entries, so pulling the whole window is cheap — and
        // limiting first would apply the cap to raw entries, making a filtered
        // query return whatever happened to fall inside the first N. Asking for
        // `?source=LocalApi&limit=50` really would come back empty while the
        // entries sat further along.
        LogEntry[] entries = log.SnapshotAfter(since, out long newestSeq, out _);

        // Sequences are derived rather than stored per entry: the batch is
        // contiguous and ends at newestSeq. Numbering BEFORE filtering is what
        // lets the cursor skip past entries the caller filtered out instead of
        // rescanning them forever.
        long firstSeq = newestSeq - entries.Length + 1;
        var matched = entries
            .Select((e, i) => (Seq: firstSeq + i, Entry: e))
            .Where(r => severities.Count == 0 || severities.Contains(r.Entry.Severity))
            .Where(r => source is null || r.Entry.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Two different intents, so two paging rules:
        //  - no `since` — an ad-hoc look, so give the most RECENT matches (a tail);
        //  - with `since` — a poller making forward progress, so give the OLDEST
        //    matches after the cursor and never skip any.
        bool tail = since <= 0;
        var page = tail
            ? matched[Math.Max(0, matched.Length - limit)..]
            : matched[..Math.Min(limit, matched.Length)];

        // nextSince is the cursor to send next time. When the page was truncated
        // it must stop at the last entry actually returned, or the untruncated
        // remainder would be skipped; otherwise it advances to the newest entry
        // examined, so filtered-out entries aren't rescanned.
        long nextSince = !tail && page.Length < matched.Length && page.Length > 0
            ? page[^1].Seq
            : newestSeq;

        return new
        {
            newestSeq,
            nextSince,
            retained = log.Count,
            matched = matched.Length,
            count = page.Length,
            // Masked here, after paging: only what is sent pays for it.
            entries = page.Select(r => new
            {
                seq = r.Seq,
                at = r.Entry.Timestamp,
                severity = r.Entry.Severity,
                source = r.Entry.Source,
                message = scrubber.Apply(r.Entry.Message),
                context = r.Entry.Context is { } context ? scrubber.Apply(context) : null,
            }).ToArray(),
        };
    }

    // only is the time spent in the game (InGameCapture.Window), the window a bug
    // report's scrollback is held to: a board's login screen, with the account
    // name as typed, is not sent. scrubber masks the saved login name and
    // passwords in what is left. The caller builds it for this one response.
    // nextSince: the newest game line's number, the cursor a caller polls
    // ScrollbackSince from to carry on where this screen tail leaves off.
    public static object Scrollback(TerminalEmulator emulator, CaptureWindow only,
        BugReportScrub.Scrubber scrubber, int lines, long nextSince)
    {
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentNullException.ThrowIfNull(only);
        ArgumentNullException.ThrowIfNull(scrubber);
        IReadOnlyList<TranscriptSnapshot.Line> tail = TranscriptSnapshot.Tail(emulator, lines, only: only);
        List<string> unmasked = tail.Select(l => l.Text).ToList();
        // A password's echo that a narrow terminal wrapped, as in the report.
        BugReportScrub.MaskWrappedPasswordEchoes(
            unmasked, tail.Select(l => l.SoftWrapped).ToList(), emulator.Screen.Cols);
        // Masked as one text, so a value the terminal wrapped onto the next row is
        // still found. The scrubber gives back as many rows as it was given.
        string[] texts = scrubber.Apply(string.Join('\n', unmasked)).Split('\n');
        // Timestamps come through as their own field rather than baked into the
        // text: a consumer correlating a screen line with a log entry needs to
        // compare them, not parse them back out of a rendered string.
        var rows = tail
            .Select((l, i) => new { at = l.Timestamp, text = texts[i] })
            .ToArray();
        return new { requested = lines, count = rows.Length, inGameNow = only.IsOn, nextSince, lines = rows };
    }

    // The game's lines numbered after since, oldest first, for a caller polling
    // for what is new. Paged like GET /log with a cursor: at most limit lines, and
    // nextSince stops at the last one sent when more are waiting. A line written
    // outside the time in the game is skipped but still passed by the cursor.
    // since past the newest number means MudPlay was restarted and numbered from 1
    // again: everything kept is sent, flagged restarted.
    public static object ScrollbackSince(LocalApiLines lines, CaptureWindow only,
        BugReportScrub.Scrubber scrubber, long since, int limit)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(only);
        ArgumentNullException.ThrowIfNull(scrubber);
        bool restarted = since > lines.NewestSeq;
        if (restarted) since = 0;
        (IReadOnlyList<LocalApiLines.Entry> after, long lost) = lines.After(since);
        List<LocalApiLines.Entry> page = after.Take(limit).ToList();
        long nextSince = page.Count < after.Count ? page[^1].Seq : lines.NewestSeq;
        var rows = page
            .Where(e => only.Covers(e.At))
            .Select(e => new { seq = e.Seq, at = e.At, text = scrubber.Apply(e.Text), prompt = e.IsPrompt, chat = e.IsChat })
            .ToArray();
        return new { since, nextSince, restarted, lost, count = rows.Length, inGameNow = only.IsOn, lines = rows };
    }

    // How long a capture is reused before a fresh one is built. One call costs
    // what pressing the Bug Report button costs — unnoticeable once. But this is
    // the obvious "just give me everything" endpoint to put on a timer, and a
    // full build runs on the UI thread, so poll rate turns it into a stutter on
    // the render loop. Reusing for a beat makes a hot loop nearly free and leaves
    // a single manual call byte-for-byte unchanged. A second of staleness doesn't
    // change what the answer means, and the response carries the capture's own
    // capturedAt, so a consumer can always see exactly how old it is.
    private static readonly TimeSpan CaptureReuseWindow = TimeSpan.FromSeconds(1);

    // UI-thread confined, like everything else here: both callers arrive through
    // LocalApiServer.OnUiAsync, so these need no lock. A caller reaching them from
    // anywhere else would be racing, which is reason enough not to.
    private static BugReportBuilder.BugReportCapture? _lastCapture;
    private static DateTimeOffset _lastCaptureAt;

    private static BugReportBuilder.BugReportCapture RecentCapture(AppServices svc, TerminalEmulator emulator)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_lastCapture is { } cached && now - _lastCaptureAt < CaptureReuseWindow) return cached;
        // Kept already masked and without its scrubber: what is held between
        // requests must not include the account details in clear.
        _lastCapture = BugReportBuilder.Prescrubbed(BugReportBuilder.Capture(svc, emulator));
        _lastCaptureAt = now;
        return _lastCapture;
    }

    // The exhaustive dump. BugReportBuilder already assembles every section a bug
    // report carries, each defensively so one broken subsystem can't take the
    // whole capture down — exactly the property this endpoint wants, since it's
    // called when something is already wrong.
    public static object Full(AppServices svc, TerminalEmulator emulator)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(emulator);
        BugReportBuilder.BugReportCapture capture = RecentCapture(svc, emulator);
        return new
        {
            capturedAt = capture.CapturedAt,
            realm = capture.Realm.ToString(),
            sections = BugReportBuilder.SectionTexts(capture),
        };
    }

    public static string FullMarkdown(AppServices svc, TerminalEmulator emulator)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(emulator);
        return BugReportBuilder.RenderStateOnly(RecentCapture(svc, emulator));
    }
}
