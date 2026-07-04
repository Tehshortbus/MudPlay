using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Map;

// Pitches in on a door the party leader is forcing. When we observe "You
// see <name> attempt to bash the door to the <dir>." and the actor is our
// current PartyState.LeaderName, send the same door verb at the same
// direction so two characters work the lock together. Verb is pick <dir>
// or bash <dir> per OtherSettings.PicklocksOverBash.
//
// Gated on PartySettings.HelpLeaderOpenDoors (off by default) and an
// active party. The leader match implies we're a follower — the leader is
// someone other than us — so no explicit self-is-leader check is needed:
// if we were the leader, the observed actor would be a follower whose name
// wouldn't match LeaderName.
public sealed class LeaderDoorAssistManager : IDisposable
{
    private readonly MessageRouter _router;
    private readonly PartyState _party;
    private readonly Func<PartySettings> _readPartySettings;
    private readonly Func<OtherSettings> _readOtherSettings;
    private readonly LogService? _log;
    private readonly IDisposable _sub;
    private readonly WireSender _wire = new();
    private bool _disposed;

    public LeaderDoorAssistManager(
        MessageRouter router,
        PartyState party,
        Func<PartySettings> readPartySettings,
        Func<OtherSettings> readOtherSettings,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(readPartySettings);
        ArgumentNullException.ThrowIfNull(readOtherSettings);
        _router = router;
        _party = party;
        _readPartySettings = readPartySettings;
        _readOtherSettings = readOtherSettings;
        _log = log;

        _sub = _router.Subscribe(KnownPatterns.PlayerDoorBashAttempt, OnLeaderBashAttempt);
    }

    // Bind the gate-wrapped wire sender (MainWindowVM's SendUserInput).
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — bytes the manager asked to write to the wire.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sub.Dispose();
    }

    private void OnLeaderBashAttempt(MatchResult m)
    {
        if (!_party.IsInParty) return;
        if (!_readPartySettings().HelpLeaderOpenDoors) return;

        // RegexPattern returns numbered groups 1..N as a 0-based list; the
        // PlayerDoorBashAttempt pattern's only captures are name then dir.
        if (m.Groups.Count < 2) return;
        string actor = m.Groups[0];
        string dirWord = m.Groups[1];
        if (actor.Length == 0 || dirWord.Length == 0) return;

        string leader = _party.LeaderName ?? string.Empty;
        if (leader.Length == 0) return;
        if (!GivenName(actor).Equals(GivenName(leader), StringComparison.OrdinalIgnoreCase))
            return;

        string dirShort = DirectionShort(dirWord);
        string verb = _readOtherSettings().PicklocksOverBash ? "pick" : "bash";
        _wire.Send($"{verb} {dirShort}");
        _log?.Info("Party", $"Helping leader {GivenName(leader)} {verb} {dirShort}.");
    }

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    // Full direction word → MajorMUD short movement token.
    private static string DirectionShort(string word) => word switch
    {
        "north"     => "n",
        "south"     => "s",
        "east"      => "e",
        "west"      => "w",
        "northeast" => "ne",
        "northwest" => "nw",
        "southeast" => "se",
        "southwest" => "sw",
        "up"        => "u",
        "down"      => "d",
        _           => word,
    };
}
