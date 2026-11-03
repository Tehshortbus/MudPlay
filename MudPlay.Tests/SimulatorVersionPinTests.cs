using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using Xunit;
using Xunit.Abstractions;

namespace MudPlay.Tests;

// Area rankings saved to disk are trusted only while LoopSimulator.SimulatorVersion
// is the number they were made with. Nothing forces that number to be bumped when
// the simulation changes, so this pins the results of two small fixed runs that
// between them lean on most of what a simulated hour does: a change to any of it
// moves a figure here, and the failure says what to do.
//   the first    melee both ways, an attack spell and its mana, a lair on a respawn
//                clock, resting, the regen ticks
//   the second   a sneak opener, a self-heal, a buff kept up, meditation, and a
//                monster whose hit lands a spell
// It also pins what a kept ranking is compared by: the group hashes of one fixed
// character, and the signature of one fixed loop. Those move when what is hashed
// does (a field added to a profile or a settings section), with or without the
// simulation changing.
//
// What none of this reaches: SimCharacterBuilder and SimLevelProjection (the
// character here is written out by hand, not built from a stat screen and gear),
// RouteExpResolver.ResolveSimLap and AreaTours (the lap is written out too), and the
// walk pace handed in. A change there still needs the bump, and nothing fails.
public sealed class SimulatorVersionPinTests(ITestOutputHelper output)
{
    // The SimulatorVersion the figures below were taken at.
    private const int PinnedAt = 3;

    private const string Bump =
        "The simulation gives a different result for the same character, lap and game data. If that is intended, " +
        "bump LoopSimulator.SimulatorVersion (saved area rankings made by the old simulation are then dropped " +
        "instead of being listed as current), then set PinnedAt and the figures in this test to the new ones.";

    private const string GroupsMoved =
        "What a kept area ranking is compared by has changed for the same character: a field was added to, taken " +
        "from or re-ordered in SimCharacter, one of its profiles, or the Combat / Health / Spells settings, or the " +
        "form of a hash changed. Every ranking saved before this will be listed as changed in that group, whether " +
        "or not it is. If the simulation reads what changed, bump LoopSimulator.SimulatorVersion, so those rankings " +
        "are dropped instead of being mislabelled; if it does not, leave what was added out of the hash " +
        "(RankingCharacter.Of). Then set the hashes in this test to the new ones.";

    private void AssertPinnedAtThisVersion() => Assert.True(LoopSimulator.SimulatorVersion == PinnedAt,
        $"LoopSimulator.SimulatorVersion is {LoopSimulator.SimulatorVersion} and the figures here were pinned at {PinnedAt}: " +
        "run these tests, copy what each prints into its pin, and set PinnedAt.");

    private string Figures(LoopSimSummary got)
    {
        string figures = string.Join(" | ", got.Runs.Select(r =>
            FormattableString.Invariant($"exp {r.Exp} kills {r.Kills} laps {r.Laps} dmg {r.DamageTaken} lowHp {r.LowestHpPercent} lowMa {r.LowestManaPercent} ") +
            FormattableString.Invariant($"attack {r.AttackingSeconds:0.0} rest {r.RestingSeconds:0.0} med {r.MeditatingSeconds:0.0} move {r.MovingSeconds:0.0} ") +
            FormattableString.Invariant($"flees {r.Flees} died {r.DiedAtSeconds?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "no"} ") +
            "casts " + string.Join(",", r.Casts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}"))));
        output.WriteLine(figures);
        return figures;
    }

    private static MonsterCatalogEntry Mob(int number, int hp, int exp, params MonsterAttackSlot[] attacks) => new(
        number, $"mob{number}", Type: 0, Align: 1, Undead: false, Exp: exp, ExpMulti: 1, RegenTime: 0,
        Hp: hp, HpRegen: 0, ArmourClass: 20, DamageResist: 2, MagicRes: 10, FollowPercent: 0, CharmLevel: 0,
        CashRunic: 0, CashPlatinum: 0, CashGold: 0, CashSilver: 0, CashCopper: 0, Weapon: 0, CreateSpell: 0,
        DeathSpell: 0, BsDefense: 0, Energy: 1000, AvgDamage: 0, Attacks: attacks,
        MidSpells: Array.Empty<MonsterMidSpellSlot>(), Drops: Array.Empty<MonsterDropSlot>(),
        Abilities: Array.Empty<MonsterAbilitySlot>(), ElementalResists: new Dictionary<int, int>(),
        Magical: 0, SpellImmunity: 0, Dodge: 5, NonLiving: false, CastsElements: Array.Empty<string>());

    private static CombatSettings Combat()
    {
        var combat = new CombatSettings();
        combat.NormalAttackSpell.SpellName = "bolt";
        return combat;
    }

    private static SimCharacter Character() => new(
        RealmType.ParaMud, Level: 30, MaxHp: 180, MaxMana: 120,
        Melee: new PlayerMatchupProfile(RealmType.ParaMud, NormalAccuracy: 120, AvgWeaponDamage: 22,
            SwingsPerRound: 2.5, HasWeapon: true, DamageResist: 3, CritChancePercent: 8, AvgCritDamage: 60, MinDamage: 12, MaxDamage: 32),
        WeaponHitMagic: 2,
        Defense: new PlayerDefenseProfile(40, 12, 0, 0, false, 0, EvilLevel.Saint, 0, AcExact: 40),
        Regen: new SimRegen(_ => 2, _ => 14, extra => 3 + extra * 0.1, 6, RealmRegenProfile.ParaMud, RestReplacesStanding: true),
        Spells: new Dictionary<string, SimSpell>(StringComparer.OrdinalIgnoreCase)
        {
            ["bolt"] = new("bolt", 1, ManaPerCast: 9, FiresPerRound: 1, CastChance: 85, MinDamagePerRound: 14,
                MaxDamagePerRound: 30, UsesMagicResist: true, TypeOfResists: 1, AttType: 2, MinHeal: 0, MaxHeal: 0),
        },
        Combat: Combat(),
        Health: new HealthSettings { UseMeditateAbility = false },
        SpellSlots: new SpellsSettings(),
        Overlay: _ => new MonsterOverlay(),
        SpellShortByNumber: _ => null,
        AlignmentValue: 0);

    [Fact]
    public void ASmallFixedRunStillGivesTheFiguresPinnedForThisSimulatorVersion()
    {
        SimRoom[] lap =
        {
            new(new RoomKey(1, 1), 0, 2, new[] { 3, 4 }, 45),
            new(new RoomKey(1, 2), 0, 0, Array.Empty<int>(), 0),
            new(new RoomKey(1, 3), 4, 0, Array.Empty<int>(), 0),
            new(new RoomKey(1, 4), 0, 0, Array.Empty<int>(), 0),
        };
        var mobs = new[]
        {
            Mob(3, hp: 90, exp: 400, new MonsterAttackSlot("claw", Type: 1, Percent: 100, TruePercent: 100,
                MinDamage: 6, MaxDamage: 18, Accuracy: 90, Energy: 500, HitSpell: 0)),
            Mob(4, hp: 140, exp: 700, new MonsterAttackSlot("bite", Type: 1, Percent: 100, TruePercent: 100,
                MinDamage: 10, MaxDamage: 26, Accuracy: 110, Energy: 1000, HitSpell: 0)),
        }.ToDictionary(m => m.Number);
        var world = new SimWorld(n => mobs.GetValueOrDefault(n));

        string figures = Figures(LoopSimulator.RunMany(Character(), lap, world, secondsPerStep: 1.2, hours: 1, runs: 2));

        AssertPinnedAtThisVersion();
        Assert.True(Pinned == figures, $"{Bump}\nPinned: {Pinned}\nNow:    {figures}");
    }

    private const string Pinned =
        "exp 4700 kills 8 laps 2 dmg 577 lowHp 19 lowMa 7 attack 210.8 rest 3362.8 med 0.0 move 26.5 flees 4 died no casts bolt=47 | " +
        "exp 5500 kills 10 laps 1 dmg 601 lowHp 22 lowMa 7 attack 210.0 rest 3373.2 med 0.0 move 16.8 flees 3 died no casts bolt=48";

    // A sneaking caster: opens each fight with a stab, heals itself in the fight,
    // keeps a mana-regen blessing up, and meditates for mana.
    private static SimCharacter Sneak() => Character() with
    {
        Spells = new Dictionary<string, SimSpell>(StringComparer.OrdinalIgnoreCase)
        {
            ["bolt"] = Character().Spells["bolt"],
            ["mend"] = new("mend", 2, ManaPerCast: 7, FiresPerRound: 1, CastChance: 90, MinDamagePerRound: 0,
                MaxDamagePerRound: 0, UsesMagicResist: false, TypeOfResists: 0, AttType: 0, MinHeal: 18, MaxHeal: 34),
            ["bles"] = new("bles", 3, ManaPerCast: 12, FiresPerRound: 1, CastChance: 95, MinDamagePerRound: 0,
                MaxDamagePerRound: 0, UsesMagicResist: false, TypeOfResists: 0, AttType: 0, MinHeal: 0, MaxHeal: 0,
                DurationSeconds: 300, ManaRegenMin: 20, ManaRegenMax: 60),
        },
        Combat = SneakCombat(),
        Health = new HealthSettings
        {
            UseMeditateAbility = true, RestIfBelowMa = 35, RestMaxMa = 90, RestIfBelowHp = 45, RestMaxHp = 90,
            MinorHealCombatTrigger = 75,
        },
        SpellSlots = new SpellsSettings { MinorHealSpell = "mend" },
        Buffs = new[] { new SimBuff("bles", 10, false, false, false, null, 0, false, BlessIfAboveMa: 20) },
        Backstab = new PlayerMatchupProfile(RealmType.ParaMud, NormalAccuracy: 140, AvgWeaponDamage: 70,
            SwingsPerRound: 1, HasWeapon: true, DamageResist: 3, BackstabMin: 55, BackstabMax: 85),
        BackstabHitMagic = 2,
    };

    private static CombatSettings SneakCombat()
    {
        CombatSettings combat = Combat();
        combat.DoBackstab = true;
        return combat;
    }

    [Fact]
    public void ASecondFixedRun_WithASneakOpenerAHealABuffMeditationAndAHitSpell_StillGivesItsPinnedFigures()
    {
        SimRoom[] lap =
        {
            new(new RoomKey(1, 1), 0, 1, new[] { 5 }, 40),
            new(new RoomKey(1, 2), 0, 0, Array.Empty<int>(), 0),
            new(new RoomKey(1, 3), 0, 2, new[] { 5, 6 }, 60),
            new(new RoomKey(1, 4), 0, 0, Array.Empty<int>(), 0),
            new(new RoomKey(1, 5), 0, 0, Array.Empty<int>(), 0),
        };
        var mobs = new[]
        {
            // Its sting lands a poison that burns for half a minute.
            Mob(5, hp: 120, exp: 500, new MonsterAttackSlot("sting", Type: 1, Percent: 100, TruePercent: 100,
                MinDamage: 5, MaxDamage: 14, Accuracy: 100, Energy: 500, HitSpell: 884)),
            Mob(6, hp: 80, exp: 300, new MonsterAttackSlot("claw", Type: 1, Percent: 100, TruePercent: 100,
                MinDamage: 6, MaxDamage: 16, Accuracy: 95, Energy: 1000, HitSpell: 0)),
        }.ToDictionary(m => m.Number);
        var poison = new SimProc(3, 6, 30, AcDelta: 0, DodgeDelta: 0, AccuracyDelta: 0, Holds: false);
        var world = new SimWorld(n => mobs.GetValueOrDefault(n), HitSpell: n => n == 884 ? poison : null);

        LoopSimSummary got = LoopSimulator.RunMany(Sneak(), lap, world, secondsPerStep: 1.2, hours: 1, runs: 2);
        string figures = Figures(got);

        // The run is only a pin of those five while it does each of them.
        Assert.All(got.Runs, r =>
        {
            Assert.True(r.Casts.GetValueOrDefault("bs") > 0, "no sneak opener");
            Assert.True(r.Casts.GetValueOrDefault("mend") > 0, "no heal was cast");
            Assert.True(r.Casts.GetValueOrDefault("bles") > 0, "no buff was cast");
            Assert.True(r.MeditatingSeconds > 0, "no meditation");
        });
        LoopSimSummary unpoisoned = LoopSimulator.RunMany(Sneak(), lap, world with { HitSpell = null }, secondsPerStep: 1.2, hours: 1, runs: 2);
        Assert.NotEqual(unpoisoned.Runs.Select(r => r.DamageTaken), got.Runs.Select(r => r.DamageTaken));
        AssertPinnedAtThisVersion();
        Assert.True(PinnedSneak == figures, $"{Bump}\nPinned: {PinnedSneak}\nNow:    {figures}");
    }

    private const string PinnedSneak =
        "exp 21900 kills 51 laps 50 dmg 2881 lowHp 41 lowMa 1 attack 684.2 rest 161.6 med 2389.8 move 364.4 flees 12 died no casts a=4,bles=10,bolt=118,bs=46,mend=74 | " +
        "exp 20100 kills 47 laps 34 dmg 2754 lowHp 38 lowMa 1 attack 659.1 rest 148.0 med 2523.8 move 269.1 flees 13 died no casts bles=10,bolt=128,bs=36,mend=67";

    // ----- What a kept ranking is compared by ---------

    [Fact]
    public void TheGroupHashesOfAFixedCharacter_AndTheSignatureOfAFixedLoop_AreAsPinned()
    {
        RankingCharacter character = RankingCharacter.Of(Sneak(), "edits", RankingReach.Hash(
            new[] { new RoomKey(1, 5) }, new[] { new RoomKey(2, 9) }, classNumber: 3, raceNumber: 2, new[] { 807, 12 }));
        var loop = new Loop("Fixed", new[] { new RoomKey(1, 1), new RoomKey(1, 2) });
        var row = new AreaRank("Fixed", 30, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2,
            new LoopSimSummary(Array.Empty<LoopSimRun>()), IsLoop: true);
        string loopNow = RankingLoops.Now(new[] { row }, new[] { loop })["Fixed"];

        string hashes = string.Join(" ", RankingCharacter.Order.Select(g => $"{g}={character.Groups[g]}")
            .Append($"{RankingLoops.Group}={loopNow}"));
        output.WriteLine(hashes);

        Assert.True(PinnedGroups == hashes, $"{GroupsMoved}\nPinned: {PinnedGroups}\nNow:    {hashes}");
    }

    private const string PinnedGroups =
        "stats or gear=7300ec2bcee84536 spells=ab646fd58ac771c1 settings=51cf8ff1d8c1a414 game-data edits=edits " +
        "reach=e4155c9c19ec28b1 loops=c40c5515bd5b1716b6113fd87190266786f9cdca2ad48af747afce130533d5a3";
}
