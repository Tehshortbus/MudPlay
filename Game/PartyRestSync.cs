using System.Text;

namespace FujinTerm.Game;

// Emit side of the @wait / @ok protocol. Engines call RequestWait / RequestOk
// when their own logic decides the party leader should pause / resume — e.g.
// the HealthManager auto-rest path, message-engine flag triggers (a paralyzed /
// held / confused ailment fires a "send @wait if following" message-flag
// handler), etc.
//
// Receive side lives in PartyEssentialHandlers (OnWait / OnOk handlers populate
// the WaitingMembers set the pause-gate consumer reads). The two halves talk
// past each other — leaders never emit (they have no one to wait for),
// followers never receive (only leaders care).
//
// We don't send @wait when solo or when we're the party leader. Leader has no
// one to ping; solo means there's no party context at all.
//
// We deliberately do NOT hook PlayerState.Position changes. A user typing a
// manual `rest` shouldn't trigger an automatic @wait — only engine decisions
// should. Engines call into this service when their own conditions fire.
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

    // Bind the wire-sender. Without it, RequestWait / RequestOk calls are silent
    // no-ops (no telepath). MainWindowViewModel supplies SendUserInput alongside
    // the other party hookups.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Engine-callable entry point — register a wait reason and telepath @wait to
    // the party leader on the 0→non-empty transition. If another reason already
    // holds the wait, this only records the new reason and sends nothing (the
    // leader is already paused). No-ops on the wire when solo, when we're the
    // leader, when there's no leader yet, or when no wire-sender is bound — but
    // the reason is still tracked so a later RequestOk balances. Idempotent at
    // the protocol level — the receiving leader's PartyEssentialHandlers.OnWait
    // dedupes via a HashSet so repeat sends don't double-count.
    //
    // announce false registers the reason silently — no @wait telepath ever
    // fires for it. Used by the held ailment, whose pause is driven by the
    // inbound .@held say on the leader's side; the reason still participates in
    // the balanced @ok-on-last-clear so a held-and-poisoned player doesn't
    // release the wait while still poisoned.
    public void RequestWait(WaitReason reason, bool announce = true)
    {
        bool wasEmpty = _waitReasons.Count == 0;
        if (!_waitReasons.Add(reason)) return;
        if (!announce) return;
        if (!wasEmpty) return;
        if (!CanSignal()) return;
        Telepath(_party.LeaderName!, "@wait");
    }

    // Engine-callable entry point — clear a wait reason and telepath @ok to the
    // party leader only on the non-empty→0 transition (the LAST reason
    // clearing). While other reasons still hold the wait, this records the
    // release and sends nothing. Same wire gates as RequestWait.
    public void RequestOk(WaitReason reason)
    {
        if (!_waitReasons.Remove(reason)) return;
        if (_waitReasons.Count > 0) return;
        if (!CanSignal()) return;
        Telepath(_party.LeaderName!, "@ok");
    }

    // Broadcast @heal to the whole party (gangpath) — the flee-side signal a
    // low-HP follower emits instead of running. A follower that ran off alone
    // would break party formation and strand itself, so it asks the party
    // healer(s) to top it up and stays put; the leader owns the party's run
    // decision. Broadcasts rather than telepathing the leader (as RequestWait /
    // RequestOk do) because the healer may be any party member, not just the
    // leader. No-ops solo, as leader, or without a wire-sender.
    public void RequestHeal()
    {
        if (!_party.IsInParty) return;
        if (_party.SelfIsLeader) return;
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes("gang @heal\r"));
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
        // MajorMUD telepath syntax: `/<given> <body>` (slash + given name, no
        // space). `t` / `tel` / `tell` are all interpreted as `say`; full
        // "Given Family" recipients are rejected.
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
