using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

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
    // what SwapWeapon diffs against. LastUpdated is set so the snapshot reads as
    // observed: SwapWeapon early-returns on an unobserved (no-'i'-dump-yet) pack,
    // so a worn-state test has to model a real inventory to reach the diff.
    private static InventorySnapshot SnapshotWithSlots(params (string Slot, string Name)[] items)
        => InventorySnapshot.Empty with
        {
            EquippedItems = items.Select(i => new EquippedItem(i.Name, i.Slot)).ToList(),
            LastUpdated = DateTimeOffset.UtcNow,
        };

    // Observed inventory (LastUpdated set so the availability gate engages) whose
    // pack holds exactly the given carried names; nothing worn. A pack of
    // "Nothing!" models the post-death empty inventory the game reports.
    private static InventorySnapshot SnapshotHeld(params string[] carried)
        => InventorySnapshot.Empty with
        {
            CarriedItems = carried,
            LastUpdated = DateTimeOffset.UtcNow,
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

        Assert.Equal(new[] { "eq long sword" }, cmds);   // weapons take the universal eq verb
    }

    [Fact]
    public void BuildWearCommands_AvailabilityGate_SkipsGearNotInPack()
    {
        // Post-death: the loadout is in a deathpile and only a torch is carried.
        // Only held gear yields a wear; the rest would draw "You do not have X
        // left unequipped." if issued.
        EquipmentSet set = Set("armor", "Armor",
            Entry(EquipmentSlot.Head, "iron helm"),
            Entry(EquipmentSlot.Torso, "plate mail"),
            Entry(EquipmentSlot.Legs, "greaves"));

        List<string> cmds = EquipmentManager.BuildWearCommands(
            set, Worn(), availableNames: Worn("greaves"));

        Assert.Equal(new[] { "wear greaves" }, cmds);
    }

    [Fact]
    public void BuildWearCommands_NullAvailability_IssuesEveryNotWornItem()
    {
        // No 'i' parsed ⇒ availability unknown ⇒ pre-gate behaviour (issue all).
        EquipmentSet set = Set("armor", "Armor",
            Entry(EquipmentSlot.Head, "iron helm"),
            Entry(EquipmentSlot.Torso, "plate mail"));

        List<string> cmds = EquipmentManager.BuildWearCommands(set, Worn(), availableNames: null);

        Assert.Equal(new[] { "wear iron helm", "wear plate mail" }, cmds);
    }

    // ===== PrependTwoHandOffHandConflictRems (pure) =====

    private static IReadOnlyList<EquippedItem> WornSlots(params (string Slot, string Name)[] items)
        => items.Select(i => new EquippedItem(i.Name, i.Slot)).ToList();

    private static bool TwoHanded(string? w, params string[] twoHanders)
        => twoHanders.Any(t => string.Equals(w, t, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void PrependConflictRems_OffHandWornWhileTwoHander_RemsTheTwoHanderFirst()
    {
        // Case 1: swapping the Default set's 2H quarterstaff to the pre-rest set's
        // off-hand + 1H — the off-hand wear is rejected unless the 2H comes off first.
        EquipmentSet set = Set("prerest", "Pre-rest",
            Entry(EquipmentSlot.OffHand, "griffon shield"),
            Entry(EquipmentSlot.Weapon, "throwing hammers"));
        var cmds = new List<string> { "wear griffon shield", "wear throwing hammers" };

        List<string> result = EquipmentManager.PrependTwoHandOffHandConflictRems(
            set, WornSlots(("Weapon Hand", "quarterstaff")),
            w => TwoHanded(w, "quarterstaff"), cmds);

        Assert.Equal(
            new[] { "rem quarterstaff", "wear griffon shield", "wear throwing hammers" }, result);
    }

    [Fact]
    public void PrependConflictRems_TwoHanderWieldedWhileOffHandWorn_RemsTheOffHandFirst()
    {
        // Case 2 (report -142732): swapping the rest set's 1H + shield to the Default
        // set's 2H quarterstaff — "You may not ready a 2-handed weapon with your
        // griffon shield worn!" unless the off-hand comes off first.
        EquipmentSet set = Set("default", "Default",
            Entry(EquipmentSlot.Weapon, "quarterstaff"));
        var cmds = new List<string> { "wear quarterstaff" };

        List<string> result = EquipmentManager.PrependTwoHandOffHandConflictRems(
            set, WornSlots(("Weapon Hand", "throwing hammers"), ("Off-Hand", "griffon shield")),
            w => TwoHanded(w, "quarterstaff"), cmds);

        Assert.Equal(new[] { "rem griffon shield", "wear quarterstaff" }, result);
    }

    [Fact]
    public void PrependConflictRems_TwoHanderWielded_NoOffHandWorn_LeavesCommandsUnchanged()
    {
        EquipmentSet set = Set("default", "Default",
            Entry(EquipmentSlot.Weapon, "quarterstaff"));
        var cmds = new List<string> { "wear quarterstaff" };

        List<string> result = EquipmentManager.PrependTwoHandOffHandConflictRems(
            set, WornSlots(("Weapon Hand", "throwing hammers")),
            w => TwoHanded(w, "quarterstaff"), cmds);

        Assert.Equal(new[] { "wear quarterstaff" }, result);
    }

    [Fact]
    public void PrependConflictRems_OneHandedWeaponWorn_LeavesCommandsUnchanged()
    {
        EquipmentSet set = Set("prerest", "Pre-rest",
            Entry(EquipmentSlot.OffHand, "griffon shield"),
            Entry(EquipmentSlot.Weapon, "throwing hammers"));
        var cmds = new List<string> { "wear griffon shield", "wear throwing hammers" };

        List<string> result = EquipmentManager.PrependTwoHandOffHandConflictRems(
            set, WornSlots(("Weapon Hand", "long sword")), _ => false, cmds);

        Assert.Equal(new[] { "wear griffon shield", "wear throwing hammers" }, result);
    }

    [Fact]
    public void PrependConflictRems_OffHandNotBeingWorn_NoRem()
    {
        // Off-hand already on (not in the wear list) ⇒ nothing to clear the hand
        // for, so a worn two-hander is left alone.
        EquipmentSet set = Set("prerest", "Pre-rest",
            Entry(EquipmentSlot.OffHand, "griffon shield"),
            Entry(EquipmentSlot.Weapon, "throwing hammers"));
        var cmds = new List<string> { "wear throwing hammers" };

        List<string> result = EquipmentManager.PrependTwoHandOffHandConflictRems(
            set, WornSlots(("Weapon Hand", "quarterstaff")), _ => true, cmds);

        Assert.Equal(new[] { "wear throwing hammers" }, result);
    }

    [Fact]
    public void PrependConflictRems_SetHasNoOffHand_OneHandedWeapon_LeavesCommandsUnchanged()
    {
        EquipmentSet set = Set("default", "Default",
            Entry(EquipmentSlot.Weapon, "long sword"));
        var cmds = new List<string> { "wear long sword" };

        List<string> result = EquipmentManager.PrependTwoHandOffHandConflictRems(
            set, WornSlots(("Weapon Hand", "dagger"), ("Off-Hand", "buckler")), _ => false, cmds);

        Assert.Equal(new[] { "wear long sword" }, result);
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

    // Regression (report paradigm-20260831-071637): a burst of auto-fire triggers
    // re-applied the Default set several times a second, spamming the same wears
    // faster than the game could confirm them. The in-flight guard suppresses a
    // re-apply while the prior apply's wears are still outstanding, then releases
    // once they confirm.
    [Fact]
    public void ApplyByTrigger_ReapplyWhileWearsInFlight_SuppressedUntilConfirmed()
    {
        EquipmentSet def = Set("default", "Default",
            Entry(EquipmentSlot.Waist, "multicoloured sash"),
            Entry(EquipmentSlot.Feet, "adamantite chainmail boots"));
        def.Id = "def";
        def.Trigger = EquipTriggerType.Default;
        EquipmentSettings settings = new() { Sets = { def } };

        // Worn: the pre-rest items in those two slots; the set's items sit in the
        // pack (available to wear), so the Default apply diffs to two wears.
        InventorySnapshot snap = InventorySnapshot.Empty with
        {
            EquippedItems = new List<EquippedItem>
            {
                new("trollskin belt", "Waist"),
                new("trollskin boots", "Feet"),
            },
            CarriedItems = new List<string> { "multicoloured sash", "adamantite chainmail boots" },
            LastUpdated = DateTimeOffset.UtcNow,
        };
        EquipmentManager mgr = Manager(settings, snap, new CombatSettings());

        // First apply sends the two wears.
        Assert.Equal(EquipResult.Applied, mgr.ApplyByTrigger(EquipTriggerType.Default));
        Assert.Equal(
            new[] { "wear multicoloured sash", "wear adamantite chainmail boots" }, Wire(mgr));

        // Re-applied while those wears are still in flight → suppressed, no re-send.
        Assert.Equal(EquipResult.NoChange, mgr.ApplyByTrigger(EquipTriggerType.Default));

        // Once the game confirms both wears, the guard releases.
        mgr.NoteEquipSucceeded("multicoloured sash");
        mgr.NoteEquipSucceeded("adamantite chainmail boots");
        Assert.Equal(EquipResult.Applied, mgr.ApplyByTrigger(EquipTriggerType.Default));
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

    // ===== CurrentSetId tracking (Currently Equipped readout) =====

    [Fact]
    public void ApplyBySetId_RecordsCurrentSet_FiresChangedOnlyOnChange()
    {
        EquipmentSettings settings = new()
        {
            Sets =
            {
                SetWithId("set-1", "Combat", Entry(EquipmentSlot.AlternateWeapon, "bow")),
                SetWithId("set-2", "Backstab", Entry(EquipmentSlot.AlternateWeapon, "dagger")),
            },
        };
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, new CombatSettings());
        int fired = 0;
        mgr.CurrentSetChanged += () => fired++;

        Assert.Null(mgr.CurrentSetId);

        mgr.ApplyBySetId("set-1");
        Assert.Equal("set-1", mgr.CurrentSetId);
        Assert.Equal(1, fired);

        // Re-applying the same set doesn't re-fire.
        mgr.ApplyBySetId("set-1");
        Assert.Equal("set-1", mgr.CurrentSetId);
        Assert.Equal(1, fired);

        // A different set updates and fires once.
        mgr.ApplyBySetId("set-2");
        Assert.Equal("set-2", mgr.CurrentSetId);
        Assert.Equal(2, fired);
    }

    // A physical-slot apply now streams its wear burst synchronously (the paced
    // DispatcherTimer is gone — report paradigm-20260827-082305 / equip-delay
    // removal), so the whole delta lands on the wire in one call and the test can
    // assert it directly (before, the unpumped timer meant nothing was sent).
    [Fact]
    public void ApplyByKeyword_PhysicalSet_StreamsWearBurstSynchronously()
    {
        EquipmentSettings settings = new()
        {
            Sets =
            {
                Set("armor", "Armor",
                    Entry(EquipmentSlot.Head, "iron helm"),
                    Entry(EquipmentSlot.Torso, "plate mail")),
            },
        };
        EquipmentManager mgr = Manager(settings,
            SnapshotHeld("iron helm", "plate mail"), new CombatSettings());

        Assert.Equal(EquipResult.Applied, mgr.ApplyByKeyword("armor"));
        Assert.Equal(new[] { "wear iron helm", "wear plate mail" }, Wire(mgr));
    }

    [Fact]
    public void ApplyBySetId_UnknownId_LeavesCurrentSetUnchanged()
    {
        EquipmentSettings settings = new()
        {
            Sets = { SetWithId("set-1", "Combat", Entry(EquipmentSlot.AlternateWeapon, "bow")) },
        };
        EquipmentManager mgr = Manager(settings, InventorySnapshot.Empty, new CombatSettings());
        mgr.ApplyBySetId("set-1");

        mgr.ApplyBySetId("nope");   // NotFound — never reaches the apply
        Assert.Equal("set-1", mgr.CurrentSetId);
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

    [Fact]
    public void ApplyBySetId_CombatOwnsWeapon_WeaponOnlyDiff_Deferred_NoChange()
    {
        // Weapon-flap repro (paradigm-20260716-095547): the Default set's
        // combat-entry trigger (an auto-fire, fillFromInventory:false) re-wears the
        // normal weapon "throwing hammers" while the combat engine is mid-swap to
        // the per-monster alternate "shimmering longsword" — the clobber that made
        // the weapon flap. With the combat-weapon-ownership probe returning true,
        // the auto-fire defers the held slots to combat: the set's only diff is the
        // weapon, so nothing is left to apply and ApplyBySetId reports NoChange
        // instead of re-wearing over the swap. (The un-deferred path that DOES
        // issue the weapon wear is pinned by BuildWearCommands_ArmorOnly / null-
        // availability tests; it starts the paced DispatcherTimer the headless
        // harness doesn't pump, so it isn't exercised end-to-end here.)
        EquipmentSettings settings = new()
        {
            Sets = { SetWithId("set-1", "Default",
                Entry(EquipmentSlot.Weapon, "throwing hammers")) },
        };
        EquipmentManager mgr = Manager(settings,
            SnapshotWithWorn("shimmering longsword"), new CombatSettings());
        mgr.SetCombatWeaponOwnershipProbe(() => true);

        Assert.Equal(EquipResult.NoChange, mgr.ApplyBySetId("set-1"));
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
    public void SwapWeapon_UnobservedInventory_NoOp()
    {
        // Fresh login, no 'i' dumped yet: the worn loadout is unknown. MajorMUD
        // persists gear across logins, so the desired weapon is already wielded —
        // a speculative `eq` would only draw "You do not have X left unequipped."
        // The equip is deferred until the pack is observed (covered by
        // SwapWeapon_ObservedPack_EquipsWeapon_WhenHeld).
        EquipmentManager mgr = SwapManager(InventorySnapshot.Empty);

        mgr.SwapWeapon("longsword", null);

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void SwapWeapon_ObservedEmptyPack_SkipsEquip_WhenWeaponNotHeld()
    {
        // The weapon was lost to a deathpile: an observed 'i' shows an empty
        // pack, so no `eq` fires (it would only draw "You do not have X left
        // unequipped." on every combat round).
        EquipmentManager mgr = SwapManager(SnapshotHeld("Nothing!"));

        mgr.SwapWeapon("quarterstaff", null);

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void SwapWeapon_ObservedPack_EquipsWeapon_WhenHeld()
    {
        EquipmentManager mgr = SwapManager(SnapshotHeld("longsword"));

        mgr.SwapWeapon("longsword", null);

        Assert.Equal(new[] { "eq longsword" }, Wire(mgr));
    }

    [Fact]
    public void SwapWeapon_ObservedPack_EquipsHeldWeapon_ButSkipsUnheldOffHand()
    {
        EquipmentManager mgr = SwapManager(SnapshotHeld("longsword"));

        mgr.SwapWeapon("longsword", "shield");

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
        EquipmentManager mgr = SwapManager(SnapshotHeld("longsword", "shield"));

        mgr.SwapWeapon("longsword", "shield");

        Assert.Equal(new[] { "eq longsword", "eq shield" }, Wire(mgr));
    }

    [Fact]
    public void SwapWeapon_OneHander_EquipsOffHand_WhenWeaponWornButOffHandDiffers()
    {
        EquipmentManager mgr = SwapManager(
            SnapshotWithSlots(("Weapon Hand", "longsword")) with { CarriedItems = new[] { "shield" } });

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
            SnapshotWithSlots(("Off-Hand", "shield")) with { CarriedItems = new[] { "warhammer" } },
            isTwoHanded: w => w == "warhammer");

        mgr.SwapWeapon("warhammer", null);

        Assert.Equal(new[] { "rem shield", "eq warhammer" }, Wire(mgr));
    }

    [Fact]
    public void SwapWeapon_TwoHander_NoOffHandWorn_JustEquipsWeapon()
    {
        EquipmentManager mgr = SwapManager(SnapshotHeld("warhammer"),
            isTwoHanded: w => w == "warhammer");

        mgr.SwapWeapon("warhammer", null);

        Assert.Equal(new[] { "eq warhammer" }, Wire(mgr));
    }

    [Fact]
    public void SwapWeapon_TwoHander_IgnoresConfiguredOffHand()
    {
        // A two-hander fills both hands — the off-hand arg is never equipped.
        EquipmentManager mgr = SwapManager(SnapshotHeld("warhammer"),
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

    // ===== ApplyBackstabArmor (pre-move backstab armor) =====
    // Sends synchronously (a burst, not the paced queue) because it runs in the
    // pre-move sequence and must land before the sneak — so the Applied path can
    // be asserted directly on the wire. The armor-only slot exclusion is pinned
    // on the pure BuildWearCommands test below.

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
    public void ApplyBackstabArmor_ArmorDeltas_SentSynchronouslyToWire()
    {
        // Enabled set with unworn armor + a held slot: the armor deltas hit the
        // wire immediately (synchronous burst), the weapon slot is excluded.
        EquipmentSettings settings = new()
        {
            Sets =
            {
                BackstabSet(enabled: true,
                    Entry(EquipmentSlot.Weapon, "dagger"),
                    Entry(EquipmentSlot.Torso, "leather"),
                    Entry(EquipmentSlot.Head, "hood")),
            },
        };
        EquipmentManager mgr = Manager(settings, SnapshotWithWorn("hood"), new CombatSettings());

        Assert.Equal(EquipResult.Applied, mgr.ApplyBackstabArmor());
        Assert.Equal(new[] { "wear leather" }, Wire(mgr));
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

    // ===== BuildEquipCommands (inventory-aware fallback plan, pure) =====
    // The resolver / canEquip game-data lookups are stubbed so the plan logic
    // (set pass → carried fallback, distinct-name + family-capacity rules) is
    // pinned without a live GameDataCache.

    private static Func<string, EquipmentSlot?> Resolver(
        params (string Name, EquipmentSlot Slot)[] map)
    {
        var d = map.ToDictionary(x => x.Name, x => x.Slot, StringComparer.OrdinalIgnoreCase);
        return name => d.TryGetValue(name, out EquipmentSlot s) ? s : (EquipmentSlot?)null;
    }

    private static readonly Func<string, bool> EquipAll = static _ => true;

    private static IReadOnlyList<EquippedItem> WornList(
        params (string Name, string Slot)[] items)
        => items.Select(i => new EquippedItem(i.Name, i.Slot)).ToList();

    // ===== ComposePairedSlotCommands (paired finger/wrist slot-1 vs slot-2 rem) =====
    // CONFIRMED mechanic (report paradigm-20260901-130100): a `wear`/`eq` into a FULL
    // paired family evicts the FIRST-listed (slot-1) worn member; into a free slot it
    // just fills it. So a slot-1 swap rides the wear (no rem); only a slot-2 swap that
    // keeps slot 1 must rem the old ring first.

    [Fact]
    public void ComposePairedSlotCommands_Slot1Swap_RidesTheWear_NoRem()
    {
        // The reported bug: worn diamond-studded (slot 1) + gold jeweled (slot 2); the
        // set swaps slot 1 to pearl and keeps gold jeweled. `wear pearl` auto-evicts
        // the slot-1 diamond-studded ring, so NO `rem` is needed — the old code emitted
        // a redundant `rem diamond-studded ring` first.
        EquipmentSet set = Set("prerest-mana", "Pre-rest Mana",
            Entry(EquipmentSlot.Finger1, "pearl ring"),
            Entry(EquipmentSlot.Finger2, "gold jeweled ring"));
        IReadOnlyList<EquippedItem> worn = WornList(
            ("diamond-studded ring", "Finger"), ("gold jeweled ring", "Finger"));
        var wears = new List<string> { "wear pearl ring" };

        List<string> cmds = EquipmentManager.ComposePairedSlotCommands(set, worn, wears);

        Assert.Equal(new[] { "wear pearl ring" }, cmds);
    }

    [Fact]
    public void ComposePairedSlotCommands_Slot2Swap_RemsSlot2First()
    {
        // Slot 1 (pearl) is kept, slot 2 swaps gold jeweled → silver. `wear silver`
        // would auto-evict the slot-1 pearl the set keeps, so free slot 2 first.
        EquipmentSet set = Set("prerest-mana", "Pre-rest Mana",
            Entry(EquipmentSlot.Finger1, "pearl ring"),
            Entry(EquipmentSlot.Finger2, "silver ring"));
        IReadOnlyList<EquippedItem> worn = WornList(
            ("pearl ring", "Finger"), ("gold jeweled ring", "Finger"));
        var wears = new List<string> { "wear silver ring" };

        List<string> cmds = EquipmentManager.ComposePairedSlotCommands(set, worn, wears);

        Assert.Equal(new[] { "rem gold jeweled ring", "wear silver ring" }, cmds);
    }

    [Fact]
    public void ComposePairedSlotCommands_FreeSlot_JustWears_NoRem()
    {
        // Only pearl worn — a finger is free, so `wear silver` fills it; nothing to rem.
        EquipmentSet set = Set("prerest-mana", "Pre-rest Mana",
            Entry(EquipmentSlot.Finger1, "pearl ring"),
            Entry(EquipmentSlot.Finger2, "silver ring"));
        IReadOnlyList<EquippedItem> worn = WornList(("pearl ring", "Finger"));
        var wears = new List<string> { "wear silver ring" };

        List<string> cmds = EquipmentManager.ComposePairedSlotCommands(set, worn, wears);

        Assert.Equal(new[] { "wear silver ring" }, cmds);
    }

    [Fact]
    public void ComposePairedSlotCommands_BothWristsSwap_InterleavesOneRem_NotTwo()
    {
        // Both bracelets swap (worn b1,b2 → set b3,b4): `wear b3` auto-evicts slot-1 b1,
        // then `wear b4` would evict the just-worn b3, so rem the remaining odd b2
        // before it. One rem, not two (report paradigm-20260827-082305).
        EquipmentSet set = Set("mana", "Pre-rest Mana",
            Entry(EquipmentSlot.Wrist1, "b3"),
            Entry(EquipmentSlot.Wrist2, "b4"));
        IReadOnlyList<EquippedItem> worn = WornList(("b1", "Wrist"), ("b2", "Wrist"));
        var wears = new List<string> { "wear b3", "wear b4" };

        List<string> cmds = EquipmentManager.ComposePairedSlotCommands(set, worn, wears);

        Assert.Equal(new[] { "wear b3", "rem b2", "wear b4" }, cmds);
    }

    [Fact]
    public void ComposePairedSlotCommands_Slot1SwapKeepsSlot2_NoRem()
    {
        // Only Wrist1 changes (slot-1 b1 → b3); the set keeps b2 on slot 2. `wear b3`
        // auto-evicts b1, so just the wear — no rem (was the redundant-rem bug).
        EquipmentSet set = Set("mana", "Pre-rest Mana",
            Entry(EquipmentSlot.Wrist1, "b3"),
            Entry(EquipmentSlot.Wrist2, "b2"));
        IReadOnlyList<EquippedItem> worn = WornList(("b1", "Wrist"), ("b2", "Wrist"));
        var wears = new List<string> { "wear b3" };

        List<string> cmds = EquipmentManager.ComposePairedSlotCommands(set, worn, wears);

        Assert.Equal(new[] { "wear b3" }, cmds);
    }

    [Fact]
    public void ComposePairedSlotCommands_FamilyNotGaining_PassesNonFamilyWearsThrough()
    {
        // Both worn rings already match the set (family not gaining) → no rem; an
        // unrelated wear in the list passes through untouched.
        EquipmentSet set = Set("default", "Default",
            Entry(EquipmentSlot.Finger1, "b1"),
            Entry(EquipmentSlot.Finger2, "b2"));
        IReadOnlyList<EquippedItem> worn = WornList(("b1", "Finger"), ("b2", "Finger"));
        var wears = new List<string> { "wear iron helm" };

        List<string> cmds = EquipmentManager.ComposePairedSlotCommands(set, worn, wears);

        Assert.Equal(new[] { "wear iron helm" }, cmds);
    }

    // ===== realm-aware eviction (Paradigm evicts slot 1, Stock slot 2) =====

    [Fact]
    public void ComposePaired_Paradigm_Slot1Swap_KeepsSlot2_NoRem()
    {
        // report paradigm-20260903-111522: re-equipping Default, only the slot-1 ring
        // changes (silver → sunstone) and slot 2 (ivory) is kept. On Paradigm a full-
        // pair eq evicts SLOT 1 (the odd-out silver), so just the eq — no rem.
        EquipmentSet set = Set("default", "Default",
            Entry(EquipmentSlot.Wrist1, "sunstone wristband"),
            Entry(EquipmentSlot.Wrist2, "ivory bracelet"));
        IReadOnlyList<EquippedItem> worn = WornList(
            ("silver bracelet", "Wrist"),    // slot 1 (odd-out — the eq evicts it)
            ("ivory bracelet", "Wrist"));    // slot 2 (kept)
        var wears = new List<string> { "eq sunstone wristband" };

        List<string> cmds = EquipmentManager.ComposePairedSlotCommands(
            set, worn, wears, evictsFirstListed: true);

        Assert.Equal(new[] { "eq sunstone wristband" }, cmds);
    }

    [Fact]
    public void ComposePaired_Stock_Slot1Swap_KeepsSlot2_RemsFirst()
    {
        // Same swap on Stock: a full-pair eq evicts SLOT 2 (the kept ivory), so the
        // slot-1 change must rem the odd-out silver first (else it drops ivory).
        EquipmentSet set = Set("default", "Default",
            Entry(EquipmentSlot.Wrist1, "sunstone wristband"),
            Entry(EquipmentSlot.Wrist2, "ivory bracelet"));
        IReadOnlyList<EquippedItem> worn = WornList(
            ("silver bracelet", "Wrist"),    // slot 1 (odd-out)
            ("ivory bracelet", "Wrist"));    // slot 2 (kept — Stock's eq would evict this)
        var wears = new List<string> { "eq sunstone wristband" };

        List<string> cmds = EquipmentManager.ComposePairedSlotCommands(
            set, worn, wears, evictsFirstListed: false);

        Assert.Equal(new[] { "rem silver bracelet", "eq sunstone wristband" }, cmds);
    }

    [Fact]
    public void ComposePaired_Stock_Slot2Swap_KeepsSlot1_NoRem()
    {
        // On Stock the eq evicts slot 2, so changing the slot-2 ring (gold → silver)
        // while keeping slot 1 (pearl) needs just the eq — no rem.
        EquipmentSet set = Set("default", "Default",
            Entry(EquipmentSlot.Finger1, "pearl ring"),
            Entry(EquipmentSlot.Finger2, "silver ring"));
        IReadOnlyList<EquippedItem> worn = WornList(
            ("pearl ring", "Finger"),           // slot 1 (kept)
            ("gold jeweled ring", "Finger"));   // slot 2 (odd-out — Stock's eq evicts it)
        var wears = new List<string> { "eq silver ring" };

        List<string> cmds = EquipmentManager.ComposePairedSlotCommands(
            set, worn, wears, evictsFirstListed: false);

        Assert.Equal(new[] { "eq silver ring" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_EmptySet_FillsFromCarriedInOrder()
    {
        EquipmentSet set = Set("default", "Default");
        var carried = new[] { "iron helm", "leather boots" };
        Func<string, EquipmentSlot?> resolve = Resolver(
            ("iron helm", EquipmentSlot.Head), ("leather boots", EquipmentSlot.Feet));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, EquipAll);

        Assert.Equal(new[] { "wear iron helm", "wear leather boots" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_DuplicateNamedItem_EquipsOnlyDistinctPerFamily()
    {
        // Two silver bracelets + one ivory: the wrist family holds two, but the
        // duplicate name is refused — a silver + the ivory, not two silvers.
        EquipmentSet set = Set("default", "Default");
        var carried = new[] { "silver bracelet", "silver bracelet", "ivory bracelet" };
        Func<string, EquipmentSlot?> resolve = Resolver(
            ("silver bracelet", EquipmentSlot.Wrist1),
            ("ivory bracelet", EquipmentSlot.Wrist1));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, EquipAll);

        Assert.Equal(new[] { "eq silver bracelet", "eq ivory bracelet" }, cmds);   // paired items take eq
    }

    [Fact]
    public void BuildEquipCommands_PairedFamily_CapsAtTwo()
    {
        // Three distinct rings, but a hand wears only two.
        EquipmentSet set = Set("default", "Default");
        var carried = new[] { "ruby ring", "emerald ring", "sapphire ring" };
        Func<string, EquipmentSlot?> resolve = Resolver(
            ("ruby ring", EquipmentSlot.Finger1),
            ("emerald ring", EquipmentSlot.Finger1),
            ("sapphire ring", EquipmentSlot.Finger1));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, EquipAll);

        Assert.Equal(new[] { "eq ruby ring", "eq emerald ring" }, cmds);   // paired items take eq
    }

    [Fact]
    public void BuildEquipCommands_SecondPairedSlot_RemsOddWornRingFirst()
    {
        // The manual / inventory-aware path (Equip All / @equip) must also free the
        // odd worn finger before wearing the set's SECOND ring, or the game's `wear`
        // trades with the kept ring and the pair never converges — the same thrash
        // the set-only path already guards (report paradigm-20260825-103537).
        // Set wants pearl (F1) + silver (F2); worn is pearl + gold jeweled; carrying silver.
        EquipmentSet set = Set("default", "Default",
            Entry(EquipmentSlot.Finger1, "pearl ring"),
            Entry(EquipmentSlot.Finger2, "silver ring"));
        var carried = new[] { "silver ring" };
        IReadOnlyList<EquippedItem> worn = WornList(
            ("pearl ring", "Finger"), ("gold jeweled ring", "Finger"));
        Func<string, EquipmentSlot?> resolve = Resolver(("silver ring", EquipmentSlot.Finger1));

        List<string> cmds = EquipmentManager.BuildEquipCommands(set, carried, worn, resolve, EquipAll);

        // rem the odd worn ring FIRST, then eq the set's second ring onto the freed finger.
        Assert.Equal(new[] { "rem gold jeweled ring", "eq silver ring" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_Weapon_UsesEqVerb()
    {
        EquipmentSet set = Set("default", "Default");
        var carried = new[] { "long sword" };
        Func<string, EquipmentSlot?> resolve = Resolver(("long sword", EquipmentSlot.Weapon));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, EquipAll);

        Assert.Equal(new[] { "eq long sword" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_SetNamesUncarriedItem_FallsBackToCarried()
    {
        // The set wants a gold helm we don't have, but we carry an iron one — the
        // fallback fills the head slot from what's actually in the pack.
        EquipmentSet set = Set("default", "Default", Entry(EquipmentSlot.Head, "gold helm"));
        var carried = new[] { "iron helm" };
        Func<string, EquipmentSlot?> resolve = Resolver(("iron helm", EquipmentSlot.Head));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, EquipAll);

        Assert.Equal(new[] { "wear iron helm" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_SetPickCarried_WinsOverFallbackForSameFamily()
    {
        // The set's explicit head pick is carried, so it's placed and fills the
        // single head slot — the other carried head item doesn't also get worn.
        EquipmentSet set = Set("default", "Default", Entry(EquipmentSlot.Head, "mithril helm"));
        var carried = new[] { "mithril helm", "leather cap" };
        Func<string, EquipmentSlot?> resolve = Resolver(
            ("mithril helm", EquipmentSlot.Head), ("leather cap", EquipmentSlot.Head));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, EquipAll);

        Assert.Equal(new[] { "wear mithril helm" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_SkipsAlreadyWornItemAndFilledFamily()
    {
        // Head already occupied: the carried helm (same name) is skipped, and the
        // family is full so no other head item is queued — only the free slot fills.
        EquipmentSet set = Set("default", "Default");
        var carried = new[] { "helm", "leather cap", "boots" };
        Func<string, EquipmentSlot?> resolve = Resolver(
            ("helm", EquipmentSlot.Head),
            ("leather cap", EquipmentSlot.Head),
            ("boots", EquipmentSlot.Feet));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(("helm", "Head")), resolve, EquipAll);

        Assert.Equal(new[] { "wear boots" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_UnequippableGear_IsSkipped()
    {
        // canEquip rejects the cursed blade (wrong class / level / alignment); the
        // next carried weapon fills the hand instead.
        EquipmentSet set = Set("default", "Default");
        var carried = new[] { "cursed blade", "plain dagger" };
        Func<string, EquipmentSlot?> resolve = Resolver(
            ("cursed blade", EquipmentSlot.Weapon), ("plain dagger", EquipmentSlot.Weapon));
        Func<string, bool> canEquip = name => !string.Equals(name, "cursed blade", StringComparison.OrdinalIgnoreCase);

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, canEquip);

        Assert.Equal(new[] { "eq plain dagger" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_NonWearableCarried_IsSkipped()
    {
        // resolveSlot returns null for loot the realm can't wear — it never queues.
        EquipmentSet set = Set("default", "Default");
        var carried = new[] { "gold coin", "iron helm" };
        Func<string, EquipmentSlot?> resolve = Resolver(("iron helm", EquipmentSlot.Head));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, EquipAll);

        Assert.Equal(new[] { "wear iron helm" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_StackedCarriedTokens_StripCountAndEquip()
    {
        // Picking up a second identical loadout stacks the pack ("2 padded helm");
        // the game's count prefix must not stop the set's named pieces from being
        // worn. A singleton ("demonhide sandals") has no prefix and equips as-is.
        EquipmentSet set = Set("default", "Default",
            Entry(EquipmentSlot.Head, "padded helm"),
            Entry(EquipmentSlot.Torso, "padded vest"),
            Entry(EquipmentSlot.Feet, "demonhide sandals"));
        var carried = new[] { "2 padded helm", "2 padded vest", "demonhide sandals" };
        Func<string, EquipmentSlot?> resolve = Resolver(
            ("padded helm", EquipmentSlot.Head),
            ("padded vest", EquipmentSlot.Torso),
            ("demonhide sandals", EquipmentSlot.Feet));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, EquipAll);

        Assert.Equal(
            new[] { "wear padded helm", "wear padded vest", "wear demonhide sandals" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_StackedCarried_FallbackStripsCount()
    {
        // Empty set → pure fallback path: a stacked carried token still resolves
        // to its slot after the count is stripped.
        EquipmentSet set = Set("default", "Default");
        var carried = new[] { "2 padded helm" };
        Func<string, EquipmentSlot?> resolve = Resolver(("padded helm", EquipmentSlot.Head));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, EquipAll);

        Assert.Equal(new[] { "wear padded helm" }, cmds);
    }

    // ===== unwearable-slot blocks =====

    private static EquipmentSet SetWithId(string id, params EquipmentSlotEntry[] slots)
        => new() { Id = id, Keyword = "k", Name = "N", Slots = slots.ToList() };

    // A manager wired for the block machinery: the restriction predicate decides
    // which item names are "unwearable"; resolve/canEquip round out the inventory
    // path but the set-only apply used by these tests doesn't need them.
    private static EquipmentManager BlockManager(
        EquipmentSettings settings, InventorySnapshot snapshot, Func<string, bool> restrictsEquip)
        => new(
            readEquipment: () => settings,
            getSnapshot:   () => snapshot,
            readCombat:    () => new CombatSettings(),
            writeCombat:   _ => { },
            canEquipItem:  static _ => true,
            restrictsEquip: restrictsEquip);

    [Fact]
    public void BuildWearCommands_SkipsBlockedNames()
    {
        EquipmentSet set = Set("armor", "Armor",
            Entry(EquipmentSlot.Head, "iron helm"),
            Entry(EquipmentSlot.Torso, "cursed plate"));

        List<string> cmds = EquipmentManager.BuildWearCommands(
            set, Worn(), blockedNames: Worn("cursed plate"));

        Assert.Equal(new[] { "wear iron helm" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_SkipsBlockedSetPick()
    {
        EquipmentSet set = Set("armor", "Armor",
            Entry(EquipmentSlot.Head, "iron helm"),
            Entry(EquipmentSlot.Torso, "cursed plate"));
        var carried = new[] { "iron helm", "cursed plate" };
        Func<string, EquipmentSlot?> resolve = Resolver(
            ("iron helm", EquipmentSlot.Head), ("cursed plate", EquipmentSlot.Torso));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, EquipAll, blockedNames: Worn("cursed plate"));

        Assert.Equal(new[] { "wear iron helm" }, cmds);
    }

    [Fact]
    public void BuildEquipCommands_BlockedItemNotRefilledByFallback()
    {
        // The set doesn't name the piece, but it's carried and fits a slot — the
        // fallback would fill it. A block must keep it out of the fallback too.
        EquipmentSet set = Set("armor", "Armor");
        var carried = new[] { "cursed plate" };
        Func<string, EquipmentSlot?> resolve = Resolver(("cursed plate", EquipmentSlot.Torso));

        List<string> cmds = EquipmentManager.BuildEquipCommands(
            set, carried, WornList(), resolve, EquipAll, blockedNames: Worn("cursed plate"));

        Assert.Empty(cmds);
    }

    [Fact]
    public void RefreshBlocksForSet_RestrictedItem_BlocksAndAnnounces()
    {
        EquipmentSet set = SetWithId("s1", Entry(EquipmentSlot.Torso, "evil cuirass"));
        EquipmentManager mgr = BlockManager(
            new EquipmentSettings { Sets = { set } }, InventorySnapshot.Empty,
            restrictsEquip: n => n == "evil cuirass");

        EquipmentManager.EquipBlock? announced = null;
        mgr.SlotBlockedAnnounced += b => announced = b;

        mgr.RefreshBlocksForSet(set, announce: true);

        Assert.True(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));
        Assert.NotNull(announced);
        Assert.Equal("evil cuirass", announced!.Value.ItemName);
    }

    [Fact]
    public void RefreshBlocksForSet_SilentSeed_DoesNotAnnounce()
    {
        EquipmentSet set = SetWithId("s1", Entry(EquipmentSlot.Torso, "evil cuirass"));
        EquipmentManager mgr = BlockManager(
            new EquipmentSettings { Sets = { set } }, InventorySnapshot.Empty,
            restrictsEquip: _ => true);

        int announces = 0;
        mgr.SlotBlockedAnnounced += _ => announces++;

        mgr.RefreshBlocksForSet(set, announce: false);

        Assert.True(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));
        Assert.Equal(0, announces);
    }

    [Fact]
    public void RefreshBlocksForSet_ItemBecomesWearable_ClearsProactiveBlock()
    {
        EquipmentSet set = SetWithId("s1", Entry(EquipmentSlot.Torso, "evil cuirass"));
        bool restricted = true;
        EquipmentManager mgr = BlockManager(
            new EquipmentSettings { Sets = { set } }, InventorySnapshot.Empty,
            restrictsEquip: _ => restricted);

        mgr.RefreshBlocksForSet(set);
        Assert.True(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));

        restricted = false;                 // e.g. alignment returned
        mgr.RefreshBlocksForSet(set);
        Assert.False(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));
    }

    [Fact]
    public void Apply_RestrictedSetItem_IsBlockedAndNotSent()
    {
        EquipmentSet set = SetWithId("s1",
            Entry(EquipmentSlot.Head, "iron helm"),
            Entry(EquipmentSlot.Torso, "evil cuirass"));
        EquipmentManager mgr = BlockManager(
            new EquipmentSettings { Sets = { set } }, InventorySnapshot.Empty,
            restrictsEquip: n => n == "evil cuirass");

        mgr.ApplyBySetId("s1");

        Assert.Contains("wear iron helm", Wire(mgr));
        Assert.DoesNotContain("wear evil cuirass", Wire(mgr));
        Assert.True(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));
    }

    [Fact]
    public void NoteWearRefused_BlocksTheAttemptedArmorSlot()
    {
        // Nothing restricted up front, so the wear is sent; the game then refuses
        // it (a stale-alignment EP-zap the client couldn't predict).
        EquipmentSet set = SetWithId("s1", Entry(EquipmentSlot.Torso, "evil cuirass"));
        EquipmentManager mgr = BlockManager(
            new EquipmentSettings { Sets = { set } }, InventorySnapshot.Empty,
            restrictsEquip: _ => false);

        mgr.ApplyBySetId("s1");
        Assert.Contains("wear evil cuirass", Wire(mgr));

        mgr.NoteWearRefused();

        Assert.True(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));
    }

    [Fact]
    public void NoteEquipSucceeded_DequeuesSoNextRefusalBlocksTheNextPiece()
    {
        EquipmentSet set = SetWithId("s1",
            Entry(EquipmentSlot.Head, "iron helm"),
            Entry(EquipmentSlot.Torso, "evil cuirass"));
        EquipmentManager mgr = BlockManager(
            new EquipmentSettings { Sets = { set } }, InventorySnapshot.Empty,
            restrictsEquip: _ => false);

        mgr.ApplyBySetId("s1");                 // sends both wears, oldest first

        mgr.NoteEquipSucceeded("iron helm");    // helm worn OK → drop it from pending
        mgr.NoteWearRefused();                  // refusal now maps to the cuirass

        Assert.False(mgr.IsSlotBlocked("s1", EquipmentSlot.Head));
        Assert.True(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));
    }

    [Fact]
    public void NoteWeaponRefused_BlocksWeaponSlotNotArmor()
    {
        EquipmentSet set = SetWithId("s1",
            Entry(EquipmentSlot.Torso, "evil cuirass"),
            Entry(EquipmentSlot.Weapon, "unholy blade"));
        EquipmentManager mgr = BlockManager(
            new EquipmentSettings { Sets = { set } }, InventorySnapshot.Empty,
            restrictsEquip: _ => false);

        mgr.ApplyBySetId("s1");

        mgr.NoteWeaponRefused();

        Assert.True(mgr.IsSlotBlocked("s1", EquipmentSlot.Weapon));
        Assert.False(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));
    }

    [Fact]
    public void ServerConfirmedBlock_IsStickyAcrossRefresh_ButClearedByClearBlock()
    {
        EquipmentSet set = SetWithId("s1", Entry(EquipmentSlot.Torso, "evil cuirass"));
        EquipmentManager mgr = BlockManager(
            new EquipmentSettings { Sets = { set } }, InventorySnapshot.Empty,
            restrictsEquip: _ => false);   // stub says wearable, but the game refused

        mgr.ApplyBySetId("s1");
        mgr.NoteWearRefused();
        Assert.True(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));

        // A re-eval that finds the item "wearable" must NOT lift a game-confirmed
        // refusal — only the user editing the slot clears it.
        mgr.RefreshBlocksForSet(set);
        Assert.True(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));

        mgr.ClearBlock("s1", EquipmentSlot.Torso);
        Assert.False(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));
    }

    [Fact]
    public void ResetBlocks_ClearsEverything()
    {
        EquipmentSet set = SetWithId("s1", Entry(EquipmentSlot.Torso, "evil cuirass"));
        EquipmentManager mgr = BlockManager(
            new EquipmentSettings { Sets = { set } }, InventorySnapshot.Empty,
            restrictsEquip: _ => true);

        mgr.RefreshBlocksForSet(set);
        Assert.True(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));

        mgr.ResetBlocks();
        Assert.False(mgr.IsSlotBlocked("s1", EquipmentSlot.Torso));
    }
}
