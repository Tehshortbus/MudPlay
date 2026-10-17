using System.Text;
using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The wire sniffer for a hand-typed gear command, and the flag that tells a typed line
// from one the client sends for itself.
public sealed class OutboundGearObserverTests
{
    private static List<string> Observe(params string[] commands)
    {
        List<string> seen = new();
        OutboundGearObserver obs = new(seen.Add);
        foreach (string command in commands)
            obs.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r\n"));
        return seen;
    }

    // Report paradigm-20261009-122342 typed `Eq phoenix`. The short forms are the
    // game's own (GAME_MECHANICS "Command words and abbreviations").
    [Theory]
    [InlineData("Eq phoenix")]
    [InlineData("eq phoenix feather")]
    [InlineData("equip sword")]
    [InlineData("wear cloak")]
    [InlineData("wea cloak")]
    [InlineData("wield sword")]
    [InlineData("rem ring")]
    [InlineData("remove ring 2")]
    public void GearCommandWithAnItem_IsSeen(string command)
    {
        Assert.Equal(new[] { command }, Observe(command));
    }

    [Theory]
    [InlineData("eq")]               // nothing named: wears nothing
    [InlineData("rem ")]
    [InlineData("e")]                // east
    [InlineData("re ring")]          // `re` is no command
    [InlineData("wealth")]
    [InlineData("weal coins")]       // `weal`…`wealth` is wealth
    [InlineData("wiel sword")]       // `wield` has no short form on record
    [InlineData("rest")]
    [InlineData("get ring")]
    [InlineData("a giant rat")]
    public void AnythingElse_IsNot(string command)
    {
        Assert.Empty(Observe(command));
    }

    [Fact]
    public void WrappedSender_MarksItsSendsAsTheClientsOwn_TypedLinesAreNot()
    {
        EngineSendGate gate = new();
        List<bool> duringSend = new();
        void Wire(byte[] _) => duringSend.Add(gate.SendingClientCommand);
        Action<byte[]> engine = gate.WrapEngineSender(Wire);

        Wire(Encoding.Latin1.GetBytes("eq phoenix\r"));        // typed: straight to the wire
        engine(Encoding.Latin1.GetBytes("eq amber sceptre\r")); // the client's own
        gate.ReplayLastClientCommand();                         // a fumble's re-send of it
        Wire(Encoding.Latin1.GetBytes("rem ring\r"));

        Assert.Equal(new[] { false, true, true, false }, duringSend);
        Assert.False(gate.SendingClientCommand);
    }

    // ----- the user's own commands, relayed by the client (user, 2026-10-09:
    // "re-attack immediately if auto-combat is on") -------------------------

    // The wire as MainWindowViewModel.SendUserInput runs it for the gear observer.
    private sealed class Wire
    {
        public EngineSendGate Gate { get; } = new();
        public List<string> GearSeen { get; } = new();
        public Action<byte[]> Engine { get; }
        public Action<byte[]> UserCommands { get; }
        private readonly OutboundGearObserver _observer;

        public Wire()
        {
            _observer = new OutboundGearObserver(GearSeen.Add);
            Engine = Gate.WrapEngineSender(Send);
            UserCommands = Gate.MarkUserCommands(Engine);
        }

        public void Send(byte[] data)
        {
            if (Gate.SendingUsersOwnCommand) _observer.ObserveOutbound(data);
        }

        public void Typed(string line) => Send(Encoding.Latin1.GetBytes(line + "\r\n"));
    }

    [Fact]
    public void Macro_GearStep_IsTheUsersOwn_TheOtherStepsAreNot()
    {
        // A macro bound to a key: "rem ring;eq ring of power;a rat".
        Wire wire = new();
        MacroDispatcher macros = new(new MacroStore());
        macros.SetSender(wire.UserCommands);

        macros.FireMacro(new Macro("F5", Ctrl: false, Shift: false, Alt: false,
            Command: "rem ring;eq ring of power;a rat", Enabled: true));

        Assert.Equal(new[] { "rem ring", "eq ring of power" }, wire.GearSeen);
    }

    [Theory]
    [InlineData("wear phoenix feather")]   // a trigger's response
    [InlineData("eq amber sceptre")]       // an event's Command action
    public void ASendThroughTheMarkedUserCommandSender_IsTheUsersOwn(string command)
    {
        // The sender MainWindowViewModel marks as the user's own, the one
        // TriggerEngine.SendResponse and EventManager.ExecuteCommand are bound to.
        Wire wire = new();
        wire.UserCommands(Encoding.Latin1.GetBytes(command + "\r"));

        Assert.Equal(new[] { command }, wire.GearSeen);
        Assert.True(wire.Gate.SendingUsersOwnCommand);    // nothing in flight afterwards: back to typed
    }

    [Fact]
    public void EngineGearCommands_AreNotTheUsersOwn_EvenBesideAMarkedSend()
    {
        // The Equipment Manager's swap, an item cast's eq / use / eq, a fumble's
        // re-send: each has its own re-attack and must not arm a second one.
        Wire wire = new();
        wire.Engine(Encoding.Latin1.GetBytes("eq platinum sceptre\r"));
        wire.UserCommands(Encoding.Latin1.GetBytes("say hello\r"));
        wire.Engine(Encoding.Latin1.GetBytes("eq amber sceptre\r"));
        wire.Gate.ReplayLastClientCommand();

        Assert.Empty(wire.GearSeen);
    }

    [Fact]
    public void MarkedSender_HeldByTheGate_SendsNothing()
    {
        // Marking a command the user's doesn't get it past a hold (the trainer form).
        Wire wire = new();
        wire.Gate.Hold("test");
        wire.UserCommands(Encoding.Latin1.GetBytes("eq sword\r"));

        Assert.Empty(wire.GearSeen);
    }
}
