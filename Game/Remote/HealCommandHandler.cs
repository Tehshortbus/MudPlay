using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;

namespace FujinTerm.Game.Remote;

// Receive side of @heal — a party member's request "cast a heal on me". A
// configured party-healer responds by polling party HP now (par), which
// refreshes every PartyMember.HpPercent so CastingDirector re-evaluates its
// party-heal thresholds on the next combat tick and casts toward whoever is
// below — including the requester. @heal only accelerates the poll PartyPoller
// already runs on a cadence; the actual cast decision stays owned by
// CastingDirector + the character's PartySettings heal-spell config.
//
// A member with no party-heal spell configured has nothing to cast, so it stays
// silent rather than answering every broadcast request with a "can't heal" line
// — in a party only the healer(s) should respond. The engine gates authorisation
// via RemoteCommandCatalog (@heal = ExecuteCommands) before the handler runs.
// Wire replies ride the Latin1/CP437 BBS wire, so the reply is ASCII-only.
//
// The flee-integration emit side lives in HealthManager /
// PartyRestSync.RequestHeal: a low-HP party follower broadcasts gang @heal
// instead of running off alone, so this handler is what turns that broadcast
// into a heal.
public sealed class HealCommandHandler : IDisposable
{
    private const string Command = "@heal";

    private readonly RemoteCommandManager _engine;
    private readonly Func<PartySettings> _readParty;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // Live gate — true while a trainer / creation screen owns the keyboard. The
    // @heal poll rides the same wall-clock leak risk as PartyPoller's timed par:
    // fired while the user is parked on the `train stats` form (which the healer
    // can't cast from anyway), the `par\r` lands in the form's Family Name field
    // and overwrites the character's last name. When the screen owns the keyboard
    // the handler stays fully silent — no par, no ack — matching the non-healer
    // path, since a menu-bound healer can't help the requester right now.
    // Null = ungated (test / pre-wire default).
    public Func<bool>? IsInTrainerMenu { get; set; }

    public HealCommandHandler(RemoteCommandManager engine, Func<PartySettings> readParty)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(readParty);
        _engine = engine;
        _readParty = readParty;

        if (!RemoteCommandCatalog.TryGetCategory(Command, out PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{Command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(Command, category, OnHeal);
    }

    // Bind the wire-sender so the handler can poll par. Without it the command
    // still authorises + replies, but no poll reaches the game.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler(Command);
    }

    private void OnHeal(RemoteCommandContext ctx)
    {
        // Only a configured healer answers. No party-heal spell set → nothing
        // to cast → stay silent, so a broadcast @heal doesn't draw a
        // "can't heal" reply from every non-healer in the party.
        if (!HasPartyHeal(_readParty())) return;

        // Parked on the trainer screen → the par would land in the stat form and
        // corrupt the last name, and we can't cast from a menu anyway. Stay
        // silent, exactly as a non-healer does.
        if (IsInTrainerMenu is { } gate && gate()) return;

        // par now → CastingDirector sees fresh member HP% on its next combat
        // tick and heals whoever is below threshold. Same command PartyPoller
        // sends on cadence; @heal just brings the poll forward.
        Send("par");
        ctx.Reply("healing");
    }

    private static bool HasPartyHeal(PartySettings p) =>
        !string.IsNullOrWhiteSpace(p.MinorPartyHealSpell)
        || !string.IsNullOrWhiteSpace(p.MinorPartyHealAoeSpell)
        || !string.IsNullOrWhiteSpace(p.MajorPartyHealSpell)
        || !string.IsNullOrWhiteSpace(p.MajorPartyHealAoeSpell);

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }
}
