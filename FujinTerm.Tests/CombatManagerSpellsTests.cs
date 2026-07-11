using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Spells;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.A (spell extension) — <see cref="CombatManager"/> combat-spell
/// round economy: the chooser-driven cast path that suppresses weapon
/// swings, the per-round heartbeat re-cast, and the opt-in guard that keeps
/// the weapon engine unchanged until <see cref="CombatManager.SetCombatSpellCaster"/>
/// is wired.
/// </summary>
public sealed class CombatManagerSpellsTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatManager Combat { get; }
        public CastCoordinator Cast { get; }
        public List<byte[]> Sent { get; } = new();
        public CombatSettings Settings { get; set; } = new()
        {
            NormalAttackCommand = "a",
            TargetOrder = TargetOrder.Normal,
        };

        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();

        // Spell.Number → Short cast-code, feeding the per-monster override
        // resolver. An unmapped number resolves to null (unknown → fall back).
        public Dictionary<int, string> SpellShorts { get; } = new();

        public bool AutoCombatEnabled { get; set; } = true;
        public bool AutoNukeEnabled { get; set; } = true;
        public int Ma { get; set; } = 100;
        public int MaxMa { get; set; } = 100;
        public bool Sneaking { get; set; }
        public HashSet<int> SeeHidden { get; } = new();

        public Harness(bool wireCaster = true)
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(b => Sent.Add(b));
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: n => Overlays.TryGetValue(n, out MonsterOverlay? o)
                                     ? o : new MonsterOverlay(),
                party: Party,
                readSettings: () => Settings,
                isEnabled: () => AutoCombatEnabled,
                readOwnGivenName: () => "Fujin",
                post: a => a(),                          // synchronous in tests
                log: Log);
            Combat.SetWireSender(b => Sent.Add(b));
            Combat.SetBackstabHooks(() => Sneaking, n => SeeHidden.Contains(n));
            Combat.SetAutoNukeGate(() => AutoNukeEnabled);
            Combat.SetSpellShortResolver(
                n => SpellShorts.TryGetValue(n, out string? s) ? s : null);
            if (wireCaster)
                Combat.SetCombatSpellCaster(Cast, () => (Ma, MaxMa));
        }

        public void SetOverlay(int monsterNumber, MonsterAttackPriority? priority = null,
                               MonsterRelationship? relationship = null)
            => Overlays[monsterNumber] = new MonsterOverlay
            {
                Priority = priority,
                Relationship = relationship,
            };

        public void AddMonster(int number, string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                HitYou: Array.Empty<string>(),
                HitOther: Array.Empty<string>(),
                DeathLine: new[] { $"The {name} dies." },
                ArmorBlockYou: Array.Empty<string>(),
                ArmorBlockOther: Array.Empty<string>(),
                DodgeYou: Array.Empty<string>(),
                DodgeOther: Array.Empty<string>(),
                MissYou: Array.Empty<string>(),
                MissOther: Array.Empty<string>(),
                FlavorPrefixes: Array.Empty<string>(),
                AllowNoPrefix: true,
                Links: new[] { new GameDataLink("Monsters", number) }));

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        /// <summary>Mirror the AppServices tick-subscription order:
        /// coordinator clears its cooldown first, then the combat heartbeat
        /// re-decides. (CastingDirector sits between them in production but
        /// isn't under test here.)</summary>
        public void Tick()
        {
            Cast.OnCombatTick();
            Combat.OnCombatTick();
        }

        public string LastSent => Sent.Count == 0
            ? string.Empty
            : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public IEnumerable<string> AllSent =>
            Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        public void Dispose()
        {
            Combat.Dispose();
            Cast.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- opt-in guard ------------------------------------------------

    [Fact]
    public void CasterUnwired_MultiAttackConfigured_StillSwingsWeapon()
    {
        using Harness h = new(wireCaster: false);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void CasterWired_NoSpellsConfigured_StillSwingsWeapon()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    // ----- spell suppresses the weapon swing ---------------------------

    [Fact]
    public void MultiAttackQualifies_CastsSpell_NoWeaponSwing()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // The cast-code is typed directly with the target appended.
        Assert.Equal("blast giant rat", h.LastSent);
        Assert.DoesNotContain("a giant rat", h.AllSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    [Fact]
    public void MultiAttackBelowMinEnemies_FallsToWeapon()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- Auto-Nuke auto-engine gate ----------------------------------

    [Fact]
    public void AutoNukeOff_MultiAttackQualifies_FallsToWeapon()
    {
        using Harness h = new();
        h.AutoNukeEnabled = false;            // nukes disabled
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // Multi-target nuke is suppressed; the single-target weapon swing runs.
        Assert.Equal("a giant rat", h.LastSent);
        Assert.DoesNotContain("blast giant rat", h.AllSent);
    }

    [Fact]
    public void AutoNukeOff_SingleTargetAttackSpell_StillFires()
    {
        using Harness h = new();
        h.AutoNukeEnabled = false;            // nukes disabled
        // A single-target attack spell is NOT a nuke — it stays available.
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "lightning", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("lightning giant rat", h.LastSent);
    }

    [Fact]
    public void AutoNukeOff_AreaDebuff_NotOffered()
    {
        using Harness h = new();
        h.AutoNukeEnabled = false;            // nukes (incl. debuffs) disabled
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // No combat spell configured beyond the debuff → weapon swing, and the
        // in-between debuff window stays empty.
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Null(h.Combat.PickInBetweenDebuff());
    }

    // ----- per-monster spell overrides (Number → Short resolution) -----

    [Fact]
    public void AttackOverride_CastsOverrideSpell_NotConfiguredNormal()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.SpellShorts[42] = "fireball";
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 42, OverrideAttackCount = 3 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // The Spell.Number override (42) resolves to its Short and replaces the
        // configured normal-attack cast-code for this monster.
        Assert.Equal("fireball giant rat", h.LastSent);
        Assert.DoesNotContain("harm giant rat", h.AllSent);
    }

    [Fact]
    public void AttackOverride_NullCount_FallsBackToConfiguredSlot()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.SpellShorts[42] = "fireball";
        // Spell set but no count (overlay documents null = 0) → not active.
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 42 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("harm giant rat", h.LastSent);
        Assert.DoesNotContain("fireball giant rat", h.AllSent);
    }

    [Fact]
    public void AttackOverride_UnknownNumber_FallsBackToConfiguredSlot()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        // Resolver has no entry for 99 → override can't resolve → configured slot.
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 99, OverrideAttackCount = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("harm giant rat", h.LastSent);
    }

    [Fact]
    public void PreAttackOverride_OfferedAsInBetweenDebuff()
    {
        using Harness h = new();
        h.SpellShorts[7] = "curse";
        h.Overlays[1] = new MonsterOverlay { OverridePreAttackSpellId = 7, OverridePreAttackCount = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");   // engages, sets the current target

        (string Spell, string? Target)? debuff = h.Combat.PickInBetweenDebuff();
        Assert.NotNull(debuff);
        Assert.Equal("curse", debuff!.Value.Spell);
        Assert.Equal("giant rat", debuff.Value.Target);
    }

    // ----- heartbeat keeps the cast going each round -------------------

    [Fact]
    public void Heartbeat_ReCastsMultiAttack_EachRound()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");      // round 1 — initial cast
        h.Tick();                             // round 2 — heartbeat re-cast
        h.Tick();                             // round 3 — heartbeat re-cast

        Assert.Equal(3, h.AllSent.Count(s => s == "blast giant rat"));
        Assert.DoesNotContain("a giant rat", h.AllSent);
    }

    [Fact]
    public void Heartbeat_CastCapReached_FallsToWeaponOnce()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell =
            new CombatSpellSlot { SpellName = "blast", MinEnemies = 1, MaxCastsPerRoom = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");      // cast 1
        h.Tick();                             // cast 2 (hits cap)
        h.Tick();                             // cap reached → weapon

        Assert.Equal(2, h.AllSent.Count(s => s == "blast giant rat"));
        Assert.Equal("a giant rat", h.LastSent);
        // Weapon mode now — heartbeat goes quiet, no further casts.
        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void Heartbeat_ManaDrained_FallsToWeapon()
    {
        using Harness h = new();
        h.Settings.SpellManaThresholdMode = ThresholdMode.Absolute;
        h.Settings.MultiAttackSpell =
            new CombatSpellSlot { SpellName = "blast", MinEnemies = 1, MinManaPerCast = 30 };
        h.AddMonster(1, "giant rat");

        h.Ma = 50;
        h.Feed("Also here: giant rat.");      // cast (50 >= 30)
        Assert.Equal("blast giant rat", h.LastSent);

        h.Ma = 20;                            // now below the gate
        h.Tick();                             // mana too low → weapon

        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- in-between debuff bridge ------------------------------------

    [Fact]
    public void AreaDebuff_OfferedAsInBetween_OncePerRoom_CombatActionAttacks()
    {
        using Harness h = new();
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // The combat action is the attack spell; the debuff is an in-between
        // action that CastingDirector pulls from the engine (not under test
        // here — we drive the bridge directly).
        h.Feed("Also here: giant rat.");
        Assert.Equal("blast giant rat", h.LastSent);

        (string Spell, string? Target)? debuff = h.Combat.PickInBetweenDebuff();
        Assert.Equal("curse", debuff?.Spell);
        Assert.Equal("giant rat", debuff?.Target);
        h.Combat.CommitInBetweenDebuff();

        Assert.Null(h.Combat.PickInBetweenDebuff());   // once per room

        h.Tick();                                       // combat action unchanged
        Assert.Equal("blast giant rat", h.LastSent);
    }

    // ----- backstab gate -----------------------------------------------

    [Fact]
    public void BackstabPending_SuppressesSpell_SendsBackstab()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.Sneaking = true;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("bs giant rat", h.LastSent);
        Assert.DoesNotContain("blast giant rat", h.AllSent);
    }

    // ----- room clear resets the chooser bookkeeping -------------------

    [Fact]
    public void RoomCleared_ResetsCastCap_NextRoomReCasts()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell =
            new CombatSpellSlot { SpellName = "blast", MinEnemies = 1, MaxCastsPerRoom = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");      // room 1 — cast (cap 1 reached)
        Assert.Equal("blast giant rat", h.LastSent);

        h.Feed("Also here: Bob.");            // room cleared → chooser reset
        h.Tick();                             // round passes (clears cast cooldown)
        h.Feed("Also here: giant rat.");      // room 2 — cap reset, casts again

        Assert.Equal(2, h.AllSent.Count(s => s == "blast giant rat"));
    }

    // ----- damage-immunity fallback (CS-c) -----------------------------

    [Fact]
    public void SpellNoEffect_CascadesPrimaryToAlternateToWeapon()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "firebolt" };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "icebolt" };
        h.AddMonster(1, "acid slime");

        h.Feed("Also here: acid slime.");                 // primary attack spell
        Assert.Equal("firebolt acid slime", h.LastSent);

        h.Feed("Your spell has no effect on acid slime."); // firebolt immune
        h.Tick();                                          // heartbeat → alternate
        Assert.Equal("icebolt acid slime", h.LastSent);

        h.Feed("Your spell has no effect on acid slime."); // icebolt immune → weapon now
        Assert.Equal("a acid slime", h.LastSent);
    }

    [Fact]
    public void SpellNoEffect_MultiAttack_NotGated_KeepsCasting()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "acid slime");

        h.Feed("Also here: acid slime.");                 // multi-attack room spell
        Assert.Equal("blast acid slime", h.LastSent);

        // One immune mob doesn't mean the room spell isn't damaging the
        // rest — multi-attack is never marked immune.
        h.Feed("Your spell has no effect on acid slime.");
        h.Tick();
        Assert.Equal("blast acid slime", h.LastSent);
        Assert.DoesNotContain("a acid slime", h.AllSent);
    }
}
