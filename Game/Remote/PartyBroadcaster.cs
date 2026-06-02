using System.Text;

namespace FujinTerm.Game.Remote;

/// <summary>
/// One-to-many @-command sender. Telepaths an @-command to every
/// non-self <see cref="PartyMember"/> in <see cref="PartyState.Members"/>.
/// PR 6.8 ships the broadcaster + the <see cref="BroadcastExpReset"/>
/// sugar method called when a loop or Auto-Lair starts (Phase 7 wires
/// the trigger). Phase 12's panic / kill broadcasts will land here too.
/// </summary>
/// <remarks>
/// <para>
/// Self is skipped — there's no point telepathing ourselves. Members
/// without a known name are skipped. The broadcaster is silent (no-op)
/// when not in a party, when no wire-sender is bound, or when
/// <see cref="AutoExpResetEnabled"/> is false for the
/// <see cref="BroadcastExpReset"/> entry-point specifically. Generic
/// <see cref="Broadcast"/> doesn't consult the toggle — callers can
/// decide their own gating.
/// </para>
/// <para>
/// Wire format per recipient: <c>t &lt;name&gt; @Reset</c> — the same
/// telepath shape the engine uses for replies. Receive-side handler
/// for <c>@Reset</c> isn't registered yet (the auto-rounds-counter it
/// would reset doesn't exist before Phase 8 SessionStats / Phase 12
/// automation engines); future PRs plug it in via the engine's
/// registry without touching this class.
/// </para>
/// </remarks>
public sealed class PartyBroadcaster
{
    private readonly PartyState _party;
    private Action<byte[]>? _wireSender;

    /// <summary>
    /// User-facing toggle for the Auto-Exp-Reset behaviour. Default true
    /// per the Phase 6 spec; PR 6.9 wires Settings.Party to flip it.
    /// </summary>
    public bool AutoExpResetEnabled { get; set; } = true;

    public PartyBroadcaster(PartyState party)
    {
        ArgumentNullException.ThrowIfNull(party);
        _party = party;
    }

    /// <summary>Bind the wire-sender. MainWindowVM supplies <c>SendUserInput</c>.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Broadcast <c>@Reset</c> to every non-self party member. Called by
    /// Phase 7's LoopManager / AutoLairScheduler on start to reset
    /// expected-XP rounds counters across the party. Silent if the
    /// <see cref="AutoExpResetEnabled"/> toggle is off, if not in a
    /// party, or if no wire-sender is bound.
    /// </summary>
    public void BroadcastExpReset()
    {
        if (!AutoExpResetEnabled) return;
        Broadcast("@Reset");
    }

    /// <summary>
    /// Generic broadcast. Telepaths <paramref name="atCommand"/> to every
    /// non-self party member. Caller is responsible for any feature-flag
    /// gating before invoking — <see cref="BroadcastExpReset"/> is the
    /// only entry-point with built-in gating.
    /// </summary>
    public void Broadcast(string atCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(atCommand);
        if (_wireSender is null) return;
        if (!_party.IsInParty) return;
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            byte[] bytes = Encoding.Latin1.GetBytes($"t {m.Name} {atCommand}\r");
            _wireSender(bytes);
        }
    }
}
