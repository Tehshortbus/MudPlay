using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game;

// Parses the in-game stat screen and writes every field onto PlayerStats.
// Drives RemoteCommandManager.LivesProvider so the @suicide hard-block has a
// real value to gate against.
//
// State machine mirrors TrainerMenuTracker's shape:
//   1. User sends stat outbound — ObserveOutbound arms ExpectingScreenWindow
//      seconds of "expecting stat output" time.
//   2. Within that window, every emitted line is scanned for label / value
//      pairs (Name:, Lives/CP:, Strength:, etc.) — each match commits the
//      value directly to PlayerStats. Lines unrelated to the stat screen pass
//      through unchanged.
//   3. Once the window expires, scanning stops. The outbound gate prevents
//      chat-noise (e.g., a gossip line with "Strength: 60") from updating our
//      state outside the user-initiated stat poll.
//
// Once any field has been parsed at least once, HasParsed flips true
// permanently for the session. RemoteCommandManager.LivesProvider uses that
// flag to decide whether to return PlayerStats.Lives or null (which the
// suicide hard-block treats as "unknown → blocked").
public sealed partial class StatParser : IDisposable
{
    private LineExtractor? _lines;
    private readonly LogService? _log;
    private bool _disposed;

    public PlayerStats Stats { get; }

    // Window after observing outbound stat during which incoming lines are
    // scanned.
    public TimeSpan ExpectingScreenWindow { get; set; } = TimeSpan.FromSeconds(5);

    // Window after observing outbound `health` during which the single compact
    // health-command line is accepted. Independent of the stat-screen gate — the
    // `health` output's `Health:` label (the HP pool) collides with the stat
    // sheet's `Health:` attribute (a core stat like Strength — a bare number; the
    // stat sheet labels the HP pool `Hits:`), so it is parsed on its own gate,
    // never through the generic field scan.
    public TimeSpan ExpectingHealthWindow { get; set; } = TimeSpan.FromSeconds(5);

    private readonly Func<DateTime> _now;

    private DateTime? _windowOpenedAt;
    private DateTime? _healthWindowOpenedAt;

    // A `stat` / `exp` went out and its screen hasn't been captured yet, so anything
    // wanting a fresh stat screen can wait for this one instead of sending another.
    public bool ScreenExpected =>
        _windowOpenedAt is { } opened && _now() - opened <= ExpectingScreenWindow;
    // Per-arm flag — flipped true the first time a field commits within the
    // current scan window, reset when the gate closes. Lets us close the gate
    // as soon as the in-game prompt returns AFTER capture, instead of waiting
    // for the full window timeout.
    private bool _capturedThisArm;

    // Per-arm counter of distinct field commits — surfaced in the gate-close
    // summary log line.
    private int _fieldsCapturedThisArm;

    // Whether this capture actually read the Hits / power-pool (Mana or Kai) lines. Only then are the
    // maxima fresh: an `exp` screen carries neither, and its snapshot still holds the
    // last `stat`'s maxima — read under whatever gear was worn then (report
    // paradigm-20260928-223148: an `exp` re-snapped Default gear's max mana from 398
    // to a Pre-rest-set 423, and the next Pre-rest swap stacked +25 on top of it).
    private bool _hitsReadThisArm;
    private bool _poolReadThisArm;
    public bool LastCaptureReadHits { get; private set; }
    // The last screen carried a Stealth figure: the moment to note what the pack was
    // doing to it (CarriedStealthPenalty).
    public bool LastCaptureReadStealth { get; private set; }
    private bool _stealthReadThisArm;
    public bool LastCaptureReadPool { get; private set; }
    // The last screen carried the Lives figure (a `stat`; an `exp` line doesn't), so
    // the count is the game's own as of now and not one remembered from before.
    public bool LastCaptureReadLives { get; private set; }
    private bool _livesReadThisArm;

    // The screen being read: which of the six stats it has shown, which of those it
    // marked modified, which marks could be read at all, and the effect lines under
    // them. Published whole at the close, so nothing downstream ever pairs this
    // screen's values with the last screen's marks.
    private bool _headerReadThisArm;
    private StatSet _statsReadThisArm;
    private StatSet _modifiedThisArm;
    private StatSet _marksKnownThisArm;
    private readonly List<StatusEffectLine> _effectsThisArm = new();

    // Lines Stock prints under the stats that are not effects.
    private static readonly string[] ScreenFooterLines =
    {
        "You are Poisoned!",
        "You are in the front rank of your group.",
        "You are in the back rank of your group.",
        "You have the following spell cast upon you:",
    };

    // Idle self-close. The reactive gate-close in OnLine only fires when a *further*
    // line arrives after capture (a terminating prompt, or any line past the window
    // expiry). But a stat screen the player is parked in front of — e.g. sitting at a
    // trainer right after `train` — emits no trailing prompt and no further line, so the
    // gate hangs open and ScreenParsed never fires until the next unrelated inbound line
    // (observed: 34 s later, long past the auto-train CP-apply's 8 s deadline). A short
    // settle timer, re-armed off the last captured field, closes the gate on its own so
    // ScreenParsed lands promptly. Reactive closes still win when a line does arrive.
    private static readonly TimeSpan SettleAfterCapture = TimeSpan.FromMilliseconds(750);
    private IDisposable? _settleTimer;
    // Arms one tick after the delay; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;

    // True once any stat-screen line has been parsed this session.
    public bool HasParsed { get; private set; }

    // A stat screen is printing: its scan window is open and has captured a field, so
    // the lines until its closing prompt (the active-effect list among them) belong
    // to it.
    public bool InStatScreen => _windowOpenedAt is not null && _capturedThisArm;

    // Fires once per scan window AFTER one or more fields commit and the gate
    // closes (whatever the close reason — prompt, expiry, etc.). Carries a
    // fresh LastKnownStats snapshot built from the current Stats values.
    // AppServices subscribes and writes the snapshot onto the loaded character
    // profile's LastKnownStats so the next session starts hydrated instead of
    // zeroed. Never fires for no-capture windows (no point in churning the
    // profile when nothing changed).
    public event Action<Models.Profile.LastKnownStats>? ScreenParsed;

    // Fires after a "You gain N experience." line accrues onto Stats.Exp,
    // carrying the new running total. Distinct from ScreenParsed (an
    // authoritative stat/exp re-anchor): this is the ONLY Exp change that
    // signals a live combat gain — the only kind that can newly cross a
    // Level-Projection threshold mid-play. LevelUpAnnouncer listens here so it
    // announces a reachable level only on a genuine gain, never on a login
    // hydrate or a catch-up poll.
    public event Action<long>? ExperienceGained;

    // Fires when the compact `health`-command output re-anchors the HP + power
    // pool. Carries (maxHits, poolMax) — poolMax is the mana OR kai ceiling, 0
    // for a no-pool class. AppServices routes these to PromptParser.ApplyStatScreenMax
    // (the sole max-field writer) so PlayerState.MaxHp/MaxMa snap to the real
    // ceilings with far less scroll than the full stat screen.
    public event Action<int, int>? HealthReanchored;

    public StatParser(PlayerStats stats, LogService? log = null, Func<DateTime>? now = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _schedule = schedule ?? DispatcherTicks.Once;
        ArgumentNullException.ThrowIfNull(stats);
        _log   = log;
        Stats  = stats;
    }

    // Restore Stats from a persisted LastKnownStats snapshot. Called by
    // AppServices on ProfileService.ProfileLoaded so the live state surfaces
    // start with the user's last-observed values instead of zeros. StatParser
    // owns these fields (per the single-writer IL test), so hydration MUST
    // route through this method rather than the caller writing to Stats
    // directly. A null snapshot resets every field back to its default — used
    // when the user switches to a fresh profile that has never run stat.
    public void Hydrate(Models.Profile.LastKnownStats? snapshot)
    {
        if (snapshot is null)
        {
            // Reset everything back to defaults — same as a freshly-
            // constructed PlayerStats. Avoids leaking the previous
            // profile's values into a fresh profile load.
            Stats.Name = string.Empty;
            Stats.Race = string.Empty;
            Stats.Class = string.Empty;
            Stats.Level = 0;
            Stats.Exp = 0;
            Stats.Lives = 0;
            Stats.Cp = 0;
            Stats.ExpToNext = 0;
            Stats.LevelExpSpan = 0;
            Stats.LevelPercent = 0;
            Stats.Hits = 0;
            Stats.MaxHits = 0;
            Stats.Mana = 0;
            Stats.MaxMana = 0;
            Stats.Kai = 0;
            Stats.MaxKai = 0;
            Stats.ArmourClass = 0;
            Stats.MaxArmourClass = 0;
            Stats.Strength = 0;
            Stats.Intellect = 0;
            Stats.Willpower = 0;
            Stats.Agility = 0;
            Stats.Health = 0;
            Stats.Charm = 0;
            Stats.Perception = 0;
            Stats.Stealth = 0;
            Stats.Thievery = 0;
            Stats.Traps = 0;
            Stats.Picklocks = 0;
            Stats.Tracking = 0;
            Stats.MartialArts = 0;
            Stats.MagicRes = 0;
            Stats.Spellcasting = 0;
            ForgetScreenMarks();
            HasParsed = false;
            return;
        }

        Stats.Name = snapshot.Name;
        Stats.Race = snapshot.Race;
        Stats.Class = snapshot.Class;
        Stats.Level = snapshot.Level;
        Stats.Exp = snapshot.Exp;
        Stats.Lives = snapshot.Lives;
        Stats.Cp = snapshot.Cp;
        Stats.ExpToNext = snapshot.ExpToNext;
        Stats.LevelExpSpan = snapshot.LevelExpSpan;
        Stats.LevelPercent = snapshot.LevelPercent;
        Stats.Hits = snapshot.Hits;
        Stats.MaxHits = snapshot.MaxHits;
        Stats.Mana = snapshot.Mana;
        Stats.MaxMana = snapshot.MaxMana;
        Stats.Kai = snapshot.Kai;
        Stats.MaxKai = snapshot.MaxKai;
        Stats.ArmourClass = snapshot.ArmourClass;
        Stats.MaxArmourClass = snapshot.MaxArmourClass;
        Stats.Strength = snapshot.Strength;
        Stats.Intellect = snapshot.Intellect;
        Stats.Willpower = snapshot.Willpower;
        Stats.Agility = snapshot.Agility;
        Stats.Health = snapshot.Health;
        Stats.Charm = snapshot.Charm;
        Stats.Perception = snapshot.Perception;
        Stats.Stealth = snapshot.Stealth;
        Stats.Thievery = snapshot.Thievery;
        Stats.Traps = snapshot.Traps;
        Stats.Picklocks = snapshot.Picklocks;
        Stats.Tracking = snapshot.Tracking;
        Stats.MartialArts = snapshot.MartialArts;
        Stats.MagicRes = snapshot.MagicRes;
        Stats.Spellcasting = snapshot.Spellcasting;
        // The marks and effect list belong to the same screen as the numbers, so
        // they come back with them. A snapshot saved without them says nothing
        // about whether its numbers were read with a buff up.
        if (snapshot.ModifiedStats is { } marked)
        {
            Stats.ModifiedStats = (StatSet)marked & StatSet.All;
            Stats.ActiveEffects = snapshot.ActiveEffects is { Count: > 0 } saved
                ? saved.Select(e => new StatusEffectLine(e.Text, e.Timed)).ToArray()
                : Array.Empty<StatusEffectLine>();
            Stats.ModifiedMarksRead = true;
        }
        else
            ForgetScreenMarks();
        // Flip the gate true so consumers (e.g. RemoteCommandManager's
        // LivesProvider) trust the hydrated values immediately — the
        // next live stat will reconfirm them.
        HasParsed = true;
    }

    private void ForgetScreenMarks()
    {
        Stats.ModifiedMarksRead = false;
        Stats.ModifiedStats = StatSet.None;
        Stats.ActiveEffects = Array.Empty<StatusEffectLine>();
    }

    private void ResetArmMarks()
    {
        _headerReadThisArm = false;
        _statsReadThisArm = _modifiedThisArm = _marksKnownThisArm = StatSet.None;
        _effectsThisArm.Clear();
    }

    // Build a snapshot of the current Stats values suitable for persisting to
    // the profile's LastKnownStats. Allocated each call (cheap — flat DTO) so
    // the recipient can store the reference without worrying about subsequent
    // mutations.
    public Models.Profile.LastKnownStats Snapshot() => new()
    {
        Name = Stats.Name,
        Race = Stats.Race,
        Class = Stats.Class,
        Level = Stats.Level,
        Exp = Stats.Exp,
        Lives = Stats.Lives,
        Cp = Stats.Cp,
        ExpToNext = Stats.ExpToNext,
        LevelExpSpan = Stats.LevelExpSpan,
        LevelPercent = Stats.LevelPercent,
        Hits = Stats.Hits,
        MaxHits = Stats.MaxHits,
        Mana = Stats.Mana,
        MaxMana = Stats.MaxMana,
        Kai = Stats.Kai,
        MaxKai = Stats.MaxKai,
        ArmourClass = Stats.ArmourClass,
        MaxArmourClass = Stats.MaxArmourClass,
        Strength = Stats.Strength,
        Intellect = Stats.Intellect,
        Willpower = Stats.Willpower,
        Agility = Stats.Agility,
        Health = Stats.Health,
        Charm = Stats.Charm,
        Perception = Stats.Perception,
        Stealth = Stats.Stealth,
        Thievery = Stats.Thievery,
        Traps = Stats.Traps,
        Picklocks = Stats.Picklocks,
        Tracking = Stats.Tracking,
        MartialArts = Stats.MartialArts,
        MagicRes = Stats.MagicRes,
        Spellcasting = Stats.Spellcasting,
        ModifiedStats = Stats.ModifiedMarksRead ? (int)Stats.ModifiedStats : null,
        ActiveEffects = Stats.ModifiedMarksRead && Stats.ActiveEffects.Count > 0
            ? Stats.ActiveEffects.Select(e => new Models.Profile.SavedEffectLine { Text = e.Text, Timed = e.Timed }).ToList()
            : null,
    };

    // True when text is a line of the stat / exp / health readout — the header
    // (Name: … Lives/CP:), any stat-attribute row ("Hits: 22/22", "Picklocks: 0",
    // "Race: Dark-Elf  Exp: …"), the single-line `exp` output, or the compact
    // `health` output. The unrecognized-line capture asks this because the stat
    // screen is read here as a gated block rather than through a router pattern, so
    // MessageRouter.AnyPatternMatches can't vouch for these rows and a `stat` poll
    // staged the whole sheet for review. Chat-shaped lines are rejected up front
    // (the same guard ScanLine uses) so a gossip embedding a stat label can't be
    // mistaken for the sheet. Recognizer only — the value-capturing regexes still
    // do the actual parsing.
    public static bool IsStatScreenLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (ChatLineRx().IsMatch(text)) return false;
        return StatHeaderRx().IsMatch(text)
            || ExpLineRx().IsMatch(text)
            || HealthCommandRx().IsMatch(text)
            || StatRowRx().IsMatch(text);
    }

    // Bind the per-session LineExtractor. Same shape as
    // PartyManager.AttachLineExtractor — the extractor is owned by the
    // main-window VM (one per terminal session) while this parser is
    // app-level. Calling again with a new extractor unhooks the previous one
    // first.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelSettleClose();
        if (_lines is not null) _lines.LineEmitted -= OnLine;
    }

    // Called by the wire-send path so we can spot the user's outbound stat
    // command. Arming gate is the same shape TrainerMenuTracker uses — without
    // an outbound stat within the window, every OnLine call is a no-op
    // (protects against chat lines containing "Strength:" or similar from
    // updating our stats).
    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 16) return;
        string raw = Encoding.Latin1.GetString(bytes);
        string cmd = raw.TrimEnd('\r', '\n', '\0').Trim().ToLowerInvariant();
        // `stat` and its abbreviations (`st` / `sta`) all open the full
        // stat-screen output — MajorMUD accepts every prefix of the command, just
        // like `exp` below, and the screen is identical however you spell it. Arm
        // on any prefix of "stat" from 2 chars up so the Player Workshop updates
        // whether the user typed `st` or `stat`. (Arming is harmless for a stray
        // `st`: with no stat lines the window just closes empty.)
        // any prefix of "experience" from 3 chars up — opens scan for
        // the single-line exp output. MajorMUD accepts every prefix
        // from `exp` through `experience` (3..10 chars) as the same
        // command; 2-char `ex` falls through to the `say "ex"` no-op
        // which the gate correctly ignores.
        bool isStat = cmd.Length is >= 2 and <= 4
                      && "stat".StartsWith(cmd, StringComparison.Ordinal);
        bool isExp  = cmd.Length >= 3 && cmd.Length <= 10
                      && "experience".StartsWith(cmd, StringComparison.Ordinal);
        // `health` (and its unambiguous prefixes `hea`..`health`) opens the
        // compact one-line HP/pool readout. It rides its own single-shot gate,
        // separate from the stat-screen scan. Arming is harmless if the command
        // turns out to be something else — the health line only commits on the
        // very specific `Health: N/M [P%] …` shape, so a stray arm just expires.
        bool isHealth = cmd.Length is >= 3 and <= 6
                      && "health".StartsWith(cmd, StringComparison.Ordinal);
        if (isHealth)
        {
            _healthWindowOpenedAt = _now();
            _log?.Log(LogSeverity.Info, "StatParser",
                $"Observed outbound `{cmd}` — armed {ExpectingHealthWindow.TotalSeconds:0}s health re-anchor window.");
        }
        if (!isStat && !isExp) return;
        _windowOpenedAt = _now();
        _capturedThisArm = false;
        _fieldsCapturedThisArm = 0;
        _hitsReadThisArm = _poolReadThisArm = _stealthReadThisArm = _livesReadThisArm = false;
        ResetArmMarks();
        CancelSettleClose();   // a prior window's pending settle-close must not close this one
        _log?.Log(LogSeverity.Info, "StatParser",
            $"Observed outbound `{cmd}` — armed {ExpectingScreenWindow.TotalSeconds:0}s scan window.");
    }

    // Open the scan window from the stat screen's own opening line when the
    // outbound `stat` gate never fired. The gate misses the command whenever
    // it doesn't reach ObserveOutbound as one whole token — most notably when
    // it's typed a byte at a time in a full-screen form's character-mode (e.g.
    // `train stats` right before `stat`), but also macros/paste/aliases. The
    // header (`Name: … Lives/CP: N/M`) is unfakeable by chat, so seeing it is
    // proof the real screen is arriving; we arm exactly as ObserveOutbound
    // would, then the caller falls through and scans this same header line.
    // No-op when a window is already open (the outbound path won the race), so
    // an in-flight capture is never reset mid-screen.
    private void MaybeSelfArmOnHeader(string text)
    {
        if (_windowOpenedAt is not null) return;
        if (string.IsNullOrEmpty(text) || !StatHeaderRx().IsMatch(text)) return;
        _windowOpenedAt = _now();
        _capturedThisArm = false;
        _fieldsCapturedThisArm = 0;
        _hitsReadThisArm = _poolReadThisArm = _stealthReadThisArm = _livesReadThisArm = false;
        ResetArmMarks();
        CancelSettleClose();
        _log?.Log(LogSeverity.Info, "StatParser",
            "Stat-screen header seen — self-armed scan (outbound `stat` gate missed the "
            + "command, e.g. typed in a full-screen form).");
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        // Lives-remaining (miracle save) — always-on, independent of
        // the stat-screen scan window.
        OnLivesRemainingLine(line.Text);
        // Experience-gain — always-on live accrual onto the Exp total.
        OnExperienceGainLine(line.Text);
        // Level-up — always-on live Level bump when a train-success line lands.
        OnTrainSuccessLine(line.Text);
        // CP awarded by that train — always-on live accrual onto Cp.
        OnCpGainLine(line.Text);
        // Compact `health`-command re-anchor — its own single-shot gate, checked
        // before the stat-screen scan (and before the stat gate's early-return
        // below, so a `health` poll re-anchors even with no stat window open).
        TryHealthWindow(line.Text);

        // Self-arm on the stat screen's own header if the outbound gate missed
        // the command — see MaybeSelfArmOnHeader. Runs before the null-gate
        // return so the header line itself is then scanned in the block below.
        MaybeSelfArmOnHeader(line.Text);

        if (_windowOpenedAt is null) return;

        // Close the gate as soon as the in-game prompt fires AFTER we
        // captured at least one field this arm. The stat screen
        // terminates with the next `[HP=...]:` prompt, which arrives
        // milliseconds after the burst — long before any human
        // keystroke could land. Without this, the gate stays open
        // for the full 5 s and a fast typist could land a command
        // whose echo contains "Strength: N" and corrupt the field.
        if (line.IsPromptLine && _capturedThisArm)
        {
            CloseGate("prompt after capture");
            return;
        }

        if (_now() - _windowOpenedAt.Value > ExpectingScreenWindow)
        {
            CloseGate("window expired");
            return;
        }

        int capturedBefore = _fieldsCapturedThisArm;
        ScanLine(line.Text, line.Attributes);
        if (_fieldsCapturedThisArm > 0) _capturedThisArm = true;
        // A field committed on this line — (re)arm the idle settle-close from the
        // latest capture, so a stat screen with no trailing prompt still closes.
        if (_fieldsCapturedThisArm > capturedBefore) RestartSettleClose();
    }

    // (Re)start the idle settle-close from the most recent captured field.
    private void RestartSettleClose()
    {
        if (_disposed) return;
        _settleTimer?.Dispose();
        _settleTimer = _schedule(SettleAfterCapture, OnSettleElapsed);
    }

    private void OnSettleElapsed()
    {
        _settleTimer = null;
        if (_windowOpenedAt is not null)
            CloseGate("settled — stat screen idle, no trailing prompt");
    }

    private void CancelSettleClose()
    {
        _settleTimer?.Dispose();
        _settleTimer = null;
    }

    // Single-line scan — apply every field regex and update the corresponding
    // PlayerStats property on a match. Multiple labels can appear on one
    // stat-screen row (e.g., "Race: Dark-Elf       Exp: 0          Perception:
    // 50"), so we run every pattern against every line — at most one field per
    // pattern is updated per call.
    private void ScanLine(string text, CellAttributes[]? attributes)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        int capturedBefore = _fieldsCapturedThisArm;

        // Chat-line shape guard — any line that opens with
        // `<player> <verb>:` is chat or a self-echo of chat the user
        // typed. Skipped wholesale so a message like
        // "Foo gossips: my Strength: 60 sucks" can't write to the
        // Strength field even if the gate happens to be open. Pairs
        // with the close-on-prompt-after-capture path: between the
        // two, the only lines that ever reach the field regexes
        // during a scan window are non-chat, non-prompt server lines
        // — i.e. the actual stat-screen output.
        if (ChatLineRx().IsMatch(text)) return;

        // String-valued fields are caught first — they have the
        // non-greedy "up to two spaces" cutoff so multi-word values
        // like "Dark-Elf" / "MudPlay WuzHere" capture fully without
        // bleeding into the next label.
        TryString(text, NameRx(),  "Name",  v => Stats.Name  = v);
        TryString(text, RaceRx(),  "Race",  v => Stats.Race  = v);
        TryString(text, ClassRx(), "Class", v => Stats.Class = v);

        // Paired N/M fields.
        TryPair(text, LivesCpRx(),     "Lives/CP",     (a, b) => { Stats.Lives = a; Stats.Cp = b; _livesReadThisArm = true; BeginScreenMarks(); });
        TryPair(text, HitsRx(),        "Hits",         (a, b) => { Stats.Hits = a; Stats.MaxHits = b; _hitsReadThisArm = true; });
        TryPair(text, KaiRx(),         "Kai",          (a, b) => { Stats.Kai  = a; Stats.MaxKai  = b; _poolReadThisArm = true; });
        TryPair(text, ManaRx(),        "Mana",         (a, b) => { Stats.Mana = a; Stats.MaxMana = b; _poolReadThisArm = true; });
        TryPair(text, ArmourClassRx(), "Armour Class", (a, b) => { Stats.ArmourClass = a; Stats.MaxArmourClass = b; });

        // Plain N fields. The `\*?` in every numeric regex tolerates the asterisk
        // Stock puts in front of an altered value — e.g. `Strength: *80` — and the
        // value captured is the altered one. For the six trainable stats the mark
        // itself is kept too (TryStat).
        TryInt(text, LevelRx(),        "Level",        v => Stats.Level        = v);
        TryLong(text, ExpRx(),         "Exp",          v => Stats.Exp          = v);
        TryInt(text, PerceptionRx(),   "Perception",   v => Stats.Perception   = v);
        TryInt(text, StealthRx(),      "Stealth",      v => { Stats.Stealth = v; _stealthReadThisArm = true; });
        TryInt(text, ThieveryRx(),     "Thievery",     v => Stats.Thievery     = v);
        TryInt(text, TrapsRx(),        "Traps",        v => Stats.Traps        = v);
        TryInt(text, PicklocksRx(),    "Picklocks",    v => Stats.Picklocks    = v);
        TryInt(text, TrackingRx(),     "Tracking",     v => Stats.Tracking     = v);
        TryStat(text, attributes, StrengthRx(),  "Strength",  StatSet.Strength,  v => Stats.Strength  = v);
        TryStat(text, attributes, IntellectRx(), "Intellect", StatSet.Intellect, v => Stats.Intellect = v);
        TryStat(text, attributes, WillpowerRx(), "Willpower", StatSet.Willpower, v => Stats.Willpower = v);
        TryStat(text, attributes, AgilityRx(),   "Agility",   StatSet.Agility,   v => Stats.Agility   = v);
        TryStat(text, attributes, HealthRx(),    "Health",    StatSet.Health,    v => Stats.Health    = v);
        TryStat(text, attributes, CharmRx(),     "Charm",     StatSet.Charm,     v => Stats.Charm     = v);
        TryInt(text, MartialArtsRx(),  "Martial Arts", v => Stats.MartialArts  = v);
        TryInt(text, MagicResRx(),     "MagicRes",     v => Stats.MagicRes     = v);
        TryInt(text, SpellcastingRx(), "Spellcasting", v => Stats.Spellcasting = v);

        // The exp-command output is a single line packing five
        // numbers — match-or-skip with one regex rather than five.
        // Anchored at the line start so a chat line like
        // "Foo gossips: my Exp: 0 is lame" can't fake the prefix.
        TryExpLine(text);

        if (_fieldsCapturedThisArm == capturedBefore) NoteEffectLine(text);
    }

    // The header opens a new screen. Until it has been read whole, the marks on
    // record belong to the last screen and say nothing about the values now landing.
    private void BeginScreenMarks()
    {
        ResetArmMarks();
        _headerReadThisArm = true;
        Stats.ModifiedMarksRead = false;
    }

    // Everything under the last stat row, up to the closing prompt, is the list of
    // active effects.
    private void NoteEffectLine(string text)
    {
        if (!_headerReadThisArm || _statsReadThisArm != StatSet.All) return;
        string line = text.Trim();
        if (Array.IndexOf(ScreenFooterLines, line) >= 0) return;
        _effectsThisArm.Add(StatusEffectLine.Parse(line));
    }

    // One of the six trainable stats, with the mark the screen puts on a value that
    // isn't the trained one (GAME_MECHANICS "How `stat` marks a modified stat").
    private void TryStat(string text, CellAttributes[]? attributes, Regex rx, string field,
                         StatSet stat, Action<int> set)
    {
        Match m = rx.Match(text);
        if (!m.Success) return;
        if (!int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int v)) return;

        bool? modified = ReadModifiedMark(m, attributes);
        _statsReadThisArm |= stat;
        if (modified is not null) _marksKnownThisArm |= stat;
        if (modified == true) _modifiedThisArm |= stat;

        set(v);
        HasParsed = true;
        _fieldsCapturedThisArm++;
        _log?.Log(LogSeverity.Debug, "StatParser",
            $"{field} = {v}{(modified == true ? " (marked modified)" : modified is null ? " (mark unreadable)" : string.Empty)}");
    }

    // Stock's asterisk is text and settles it alone. Otherwise the value's colour
    // does: red for a modified stat, cyan for a trained one. Read as the palette
    // index, not the drawn colour, so a custom palette can't move it. Null when the
    // line carries neither.
    private static bool? ReadModifiedMark(Match m, CellAttributes[]? attributes)
    {
        if (m.Value.Contains('*')) return true;
        int at = m.Groups[1].Index;
        if (attributes is null || at >= attributes.Length) return null;
        TerminalColor fg = attributes[at].Foreground;
        if (fg.Kind != ColorKind.Indexed) return null;
        return fg.Value switch
        {
            1 or 9  => true,
            6 or 14 => false,
            _       => null,
        };
    }

    // Parse the one-line exp-command output:
    // "Exp: N Level: M Exp needed for next level: P (Q) [R%]". All five fields
    // commit atomically on a successful match.
    private void TryExpLine(string text)
    {
        Match m = ExpLineRx().Match(text);
        if (!m.Success) return;
        System.Globalization.NumberStyles ns = System.Globalization.NumberStyles.Integer;
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        // Exp / needed / span are long — Paradigm's values pass int32. Level and
        // percent stay int.
        if (!long.TryParse(m.Groups[1].Value, ns, inv, out long exp))      return;
        if (!int.TryParse(m.Groups[2].Value, ns, inv, out int level))      return;
        if (!long.TryParse(m.Groups[3].Value, ns, inv, out long toNext))   return;
        if (!long.TryParse(m.Groups[4].Value, ns, inv, out long threshold))return;
        if (!int.TryParse(m.Groups[5].Value, ns, inv, out int percent))    return;
        Stats.Exp           = exp;
        Stats.Level         = level;
        Stats.ExpToNext     = toNext;
        Stats.LevelExpSpan  = threshold;
        Stats.LevelPercent  = percent;
        HasParsed = true;
        // Single-line capture = 5 fields per match, count them all so
        // the gate-close summary reads truthfully.
        _fieldsCapturedThisArm += 5;
        _log?.Log(LogSeverity.Debug, "StatParser",
            $"Exp = {exp}  Level = {level}  ExpToNext = {toNext}  LevelExpSpan = {threshold}  LevelPercent = {percent}");
    }

    // Always-on handler for the post-miracle-save line
    // "You have N lives left.". Updates Stats.Lives immediately so the
    // @suicide hard-block reflects the new count without waiting for the next
    // user-issued stat. Bypasses the outbound-stat gate because this line is
    // server-emitted as a game event, not part of a stat block.
    private void OnLivesRemainingLine(string text)
    {
        Match m = LivesRemainingRx().Match(text);
        if (!m.Success) return;
        if (!int.TryParse(m.Groups[1].Value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int lives)) return;
        Stats.Lives = lives;
        HasParsed = true;
        _log?.Log(LogSeverity.Info, "StatParser",
            $"Updated Lives → {lives} (post-suicide / miracle-save line).");
    }

    // Always-on handler for the combat reward line "You gain N experience.".
    // Adds the gain onto the running Stats.Exp total so consumers (the Level
    // Projection tab, the auto-trainer's can-level check) stay current between
    // manual exp / stat polls — the user no longer has to re-poll to refresh
    // their banked experience. Bypasses the stat-screen scan gate: it's a
    // server-emitted game event, like the miracle-save line. StatParser owns
    // Stats.Exp, so this accrual lives here rather than in a consumer.
    private void OnExperienceGainLine(string text)
    {
        Match m = ExpGainRx().Match(text);
        if (!m.Success) return;
        if (!long.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long gained)) return;
        // Stats.Exp is a long, so the running total holds Paradigm's hundreds-of-
        // billions without the int clamp that used to pin it at int.MaxValue.
        Stats.Exp += gained;
        // Keep the derived "to next level" fields current too, so @exp's Needed / ETA and
        // @level's to-next don't lag behind the live Exp total until the next exp poll. Only
        // when a real exp-screen baseline exists (LevelExpSpan > 0); the exp screen refills
        // exact values on the next poll and on a level-up (see OnTrainSuccessLine).
        if (Stats.LevelExpSpan > 0 && Stats.ExpToNext > 0)
        {
            Stats.ExpToNext = Math.Max(0, Stats.ExpToNext - gained);
            long into = Stats.LevelExpSpan - Stats.ExpToNext;
            Stats.LevelPercent = (int)Math.Clamp(into * 100 / Stats.LevelExpSpan, 0, 100);
        }
        HasParsed = true;
        _log?.Log(LogSeverity.Debug, "StatParser", $"Exp += {gained} → {Stats.Exp} (gain line).");
        ExperienceGained?.Invoke(Stats.Exp);
    }

    // Always-on handler for the server's train-success line, so Stats.Level tracks a
    // level-up the moment the character trains rather than waiting for the next stat /
    // exp poll (nothing else wrote Level live — the auto-trainer kept its own count).
    // Two wordings: the stock line carries the attained level ("...attain level N."),
    // the Paradigm line carries none ("...train to the next level!") so infer current + 1
    // when the current level is known. Mirrors KnownPatterns.TrainAttainLevel /
    // TrainAttainNextLevel (the router copies drive auto-train); kept here because
    // StatParser owns Stats.Level. The new level's exp span / to-next aren't known until
    // the next exp poll, so clear the derived fields — queries then read "unknown (type
    // exp)" rather than the old level's leftover to-next.
    private void OnTrainSuccessLine(string text)
    {
        int newLevel;
        Match stock = TrainAttainLevelRx().Match(text);
        if (stock.Success &&
            int.TryParse(stock.Groups[1].Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int attained))
            newLevel = attained;
        else if (Stats.Level > 0 && TrainNextLevelRx().IsMatch(text))
            newLevel = Stats.Level + 1;
        else
            return;

        Stats.Level        = newLevel;
        Stats.ExpToNext    = 0;
        Stats.LevelExpSpan = 0;
        Stats.LevelPercent = 0;
        HasParsed = true;
        _log?.Log(LogSeverity.Info, "StatParser", $"Level → {newLevel} (train-success line).");
    }

    // Always-on handler for the CP a train awards ("You gain 15 CPs", right after the
    // train-success line). Stats.Cp otherwise only moved on a `stat` poll, so a hand
    // `train` followed by `train stats` budgeted the CP plan against the PRE-train CP:
    // nothing looked affordable and Auto-train stats silently did nothing until the
    // user typed `stat` (reports paradigm-20260924-132158 / -132507). StatParser owns
    // Stats.Cp, so the accrual lives here; the next stat poll still re-anchors it.
    private void OnCpGainLine(string text)
    {
        Match m = CpGainRx().Match(text);
        if (!m.Success) return;
        if (!int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int gained) || gained <= 0) return;
        Stats.Cp += gained;
        HasParsed = true;
        _log?.Log(LogSeverity.Info, "StatParser", $"Cp += {gained} → {Stats.Cp} (train CP-gain line).");
    }

    // The compact `health` command re-anchors HP + power-pool ceilings with far
    // less scroll than the full stat screen. Its `Health:` label (the HP pool)
    // collides with the stat sheet's `Health:` attribute (a bare-number core stat;
    // the sheet labels the HP pool `Hits:`), so it rides its own single-shot gate
    // (armed on outbound `health`) and is parsed here — never through ScanLine's
    // generic HealthRx, which would misread the HP value as the Health attribute.
    private void TryHealthWindow(string text)
    {
        if (_healthWindowOpenedAt is not { } opened) return;
        if (_now() - opened > ExpectingHealthWindow) { _healthWindowOpenedAt = null; return; }
        if (string.IsNullOrWhiteSpace(text)) return;
        // A chat echo embedding the readout can never commit the re-anchor.
        if (ChatLineRx().IsMatch(text)) return;
        if (TryHealthCommandLine(text)) _healthWindowOpenedAt = null;   // committed → close the gate
    }

    // Parse the one-line `health` output — "Health: 91/91 [100%] Kai: 6/10 [60%]"
    // (stock) / "Health: 593/593 [100%] Mana: 619/619 [100%]" (paradigm). Commits
    // Hits/MaxHits and, when present, the mana OR kai pool, then fires
    // HealthReanchored(maxHits, poolMax) so PlayerState.MaxHp/MaxMa snap to the
    // authoritative ceilings. A no-pool class (warrior) omits the second field →
    // poolMax 0, which ApplyStatScreenMax ignores. Returns true on a commit.
    private bool TryHealthCommandLine(string text)
    {
        Match m = HealthCommandRx().Match(text);
        if (!m.Success) return false;
        System.Globalization.NumberStyles ns = System.Globalization.NumberStyles.Integer;
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(m.Groups[1].Value, ns, inv, out int hits)) return false;
        if (!int.TryParse(m.Groups[2].Value, ns, inv, out int maxHits)) return false;
        Stats.Hits = hits;
        Stats.MaxHits = maxHits;

        int poolMax = 0;
        string poolLabel = string.Empty;
        if (m.Groups[3].Success
            && int.TryParse(m.Groups[4].Value, ns, inv, out int pool)
            && int.TryParse(m.Groups[5].Value, ns, inv, out int poolMaxParsed))
        {
            poolMax = poolMaxParsed;
            poolLabel = m.Groups[3].Value;
            if (poolLabel.Equals("Mana", StringComparison.OrdinalIgnoreCase))
            {
                Stats.Mana = pool;
                Stats.MaxMana = poolMaxParsed;
            }
            else   // "Kai"
            {
                Stats.Kai = pool;
                Stats.MaxKai = poolMaxParsed;
            }
        }

        HasParsed = true;
        _log?.Log(LogSeverity.Info, "StatParser",
            $"Health re-anchor — Hits={hits}/{maxHits}"
            + (poolMax > 0 ? $"  {poolLabel}={m.Groups[4].Value}/{poolMax}" : string.Empty)
            + " → snapping max HP/mana to the authoritative ceilings.");
        HealthReanchored?.Invoke(maxHits, poolMax);
        return true;
    }

    private void TryString(string text, Regex rx, string field, Action<string> set)
    {
        Match m = rx.Match(text);
        if (!m.Success) return;
        string value = m.Groups[1].Value.Trim();
        set(value);
        HasParsed = true;
        _fieldsCapturedThisArm++;
        _log?.Log(LogSeverity.Debug, "StatParser", $"{field} = \"{value}\"");
    }

    private void TryInt(string text, Regex rx, string field, Action<int> set)
    {
        Match m = rx.Match(text);
        if (!m.Success) return;
        if (!int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int v)) return;
        set(v);
        HasParsed = true;
        _fieldsCapturedThisArm++;
        _log?.Log(LogSeverity.Debug, "StatParser", $"{field} = {v}");
    }

    // Long sibling of TryInt — for Exp, whose Paradigm value passes int32.
    private void TryLong(string text, Regex rx, string field, Action<long> set)
    {
        Match m = rx.Match(text);
        if (!m.Success) return;
        if (!long.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long v)) return;
        set(v);
        HasParsed = true;
        _fieldsCapturedThisArm++;
        _log?.Log(LogSeverity.Debug, "StatParser", $"{field} = {v}");
    }

    private void TryPair(string text, Regex rx, string field, Action<int, int> set)
    {
        Match m = rx.Match(text);
        if (!m.Success) return;
        System.Globalization.NumberStyles ns = System.Globalization.NumberStyles.Integer;
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(m.Groups[1].Value, ns, inv, out int a)) return;
        if (!int.TryParse(m.Groups[2].Value, ns, inv, out int b)) return;
        set(a, b);
        HasParsed = true;
        _fieldsCapturedThisArm++;
        _log?.Log(LogSeverity.Debug, "StatParser", $"{field} = {a}/{b}");
    }

    // Close the scan gate + emit an INF summary of what we captured this arm.
    // reason describes which terminator fired ("prompt after capture",
    // "window expired", etc.) so the user can correlate with what they did on
    // the wire.
    private void CloseGate(string reason)
    {
        if (_windowOpenedAt is null) return;
        bool capturedSomething = _fieldsCapturedThisArm > 0;
        if (capturedSomething)
        {
            // Quick-glance digest of the most-load-bearing fields so
            // the INF log doesn't require expanding DBG entries to
            // see what landed.
            _log?.Log(LogSeverity.Info, "StatParser",
                $"Stat capture closed ({reason}) — {_fieldsCapturedThisArm} field(s) updated. "
                + $"Name=\"{Stats.Name}\"  Level={Stats.Level}  Lives={Stats.Lives}/CP={Stats.Cp}  "
                + $"Hits={Stats.Hits}/{Stats.MaxHits}.");
        }
        else
        {
            _log?.Log(LogSeverity.Info, "StatParser",
                $"Stat capture closed ({reason}) — no fields matched this window.");
        }
        _windowOpenedAt = null;
        _capturedThisArm = false;
        _fieldsCapturedThisArm = 0;
        LastCaptureReadHits = _hitsReadThisArm;
        LastCaptureReadStealth = _stealthReadThisArm;
        LastCaptureReadPool = _poolReadThisArm;
        LastCaptureReadLives = _livesReadThisArm;
        _hitsReadThisArm = _poolReadThisArm = _stealthReadThisArm = _livesReadThisArm = false;
        PublishScreenMarks();
        CancelSettleClose();
        // Fire ScreenParsed only when something actually changed —
        // there's no value in churning the profile snapshot for an
        // empty window.
        if (capturedSomething) ScreenParsed?.Invoke(Snapshot());
    }

    // Publish what the closing screen said about its own six stats. Only a `stat`
    // screen does (an `exp` line has no header), and the marks count as read only
    // when all six stats were shown and every mark could be made out. The flag goes
    // up last, so a consumer reacting to it finds the marks and effects in place.
    private void PublishScreenMarks()
    {
        if (!_headerReadThisArm) return;
        bool whole = _statsReadThisArm == StatSet.All;
        Stats.ModifiedStats = whole ? _modifiedThisArm : StatSet.None;
        Stats.ActiveEffects = whole && _effectsThisArm.Count > 0
            ? _effectsThisArm.ToArray()
            : Array.Empty<StatusEffectLine>();
        Stats.ModifiedMarksRead = whole && _marksKnownThisArm == StatSet.All;
        if (Stats.ModifiedStats != StatSet.None)
            _log?.Log(LogSeverity.Debug, "StatParser",
                $"Stat screen marks {Stats.ModifiedStats} as modified; {Stats.ActiveEffects.Count} effect line(s) listed.");
        ResetArmMarks();
    }

    // ----- Regexes -------------------------------------------------------
    // Source-generated for hot-path efficiency. String labels use the
    // `(?=\s{2,}|$)` lookahead to stop the value at two-space gutters
    // between row columns (so "Name: MudPlay WuzHere    Lives/CP: ..." captures
    // just "MudPlay WuzHere"). Numeric labels capture digits only.

    [GeneratedRegex(@"\bName:\s+(\S[\w '\-]*?)(?=\s{2,}|$)",      RegexOptions.CultureInvariant)] private static partial Regex NameRx();
    [GeneratedRegex(@"\bRace:\s+(\S[\w '\-]*?)(?=\s{2,}|$)",      RegexOptions.CultureInvariant)] private static partial Regex RaceRx();
    // Class sits in a column whose next label can be only a SINGLE space
    // away ("Class: Missionary Level: 2") on some realm layouts — unlike
    // Name / Race, which always have a 2-space gutter. So the terminator
    // also stops the value before the next `Label:` column, otherwise the
    // 2-space-only lookahead silently drops Class on single-space layouts.
    [GeneratedRegex(@"\bClass:\s+(\S[\w '\-]*?)(?=\s+\w+:|\s{2,}|$)", RegexOptions.CultureInvariant)] private static partial Regex ClassRx();

    // Paired N/M fields — Hits / Kai / Mana / Armour Class allow `*`
    // between the colon and digits to capture altered values
    // ("Hits: *22/22"). Lives/CP is never altered in-game so doesn't
    // need the tolerance.
    [GeneratedRegex(@"\bLives/CP:\s+(\d+)/(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex LivesCpRx();
    [GeneratedRegex(@"\bHits:\s+\*?\s*(\d+)/(\d+)",                     RegexOptions.CultureInvariant)] private static partial Regex HitsRx();
    [GeneratedRegex(@"\bKai:\s+\*?\s*(\d+)/(\d+)",                      RegexOptions.CultureInvariant)] private static partial Regex KaiRx();
    [GeneratedRegex(@"\bMana:\s+\*?\s*(\d+)/(\d+)",                     RegexOptions.CultureInvariant)] private static partial Regex ManaRx();
    [GeneratedRegex(@"\bArmour Class:\s+\*?\s*(\d+)/(\d+)",             RegexOptions.CultureInvariant)] private static partial Regex ArmourClassRx();

    // The compact `health`-command output. Anchored at line start (with optional
    // leading whitespace) so it never matches the stat sheet's mid-row `Health:`
    // attribute field (a bare-number core stat), and so a chat line embedding it
    // can't fake the prefix. HP is
    // always paired N/M with a [P%]; the pool (Kai on stock, Mana on paradigm) is
    // optional so a no-mana class parses too. Groups: 1 curHp, 2 maxHp, 3 pool
    // label, 4 curPool, 5 maxPool.
    [GeneratedRegex(@"^\s*Health:\s+\*?\s*(\d+)/(\d+)\s+\[\s*\d+%\](?:\s+(Kai|Mana):\s+\*?\s*(\d+)/(\d+)\s+\[\s*\d+%\])?",
        RegexOptions.CultureInvariant)] private static partial Regex HealthCommandRx();

    // Plain N fields — `\*?` between the colon and the digits
    // tolerates the altered-stat marker. Stock prints three of the six stats with
    // no space after the colon (`Intellect:`, `Willpower:`, `Agility:`), so a
    // modified one reads `Agility:*50`: the six stat labels take `\s*` there.
    [GeneratedRegex(@"\bLevel:\s+(\d+)",                                RegexOptions.CultureInvariant)] private static partial Regex LevelRx();
    [GeneratedRegex(@"\bExp:\s+(\d+)",                                  RegexOptions.CultureInvariant)] private static partial Regex ExpRx();
    [GeneratedRegex(@"\bPerception:\s+\*?\s*(\d+)",                     RegexOptions.CultureInvariant)] private static partial Regex PerceptionRx();
    [GeneratedRegex(@"\bStealth:\s+\*?\s*(\d+)",                        RegexOptions.CultureInvariant)] private static partial Regex StealthRx();
    [GeneratedRegex(@"\bThievery:\s+\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex ThieveryRx();
    [GeneratedRegex(@"\bTraps:\s+\*?\s*(\d+)",                          RegexOptions.CultureInvariant)] private static partial Regex TrapsRx();
    [GeneratedRegex(@"\bPicklocks:\s+\*?\s*(\d+)",                      RegexOptions.CultureInvariant)] private static partial Regex PicklocksRx();
    [GeneratedRegex(@"\bTracking:\s+\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex TrackingRx();
    [GeneratedRegex(@"\bStrength:\s*\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex StrengthRx();
    [GeneratedRegex(@"\bIntellect:\s*\*?\s*(\d+)",                      RegexOptions.CultureInvariant)] private static partial Regex IntellectRx();
    [GeneratedRegex(@"\bWillpower:\s*\*?\s*(\d+)",                      RegexOptions.CultureInvariant)] private static partial Regex WillpowerRx();
    [GeneratedRegex(@"\bAgility:\s*\*?\s*(\d+)",                        RegexOptions.CultureInvariant)] private static partial Regex AgilityRx();
    [GeneratedRegex(@"\bHealth:\s*\*?\s*(\d+)",                         RegexOptions.CultureInvariant)] private static partial Regex HealthRx();
    [GeneratedRegex(@"\bCharm:\s*\*?\s*(\d+)",                          RegexOptions.CultureInvariant)] private static partial Regex CharmRx();
    [GeneratedRegex(@"\bMartial Arts:\s+\*?\s*(\d+)",                   RegexOptions.CultureInvariant)] private static partial Regex MartialArtsRx();
    [GeneratedRegex(@"\bMagicRes:\s+\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex MagicResRx();
    [GeneratedRegex(@"\bSpellcasting:\s+\*?\s*(\d+)",                   RegexOptions.CultureInvariant)] private static partial Regex SpellcastingRx();

    // The single-line `exp`-command output. Anchored at line start
    // so a chat line embedding "Exp: ..." can't match. Five
    // captures: current exp, current level, exp delta to next
    // level, absolute next-level threshold, percent progress.
    [GeneratedRegex(@"^Exp:\s+(\d+)\s+Level:\s+(\d+)\s+Exp needed for next level:\s+(\d+)\s+\((\d+)\)\s+\[(\d+)%\]",
        RegexOptions.CultureInvariant)] private static partial Regex ExpLineRx();

    // The stat screen's opening line — "Name: <name>  …  Lives/CP: N/M". Its
    // self-arm safety comes from the pairing: `Name:` anchored at line start AND
    // a `Lives/CP: N/M` column on the same line is unique to the stat sheet. A
    // chat line opens with "<who> <verb>:" (never `Name:` at column 0), and no
    // other server output pairs those two labels, so matching this is proof the
    // real screen is arriving — even when the outbound `stat` gate missed the
    // command (typed a byte at a time in a full-screen form's character-mode).
    [GeneratedRegex(@"^\s*Name:\s+\S.*?\bLives/CP:\s+\d+/\d+", RegexOptions.CultureInvariant)]
    private static partial Regex StatHeaderRx();

    // Any stat-sheet attribute row — used only by IsStatScreenLine, the
    // unrecognized-line capture's recognizer, NOT by the parse path. The three
    // string-valued labels (Name / Race / Class) are anchored at line start so a
    // mid-line word can't fake them; the numeric labels may appear anywhere but
    // must be followed by a colon + value (optionally `*`-prefixed for altered
    // stats). Every stat row carries at least one numeric label, so this catches
    // all of them without needing to re-list the value-capturing shapes above.
    [GeneratedRegex(
        @"^\s*(?:Name|Race|Class):\s+\S|\b(?:Lives/CP|Hits|Kai|Mana|Armour Class|Level|Exp|Perception|Stealth|Thievery|Traps|Picklocks|Tracking|Strength|Intellect|Willpower|Agility|Health|Charm|Martial Arts|MagicRes|Spellcasting):\s*\*?\s*\d",
        RegexOptions.CultureInvariant)]
    private static partial Regex StatRowRx();

    // Always-on lives-update line — fires outside the stat-screen
    // window. MajorMUD emits this in two phrasings:
    //   "You now have N lives remaining."   ← after a suicide
    //   "You have N lives left."            ← after a miracle save
    // Plus the singular forms (1 life). Both routes update Lives so
    // the @suicide hard-block sees the fresh count without waiting
    // for the next `stat` poll. Without "You now have ..." matching,
    // remote @suicides chained because the LivesProvider returned
    // the stale value from the last manual `stat`.
    [GeneratedRegex(@"^You (?:now have|have) (\d+) (?:lives?|life) (?:remaining|left)\.",
        RegexOptions.CultureInvariant)] private static partial Regex LivesRemainingRx();

    // Always-on experience-gain line — fires outside the stat-screen
    // window. Mirrors KnownPatterns.UserGainExperience (the MessageRouter
    // copy drives combat tracking); kept here so the Exp owner can accrue
    // the total without depending on the router pipeline.
    [GeneratedRegex(@"^You gain (\d+) experience\.",
        RegexOptions.CultureInvariant)] private static partial Regex ExpGainRx();

    // Train-success lines (mirror KnownPatterns.TrainAttainLevel / TrainAttainNextLevel).
    // Stock carries the attained level in group 1; the Paradigm line carries none, so the
    // handler infers current + 1. Mutually exclusive — the stock line says "attain level N",
    // never "train to the next level".
    [GeneratedRegex(@"^You hand over .+ and you receive training to attain level (\d+)",
        RegexOptions.CultureInvariant)] private static partial Regex TrainAttainLevelRx();

    [GeneratedRegex(@"^You hand over .+ to train to the next level",
        RegexOptions.CultureInvariant)] private static partial Regex TrainNextLevelRx();

    // The CP a train awards, as printed after the train-success line: "You gain 15 CPs".
    [GeneratedRegex(@"^You gain (\d+) CPs?\b",
        RegexOptions.CultureInvariant)] private static partial Regex CpGainRx();

    // Chat-line shape — matched at line start. Any of the standard
    // MajorMUD chat verbs after a single-word speaker means the
    // entire line is chat noise (including the user's own outgoing
    // gossip / say echoes). Used as a skip-guard in ScanLine so a
    // chat line embedding a stat label can't write to PlayerStats
    // even if it lands inside the scan window.
    [GeneratedRegex(@"^\w+\s+(?:gossips|telepaths|yells|says|auctions|gangpaths|broadcasts):",
        RegexOptions.CultureInvariant)] private static partial Regex ChatLineRx();
}
