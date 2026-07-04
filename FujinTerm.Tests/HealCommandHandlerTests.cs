using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the receive side of <see cref="HealCommandHandler"/>: an inbound
/// <c>@heal</c> makes a configured party-healer poll <c>par</c> (so
/// CastingDirector re-evaluates against fresh party HP) and acknowledge;
/// a member with no party-heal spell configured stays silent, and an
/// unauthorised sender is denied before the handler runs.
/// </summary>
public sealed class HealCommandHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public required RemoteCommandManager Engine { get; init; }
        public required PlayerDatabase Players { get; init; }
        public required PartySettings Party { get; init; }
        public required List<byte[]> WireSent { get; init; }
    }

    private static Harness Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        PartySettings settings = new();
        List<byte[]> wire = new();
        HealCommandHandler handler = new(engine, () => settings);
        handler.SetWireSender(wire.Add);
        return new Harness
        {
            Engine = engine,
            Players = players,
            Party = settings,
            WireSent = wire,
        };
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static List<string> Replies(RemoteCommandManager engine) =>
        engine.LastSentForTests
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

        h.Engine.DispatchForTests(Telepath("Bob", "@heal"));

        Assert.Equal(new[] { "par" }, Wire(h));
        Assert.Equal("healing", Assert.Single(Replies(h.Engine)));
    }

    [Fact]
    public void Heal_WithAoeHealerConfigured_AlsoResponds()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        // Only a group / AOE heal configured — still a healer.
        h.Party.MajorPartyHealAoeSpell = "groupheal";

        h.Engine.DispatchForTests(Telepath("Bob", "@heal"));

        Assert.Equal(new[] { "par" }, Wire(h));
        Assert.Equal("healing", Assert.Single(Replies(h.Engine)));
    }

    [Fact]
    public void Heal_WithNoHealConfigured_StaysSilent()
    {
        Harness h = Setup();
        SeedPlayer(h.Players, "Bob", PlayerRemoteControls.ExecuteCommands);
        // No party-heal spell set — nothing to cast, so no par + no reply.

        h.Engine.DispatchForTests(Telepath("Bob", "@heal"));

        Assert.Empty(Wire(h));
        Assert.Empty(Replies(h.Engine));
    }

    [Fact]
    public void Heal_FromUnauthorisedSender_IsDenied()
    {
        Harness h = Setup();
        h.Engine.WarnOnDenial = false;
        SeedPlayer(h.Players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.ExecuteCommands);
        h.Party.MinorPartyHealSpell = "mheal";

        h.Engine.DispatchForTests(Telepath("Stranger", "@heal"));

        Assert.Empty(Wire(h));
        Assert.Empty(Replies(h.Engine));
    }

    [Fact]
    public void Disposed_StopsResponding()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        PartySettings settings = new() { MinorPartyHealSpell = "mheal" };
        List<byte[]> wire = new();
        HealCommandHandler handler = new(engine, () => settings);
        handler.SetWireSender(wire.Add);
        SeedPlayer(players, "Bob", PlayerRemoteControls.ExecuteCommands);
        engine.WarnOnDenial = false;

        handler.Dispose();
        engine.DispatchForTests(Telepath("Bob", "@heal"));

        Assert.Empty(wire.Select(b => Encoding.Latin1.GetString(b)).ToList());
    }
}
