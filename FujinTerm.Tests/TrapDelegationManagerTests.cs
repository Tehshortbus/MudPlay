using System.IO;
using System.Linq;
using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for <see cref="TrapDelegationManager"/> — the party-member
/// trap-delegation path. Verifies capability resolution (class main gate /
/// race secondary, excluding self + invitees), race-capture-on-join via
/// <c>look</c>, the <c>.@trap</c> say broadcast, and resume-watch on a
/// party member's say reply. Each test seeds an isolated game-data set so
/// the suite never touches the user's real Data folder.
/// </summary>
public sealed class TrapDelegationManagerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 12, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public TrapDelegationManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-trapdel-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ----- Fixtures -------------------------------------------------------

    /// <summary>
    /// Seed a game-data set: "Ninja" class + "Gnome" race grant traps
    /// (codes 40 / 1002); "Mage" + "Human" don't. Returns an active cache.
    /// </summary>
    private GameDataCache SeededCache()
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Classes.json"),
            "[{\"Name\":\"Ninja\",\"Abil-0\":40},{\"Name\":\"Mage\",\"Abil-0\":5}]");
        File.WriteAllText(Path.Combine(dir, "Races.json"),
            "[{\"Name\":\"Gnome\",\"Abil-0\":1002},{\"Name\":\"Human\",\"Abil-0\":0}]");
        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        return cache;
    }

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], Now, IsPromptLine: false);

    /// <summary>
    /// Build router (with default patterns) + party + players + game data.
    /// The manager is constructed by the caller so tests control whether
    /// <see cref="PartyManager.MemberFollowConfirmed"/> fires before or
    /// after construction.
    /// </summary>
    private (MessageRouter router, PartyManager party, PlayerDatabase players, GameDataCache gameData) Harness(
        string localName = "Forged")
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyManager party = new(router, new PartyState()) { LocalCharacterName = localName };
        PlayerDatabase players = new();
        GameDataCache gameData = SeededCache();
        return (router, party, players, gameData);
    }

    private static TrapDelegationManager NewManager(
        (MessageRouter router, PartyManager party, PlayerDatabase players, GameDataCache gameData) h)
        => new(h.party, h.players, h.gameData, h.router);

    private static string Sent(TrapDelegationManager mgr, int index) =>
        Encoding.Latin1.GetString(mgr.LastSentForTests[index]);

    // ===== Capability — class main gate ==================================

    [Fact]
    public void AnyPartyMemberCanDisarm_True_WhenMemberClassGrantsTraps()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        h.party.State.Members.First(m => m.Name == "Helper").Class = "Ninja";

        TrapDelegationManager mgr = NewManager(h);
        Assert.True(mgr.AnyPartyMemberCanDisarm());
    }

    [Fact]
    public void AnyPartyMemberCanDisarm_False_WhenNoMemberCapable()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        h.party.State.Members.First(m => m.Name == "Helper").Class = "Mage";

        TrapDelegationManager mgr = NewManager(h);
        Assert.False(mgr.AnyPartyMemberCanDisarm());
    }

    [Fact]
    public void AnyPartyMemberCanDisarm_ExcludesSelf()
    {
        // Self row carries the trap-capable class; it must NOT count —
        // delegation is about OTHER members covering for us.
        var h = Harness(localName: "Forged");
        h.router.Dispatch(Line("Helper started to follow you."));
        h.party.State.Members.First(m => m.IsSelf).Class = "Ninja";
        h.party.State.Members.First(m => m.Name == "Helper").Class = "Mage";

        TrapDelegationManager mgr = NewManager(h);
        Assert.False(mgr.AnyPartyMemberCanDisarm());
    }

    // ===== Capability — race secondary ===================================

    [Fact]
    public void AnyPartyMemberCanDisarm_True_ViaRace_WhenClassDoesntGrant()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        h.party.State.Members.First(m => m.Name == "Helper").Class = "Mage";
        // Race known from a prior look — Gnome grants traps (code 1002).
        h.players.RecordLook("Helper", "Gnome", "Mage", null, Now.UtcDateTime);

        TrapDelegationManager mgr = NewManager(h);
        Assert.True(mgr.AnyPartyMemberCanDisarm());
    }

    [Fact]
    public void AnyPartyMemberCanDisarm_False_WhenRaceUnknown_AndClassDoesntGrant()
    {
        // "shadowy figure" look leaves race null — we don't assume.
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        h.party.State.Members.First(m => m.Name == "Helper").Class = "Mage";

        TrapDelegationManager mgr = NewManager(h);
        Assert.False(mgr.AnyPartyMemberCanDisarm());
    }

    // ===== Race capture on join ==========================================

    [Fact]
    public void OnMemberJoined_LooksAtMember_WhenRaceUnknownAndClassNoGrant()
    {
        var h = Harness();
        TrapDelegationManager mgr = NewManager(h);    // subscribe BEFORE the join

        h.router.Dispatch(Line("Helper started to follow you."));

        Assert.Equal("look Helper\r", Sent(mgr, 0));
    }

    [Fact]
    public void OnMemberJoined_NoLook_WhenRaceAlreadyKnown()
    {
        var h = Harness();
        h.players.RecordLook("Helper", "Human", null, null, Now.UtcDateTime);
        TrapDelegationManager mgr = NewManager(h);

        h.router.Dispatch(Line("Helper started to follow you."));

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void OnMemberJoined_NoLook_WhenClassAlreadyGrantsTraps()
    {
        // Member seen in `par` first (class known) before the follow
        // confirmation — class settles capability, no race probe needed.
        var h = Harness();
        TrapDelegationManager mgr = NewManager(h);
        h.party.TestEnterParBlock();
        h.party.FeedTestLines(new[]
        {
            "  Helper                          (Ninja)         [M:100%] [H:100%]   - Frontrank",
            "",
        });

        h.router.Dispatch(Line("Helper started to follow you."));

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void OnMemberJoined_SkipsLook_WhileReformSettling()
    {
        // No member looks during a party-splitting-teleport reform: a look renders
        // the member's description right as the walker's gate releases and can
        // re-strand the resuming move. Race stays unknown until a later join.
        var h = Harness();
        TrapDelegationManager mgr = NewManager(h);
        mgr.IsPartyReformSettling = () => true;

        h.router.Dispatch(Line("Helper started to follow you."));

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void OnMemberJoined_Looks_WhenReformNotSettling()
    {
        // Outside a reform (predicate false) the ordinary race probe still fires.
        var h = Harness();
        TrapDelegationManager mgr = NewManager(h);
        mgr.IsPartyReformSettling = () => false;

        h.router.Dispatch(Line("Helper started to follow you."));

        Assert.Equal("look Helper\r", Sent(mgr, 0));
    }

    // ===== Delegation broadcast ==========================================

    [Fact]
    public void Delegate_BroadcastsTrapOnSay()
    {
        var h = Harness();
        TrapDelegationManager mgr = NewManager(h);

        mgr.Delegate("north", _ => { });

        Assert.Equal(".@trap north\r", Sent(mgr, mgr.LastSentForTests.Count - 1));
    }

    // ===== Resume watch ==================================================

    [Fact]
    public void PartyMemberSayReply_ResumesDelegation()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        TrapDelegationManager mgr = NewManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        h.router.Dispatch(Line("Helper says \"Trap to the n disarmed.\""));

        Assert.Equal("Trap to the n disarmed.", reply);
    }

    [Fact]
    public void PartyMemberFailureReply_StillForwardsToWalker()
    {
        // Any terminal reply (failure / stop) ends the delegation — the
        // walker's OnTrapReply decides resume vs abort.
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        TrapDelegationManager mgr = NewManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        h.router.Dispatch(Line("Helper says \"Couldn't find trap to the n (5 attempts).\""));

        Assert.Equal("Couldn't find trap to the n (5 attempts).", reply);
    }

    [Fact]
    public void NonPartySpeaker_DoesNotResume()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        TrapDelegationManager mgr = NewManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        h.router.Dispatch(Line("Stranger says \"Trap to the n disarmed.\""));

        Assert.Null(reply);
    }

    [Fact]
    public void OwnEcho_DoesNotResume()
    {
        // "You say …" — the local character's own speech, empty speaker.
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        TrapDelegationManager mgr = NewManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        h.router.Dispatch(Line("You say \"Trap to the n disarmed.\""));

        Assert.Null(reply);
    }

    [Fact]
    public void NonTrapSayLine_DoesNotResume()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        TrapDelegationManager mgr = NewManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        h.router.Dispatch(Line("Helper says \"hello there.\""));

        Assert.Null(reply);
    }

    [Fact]
    public void Cancel_DropsPendingWatch()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        TrapDelegationManager mgr = NewManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        mgr.Cancel();
        h.router.Dispatch(Line("Helper says \"Trap to the n disarmed.\""));

        Assert.Null(reply);
    }

    [Fact]
    public void SayReply_BeforeDelegate_Ignored()
    {
        // A stray say reply with no delegation in flight must not throw or
        // resume anything.
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        TrapDelegationManager mgr = NewManager(h);

        h.router.Dispatch(Line("Helper says \"Trap to the n disarmed.\""));
        // No assertion beyond "didn't throw" — the pending callback is null.
        Assert.True(true);
    }

    [Fact]
    public void Dispose_UnsubscribesSayWatch()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        TrapDelegationManager mgr = NewManager(h);
        string? reply = null;
        mgr.Delegate("n", t => reply = t);

        mgr.Dispose();
        h.router.Dispatch(Line("Helper says \"Trap to the n disarmed.\""));

        Assert.Null(reply);
    }
}
