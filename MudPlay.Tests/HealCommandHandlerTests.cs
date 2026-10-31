using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins the receive side of HealCommandHandler: an inbound
// @heal makes a configured party-healer queue a heal on a party member, poll par (so
// CastingDirector re-evaluates against fresh party HP) and acknowledge; a player
// outside the party heard on gangpath or broadcast gets a queued heal unless the
// healer turned that off; a member with no party-heal spell configured stays silent,
// and an unauthorised sender is denied before the handler runs.
public sealed class HealCommandHandlerTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public required RemoteCommandManager Engine { get; init; }
        public required PlayerDatabase Players { get; init; }
        public required PartySettings Party { get; init; }
        public required List<byte[]> WireSent { get; init; }
        public required HealCommandHandler Handler { get; init; }
        // The heals the handler queued (sender, party member), and who is in our party.
        public required List<(string Sender, bool Member)> Queued { get; init; }
        public required HashSet<string> Members { get; init; }
    }

    private Harness Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        PartySettings settings = new();
        List<byte[]> wire = new();
        List<(string, bool)> queued = new();
        HashSet<string> members = new(StringComparer.OrdinalIgnoreCase);
        HealCommandHandler handler = new(engine, () => settings,
            queueHeal: (sender, member) => queued.Add((sender, member)), isPartyMember: members.Contains);
        handler.SetWireSender(wire.Add);
        return new Harness
        {
            Engine = engine,
            Players = players,
            Party = settings,
            WireSent = wire,
            Handler = handler,
            Queued = queued,
            Members = members,
        };
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private List<string> Replies() =>
        _replies
            .Select(b => Encoding.Latin1.GetString(b))
            .Select(StripWire)
            .ToList();

    private static List<string> Wire(Harness h) =>
        h.WireSent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

    private static string StripWire(string wire)
    {
        string s = wire.TrimEnd('\r');
        int open = s.IndexOf('{');
        int close = s.LastIndexOf('}');
        return open >= 0 && close > open ? s[(open + 1)..close] : s;
    }

    [Fact]
    public void Heal_WithHealerConfigured_PollsParAndAcknowledges()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Party.MinorPartyHealSpell = "mheal"; // this character heals the party

        ChatLines.Hear(_routers[h.Engine], Telepath("Bob", "@heal"));

        Assert.Equal(new[] { "par" }, Wire(h));
        Assert.Equal("healing", Assert.Single(Replies()));
    }

    // A party member's @heal, on any channel, queues a heal on them as well as the poll.
    [Fact]
    public void Heal_FromAPartyMember_QueuesAHealOnThem()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Party.MajorPartyHealSpell = "mahe";
        h.Members.Add("Bob");

        ChatLines.Hear(_routers[h.Engine], new ChatLine(ChatChannel.Local, "Bob", "@heal"));

        Assert.Equal(new[] { ("Bob", true) }, h.Queued);
        Assert.Equal(new[] { "par" }, Wire(h));
    }

    // A player outside the party heard on gangpath or broadcast gets a queued heal (and
    // no par, which can't show them), unless the healer turned that off.
    [Theory]
    [InlineData(ChatChannel.Gangpath, true, true)]
    [InlineData(ChatChannel.Broadcast, true, true)]
    [InlineData(ChatChannel.Gangpath, false, false)]
    public void Heal_FromOutsideTheParty_OnGangOrBroadcast_IsQueuedIfAllowed(ChatChannel channel, bool allowed, bool queued)
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Eddie", PlayerRemoteControls.ExecuteCommands);
        h.Party.MajorPartyHealSpell = "mahe";
        h.Party.AnswerOutsideHealRequests = allowed;
        h.Engine.DisableBroadcastChannel = false;

        ChatLines.Hear(_routers[h.Engine], new ChatLine(channel, "Eddie", "@heal"));

        Assert.Equal(queued ? new[] { ("Eddie", false) } : [], h.Queued);
        Assert.Empty(Wire(h));
        Assert.Equal(queued ? 1 : 0, Replies().Count);
    }

    // Outside the party by telepath, nothing is queued: only gangpath and broadcast
    // carry a stranger's call.
    [Fact]
    public void Heal_FromOutsideTheParty_ByTelepath_QueuesNothing()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Eddie", PlayerRemoteControls.ExecuteCommands);
        h.Party.MajorPartyHealSpell = "mahe";

        ChatLines.Hear(_routers[h.Engine], Telepath("Eddie", "@heal"));

        Assert.Empty(h.Queued);
    }

    [Fact]
    public void Heal_WithAoeHealerConfigured_AlsoResponds()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        // Only a group / AOE heal configured — still a healer.
        h.Party.MajorPartyHealAoeSpell = "groupheal";

        ChatLines.Hear(_routers[h.Engine], Telepath("Bob", "@heal"));

        Assert.Equal(new[] { "par" }, Wire(h));
        Assert.Equal("healing", Assert.Single(Replies()));
    }

    [Fact]
    public void Heal_WithNoHealConfigured_StaysSilent()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        // No party-heal spell set — nothing to cast, so no par + no reply.

        ChatLines.Hear(_routers[h.Engine], Telepath("Bob", "@heal"));

        Assert.Empty(Wire(h));
        Assert.Empty(Replies());
    }

    [Fact]
    public void Heal_WhileInTrainerMenu_StaysSilent()
    {
        // Parked on the `train stats` form a configured healer can't cast, and a
        // par poll would land in the stat form's Family Name field, overwriting
        // the last name. So the handler stays fully silent — no par, no ack.
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        h.Party.MinorPartyHealSpell = "mheal";
        h.Handler.IsInTrainerMenu = () => true;

        ChatLines.Hear(_routers[h.Engine], Telepath("Bob", "@heal"));

        Assert.Empty(Wire(h));
        Assert.Empty(Replies());
    }

    [Fact]
    public void Heal_FromUnauthorisedSender_IsDenied()
    {
        Harness h = Setup();
        h.Engine.WarnOnDenial = false;
        SeedPlayer(h.Players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.ExecuteCommands);
        h.Party.MinorPartyHealSpell = "mheal";

        ChatLines.Hear(_routers[h.Engine], Telepath("Stranger", "@heal"));

        Assert.Empty(Wire(h));
        Assert.Empty(Replies());
    }

    [Fact]
    public void Disposed_StopsResponding()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        PartySettings settings = new() { MinorPartyHealSpell = "mheal" };
        List<byte[]> wire = new();
        HealCommandHandler handler = new(engine, () => settings, queueHeal: (_, _) => { }, isPartyMember: _ => true);
        handler.SetWireSender(wire.Add);
        SeedPlayer(players, "Bob", PlayerRemoteControls.ExecuteCommands);
        engine.WarnOnDenial = false;

        handler.Dispose();
        ChatLines.Hear(_routers[engine], Telepath("Bob", "@heal"));

        Assert.Empty(wire.Select(b => Encoding.Latin1.GetString(b)).ToList());
    }
}
