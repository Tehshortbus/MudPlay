using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// Party reaction to an ALLY dropping to the ground (hitting 0 HP — mortally
// wounded, bleeding out, not yet dead). Distinct from PlayerDroppedGate, which
// owns OUR OWN drop; this owns everyone else's. Not scoped to a healer: `aid` is
// universal and by itself lifts a dropped ally back above 0 HP, so any party
// member reacts — a heal top-up (when configured) merely speeds the recovery.
//
// The whole flow is confirmed MajorMUD mechanics (GAME_MECHANICS "Dropped ally rescue"):
//   • "<name> drops to the ground!" is the room/party-side signal a member went
//     down. The dropper sees it with their own name, so a self-match is ignored.
//   • A dropped ally leaves `par` — their vitals stop refreshing — so once we've
//     brought them back we poll their HP out-of-band via an `@health` telepath.
//   • `aid <name>` (universal) stops the bleeding; it doesn't stand them up. HP
//     then climbs 1 per 30 s tick until it's positive, and until then they can't
//     act or answer a telepath. A heal cast AT THEM BY NAME still lands even though
//     they're off the roster and gets them up sooner (fed to CastingDirector via
//     AidedDownedGivenNames).
//   • So the rescue is timed off the climb: a `@health` reply at negative HP gives
//     the exact wait, else the realm's death floor gives the longest it can take
//     (the deepest living HP is floor + 1). We check on them once that's up.
//   • Recovery to positive HP does NOT auto-rejoin the party — an explicit
//     `invite <name>` is required, and they can't accept it until they're up. Only
//     the leader can send it, so the re-invite waits for a positive `@health` and
//     is gated on SelfIsLeader (a follower's leader re-invites THEM instead).
//   • The drop is treated as a wait condition: AllyDownGate holds every movement
//     engine so we stay in the room and keep aiding / healing rather than walking
//     the farm loop off without the downed member.
//
// Recognition gap this handler closes: in the reported failure the ally that
// dropped was the LEADER, and a leader-disconnect dissolves the party — so by the
// time "MudPlay drops to the ground!" arrived, MudPlay was already off our roster AND
// absent from PartyManager's disconnect grace map (a follower never stamps its own
// leader there). We therefore keep our OWN recent-leader memory: when LeaderName
// clears we snapshot the departing leader's given name with a timestamp, and a
// drop line for that name within RecentLeaderGrace is recognised as an ally.
//
// Threading: MessageRouter + ChatRouter already marshal to the UI thread and the
// DispatcherTimer ticks on it, so all state here is touched single-threaded.
public sealed partial class AllyDroppedHandler : IDisposable
{
    // Surfaced in MovementCoordinator.History as the AllyDownGate asserter.
    public const string AsserterName = "AllyDroppedHandler";

    // LogService category — party-flavoured lifecycle rows.
    private const string LogCategory = "Party";

    // One regen tick: an aided ally climbs 1 HP per tick (GAME_MECHANICS "0 HP —
    // dropped / bleeding out").
    private static readonly TimeSpan HpTick = TimeSpan.FromSeconds(30);

    // How long past the expected stand-up time we keep asking before giving up, so
    // a botched rescue can't wedge the movement hold forever.
    private static readonly TimeSpan CheckGrace = TimeSpan.FromSeconds(60);

    // Once they're up, how long we keep holding (and healing by name) for them to
    // reach the party-heal bar before moving on.
    private static readonly TimeSpan TopUpWindow = TimeSpan.FromSeconds(120);

    // Cadence for the `/given @health` poll once they should be able to answer.
    private static readonly TimeSpan PollCadence = TimeSpan.FromSeconds(5);

    // Window after our leader is lost (LeaderName → null) during which a drop line
    // for that former leader is still recognised as an ally — covers the leader-
    // disconnect → dissolve → reconnect → drop sequence where the leader is off
    // our roster (and never in the disconnect grace map) by drop time.
    private static readonly TimeSpan RecentLeaderGrace = TimeSpan.FromSeconds(60);

    private readonly MessageRouter _router;
    private readonly PartyState _party;
    private readonly PartyManager _manager;
    private readonly ChatRouter _chat;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<PartySettings> _readParty;
    private readonly Func<bool> _isEnabled;
    private readonly Func<int> _readDeathFloor;
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();
    // Arms one poll tick after the cadence; disposing what it returns cancels it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _pollTick;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // Per-ally rescue state, keyed by given name (OrdinalIgnoreCase).
    private sealed class DownedAlly
    {
        public string Given = string.Empty;
        public DateTime DroppedAt;
        public bool Aided;
        public bool Invited;
        public DateTime? LastPollAt;
        // When they should be up (first @health), and when we stop waiting.
        public DateTime CheckAt;
        public DateTime Deadline;
        // A @health reply gave their negative HP, so CheckAt is exact.
        public bool HpKnown;
        // A @health reply showed positive HP — they're on their feet.
        public bool Up;
    }

    private readonly Dictionary<string, DownedAlly> _downed =
        new(StringComparer.OrdinalIgnoreCase);

    // Recent-leader memory (see RecentLeaderGrace). _currentLeaderGiven tracks the
    // live leader; when LeaderName clears we snapshot it here with a timestamp.
    private string? _currentLeaderGiven;
    private string? _recentLeaderGiven;
    private DateTime _recentLeaderAt;

    private readonly Func<DateTime> _now;

    // Canonical @health reply shape, mirroring PartyPoller: {HP=cur/max[,MA=cur/max]
    // [,status text]}. The cur halves accept a leading minus so a still-negative (aided but
    // not yet fully healed) reading parses instead of silently failing to match.
    [GeneratedRegex(
        @"^\{?HP=(?<hp>-?\d+)/(?<hpmax>\d+)(?:,(?:MA|KAI)=(?<mp>-?\d+)/(?<mpmax>\d+))?(?:,[^}]*)?\}?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HealthReply();

    // schedule stands in for the dispatcher's poll timer; now for the clock.
    public AllyDroppedHandler(
        MessageRouter router,
        PartyState party,
        PartyManager manager,
        ChatRouter chat,
        MovementCoordinator coordinator,
        Func<PartySettings> readParty,
        Func<bool> isEnabled,
        LogService? log = null,
        Func<int>? readDeathFloor = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(readParty);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _router = router;
        _party = party;
        _manager = manager;
        _chat = chat;
        _coordinator = coordinator;
        _readParty = readParty;
        _isEnabled = isEnabled;
        _readDeathFloor = readDeathFloor ?? (static () => -25);
        _log = log;
        _schedule = schedule ?? DispatcherTicks.Once;

        _subs.Add(_router.Subscribe(KnownPatterns.PartyMemberDropped, OnDropped));
        _subs.Add(_router.Subscribe(KnownPatterns.UserAidedAlly,      OnAided));
        // Any way a tracked ally can leave the picture — died, slain, logged off,
        // disconnected, or walked out — resolves the rescue so the hold releases.
        _subs.Add(_router.Subscribe(KnownPatterns.PartyMemberDied,    OnAllyGone));
        _subs.Add(_router.Subscribe(KnownPatterns.PartyMemberDeath,   OnAllyGone));
        _subs.Add(_router.Subscribe(KnownPatterns.PlayerDisconnects,  OnAllyGone));
        _subs.Add(_router.Subscribe(KnownPatterns.PlayerHungUp,       OnAllyGone));
        _subs.Add(_router.Subscribe(KnownPatterns.PlayerExits,        OnAllyGone));

        _party.PropertyChanged += OnPartyPropertyChanged;
        _party.Members.CollectionChanged += OnMembersChanged;
        _chat.EntryClassified += OnChatEntry;

        // Seed the current leader if we wire up mid-party.
        if (!string.IsNullOrEmpty(_party.LeaderName))
            _currentLeaderGiven = GivenName(_party.LeaderName);
    }

    // Bind the wire-sender — same gate-wrapped engineSend the other party engines
    // get, so `aid` / `@health` are held while WE are mortally wounded. Without it
    // the handler still tracks state but can't send anything.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Every ally we're holding for while they're down, aided or not yet.
    public IReadOnlyCollection<string> DownedGivenNames => _downed.Keys;

    // Given names of aided-but-still-off-roster downed allies the CastingDirector
    // should top up by name (fed via SetDownedAllyProvider). Empty until an ally
    // is aided — an un-aided, still-mortally-wounded ally can't be healed anyway.
    public IReadOnlyList<string> AidedDownedGivenNames()
    {
        List<string>? names = null;
        foreach (DownedAlly a in _downed.Values)
        {
            if (!a.Aided) continue;
            (names ??= new()).Add(a.Given);
        }
        return names ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    // ----- Drop / aid / gone observers -----------------------------------

    // One of our own (not us) dropped to the ground. Carries the given name.
    public event Action<string>? AllyDown;

    private void OnDropped(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        HandleDropped(result.Groups[0]);
    }

    private void OnAided(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        HandleAided(result.Groups[0]);
    }

    private void OnAllyGone(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        HandleAllyGone(result.Groups[0]);
    }

    private void HandleDropped(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        string given = GivenName(name);
        if (given.Length == 0) return;

        // Never react to our OWN drop line (the dropper sees their own name) — the
        // self-drop path is owned by PlayerDroppedGate.
        string? self = _manager.LocalCharacterName;
        if (!string.IsNullOrEmpty(self)
            && GivenName(self).Equals(given, StringComparison.OrdinalIgnoreCase))
            return;

        if (!IsRecognisedAlly(given, name)) return;

        // Raised whether or not the rescue is switched on: a listener only wants to
        // know one of ours went down.
        AllyDown?.Invoke(given);
        if (!_isEnabled())
        {
            // Not rescued now, but not forgotten: if they are still down when the
            // rescue is switched back on, it starts then (ResumeOwedRescues).
            _owed[given] = name;
            return;
        }
        _owed.Remove(given);

        // Deliberately NOT scoped to a party-heal loadout. `aid <name>` is a
        // universal command and, per the confirmed drop mechanics, aid alone
        // lifts a mortally-wounded ally back above 0 HP — a heal top-up only
        // speeds it. So a non-healer (a Mystic whose only heal is a self-power,
        // the reported case) still aids, holds movement, and — when leading —
        // re-invites; the name-heal top-up simply no-ops without a party-heal
        // spell (CastingDirector reads AidedDownedGivenNames). Master gate is
        // AutoHeal via _isEnabled; IsRecognisedAlly keeps it to our own.

        if (_downed.ContainsKey(given)) return; // already rescuing this ally

        bool first = _downed.Count == 0;
        DateTime now = _now();
        DownedAlly ally = new() { Given = given, DroppedAt = now };
        _downed[given] = ally;
        if (first)
        {
            _coordinator.AssertGate(MovementCoordinator.AllyDownGate, AsserterName,
                $"ally-down={given}");
            EnsurePollTimerRunning();
        }
        TimeSpan worst = WorstCaseStand();
        ScheduleCheck(ally, now + worst);
        SendAid(given);
        // Worth one ask: a reply at negative HP times the rescue exactly.
        SendHealthPoll(given);
        _log?.Info(LogCategory,
            $"Ally {given} dropped — aiding + holding movement; up within {worst.TotalSeconds:0}s " +
            $"(death floor {_readDeathFloor()}) unless healed.");
    }

    private void HandleAided(string name)
    {
        string given = GivenName(name);
        if (given.Length == 0) return;
        if (!_downed.TryGetValue(given, out DownedAlly? a)) return;
        // Aided: the bleeding stops and the climb to positive HP starts now, so the
        // stand-up clock restarts here (unless a @health reply already timed it).
        // The name-targeted heal can land from here on (fed to CastingDirector).
        a.Aided = true;
        if (!a.HpKnown && !a.Up)
            ScheduleCheck(a, _now() + WorstCaseStand());
        _log?.Info(LogCategory,
            $"Ally {given} aided — healing by name; checking on them at {a.CheckAt.ToLocalTime():HH:mm:ss}.");
    }

    // The longest an aided ally can take to stand: from the deepest living HP
    // (floor + 1) up to 1 HP, one HP per tick.
    private TimeSpan WorstCaseStand()
    {
        int floor = Math.Min(_readDeathFloor(), 0);
        return HpTick * Math.Max(1, -floor);
    }

    private static void ScheduleCheck(DownedAlly a, DateTime checkAt)
    {
        a.CheckAt = checkAt;
        a.Deadline = checkAt + CheckGrace;
    }

    // Recovery does NOT auto-rejoin the party — an explicit invite is required, and
    // only the leader can send it. A follower's leader re-invites THEM.
    private void ReInviteIfLeading(DownedAlly a)
    {
        if (!_party.SelfIsLeader || a.Invited) return;
        _manager.Invite(a.Given);
        a.Invited = true;
        _log?.Info(LogCategory, $"Ally {a.Given} is up — re-invited to the party.");
    }

    private void HandleAllyGone(string name)
    {
        string given = GivenName(name);
        if (given.Length == 0) return;
        _owed.Remove(given);
        Resolve(given, "ally left / died");
    }

    // Allies seen to drop whose rescue is owed: the drop came while the rescue
    // was off, or the master switch ended a rescue under way. Given name → the
    // name on the drop line. One comes off the list when they are seen to leave,
    // die or rejoin the roster.
    private readonly Dictionary<string, string> _owed = new(StringComparer.OrdinalIgnoreCase);

    // The master switch is going off: end every rescue (its poll and aid would
    // go on sending by themselves) but keep who was down.
    public void HoldForMasterSwitch()
    {
        List<string> rescuing = _downed.Keys.ToList();
        Clear("master switch off");
        // Only this off-period's: a drop from an hour ago with Auto Heal unticked
        // is not something to start aiding now.
        _owed.Clear();
        foreach (string given in rescuing) _owed[given] = given;
    }

    // The master switch is back on: start the rescue for everyone still owed
    // one. A name that has left, died or rejoined meanwhile is off the list
    // already; one who got up unseen costs an aid and a few @health asks before
    // the rescue gives up.
    public void ResumeOwedRescues()
    {
        List<string> names = _owed.Values.ToList();
        _owed.Clear();
        if (!_isEnabled()) return;
        foreach (string name in names) HandleDropped(name);
    }

    // True when the dropped name is one of ours: a live roster member, a member
    // who left our roster inside the disconnect grace window (a follower we lead
    // who dropped), or our recently-lost leader (the report's case).
    private bool IsRecognisedAlly(string given, string fullName)
    {
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            if (GivenName(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (_manager.WasRecentlyPartied(fullName)) return true;
        if (_currentLeaderGiven is { } cur
            && cur.Equals(given, StringComparison.OrdinalIgnoreCase))
            return true;
        if (_recentLeaderGiven is { } recent
            && recent.Equals(given, StringComparison.OrdinalIgnoreCase)
            && _now() - _recentLeaderAt <= RecentLeaderGrace)
            return true;
        return false;
    }

    // ----- Leader tracking + rejoin clear --------------------------------

    private void OnPartyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PartyState.LeaderName)) return;
        string? leader = _party.LeaderName;
        if (!string.IsNullOrEmpty(leader))
        {
            _currentLeaderGiven = GivenName(leader);
        }
        else if (_currentLeaderGiven is { } lost)
        {
            // Leadership just cleared (dissolution / leader disconnect). Remember
            // the departing leader so a drop line for them lands as an ally.
            _recentLeaderGiven = lost;
            _recentLeaderAt = _now();
            _currentLeaderGiven = null;
        }
    }

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (e.NewItems is null) return;
        // A downed ally genuinely rejoining the roster ends the rescue — normal
        // party-heal covers them from here. A bare [Invited] placeholder is NOT a
        // rejoin: a mortally-wounded ally can't accept the invite, so releasing the
        // rescue on it would stop the @health poll and name-heal targeting while
        // they're still down. Hold until they actually join / recover / time out.
        foreach (object? item in e.NewItems)
        {
            if (item is not PartyMember m) continue;
            if (m.IsInvited) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            _owed.Remove(GivenName(m.Name));
            Resolve(GivenName(m.Name), "rejoined roster");
        }
    }

    // ----- @health poll --------------------------------------------------

    private void OnChatEntry(ChatLogEntry entry)
    {
        if (entry.Channel != ChatChannel.TelepathIncoming) return;
        if (string.IsNullOrEmpty(entry.Speaker)) return;
        string given = GivenName(entry.Speaker);
        if (!_downed.TryGetValue(given, out DownedAlly? a)) return;
        if (string.IsNullOrEmpty(entry.Message)) return;
        Match m = HealthReply().Match(entry.Message);
        if (!m.Success) return;
        int hpCur = int.Parse(m.Groups["hp"].Value,    CultureInfo.InvariantCulture);
        int hpMax = int.Parse(m.Groups["hpmax"].Value, CultureInfo.InvariantCulture);
        DateTime now = _now();
        if (hpCur <= 0)
        {
            // Still down: they reach 1 HP after (1 − hp) ticks. 0 isn't up.
            a.HpKnown = true;
            ScheduleCheck(a, now + HpTick * (1 - hpCur));
            _log?.Info(LogCategory,
                $"Ally {given} at HP {hpCur} — up in about {(a.CheckAt - now).TotalSeconds:0}s; checking then.");
            return;
        }
        if (!a.Up)
        {
            a.Up = true;
            a.Deadline = now + TopUpWindow;
            ReInviteIfLeading(a);
        }
        int pct = hpMax > 0 ? hpCur * 100 / hpMax : 0;
        // Recovered once they're back at / above the party-heal trigger — no longer
        // hurt enough to heal, so stop holding + stop topping them up. Clamp to a
        // positive bar so a misconfigured 0 threshold can't release on the first
        // (still-critical) reply.
        int recovered = Math.Max(1, _readParty().MinorHealMemberThresholdPercent);
        if (pct >= recovered)
            Resolve(given, $"recovered HP {hpCur}/{hpMax} ({pct}%)");
    }

    // One poll tick: the next is armed before this one runs.
    private void OnPollTick()
    {
        _pollTick = _schedule(PollCadence, OnPollTick);
        TickPoll();
    }

    private void TickPoll()
    {
        if (_downed.Count == 0) { StopPollTimer(); return; }
        DateTime now = _now();
        foreach (string given in _downed.Keys.ToArray())
        {
            if (!_downed.TryGetValue(given, out DownedAlly? a)) continue;
            if (now >= a.Deadline)
            {
                Resolve(given, a.Up
                    ? $"up but not back to the heal bar within {TopUpWindow.TotalSeconds:0}s"
                    : $"no sign of them standing {CheckGrace.TotalSeconds:0}s past the expected time");
                continue;
            }
            // Don't poll before they can stand — a downed ally can't answer a
            // telepath (every action bounces until their HP is positive).
            if (!a.Up && now < a.CheckAt) continue;
            if (a.LastPollAt is { } last && now - last < PollCadence) continue;
            SendHealthPoll(given);
            a.LastPollAt = now;
        }
    }

    // ----- Resolution + wire ---------------------------------------------

    // Reset States: stop the rescue of every downed ally and release the hold.
    public void Clear(string reason)
    {
        foreach (string given in _downed.Keys.ToList()) Resolve(given, reason);
    }

    // Drop a tracked ally and, once the last one is resolved, release the movement
    // hold + stop the poll timer. Idempotent — an untracked name is a no-op.
    private void Resolve(string given, string reason)
    {
        if (!_downed.Remove(given)) return;
        _log?.Info(LogCategory, $"Ally {given} rescue resolved — {reason}.");
        if (_downed.Count == 0)
        {
            _coordinator.ClearGate(MovementCoordinator.AllyDownGate, AsserterName, reason);
            StopPollTimer();
        }
    }

    private void SendAid(string given)
    {
        _wireSender?.Invoke(Encoding.Latin1.GetBytes($"aid {given}\r"));
    }

    private void SendHealthPoll(string given)
    {
        // MajorMUD telepath syntax: `/<given> <msg>` (slash + given name, no space).
        _wireSender?.Invoke(Encoding.Latin1.GetBytes($"/{given} @health\r"));
    }

    private void EnsurePollTimerRunning() => _pollTick ??= _schedule(PollCadence, OnPollTick);

    private void StopPollTimer()
    {
        _pollTick?.Dispose();
        _pollTick = null;
    }

    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable s in _subs) s.Dispose();
        _subs.Clear();
        _party.PropertyChanged -= OnPartyPropertyChanged;
        _party.Members.CollectionChanged -= OnMembersChanged;
        _chat.EntryClassified -= OnChatEntry;
        StopPollTimer();
        if (_downed.Count > 0)
        {
            _downed.Clear();
            _coordinator.ClearGate(MovementCoordinator.AllyDownGate, AsserterName, "disposed");
        }
    }
}
