using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

// Party-healer reaction to an ALLY dropping to the ground (hitting 0 HP —
// mortally wounded, bleeding out, not yet dead). Distinct from PlayerDroppedGate,
// which owns OUR OWN drop; this owns everyone else's.
//
// The whole flow is confirmed MajorMUD mechanics (see GAME_MECHANICS.md, the
// "Drop removes you from your party" section):
//   • "<name> drops to the ground!" is the room/party-side signal a member went
//     down. The dropper sees it with their own name, so a self-match is ignored.
//   • A dropped ally leaves `par` — their vitals stop refreshing — so once we've
//     brought them back we poll their HP out-of-band via an `@health` telepath.
//   • `aid <name>` (universal) lifts a dropped ally back to positive HP; a heal
//     cast AT THEM BY NAME still lands even though they're off the roster. So we
//     aid first, then keep topping them up by name (fed to CastingDirector via
//     AidedDownedGivenNames) until they recover.
//   • Recovery to positive HP does NOT auto-rejoin the party — an explicit
//     `invite <name>` is required. Only the leader can do that, so the re-invite
//     step is gated on SelfIsLeader (a follower's leader re-invites THEM instead).
//   • The drop is treated as a wait condition: AllyDownGate holds every movement
//     engine so we stay in the room and keep aiding / healing rather than walking
//     the farm loop off without the downed member.
//
// Recognition gap this handler closes: in the reported failure the ally that
// dropped was the LEADER, and a leader-disconnect dissolves the party — so by the
// time "Fujin drops to the ground!" arrived, Fujin was already off our roster AND
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

    // Give up on a downed ally that never got aided / never recovered after this
    // long, so a botched rescue can't wedge the movement hold forever.
    private static readonly TimeSpan RescueTimeout = TimeSpan.FromSeconds(120);

    // Cadence for the `/given @health` poll on an aided-but-off-roster ally.
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
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();
    private readonly DispatcherTimer? _pollTimer;
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
    }

    private readonly Dictionary<string, DownedAlly> _downed =
        new(StringComparer.OrdinalIgnoreCase);

    // Recent-leader memory (see RecentLeaderGrace). _currentLeaderGiven tracks the
    // live leader; when LeaderName clears we snapshot it here with a timestamp.
    private string? _currentLeaderGiven;
    private string? _recentLeaderGiven;
    private DateTime _recentLeaderAt;

    // Test seam — overrides DateTime.UtcNow for the poll / grace clocks.
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    // Canonical @health reply shape, mirroring PartyPoller: {HP=cur/max[,MA=cur/max]
    // [,pos]}. The cur halves accept a leading minus so a still-negative (aided but
    // not yet fully healed) reading parses instead of silently failing to match.
    [GeneratedRegex(
        @"^\{?HP=(?<hp>-?\d+)/(?<hpmax>\d+)(?:,(?:MA|KAI)=(?<mp>-?\d+)/(?<mpmax>\d+))?(?:,\s*\w+)?\}?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HealthReply();

    // Default constructor — wires the in-process DispatcherTimer. Use the
    // test-seam ctor (no timer) for unit tests.
    public AllyDroppedHandler(
        MessageRouter router,
        PartyState party,
        PartyManager manager,
        ChatRouter chat,
        MovementCoordinator coordinator,
        Func<PartySettings> readParty,
        Func<bool> isEnabled,
        LogService? log = null)
        : this(router, party, manager, chat, coordinator, readParty, isEnabled,
               useTimer: true, log) { }

    internal AllyDroppedHandler(
        MessageRouter router,
        PartyState party,
        PartyManager manager,
        ChatRouter chat,
        MovementCoordinator coordinator,
        Func<PartySettings> readParty,
        Func<bool> isEnabled,
        bool useTimer,
        LogService? log = null)
    {
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
        _log = log;

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

        if (useTimer)
        {
            _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = PollCadence,
            };
            _pollTimer.Tick += (_, _) => TickPoll();
        }
    }

    // Bind the wire-sender — same gate-wrapped engineSend the other party engines
    // get, so `aid` / `@health` are held while WE are mortally wounded. Without it
    // the handler still tracks state but can't send anything.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

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
        if (!_isEnabled()) return;

        // Never react to our OWN drop line (the dropper sees their own name) — the
        // self-drop path is owned by PlayerDroppedGate.
        string? self = _manager.LocalCharacterName;
        if (!string.IsNullOrEmpty(self)
            && GivenName(self).Equals(given, StringComparison.OrdinalIgnoreCase))
            return;

        if (!IsRecognisedAlly(given, name)) return;

        // Scope the auto-rescue to a configured party healer — matches the
        // confirmed client-reaction spec. Aid is universal, but keeping the
        // automatic behaviour opt-in to a party-heal loadout means a non-healer
        // isn't surprised by the client aiding on its own.
        PartySettings settings = _readParty();
        if (string.IsNullOrWhiteSpace(settings.MinorPartyHealSpell)
            && string.IsNullOrWhiteSpace(settings.MajorPartyHealSpell))
            return;

        if (_downed.ContainsKey(given)) return; // already rescuing this ally

        bool first = _downed.Count == 0;
        _downed[given] = new DownedAlly { Given = given, DroppedAt = NowProvider() };
        if (first)
        {
            _coordinator.AssertGate(MovementCoordinator.AllyDownGate, AsserterName,
                $"ally-down={given}");
            EnsurePollTimerRunning();
        }
        SendAid(given);
        _log?.Info(LogCategory,
            $"Ally {given} dropped — aiding + holding movement to rescue.");
    }

    private void HandleAided(string name)
    {
        string given = GivenName(name);
        if (given.Length == 0) return;
        if (!_downed.TryGetValue(given, out DownedAlly? a)) return;
        // Aided back to positive HP — the name-targeted heal will land now (fed to
        // CastingDirector), and the @health poll can get a reply (a still-mortally-
        // wounded ally bounces every action, including answering a telepath).
        a.Aided = true;
        // Recovery does NOT auto-rejoin the party — an explicit invite is required,
        // and only the leader can send it. A follower's leader re-invites THEM.
        if (_party.SelfIsLeader && !a.Invited)
        {
            _manager.Invite(given);
            a.Invited = true;
            _log?.Info(LogCategory, $"Ally {given} aided — re-invited to the party.");
        }
        else
        {
            _log?.Info(LogCategory, $"Ally {given} aided — healing by name until recovered.");
        }
    }

    private void HandleAllyGone(string name)
    {
        string given = GivenName(name);
        if (given.Length == 0) return;
        Resolve(given, "ally left / died");
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
            && NowProvider() - _recentLeaderAt <= RecentLeaderGrace)
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
            _recentLeaderAt = NowProvider();
            _currentLeaderGiven = null;
        }
    }

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (e.NewItems is null) return;
        // A downed ally reappearing on the roster means they've been re-invited and
        // rejoined — the rescue is over; normal party-heal covers them from here.
        foreach (object? item in e.NewItems)
        {
            if (item is not PartyMember m) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            Resolve(GivenName(m.Name), "rejoined roster");
        }
    }

    // ----- @health poll --------------------------------------------------

    private void OnChatEntry(ChatLogEntry entry)
    {
        if (entry.Channel != ChatChannel.TelepathIncoming) return;
        if (string.IsNullOrEmpty(entry.Speaker)) return;
        string given = GivenName(entry.Speaker);
        if (!_downed.TryGetValue(given, out DownedAlly? _)) return;
        if (string.IsNullOrEmpty(entry.Message)) return;
        Match m = HealthReply().Match(entry.Message);
        if (!m.Success) return;
        int hpCur = int.Parse(m.Groups["hp"].Value,    CultureInfo.InvariantCulture);
        int hpMax = int.Parse(m.Groups["hpmax"].Value, CultureInfo.InvariantCulture);
        int pct = hpMax > 0 ? hpCur * 100 / hpMax : 0;
        // Recovered once they're back at / above the party-heal trigger — no longer
        // hurt enough to heal, so stop holding + stop topping them up. Clamp to a
        // positive bar so a misconfigured 0 threshold can't release on the first
        // (still-critical) reply.
        int recovered = Math.Max(1, _readParty().MinorHealMemberThresholdPercent);
        if (pct >= recovered)
            Resolve(given, $"recovered HP {hpCur}/{hpMax} ({pct}%)");
    }

    internal void TickPollForTests() => TickPoll();

    private void TickPoll()
    {
        if (_downed.Count == 0) { StopPollTimer(); return; }
        DateTime now = NowProvider();
        foreach (string given in _downed.Keys.ToArray())
        {
            if (!_downed.TryGetValue(given, out DownedAlly? a)) continue;
            if (now - a.DroppedAt >= RescueTimeout)
            {
                Resolve(given, $"rescue timeout ({RescueTimeout.TotalSeconds:0}s)");
                continue;
            }
            // Poll vitals only once aided — a still-mortally-wounded ally can't
            // answer an @health telepath (every action bounces until aided).
            if (!a.Aided) continue;
            if (a.LastPollAt is { } last && now - last < PollCadence) continue;
            SendHealthPoll(given);
            a.LastPollAt = now;
        }
    }

    // ----- Resolution + wire ---------------------------------------------

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

    private void EnsurePollTimerRunning()
    {
        if (_pollTimer is null) return;
        if (!_pollTimer.IsEnabled) _pollTimer.Start();
    }

    private void StopPollTimer() => _pollTimer?.Stop();

    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    // ----- Test seams ----------------------------------------------------

    internal void NoteDroppedForTests(string playerName) => HandleDropped(playerName);
    internal void NoteAidedForTests(string playerName) => HandleAided(playerName);
    internal void NoteAllyGoneForTests(string playerName) => HandleAllyGone(playerName);
    internal void NoteHealthReplyForTests(string speaker, string message) =>
        OnChatEntry(new ChatLogEntry(
            DateTimeOffset.UtcNow, ChatChannel.TelepathIncoming, speaker, message, message));
    internal bool IsTrackingForTests(string given) => _downed.ContainsKey(GivenName(given));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable s in _subs) s.Dispose();
        _subs.Clear();
        _party.PropertyChanged -= OnPartyPropertyChanged;
        _party.Members.CollectionChanged -= OnMembersChanged;
        _chat.EntryClassified -= OnChatEntry;
        _pollTimer?.Stop();
        if (_downed.Count > 0)
        {
            _downed.Clear();
            _coordinator.ClearGate(MovementCoordinator.AllyDownGate, AsserterName, "disposed");
        }
    }
}
