using System.Collections.Specialized;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;

namespace FujinTerm.Game;

/// <summary>
/// Drives the two Phase 6 PR 6.4 cadences:
/// </summary>
/// <remarks>
/// <list type="number">
///   <item><b>On-join <c>@health</c> exchange.</b> When a fresh member
///         lands in <see cref="PartyState.Members"/> (via
///         <see cref="PartyManager"/>'s follows-you / par parsing), the
///         poller telepaths <c>@health</c> to that member, parses the
///         reply, and writes the absolute HP/MA into the matching
///         <see cref="PartyMember"/>'s <see cref="PartyMember.BaselineHp"/>
///         / <see cref="PartyMember.BaselineMp"/> through
///         <see cref="PartyManager.SetMemberHealthSnapshot"/>.</item>
///   <item><b><c>par</c> poll.</b> A <see cref="DispatcherTimer"/>
///         ticks at <see cref="ParCadence"/> (5 s default per the
///         Phase 6 spec; Settings.Party in PR 6.9 makes this
///         user-configurable). Each tick sends <c>par</c> on the wire,
///         which the server responds to with the multi-line table
///         <see cref="PartyManager"/> already parses to update HP%/MA%/
///         position for every member.</item>
/// </list>
/// <para>
/// Reply-format match: the on-join replies come back as
/// <c>"X telepaths: HP 690/720, MA 200/300 (Resting)"</c> — i.e. the
/// other party member's <see cref="Remote.PartyEssentialHandlers.OnHealth"/>
/// reply routed through their telepath. The poller subscribes to
/// <see cref="ChatRouter.EntryClassified"/> and watches incoming
/// telepaths whose body matches the canonical health-reply regex; the
/// speaker field tells us which member to update.
/// </para>
/// <para>
/// Self handling: <see cref="PartyMember.IsSelf"/> rows are skipped on
/// the @health round-trip — our own HP/MA flows in through
/// <see cref="PromptParser"/> on every statline observation, which is
/// fresher than a self-sent telepath would be.
/// </para>
/// <para>
/// Lifetime: poller is app-singleton like <see cref="PartyManager"/>;
/// it's safe to keep the timer running even when not in a party —
/// <see cref="DoParPoll"/> short-circuits on
/// <see cref="PartyState.IsInParty"/> = false so we don't spam <c>par</c>
/// at the wire while solo.
/// </para>
/// </remarks>
public sealed partial class PartyPoller : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly PartyState _state;
    private readonly PartyManager _manager;
    private readonly DispatcherTimer? _timer;
    private DispatcherTimer? _healthNagTimer;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    /// <summary>How often to send <c>par</c> on the wire. Default 5 s per the Phase 6 spec.</summary>
    public TimeSpan ParCadence { get; private set; } = TimeSpan.FromSeconds(5);

    // ----- @health nag escalation knobs (shared with @join nag) ----------
    // Pushed in by AppServices.ApplyPartyFromActiveProfile from the same
    // Settings.Party JoinNag* fields AutoPartyManager reads. The label in
    // the UI is "@join/@health nag settings" — both nag flows share the
    // cadence because they share the user intent ("after asking for X,
    // wait this long, retry this often, give up after this window").

    /// <summary>Wait this long after the initial <c>/given @health</c> before the first retry.</summary>
    public TimeSpan HealthNagInitialDelay { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Cadence for subsequent <c>/given @health</c> resends.</summary>
    public TimeSpan HealthNagFrequency { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Hard cap on the total nag window measured from the initial send.</summary>
    public TimeSpan HealthNagMaxTotal { get; set; } = TimeSpan.FromSeconds(55);

    /// <summary>
    /// Master enable for the on-join <c>@health</c> round-trip + retry nag.
    /// When false, a newly joined member is never telepathed
    /// <c>/given @health</c> and no nag is armed — read at fire time, so
    /// toggling it off prevents new round-trips (matching
    /// <see cref="AutoPartyManager.JoinNagEnabled"/>'s start-time gate).
    /// Mirrors <see cref="Models.Profile.PartySettings.SendHealthToMembers"/>.
    /// </summary>
    public bool HealthNagEnabled { get; set; } = true;

    /// <summary>Test seam — overrides <see cref="DateTime.UtcNow"/> for the nag tick clock.</summary>
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    /// <summary>Per-given-name nag state. Created on the initial @health send; cleared on baseline arrival / member-leave / cap.</summary>
    private readonly Dictionary<string, HealthNagState> _activeNags =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class HealthNagState
    {
        public string Given { get; set; } = string.Empty;
        public DateTime FirstSentAt { get; set; }
        public DateTime? LastSentAt { get; set; }
        public int Sends { get; set; }
    }

    /// <summary>
    /// Canonical reply regex for the @health round-trip. Matches the
    /// shape <see cref="Remote.PartyEssentialHandlers.OnHealth"/>
    /// produces — <c>{HP=cur/max,MA=cur/max[, Resting|Meditating]}</c>.
    /// The leading <c>{</c> + trailing <c>}</c> are the brace-wrap the
    /// engine adds at SendReply time per the remote-command meta-line
    /// convention. Mana group is optional (warriors reply HP-only);
    /// position suffix is optional and ignored — we only need the
    /// HP/MA numbers for baseline capture.
    /// </summary>
    [GeneratedRegex(
        // `cur` halves accept an optional leading `-` because MajorMUD
        // lets HP go from 0 down to a BBS-set death threshold (the
        // "dropped" state — alive but immobile, bleeding out unless
        // someone `aid`s them). Without the optional minus the regex
        // silently fails to match a dropped member's @health reply,
        // BaselineHp stays 0, and PartyPoller's nag retries until
        // max-total instead of cancelling on first valid reply.
        // Max halves stay `\d+` — max HP / MA are always positive.
        @"^\{?HP=(?<hp>-?\d+)/(?<hpmax>\d+)(?:,(?:MA|KAI)=(?<mp>-?\d+)/(?<mpmax>\d+))?(?:,\s*\w+)?\}?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HealthReply();

    /// <summary>
    /// Default constructor — wires the in-process <see cref="DispatcherTimer"/>.
    /// Use the test-seam ctor (no timer) for unit tests.
    /// </summary>
    public PartyPoller(ChatRouter chat, PartyState state, PartyManager manager)
        : this(chat, state, manager, useTimer: true) { }

    internal PartyPoller(ChatRouter chat, PartyState state, PartyManager manager, bool useTimer)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(manager);
        _chat    = chat;
        _state   = state;
        _manager = manager;

        _state.Members.CollectionChanged += OnMembersChanged;
        _chat.EntryClassified += OnChatEntry;

        if (useTimer)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = ParCadence,
            };
            _timer.Tick += (_, _) => DoParPoll();
            _timer.Start();
        }
    }

    /// <summary>
    /// Bind the wire-sender. Same shape as other managers — main-window
    /// VM supplies <c>SendUserInput</c>. Without it the poller still
    /// observes party events but can't send anything.
    /// </summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Update the <c>par</c> poll cadence. PR 6.9's Settings.Party tab
    /// calls this when the user edits the par-frequency field.
    /// </summary>
    public void SetParCadence(TimeSpan cadence)
    {
        if (cadence <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cadence), "par cadence must be positive.");
        ParCadence = cadence;
        if (_timer is not null) _timer.Interval = cadence;
    }

    /// <summary>Test seam — drives one par poll without a real timer tick.</summary>
    internal void DoParPollForTests() => DoParPoll();

    /// <summary>Test seam — drives the @health round-trip request side without a CollectionChanged event.</summary>
    internal void SendHealthRequestForTests(string memberName)
    {
        if (_wireSender is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes($"/{GivenName(memberName)} @health\r");
        _wireSender(bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Members.CollectionChanged -= OnMembersChanged;
        _chat.EntryClassified -= OnChatEntry;
        _timer?.Stop();
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

    /// <summary>
    /// Per-row PropertyChanged handler — fires the on-join @health
    /// round-trip when an existing row flips from IsInvited=true to
    /// IsInvited=false (the acceptance path: PartyManager's
    /// OnYouInvited adds the row, then OnFollowsYou clears the chip).
    /// Without this hook the round-trip would only fire on
    /// CollectionChanged.Add and an invitee who accepts would never
    /// get their baseline captured until the user manually typed
    /// <c>par</c>.
    /// </summary>
    private void OnMemberPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PartyMember.IsInvited)) return;
        if (sender is not PartyMember m) return;
        if (m.IsInvited) return;   // we only fire on the invited→accepted transition
        TryFireOnJoinHealth(m);
    }

    private void TryFireOnJoinHealth(PartyMember m)
    {
        // Master gate — when the user turns off "send @health nags to party
        // members", skip both the initial round-trip and the retry nag.
        if (!HealthNagEnabled) return;
        if (_wireSender is null) return;
        // Skip self — our own HP/MA flows in through PromptParser on
        // every statline. Skip missing names. Skip pending invitees
        // (the row exists for the PartyWindow chip; no health data
        // yet). Skip rows that already have a baseline captured
        // (re-Add scenarios + acceptance-after-cached-baseline edge).
        if (m.IsSelf) return;
        if (m.IsInvited) return;
        if (string.IsNullOrEmpty(m.Name)) return;
        // MajorMUD telepath syntax on Playpen BBS is `/<given> <msg>`
        // (slash + given name, no space). Short forms `t` and `tel`
        // are interpreted as `say`; addressing by full "Given Family"
        // is also rejected — given name only.
        string given = GivenName(m.Name);
        SendHealthRequest(given);
        if (m.BaselineHp > 0) return;
        DateTime now = NowProvider();
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
        _manager.SetMemberHealthSnapshot(entry.Speaker, hpCur, hpMax, mpCur, mpMax);
        // Baseline now on file — kill the @health nag for this sender.
        CancelHealthNag(GivenName(entry.Speaker));
    }

    // ----- @health nag escalation ----------------------------------------

    private void EnsureHealthNagTimerRunning()
    {
        if (_healthNagTimer is not null) return;
        _healthNagTimer = new DispatcherTimer(
            interval: TimeSpan.FromMilliseconds(500),
            priority: DispatcherPriority.Background,
            callback: (_, _) => TickHealthNags());
        _healthNagTimer.Start();
    }

    private void StopHealthNagTimer()
    {
        _healthNagTimer?.Stop();
        _healthNagTimer = null;
    }

    private void CancelHealthNag(string given)
    {
        if (string.IsNullOrEmpty(given)) return;
        if (!_activeNags.Remove(given)) return;
        if (_activeNags.Count == 0) StopHealthNagTimer();
    }

    /// <summary>Test seam — runs one pass of the @health nag loop without a real timer.</summary>
    internal void TickHealthNagsForTests() => TickHealthNags();

    private void TickHealthNags()
    {
        if (_activeNags.Count == 0) { StopHealthNagTimer(); return; }
        DateTime now = NowProvider();

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

    // ----- par poll ------------------------------------------------------

    private void DoParPoll()
    {
        if (_wireSender is null) return;
        if (!_state.IsInParty) return;
        _wireSender(Encoding.Latin1.GetBytes("par\r"));
    }
}
