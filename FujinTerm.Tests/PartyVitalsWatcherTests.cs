using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for <see cref="PartyVitalsWatcher"/>: it asserts the
/// <see cref="MovementCoordinator.PartyVitalsGate"/> while another party
/// member's HP% is below the configured threshold and clears it once
/// everyone recovers — excluding the self row and members whose vitals
/// haven't been observed yet (HpPercent == 0).
/// </summary>
public sealed class PartyVitalsWatcherTests
{
    private sealed class Harness : IDisposable
    {
        public PartyState Party { get; } = new();
        public PartySettings Cfg { get; } = new() { WaitIfMemberBelowPercent = 50 };
        public MovementCoordinator Coordinator { get; } = new();
        public PartyVitalsWatcher Watcher { get; }

        public Harness()
        {
            Party.IsInParty = true;
            Watcher = new PartyVitalsWatcher(Party, Coordinator, () => Cfg);
        }

        public PartyMember Add(string name, int hpPercent, bool isSelf = false)
        {
            PartyMember m = new() { Name = name, IsSelf = isSelf, HpPercent = hpPercent };
            Party.Members.Add(m);
            return m;
        }

        public bool GateAsserted =>
            Coordinator.AssertedGates.Contains(MovementCoordinator.PartyVitalsGate);

        public void Dispose() => Watcher.Dispose();
    }

    [Fact]
    public void MemberBelowThreshold_AssertsGate()
    {
        using Harness h = new();
        h.Add("Raijin", hpPercent: 30);

        Assert.True(h.GateAsserted);
        Assert.True(h.Coordinator.IsPaused);
    }

    [Fact]
    public void MemberRecovers_ClearsGate()
    {
        using Harness h = new();
        PartyMember m = h.Add("Raijin", hpPercent: 30);
        Assert.True(h.GateAsserted);

        m.HpPercent = 80;

        Assert.False(h.GateAsserted);
        Assert.False(h.Coordinator.IsPaused);
    }

    [Fact]
    public void HealthyMember_DoesNotAssert()
    {
        using Harness h = new();
        h.Add("Raijin", hpPercent: 90);

        Assert.False(h.GateAsserted);
    }

    [Fact]
    public void SelfRow_Ignored()
    {
        using Harness h = new();
        h.Add("Fujin", hpPercent: 10, isSelf: true);

        Assert.False(h.GateAsserted);
    }

    [Fact]
    public void UnobservedMember_DoesNotAssert()
    {
        using Harness h = new();
        // Just-joined member — HpPercent still 0 (no par / @health yet).
        h.Add("Raijin", hpPercent: 0);

        Assert.False(h.GateAsserted);
    }

    [Fact]
    public void ThresholdZero_NeverAsserts()
    {
        using Harness h = new();
        h.Cfg.WaitIfMemberBelowPercent = 0;
        h.Add("Raijin", hpPercent: 5);
        h.Watcher.Evaluate();

        Assert.False(h.GateAsserted);
    }

    [Fact]
    public void NotInParty_NeverAsserts()
    {
        using Harness h = new();
        h.Party.IsInParty = false;
        h.Add("Raijin", hpPercent: 5);
        h.Watcher.Evaluate();

        Assert.False(h.GateAsserted);
    }

    [Fact]
    public void MemberRemoved_ClearsGate()
    {
        using Harness h = new();
        PartyMember m = h.Add("Raijin", hpPercent: 20);
        Assert.True(h.GateAsserted);

        h.Party.Members.Remove(m);

        Assert.False(h.GateAsserted);
    }

    [Fact]
    public void OneHurtAmongHealthy_AssertsUntilAllRecover()
    {
        using Harness h = new();
        h.Add("Ally", hpPercent: 95);
        PartyMember hurt = h.Add("Raijin", hpPercent: 25);
        Assert.True(h.GateAsserted);

        hurt.HpPercent = 60;

        Assert.False(h.GateAsserted);
    }
}
