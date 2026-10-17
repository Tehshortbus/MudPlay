using System.Text;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The wording of three answers: what a hide sweep kept back, the profile roster,
// and a full party.
public sealed class RemoteReplyWordingTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ChatLine Telepath(string msg) =>
        new(ChatChannel.TelepathIncoming, "Friend", msg);

    // Each engine's own wire, captured through the sender the client binds.
    private readonly Dictionary<RemoteCommandManager, List<byte[]>> _wire = new();

    private (RemoteCommandManager engine, MessageRouter router, PartyState party) Engine()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        players.RecordObservation("Friend", @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        players.EditCustomization("Friend", new PlayerCustomization(RemoteControls: PlayerRemoteControls.All));
        RemoteCommandManager engine = new(new ChatRouter(router), party, players);
        _routers[engine] = router;
        engine.SetWireSender((_wire[engine] = new()).Add);
        return (engine, router, party);
    }

    private string Only(RemoteCommandManager engine) =>
        Encoding.Latin1.GetString(Assert.Single(_wire[engine]));

    [Theory]
    [InlineData("@drop-all", "can't be dropped: cursed ring)")]
    [InlineData("@hide-all", "can't be hidden: cursed ring)")]
    public void ASweep_NamesWhatItKeptBack_InPlainWords(string command, string tail)
    {
        var (engine, router, party) = Engine();
        InventoryManager inventory = new(log: null, itemWeightResolver: null, slotResolver: null);
        ServerFeed feed = new();
        inventory.AttachLineExtractor(feed.Lines);
        CashSettings cash = new();
        _ = new InventoryActionHandler(engine, inventory, new GroundItemTracker(router, new CurrencyNaming()),
            party, () => cash, new CurrencyNaming(), cannotDrop: (name, _) => name == "cursed ring");
        feed.Line("You are carrying a rusty dagger, cursed ring.");
        feed.Line("You have no keys.");
        feed.Line("Wealth:    0 copper farthings");
        feed.Line("Encumbrance:    36/2880  -  Light  [1%]");

        ChatLines.Hear(_routers[engine], Telepath(command));

        Assert.EndsWith(tail + "}\r", Only(engine));
    }

    // The roster is written in braces for the terminal; the reply's own pair is
    // the only one that goes out.
    [Fact]
    public void ProfileRoster_GoesOutInOnePairOfBraces()
    {
        var (engine, _, _) = Engine();
        _ = new ProfileSwapHandler(engine, TwoProfiles());

        ChatLines.Hear(_routers[engine], Telepath("@profile"));

        Assert.Equal("/Friend {Current: 1)Fire, On Standby: 2)Cold}\r", Only(engine));
    }

    // The swap report is the terminal's, with a long dash and dots; the copy that
    // goes to the asker has neither.
    [Fact]
    public void ProfileSwitch_AnswersInPlainAscii()
    {
        var (engine, _, _) = Engine();
        _ = new ProfileSwapHandler(engine, TwoProfiles());

        ChatLines.Hear(_routers[engine], Telepath("@profile 2"));

        Assert.Equal("/Friend {Combat profile 2 (Cold) - normal: lbol, drain: drai}\r", Only(engine));
    }

    private static CombatProfileManager TwoProfiles()
    {
        CombatSpellProfile fire = new() { Name = "Fire" };
        CombatSpellProfile cold = new() { Name = "Cold" };
        cold.NormalAttackSpell.SpellName = "lbol";
        cold.DrainSpell.SpellName = "drai";
        CharacterProfile profile = new()
        {
            CombatProfiles = new CombatProfileSettings { Profiles = { fire, cold }, ActiveId = fire.Id },
        };
        CombatSettings combat = new();
        HealthSettings health = new();
        SpellsSettings spells = new();
        return new CombatProfileManager(
            profile: () => profile,
            readCombat: () => combat,
            writeCombat: _ => { },
            readHealth: () => health,
            writeHealth: _ => { },
            readSpells: () => spells,
            writeSpells: _ => { },
            equipment: () => null,
            save: () => { });
    }

    [Fact]
    public void FullParty_WithOneOtherMemberListed_ReadsInTheSingular()
    {
        var (engine, _, party) = Engine();
        PartyEssentialHandlers handlers = new(engine, new PlayerState(), party);
        handlers.SetWireSender(_ => { });
        party.IsInParty = true;
        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "Tank" });
        for (int i = 0; i < 5; i++) party.Members.Add(new PartyMember { Name = string.Empty });

        ChatLines.Hear(_routers[engine], Telepath("@invite"));

        Assert.Equal("/Friend {My party is full: Tank is following me.}\r", Only(engine));
    }
}
