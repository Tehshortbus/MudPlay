using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.PvP;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.G — <see cref="PvPManager"/> player-attack detection,
/// per-settings Action dispatch (Ignore / Flee / Hangup), single-
/// shot encounter gate, and the AutoCombat master.
/// </summary>
public sealed class PvPManagerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public PvPManager PvP { get; }
        public List<byte[]> Sent { get; } = new();
        public PvPSettings Settings { get; set; } = new();
        public bool AutoCombatEnabled { get; set; } = true;
        public List<(string Attacker, PvPSettings.Action Action)> Events { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            PvP = new PvPManager(Router, State,
                readSettings: () => Settings,
                isEnabled: () => AutoCombatEnabled,
                log: Log);
            PvP.SetWireSender(b => Sent.Add(b));
            PvP.PvPDetected += (a, act) => Events.Add((a, act));
        }

        public void Feed(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public string LastSent => Sent.Count == 0
            ? string.Empty
            : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public void Dispose() => PvP.Dispose();
    }

    // ----- detection --------------------------------------------------

    [Fact]
    public void PlayerHit_FiresDetected()
    {
        using Harness h = new();
        h.Settings.DefaultAction = PvPSettings.Action.Ignore;

        h.Feed("Bob hits you for 50 damage!");

        Assert.Single(h.Events);
        Assert.Equal("Bob", h.Events[0].Attacker);
        Assert.Equal(PvPSettings.Action.Ignore, h.Events[0].Action);
        Assert.Empty(h.Sent);     // Ignore = no wire send
    }

    [Fact]
    public void PlayerMiss_FiresDetected()
    {
        using Harness h = new();
        h.Settings.DefaultAction = PvPSettings.Action.Ignore;

        h.Feed("Bob swings at you but misses!");

        Assert.Single(h.Events);
        Assert.Equal("Bob", h.Events[0].Attacker);
    }

    [Fact]
    public void MobHit_NotPvP_NoFire()
    {
        // "The giant rat" — mob, not player. Must NOT trigger PvP.
        using Harness h = new();
        h.Settings.DefaultAction = PvPSettings.Action.Flee;

        h.Feed("The giant rat hits you for 10 damage!");

        Assert.Empty(h.Events);
        Assert.Empty(h.Sent);
    }

    // ----- Flee -------------------------------------------------------

    [Fact]
    public void Flee_SendsFleeCommand()
    {
        using Harness h = new();
        h.Settings.DefaultAction = PvPSettings.Action.Flee;

        h.Feed("Bob hits you for 50 damage!");

        Assert.Single(h.Sent);
        Assert.Equal("flee", h.LastSent);
    }

    [Fact]
    public void Flee_WithDirection_SendsRunCommand()
    {
        using Harness h = new();
        h.Settings.DefaultAction = PvPSettings.Action.Flee;
        h.Settings.FleeDirection = "north";

        h.Feed("Bob hits you for 50 damage!");

        Assert.Single(h.Sent);
        Assert.Equal("run north", h.LastSent);
    }

    // ----- Hangup -----------------------------------------------------

    [Fact]
    public void Hangup_SendsConfiguredCommand()
    {
        using Harness h = new();
        h.Settings.DefaultAction = PvPSettings.Action.Hangup;
        h.Settings.HangupCommand = "/q";

        h.Feed("Bob hits you for 50 damage!");

        Assert.Single(h.Sent);
        Assert.Equal("/q", h.LastSent);
    }

    // ----- single-shot per encounter ----------------------------------

    [Fact]
    public void RepeatedHits_OnlyFireOnce()
    {
        using Harness h = new();
        h.Settings.DefaultAction = PvPSettings.Action.Flee;

        h.Feed("Bob hits you for 50 damage!");
        h.Feed("Bob hits you for 30 damage!");
        h.Feed("Bob swings at you but misses!");

        Assert.Single(h.Sent);              // one flee
        Assert.Single(h.Events);            // one detection event
        Assert.True(h.PvP.EncounterActive);
    }

    [Fact]
    public void CombatEnds_ReArmsEncounter()
    {
        // Combat ended (InCombat=false) — the next PvP hit should
        // re-fire the action.
        using Harness h = new();
        h.Settings.DefaultAction = PvPSettings.Action.Flee;
        h.State.InCombat = true;

        h.Feed("Bob hits you for 50 damage!");
        Assert.Single(h.Sent);

        h.State.InCombat = false;
        Assert.False(h.PvP.EncounterActive);

        h.Feed("Bob hits you for 50 damage!");
        Assert.Equal(2, h.Sent.Count);
    }

    // ----- master switch ---------------------------------------------

    [Fact]
    public void AutoCombatOff_NoAction()
    {
        using Harness h = new() { AutoCombatEnabled = false };
        h.Settings.DefaultAction = PvPSettings.Action.Flee;

        h.Feed("Bob hits you for 50 damage!");

        Assert.Empty(h.Sent);
        Assert.Empty(h.Events);
    }

    // ----- reserved actions ------------------------------------------

    [Fact]
    public void Attack_Reserved_NoWireSend()
    {
        using Harness h = new();
        h.Settings.DefaultAction = PvPSettings.Action.Attack;

        h.Feed("Bob hits you for 50 damage!");

        Assert.Single(h.Events);            // detection still fires
        Assert.Empty(h.Sent);                // but reserved actions don't emit
    }

    [Fact]
    public void Chase_Reserved_NoWireSend()
    {
        using Harness h = new();
        h.Settings.DefaultAction = PvPSettings.Action.Chase;

        h.Feed("Bob hits you for 50 damage!");

        Assert.Single(h.Events);
        Assert.Empty(h.Sent);
    }
}
