using System.Text;
using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// CastCoordinator's wire emission, cooldown gating, the cast-block latch driven
// by server failure messages, and the combat-tick reset.
public sealed class CastCoordinatorTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public CastCoordinator Cast { get; }
        public List<byte[]> Sent { get; } = new();
        public List<(CastFailureReason Reason, string Detail, string? Spell)> Failures { get; } = new();
        public List<string> Casts { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(b => Sent.Add(b));
            Cast.CastSent += line => Casts.Add(line);
            Cast.CastFailed += (r, d, s) => Failures.Add((r, d, s));
        }

        public void Feed(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        // A prompt row with a command typed on it, the way the game shows it: the
        // prompt and its echo share the row's timestamp.
        public void FeedEcho(string command)
        {
            DateTimeOffset at = DateTimeOffset.UtcNow;
            Router.Dispatch(new LineExtractor.EmittedLine(
                "[HP=609/MA=330]:", Array.Empty<CellAttributes>(), at, IsPromptLine: true));
            Router.Dispatch(new LineExtractor.EmittedLine(
                command, Array.Empty<CellAttributes>(), at, IsPromptLine: false));
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
        bool ok = h.Cast.TryCast("heal", "MudPlay");

        Assert.True(ok);
        Assert.Equal("heal MudPlay", h.LastSent);
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

    // The room-wide refusal is also what a running room attack prints on a round with
    // nothing left to hit, so it reports the failure without latching the block.
    [Fact]
    public void NoEffectInRoom_FiresNoTargetsWithoutBlocking()
    {
        using Harness h = new();
        h.Cast.TryCast("stnk");
        h.Feed("Your spell has no effect in this room!");

        Assert.Contains(h.Failures, f => f.Reason == CastFailureReason.NoTargets);
        Assert.True(h.Cast.TryCast("hsto", bypassRoundCooldown: true));
    }

    [Fact]
    public void AlreadyCastThisRound_OnAttackSlot_BlocksAndFiresFailure()
    {
        using Harness h = new();
        h.Cast.TryCast("blast", bypassRoundCooldown: true);   // last send was on the attack slot
        h.Feed("You have already cast a spell this round!");

        Assert.True(h.Cast.IsCastBlocked);
        Assert.Contains(h.Failures, f => f.Reason == CastFailureReason.AlreadyCastThisRound);
    }

    // The refusal names no spell. A flux of ours had landed; two seconds later the user
    // typed `use coin` into the same round and the game refused THAT. Pinned on flux, the
    // buff's timer was dropped and a good roll was recast.
    [Fact]
    public void AlreadyCastThisRound_AnsweringAnotherCommand_IsNotOurCast()
    {
        using Harness h = new();
        h.Cast.TryCast("flux");
        h.FeedEcho("use coin");
        h.Feed("You have already cast a spell this round!");

        (CastFailureReason reason, _, string? spell) = Assert.Single(h.Failures);
        Assert.Equal(CastFailureReason.AlreadyCastThisRound, reason);
        Assert.Equal("use coin", spell);
        // The round's attack is a slot of its own and still goes out.
        Assert.True(h.Cast.TryCast("hsto", bypassRoundCooldown: true));
    }

    [Theory]
    [InlineData("flux")]
    [InlineData("FLUX")]
    [InlineData("glac nasty granite colossus")]
    public void AlreadyCastThisRound_AnsweringOurOwnCast_IsOurs(string echoed)
    {
        using Harness h = new();
        string code = echoed.Split(' ')[0].ToLowerInvariant();
        h.Cast.TryCast(code);
        h.FeedEcho(echoed);
        h.Feed("You have already cast a spell this round!");

        Assert.Equal(code, Assert.Single(h.Failures).Spell);
    }

    [Fact]
    public void AlreadyCastThisRound_AnsweringAnotherCommand_DoesNotBlockTheAttackSlot()
    {
        using Harness h = new();
        h.Cast.TryCast("blast", bypassRoundCooldown: true);
        h.FeedEcho("use coin");
        h.Feed("You have already cast a spell this round!");

        Assert.False(h.Cast.IsCastBlocked);
    }

    // The attack + between-round slots are independent server-side, so a between-round cast
    // rejected "already cast this round" must NOT block the round's combat attack (report
    // paradigm-20260923-103506: the attack was stranded behind the debuff's retry). The
    // failure still fires so the debuff rolls back and re-offers next round.
    [Fact]
    public void AlreadyCastThisRound_OnBetweenRoundSlot_DoesNotBlockAttackSlot()
    {
        using Harness h = new();
        h.Cast.TryCast("isto");                               // last send was on the between-round slot
        h.Feed("You have already cast a spell this round!");

        // The failure still fires (so the debuff rolls back + re-offers next round)...
        Assert.Contains(h.Failures, f => f.Reason == CastFailureReason.AlreadyCastThisRound);
        // ...but the shared block latch was NOT set, so the round's combat attack (an
        // independent attack-slot cast, which checks that latch) still goes out. Before the
        // fix this returned false — the attack was stranded behind the debuff's rejection.
        Assert.True(h.Cast.TryCast("hsto", bypassRoundCooldown: true));
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
        h.Cast.TryCast("blast", bypassRoundCooldown: true);   // attack slot, so the reject blocks
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

    [Fact]
    public void AttackAfterBetweenRoundCast_NeedsNoRecastIntervalBypass()
    {
        // A between-round cast (armr, no bypass — CastingDirector's own shape) and
        // an attack-slot cast (mmis, bypassRoundCooldown — CombatManager's shape)
        // occupy independent slots server-side (GAME_MECHANICS.md), so they're
        // paced on separate clocks: the attack landing the same instant needs no
        // recast-interval bypass at all, since it was never competing with the
        // buff's clock in the first place.
        using Harness h = new();
        Assert.True(h.Cast.TryCast("armr"));                                    // between-round buff
        Assert.True(h.Cast.TryCast("mmis", "outcast", bypassRoundCooldown: true));
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("mmis outcast", h.LastSent);
    }

    [Fact]
    public void BetweenRoundCastAfterAttack_NeedsNoRecastIntervalBypass()
    {
        // Report paradigm-20260916-113009: a fresh-engage attack (bypassRoundCooldown)
        // stamped the SAME shared clock an immediately-following emergency heal (no
        // bypass, CastingDirector's own shape) checked without one — so the heal lost
        // its round to an unrelated attack and never got a window to fire before the
        // character died. Attack-slot and between-round casts are independent slots
        // server-side and must never block each other in EITHER direction — this is
        // the reverse of AttackAfterBetweenRoundCast_NeedsNoRecastIntervalBypass above.
        using Harness h = new();
        Assert.True(h.Cast.TryCast("soul", "fat orca", bypassRoundCooldown: true)); // fresh-engage attack
        Assert.True(h.Cast.TryCast("dmer"));                                       // emergency heal, same instant
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("dmer", h.LastSent);
    }

    [Fact]
    public void RecastIntervalBypass_LetsResumeReattackLandImmediately()
    {
        // Explicitly passing both bypass flags still works (now redundant for this
        // specific cross-slot shape now that attack and between-round casts pace on
        // separate clocks — see AttackAfterBetweenRoundCast_NeedsNoRecastIntervalBypass
        // above — but bypassRecastInterval remains meaningful for a same-slot burst,
        // e.g. two attack-spell dispatches landing in the same frame).
        using Harness h = new();
        Assert.True(h.Cast.TryCast("armr"));                                    // self-buff
        Assert.True(h.Cast.TryCast("mmis", "outcast",
            bypassRoundCooldown: true, bypassRecastInterval: true));
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("mmis outcast", h.LastSent);
    }

    [Fact]
    public void RecastIntervalBypass_StillHonoursFailureLatch()
    {
        // The burst-guard bypass must not punch through the server failure latch:
        // a fizzled last cast means an instant retry would just re-fail.
        using Harness h = new();
        h.Feed("You attempt to cast heal, but fail.");
        Assert.False(h.Cast.TryCast("mmis", "outcast",
            bypassRoundCooldown: true, bypassRecastInterval: true));
        Assert.Empty(h.Sent);
    }

}
