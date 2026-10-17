using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// @dupe <player> — copies the SENDER's query, roomba, and quest permissions onto a known
// player. Elevated Commands senders only, one use per sender until the user resets it
// in the client, and accepted over telepath and gangpath only; say, gossip (which also
// carries auctions), broadcast and yell must never reach it, and the local API must
// not be able to drive it. The copy is additive, can never carry anything beyond
// queries (so a duplicated player can't dupe onward or gain Elevated), and never
// touches the target's other settings.
public sealed class DupeHandlerTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    private static readonly DateTime Now = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    private const PlayerRemoteControls Elevated = PlayerRemoteControls.SysopCommands;
    private const PlayerRemoteControls Shareable = DupeHandler.Shareable;

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private (MessageRouter router, RemoteCommandManager engine, PlayerDatabase players) Setup(
        string? localCharacter = null, LogService? log = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, new PartyState(), players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        _ = new DupeHandler(engine, players, () => localCharacter, log);
        return (router, engine, players);
    }

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static PlayerRemoteControls Controls(PlayerDatabase db, string name) =>
        db.Find(name)!.RemoteControls;

    private static bool Spent(PlayerDatabase db, string name) =>
        db.Find(name)!.DupeUsedAtUtc is not null;

    // What the user does in the edit dialog: press Reset @dupe, then Save.
    private static void ResetInTheClient(PlayerDatabase db, string name)
    {
        PlayerCustomization live = db.Find(name)!.ToCustomization();
        db.EditCustomization(name, live.KeepDupeLockFrom(live, reset: true));
    }

    private List<string> Replies() =>
        _replies.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

    private static string Payload(string wire)
    {
        int open = wire.IndexOf('{');
        int close = wire.LastIndexOf('}');
        return open >= 0 && close > open ? wire[(open + 1)..close] : wire;
    }

    // ===== The feature =====

    [Fact]
    public void Telepath_CopiesOnlyQueryAndRoomba_OntoTarget()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.Equal(Shareable, Controls(players, "Moron"));
        string reply = Assert.Single(Replies());
        Assert.StartsWith("/Reveal ", reply);
        Assert.Equal("Moron now has your query, roomba, and quest permissions", Payload(reply));
    }

    [Fact]
    public void Gangpath_CopiesOnlyQueryAndRoomba_AndRepliesOnGangpath()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal gangpaths: @dupe Moron"));

        Assert.Equal(Shareable, Controls(players, "Moron"));
        Assert.StartsWith("bg ", Assert.Single(Replies()));
    }

    [Fact]
    public void Copy_IsAdditive_TargetKeepsWhatItAlreadyHas()
    {
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.QueryVersion | Elevated);
        SeedPlayer(players, "Moron", PlayerRemoteControls.ExecuteCommands);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.Equal(
            PlayerRemoteControls.ExecuteCommands | PlayerRemoteControls.QueryVersion,
            Controls(players, "Moron"));
    }

    [Fact]
    public void Copy_MovesOnlyThePermissionGrid_NotTheOtherSettings()
    {
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);
        players.EditCustomization("Moron", new PlayerCustomization(
            InviteToPartyIfSeen: true, DontAutoDelete: true, Notes: "alt of Reveal"));

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        PlayerRecord moron = players.Find("Moron")!;
        Assert.Equal(Shareable, moron.RemoteControls);
        Assert.True(moron.InviteToPartyIfSeen);
        Assert.True(moron.DontAutoDelete);
        Assert.Equal("alt of Reveal", moron.Notes);
    }

    [Fact]
    public void TargetAlreadyHoldsEverything_RepliesAndChangesNothing_AndKeepsTheUse()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.QueryVersion | Elevated);
        SeedPlayer(players, "Moron", PlayerRemoteControls.All);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.All, Controls(players, "Moron"));
        Assert.Equal("Moron already has all your query, roomba, and quest permissions",
            Payload(Assert.Single(Replies())));
        Assert.False(Spent(players, "Reveal"));
    }

    // ===== Safeguard: query, roomba, and quest only =====

    [Theory]
    [InlineData(PlayerRemoteControls.RequestInvite)]
    [InlineData(PlayerRemoteControls.MovePlayer)]
    [InlineData(PlayerRemoteControls.ExecuteCommands)]
    [InlineData(PlayerRemoteControls.HangupDisconnect)]
    [InlineData(PlayerRemoteControls.AlterSettings)]
    [InlineData(PlayerRemoteControls.DivertConversations)]
    [InlineData(PlayerRemoteControls.SysopCommands)]
    public void Copy_NeverGrantsAnythingBeyondQueries(PlayerRemoteControls flag)
    {
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.True(Spent(players, "Reveal"));   // the dupe really happened
        Assert.False(Controls(players, "Moron").HasFlag(flag), $"{flag} must never be duplicated");
    }

    [Fact]
    public void DuplicatedPlayer_CannotDupeOnward()
    {
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);
        SeedPlayer(players, "Third", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));
        router.Dispatch(Line("Moron telepaths: @dupe Third"));

        Assert.False(Controls(players, "Moron").HasFlag(Elevated));
        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Third"));
    }

    [Fact]
    public void SenderWithNoQueryPermissions_IsRefused_AndKeepsTheUse()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", Elevated);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Equal("you have no query, roomba, or quest permissions to copy",
            Payload(Assert.Single(Replies())));
        Assert.False(Spent(players, "Reveal"));
    }

    // ===== Safeguard: one use, reset only in the client =====

    [Fact]
    public void Dupe_SpendsTheUse_AndRecordsWhoAndWhen()
    {
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);
        DateTime before = DateTime.UtcNow;

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        PlayerRecord reveal = players.Find("Reveal")!;
        Assert.NotNull(reveal.DupeUsedAtUtc);
        Assert.InRange(reveal.DupeUsedAtUtc!.Value, before, DateTime.UtcNow);
        Assert.Equal("Moron", reveal.DupedPlayer);
    }

    [Fact]
    public void SecondDupe_IsRefused_UntilResetInTheClient()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);
        SeedPlayer(players, "Third", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));
        router.Dispatch(Line("Reveal telepaths: @dupe Third"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Third"));
        Assert.Equal("your @dupe has already been used", Payload(Replies()[^1]));

        ResetInTheClient(players, "Reveal");
        router.Dispatch(Line("Reveal telepaths: @dupe Third"));

        Assert.Equal(Shareable, Controls(players, "Third"));
    }

    [Fact]
    public void NoRemoteCommandCanResetTheLock()
    {
        // The reset exists only as an edit-dialog action. Every catalog command a
        // permissioned sender could try must leave a spent @dupe spent.
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);
        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));
        Assert.True(Spent(players, "Reveal"));

        foreach (string command in new[]
            { "@auto-all", "@settings", "@reset", "@profile 1", "@divert Moron", "@dupe Reveal", "@do rest" })
            router.Dispatch(Line($"Reveal telepaths: {command}"));

        Assert.True(Spent(players, "Reveal"));
    }

    [Fact]
    public void FailedAttempts_DoNotSpendTheUse()
    {
        var (router, _, players) = Setup(localCharacter: "Kyau");
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Kyau", PlayerRemoteControls.None);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        foreach (string attempt in new[] { "@dupe Nobody", "@dupe Reveal", "@dupe Kyau", "@dupe", "@dupe Moron Extra" })
            router.Dispatch(Line($"Reveal telepaths: {attempt}"));
        Assert.False(Spent(players, "Reveal"));

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));
        Assert.Equal(Shareable, Controls(players, "Moron"));
    }

    [Fact]
    public void Lock_CountsAsNonDefault_SoItIsNeverPruned()
    {
        // EditCustomization drops any all-default customization; a sender whose only
        // non-default value is a spent @dupe must not lose the lock that way.
        PlayerCustomization spent = new(DupeUsedAtUtc: Now, DupedPlayer: "Moron");
        Assert.False(spent.IsDefault);

        PlayerDatabase players = new(new ProfileService(), () => null);
        SeedPlayer(players, "Reveal", PlayerRemoteControls.None);
        players.EditCustomization("Reveal", spent);

        Assert.Equal(Now, players.Find("Reveal")!.DupeUsedAtUtc);
    }

    [Fact]
    public void KeepDupeLockFrom_TakesTheLiveLock_NotTheEditorsStaleCopy()
    {
        // An edit dialog opened before a @dupe landed still holds "not spent".
        PlayerCustomization staleEditor = new(RemoteControls: PlayerRemoteControls.All);
        PlayerCustomization live = new(DupeUsedAtUtc: Now, DupedPlayer: "Moron");

        PlayerCustomization saved = staleEditor.KeepDupeLockFrom(live, reset: false);

        Assert.Equal(Now, saved.DupeUsedAtUtc);
        Assert.Equal("Moron", saved.DupedPlayer);
        Assert.Equal(PlayerRemoteControls.All, saved.RemoteControls);
    }

    [Fact]
    public void KeepDupeLockFrom_ClearsTheLock_OnlyOnAnExplicitReset()
    {
        PlayerCustomization live = new(DupeUsedAtUtc: Now, DupedPlayer: "Moron");

        PlayerCustomization saved = new PlayerCustomization().KeepDupeLockFrom(live, reset: true);

        Assert.Null(saved.DupeUsedAtUtc);
        Assert.Null(saved.DupedPlayer);
    }

    [Fact]
    public void Lock_SurvivesJson_AndOldProfilesWithoutItStillLoad()
    {
        PlayerCustomization spent = new(
            RemoteControls: PlayerRemoteControls.QueryVersion | Elevated,
            DupeUsedAtUtc: Now, DupedPlayer: "Moron");

        string json = JsonSerializer.Serialize(spent, JsonStore.Options);
        Assert.Equal(spent, JsonSerializer.Deserialize<PlayerCustomization>(json, JsonStore.Options));

        // A profile written before the lock existed carries neither field.
        JsonObject legacy = JsonNode.Parse(json)!.AsObject();
        foreach (string name in legacy.Select(p => p.Key).ToList())
            if (name.StartsWith("DupeUsed", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("DupedPlayer", StringComparison.OrdinalIgnoreCase))
                legacy.Remove(name);
        PlayerCustomization loaded =
            JsonSerializer.Deserialize<PlayerCustomization>(legacy.ToJsonString(), JsonStore.Options);

        Assert.Null(loaded.DupeUsedAtUtc);
        Assert.Equal(PlayerRemoteControls.QueryVersion | Elevated, loaded.RemoteControls);
    }

    // ===== Safeguard: logged =====

    [Fact]
    public void Dupe_IsLogged_WithWhoWhatAndTheSpentLock()
    {
        LogService log = new();
        var (router, _, players) = Setup(log: log);
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        LogEntry entry = Assert.Single(log.Snapshot(), e => e.Message.Contains("@dupe from Reveal onto Moron"));
        Assert.Equal(LogSeverity.Info, entry.Severity);
        Assert.Equal("RemoteCmd", entry.Source);
        Assert.Contains("granted", entry.Message);
        Assert.Contains("spent", entry.Message);
    }

    [Fact]
    public void RefusedDupe_IsLoggedToo()
    {
        LogService log = new();
        var (router, _, players) = Setup(log: log);
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);
        SeedPlayer(players, "Third", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));
        router.Dispatch(Line("Reveal telepaths: @dupe Third"));

        Assert.Contains(log.Snapshot(), e =>
            e.Message.Contains("@dupe from Reveal refused") && e.Message.Contains("already been used"));
    }

    // ===== Channels: telepath and gangpath only =====

    [Fact]
    public void Say_IsIgnored_NoChange_NoReply()
    {
        var (_, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        ChatLines.Hear(_routers[engine], new ChatLine(ChatChannel.Local, "Reveal", "@dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Empty(_replies);
        Assert.False(Spent(players, "Reveal"));
    }

    [Fact]
    public void Gossip_IsIgnored()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal gossips: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Empty(_replies);
    }

    [Fact]
    public void Auction_IsIgnored()
    {
        // Auctions share gossip's shape and ride the Gossip channel.
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal auctions: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Empty(_replies);
    }

    [Fact]
    public void Broadcast_IsIgnored()
    {
        var (_, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        ChatLines.Hear(_routers[engine], new ChatLine(ChatChannel.Broadcast, "Reveal", "@dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Empty(_replies);
    }

    [Fact]
    public void Yell_IsIgnored()
    {
        var (_, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        ChatLines.Hear(_routers[engine], new ChatLine(ChatChannel.Yell, "Reveal", "@dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Empty(_replies);
    }

    [Fact]
    public void LocalApi_CannotDrive_Dupe()
    {
        // TryInvokeLocal stamps every invocation as a telepath, so without an
        // explicit refusal a channel check inside the handler would wave it through.
        var (_, engine, players) = Setup();
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);
        List<string> replies = [];

        var result = engine.TryInvokeLocal("@dupe", ["Moron"], replies.Add);

        Assert.Equal(RemoteCommandManager.LocalInvokeResult.PathChannelOnly, result);
        Assert.Empty(replies);
        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
    }

    [Fact]
    public void Catalog_MarksDupeAsPathChannelOnly_AndNothingElse()
    {
        Assert.True(RemoteCommandCatalog.IsPathChannelOnly("@dupe"));
        Assert.True(RemoteCommandCatalog.IsPathChannelOnly("@DUPE"));
        Assert.False(RemoteCommandCatalog.IsPathChannelOnly("@where"));
        Assert.False(RemoteCommandCatalog.IsPathChannelOnly(""));
    }

    // ===== Who may use it =====

    [Fact]
    public void Catalog_GatesDupeBehindElevatedCommands()
    {
        // Handing out trust must sit behind the same tier as @suicide, not behind an
        // ordinary grant.
        Assert.True(RemoteCommandCatalog.TryGetCategory("@dupe", out PlayerRemoteControls category));
        Assert.Equal(PlayerRemoteControls.SysopCommands, category);
    }

    [Fact]
    public void ExecuteCommandsAlone_DoesNotAllowDupe()
    {
        // ExecuteCommands is the next-highest tier (@do); it must not be enough.
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.ExecuteCommands);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
    }

    [Fact]
    public void SenderWithoutElevatedCommands_IsDenied_EvenWithEveryOtherPermission()
    {
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All & ~Elevated);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
    }

    [Fact]
    public void UnknownSender_IsDenied()
    {
        var (router, _, players) = Setup();
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Stranger telepaths: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
    }

    // ===== Refusals =====

    [Fact]
    public void UnknownTarget_IsRefused_AndNoRecordIsCreated()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);

        router.Dispatch(Line("Reveal telepaths: @dupe Nobody"));

        Assert.Null(players.Find("Nobody"));
        Assert.Equal("unknown player Nobody", Payload(Assert.Single(Replies())));
    }

    [Fact]
    public void LocalCharacter_IsRefused()
    {
        var (router, engine, players) = Setup(localCharacter: "Kyau");
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Kyau", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Kyau"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Kyau"));
        Assert.Equal("can't change my own permissions", Payload(Assert.Single(Replies())));
    }

    [Fact]
    public void DupingYourself_IsRefused()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);

        router.Dispatch(Line("Reveal telepaths: @dupe Reveal"));

        Assert.Equal("you already have your own permissions", Payload(Assert.Single(Replies())));
    }

    [Theory]
    [InlineData("@dupe")]
    [InlineData("@dupe Moron Extra")]
    public void WrongArgumentCount_RepliesWithUsage_AndChangesNothing(string message)
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line($"Reveal telepaths: {message}"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Equal("usage: @dupe <player>", Payload(Assert.Single(Replies())));
    }

    [Fact]
    public void WarnOnDenialOff_SuppressesFailureReplies()
    {
        var (router, engine, players) = Setup();
        engine.WarnOnDenial = false;
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);

        router.Dispatch(Line("Reveal telepaths: @dupe Nobody"));

        Assert.Empty(_replies);
    }
}
