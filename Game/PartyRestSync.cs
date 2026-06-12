using System.Text;

namespace FujinTerm.Game;

/// <summary>
/// Emit side of the Phase 6 PR 6.7 <c>@wait</c> / <c>@ok</c> protocol.
/// Engines call <see cref="RequestWait"/> / <see cref="RequestOk"/>
/// when their own logic decides the party leader should pause / resume —
/// e.g. the Phase 12 HealthManager auto-rest path, message-engine
/// flag triggers (a paralyzed / held / confused ailment fires a
/// "send @wait if following" message-flag handler), etc.
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
/// Per user direction (live-test feedback): we deliberately do NOT
/// hook <see cref="PlayerState.Position"/> changes. A user typing a
/// manual <c>rest</c> shouldn't trigger an automatic @wait — only
/// engine decisions should. Engines call into this service when
/// their own conditions fire.
/// </para>
/// </remarks>
public sealed class PartyRestSync : IDisposable
{
    private readonly PartyState _party;
    private readonly HashSet<WaitReason> _waitReasons = new();
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public PartyRestSync(PartyState party)
    {
        ArgumentNullException.ThrowIfNull(party);
        _party  = party;
    }

    /// <summary>
    /// Bind the wire-sender. Without it, <see cref="RequestWait"/> /
    /// <see cref="RequestOk"/> calls are silent no-ops (no telepath).
    /// MainWindowViewModel supplies <c>SendUserInput</c> alongside the
    /// other Phase 6 hookups.
    /// </summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Engine-callable entry point — register a wait <paramref name="reason"/>
    /// and telepath <c>@wait</c> to the party leader on the 0→non-empty
    /// transition. If another reason already holds the wait, this only
    /// records the new reason and sends nothing (the leader is already
    /// paused). No-ops on the wire when solo, when we're the leader, when
    /// there's no leader yet, or when no wire-sender is bound — but the
    /// reason is still tracked so a later <see cref="RequestOk"/> balances.
    /// Idempotent at the protocol level — the receiving leader's
    /// <see cref="Remote.PartyEssentialHandlers.OnWait"/> dedupes via
    /// a HashSet so repeat sends don't double-count.
    /// </summary>
    public void RequestWait(WaitReason reason)
    {
        bool wasEmpty = _waitReasons.Count == 0;
        if (!_waitReasons.Add(reason)) return;
        if (!wasEmpty) return;
        if (!CanSignal()) return;
        Telepath(_party.LeaderName!, "@wait");
    }

    /// <summary>
    /// Engine-callable entry point — clear a wait <paramref name="reason"/>
    /// and telepath <c>@ok</c> to the party leader only on the
    /// non-empty→0 transition (the LAST reason clearing). While other
    /// reasons still hold the wait, this records the release and sends
    /// nothing. Same wire gates as <see cref="RequestWait"/>.
    /// </summary>
    public void RequestOk(WaitReason reason)
    {
        if (!_waitReasons.Remove(reason)) return;
        if (_waitReasons.Count > 0) return;
        if (!CanSignal()) return;
        Telepath(_party.LeaderName!, "@ok");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    private bool CanSignal()
    {
        if (!_party.IsInParty) return false;
        if (_party.SelfIsLeader) return false;
        if (string.IsNullOrEmpty(_party.LeaderName)) return false;
        return true;
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
