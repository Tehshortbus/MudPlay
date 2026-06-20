using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the @help handler: an authorised sender telepathing <c>@help</c>
/// receives a flat list of the catalog commands their per-player
/// permission grant allows. Party-whitelist (None-category) commands are
/// never listed; long lists split across multiple telepaths.
/// </summary>
public sealed class HelpHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static (MessageRouter router, HelpHandler handler, PlayerDatabase players, RemoteCommandManager engine) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
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

    /// <summary>Bare payload text of every reply, in send order (braces + prefix stripped).</summary>
    private static List<string> Replies(RemoteCommandManager engine) =>
        engine.LastSentForTests
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

        string joined = string.Join(", ", Replies(engine));
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

        string joined = string.Join(" ", Replies(engine));
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

        List<string> replies = Replies(engine);
        Assert.True(replies.Count > 1, "expected the long list to split across telepaths");
        Assert.All(replies, r => Assert.True(r.Length <= 200, $"chunk over cap: {r.Length}"));
    }

    [Fact]
    public void Help_ChunksContainNoEmptyEntries()
    {
        var (router, _, players, engine) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.All);

        Telepath(router, "Bob", "@help");

        foreach (string chunk in Replies(engine))
        {
            Assert.False(chunk.StartsWith(", "), "chunk should not start with a separator");
            Assert.False(chunk.EndsWith(", "), "chunk should not end with a separator");
        }
    }

    [Fact]
    public void Help_FromUnauthorisedSender_DoesNotReply()
    {
        // Stranger lacks QueryVersion → @help itself denies; the engine's
        // generic denial may fire, but no command list is produced.
        var (router, _, players, engine) = Setup();
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.QueryVersion);

        Telepath(router, "Stranger", "@help");

        string joined = string.Join(" ", Replies(engine));
        Assert.DoesNotContain("@version", joined);
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
    public void Dispose_UnregistersHandler()
    {
        var (_, handler, _, engine) = Setup();
        handler.Dispose();
        Assert.False(engine.UnregisterHandler("@help")); // already gone
    }
}
