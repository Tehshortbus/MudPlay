using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Coverage — per-character avoided + stash room state,
// IRoomFilter contract, profile-event lifecycle, and the round-trip
// through CharacterProfile so the next session sees the same list.
public sealed class MovementFilterTests
{
    private static (ProfileService Profile, MovementFilter Filter) NewPair()
    {
        ProfileService profile = TestProfiles.Service();
        MovementFilter filter = new(profile);
        return (profile, filter);
    }

    [Fact]
    public void FreshFilter_IsEmpty()
    {
        (_, MovementFilter filter) = NewPair();
        Assert.False(filter.IsAvoided(new RoomKey(1, 1)));
        Assert.False(filter.IsStash(new RoomKey(1, 1)));
        Assert.Empty(filter.Avoided);
        Assert.Empty(filter.Stash);
    }

    [Fact]
    public void ProfileLoaded_HydratesFromPersistedLists()
    {
        ProfileService profile = TestProfiles.Service();
        CharacterProfile draft = profile.LoadDefaultProfile();
        draft.AvoidedRooms = new() { new RoomRef(1, 5), new RoomRef(2, 9) };
        draft.StashRooms   = new() { new RoomRef(3, 7) };

        // Subscribing the filter after the profile is already loaded
        // is the AppServices wiring order — the ctor must catch it.
        MovementFilter filter = new(profile);

        Assert.True(filter.IsAvoided(new RoomKey(1, 5)));
        Assert.True(filter.IsAvoided(new RoomKey(2, 9)));
        Assert.False(filter.IsAvoided(new RoomKey(2, 8)));
        Assert.True(filter.IsStash(new RoomKey(3, 7)));
    }

    [Fact]
    public void ProfileClosed_ClearsBothSets()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        profile.LoadDefaultProfile();
        filter.MarkAvoided(new RoomKey(1, 1));
        filter.MarkStash(new RoomKey(1, 2));

        profile.Close();

        Assert.Empty(filter.Avoided);
        Assert.Empty(filter.Stash);
    }

    [Fact]
    public void MarkAvoided_NoProfile_IsNoOp()
    {
        (_, MovementFilter filter) = NewPair();
        filter.MarkAvoided(new RoomKey(1, 1));
        Assert.False(filter.IsAvoided(new RoomKey(1, 1)));
    }

    [Fact]
    public void MarkAvoided_AddsToSet_AndMirrorsBackToProfile()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        CharacterProfile draft = profile.LoadDefaultProfile();

        filter.MarkAvoided(new RoomKey(1, 5));

        Assert.True(filter.IsAvoided(new RoomKey(1, 5)));
        Assert.NotNull(draft.AvoidedRooms);
        Assert.Single(draft.AvoidedRooms);
        Assert.Equal(1, draft.AvoidedRooms![0].Map);
        Assert.Equal(5, draft.AvoidedRooms[0].Room);
    }

    [Fact]
    public void MarkAvoided_Idempotent()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        CharacterProfile draft = profile.LoadDefaultProfile();

        filter.MarkAvoided(new RoomKey(1, 5));
        filter.MarkAvoided(new RoomKey(1, 5));

        Assert.Single(filter.Avoided);
        Assert.Single(draft.AvoidedRooms!);
    }

    [Fact]
    public void UnmarkAvoided_RemovesFromBothInMemoryAndProfile()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        CharacterProfile draft = profile.LoadDefaultProfile();

        filter.MarkAvoided(new RoomKey(1, 5));
        filter.MarkAvoided(new RoomKey(2, 9));

        filter.UnmarkAvoided(new RoomKey(1, 5));

        Assert.False(filter.IsAvoided(new RoomKey(1, 5)));
        Assert.True(filter.IsAvoided(new RoomKey(2, 9)));
        Assert.Single(draft.AvoidedRooms!);
        Assert.Equal(2, draft.AvoidedRooms![0].Map);
    }

    [Fact]
    public void UnmarkAvoided_NotMarked_IsNoOp()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        profile.LoadDefaultProfile();

        int events = 0;
        filter.AvoidedChanged += () => events++;

        filter.UnmarkAvoided(new RoomKey(1, 5));

        Assert.Equal(0, events);
    }

    [Fact]
    public void MarkStash_TracksSeparateSet_FromAvoided()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        CharacterProfile draft = profile.LoadDefaultProfile();

        filter.MarkAvoided(new RoomKey(1, 1));
        filter.MarkStash(new RoomKey(1, 2));

        Assert.True(filter.IsAvoided(new RoomKey(1, 1)));
        Assert.False(filter.IsStash(new RoomKey(1, 1)));
        Assert.True(filter.IsStash(new RoomKey(1, 2)));
        Assert.False(filter.IsAvoided(new RoomKey(1, 2)));

        Assert.Single(draft.AvoidedRooms!);
        Assert.Single(draft.StashRooms!);
    }

    [Fact]
    public void Mark_Fires_ChangedEvents()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        profile.LoadDefaultProfile();

        int avoidedFires = 0;
        int stashFires = 0;
        filter.AvoidedChanged += () => avoidedFires++;
        filter.StashChanged   += () => stashFires++;

        filter.MarkAvoided(new RoomKey(1, 1));
        filter.MarkStash(new RoomKey(1, 2));
        filter.UnmarkAvoided(new RoomKey(1, 1));
        filter.UnmarkStash(new RoomKey(1, 2));

        Assert.Equal(2, avoidedFires);
        Assert.Equal(2, stashFires);
    }

    // ----- ReplaceAll: the Modify-Avoid-Rooms dialog Save-commit path -----

    [Fact]
    public void ReplaceAll_SetsBothSets_AndPersistsToProfile()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        CharacterProfile draft = profile.LoadDefaultProfile();

        filter.ReplaceAll(
            avoided: new[] { new RoomKey(1, 5), new RoomKey(2, 9) },
            stash:   new[] { new RoomKey(3, 7) });

        Assert.True(filter.IsAvoided(new RoomKey(1, 5)));
        Assert.True(filter.IsAvoided(new RoomKey(2, 9)));
        Assert.True(filter.IsStash(new RoomKey(3, 7)));
        Assert.Equal(2, draft.AvoidedRooms!.Count);
        Assert.Single(draft.StashRooms!);
    }

    [Fact]
    public void ReplaceAll_DropsEntriesNotInNewSets()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        profile.LoadDefaultProfile();
        filter.MarkAvoided(new RoomKey(1, 1));
        filter.MarkAvoided(new RoomKey(1, 2));
        filter.MarkStash(new RoomKey(1, 3));

        // Save a working copy that keeps only 1/1 avoided and drops the stash.
        filter.ReplaceAll(avoided: new[] { new RoomKey(1, 1) }, stash: Array.Empty<RoomKey>());

        Assert.True(filter.IsAvoided(new RoomKey(1, 1)));
        Assert.False(filter.IsAvoided(new RoomKey(1, 2)));
        Assert.Empty(filter.Stash);
    }

    [Fact]
    public void ReplaceAll_MoveRoomBetweenSets()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        profile.LoadDefaultProfile();
        filter.MarkAvoided(new RoomKey(4, 4));

        // Editor removed the avoid row and added the same room as a stash.
        filter.ReplaceAll(avoided: Array.Empty<RoomKey>(), stash: new[] { new RoomKey(4, 4) });

        Assert.False(filter.IsAvoided(new RoomKey(4, 4)));
        Assert.True(filter.IsStash(new RoomKey(4, 4)));
    }

    [Fact]
    public void ReplaceAll_FiresBothChangedEvents()
    {
        (ProfileService profile, MovementFilter filter) = NewPair();
        profile.LoadDefaultProfile();

        int avoidedFires = 0, stashFires = 0;
        filter.AvoidedChanged += () => avoidedFires++;
        filter.StashChanged   += () => stashFires++;

        filter.ReplaceAll(avoided: new[] { new RoomKey(1, 1) }, stash: new[] { new RoomKey(1, 2) });

        Assert.Equal(1, avoidedFires);
        Assert.Equal(1, stashFires);
    }

    [Fact]
    public void ReplaceAll_NoProfile_IsNoOp()
    {
        (_, MovementFilter filter) = NewPair();
        filter.ReplaceAll(avoided: new[] { new RoomKey(1, 1) }, stash: Array.Empty<RoomKey>());
        Assert.Empty(filter.Avoided);
        Assert.Empty(filter.Stash);
    }

    // Loading another character drops the old one's rooms and takes the new one's.
    [Fact]
    public void ProfileSwap_RehydratesIntoFreshSet()
    {
        string bbs = "test-filterbbs-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.ProfileFolder(bbs, "Second"));
            JsonStore.Save(AppPaths.CharacterProfileFile(bbs, "Second"),
                new CharacterProfile { Name = "Second", AvoidedRooms = new() { new RoomRef(9, 9) } });
            ProfileService profile = TestProfiles.Service();
            MovementFilter filter = new(profile);
            profile.LoadDefaultProfile();
            filter.MarkAvoided(new RoomKey(1, 1));
            Assert.True(filter.IsAvoided(new RoomKey(1, 1)));

            profile.Load(bbs, "Second");

            Assert.False(filter.IsAvoided(new RoomKey(1, 1)));
            Assert.True(filter.IsAvoided(new RoomKey(9, 9)));
        }
        finally
        {
            string folder = AppPaths.BbsFolder(bbs);
            if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, recursive: true);
        }
    }

    // ----- IsExitBlocked: Form-A level-gate evaluation ---------------

    private static RoomExit GatedExit(int min, int max) =>
        new(new RoomKey(1, 2), RoomExitHint.None, RawHint: null,
            MinLevel: min, MaxLevel: max);

    [Fact]
    public void IsExitBlocked_NoGate_NeverBlocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 1;
        Assert.False(filter.IsExitBlocked(GatedExit(0, 0)));
    }

    [Fact]
    public void IsExitBlocked_UnknownLevel_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => null;   // no stat screen parsed yet
        Assert.False(filter.IsExitBlocked(GatedExit(20, 0)));
    }

    [Fact]
    public void IsExitBlocked_NoLevelProvider_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        // LevelProvider unset (AppServices wires it; a bare filter has none).
        Assert.False(filter.IsExitBlocked(GatedExit(20, 0)));
    }

    [Fact]
    public void IsExitBlocked_BelowFloor_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 19;
        Assert.True(filter.IsExitBlocked(GatedExit(20, 0)));   // need 20+, have 19
    }

    [Fact]
    public void IsExitBlocked_AtFloor_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 20;
        Assert.False(filter.IsExitBlocked(GatedExit(20, 0)));  // exactly meets floor
    }

    [Fact]
    public void IsExitBlocked_AboveCap_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 4;
        Assert.True(filter.IsExitBlocked(GatedExit(0, 3)));    // cap 3, have 4
    }

    [Fact]
    public void IsExitBlocked_AtCap_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 3;
        Assert.False(filter.IsExitBlocked(GatedExit(0, 3)));   // exactly at cap
    }

    [Fact]
    public void IsExitBlocked_WithinWindow_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 15;
        Assert.False(filter.IsExitBlocked(GatedExit(10, 25))); // 10..25, have 15
    }

    [Fact]
    public void IsExitBlocked_OutsideWindow_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 30;
        Assert.True(filter.IsExitBlocked(GatedExit(10, 25)));  // 10..25, have 30
    }

    // ----- IsExitBlocked: (Class: N OK) class-gate evaluation --------

    private static RoomExit ClassGatedExit(int classNumber) =>
        new(new RoomKey(1, 2), RoomExitHint.None, RawHint: null,
            ClassGate: classNumber);

    private static RoomExit ClassGatedTeleport(int classNumber) =>
        new(new RoomKey(1, 2), RoomExitHint.Teleport, RawHint: null,
            ClassGate: classNumber);

    [Fact]
    public void IsExitBlocked_ClassGate_UnknownClass_DoesNotBlockPlainExit()
    {
        (_, MovementFilter filter) = NewPair();
        filter.ClassNumberProvider = () => null;   // no stat screen parsed yet
        // A class-gated CARDINAL stays fail-open on unknown class (walk up, halt on
        // the game's refusal), same as a level gate.
        Assert.False(filter.IsExitBlocked(ClassGatedExit(13)));
    }

    [Fact]
    public void IsExitBlocked_ClassGate_NoProvider_DoesNotBlockPlainExit()
    {
        (_, MovementFilter filter) = NewPair();
        // ClassNumberProvider unset (AppServices wires it; bare filter has none).
        Assert.False(filter.IsExitBlocked(ClassGatedExit(13)));
    }

    // ----- IsExitBlocked: (Race: N OK) race-gate evaluation ----------

    [Fact]
    public void IsExitBlocked_RaceGate_ClosesToAnotherRace_AndNamesTheReason()
    {
        (_, MovementFilter filter) = NewPair();
        RoomExit gauntOnly = new(new RoomKey(7, 1362), RoomExitHint.None, RawHint: null, RaceGate: 13);

        filter.RaceNumberProvider = () => 1;        // a Human at the Gaunt One exit
        Assert.True(filter.IsExitBlocked(gauntOnly));
        Assert.Equal(ExitBlockReason.Race, filter.DescribeExitBlock(gauntOnly));

        filter.RaceNumberProvider = () => 13;       // a Gaunt One
        Assert.False(filter.IsExitBlocked(gauntOnly));

        filter.RaceNumberProvider = () => null;     // race not read yet: walk up and see
        Assert.False(filter.IsExitBlocked(gauntOnly));
    }

    [Fact]
    public void IsExitBlocked_ClassGatedTeleport_UnknownClass_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.ClassNumberProvider = () => null;   // stat screen not parsed yet
        // A class-gated TELEPORT is a discrete shortcut the router offers — don't
        // route through it on an unknown class (report stock-20260908-103628: a
        // bard-only barmaid transport surfaced to a not-yet-identified warrior).
        Assert.True(filter.IsExitBlocked(ClassGatedTeleport(9)));
    }

    [Fact]
    public void IsExitBlocked_ClassGatedTeleport_MatchingClass_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.ClassNumberProvider = () => 9;   // Bard at the bard-only transport
        Assert.False(filter.IsExitBlocked(ClassGatedTeleport(9)));
    }

    [Fact]
    public void IsExitBlocked_ClassGatedTeleport_WrongClass_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.ClassNumberProvider = () => 1;   // Warrior at the bard-only transport
        Assert.True(filter.IsExitBlocked(ClassGatedTeleport(9)));
    }

    [Fact]
    public void IsExitBlocked_ClassGate_WrongClass_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.ClassNumberProvider = () => 1;    // Warrior at a Druid (13) hall
        Assert.True(filter.IsExitBlocked(ClassGatedExit(13)));
    }

    [Fact]
    public void IsExitBlocked_ClassGate_MatchingClass_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.ClassNumberProvider = () => 13;   // Druid at the Druid hall
        Assert.False(filter.IsExitBlocked(ClassGatedExit(13)));
    }

    [Fact]
    public void IsExitBlocked_ClassGate_IndependentOfLevelAndToll()
    {
        (_, MovementFilter filter) = NewPair();
        // A pure class gate carries no level window or toll, so those branches
        // must never rescue or block it.
        filter.LevelProvider = () => 1;
        filter.WealthProvider = () => 0;
        filter.ClassNumberProvider = () => 13;
        Assert.False(filter.IsExitBlocked(ClassGatedExit(13)));   // right class → allowed
    }

    [Fact]
    public void IsExitBlocked_ClassGate_DoesNotAffectPlainExit()
    {
        (_, MovementFilter filter) = NewPair();
        // Wrong class, but the exit has no class gate — must not block.
        filter.ClassNumberProvider = () => 1;
        Assert.False(filter.IsExitBlocked(GatedExit(0, 0)));
    }

    // ----- IsExitBlocked: (Alignment: X to Y) alignment-gate (report -144553) -----

    // "(Alignment: Neutral to Fiend)" = numeric [0, 300] — the evil entrance.
    private static RoomExit AlignmentGatedExit() =>
        new(new RoomKey(1, 2), RoomExitHint.None, RawHint: null,
            AlignmentGate: (0, 300));

    [Fact]
    public void IsExitBlocked_AlignmentGate_NoProvider_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        Assert.False(filter.IsExitBlocked(AlignmentGatedExit()));   // can't evaluate → don't gate
    }

    [Fact]
    public void IsExitBlocked_AlignmentGate_KnownExcludedMember_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        // A Good (-100) character can't enter a Neutral-to-Fiend [0,300] gate.
        filter.PartyAlignmentsProvider = () => new int?[] { -100 };
        Assert.True(filter.IsExitBlocked(AlignmentGatedExit()));
    }

    [Fact]
    public void IsExitBlocked_AlignmentGate_AllInside_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        // Neutral (0) and Fiend (300) both sit inside [0,300].
        filter.PartyAlignmentsProvider = () => new int?[] { 0, 300 };
        Assert.False(filter.IsExitBlocked(AlignmentGatedExit()));
    }

    [Fact]
    public void IsExitBlocked_AlignmentGate_WholeParty_BlocksOnTightestMember()
    {
        (_, MovementFilter filter) = NewPair();
        // Leader Neutral (0, inside) but a follower Good (-100, outside) → the whole
        // party is stopped at the tightest member.
        filter.PartyAlignmentsProvider = () => new int?[] { 0, -100 };
        Assert.True(filter.IsExitBlocked(AlignmentGatedExit()));
    }

    [Fact]
    public void IsExitBlocked_AlignmentGate_UnknownMember_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        // Unknown alignment (null) doesn't route around — the walker walks up to the
        // gate and halts there instead of detouring on a guess.
        filter.PartyAlignmentsProvider = () => new int?[] { null };
        Assert.False(filter.IsExitBlocked(AlignmentGatedExit()));
    }

    [Fact]
    public void DescribeExitBlock_AlignmentGate_NamesAlignment()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyAlignmentsProvider = () => new int?[] { -100 };
        Assert.Equal(ExitBlockReason.Alignment, filter.DescribeExitBlock(AlignmentGatedExit()));
    }

    // ----- IsExitBlocked: key-only door ------------------------------

    private static RoomExit KeyOnlyDoor(int keyItemId) =>
        // (Key: N) with no stat clause → StatRequirement 0, CanBash defaults true.
        new(new RoomKey(1, 2), RoomExitHint.KeyLocked, RawHint: null,
            StatRequirement: 0, KeyItemId: keyItemId);

    [Fact]
    public void IsExitBlocked_KeyOnlyDoor_NoKey_BlocksAsLockedDoor()
    {
        (_, MovementFilter filter) = NewPair();
        // Str 127 / Picklocks 0, key not carried: a 0-requirement door reads as a
        // plain "(Door)" anyone can bash, but this one is key-only — without the
        // key it must block so the route picker surfaces the key requirement.
        filter.StrengthProvider = () => 127;
        filter.PicklocksProvider = () => 0;
        filter.InventoryReadyProbe = () => true;
        filter.ItemCarriedProbe = _ => false;
        RoomExit door = KeyOnlyDoor(475);

        Assert.True(filter.IsExitBlocked(door));
        Assert.Equal(ExitBlockReason.LockedDoor, filter.DescribeExitBlock(door));
    }

    [Fact]
    public void IsExitBlocked_KeyOnlyDoor_WithKey_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.StrengthProvider = () => 127;
        filter.PicklocksProvider = () => 0;
        filter.InventoryReadyProbe = () => true;
        filter.ItemCarriedProbe = id => id == 475;   // carry the key
        Assert.False(filter.IsExitBlocked(KeyOnlyDoor(475)));
    }

    // ----- IsExitBlocked: party-bounds branch ------------------------

    [Fact]
    public void PartyBounds_TakePrecedence_OverSelfLevel()
    {
        (_, MovementFilter filter) = NewPair();
        // Self clears the floor, but the party's lowest member doesn't —
        // route around so we don't leave that member behind.
        filter.LevelProvider = () => 50;
        filter.PartyLevelBoundsProvider = () => (Low: 18, High: 50);
        Assert.True(filter.IsExitBlocked(GatedExit(20, 0)));   // need 20+, party low 18
    }

    [Fact]
    public void PartyBounds_WholePartyClearsFloor_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyLevelBoundsProvider = () => (Low: 22, High: 40);
        Assert.False(filter.IsExitBlocked(GatedExit(20, 0)));  // everyone ≥ 20
    }

    [Fact]
    public void PartyBounds_HighestMemberAboveCap_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyLevelBoundsProvider = () => (Low: 3, High: 30);
        Assert.True(filter.IsExitBlocked(GatedExit(0, 25)));   // cap 25, someone is 30
    }

    [Fact]
    public void PartyBounds_WholePartyWithinWindow_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyLevelBoundsProvider = () => (Low: 12, High: 24);
        Assert.False(filter.IsExitBlocked(GatedExit(10, 25))); // 10..25 covers 12..24
    }

    [Fact]
    public void PartyBounds_Null_FallsBackToSelfLevel()
    {
        (_, MovementFilter filter) = NewPair();
        // Not leading / nobody's level known — provider returns null, so
        // the self-only branch decides.
        filter.PartyLevelBoundsProvider = () => null;
        filter.LevelProvider = () => 19;
        Assert.True(filter.IsExitBlocked(GatedExit(20, 0)));   // self-only: 19 < 20
    }

    [Fact]
    public void PartyBounds_NoProvider_FallsBackToSelfLevel()
    {
        (_, MovementFilter filter) = NewPair();
        // PartyLevelBoundsProvider unset entirely (solo character).
        filter.LevelProvider = () => 30;
        Assert.False(filter.IsExitBlocked(GatedExit(20, 0)));  // self-only: 30 ≥ 20
    }

    [Fact]
    public void PartyBounds_NoGate_NeverBlocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyLevelBoundsProvider = () => (Low: 1, High: 1);
        Assert.False(filter.IsExitBlocked(GatedExit(0, 0)));   // ungated exit
    }

    // ----- IsExitBlocked: (Toll: N) wealth-gate evaluation ----------

    private static RoomExit TollExit(int tollGold) =>
        new(new RoomKey(1, 2), RoomExitHint.Toll, RawHint: null,
            TollGold: tollGold);

    [Fact]
    public void IsExitBlocked_Toll_NoWealthProvider_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        // WealthProvider unset (AppServices wires it; a bare filter has none).
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void IsExitBlocked_Toll_UnknownWealth_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        filter.WealthProvider = () => null;   // no inventory parsed yet
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void IsExitBlocked_Toll_CanAfford_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        // Toll 5 needs 5*100 = 500 copper-value on hand.
        filter.WealthProvider = () => 500;
        Assert.False(filter.IsExitBlocked(TollExit(5)));   // exactly meets the bar
    }

    [Fact]
    public void IsExitBlocked_Toll_CannotAfford_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.WealthProvider = () => 499;
        Assert.True(filter.IsExitBlocked(TollExit(5)));    // one short of 500
    }

    [Fact]
    public void IsExitBlocked_Toll_ZeroTollGold_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        filter.WealthProvider = () => 0;
        // A Toll-hint exit with no parsed amount can't gate — nothing to owe.
        Assert.False(filter.IsExitBlocked(TollExit(0)));
    }

    [Fact]
    public void IsExitBlocked_Toll_IndependentOfLevelGate()
    {
        (_, MovementFilter filter) = NewPair();
        // A pure toll exit carries no level window, so the level branch must
        // never block it even when the level provider says we're low.
        filter.LevelProvider = () => 1;
        filter.WealthProvider = () => 1000;
        Assert.False(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void IsExitBlocked_LevelGate_UnaffectedByWealthProvider()
    {
        (_, MovementFilter filter) = NewPair();
        // A pure level gate carries no toll, so the wealth branch must never
        // rescue a walk the level window forbids.
        filter.LevelProvider = () => 19;
        filter.WealthProvider = () => 1_000_000;
        Assert.True(filter.IsExitBlocked(GatedExit(20, 0)));
    }

    // ----- IsExitBlocked: the party toll check ----------------------

    [Fact]
    public void PartyTollCheck_ClosesATollWeCanAfford()
    {
        (_, MovementFilter filter) = NewPair();
        // We can afford the toll, but the party can't all get through it (the
        // party-toll check's verdict) — route around so nobody is left at the gate.
        filter.WealthProvider = () => 1000;
        filter.PartyTollClosedProbe = _ => true;
        Assert.True(filter.IsExitBlocked(TollExit(5)));
    }

    [Fact]
    public void PartyWealth_PoorestKnownPurse_BlocksNeitherAFareNorAToll_ThePartysCheckDoes()
    {
        (_, MovementFilter filter) = NewPair();
        filter.PartyWealthProvider = () => 499;
        // Neither is judged on the poorest known purse: the party's check asks
        // everyone at the crossing and pays for whoever is short when it may (user,
        // 2026-10-10; a ride is checked as a toll is).
        Assert.False(filter.IsExitBlocked(FareExit(500)));
        Assert.False(filter.IsExitBlocked(TollExit(5)));

        filter.PartyTollClosedProbe = _ => true;
        Assert.True(filter.IsExitBlocked(FareExit(500)));
        Assert.True(filter.IsExitBlocked(TollExit(5)));
    }

    // ----- IsExitBlocked: NPC ask-transport fare -------------------

    // Seher'Sahham's activate: 1 runic, charged to every person who asks.
    private static RoomExit FareExit(long fareCopper) =>
        new(new RoomKey(16, 637), RoomExitHint.Teleport, RawHint: "greet teleport",
            TextCommands: new[] { "ask Seher'Sahham activate" }, FareCopper: fareCopper);

    [Fact]
    public void Fare_SelfCannotAfford_BlocksAsFare()
    {
        (_, MovementFilter filter) = NewPair();
        filter.WealthProvider = () => 999_999;
        Assert.True(filter.IsExitBlocked(FareExit(1_000_000)));
        Assert.Equal(ExitBlockReason.Fare, filter.DescribeExitBlock(FareExit(1_000_000)));
    }

    [Fact]
    public void Fare_ThePartyCantAllPay_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        // We can pay, but each member pays their own fare, and the party's check
        // says one can't and isn't paid for.
        filter.WealthProvider = () => 5_000_000;
        filter.PartyWealthProvider = () => 999_999;
        Assert.False(filter.IsExitBlocked(FareExit(1_000_000)));
        filter.PartyTollClosedProbe = exit => exit.FareCopper == 1_000_000;
        Assert.True(filter.IsExitBlocked(FareExit(1_000_000)));
        Assert.Equal(ExitBlockReason.Fare, filter.DescribeExitBlock(FareExit(1_000_000)));
    }

    [Fact]
    public void Fare_UnknownWealth_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        filter.WealthProvider = () => null;
        Assert.False(filter.IsExitBlocked(FareExit(1_000_000)));
    }

    [Fact]
    public void Fare_FreeTransport_NeverBlocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.WealthProvider = () => 0;
        Assert.False(filter.IsExitBlocked(FareExit(0)));
    }

    // ----- IRoomFilter integration with BfsMapper -------------------

    [Fact]
    public void BfsMapper_HonoursMovementFilter()
    {
        // Two-row strip: 1/1 ──N── 1/2 ──N── 1/3.
        // With 1/2 avoided, no path from 1/1 to 1/3.
        string root = Path.Combine(Path.GetTempPath(),
            "mudplay-movementfilter-bfs-" + Path.GetRandomFileName());
        try
        {
            string setDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "Rooms.json"), """
                [
                  { "Map Number": 1, "Room Number": 1, "Name": "A",
                    "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                    "N": "1/2", "S": "0", "E": "0", "W": "0",
                    "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
                  { "Map Number": 1, "Room Number": 2, "Name": "B",
                    "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                    "N": "1/3", "S": "1/1", "E": "0", "W": "0",
                    "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
                  { "Map Number": 1, "Room Number": 3, "Name": "C",
                    "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                    "N": "0", "S": "1/2", "E": "0", "W": "0",
                    "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
                ]
                """);
            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            BfsMapper bfs = new(graph);

            ProfileService profile = TestProfiles.Service();
            profile.LoadDefaultProfile();
            MovementFilter filter = new(profile);
            filter.MarkAvoided(new RoomKey(1, 2));

            Assert.Null(bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 3), filter));
            Assert.NotNull(bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 3), filter: null));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // ----- WarmForRoute: route-scoped @wealth probe -----------------

    // A ──E── B ──E(Toll:5)── C, plus an off-path A ──N(Toll:5)── D branch.
    // The A→C route crosses a toll; the A→B route does not (but the N→D toll
    // edge sits in the BFS frontier, which is exactly the off-path case that
    // used to fire a spurious probe).
    private const string TollStripJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/4 (Toll: 5)", "S": "0", "E": "1/2", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/3 (Toll: 5)", "W": "1/1",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/2",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "D",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static void WithGraph(string roomsJson, Action<BfsMapper> body)
    {
        string root = Path.Combine(Path.GetTempPath(),
            "mudplay-warmroute-" + Path.GetRandomFileName());
        try
        {
            string setDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "Rooms.json"), roomsJson);
            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            body(new BfsMapper(graph));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void WarmForRoute_TollOnRoute_Probes()
    {
        WithGraph(TollStripJson, bfs =>
        {
            (_, MovementFilter filter) = NewPair();
            int probes = 0;
            filter.PartyWealthProvider = () => 100;   // leading a party
            filter.WealthWarmProbe = () => probes++;

            filter.WarmForRoute(bfs, new RoomKey(1, 1), new RoomKey(1, 3));

            Assert.Equal(1, probes);   // A→C crosses the B→C toll
        });
    }

    [Fact]
    public void WarmForRoute_TollOffRoute_DoesNotProbe()
    {
        WithGraph(TollStripJson, bfs =>
        {
            (_, MovementFilter filter) = NewPair();
            int probes = 0;
            filter.PartyWealthProvider = () => 100;
            filter.WealthWarmProbe = () => probes++;

            // A→B is a plain E; the N→D toll is in the frontier but not on
            // the route, so it must not trigger a poll (the reported bug).
            filter.WarmForRoute(bfs, new RoomKey(1, 1), new RoomKey(1, 2));

            Assert.Equal(0, probes);
        });
    }

    [Fact]
    public void WarmForRoute_NoPartyGate_DoesNotProbe()
    {
        WithGraph(TollStripJson, bfs =>
        {
            (_, MovementFilter filter) = NewPair();
            int probes = 0;
            // PartyWealthProvider unset (solo / not leading) → nothing to warm.
            filter.WealthWarmProbe = () => probes++;

            filter.WarmForRoute(bfs, new RoomKey(1, 1), new RoomKey(1, 3));

            Assert.Equal(0, probes);
        });
    }

    [Fact]
    public void WarmForRoute_RestoresTollGate()
    {
        WithGraph(TollStripJson, bfs =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.PartyWealthProvider = () => 100;   // leading, so the warm runs
            filter.WealthProvider = () => 100;        // we can't cover 500
            filter.WealthWarmProbe = () => { };

            filter.WarmForRoute(bfs, new RoomKey(1, 1), new RoomKey(1, 3));

            // The suspend flag is cleared in the finally: a toll we can't
            // afford still blocks after warming.
            Assert.True(filter.IsExitBlocked(TollExit(5)));
        });
    }

    // ----- IsExitBlocked: item / ticket / key-door acquirable gates ---

    private static RoomExit ItemExit(int keyItemId) =>
        new(new RoomKey(1, 2), RoomExitHint.Item, RawHint: null, KeyItemId: keyItemId);

    private static RoomExit TicketExit(int keyItemId) =>
        new(new RoomKey(1, 2), RoomExitHint.Ticket, RawHint: null, KeyItemId: keyItemId);

    // A key-locked door: opens with the key item, or by pick/bash meeting statReq.
    private static RoomExit KeyLockedExit(int keyItemId, int statReq = 0, bool canBash = true) =>
        new(new RoomKey(1, 2), RoomExitHint.KeyLocked, RawHint: null,
            StatRequirement: statReq, CanBash: canBash, KeyItemId: keyItemId);

    // Marks the crosser's inventory as parsed and holding exactly the given ids.
    private static void SetInventory(MovementFilter filter, params int[] held)
    {
        filter.InventoryReadyProbe = () => true;
        filter.ItemCarriedProbe = id => held.Contains(id);
    }

    // A synthesised item-use teleport edge: crosses by using the held item.
    private static RoomExit ItemUseTeleportExit(int keyItemId) =>
        new(new RoomKey(9, 1009), RoomExitHint.Teleport, RawHint: "item-use teleport",
            KeyItemId: keyItemId, TextCommands: new[] { "use potion of levitation" });

    [Fact]
    public void IsExitBlocked_ItemUseTeleport_Lacking_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);                                   // carrying nothing
        Assert.True(filter.IsExitBlocked(ItemUseTeleportExit(992)));
    }

    [Fact]
    public void IsExitBlocked_ItemUseTeleport_Carried_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter, 992);                              // carrying the potion
        Assert.False(filter.IsExitBlocked(ItemUseTeleportExit(992)));
    }

    [Fact]
    public void IsExitBlocked_Teleport_NoKeyItem_NeverBlocks()
    {
        // A plain CMD / greet teleport (no possession requirement) must not be gated
        // by the new item-use case — KeyItemId 0 means "no item needed".
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);
        RoomExit plain = new(new RoomKey(9, 1009), RoomExitHint.Teleport, RawHint: "CMD teleport",
            TextCommands: new[] { "go hole" });
        Assert.False(filter.IsExitBlocked(plain));
    }

    [Fact]
    public void IsExitBlocked_Item_InventoryUnknown_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        filter.InventoryReadyProbe = () => false;   // no dump parsed yet
        filter.ItemCarriedProbe = _ => false;
        Assert.False(filter.IsExitBlocked(ItemExit(5)));
    }

    [Fact]
    public void IsExitBlocked_Item_NoCarriedProbe_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        filter.InventoryReadyProbe = () => true;
        // ItemCarriedProbe unset → can't evaluate → don't gate.
        Assert.False(filter.IsExitBlocked(ItemExit(5)));
    }

    [Fact]
    public void IsExitBlocked_Item_Lacking_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);   // parsed, holding nothing
        Assert.True(filter.IsExitBlocked(ItemExit(5)));
    }

    [Fact]
    public void IsExitBlocked_Item_Held_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter, 5);
        Assert.False(filter.IsExitBlocked(ItemExit(5)));
    }

    [Fact]
    public void IsExitBlocked_Item_ZeroKeyItem_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);
        // No item id on the modifier → nothing to require.
        Assert.False(filter.IsExitBlocked(ItemExit(0)));
    }

    [Fact]
    public void IsExitBlocked_Ticket_Lacking_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);
        Assert.True(filter.IsExitBlocked(TicketExit(9)));
    }

    [Fact]
    public void IsExitBlocked_Ticket_Held_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter, 9);
        Assert.False(filter.IsExitBlocked(TicketExit(9)));
    }

    [Fact]
    public void IsExitBlocked_KeyDoor_KeyHeld_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter, 7);
        // Key in hand → passable regardless of pick/bash skill (both unset).
        Assert.False(filter.IsExitBlocked(KeyLockedExit(7, statReq: 80, canBash: false)));
    }

    [Fact]
    public void IsExitBlocked_KeyDoor_NoStats_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);   // key not held
        // Strength / Picklocks providers unset → leave the door to the FSM.
        Assert.False(filter.IsExitBlocked(KeyLockedExit(7, statReq: 80, canBash: true)));
    }

    [Fact]
    public void IsExitBlocked_KeyDoor_Bashable_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);   // key not held
        filter.StrengthProvider = () => 60;
        filter.PicklocksProvider = () => 0;
        filter.MaxBashableStrengthProvider = () => 200;
        // statReq 50, bashable, strength 60 ≥ 50 → openable by bash.
        Assert.False(filter.IsExitBlocked(KeyLockedExit(7, statReq: 50, canBash: true)));
    }

    [Fact]
    public void IsExitBlocked_KeyDoor_PickSufficient_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);
        filter.StrengthProvider = () => 0;
        filter.PicklocksProvider = () => 40;
        filter.MaxBashableStrengthProvider = () => 200;
        // pick-only door, picks 40 ≥ statReq 40 → openable.
        Assert.False(filter.IsExitBlocked(KeyLockedExit(7, statReq: 40, canBash: false)));
    }

    [Fact]
    public void IsExitBlocked_KeyDoor_PickOnlyInsufficient_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);
        filter.StrengthProvider = () => 200;   // strong, but bash is barred
        filter.PicklocksProvider = () => 50;
        filter.MaxBashableStrengthProvider = () => 200;
        // pick-only door (canBash false), picks 50 < statReq 80 → no way in.
        Assert.True(filter.IsExitBlocked(KeyLockedExit(7, statReq: 80, canBash: false)));
    }

    [Fact]
    public void IsExitBlocked_KeyDoor_AboveBashCeiling_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);
        filter.StrengthProvider = () => 300;
        filter.PicklocksProvider = () => 0;
        filter.MaxBashableStrengthProvider = () => 200;   // realm bash ceiling
        // statReq 250 exceeds the set-wide bash ceiling → unbashable by anyone,
        // and picks 0 can't pick → blocked even for a maxed-strength build.
        Assert.True(filter.IsExitBlocked(KeyLockedExit(7, statReq: 250, canBash: true)));
    }

    [Fact]
    public void SuspendAcquirableGates_SuspendsItemGate_ThenRestores()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);   // lacking the raft
        RoomExit raft = ItemExit(5);

        Assert.True(filter.IsExitBlocked(raft));       // gated before

        using (filter.SuspendAcquirableGates())
            Assert.False(filter.IsExitBlocked(raft));  // planned-through during

        Assert.True(filter.IsExitBlocked(raft));       // restored after
    }

    [Fact]
    public void SuspendAcquirableGates_LeavesLevelGateActive()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 19;
        // Level / class / toll aren't acquired on demand, so the planning
        // suspend must not relax them.
        using (filter.SuspendAcquirableGates())
            Assert.True(filter.IsExitBlocked(GatedExit(20, 0)));
    }

    // ----- IsExitBlocked: plain (Door) pick/bash achievability gate ---

    // A keyless "(Door [N picklocks/strength])" — opened only by pick or bash.
    private static RoomExit DoorExit(int statReq, bool canBash = true) =>
        new(new RoomKey(1, 2), RoomExitHint.Door, RawHint: null,
            StatRequirement: statReq, CanBash: canBash);

    [Fact]
    public void IsExitBlocked_Door_NoStats_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        // Strength / Picklocks providers unset → unknown build, leave to FSM.
        Assert.False(filter.IsExitBlocked(DoorExit(251)));
    }

    [Fact]
    public void IsExitBlocked_Door_Unopenable_Blocks()
    {
        // The Bandit Keep front door: req 251, str 96, picks 0, bashable but
        // 251 sits above the realm bash ceiling → routed around at plan time.
        (_, MovementFilter filter) = NewPair();
        filter.StrengthProvider = () => 96;
        filter.PicklocksProvider = () => 0;
        filter.MaxBashableStrengthProvider = () => 200;
        Assert.True(filter.IsExitBlocked(DoorExit(251)));
    }

    [Fact]
    public void IsExitBlocked_Door_Bashable_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.StrengthProvider = () => 60;
        filter.PicklocksProvider = () => 0;
        filter.MaxBashableStrengthProvider = () => 200;
        Assert.False(filter.IsExitBlocked(DoorExit(50)));   // str 60 ≥ req 50
    }

    [Fact]
    public void IsExitBlocked_Door_Pickable_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.StrengthProvider = () => 0;
        filter.PicklocksProvider = () => 60;
        filter.MaxBashableStrengthProvider = () => 200;
        Assert.False(filter.IsExitBlocked(DoorExit(50, canBash: false)));  // picks 60 ≥ 50
    }

    [Fact]
    public void IsExitBlocked_Door_NoRequirement_Allows()
    {
        (_, MovementFilter filter) = NewPair();
        filter.StrengthProvider = () => 10;
        filter.PicklocksProvider = () => 0;
        Assert.False(filter.IsExitBlocked(DoorExit(0)));   // "(Door)" — anyone opens
    }

    [Fact]
    public void IsExitBlocked_Door_PickOnlyInsufficient_Blocks()
    {
        (_, MovementFilter filter) = NewPair();
        filter.StrengthProvider = () => 300;   // strong, but bash is barred
        filter.PicklocksProvider = () => 40;
        filter.MaxBashableStrengthProvider = () => 200;
        Assert.True(filter.IsExitBlocked(DoorExit(80, canBash: false)));  // picks 40 < 80
    }

    [Fact]
    public void Door_Gate_StaysActive_UnderSuspendAcquirableGates()
    {
        // A strength door has no acquirable unlock, so the route picker's
        // gated-route pass must NOT wave it through the way it does an item /
        // key door — else the picker plans a route into a door it can't open.
        (_, MovementFilter filter) = NewPair();
        filter.StrengthProvider = () => 96;
        filter.PicklocksProvider = () => 0;
        filter.MaxBashableStrengthProvider = () => 200;
        RoomExit door = DoorExit(251);

        Assert.True(filter.IsExitBlocked(door));
        using (filter.SuspendAcquirableGates())
            Assert.True(filter.IsExitBlocked(door));   // still blocked during
        Assert.True(filter.IsExitBlocked(door));       // restored after
    }

    [Fact]
    public void BfsMapper_RoutesAroundUnopenableDoor()
    {
        // 1/1 ─E(Door [251 picklocks/strength])→ 1/2   (1 hop, unopenable)
        // 1/1 ─N→ 1/3 ─E→ 1/2                          (2 hops, all plain)
        // A build that can't open the door must take the longer plain route;
        // a build that can takes the 1-hop door.
        string root = Path.Combine(Path.GetTempPath(),
            "mudplay-door-routearound-" + Path.GetRandomFileName());
        try
        {
            string setDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "Rooms.json"), """
                [
                  { "Map Number": 1, "Room Number": 1, "Name": "Outside",
                    "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                    "N": "1/3", "S": "0", "E": "1/2 (Door [251 picklocks/strength])", "W": "0",
                    "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
                  { "Map Number": 1, "Room Number": 2, "Name": "Inside",
                    "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                    "N": "0", "S": "0", "E": "0", "W": "1/1 (Door [251 picklocks/strength])",
                    "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
                  { "Map Number": 1, "Room Number": 3, "Name": "Wall",
                    "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                    "N": "0", "S": "0", "E": "1/2", "W": "1/1",
                    "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
                ]
                """);
            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");
            BfsMapper bfs = new(graph);

            (_, MovementFilter filter) = NewPair();
            filter.PicklocksProvider = () => 0;
            filter.MaxBashableStrengthProvider = () => 300;

            filter.StrengthProvider = () => 96;   // can't muscle req 251
            Assert.Equal(
                new[] { Direction.N, Direction.E },
                bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 2), filter));

            filter.StrengthProvider = () => 255;  // now bashes req 251 (≤ ceiling 300)
            Assert.Equal(
                new[] { Direction.E },
                bfs.FindPath(new RoomKey(1, 1), new RoomKey(1, 2), filter));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // ----- IsExitBlocked: room-entry hazard gates --------------------

    // Spell 700 damages on entry; item 42 negates it — the minimal shape of a
    // protectable room-entry hazard (see RoomHazardIndex).
    private const string HazardRoomsJson = """
        [ { "Map Number": 1, "Room Number": 2, "Name": "Hazard", "Spell": 700,
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" } ]
        """;
    private const string HazardSpellsJson = """
        [ { "Number": 700, "Abil-0": 1, "AbilVal-0": 25 } ]
        """;
    private const string HazardItemsJson = """
        [ { "Number": 42, "NegateSpell-0": 700 } ]
        """;

    private static void WithHazards(Action<RoomHazardIndex> body)
    {
        string root = Path.Combine(Path.GetTempPath(),
            "mudplay-hazards-" + Path.GetRandomFileName());
        try
        {
            string setDir = Path.Combine(root, "alpha");
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "Rooms.json"), HazardRoomsJson);
            File.WriteAllText(Path.Combine(setDir, "Spells.json"), HazardSpellsJson);
            File.WriteAllText(Path.Combine(setDir, "Items.json"), HazardItemsJson);
            GameDataCache cache = new(root);
            cache.SwitchSet("alpha");
            RoomHazardIndex index = new(cache);
            index.OnActiveSetChanged("alpha");
            body(index);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private static RoomExit PlainExitTo(RoomKey target) =>
        new(target, RoomExitHint.None, RawHint: null);

    [Fact]
    public void IsExitBlocked_Hazard_CounterLacking_Blocks()
    {
        WithHazards(index =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 2) ? 700 : 0;
            SetInventory(filter);   // no negator held
            Assert.True(filter.IsExitBlocked(PlainExitTo(new RoomKey(1, 2))));
        });
    }

    [Fact]
    public void IsExitBlocked_Hazard_CounterHeld_Allows()
    {
        WithHazards(index =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 2) ? 700 : 0;
            SetInventory(filter, 42);   // carries the negator
            Assert.False(filter.IsExitBlocked(PlainExitTo(new RoomKey(1, 2))));
        });
    }

    // A negating item works only worn. Carried, it opens the room to a route when
    // the client will put it on before the step, and not when that is switched off
    // (the setting, the master switch) or the character can't wear it.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void IsExitBlocked_Hazard_CarriedNegator_OpensTheRoomOnlyWhenItWillBeWorn(bool usable, bool blocked)
    {
        WithHazards(index =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 2) ? 700 : 0;
            SetInventory(filter, 42);
            filter.NegatingItemUsableProbe = id => id == 42 && usable;

            Assert.Equal(blocked, filter.IsExitBlocked(PlainExitTo(new RoomKey(1, 2))));
        });
    }

    // Standing in an uncountered hazard room (a wear the game refused part-way along
    // a route), the rooms of that same hazard are the way on: a re-plan from the
    // middle of them must not find every exit shut. A plan from anywhere else still
    // finds them shut.
    [Theory]
    [InlineData(1, false)]    // standing in room 1/1, which carries the hazard too
    [InlineData(9, true)]     // standing somewhere ordinary
    [InlineData(null, true)]  // the room stood in isn't known
    public void IsExitBlocked_Hazard_TheSameHazardAsTheRoomStoodIn_StaysLeavable(int? standingIn, bool blocked)
    {
        WithHazards(index =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key.Room is 1 or 2 ? 700 : 0;
            SetInventory(filter);   // no counter in the pack
            filter.StandingRoomProbe = () => standingIn is { } room ? new RoomKey(1, room) : null;

            RoomExit exit = PlainExitTo(new RoomKey(1, 2));
            Assert.Equal(blocked, filter.IsExitBlocked(exit));
            Assert.Equal(blocked, filter.DescribeExitBlock(exit).HasFlag(ExitBlockReason.Hazard));
            // What a route card and the rest rules ask is unchanged: the room is uncountered.
            Assert.True(filter.IsUncounteredHazardRoom(new RoomKey(1, 2)));
        });
    }

    [Fact]
    public void IsExitBlocked_Hazard_BenignRoom_DoesNotBlock()
    {
        WithHazards(index =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = _ => 0;   // room casts nothing on entry
            SetInventory(filter);
            Assert.False(filter.IsExitBlocked(PlainExitTo(new RoomKey(1, 2))));
        });
    }

    [Fact]
    public void IsExitBlocked_Hazard_InventoryUnknown_DoesNotBlock()
    {
        WithHazards(index =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = _ => 700;
            filter.InventoryReadyProbe = () => false;   // unparsed
            filter.ItemCarriedProbe = _ => false;
            Assert.False(filter.IsExitBlocked(PlainExitTo(new RoomKey(1, 2))));
        });
    }

    [Fact]
    public void IsExitBlocked_Hazard_NoIndex_DoesNotBlock()
    {
        (_, MovementFilter filter) = NewPair();
        // Hazards unset (AppServices wires it; a bare filter has none).
        filter.RoomEntrySpellProbe = _ => 700;
        SetInventory(filter);
        Assert.False(filter.IsExitBlocked(PlainExitTo(new RoomKey(1, 2))));
    }

    // Report paradigm-20261004-204354: a stash transfer planned through gates took
    // the Silver River, whose counter is a choice between boats that the walk start
    // provisions for nobody, and walked in unprotected. A through-gates plan nobody
    // picked keeps such a room closed.
    [Fact]
    public void SuspendButUncounteredHazards_KeepsAnUnarrangedHazardClosed()
    {
        WithHazards(index =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 2) ? 700 : 0;
            SetInventory(filter);   // no negator held, none arranged
            RoomExit exit = PlainExitTo(new RoomKey(1, 2));

            using (filter.SuspendAcquirableGatesButUncounteredHazards())
                Assert.True(filter.IsExitBlocked(exit));
            using (filter.SuspendAcquirableGates())
                Assert.False(filter.IsExitBlocked(exit));    // a picked route still opens it
            Assert.True(filter.IsExitBlocked(exit));          // and gating comes back
        });
    }

    [Fact]
    public void SuspendButUncounteredHazards_OpensAHazardTheWalkWillCounter()
    {
        WithHazards(index =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = key => key == new RoomKey(1, 2) ? 700 : 0;
            SetInventory(filter);
            RoomExit exit = PlainExitTo(new RoomKey(1, 2));

            // The walk will obtain the counter on the way.
            filter.HazardProvisionProbe = _ => new[] { 42 };
            using (filter.SuspendAcquirableGatesButUncounteredHazards())
                Assert.False(filter.IsExitBlocked(exit));

            // Or it is simply carried.
            filter.HazardProvisionProbe = null;
            SetInventory(filter, 42);
            using (filter.SuspendAcquirableGatesButUncounteredHazards())
                Assert.False(filter.IsExitBlocked(exit));
        });
    }

    [Fact]
    public void SuspendButUncounteredHazards_StillSuspendsItemGates()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);   // lacking the raft
        RoomExit raft = ItemExit(5);

        Assert.True(filter.IsExitBlocked(raft));
        using (filter.SuspendAcquirableGatesButUncounteredHazards())
            Assert.False(filter.IsExitBlocked(raft));
    }

    [Fact]
    public void SuspendAcquirableGates_SuspendsHazardGate()
    {
        WithHazards(index =>
        {
            (_, MovementFilter filter) = NewPair();
            filter.Hazards = index;
            filter.RoomEntrySpellProbe = _ => 700;
            SetInventory(filter);   // no counter

            RoomExit into = PlainExitTo(new RoomKey(1, 2));
            Assert.True(filter.IsExitBlocked(into));

            using (filter.SuspendAcquirableGates())
                Assert.False(filter.IsExitBlocked(into));

            Assert.True(filter.IsExitBlocked(into));
        });
    }

    // ----- DescribeExitBlock: names the real gate kind ----------------

    [Fact]
    public void DescribeExitBlock_Passable_ReportsNone()
    {
        (_, MovementFilter filter) = NewPair();
        Assert.Equal(ExitBlockReason.None, filter.DescribeExitBlock(GatedExit(0, 0)));
    }

    [Fact]
    public void DescribeExitBlock_KeyDoorUnopenable_ReportsLockedDoor()
    {
        // 223929: a key-locked door we lack the key for and can't pick must
        // read as a LockedDoor — NOT the pack-item Item reason — so the walker's
        // failure message names a locked door instead of a mismatched "item".
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);              // key not held
        filter.StrengthProvider = () => 200;
        filter.PicklocksProvider = () => 50;
        filter.MaxBashableStrengthProvider = () => 200;
        Assert.Equal(ExitBlockReason.LockedDoor,
            filter.DescribeExitBlock(KeyLockedExit(7, statReq: 80, canBash: false)));
    }

    [Fact]
    public void DescribeExitBlock_KeyDoorKeyHeld_ReportsNone()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter, 7);           // key in hand → passable
        Assert.Equal(ExitBlockReason.None,
            filter.DescribeExitBlock(KeyLockedExit(7, statReq: 80, canBash: false)));
    }

    [Fact]
    public void DescribeExitBlock_ItemLacking_ReportsItem()
    {
        (_, MovementFilter filter) = NewPair();
        SetInventory(filter);              // raft not held
        Assert.Equal(ExitBlockReason.Item, filter.DescribeExitBlock(ItemExit(5)));
    }

    [Fact]
    public void DescribeExitBlock_LevelGate_ReportsLevel()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 19;
        Assert.Equal(ExitBlockReason.Level, filter.DescribeExitBlock(GatedExit(20, 0)));
    }

    [Fact]
    public void DescribeExitBlock_Toll_ReportsToll()
    {
        (_, MovementFilter filter) = NewPair();
        filter.WealthProvider = () => 499;
        Assert.Equal(ExitBlockReason.Toll, filter.DescribeExitBlock(TollExit(5)));
    }

    [Fact]
    public void DescribeExitBlock_PlainDoorUnopenable_ReportsDoor()
    {
        (_, MovementFilter filter) = NewPair();
        filter.StrengthProvider = () => 96;
        filter.PicklocksProvider = () => 0;
        filter.MaxBashableStrengthProvider = () => 200;
        Assert.Equal(ExitBlockReason.Door, filter.DescribeExitBlock(DoorExit(251)));
    }

    // ----- Boat sailing gates: per-member minlevel + copper fare ------------

    private static BoatPassage Boat(int minLevel, long fareCopper, bool checkability = false) =>
        new(new RoomKey(14, 759), "albion", "secure passage to albion",
            new RoomKey(14, 702), minLevel, fareCopper, checkability);

    [Fact]
    public void Boat_UnknownLevelAndWealth_DoesNotGate()
    {
        (_, MovementFilter filter) = NewPair();
        // Neither level nor wallet parsed — don't refuse a sailing we can't
        // yet evaluate (same rule as the land gates).
        Assert.True(filter.IsBoatPassable(Boat(50, 2_000_000)));
    }

    [Fact]
    public void Boat_SelfClearsBothGates_Passable()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 55;
        filter.WealthProvider = () => 2_000_000;
        Assert.True(filter.IsBoatPassable(Boat(50, 2_000_000)));
    }

    [Fact]
    public void Boat_SelfUnderLevel_BlocksLevel()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 49;
        filter.WealthProvider = () => 5_000_000;
        Assert.False(filter.IsBoatPassable(Boat(50, 2_000_000)));
        Assert.Equal(ExitBlockReason.Level, filter.DescribeBoatBlock(Boat(50, 2_000_000)));
    }

    [Fact]
    public void Boat_SelfTooPoor_BlocksFare()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 60;
        filter.WealthProvider = () => 1_999_999;   // one short of the fare
        Assert.Equal(ExitBlockReason.Fare, filter.DescribeBoatBlock(Boat(50, 2_000_000)));
    }

    [Fact]
    public void Boat_FareIsCopperNotGold_NoHundredScaling()
    {
        // A boat charges copper directly (unlike a toll's gold*100): 2,000,000
        // copper on hand exactly covers a 2,000,000 fare.
        (_, MovementFilter filter) = NewPair();
        filter.WealthProvider = () => 2_000_000;
        Assert.False(filter.DescribeBoatBlock(Boat(0, 2_000_000)).HasFlag(ExitBlockReason.Fare));
    }

    [Fact]
    public void Boat_ThePartysCheckGatesFare()
    {
        (_, MovementFilter filter) = NewPair();
        // Leading a party one of whom can't pay the fare and isn't paid for: the
        // captain would leave them at the dock, so the sailing isn't routable. The
        // check is asked about the sailing as a fare into its port.
        filter.PartyWealthProvider = () => 1_500_000;
        filter.WealthProvider = () => 9_000_000;   // leader is rich
        Assert.Equal(ExitBlockReason.None, filter.DescribeBoatBlock(Boat(0, 2_000_000)));
        filter.PartyTollClosedProbe = exit => exit.FareCopper == 2_000_000;
        Assert.Equal(ExitBlockReason.Fare, filter.DescribeBoatBlock(Boat(0, 2_000_000)));
    }

    [Fact]
    public void Boat_PartyLevelBoundsGateMinLevel()
    {
        (_, MovementFilter filter) = NewPair();
        // Party's lowest member is under the sailing's level floor.
        filter.PartyLevelBoundsProvider = () => (Low: 45, High: 70);
        filter.WealthProvider = () => 9_000_000;
        Assert.Equal(ExitBlockReason.Level, filter.DescribeBoatBlock(Boat(50, 2_000_000)));
    }

    [Fact]
    public void Boat_CheckabilityNotGated()
    {
        (_, MovementFilter filter) = NewPair();
        filter.LevelProvider = () => 70;
        filter.WealthProvider = () => 9_000_000;
        // RequiresCheckability=true is the user's responsibility (client can't
        // read the quest flag) — it must NOT block an otherwise-clearable boat.
        Assert.True(filter.IsBoatPassable(Boat(65, 6_000_000, checkability: true)));
    }

    [Fact]
    public void WarmForBoat_LeadingParty_FiresBothProbes()
    {
        (_, MovementFilter filter) = NewPair();
        int wealth = 0, level = 0;
        filter.PartyWealthProvider = () => 100;
        filter.PartyLevelBoundsProvider = () => (Low: 10, High: 20);
        filter.WealthWarmProbe = () => wealth++;
        filter.LevelWarmProbe = () => level++;

        filter.WarmForBoat();

        Assert.Equal(1, wealth);
        Assert.Equal(1, level);
    }

    [Fact]
    public void WarmForBoat_Solo_FiresNothing()
    {
        (_, MovementFilter filter) = NewPair();
        int probes = 0;
        // No party providers set → solo, gates on own live wallet/level, no
        // round-trip needed.
        filter.WealthWarmProbe = () => probes++;
        filter.LevelWarmProbe = () => probes++;

        filter.WarmForBoat();

        Assert.Equal(0, probes);
    }
}
