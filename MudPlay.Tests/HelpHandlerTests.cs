using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Pins the @help handler: an authorised sender telepathing @help
// receives a flat list of the catalog commands their per-player
// permission grant allows. Party-whitelist (None-category) commands are
// never listed; long lists split across multiple telepaths.
public sealed class HelpHandlerTests
{
    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private (MessageRouter router, HelpHandler handler, PlayerDatabase players, RemoteCommandManager engine) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        RemoteCommandManager engine = new(chat, party, players);
        engine.SetWireSender(_replies.Add);
        HelpHandler handler = new(engine);
        return (router, handler, players, engine);
    }

    private static void Telepath(MessageRouter router, string sender, string message) =>
        router.Dispatch(Line($"{sender} telepaths: {message}"));

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    // Bare payload text of every reply, in send order (braces + prefix stripped).
    private List<string> Replies() =>
        _replies
            .Select(b => Encoding.Latin1.GetString(b))
            .Select(StripWire)
            .ToList();

    private static string StripWire(string wire)
    {
        // "/Bob {payload}\r" -> "payload"
        string s = wire.TrimEnd('\r');
        int open = s.IndexOf('{');
        int close = s.LastIndexOf('}');
        return open >= 0 && close > open ? s[(open + 1)..close] : s;
    }

    [Fact]
    public void Help_ListsOnlyGrantedCommands()
    {
        // QueryVersion alone → only @version + @help are reachable.
        var (router, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryVersion);

        Telepath(router, "Bob", "@help");

        string joined = string.Join(", ", Replies());
        Assert.Contains("@version", joined);
        Assert.Contains("@help", joined);
        Assert.DoesNotContain("@where", joined);
        Assert.DoesNotContain("@do", joined);
    }

    [Fact]
    public void Help_ExcludesPartyWhitelistCommands()
    {
        // Even with every flag granted, None-category party-whitelist
        // commands (@wait / @ok / @share / @comeback) are never listed —
        // they're gated by party membership, not a per-player flag. (@kill
        // is NOT in this family: it's an ExecuteCommands action request, so
        // it DOES appear for a granted sender — see the catalog.)
        var (router, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.All);

        Telepath(router, "Bob", "@help");

        string joined = string.Join(" ", Replies());
        foreach (string party in new[] { "@wait", "@ok", "@share", "@comeback" })
            Assert.DoesNotContain(party, joined.Split(' ', ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Help_AllPermissions_SplitsAcrossTelepaths()
    {
        // The full grid's command list exceeds one 200-char telepath, so
        // the reply arrives as multiple chunks, none over the cap.
        var (router, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.All);

        Telepath(router, "Bob", "@help");

        List<string> replies = Replies();
        Assert.True(replies.Count > 1, "expected the long list to split across telepaths");
        Assert.All(replies, r => Assert.True(r.Length <= 200, $"chunk over cap: {r.Length}"));
    }

    [Fact]
    public void Help_ChunksContainNoEmptyEntries()
    {
        var (router, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.All);

        Telepath(router, "Bob", "@help");

        foreach (string chunk in Replies())
        {
            Assert.False(chunk.StartsWith(", "), "chunk should not start with a separator");
            Assert.False(chunk.EndsWith(", "), "chunk should not end with a separator");
        }
    }

    [Fact]
    public void Help_FromUnauthorisedSender_DoesNotReply()
    {
        // Stranger lacks QueryVersion → @help denies; with denial warnings off
        // nothing at all goes back.
        var (router, _, players, engine) = Setup();
        engine.WarnOnDenial = false;
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.QueryVersion);

        Telepath(router, "Stranger", "@help");

        Assert.Empty(Replies());
    }

    [Fact]
    public void GetPermittedCommands_ExcludesNoneAndUngranted()
    {
        var (_, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryLocation);

        IReadOnlyList<string> cmds = engine.GetPermittedCommands("Bob");

        Assert.Contains("@where", cmds);
        Assert.Contains("@who", cmds);
        Assert.Contains("@path", cmds);
        Assert.DoesNotContain("@version", cmds); // QueryVersion not granted
        Assert.DoesNotContain("@wait", cmds);    // party-whitelist
    }

    [Fact]
    public void GetPermittedCommands_ListsRelayBack_OnlyWithExecuteGrant()
    {
        var (_, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryLocation);
        SeedPlayer(players, "Ann", PlayerRemoteControls.ExecuteCommands);

        Assert.DoesNotContain("&@<command>", engine.GetPermittedCommands("Bob"));
        Assert.Contains("&@<command>", engine.GetPermittedCommands("Ann"));
    }

    [Fact]
    public void Help_WithCommandArg_RepliesWithThatCommandsSyntax()
    {
        // @help only needs QueryVersion; the ARG command is merely described, so a
        // sender can look up a command they can't themselves use.
        var (router, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryVersion);

        Telepath(router, "Bob", "@help suicide");

        string joined = string.Join(" ", Replies());
        Assert.Contains("@suicide", joined);
        Assert.Contains("Elevated", joined);   // from the description
    }

    [Fact]
    public void Help_CommandArg_AcceptsWithOrWithoutAtSign()
    {
        var (router, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryVersion);

        Telepath(router, "Bob", "@help @goto");
        Telepath(router, "Bob", "@help goto");

        List<string> replies = Replies();
        Assert.Equal(2, replies.Count);
        Assert.Equal(replies[0], replies[1]);          // same answer either way
        Assert.Contains("@goto <destination>", replies[0]);
    }

    [Fact]
    public void Help_UnknownCommandArg_RepliesNoSuchCommand()
    {
        var (router, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryVersion);

        Telepath(router, "Bob", "@help notacommand");

        Assert.Contains("no such command", string.Join(" ", Replies()));
    }

    [Fact]
    public void Help_NoArg_IncludesUsageHint()
    {
        var (router, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryVersion);

        Telepath(router, "Bob", "@help");

        Assert.Contains(Replies(), r => r.Contains("@help <command>"));
    }

    [Fact]
    public void BareHelp_WithoutAtSign_DoesNotFire()
    {
        // A plain `help` typed between players in chat must NOT trigger the command
        // — only the @-prefixed @help does. (No reply, permitted or not.)
        var (router, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryVersion);

        Telepath(router, "Bob", "help");
        Telepath(router, "Bob", "help me carry this");

        Assert.Empty(Replies());
    }

    [Fact]
    public void Dispose_UnregistersHandler()
    {
        var (_, handler, _, engine) = Setup();
        handler.Dispose();
        Assert.False(engine.UnregisterHandler("@help")); // already gone
    }
}
