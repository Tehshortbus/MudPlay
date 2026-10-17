using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Party HP between `par` polls: PartyManager.AdjustMemberHp (the sole writer of the HP
// fields) and PartyHpEstimator feeding it the damage and heals seen on a member.
public sealed class PartyHpEstimatorTests
{
    private const string RaijinParRow =
        "   Raijin WuzHere                  (Priest)        [M:100%] [H: 85%]   - Backrank";

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PlayerState State { get; } = new();
        public PartyManager Party { get; }
        // The game's output through the router and the extractor the party reads.
        public ServerFeed Server { get; }
        public RoundDamageTracker Damage { get; }
        public PartyHpEstimator Estimator { get; }

        public Harness(IReadOnlyList<HealSpell>? heals = null, IEnumerable<MessageRecord>? records = null,
            IEnumerable<int>? drains = null)
        {
            DefaultPatterns.Seed(Router);
            Party = new PartyManager(Router, new PartyState()) { LocalCharacterName = "Fujin" };
            Server = new ServerFeed(Router);
            Party.AttachLineExtractor(Server.Lines);
            Damage = new RoundDamageTracker(Router, State);
            Damage.SetNameSources(
                partyNames: () => Party.State.Members.Select(m => m.Name),
                selfName: () => "Fujin");
            Damage.NoteRoomEntities(new RoomEntitiesObservation(
                "Also here: goblin, Raijin.",
                new[]
                {
                    new RoomEntity("goblin", "goblin", EntityKind.Monster, 1),
                    new RoomEntity("Raijin", "Raijin", EntityKind.Player, null),
                },
                DateTimeOffset.UnixEpoch));
            Estimator = new PartyHpEstimator(Router, Damage, Party,
                buildReader: () => new HealLineReader(heals ?? Array.Empty<HealSpell>(), records ?? Array.Empty<MessageRecord>()),
                buildDrains: () => new DrainLineSet(drains ?? Array.Empty<int>(), records ?? Array.Empty<MessageRecord>()),
                ownLevel: () => 20);
        }

        public PartyMember Member(string given) => Party.State.Members.Single(m => m.Name.StartsWith(given, StringComparison.Ordinal));

        // Raijin joins at 800 / 1000 HP (the @health exchange).
        public PartyMember JoinRaijin()
        {
            Feed("Raijin started to follow you.");
            Party.SetMemberHealthSnapshot("Raijin", 800, 1000, 50, 50, isKai: false);
            return Member("Raijin");
        }

        public void Feed(string line) => Server.Line(line);

        public void Dispose()
        {
            Estimator.Dispose();
            Damage.Dispose();
            Party.Dispose();
        }
    }

    // ----- PartyManager.AdjustMemberHp ---------------------------------

    [Fact]
    public void Adjust_KeepsAbsoluteHp_SoRoundedPercentsDontDrift()
    {
        using Harness h = new();
        PartyMember raijin = h.JoinRaijin();

        h.Party.AdjustMemberHp("Raijin", -35);   // 765
        h.Party.AdjustMemberHp("Raijin", -35);   // 730

        Assert.Equal(73, raijin.HpPercent);
        Assert.Equal(730, h.Party.HpEstimateOf(raijin).EstimateHp);
    }

    [Fact]
    public void Adjust_ClampsToMaxHp()
    {
        using Harness h = new();
        PartyMember raijin = h.JoinRaijin();

        Assert.Equal(("Raijin", 1000, 1000), h.Party.AdjustMemberHp("Raijin WuzHere", 500));
        Assert.Equal(100, raijin.HpPercent);
    }

    [Fact]
    public void Adjust_NeverWritesZeroPercent()
    {
        using Harness h = new();
        PartyMember raijin = h.JoinRaijin();

        h.Party.AdjustMemberHp("Raijin", -5000);

        Assert.Equal(0, h.Party.HpEstimateOf(raijin).EstimateHp);
        Assert.Equal(1, raijin.HpPercent);
    }

    [Fact]
    public void Adjust_SkipsSelf_Strangers_AndMembersWithoutAMaxHp()
    {
        using Harness h = new();
        h.Feed("Raijin started to follow you.");   // no @health yet

        Assert.Null(h.Party.AdjustMemberHp("Raijin", -10));
        Assert.Null(h.Party.AdjustMemberHp("Fujin", -10));
        Assert.Null(h.Party.AdjustMemberHp("goblin", -10));
        Assert.False(h.Party.HasHpEstimableMember());
    }

    [Fact]
    public void Par_ReplacesTheEstimate_AndReportsTheCorrection()
    {
        using Harness h = new();
        PartyMember raijin = h.JoinRaijin();
        (string Name, int Est, int Stated, string Source)? corrected = null;
        h.Party.HpEstimateCorrected += (n, e, s, src) => corrected = (n, e, s, src);
        h.Party.AdjustMemberHp("Raijin", -70);   // 730 → 73%

        h.Server.Line("The following people are in your travel party:");
        h.Server.Rows(new[] { RaijinParRow });

        Assert.Equal(85, raijin.HpPercent);
        Assert.Null(h.Party.HpEstimateOf(raijin).EstimateHp);
        Assert.Equal(("Raijin WuzHere", 73, 85, "par"), corrected);
        Assert.Equal(85, h.Party.HpEstimateOf(raijin).LastReading?.Percent);

        // The next step starts again from what par said: 850 - 50.
        h.Party.AdjustMemberHp("Raijin", -50);
        Assert.Equal(800, h.Party.HpEstimateOf(raijin).EstimateHp);
    }

    // ----- PartyHpEstimator --------------------------------------------

    [Fact]
    public void DamageOnAMember_ComesOffTheirHp()
    {
        using Harness h = new();
        PartyMember raijin = h.JoinRaijin();

        h.Feed("The goblin bites Raijin for 30 damage!");

        Assert.Equal(770, h.Party.HpEstimateOf(raijin).EstimateHp);
        Assert.Equal(77, raijin.HpPercent);
    }

    [Fact]
    public void DamageOnUs_LeavesTheMembersAlone()
    {
        using Harness h = new();
        PartyMember raijin = h.JoinRaijin();

        h.Feed("The goblin bites you for 30 damage!");

        Assert.Null(h.Party.HpEstimateOf(raijin).EstimateHp);
        Assert.Equal(80, raijin.HpPercent);
    }

    [Fact]
    public void HealOnAMember_GoesOnTheirHp()
    {
        using Harness h = new(
            new[] { MinorHealing() },
            new[] { Record("minor healing", 13,
                caster: "You cast {spellname} on {target}, healing {damage} damage!",
                target: "{source} casts {spellname} on you!",
                witness: "{source} casts {spellname} on {target}!") });
        PartyMember raijin = h.JoinRaijin();

        h.Feed("The goblin bites Raijin for 30 damage!");
        h.Feed("You cast minor healing on Raijin, healing 12 damage!");

        Assert.Equal(782, h.Party.HpEstimateOf(raijin).EstimateHp);
        Assert.Equal(78, raijin.HpPercent);
    }

    [Fact]
    public void PartyHeal_FromOutsideTheParty_IsIgnored()
    {
        using Harness h = new(
            new[] { new HealSpell(123, "greater healing rain", 13, new SpellFormulaInput
            {
                Number = 123, MinBase = 15, MinInc = 1, MinIncLVLs = 2, MaxBase = 35, MaxInc = 1, MaxIncLVLs = 1,
                ReqLevel = 24, Cap = 46, Abilities = new[] { new SpellAbility(18, 0) },
            }) },
            new[] { Record("greater healing rain", 123,
                caster: "You cast {spellname} on your party, healing {damage} damage!",
                target: "{source} casts {spellname} on you, healing {damage} damage!",
                witness: "{source} casts {spellname} on the room!") });
        PartyMember raijin = h.JoinRaijin();

        h.Feed("Stranger casts greater healing rain on the room!");
        Assert.Equal(80, raijin.HpPercent);

        h.Feed("You cast greater healing rain on your party, healing 40 damage!");
        Assert.Equal(840, h.Party.HpEstimateOf(raijin).EstimateHp);
    }

    // A drain heals whoever lands it by what it dealt: here Raijin's necromantic bolt,
    // whose chained drain line names only the victim and so goes to the last hitter.
    [Fact]
    public void MembersDrain_HealsThem()
    {
        using Harness h = new(
            records: new[] { Record("nebo secondary", 1246,
                caster: "{target}'s life is drained for {damage} damage!", target: "", witness: "") },
            drains: new[] { 1246 });
        PartyMember raijin = h.JoinRaijin();

        h.Feed("Raijin fires a necromantic bolt at goblin for 50 damage!");
        h.Feed("goblin's life is drained for 20 damage!");

        Assert.Equal(820, h.Party.HpEstimateOf(raijin).EstimateHp);
    }

    [Fact]
    public void CloseWounds_IsAHeal_NotDamage()
    {
        using Harness h = new(
            new[] { new HealSpell(1043, "close wounds", 2, new SpellFormulaInput
            {
                Number = 1043, MinBase = 20, MaxBase = 50, ReqLevel = 1, Cap = 65,
                Abilities = new[] { new SpellAbility(18, 0) },
            }) },
            new[] { Record("close wounds", 1043,
                caster: "You cast {spellname} on {target} closing their wounds for {damage} damage!",
                target: "{source} casts {spellname} on you, closing your wounds!",
                witness: "{source} casts {spellname} on {target}, closing their wounds!") });
        PartyMember raijin = h.JoinRaijin();

        h.Feed("You cast close wounds on Raijin closing their wounds for 30 damage!");

        Assert.Equal(830, h.Party.HpEstimateOf(raijin).EstimateHp);
    }

    private static HealSpell MinorHealing() => new(13, "minor healing", 2, new SpellFormulaInput
    {
        Number = 13, MinBase = 2, MinInc = 1, MinIncLVLs = 3, MaxBase = 8, MaxInc = 2, MaxIncLVLs = 3,
        ReqLevel = 1, Cap = 10, Abilities = new[] { new SpellAbility(18, 0) },
    });

    private static MessageRecord Record(string name, int spell, string caster, string target, string witness)
        => new(
            Id: MessageRecord.ComputeId(name, caster, target, witness, "", ""),
            Name: name,
            Flags: MessageFlags.None,
            RawFlagsHex: 0,
            CasterMessage: caster,
            TargetMessage: target,
            WitnessMessage: witness,
            AppliedMessage: "",
            AppliedEndsWith: "",
            Links: new[] { new GameDataLink("Spells", spell) });
}
