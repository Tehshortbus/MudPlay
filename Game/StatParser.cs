using System.Text;
using System.Text.RegularExpressions;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game;

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

    // Test seam.
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    private DateTime? _windowOpenedAt;
    // Per-arm flag — flipped true the first time a field commits within the
    // current scan window, reset when the gate closes. Lets us close the gate
    // as soon as the in-game prompt returns AFTER capture, instead of waiting
    // for the full window timeout.
    private bool _capturedThisArm;

    // Per-arm counter of distinct field commits — surfaced in the gate-close
    // summary log line.
    private int _fieldsCapturedThisArm;

    // Idle self-close. The reactive gate-close in OnLine only fires when a *further*
    // line arrives after capture (a terminating prompt, or any line past the window
    // expiry). But a stat screen the player is parked in front of — e.g. sitting at a
    // trainer right after `train` — emits no trailing prompt and no further line, so the
    // gate hangs open and ScreenParsed never fires until the next unrelated inbound line
    // (observed: 34 s later, long past the auto-train CP-apply's 8 s deadline). A short
    // settle timer, re-armed off the last captured field, closes the gate on its own so
    // ScreenParsed lands promptly. Reactive closes still win when a line does arrive.
    private static readonly TimeSpan SettleAfterCapture = TimeSpan.FromMilliseconds(750);
    private SynchronizationContext? _home;   // captured at arm — marshals the settle-close back onto the line-pipeline thread
    private int _settleSession;              // bumped on arm / restart / close so a stale settle timer no-ops

    // True once any stat-screen line has been parsed this session.
    public bool HasParsed { get; private set; }

    // True once we've actually observed our Traps skill — either the full stat
    // screen's "Traps:" row committed, or a persisted snapshot hydrated it on
    // profile load. Distinct from HasParsed, which the single-line `exp` /
    // lives / xp-gain handlers also flip WITHOUT ever touching Traps — so
    // HasParsed can't tell "trap skill unknown" from "known zero." The walker's
    // trap-disarm gate needs that distinction: with disarm enabled it halts on a
    // trap when the skill is genuinely unknown, rather than waltzing through on a
    // defaulted zero it never actually parsed.
    public bool TrapsKnown { get; private set; }

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
    public event Action<int>? ExperienceGained;

    public StatParser(PlayerStats stats, LogService? log = null)
    {
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
            HasParsed = false;
            TrapsKnown = false;
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
        // Flip the gate true so consumers (e.g. RemoteCommandManager's
        // LivesProvider) trust the hydrated values immediately — the
        // next live stat will reconfirm them. A persisted snapshot is only
        // ever written after a real capture (CloseGate snapshots only when a
        // field committed), so a hydrated Traps value counts as known.
        HasParsed = true;
        TrapsKnown = true;
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
    };

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
        _settleSession++;   // strand any in-flight settle timer
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
        // `stat` — opens scan for the full stat-screen output.
        // any prefix of "experience" from 3 chars up — opens scan for
        // the single-line exp output. MajorMUD accepts every prefix
        // from `exp` through `experience` (3..10 chars) as the same
        // command; 2-char `ex` falls through to the `say "ex"` no-op
        // which the gate correctly ignores.
        bool isStat = cmd == "stat";
        bool isExp  = cmd.Length >= 3 && cmd.Length <= 10
                      && "experience".StartsWith(cmd, StringComparison.Ordinal);
        if (!isStat && !isExp) return;
        _windowOpenedAt = NowProvider();
        _capturedThisArm = false;
        _fieldsCapturedThisArm = 0;
        _home = SynchronizationContext.Current;   // (re)capture the pipeline thread for the settle-close
        _settleSession++;                          // invalidate any prior window's pending settle timer
        _log?.Log(LogSeverity.Info, "StatParser",
            $"Observed outbound `{cmd}` — armed {ExpectingScreenWindow.TotalSeconds:0}s scan window.");
    }

    // ----- Test seam -----------------------------------------------------
    // Test seam — arm the scanner without going through the wire-observation
    // path.
    internal void TestArm() => _windowOpenedAt = NowProvider();

    // Test seam — pump a line through the full handler path without a real
    // LineExtractor. Mirrors OnLine so tests exercise the always-on lives
    // handler + the gated scan together. isPromptLine defaults to false; set
    // true for tests that want to exercise the close-on-prompt-after-capture
    // path.
    internal void FeedTestLine(string text, bool isPromptLine = false)
    {
        OnLivesRemainingLine(text);
        OnExperienceGainLine(text);
        if (_windowOpenedAt is null) return;
        if (isPromptLine && _capturedThisArm)
        {
            CloseGate("prompt after capture");
            return;
        }
        if (NowProvider() - _windowOpenedAt.Value > ExpectingScreenWindow)
        {
            CloseGate("window expired");
            return;
        }
        bool hadParsed = HasParsed;
        ScanLine(text);
        if (HasParsed && !hadParsed) _capturedThisArm = true;
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        // Lives-remaining (miracle save) — always-on, independent of
        // the stat-screen scan window.
        OnLivesRemainingLine(line.Text);
        // Experience-gain — always-on live accrual onto the Exp total.
        OnExperienceGainLine(line.Text);

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

        if (NowProvider() - _windowOpenedAt.Value > ExpectingScreenWindow)
        {
            CloseGate("window expired");
            return;
        }

        bool hadParsed = HasParsed;
        int capturedBefore = _fieldsCapturedThisArm;
        ScanLine(line.Text);
        if (HasParsed && !hadParsed) _capturedThisArm = true;
        // A field committed on this line — (re)arm the idle settle-close from the
        // latest capture, so a stat screen with no trailing prompt still closes.
        if (_fieldsCapturedThisArm > capturedBefore) RestartSettleClose();
    }

    // (Re)start the idle settle-close from the most recent captured field. Only the
    // latest session survives — earlier timers find a bumped _settleSession and no-op.
    // No-ops when no SynchronizationContext was captured at arm (unit tests have none;
    // they drive the close synchronously via SettleNowForTests / the reactive paths).
    private void RestartSettleClose()
    {
        if (_home is null) return;
        int session = ++_settleSession;
        _ = SettleCloseAsync(session);
    }

    private async Task SettleCloseAsync(int session)
    {
        try { await Task.Delay(SettleAfterCapture).ConfigureAwait(false); }
        catch { return; }
        _home?.Post(_ =>
        {
            // Lost the race to a reactive close (prompt / expiry) or a newer window? no-op.
            if (session == _settleSession && _windowOpenedAt is not null)
                CloseGate("settled — stat screen idle, no trailing prompt");
        }, null);
    }

    // Test seam — fire the idle settle-close immediately (no real delay).
    internal void SettleNowForTests()
    {
        if (_windowOpenedAt is not null) CloseGate("settled (test)");
    }

    // Single-line scan — apply every field regex and update the corresponding
    // PlayerStats property on a match. Multiple labels can appear on one
    // stat-screen row (e.g., "Race: Dark-Elf       Exp: 0          Perception:
    // 50"), so we run every pattern against every line — at most one field per
    // pattern is updated per call.
    private void ScanLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

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
        // like "Dark-Elf" / "Fujin WuzHere" capture fully without
        // bleeding into the next label.
        TryString(text, NameRx(),  "Name",  v => Stats.Name  = v);
        TryString(text, RaceRx(),  "Race",  v => Stats.Race  = v);
        TryString(text, ClassRx(), "Class", v => Stats.Class = v);

        // Paired N/M fields.
        TryPair(text, LivesCpRx(),     "Lives/CP",     (a, b) => { Stats.Lives = a; Stats.Cp = b; });
        TryPair(text, HitsRx(),        "Hits",         (a, b) => { Stats.Hits = a; Stats.MaxHits = b; });
        TryPair(text, KaiRx(),         "Kai",          (a, b) => { Stats.Kai  = a; Stats.MaxKai  = b; });
        TryPair(text, ManaRx(),        "Mana",         (a, b) => { Stats.Mana = a; Stats.MaxMana = b; });
        TryPair(text, ArmourClassRx(), "Armour Class", (a, b) => { Stats.ArmourClass = a; Stats.MaxArmourClass = b; });

        // Plain N fields. The `\*?` in every numeric regex tolerates
        // the asterisk that altered (buffed / cursed) stats prefix
        // their value with — e.g. `Strength: *80`. We strip the
        // asterisk and capture the raw post-modifier value (the
        // altered-or-not distinction isn't surfaced anywhere yet;
        // can be added as parallel bool fields if a future consumer
        // wants it).
        TryInt(text, LevelRx(),        "Level",        v => Stats.Level        = v);
        TryInt(text, ExpRx(),          "Exp",          v => Stats.Exp          = v);
        TryInt(text, PerceptionRx(),   "Perception",   v => Stats.Perception   = v);
        TryInt(text, StealthRx(),      "Stealth",      v => Stats.Stealth      = v);
        TryInt(text, ThieveryRx(),     "Thievery",     v => Stats.Thievery     = v);
        TryInt(text, TrapsRx(),        "Traps",        v => { Stats.Traps = v; TrapsKnown = true; });
        TryInt(text, PicklocksRx(),    "Picklocks",    v => Stats.Picklocks    = v);
        TryInt(text, TrackingRx(),     "Tracking",     v => Stats.Tracking     = v);
        TryInt(text, StrengthRx(),     "Strength",     v => Stats.Strength     = v);
        TryInt(text, IntellectRx(),    "Intellect",    v => Stats.Intellect    = v);
        TryInt(text, WillpowerRx(),    "Willpower",    v => Stats.Willpower    = v);
        TryInt(text, AgilityRx(),      "Agility",      v => Stats.Agility      = v);
        TryInt(text, HealthRx(),       "Health",       v => Stats.Health       = v);
        TryInt(text, CharmRx(),        "Charm",        v => Stats.Charm        = v);
        TryInt(text, MartialArtsRx(),  "Martial Arts", v => Stats.MartialArts  = v);
        TryInt(text, MagicResRx(),     "MagicRes",     v => Stats.MagicRes     = v);
        TryInt(text, SpellcastingRx(), "Spellcasting", v => Stats.Spellcasting = v);

        // The exp-command output is a single line packing five
        // numbers — match-or-skip with one regex rather than five.
        // Anchored at the line start so a chat line like
        // "Foo gossips: my Exp: 0 is lame" can't fake the prefix.
        TryExpLine(text);
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
        if (!int.TryParse(m.Groups[1].Value, ns, inv, out int exp))      return;
        if (!int.TryParse(m.Groups[2].Value, ns, inv, out int level))    return;
        if (!int.TryParse(m.Groups[3].Value, ns, inv, out int toNext))   return;
        if (!int.TryParse(m.Groups[4].Value, ns, inv, out int threshold))return;
        if (!int.TryParse(m.Groups[5].Value, ns, inv, out int percent))  return;
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
        // Exp is an int field; clamp the running total so a huge gain can't
        // overflow it (high-level MajorMUD exp can pass int range).
        long total = (long)Stats.Exp + gained;
        Stats.Exp = total > int.MaxValue ? int.MaxValue : (int)total;
        HasParsed = true;
        _log?.Log(LogSeverity.Debug, "StatParser", $"Exp += {gained} → {Stats.Exp} (gain line).");
        ExperienceGained?.Invoke(Stats.Exp);
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
        _settleSession++;   // cancel any settle timer still pending for this window
        // Fire ScreenParsed only when something actually changed —
        // there's no value in churning the profile snapshot for an
        // empty window.
        if (capturedSomething) ScreenParsed?.Invoke(Snapshot());
    }

    // ----- Regexes -------------------------------------------------------
    // Source-generated for hot-path efficiency. String labels use the
    // `(?=\s{2,}|$)` lookahead to stop the value at two-space gutters
    // between row columns (so "Name: Fujin WuzHere    Lives/CP: ..." captures
    // just "Fujin WuzHere"). Numeric labels capture digits only.

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

    // Plain N fields — `\*?` between the colon and the digits
    // tolerates the altered-stat marker.
    [GeneratedRegex(@"\bLevel:\s+(\d+)",                                RegexOptions.CultureInvariant)] private static partial Regex LevelRx();
    [GeneratedRegex(@"\bExp:\s+(\d+)",                                  RegexOptions.CultureInvariant)] private static partial Regex ExpRx();
    [GeneratedRegex(@"\bPerception:\s+\*?\s*(\d+)",                     RegexOptions.CultureInvariant)] private static partial Regex PerceptionRx();
    [GeneratedRegex(@"\bStealth:\s+\*?\s*(\d+)",                        RegexOptions.CultureInvariant)] private static partial Regex StealthRx();
    [GeneratedRegex(@"\bThievery:\s+\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex ThieveryRx();
    [GeneratedRegex(@"\bTraps:\s+\*?\s*(\d+)",                          RegexOptions.CultureInvariant)] private static partial Regex TrapsRx();
    [GeneratedRegex(@"\bPicklocks:\s+\*?\s*(\d+)",                      RegexOptions.CultureInvariant)] private static partial Regex PicklocksRx();
    [GeneratedRegex(@"\bTracking:\s+\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex TrackingRx();
    [GeneratedRegex(@"\bStrength:\s+\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex StrengthRx();
    [GeneratedRegex(@"\bIntellect:\s+\*?\s*(\d+)",                      RegexOptions.CultureInvariant)] private static partial Regex IntellectRx();
    [GeneratedRegex(@"\bWillpower:\s+\*?\s*(\d+)",                      RegexOptions.CultureInvariant)] private static partial Regex WillpowerRx();
    [GeneratedRegex(@"\bAgility:\s+\*?\s*(\d+)",                        RegexOptions.CultureInvariant)] private static partial Regex AgilityRx();
    [GeneratedRegex(@"\bHealth:\s+\*?\s*(\d+)",                         RegexOptions.CultureInvariant)] private static partial Regex HealthRx();
    [GeneratedRegex(@"\bCharm:\s+\*?\s*(\d+)",                          RegexOptions.CultureInvariant)] private static partial Regex CharmRx();
    [GeneratedRegex(@"\bMartial Arts:\s+\*?\s*(\d+)",                   RegexOptions.CultureInvariant)] private static partial Regex MartialArtsRx();
    [GeneratedRegex(@"\bMagicRes:\s+\*?\s*(\d+)",                       RegexOptions.CultureInvariant)] private static partial Regex MagicResRx();
    [GeneratedRegex(@"\bSpellcasting:\s+\*?\s*(\d+)",                   RegexOptions.CultureInvariant)] private static partial Regex SpellcastingRx();

    // The single-line `exp`-command output. Anchored at line start
    // so a chat line embedding "Exp: ..." can't match. Five
    // captures: current exp, current level, exp delta to next
    // level, absolute next-level threshold, percent progress.
    [GeneratedRegex(@"^Exp:\s+(\d+)\s+Level:\s+(\d+)\s+Exp needed for next level:\s+(\d+)\s+\((\d+)\)\s+\[(\d+)%\]",
        RegexOptions.CultureInvariant)] private static partial Regex ExpLineRx();

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

    // Chat-line shape — matched at line start. Any of the standard
    // MajorMUD chat verbs after a single-word speaker means the
    // entire line is chat noise (including the user's own outgoing
    // gossip / say echoes). Used as a skip-guard in ScanLine so a
    // chat line embedding a stat label can't write to PlayerStats
    // even if it lands inside the scan window.
    [GeneratedRegex(@"^\w+\s+(?:gossips|telepaths|yells|says|auctions|gangpaths|broadcasts):",
        RegexOptions.CultureInvariant)] private static partial Regex ChatLineRx();
}
