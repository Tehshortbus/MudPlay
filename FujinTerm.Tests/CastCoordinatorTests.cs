using System.Text;
using FujinTerm.Game.Spells;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.C — <see cref="CastCoordinator"/> wire emission, cooldown
/// gating, cast-block latch driven by server failure messages, and
/// combat-tick reset.
/// </summary>
public sealed class CastCoordinatorTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public CastCoordinator Cast { get; }
        public List<byte[]> Sent { get; } = new();
        public List<(CastFailureReason Reason, string Detail)> Failures { get; } = new();
        public List<string> Casts { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(b => Sent.Add(b));
            Cast.CastSent += line => Casts.Add(line);
            Cast.CastFailed += (r, d) => Failures.Add((r, d));
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

        public void Dispose() => Cast.Dispose();
    }

    // ----- TryCast happy path -----------------------------------------

    [Fact]
    public void TryCast_NoTarget_SendsCommandAndFiresEvent()
    {
        using Harness h = new();
        bool ok = h.Cast.TryCast("heal");

        Assert.True(ok);
        Assert.Equal("heal", h.LastSent);
        Assert.Single(h.Casts);
        Assert.Empty(h.Failures);
    }

    [Fact]
    public void TryCast_WithTarget_AppendsTarget()
    {
        using Harness h = new();
        bool ok = h.Cast.TryCast("heal", "Fujin");

        Assert.True(ok);
        Assert.Equal("heal Fujin", h.LastSent);
    }

    [Fact]
    public void TryCast_EmptySpell_NoOp()
    {
        using Harness h = new();
        Assert.False(h.Cast.TryCast(""));
        Assert.False(h.Cast.TryCast("   "));
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void TryCast_ItemCastToken_RejectedNotSentToWire()
    {
        using Harness h = new();
        // A '#'-prefixed item-cast token must never reach the wire as a raw
        // cast — it needs the equip/use/re-equip sequence instead.
        bool ok = h.Cast.TryCast("#emerald tipped crozier");

        Assert.False(ok);
        Assert.Empty(h.Sent);
        Assert.Empty(h.Casts);
        Assert.Contains(h.Failures, f => f.Detail == "item-cast-token");
    }

    [Fact]
    public void TryCast_NoWireSender_NoOp()
    {
        using Harness h = new();
        // Wipe the wire so the gate check runs without a sender.
        CastCoordinator bare = new(h.Router, h.Log);
        Assert.False(bare.TryCast("heal"));
        bare.Dispose();
    }

    // ----- recent-cast cooldown (5.5s) --------------------------------

    [Fact]
    public void SecondCast_WithinCooldown_IsBlocked()
    {
        using Harness h = new();
        Assert.True(h.Cast.TryCast("heal"));
        Assert.True(h.Cast.IsCastBlocked);

        Assert.False(h.Cast.TryCast("heal"));
        Assert.Single(h.Sent);
        Assert.Single(h.Failures);
        Assert.Equal(CastFailureReason.Blocked, h.Failures[0].Reason);
    }

    [Fact]
    public void CombatTick_ClearsCooldown_AllowsImmediateRecast()
    {
        // Real-life cadence: cast → combat tick (5s) → next round can cast.
        using Harness h = new();
        Assert.True(h.Cast.TryCast("heal"));
        Assert.True(h.Cast.IsCastBlocked);

        h.Cast.OnCombatTick();

        Assert.False(h.Cast.IsCastBlocked);
        Assert.True(h.Cast.TryCast("heal"));
        Assert.Equal(2, h.Sent.Count);
    }

    // ----- server-driven block latch ----------------------------------

    [Fact]
    public void Fizzle_BlocksAndFiresFailure()
    {
        using Harness h = new();
        h.Feed("You attempt to cast heal, but fail.");

        Assert.True(h.Cast.IsCastBlocked);
        Assert.Single(h.Failures);
        Assert.Equal(CastFailureReason.Fizzled, h.Failures[0].Reason);
    }

    [Fact]
    public void NoMana_BlocksAndFiresFailure()
    {
        using Harness h = new();
        h.Feed("You do not have enough mana to cast that spell.");

        Assert.True(h.Cast.IsCastBlocked);
        Assert.Single(h.Failures);
        Assert.Equal(CastFailureReason.NotEnoughMana, h.Failures[0].Reason);
    }

    [Fact]
    public void AlreadyCastThisRound_BlocksAndFiresFailure()
    {
        using Harness h = new();
        h.Feed("You have already cast a spell this round!");

        Assert.True(h.Cast.IsCastBlocked);
        Assert.Single(h.Failures);
        Assert.Equal(CastFailureReason.AlreadyCastThisRound, h.Failures[0].Reason);
    }

    [Fact]
    public void Interrupted_BlocksAndFiresFailure()
    {
        using Harness h = new();
        h.Feed("You lost your concentration on the spell!");

        Assert.True(h.Cast.IsCastBlocked);
        Assert.Single(h.Failures);
        Assert.Equal(CastFailureReason.Interrupted, h.Failures[0].Reason);
    }

    [Fact]
    public void Blocked_TryCast_ReturnsFalseWithoutSending()
    {
        using Harness h = new();
        h.Feed("You do not have enough mana to cast that spell.");

        Assert.False(h.Cast.TryCast("heal"));
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void CombatTick_ClearsServerBlock()
    {
        using Harness h = new();
        h.Feed("You have already cast a spell this round!");
        Assert.True(h.Cast.IsCastBlocked);

        h.Cast.OnCombatTick();
        Assert.False(h.Cast.IsCastBlocked);
        Assert.True(h.Cast.TryCast("heal"));
    }

    // ----- external-cast notification ---------------------------------

    [Fact]
    public void NotifyExternalCastSent_ArmsTheCooldown()
    {
        // CombatManager's pre-attack debuff sends `c X` directly.
        // Without NotifyExternalCastSent the coordinator would happily
        // also cast in the same round.
        using Harness h = new();
        h.Cast.NotifyExternalCastSent();
        Assert.True(h.Cast.IsCastBlocked);
        Assert.False(h.Cast.TryCast("heal"));
    }

    // ----- min recast interval (burst absorb) -------------------------

    [Fact]
    public void TwoTryCastInSameFrame_OnlyFirstLands()
    {
        // CastingDirector evaluates several candidates in a frame and
        // calls TryCast on each. The min-recast gap absorbs the burst.
        using Harness h = new();
        Assert.True(h.Cast.TryCast("heal"));
        Assert.False(h.Cast.TryCast("freedom"));      // sub-cooldown
        Assert.Single(h.Sent);
    }
}
