using System.ComponentModel;
using System.Text;

namespace FujinTerm.Game;

/// <summary>
/// Emit side of the Phase 6 PR 6.7 <c>@wait</c> / <c>@ok</c> protocol.
/// When the local character enters a non-Standing position (Resting or
/// Meditating) while following a leader, we telepath <c>@wait</c> so the
/// leader's auto-walker / auto-combat can pause until we're ready.
/// Coming back to Standing emits <c>@ok</c> so the leader can resume.
/// </summary>
/// <remarks>
/// <para>
/// Receive side ships in PR 6.3 (<see cref="Remote.PartyEssentialHandlers"/>
/// OnWait / OnOk handlers populate the <c>WaitingMembers</c> set the
/// pause-gate consumer reads). The two halves talk past each other —
/// leaders never emit (they have no one to wait for), followers never
/// receive (only leaders care).
/// </para>
/// <para>
/// We don't send <c>@wait</c> when solo or when we're the party leader.
/// Leader has no one to ping; solo means there's no party context at all.
/// </para>
/// <para>
/// Meditating is treated as a rest state for the protocol — same as
/// Resting from the leader's "I need to wait" perspective. The
/// receiving leader's <see cref="Remote.PartyEssentialHandlers.OnWait"/>
/// adds to a HashSet so duplicate <c>@wait</c> emissions are idempotent.
/// </para>
/// </remarks>
public sealed class PartyRestSync : IDisposable
{
    private readonly PlayerState _player;
    private readonly PartyState _party;
    private Action<byte[]>? _wireSender;
    private PlayerPosition _lastObservedPosition;
    private bool _disposed;

    public PartyRestSync(PlayerState player, PartyState party)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(party);
        _player = player;
        _party  = party;
        _lastObservedPosition = _player.Position;
        _player.PropertyChanged += OnPlayerChanged;
    }

    /// <summary>
    /// Bind the wire-sender. Without it, position transitions are still
    /// observed but no telepath fires. MainWindowViewModel supplies
    /// <c>SendUserInput</c> alongside the other Phase 6 hookups.
    /// </summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>Test seam — force a position-transition emission without going through PlayerState mutation.</summary>
    internal void HandlePositionChangeForTests(PlayerPosition newPosition)
    {
        HandlePositionChange(newPosition);
        _lastObservedPosition = newPosition;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.PropertyChanged -= OnPlayerChanged;
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerState.Position)) return;
        if (_player.Position == _lastObservedPosition) return;
        HandlePositionChange(_player.Position);
        _lastObservedPosition = _player.Position;
    }

    private void HandlePositionChange(PlayerPosition newPos)
    {
        // No-op outside a party — there's no one to telepath.
        if (!_party.IsInParty) return;
        // Leaders don't @wait themselves — they're the recipient of
        // other members' @waits.
        if (_party.SelfIsLeader) return;
        // No leader identified yet → no recipient.
        if (string.IsNullOrEmpty(_party.LeaderName)) return;

        bool wasResting = _lastObservedPosition != PlayerPosition.Standing;
        bool nowResting = newPos != PlayerPosition.Standing;
        if (wasResting == nowResting) return;

        string verb = nowResting ? "@wait" : "@ok";
        Telepath(_party.LeaderName, verb);
    }

    private void Telepath(string recipient, string body)
    {
        if (_wireSender is null) return;
        // Playpen BBS telepath syntax: `/<given> <body>` (slash + given
        // name, no space). `t` / `tel` / `tell` are all interpreted as
        // `say`; full "Given Family" recipients are rejected.
        string given = GivenName(recipient);
        byte[] bytes = Encoding.Latin1.GetBytes($"/{given} {body}\r");
        _wireSender(bytes);
    }

    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
