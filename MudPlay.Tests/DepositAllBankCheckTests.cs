using System.Text;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// @deposit-all answers "depositing" only where a deposit can start: in a room
// known to be no bank it sends nothing and says so.
public sealed class DepositAllBankCheckTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private (RemoteCommandManager engine, List<string> game) Setup(bool? inBank)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        players.RecordObservation("Leader", @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        players.EditCustomization("Leader", new PlayerCustomization(RemoteControls: PlayerRemoteControls.ExecuteCommands));
        RemoteCommandManager engine = new(new ChatRouter(router), party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        InventoryManager inventory = new(log: null, itemWeightResolver: null, slotResolver: null);
        inventory.AttachLineExtractor(new LineExtractor(new TerminalEmulator(80, 24)));
        CashSettings cash = new();
        List<string> game = new();
        InventoryActionHandler handler = new(engine, inventory, new GroundItemTracker(router, new CurrencyNaming()),
            party, () => cash, new CurrencyNaming())
        {
            InBank = () => inBank,
        };
        handler.SetWireSender(bytes => game.Add(Encoding.Latin1.GetString(bytes)));
        return (engine, game);
    }

    private static ChatLine Telepath(string msg) =>
        new(ChatChannel.TelepathIncoming, "Leader", msg);

    private string Only() =>
        Encoding.Latin1.GetString(Assert.Single(_replies));

    [Fact]
    public void OutsideABank_NothingIsSent_AndTheAnswerSaysSo()
    {
        var (engine, game) = Setup(inBank: false);

        ChatLines.Hear(_routers[engine], Telepath("@deposit-all"));

        Assert.Equal("/Leader {not in a bank - nothing deposited}\r", Only());
        Assert.Empty(game);
    }

    // A leader's stash transfer waits for this answer, so the Warn setting
    // doesn't silence it.
    [Fact]
    public void OutsideABank_TheAnswerGoesWithWarnOffToo()
    {
        var (engine, _) = Setup(inBank: false);
        engine.WarnOnDenial = false;

        ChatLines.Hear(_routers[engine], Telepath("@deposit-all"));

        Assert.StartsWith("/Leader {not in a bank", Only());
    }

    // In a bank, or in a room the client can't place, the command is worked as
    // before (here it stops at the unread inventory).
    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void InABank_OrInARoomWeCannotPlace_TheDepositIsWorked(bool? inBank)
    {
        var (engine, _) = Setup(inBank);

        ChatLines.Hear(_routers[engine], Telepath("@deposit-all"));

        Assert.StartsWith("/Leader {wealth unknown", Only());
    }
}
