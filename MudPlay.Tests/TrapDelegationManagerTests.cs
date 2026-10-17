using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Coverage for TrapDelegationManager — the party-member
// trap-delegation path. Verifies capability resolution (class main gate /
// race secondary, excluding self + invitees), race-capture-on-join via
// look, the .@trap say broadcast, and resume-watch on a
// party member's say reply. Each test seeds an isolated game-data set so
// the suite never touches the user's real Data folder.
public sealed class TrapDelegationManagerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 12, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public TrapDelegationManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-trapdel-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ----- Fixtures -------------------------------------------------------

    // Seed a game-data set: "Ninja" class + "Gnome" race grant traps
    // (codes 40 / 1002); "Mage" + "Human" don't. Returns an active cache.
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

    // Build router (with default patterns) + party + players + game data.
    // The manager is constructed by the caller so tests control whether
    // PartyManager.MemberFollowConfirmed fires before or
    // after construction.
    private (MessageRouter router, PartyManager party, PlayerDatabase players, GameDataCache gameData) Harness(
        string localName = "Forged")
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyManager party = new(router, new PartyState()) { LocalCharacterName = localName };
        PlayerDatabase players = new(new ProfileService(), () => null);
        GameDataCache gameData = SeededCache();
        return (router, party, players, gameData);
    }

    // What the manager put on the wire, through the sender the client binds.
    private readonly List<byte[]> _sent = new();

    private TrapDelegationManager NewManager(
        (MessageRouter router, PartyManager party, PlayerDatabase players, GameDataCache gameData) h)
    {
        TrapDelegationManager mgr = new(h.party, h.players, h.gameData, h.router);
        mgr.SetWireSender(_sent.Add);
        return mgr;
    }

    private string Sent(int index) =>
        Encoding.Latin1.GetString(_sent[index]);

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
        h.players.RecordLook("Helper", "Gnome", "Mage", null, null, Now.UtcDateTime);

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

        Assert.Equal("look Helper\r", Sent(0));
    }

    [Fact]
    public void OnMemberJoined_NoLook_WhenRaceAlreadyKnown()
    {
        var h = Harness();
        h.players.RecordLook("Helper", "Human", null, null, null, Now.UtcDateTime);
        TrapDelegationManager mgr = NewManager(h);

        h.router.Dispatch(Line("Helper started to follow you."));

        Assert.Empty(_sent);
    }

    [Fact]
    public void OnMemberJoined_NoLook_WhenClassAlreadyGrantsTraps()
    {
        // Member seen in `par` first (class known) before the follow
        // confirmation — class settles capability, no race probe needed.
        var h = Harness();
        TrapDelegationManager mgr = NewManager(h);
        ServerFeed feed = new(h.router);
        h.party.AttachLineExtractor(feed.Lines);
        feed.Line("The following people are in your travel party:");
        feed.Rows(new[]
        {
            "  Helper                          (Ninja)         [M:100%] [H:100%]   - Frontrank",
            "",
        });

        h.router.Dispatch(Line("Helper started to follow you."));

        Assert.Empty(_sent);
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

        Assert.Empty(_sent);
    }

    [Fact]
    public void OnMemberJoined_Looks_WhenReformNotSettling()
    {
        // Outside a reform (predicate false) the ordinary race probe still fires.
        var h = Harness();
        TrapDelegationManager mgr = NewManager(h);
        mgr.IsPartyReformSettling = () => false;

        h.router.Dispatch(Line("Helper started to follow you."));

        Assert.Equal("look Helper\r", Sent(0));
    }

    // ===== Delegation broadcast ==========================================

    [Fact]
    public void Delegate_BroadcastsTrapOnSay()
    {
        var h = Harness();
        TrapDelegationManager mgr = NewManager(h);

        mgr.Delegate("north", _ => { });

        Assert.Equal(".@trap north\r", Sent(_sent.Count - 1));
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
        h.router.Dispatch(Line("Helper says \"Couldn't disarm the trap to the n (5 attempts).\""));

        Assert.Equal("Couldn't disarm the trap to the n (5 attempts).", reply);
    }

    // ===== The acceptance, the result, and nobody answering ==============

    // A one-shot timer the test fires by hand.
    private sealed class FakeDelay : IDisposable
    {
        public Action? Callback;
        public TimeSpan Delay;
        public bool Disposed;
        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            Delay = delay;
            Callback = callback;
            Disposed = false;
            return this;
        }
        public void Dispose() => Disposed = true;
        public void Fire() { if (!Disposed) Callback?.Invoke(); }
    }

    private static (TrapDelegationManager mgr, FakeDelay delay) NewTimedManager(
        (MessageRouter router, PartyManager party, PlayerDatabase players, GameDataCache gameData) h)
    {
        FakeDelay delay = new();
        return (new TrapDelegationManager(h.party, h.players, h.gameData, h.router, null, delay.Schedule), delay);
    }

    // Every line the disarming client can end a request with is read by the asker,
    // in the { } a remote reply arrives in: a wording the asker didn't know left
    // its walk waiting on an answer it had been given.
    [Theory]
    [InlineData("{Trap to the n disarmed.}")]
    [InlineData("{Trap to the n disarmed 12s ago.}")]
    [InlineData("{Trap to the n already disarmed.}")]
    [InlineData("{No trap to the n to disarm.}")]
    [InlineData("{No trap to the n to disarm (failed 5 times; taking it as clear).}")]
    [InlineData("{Couldn't disarm the trap to the n (5 attempts).}")]
    [InlineData("{Trap flow stopped.}")]
    public void EveryEndingTheDisarmerSends_EndsTheWait(string said)
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        TrapDelegationManager mgr = NewManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        h.router.Dispatch(Line($"Helper says (to Forged) \"{said}\""));

        Assert.Equal(said, reply);
    }

    // A sneaking member answers by telepath so its sneak holds.
    [Fact]
    public void ATelepathedResult_EndsTheWait()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        TrapDelegationManager mgr = NewManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        h.router.Dispatch(Line("Helper telepaths: {Attempting to disarm trap n.}"));
        Assert.Null(reply);
        h.router.Dispatch(Line("Helper telepaths: {Trap to the n disarmed.}"));

        Assert.Equal("{Trap to the n disarmed.}", reply);
    }

    [Fact]
    public void AnAcceptance_IsNotAResult_AndStopsTheNobodyAnsweredTimer()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        var (mgr, delay) = NewTimedManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        Assert.Equal(TrapDelegationManager.AcceptWindow, delay.Delay);

        h.router.Dispatch(Line("Helper says (to Forged) \"{Attempting to disarm trap n.}\""));
        Assert.Null(reply);
        Assert.True(delay.Disposed);
        Assert.Contains("Helper", mgr.Describe());

        // However long the disarm takes (a rest, several tries), the wait goes on.
        delay.Callback!();
        Assert.Null(reply);

        h.router.Dispatch(Line("Helper says (to Forged) \"{Trap to the n disarmed.}\""));
        Assert.Equal("{Trap to the n disarmed.}", reply);
        Assert.Equal("idle", mgr.Describe());
    }

    [Fact]
    public void NobodyAccepting_TellsTheWalkSo()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        var (mgr, delay) = NewTimedManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        delay.Fire();

        Assert.Equal(TrapReply.Unanswered, reply);
        Assert.Equal(TrapReplyOutcome.Unanswered, TrapReply.Read(reply));
    }

    [Fact]
    public void WithTwoAtTheTrap_OneGivingUpDoesNotEndIt_TheFirstClearDoes()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        h.router.Dispatch(Line("Second started to follow you."));
        var (mgr, _) = NewTimedManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        h.router.Dispatch(Line("Helper says (to Forged) \"{Attempting to disarm trap n.}\""));
        h.router.Dispatch(Line("Second says (to Forged) \"{Attempting to disarm trap n.}\""));

        h.router.Dispatch(Line("Helper says (to Forged) \"{Couldn't disarm the trap to the n (5 attempts).}\""));
        Assert.Null(reply);

        h.router.Dispatch(Line("Second says (to Forged) \"{Trap to the n disarmed.}\""));
        Assert.Equal("{Trap to the n disarmed.}", reply);
    }

    [Fact]
    public void TheOnlyOneAtTheTrapGivingUp_EndsIt()
    {
        var h = Harness();
        h.router.Dispatch(Line("Helper started to follow you."));
        var (mgr, _) = NewTimedManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        h.router.Dispatch(Line("Helper says (to Forged) \"{Attempting to disarm trap n.}\""));
        h.router.Dispatch(Line("Helper says (to Forged) \"{Couldn't disarm the trap to the n (5 attempts).}\""));

        Assert.Equal(TrapReplyOutcome.Failed, TrapReply.Read(reply));
    }

    [Fact]
    public void Cancel_StopsTheTimer_AndALateFireDoesNothing()
    {
        var h = Harness();
        var (mgr, delay) = NewTimedManager(h);

        string? reply = null;
        mgr.Delegate("n", t => reply = t);
        mgr.Cancel();

        Assert.True(delay.Disposed);
        delay.Callback!();
        Assert.Null(reply);
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

        Assert.Equal("idle", mgr.Describe());
        Assert.Empty(_sent);
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
