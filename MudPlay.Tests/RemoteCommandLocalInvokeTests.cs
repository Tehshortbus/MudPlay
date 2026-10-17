using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// TryInvokeLocal — running a registered @-command from the local machine instead
// of from a chat channel. It skips the per-player permission check on purpose
// (that gate answers "may this OTHER player do this to me?", which is meaningless
// for the person holding the keyboard) but must still honour the user's own master
// switch and the unconditional hard-blocks.
public sealed class RemoteCommandLocalInvokeTests
{
    private static RemoteCommandManager Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        return new RemoteCommandManager(chat, new PartyState(), new PlayerDatabase(new ProfileService(), () => null));
    }

    [Fact]
    public void RegisteredCommand_Invokes_AndCollectsReplies()
    {
        RemoteCommandManager engine = Setup();
        engine.RegisterHandler("@ping", PlayerRemoteControls.QueryVersion,
            ctx => { ctx.Reply("pong"); ctx.Reply("again"); });

        List<string> replies = [];
        var result = engine.TryInvokeLocal("@ping", null, replies.Add);

        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok, result);
        Assert.Equal(new[] { "pong", "again" }, replies);
    }

    [Fact]
    public void NoPlayerPermissionNeeded()
    {
        // The player database is empty, so over chat this would be denied. Locally
        // it must run: the caller already has the machine.
        RemoteCommandManager engine = Setup();
        bool ran = false;
        engine.RegisterHandler("@elevated", PlayerRemoteControls.SysopCommands, _ => ran = true);

        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok,
            engine.TryInvokeLocal("@elevated", null, _ => { }));
        Assert.True(ran);
    }

    [Fact]
    public void MissingAtPrefix_IsAccepted()
    {
        RemoteCommandManager engine = Setup();
        engine.RegisterHandler("@ping", PlayerRemoteControls.QueryVersion, ctx => ctx.Reply("pong"));

        List<string> replies = [];
        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok,
            engine.TryInvokeLocal("ping", null, replies.Add));
        Assert.Equal("pong", Assert.Single(replies));
    }

    [Fact]
    public void UnknownCommand_Reported_NotSilentlySwallowed()
    {
        // The chat path stays silent on an unknown @-token because it's usually
        // ordinary chat. An API caller asked a direct question and deserves an
        // answer.
        RemoteCommandManager engine = Setup();
        Assert.Equal(RemoteCommandManager.LocalInvokeResult.UnknownCommand,
            engine.TryInvokeLocal("@nope", null, _ => { }));
    }

    [Fact]
    public void MasterDisable_IsHonoured()
    {
        // The user's own off switch for the whole subsystem outranks the local
        // caller — this isn't a permission check, it's "I turned this off".
        RemoteCommandManager engine = Setup();
        bool ran = false;
        engine.RegisterHandler("@ping", PlayerRemoteControls.QueryVersion, _ => ran = true);
        engine.MasterDisable = true;

        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Disabled,
            engine.TryInvokeLocal("@ping", null, _ => { }));
        Assert.False(ran);
    }

    // "no remote command except @auto-all on or off should work, if the master
    // switch is off": the rule is about the command, so the local route follows it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MasterSwitch_IsHonoured_AndAutoAllStillRuns(bool switchOff)
    {
        RemoteCommandManager engine = Setup();
        List<string> ran = [];
        engine.RegisterHandler("@ping", PlayerRemoteControls.QueryVersion, _ => ran.Add("@ping"));
        engine.RegisterHandler(RemoteCommandManager.MasterSwitchCommand, PlayerRemoteControls.AlterSettings,
            _ => ran.Add(RemoteCommandManager.MasterSwitchCommand));
        List<string> skipped = [];
        engine.BlockedByMasterSwitch = what => { if (switchOff) skipped.Add(what); return switchOff; };

        var ping = engine.TryInvokeLocal("@ping", null, _ => { });
        var autoAll = engine.TryInvokeLocal("@auto-all", new[] { "on" }, _ => { });

        Assert.Equal(switchOff
            ? RemoteCommandManager.LocalInvokeResult.MasterSwitchOff
            : RemoteCommandManager.LocalInvokeResult.Ok, ping);
        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok, autoAll);
        Assert.Equal(switchOff
            ? new[] { RemoteCommandManager.MasterSwitchCommand }
            : new[] { "@ping", RemoteCommandManager.MasterSwitchCommand }, ran);
        Assert.Equal(switchOff ? 1 : 0, skipped.Count);
    }

    [Fact]
    public void HardBlock_AppliesLocallyToo()
    {
        // reroll is denied by any route, to anyone. "Local" is not an exemption.
        RemoteCommandManager engine = Setup();
        bool ran = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => ran = true);

        Assert.Equal(RemoteCommandManager.LocalInvokeResult.HardBlocked,
            engine.TryInvokeLocal("@do", new[] { "reroll" }, _ => { }));
        Assert.False(ran);
    }

    [Fact]
    public void ArgsReachTheHandler()
    {
        RemoteCommandManager engine = Setup();
        IReadOnlyList<string>? seen = null;
        engine.RegisterHandler("@goto", PlayerRemoteControls.MovePlayer, ctx => seen = ctx.Args);

        engine.TryInvokeLocal("@goto", new[] { "Newhaven", "Cabin" }, _ => { });

        Assert.Equal(new[] { "Newhaven", "Cabin" }, seen);
    }

    [Fact]
    public void ThrowingHandler_ReportsFailure_InsteadOfTearingDownTheEngine()
    {
        RemoteCommandManager engine = Setup();
        engine.RegisterHandler("@boom", PlayerRemoteControls.QueryVersion,
            _ => throw new InvalidOperationException("kaboom"));

        List<string> replies = [];
        // Still Ok — the command existed and ran; the failure is surfaced to the
        // caller rather than being reported as "unknown command".
        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok,
            engine.TryInvokeLocal("@boom", null, replies.Add));
        Assert.Contains("kaboom", Assert.Single(replies));
    }

    [Fact]
    public void LocalInvocation_IsAttributedToTheApi_NotAPlayer()
    {
        RemoteCommandManager engine = Setup();
        string? sender = null;
        engine.RegisterHandler("@ping", PlayerRemoteControls.QueryVersion, ctx => sender = ctx.Sender);

        engine.TryInvokeLocal("@ping", null, _ => { });

        // So a locally-driven action stays distinguishable from a party member's
        // in the log and in any handler that echoes the caller.
        Assert.Equal(RemoteCommandManager.LocalSenderName, sender);
    }
}
