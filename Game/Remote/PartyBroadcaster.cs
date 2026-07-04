using System.Text;

namespace FujinTerm.Game.Remote;

// One-to-many @-command sender. Telepaths an @-command to every non-self
// PartyMember in PartyState.Members. Provides the BroadcastExpReset sugar method
// called when a loop or Auto-Lair starts.
//
// Self is skipped — there's no point telepathing ourselves. Members without a
// known name are skipped. The broadcaster is silent (no-op) when not in a party,
// when no wire-sender is bound, or when AutoExpResetEnabled is false for the
// BroadcastExpReset entry-point specifically. Generic Broadcast doesn't consult
// the toggle — callers can decide their own gating.
//
// Wire format per recipient: t <name> @Reset — the same telepath shape the engine
// uses for replies.
public sealed class PartyBroadcaster
{
    private readonly PartyState _party;
    private Action<byte[]>? _wireSender;

    // User-facing toggle for the Auto-Exp-Reset behaviour. Default true; Settings.
    // Party flips it.
    public bool AutoExpResetEnabled { get; set; } = true;

    public PartyBroadcaster(PartyState party)
    {
        ArgumentNullException.ThrowIfNull(party);
        _party = party;
    }

    // Bind the wire-sender. MainWindowVM supplies SendUserInput.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Broadcast @Reset to every non-self party member. Called by the LoopManager /
    // AutoLairScheduler on start to reset expected-XP rounds counters across the
    // party. Silent if the AutoExpResetEnabled toggle is off, if not in a party,
    // or if no wire-sender is bound.
    public void BroadcastExpReset()
    {
        if (!AutoExpResetEnabled) return;
        Broadcast("@Reset");
    }

    // Generic broadcast. Telepaths atCommand to every non-self party member.
    // Caller is responsible for any feature-flag gating before invoking —
    // BroadcastExpReset is the only entry-point with built-in gating.
    public void Broadcast(string atCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(atCommand);
        if (_wireSender is null) return;
        if (!_party.IsInParty) return;
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            // Playpen BBS telepath syntax: `/<given> <atCommand>`
            // (slash + given name). The verbose `t` / `tel` / `tell`
            // forms are all interpreted as `say`; "Given Family"
            // recipients are rejected — given name only.
            string given = GivenName(m.Name);
            byte[] bytes = Encoding.Latin1.GetBytes($"/{given} {atCommand}\r");
            _wireSender(bytes);
        }
    }

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
