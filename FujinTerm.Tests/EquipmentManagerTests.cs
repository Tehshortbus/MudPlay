using System;
using System.Collections.Generic;
using System.Linq;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the gear-set apply core: the pure <see cref="EquipmentManager.BuildWearCommands"/>
/// / <see cref="EquipmentManager.ApplyVirtualSlots"/> diff logic and the
/// non-timer <see cref="EquipmentManager.ApplyByKeyword"/> resolution paths.
/// The paced wire-send (DispatcherTimer) is UI plumbing the headless test
/// harness doesn't pump, so the tests deliberately exercise only the apply
/// outcomes that resolve without enqueuing a physical wear sequence
/// (NotFound / NoChange / virtual-only Applied).
/// </summary>
public sealed class EquipmentManagerTests
{
    // ----- builders -------------------------------------------------------

    private static EquipmentSlotEntry Entry(EquipmentSlot slot, string? item)
        => new(slot, item);

    private static EquipmentSet Set(string keyword, string name, params EquipmentSlotEntry[] slots)
        => new() { Keyword = keyword, Name = name, Slots = slots.ToList() };

    private static InventorySnapshot SnapshotWithWorn(params string[] names)
        => InventorySnapshot.Empty with
        {
            EquippedItems = names.Select(n => new EquippedItem(n, "slot")).ToList(),
        };

    // Worn loadout keyed by real slot strings ("Weapon Hand" / "Off-Hand") —
    // what SwapWeapon diffs against.
    private static InventorySnapshot SnapshotWithSlots(params (string Slot, string Name)[] items)
        => InventorySnapshot.Empty with
        {
            EquippedItems = items.Select(i => new EquippedItem(i.Name, i.Slot)).ToList(),
        };

    // A manager wired for the immediate weapon fast path — snapshot + two-handed
    // predicate are the only inputs SwapWeapon reads.
    private static EquipmentManager SwapManager(
        InventorySnapshot snapshot, Func<string?, bool>? isTwoHanded = null)
        => new(
            readEquipment: () => new EquipmentSettings(),
            getSnapshot:   () => snapshot,
            readCombat:    () => new CombatSettings(),
            writeCombat:   _ => { },
            isTwoHanded:   isTwoHanded);

    private static List<string> Wire(EquipmentManager mgr)
        => mgr.LastSentForTests
            .Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r'))
            .ToList();

    private static EquipmentManager Manager(
        EquipmentSettings settings,
        InventorySnapshot snapshot,
        CombatSettings combat,
        Action<CombatSettings>? onWrite = null)
        => new(
            readEquipment: () => settings,
            getSnapshot:   () => snapshot,
            readCombat:    () => combat,
            writeCombat:   c => onWrite?.Invoke(c));

    private static ISet<string> Worn(params string[] names)
        => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    // ===== BuildWearCommands (pure) =====

    [Fact]
    public void BuildWearCommands_PhysicalControlledNotWorn_EmitsWearInSlotOrder()
    {
        EquipmentSet set = Set("armor", "Armor",
            Entry(EquipmentSlot.Head, "iron helm"),
            Entry(EquipmentSlot.Torso, "plate mail"));

        List<string> cmds = EquipmentManager.BuildWearCommands(set, Worn());

        Assert.Equal(new[] { "wear iron helm", "wear plate mail" }, cmds);
    }

    [Fact]
    public void BuildWearCommands_SkipsEmptyOrWhitespaceItemNames()
    {
        EquipmentSet set = Set("armor", "Armor",
            Entry(EquipmentSlot.Head, null),
            Entry(EquipmentSlot.Torso, "   "),
            Entry(EquipmentSlot.Legs, "greaves"));

        List<string> cmds = EquipmentManager.BuildWearCommands(set, Worn());

        Assert.Equal(new[] { "wear greaves" }, cmds);
    }

    [Fact]
    public void BuildWearCommands_SkipsAlreadyWornItem_CaseInsensitive()
    {
        EquipmentSet set = Set("armor", "Armor",
            Entry(EquipmentSlot.Head, "Helm"),
            Entry(EquipmentSlot.Torso, "mail"));

        List<string> cmds = EquipmentManager.BuildWearCommands(set, Worn("helm"));

        Assert.Equal(new[] { "wear mail" }, cmds);
    }

    [Fact]
    public void BuildWearCommands_ExcludesVirtualSlots()
    {
        EquipmentSet set = Set("fighting", "Fighting",
            Entry(EquipmentSlot.AlternateWeapon, "dagger"),
            Entry(EquipmentSlot.Weapon, "long sword"));

        List<string> cmds = EquipmentManager.BuildWearCommands(set, Worn());

        Assert.Equal(new[] { "wear long sword" }, cmds);
    }

    // ===== ApplyVirtualSlots (pure) =====

    [Fact]
    public void ApplyVirtualSlots_SetsAlternateWeapon_ReturnsTrue()
    {
        EquipmentSet set = Set("alt", "Alt",
            Entry(EquipmentSlot.AlternateWeapon, "long bow"));
        CombatSettings combat = new();

        bool changed = EquipmentManager.ApplyVirtualSlots(set, combat);

        Assert.True(changed);
        Assert.Equal("long bow", combat.AlternateWeapon);
    }

    [Fact]
    public void ApplyVirtualSlots_SetsAlternateOffHand_ReturnsTrue()
    {
        EquipmentSet set = Set("alt", "Alt",
            Entry(EquipmentSlot.AlternateOffHand, "buckler"));
        CombatSettings combat = new();

        bool changed = EquipmentManager.ApplyVirtualSlots(set, combat);

        Assert.True(changed);
        Assert.Equal("buckler", combat.AlternateOffHand);
    }

    [Fact]
    public void ApplyVirtualSlots_AlreadyEqual_ReturnsFalse()
    {
        EquipmentSet set = Set("alt", "Alt",
            Entry(EquipmentSlot.AlternateWeapon, "bow"));
        CombatSettings combat = new() { AlternateWeapon = "bow" };

        bool changed = EquipmentManager.ApplyVirtualSlots(set, combat);

        Assert.False(changed);
        Assert.Equal("bow", combat.AlternateWeapon);
    }

    [Fact]
    public void ApplyVirtualSlots_EmptyName_LeavesFieldUntouched()
    {
        EquipmentSet set = Set("alt", "Alt",
            Entry(EquipmentSlot.AlternateWeapon, "   "));
        CombatSettings combat = new() { AlternateWeapon = "existing" };

        bool changed = EquipmentManager.ApplyVirtualSlots(set, combat);

        Assert.False(changed);
        Assert.Equal("existing", combat.AlternateWeapon);
    }

    [Fact]
    public void ApplyVirtualSlots_IgnoresPhysicalSlots()
    {
        EquipmentSet set = Set("fighting", "Fighting",
            Entry(EquipmentSlot.Weapon, "sword"));
        CombatSettings combat = new();

        bool changed = EquipmentManager.ApplyVirtualSlots(set, combat);

        Assert.False(changed);
        Assert.Null(combat.AlternateWeapon);
        Assert.Null(combat.AlternateOffHand);
    }

    // ===== ApplyByKeyword resolution (non-timer paths) =====

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ApplyByKeyword_BlankKeyword_NotFound(string keyword)
    {
        EquipmentManager mgr = Manager(new EquipmentSettings(),
            InventorySnapshot.Empty, new CombatSettings());

        Assert.Equal(EquipResult.NotFound, mgr.ApplyByKeyword(keyword));
    }

    [Fact]
    public void ApplyByKeyword_UnknownKeyword_NotFound()
    {
        EquipmentSettings settings = new()
        {
            Sets = { Set("fighting", "Fighting", Entry(EquipmentSlot.Weapon, "sword")) },
        };
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, new CombatSettings());

        Assert.Equal(EquipResult.NotFound, mgr.ApplyByKeyword("tank"));
    }

    [Fact]
    public void ApplyByKeyword_MatchesByKeyword_VirtualOnly_AppliedAndPersistsCombat()
    {
        EquipmentSettings settings = new()
        {
            Sets = { Set("alt", "Alternate", Entry(EquipmentSlot.AlternateWeapon, "bow")) },
        };
        CombatSettings combat = new();
        CombatSettings? written = null;
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, combat,
            onWrite: c => written = c);

        // Case-insensitive keyword match.
        Assert.Equal(EquipResult.Applied, mgr.ApplyByKeyword("ALT"));
        Assert.NotNull(written);
        Assert.Equal("bow", written!.AlternateWeapon);
    }

    [Fact]
    public void ApplyByKeyword_MatchesByNameFallback_WhenKeywordDiffers()
    {
        EquipmentSettings settings = new()
        {
            // Keyword deliberately doesn't match; the set name does.
            Sets = { Set("xyz", "Fighting", Entry(EquipmentSlot.AlternateWeapon, "bow")) },
        };
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, new CombatSettings());

        Assert.Equal(EquipResult.Applied, mgr.ApplyByKeyword("fighting"));
    }

    [Fact]
    public void ApplyByKeyword_VirtualSlotAlreadyInEffect_NoChange()
    {
        EquipmentSettings settings = new()
        {
            Sets = { Set("alt", "Alternate", Entry(EquipmentSlot.AlternateWeapon, "bow")) },
        };
        CombatSettings combat = new() { AlternateWeapon = "bow" };
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, combat);

        Assert.Equal(EquipResult.NoChange, mgr.ApplyByKeyword("alt"));
    }

    [Fact]
    public void ApplyByKeyword_PhysicalItemsAllWorn_NoChange()
    {
        EquipmentSettings settings = new()
        {
            Sets = { Set("armor", "Armor", Entry(EquipmentSlot.Head, "helm")) },
        };
        EquipmentManager mgr = Manager(settings, SnapshotWithWorn("helm"), new CombatSettings());

        Assert.Equal(EquipResult.NoChange, mgr.ApplyByKeyword("armor"));
    }

    // ===== ApplyBySetId resolution (trigger coordinator entry point) =====

    private static EquipmentSet SetWithId(string id, string name, params EquipmentSlotEntry[] slots)
        => new() { Id = id, Name = name, Slots = slots.ToList() };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ApplyBySetId_BlankId_NotFound(string id)
    {
        EquipmentManager mgr = Manager(new EquipmentSettings(),
            InventorySnapshot.Empty, new CombatSettings());

        Assert.Equal(EquipResult.NotFound, mgr.ApplyBySetId(id));
    }

    [Fact]
    public void ApplyBySetId_UnknownId_NotFound()
    {
        EquipmentSettings settings = new()
        {
            Sets = { SetWithId("set-1", "Fighting", Entry(EquipmentSlot.AlternateWeapon, "bow")) },
        };
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, new CombatSettings());

        Assert.Equal(EquipResult.NotFound, mgr.ApplyBySetId("set-missing"));
    }

    [Fact]
    public void ApplyBySetId_KnownId_VirtualOnly_AppliedAndPersistsCombat()
    {
        EquipmentSettings settings = new()
        {
            Sets = { SetWithId("set-7", "Alternate", Entry(EquipmentSlot.AlternateWeapon, "bow")) },
        };
        CombatSettings combat = new();
        CombatSettings? written = null;
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, combat,
            onWrite: c => written = c);

        Assert.Equal(EquipResult.Applied, mgr.ApplyBySetId("set-7"));
        Assert.NotNull(written);
        Assert.Equal("bow", written!.AlternateWeapon);
    }

    [Fact]
    public void ApplyBySetId_IdMatchIsCaseSensitive_GuidContract()
    {
        // SetId is a GUID string compared ordinally — a case-flipped id must miss.
        EquipmentSettings settings = new()
        {
            Sets = { SetWithId("ABC", "Alternate", Entry(EquipmentSlot.AlternateWeapon, "bow")) },
        };
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, new CombatSettings());

        Assert.Equal(EquipResult.NotFound, mgr.ApplyBySetId("abc"));
    }

    [Fact]
    public void ApplyBySetId_VirtualSlotAlreadyInEffect_NoChange()
    {
        EquipmentSettings settings = new()
        {
            Sets = { SetWithId("set-7", "Alternate", Entry(EquipmentSlot.AlternateWeapon, "bow")) },
        };
        CombatSettings combat = new() { AlternateWeapon = "bow" };
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, combat);

        Assert.Equal(EquipResult.NoChange, mgr.ApplyBySetId("set-7"));
    }

    // ===== SwapWeapon (immediate combat fast path) =====
    // Unlike the paced apply, this bypasses the DispatcherTimer and writes the
    // wire synchronously, so the tests can assert directly on LastSentForTests.

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SwapWeapon_EmptyWeapon_NoOp(string? weapon)
    {
        EquipmentManager mgr = SwapManager(InventorySnapshot.Empty);

        mgr.SwapWeapon(weapon, "shield");

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void SwapWeapon_WeaponNotWorn_EquipsWeapon()
    {
        EquipmentManager mgr = SwapManager(InventorySnapshot.Empty);

        mgr.SwapWeapon("longsword", null);

        Assert.Equal(new[] { "eq longsword" }, Wire(mgr));
    }

    [Fact]
    public void SwapWeapon_WeaponAlreadyWorn_SkipsWeaponEquip_CaseInsensitive()
    {
        EquipmentManager mgr = SwapManager(SnapshotWithSlots(("Weapon Hand", "LongSword")));

        mgr.SwapWeapon("longsword", null);

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void SwapWeapon_OneHander_EquipsWeaponAndOffHand_WhenNeitherWorn()
    {
        EquipmentManager mgr = SwapManager(InventorySnapshot.Empty);

        mgr.SwapWeapon("longsword", "shield");

        Assert.Equal(new[] { "eq longsword", "eq shield" }, Wire(mgr));
    }

    [Fact]
    public void SwapWeapon_OneHander_EquipsOffHand_WhenWeaponWornButOffHandDiffers()
    {
        EquipmentManager mgr = SwapManager(SnapshotWithSlots(("Weapon Hand", "longsword")));

        mgr.SwapWeapon("longsword", "shield");

        Assert.Equal(new[] { "eq shield" }, Wire(mgr));
    }

    [Fact]
    public void SwapWeapon_OneHander_SkipsOffHand_WhenAlreadyWorn()
    {
        EquipmentManager mgr = SwapManager(
            SnapshotWithSlots(("Weapon Hand", "longsword"), ("Off-Hand", "shield")));

        mgr.SwapWeapon("longsword", "shield");

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void SwapWeapon_TwoHander_RemovesWornOffHand_ThenEquipsWeapon()
    {
        // The game refuses a two-hander wield while a hand is full, so the
        // off-hand is rem'd first — the auto-trade doesn't cover this.
        EquipmentManager mgr = SwapManager(
            SnapshotWithSlots(("Off-Hand", "shield")),
            isTwoHanded: w => w == "warhammer");

        mgr.SwapWeapon("warhammer", null);

        Assert.Equal(new[] { "rem shield", "eq warhammer" }, Wire(mgr));
    }

    [Fact]
    public void SwapWeapon_TwoHander_NoOffHandWorn_JustEquipsWeapon()
    {
        EquipmentManager mgr = SwapManager(InventorySnapshot.Empty,
            isTwoHanded: w => w == "warhammer");

        mgr.SwapWeapon("warhammer", null);

        Assert.Equal(new[] { "eq warhammer" }, Wire(mgr));
    }

    [Fact]
    public void SwapWeapon_TwoHander_IgnoresConfiguredOffHand()
    {
        // A two-hander fills both hands — the off-hand arg is never equipped.
        EquipmentManager mgr = SwapManager(InventorySnapshot.Empty,
            isTwoHanded: w => w == "warhammer");

        mgr.SwapWeapon("warhammer", "shield");

        Assert.Equal(new[] { "eq warhammer" }, Wire(mgr));
    }

    [Fact]
    public void SwapWeapon_TwoHander_AlreadyWorn_NoOp()
    {
        EquipmentManager mgr = SwapManager(
            SnapshotWithSlots(("Weapon Hand", "warhammer")),
            isTwoHanded: w => w == "warhammer");

        mgr.SwapWeapon("warhammer", null);

        Assert.Empty(mgr.LastSentForTests);
    }

    // ===== ApplyBackstabArmor (room-clear backstab armor) =====
    // Only the resolutions that short-circuit before the paced queue
    // (NotFound / NoChange) are asserted — the Applied path enqueues on the
    // DispatcherTimer the headless harness doesn't pump. The armor-only slot
    // exclusion is pinned on the pure BuildWearCommands test below.

    private static EquipmentSet BackstabSet(bool enabled, params EquipmentSlotEntry[] slots)
        => new()
        {
            Trigger = EquipTriggerType.Backstab,
            Enabled = enabled,
            Name = "Backstab",
            Slots = slots.ToList(),
        };

    [Fact]
    public void ApplyBackstabArmor_NoBackstabSet_NotFound()
    {
        EquipmentManager mgr = Manager(new EquipmentSettings(),
            InventorySnapshot.Empty, new CombatSettings());

        Assert.Equal(EquipResult.NotFound, mgr.ApplyBackstabArmor());
    }

    [Fact]
    public void ApplyBackstabArmor_DisabledSet_NotFound()
    {
        EquipmentSettings settings = new()
        {
            Sets = { BackstabSet(enabled: false, Entry(EquipmentSlot.Torso, "leather")) },
        };
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, new CombatSettings());

        Assert.Equal(EquipResult.NotFound, mgr.ApplyBackstabArmor());
    }

    [Fact]
    public void ApplyBackstabArmor_AllArmorWorn_NoChange()
    {
        EquipmentSettings settings = new()
        {
            Sets = { BackstabSet(enabled: true, Entry(EquipmentSlot.Torso, "leather")) },
        };
        EquipmentManager mgr = Manager(settings, SnapshotWithWorn("leather"), new CombatSettings());

        Assert.Equal(EquipResult.NoChange, mgr.ApplyBackstabArmor());
    }

    [Fact]
    public void ApplyBackstabArmor_OnlyHeldSlots_NoChange()
    {
        // A set naming only weapon / off-hand contributes no armor — the
        // armor-only pass leaves those to the combat engine's immediate swap.
        EquipmentSettings settings = new()
        {
            Sets =
            {
                BackstabSet(enabled: true,
                    Entry(EquipmentSlot.Weapon, "dagger"),
                    Entry(EquipmentSlot.OffHand, "buckler")),
            },
        };
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, new CombatSettings());

        Assert.Equal(EquipResult.NoChange, mgr.ApplyBackstabArmor());
    }

    [Fact]
    public void BuildWearCommands_ArmorOnly_ExcludesHeldSlots()
    {
        EquipmentSet set = Set("bs", "Backstab",
            Entry(EquipmentSlot.Weapon, "dagger"),
            Entry(EquipmentSlot.OffHand, "buckler"),
            Entry(EquipmentSlot.Torso, "leather"),
            Entry(EquipmentSlot.Head, "hood"));

        List<string> cmds = EquipmentManager.BuildWearCommands(set, Worn(), armorOnly: true);

        Assert.Equal(new[] { "wear leather", "wear hood" }, cmds);
    }
}
