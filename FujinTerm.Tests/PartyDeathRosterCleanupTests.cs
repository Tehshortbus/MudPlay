using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// The leader-side roster-cleanup bridge: when we're running an automated route
/// and an active party member dies (turning into a phantom
/// <see cref="PartyMember.IsInvited"/> par slot), it uninvites that slot once the
/// <see cref="MovementCoordinator.CombatGate"/> clears so the loop doesn't stall
/// on the PartyInviteGate waiting for a corpse to "join". Every action is doubly
/// bounded — we only record a death for a name that was ACTIVE in our roster, and
/// only send the uninvite once that same name shows as invited.
/// </summary>
public sealed class PartyDeathRosterCleanupTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PartyState Party { get; } = new();
        public PartyManager Manager { get; }
        public MovementCoordinator Coord { get; } = new();
        public PartyDeathRosterCleanup Cleanup { get; }

        public bool MovementActive { get; set; } = true;
        public DateTimeOffset Now { get; set; } = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Party.IsInParty = true;
            Party.SelfIsLeader = true;
            Manager = new PartyManager(Router, Party);
            Cleanup = new PartyDeathRosterCleanup(
                Router, Party, Manager, Coord,
                isMovementActive: () => MovementActive)
            {
                NowProvider = () => Now,
            };
        }

        public PartyMember Add(string name, bool isInvited = false, bool isSelf = false)
        {
            PartyMember m = new() { Name = name, IsSelf = isSelf, IsInvited = isInvited };
            Party.Members.Add(m);
            return m;
        }

        public void Died(string name) => Feed($"{name} has died.");

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        // The bridge routes every uninvite through PartyManager.Uninvite, which
        // records the wire bytes even with no sender bound.
        public IReadOnlyList<string> Uninvited =>
            Manager.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToList();

        public void Dispose()
        {
            Cleanup.Dispose();
            Manager.Dispose();
        }
    }

    [Fact]
    public void ActiveMemberDies_ThenShowsInvited_Uninvites()
    {
        using Harness h = new();
        PartyMember m = h.Add("Raijin");           // active member

        h.Died("Raijin");                          // records the death
        Assert.Empty(h.Uninvited);                 // not yet invited — nothing sent

        m.IsInvited = true;                        // par flips them to the phantom slot
        Assert.Contains("uninvite Raijin\r", h.Uninvited);
    }

    [Fact]
    public void DefersUntilCombatClears()
    {
        using Harness h = new();
        PartyMember m = h.Add("Raijin");
        h.Coord.AssertGate(MovementCoordinator.CombatGate);

        h.Died("Raijin");
        m.IsInvited = true;
        Assert.Empty(h.Uninvited);                 // room not clear yet

        h.Coord.ClearGate(MovementCoordinator.CombatGate);
        Assert.Contains("uninvite Raijin\r", h.Uninvited);
    }

    [Fact]
    public void NotLeader_NeverRecords()
    {
        using Harness h = new();
        h.Party.SelfIsLeader = false;
        PartyMember m = h.Add("Raijin");

        h.Died("Raijin");
        m.IsInvited = true;

        Assert.Empty(h.Uninvited);
    }

    [Fact]
    public void MovementIdle_NeverRecords()
    {
        using Harness h = new();
        h.MovementActive = false;
        PartyMember m = h.Add("Raijin");

        h.Died("Raijin");
        m.IsInvited = true;

        Assert.Empty(h.Uninvited);
    }

    [Fact]
    public void PendingRecruitDies_NeverUninvited()
    {
        // A still-pending invite was invited from the start (never active), so the
        // active->invited transition never happened — the death is not ours to act
        // on and no uninvite fires even though they show invited.
        using Harness h = new();
        h.Add("Newbie", isInvited: true);          // pending recruit from the start

        h.Died("Newbie");
        Assert.Empty(h.Uninvited);
    }

    [Fact]
    public void RandoOrMobDies_NeverUninvited()
    {
        // "<Name> has died." fires for randoms and mobs too. A name that isn't in
        // our roster is never recorded.
        using Harness h = new();
        h.Add("Raijin");

        h.Died("SomeRando");
        Assert.Empty(h.Uninvited);
    }

    [Fact]
    public void SelfDies_NeverUninvited()
    {
        using Harness h = new();
        h.Add("Fujin", isSelf: true);

        h.Died("Fujin");
        Assert.Empty(h.Uninvited);
    }

    [Fact]
    public void ExpiredDeath_NeverUninvites()
    {
        // A recorded death that hasn't surfaced as an invited slot within the
        // window is dropped, not left to fire late.
        using Harness h = new();
        PartyMember m = h.Add("Raijin");

        h.Died("Raijin");
        h.Now += h.Cleanup.CleanupWindow + TimeSpan.FromSeconds(1);
        m.IsInvited = true;                        // flip arrives too late

        Assert.Empty(h.Uninvited);
    }

    [Fact]
    public void GivenNameMatch_AcrossChatAndParForms()
    {
        // Par may carry a family suffix ("Raijin WuzHere") while the death line is
        // the given name only. Roster matching is by given name on both sides.
        using Harness h = new();
        PartyMember m = h.Add("Raijin WuzHere");

        h.Died("Raijin");
        m.IsInvited = true;

        Assert.Contains("uninvite Raijin\r", h.Uninvited);
    }

    [Fact]
    public void UninvitesOnlyOnce()
    {
        using Harness h = new();
        PartyMember m = h.Add("Raijin");

        h.Died("Raijin");
        m.IsInvited = true;
        Assert.Single(h.Uninvited);

        // A further gate churn must not re-send — the pending record is consumed.
        h.Coord.AssertGate(MovementCoordinator.CombatGate);
        h.Coord.ClearGate(MovementCoordinator.CombatGate);
        Assert.Single(h.Uninvited);
    }

    [Fact]
    public void Dispose_StopsReactingToDeath()
    {
        using Harness h = new();
        PartyMember m = h.Add("Raijin");
        h.Cleanup.Dispose();

        h.Died("Raijin");
        m.IsInvited = true;

        Assert.Empty(h.Uninvited);
    }
}
