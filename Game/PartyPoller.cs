using System.Collections.Specialized;
using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Game.Combat;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game;

// Drives the two party-health cadences:
//
//   1. On-join `@health` exchange. When a fresh member lands in
//      PartyState.Members (via PartyManager's follows-you / par parsing), the
//      poller telepaths `@health` to that member, parses the reply, and writes
//      the absolute HP/MA into the matching PartyMember's BaselineHp /
//      BaselineMp through PartyManager.SetMemberHealthSnapshot.
//   2. `par` poll. Sends `par` on the wire, which the server responds to with
//      the multi-line table PartyManager already parses to update HP%/MA%/position
//      for every member. Settings → Party picks what sends it, any mix or none: a
//      DispatcherTimer ticking at ParCadence (5 s default), a combat round we
//      fought in closing (or, ticked in, one we only witnessed), and a round
//      whose totals carry unknown damage. Apart from those, RequestPar sends one
//      for a caller that needs to know who is still in the party.
//
// Reply-format match: the on-join reply comes back as a telepath carrying the
// member's PartyEssentialHandlers.OnHealth answer, "{HP=690/720,MA=200/300, Resting}".
// The poller subscribes to ChatRouter.EntryClassified and watches incoming
// telepaths whose body matches the canonical health-reply regex; the speaker
// field tells us which member to update.
//
// Self handling: PartyMember.IsSelf rows are skipped on the @health round-trip
// — our own HP/MA flows in through PromptParser on every statline observation,
// which is fresher than a self-sent telepath would be.
//
// Lifetime: poller is app-singleton like PartyManager; it's safe to keep the
// timer running even when not in a party — SendPar short-circuits on
// PartyState.IsInParty = false so we don't spam `par` at the wire while solo.
// It also short-circuits on IsParPollEnabled so the poll obeys the
// auto-heal toggle (and, through it, the auto-all kill switch).
public sealed partial class PartyPoller : IDisposable
{
    public const string LogSource = "PartyPoll";

    private readonly ChatRouter _chat;
    private readonly PartyState _state;
    private readonly PartyManager _manager;
    private readonly LogService? _log;
    // Arms one tick after the delay; disposing what it returns cancels it. The par
    // cadence and the @health nag each keep their own tick on it.
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _parTick;
    private IDisposable? _healthNagTick;
    private Action<byte[]>? _wireSender;
    // Suspended between a disconnect and the first in-game prompt after we're back
    // in the realm. `par` and the @health round-trip are wall-clock timers that
    // fire independent of the game prompt, so left running they put `par` /
    // telepaths on the wire while the client sits at the BBS login menu, where they
    // land as stray menu keystrokes and derail re-entry (report stock-20260731-004105).
    private bool _suspended;
    private bool _disposed;

    // How often the timer sends `par` on the wire. Default 5 s.
    public TimeSpan ParCadence { get; private set; } = TimeSpan.FromSeconds(5);

    // What sends `par` (Settings → Party, ApplyParSettings): any mix of the three,
    // or none — then nothing here sends it, and members' HP between the user's own
    // `par`s is whatever PartyHpEstimator reads off the round ledger.
    public bool ParOnTimer { get; private set; } = true;
    public bool ParAfterCombatRound { get; private set; }
    // ParAfterCombatRound also takes rounds we only witnessed.
    public bool ParIncludeWitnessedRounds { get; private set; }
    public bool ParOnUnknownRoundDamage { get; private set; }

    // Live gate for every automatic `par`. `par`'s sole purpose is reading party
    // health for the party heals, so it rides the auto-heal toggle — when that's
    // off (and because AutoModeController's kill-all zeroes the heal flag, when
    // auto-all is off too) nothing here
    // may put `par` on the wire. Null = ungated (test / pre-wire default),
    // matching the historical always-on behaviour.
    public Func<bool>? IsParPollEnabled { get; set; }

    // Live gate for a `par` a caller asks for to learn who is in the party
    // (RequestPar). That is not a health read, so it doesn't ride the auto-heal
    // toggle: only the master switch stops it, as it stops everything automatic.
    // Null = ungated (test / pre-wire default).
    public Func<bool>? IsAutomationEnabled { get; set; }

    // Live gate — true while the character is parked in the full-screen trainer
    // stats menu. Both timer cadences here (par poll + @health nag) fire on a
    // wall clock rather than off the in-game prompt, so unlike prompt-driven
    // automation they don't naturally pause when the trainer screen suppresses
    // the statline. Left ungated they leak `par` / telepaths into the menu,
    // where they're swallowed as stray keystrokes. The auto-trainer's own CP
    // replay is unaffected — it sends through its own wire path, not this poller.
    // Null = never suppressed (test / pre-wire default).
    public Func<bool>? IsInTrainerMenu { get; set; }

    private bool InTrainerMenu => IsInTrainerMenu is { } gate && gate();

    // ----- @health nag escalation knobs (shared with @join nag) ----------
    // Pushed in by AppServices.ApplyPartyFromActiveProfile from the same
    // Settings.Party JoinNag* fields AutoPartyManager reads. The label in
    // the UI is "@join/@health nag settings" — both nag flows share the
    // cadence because they share the user intent ("after asking for X,
    // wait this long, retry this often, give up after this window").

    // Wait this long after the initial `/given @health` before the first retry.
    public TimeSpan HealthNagInitialDelay { get; set; } = TimeSpan.FromSeconds(5);
    // Cadence for subsequent `/given @health` resends.
    public TimeSpan HealthNagFrequency { get; set; } = TimeSpan.FromSeconds(10);
    // Hard cap on the total nag window measured from the initial send.
    public TimeSpan HealthNagMaxTotal { get; set; } = TimeSpan.FromSeconds(55);

    // Master enable for the on-join `@health` round-trip + retry nag. When
    // false, a newly joined member is never telepathed `/given @health` and no
    // nag is armed — read at fire time, so toggling it off prevents new
    // round-trips (matching AutoPartyManager.JoinNagEnabled's start-time gate).
    // Mirrors PartySettings.SendHealthToMembers.
    public bool HealthNagEnabled { get; set; } = true;

    private readonly Func<DateTime> _now;

    // Per-given-name nag state. Created on the initial @health send; cleared on
    // baseline arrival / member-leave / cap.
    private readonly Dictionary<string, HealthNagState> _activeNags =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class HealthNagState
    {
        public string Given { get; set; } = string.Empty;
        public DateTime FirstSentAt { get; set; }
        public DateTime? LastSentAt { get; set; }
        public int Sends { get; set; }
    }

    // Canonical reply regex for the @health round-trip. Matches the shape
    // PartyEssentialHandlers.OnHealth produces —
    // {HP=cur/max,MA=cur/max[, Resting|Meditating]}; a MegaMUD member's reply can
    // carry several words of status there (", Losing HPs", ", Resting, Cant move"),
    // so anything up to the closing brace is accepted. The leading { + trailing }
    // are the brace-wrap the engine adds at SendReply time per the
    // remote-command meta-line convention. Mana group is optional (warriors
    // reply HP-only); position suffix is optional and ignored — we only need the
    // HP/MA numbers for baseline capture.
    [GeneratedRegex(
        // `cur` halves accept an optional leading `-` because MajorMUD
        // lets HP go from 0 down to a BBS-set death threshold (the
        // "dropped" state — alive but immobile, bleeding out unless
        // someone `aid`s them). Without the optional minus the regex
        // silently fails to match a dropped member's @health reply,
        // BaselineHp stays 0, and PartyPoller's nag retries until
        // max-total instead of cancelling on first valid reply.
        // Max halves stay `\d+` — max HP / MA are always positive.
        @"^\{?HP=(?<hp>-?\d+)/(?<hpmax>\d+)(?:,(?<mpkind>MA|KAI)=(?<mp>-?\d+)/(?<mpmax>\d+))?(?:,[^}]*)?\}?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HealthReply();

    // schedule stands in for the dispatcher's timers.
    public PartyPoller(ChatRouter chat, PartyState state, PartyManager manager, LogService? log = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(manager);
        _chat    = chat;
        _state   = state;
        _manager = manager;
        _log     = log;
        _schedule = schedule ?? DispatcherTicks.Once;

        _state.Members.CollectionChanged += OnMembersChanged;
        _chat.EntryClassified += OnChatEntry;
        StartParTick();
    }

    // Bind the wire-sender. Same shape as other managers — main-window VM
    // supplies SendUserInput. Without it the poller still observes party events
    // but can't send anything.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Take the `par` triggers and the timer's cadence from the character's Party
    // settings — on profile load and on every Settings save.
    public void ApplyParSettings(PartySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string before = ParTriggerSummary;
        TimeSpan cadence = TimeSpan.FromSeconds(Math.Clamp(settings.ParPollFrequencySec, 1, 60));
        // Only a changed cadence touches the timer: this runs on every profile save,
        // and re-setting the interval would start the countdown over each time.
        if (cadence != ParCadence)
        {
            ParCadence = cadence;
            if (_parTick is not null) StartParTick();
        }
        ParOnTimer              = settings.ParPollOnTimer;
        ParAfterCombatRound     = settings.ParPollAfterCombatRound;
        ParIncludeWitnessedRounds = settings.ParPollIncludeWitnessedRounds;
        ParOnUnknownRoundDamage = settings.ParPollOnUnknownDamage;
        SyncTimer();
        if (ParTriggerSummary != before) _log?.Info(LogSource, $"par is sent: {ParTriggerSummary}.");
    }

    // The ticked triggers in words, for the program log and the bug report.
    public string ParTriggerSummary
    {
        get
        {
            List<string> on = new(3);
            if (ParOnTimer) on.Add($"every {ParCadence.TotalSeconds:0} s");
            if (ParAfterCombatRound)
                on.Add(ParIncludeWitnessedRounds
                    ? "after each combat round (ours or only witnessed)"
                    : "after each combat round");
            if (ParOnUnknownRoundDamage) on.Add("when a round has unknown damage");
            return on.Count == 0 ? "never (no trigger ticked)" : string.Join(", ", on);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Members.CollectionChanged -= OnMembersChanged;
        _chat.EntryClassified -= OnChatEntry;
        StopParTick();
        StopHealthNagTimer();
    }

    // ----- @health round-trip --------------------------------------------

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Removed members — cancel any pending @health nag + detach
        // the per-row PropertyChanged subscription so an evicted row
        // can be GC'd cleanly.
        if (e.OldItems is not null)
        {
            foreach (object? item in e.OldItems)
            {
                if (item is not PartyMember m) continue;
                m.PropertyChanged -= OnMemberPropertyChanged;
                if (string.IsNullOrEmpty(m.Name)) continue;
                CancelHealthNag(GivenName(m.Name));
            }
        }

        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (e.NewItems is null) return;
        // Subscribe + fire the on-join round-trip in a single helper
        // so the "added as IsInvited, then accepted later" path can
        // reuse the same trigger from the PropertyChanged handler
        // without duplicating wire-send code.
        foreach (object? item in e.NewItems)
        {
            if (item is not PartyMember m) continue;
            m.PropertyChanged += OnMemberPropertyChanged;
            TryFireOnJoinHealth(m);
        }
    }

    // Per-row PropertyChanged handler — fires the on-join @health round-trip
    // when an existing row flips from IsInvited=true to IsInvited=false (the
    // acceptance path: PartyManager's OnYouInvited adds the row, then
    // OnFollowsYou clears the chip). Without this hook the round-trip would only
    // fire on CollectionChanged.Add and an invitee who accepts would never get
    // their baseline captured until the user manually typed `par`.
    private void OnMemberPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PartyMember.IsInvited)) return;
        if (sender is not PartyMember m) return;
        if (m.IsInvited) return;   // we only fire on the invited→accepted transition
        TryFireOnJoinHealth(m);
    }

    // The master switch (true = off). The par poll rides IsParPollEnabled, which
    // already reads it; this covers the @health round-trip and its nags.
    public Func<bool>? MasterSwitchOff { get; set; }

    private void TryFireOnJoinHealth(PartyMember m)
    {
        // Master gate — when the user turns off "send @health nags to party
        // members", skip both the initial round-trip and the retry nag.
        if (!HealthNagEnabled) return;
        // Before the nag is registered, so nothing is left pending for a member
        // who joined while the master switch was off.
        if (MasterSwitchOff?.Invoke() == true) return;
        // Dropped / at the login menu — no telepaths onto the wire until we're
        // back in the realm.
        if (_suspended) return;
        // Don't telepath into the trainer menu — the request would land as
        // stray keystrokes and no baseline reply can arrive while the statline
        // is suppressed anyway.
        if (InTrainerMenu) return;
        if (_wireSender is null) return;
        // Skip self — our own HP/MA flows in through PromptParser on
        // every statline. Skip missing names. Skip pending invitees
        // (the row exists for the PartyWindow chip; no health data
        // yet). Skip rows that already have a baseline captured
        // (re-Add scenarios + acceptance-after-cached-baseline edge).
        if (m.IsSelf) return;
        if (m.IsInvited) return;
        if (string.IsNullOrEmpty(m.Name)) return;
        // MajorMUD telepath syntax is `/<given> <msg>` (slash + given name, no
        // space). Short forms `t` and `tel` are interpreted as `say`;
        // addressing by full "Given Family" is also rejected — given name only.
        string given = GivenName(m.Name);
        SendHealthRequest(given);
        if (m.BaselineHp > 0) return;
        DateTime now = _now();
        _activeNags[given] = new HealthNagState
        {
            Given       = given,
            FirstSentAt = now,
            Sends       = 1,
        };
        EnsureHealthNagTimerRunning();
    }

    private void SendHealthRequest(string given)
    {
        if (_wireSender is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes($"/{given} @health\r");
        _wireSender(bytes);
    }

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    private void OnChatEntry(ChatLogEntry entry)
    {
        if (entry.Channel != ChatChannel.TelepathIncoming) return;
        if (string.IsNullOrEmpty(entry.Speaker)) return;
        if (string.IsNullOrEmpty(entry.Message)) return;
        Match m = HealthReply().Match(entry.Message);
        if (!m.Success) return;
        // Reply shape is `{HP=cur/max[,MA=cur/max]}`. Capture both halves
        // — max becomes the baseline (the cap), cur drives the initial
        // percent so the bar shows real health from the very first frame.
        // Without the percent assignment the row sat at "H:0/max 0%"
        // until the next par poll dribbled in a percentage.
        // MA segment is optional — Warriors and other non-casters reply
        // without it; we store 0 for both mp baseline and percent and the
        // UI hides the MA sub-row via GreaterThanZeroConverter.
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        int hpCur = int.Parse(m.Groups["hp"].Value,    inv);
        int hpMax = int.Parse(m.Groups["hpmax"].Value, inv);
        int mpCur = m.Groups["mp"].Success    ? int.Parse(m.Groups["mp"].Value,    inv) : 0;
        int mpMax = m.Groups["mpmax"].Success ? int.Parse(m.Groups["mpmax"].Value, inv) : 0;
        bool isKai = m.Groups["mpkind"].Value.Equals("KAI", StringComparison.OrdinalIgnoreCase);
        _manager.SetMemberHealthSnapshot(entry.Speaker, hpCur, hpMax, mpCur, mpMax, isKai);
        // Baseline now on file — kill the @health nag for this sender.
        CancelHealthNag(GivenName(entry.Speaker));
    }

    // ----- @health nag escalation ----------------------------------------

    private void EnsureHealthNagTimerRunning()
    {
        _healthNagTick ??= _schedule(HealthNagTick, OnHealthNagTick);
    }

    // The @health nag loop's cadence.
    public static readonly TimeSpan HealthNagTick = TimeSpan.FromMilliseconds(500);

    // One nag tick: the next is armed before this one runs.
    private void OnHealthNagTick()
    {
        _healthNagTick = _schedule(HealthNagTick, OnHealthNagTick);
        TickHealthNags();
    }

    private void StopHealthNagTimer()
    {
        _healthNagTick?.Dispose();
        _healthNagTick = null;
    }

    private void CancelHealthNag(string given)
    {
        if (string.IsNullOrEmpty(given)) return;
        if (!_activeNags.Remove(given)) return;
        if (_activeNags.Count == 0) StopHealthNagTimer();
    }

    private void TickHealthNags()
    {
        if (_activeNags.Count == 0) { StopHealthNagTimer(); return; }
        // Pause resends while in the trainer menu — keep the timer and the
        // pending nag state alive so the retry cadence resumes on menu exit.
        if (InTrainerMenu) return;
        // Held the same way with the master switch off: the nags wait and are
        // resent or given up on the first tick after it is back on.
        if (MasterSwitchOff?.Invoke() == true) return;
        DateTime now = _now();

        foreach (string given in _activeNags.Keys.ToArray())
        {
            if (!_activeNags.TryGetValue(given, out HealthNagState? s)) continue;

            // Hard cap from the first send — give up regardless.
            if (now - s.FirstSentAt >= HealthNagMaxTotal)
            {
                CancelHealthNag(given);
                continue;
            }

            // Baseline arrived between ticks? The OnChatEntry path
            // also cancels, but check defensively in case the member
            // was added with a baseline already set (e.g. a future
            // refresh path).
            if (BaselineKnownFor(given))
            {
                CancelHealthNag(given);
                continue;
            }

            // Decide whether to fire. The first send already went out
            // in OnMembersChanged; LastSentAt is null until the first
            // retry, so the initial-delay window measures from
            // FirstSentAt for the first retry and from LastSentAt for
            // every subsequent one.
            DateTime since = s.LastSentAt ?? s.FirstSentAt;
            TimeSpan wait  = s.LastSentAt is null ? HealthNagInitialDelay : HealthNagFrequency;
            if (now - since < wait) continue;

            SendHealthRequest(given);
            s.LastSentAt = now;
            s.Sends++;
        }
    }

    private bool BaselineKnownFor(string given)
    {
        foreach (PartyMember m in _state.Members)
        {
            if (GivenName(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase))
                return m.BaselineHp > 0;
        }
        // Member no longer in the roster — treat as "known" so the
        // nag cancels rather than continuing to telepath a stranger.
        return true;
    }

    // ----- connection lifecycle ------------------------------------------

    // Any disconnect (user- or server-initiated) suspends both cadences so no
    // `par` / @health telepath leaks onto the wire while we're dropped or sitting
    // at the BBS login menu during re-entry. Pending @health nags are dropped —
    // the roster is wiped on reconnect and re-formed fresh.
    public void NotifyDisconnected()
    {
        _suspended = true;
        StopParTick();
        StopHealthNagTimer();
        _activeNags.Clear();
    }

    // First in-game prompt after a (re)connect — we're back in the realm, so the
    // `par` cadence may resume. Idempotent: only the suspended→live edge restarts
    // the timer, so it's cheap to call on every prompt.
    public void NotifyEnteredRealm()
    {
        if (!_suspended) return;
        _suspended = false;
        SyncTimer();
    }

    // ----- par poll ------------------------------------------------------

    // The timer runs only while its box is ticked and we're in the realm. Start on
    // a running timer is a no-op, so this never restarts a countdown in flight.
    private void SyncTimer()
    {
        if (ParOnTimer && !_suspended) _parTick ??= _schedule(ParCadence, OnParTick);
        else StopParTick();
    }

    private void StartParTick()
    {
        _parTick?.Dispose();
        _parTick = _schedule(ParCadence, OnParTick);
    }

    private void StopParTick()
    {
        _parTick?.Dispose();
        _parTick = null;
    }

    // One par tick: the next is armed before this one runs.
    private void OnParTick()
    {
        _parTick = _schedule(ParCadence, OnParTick);
        OnParTimer();
    }

    private void OnParTimer()
    {
        if (ParOnTimer) SendPar(healthRead: true);
    }

    // A round's ledger closed (RoundDamageTracker.RoundComplete). One `par` per
    // round at most, however many of the round boxes ask for it. A round we only
    // stood by for (a member's fight in the room, or ours with Auto Combat off)
    // isn't ours, so "after each combat round" skips it unless the user ticked
    // witnessed rounds in; unknown damage in it counts either way.
    public void NoteRoundComplete(RoundSummary round)
    {
        string why;
        if (ParAfterCombatRound && (round.Engaged || ParIncludeWitnessedRounds))
            why = round.Engaged
                ? $"combat round {round.FightRound} ended"
                : $"witnessed combat round {round.FightRound} ended";
        else if (ParOnUnknownRoundDamage && round.HasUnknown)
            why = $"round {round.FightRound} had unknown damage (dealt {round.UnknownDealt}, taken {round.UnknownTaken})";
        else
            return;
        if (!SendPar(healthRead: true)) return;
        _log?.Debug(LogSource, $"par sent: {why}.");
        RestartTimerCountdown();
    }

    // One `par` now, for a caller that needs to know who is still in the party
    // rather than how hurt they are. It goes out with the health triggers all
    // unticked and with auto-heal off, and is held back by everything else that
    // holds the poll: no party, a disconnect, the trainer menu, the master switch.
    // False when it wasn't sent.
    public bool RequestPar(string why)
    {
        if (!SendPar(healthRead: false)) return false;
        _log?.Debug(LogSource, $"par sent: {why}.");
        RestartTimerCountdown();
        return true;
    }

    // The timer counts from the latest `par`, so a `par` sent for another reason
    // isn't followed by the timer's own back to back; with rounds coming faster
    // than the cadence it stays quiet.
    private void RestartTimerCountdown()
    {
        if (_parTick is null) return;
        StartParTick();
    }

    private bool SendPar(bool healthRead)
    {
        if (_wireSender is null) return false;
        if (_suspended) return false;
        if (!_state.IsInParty) return false;
        if (InTrainerMenu) return false;
        Func<bool>? gate = healthRead ? IsParPollEnabled : IsAutomationEnabled;
        if (gate is not null && !gate()) return false;
        _wireSender(Encoding.Latin1.GetBytes("par\r"));
        return true;
    }
}
