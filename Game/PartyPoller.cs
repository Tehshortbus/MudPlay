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
///         <see cref="PartyManager.SetMemberBaseline"/>.</item>
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
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    /// <summary>How often to send <c>par</c> on the wire. Default 5 s per the Phase 6 spec.</summary>
    public TimeSpan ParCadence { get; private set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Canonical reply regex for the @health round-trip. Matches the
    /// shape <see cref="Remote.PartyEssentialHandlers.OnHealth"/>
    /// produces. Mana group is optional — Warrior-class members reply
    /// without an MA segment.
    /// </summary>
    [GeneratedRegex(
        @"^HP\s+(?<hp>\d+)/(?<hpmax>\d+)(?:,\s+(?:MA|KAI)\s+(?<mp>\d+)/(?<mpmax>\d+))?",
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
    }

    // ----- @health round-trip --------------------------------------------

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_wireSender is null) return;
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (e.NewItems is null) return;
        foreach (object? item in e.NewItems)
        {
            if (item is not PartyMember m) continue;
            // Skip self — our own HP/MA flows in through PromptParser
            // on every statline, which is fresher than this round-trip
            // would be. Skip if name is missing too.
            if (m.IsSelf) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            // MajorMUD telepath syntax on Playpen BBS is `/<given> <msg>`
            // (slash + given name, no space). Short forms `t` and `tel`
            // are interpreted as `say`; addressing by full "Given Family"
            // is also rejected — given name only.
            string given = GivenName(m.Name);
            byte[] bytes = Encoding.Latin1.GetBytes($"/{given} @health\r");
            _wireSender(bytes);
        }
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
        // hpmax is the absolute baseline (the cap). For mana, mpmax is
        // optional — Warriors and other non-casters reply without an MA
        // segment, in which case we store 0 for the baseline (UI shows
        // "—" rather than a percent).
        int hpBaseline = int.Parse(m.Groups["hpmax"].Value, System.Globalization.CultureInfo.InvariantCulture);
        int mpBaseline = m.Groups["mpmax"].Success
            ? int.Parse(m.Groups["mpmax"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        _manager.SetMemberBaseline(entry.Speaker, hpBaseline, mpBaseline);
    }

    // ----- par poll ------------------------------------------------------

    private void DoParPoll()
    {
        if (_wireSender is null) return;
        if (!_state.IsInParty) return;
        _wireSender(Encoding.Latin1.GetBytes("par\r"));
    }
}
