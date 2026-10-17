using System.IO;
using System.Text.Json;
using MudPlay.Game.Health;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// A realm's hang-up penalty is a record of the board's rule: how it is stored
// with the realm, kept in range, staged on the Settings BBS tab, and worded for
// the program log and the bug report. Nothing here decides a hang-up.
[Collection(BbsSectionCollection.Name)]
public sealed class HangupPenaltyTests : IDisposable
{
    private readonly List<string> _boards = new();

    private string NewBoard()
    {
        string name = "!mudplay-test-hangup-" + Path.GetRandomFileName();
        _boards.Add(name);
        return name;
    }

    public void Dispose()
    {
        foreach (string board in _boards)
        {
            try { if (Directory.Exists(AppPaths.BbsFolder(board))) Directory.Delete(AppPaths.BbsFolder(board), recursive: true); }
            catch (IOException) { /* best-effort cleanup of a scratch board */ }
        }
    }

    // ----- Storage -----------------------------------------------------

    // Off, with the HP pair at the game's own initial 25 and 50 and no items.
    [Fact]
    public void Defaults_NoPenalty_WithTheGamesInitialHpShare()
    {
        RealmProfile realm = new();

        Assert.False(realm.HangupPenaltyEnabled);
        Assert.Equal(25, realm.HangupPvpHpFromPercent);
        Assert.Equal(50, realm.HangupPvpHpToPercent);
        Assert.Equal(0, realm.HangupPvpItemsDropped);
        Assert.False(realm.HangupPvePenaltyEnabled);
        Assert.Equal(25, realm.HangupPveHpFromPercent);
        Assert.Equal(50, realm.HangupPveHpToPercent);
        Assert.Equal(0, realm.HangupPveItemsDropped);
    }

    // Through the BBS file, the way the death floor is saved: each realm of a
    // board keeps its own penalty.
    [Fact]
    public void SavedWithTheRealm_EachRealmKeepsItsOwn()
    {
        string board = NewBoard();
        new BbsProfileStore().Save(new BbsProfile
        {
            Name = board,
            Realms =
            {
                new RealmProfile { Name = "PVE", PlayerDiesAtHp = -40 },
                new RealmProfile
                {
                    Name = "PVP",
                    HangupPenaltyEnabled = true,
                    HangupPvpHpFromPercent = 10,
                    HangupPvpHpToPercent = 30,
                    HangupPvpItemsDropped = 3,
                    HangupPvePenaltyEnabled = true,
                    HangupPveHpFromPercent = 5,
                    HangupPveHpToPercent = 15,
                    HangupPveItemsDropped = 1,
                },
            },
        });

        BbsProfile loaded = new BbsProfileStore().Get(board)!;

        RealmProfile pve = loaded.RealmFor("PVE")!;
        Assert.Equal(-40, pve.PlayerDiesAtHp);
        Assert.False(pve.HangupPenaltyEnabled);
        Assert.Equal(25, pve.HangupPvpHpFromPercent);

        RealmProfile pvp = loaded.RealmFor("PVP")!;
        Assert.True(pvp.HangupPenaltyEnabled);
        Assert.Equal(10, pvp.HangupPvpHpFromPercent);
        Assert.Equal(30, pvp.HangupPvpHpToPercent);
        Assert.Equal(3, pvp.HangupPvpItemsDropped);
        Assert.True(pvp.HangupPvePenaltyEnabled);
        Assert.Equal(5, pvp.HangupPveHpFromPercent);
        Assert.Equal(15, pvp.HangupPveHpToPercent);
        Assert.Equal(1, pvp.HangupPveItemsDropped);
    }

    // A board saved before the setting existed reads as "no penalty".
    [Fact]
    public void RealmSavedWithoutTheFields_ReadsAsNoPenalty()
    {
        const string older = """ { "Name": "Old", "Realms": [ { "Name": "Old", "PlayerDiesAtHp": -30 } ] } """;

        RealmProfile realm = JsonSerializer.Deserialize<BbsProfile>(older)!.Realms[0];

        Assert.Equal(-30, realm.PlayerDiesAtHp);
        Assert.False(realm.HangupPenaltyEnabled);
        Assert.Equal(25, realm.HangupPvpHpFromPercent);
        Assert.Equal(50, realm.HangupPvpHpToPercent);
        Assert.Equal("none", HangupPenaltyNotice.Describe(realm));
    }

    // ----- Limits ------------------------------------------------------

    [Theory]
    [InlineData(25, 50, 25, 50)]
    [InlineData(40, 40, 40, 40)]
    [InlineData(60, 50, 60, 60)]     // the upper end is never under the lower
    [InlineData(-5, 20, 0, 20)]
    [InlineData(10, 250, 10, 100)]
    [InlineData(150, 10, 100, 100)]
    public void HpRange_StaysInsideZeroToHundred_WithToAtLeastFrom(int from, int to, int wantFrom, int wantTo)
    {
        Assert.Equal((wantFrom, wantTo), HangupPenaltyNotice.HpRange(from, to));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(500, 100)]
    public void Items_StaysInsideZeroToHundred(int items, int want)
    {
        Assert.Equal(want, HangupPenaltyNotice.Items(items));
    }

    // ----- The log line ------------------------------------------------

    private static RealmProfile Penalised(bool pve) => new()
    {
        HangupPenaltyEnabled = true,
        HangupPvpHpFromPercent = 25,
        HangupPvpHpToPercent = 50,
        HangupPvpItemsDropped = 3,
        HangupPvePenaltyEnabled = pve,
        HangupPveHpFromPercent = 10,
        HangupPveHpToPercent = 20,
        HangupPveItemsDropped = 0,
    };

    [Fact]
    public void LogLine_InPvp_GivesThePvpSide()
    {
        Assert.Equal(
            "This realm penalises a hang-up in PvP: 25–50% of max HP and up to 3 items.",
            HangupPenaltyNotice.ForHangup(Penalised(pve: true), pvp: true, inCombat: false));
    }

    // A fight with a player raises InCombat too; the PvP side still wins.
    [Fact]
    public void LogLine_InPvpAndInCombat_GivesThePvpSide()
    {
        Assert.Equal(
            "This realm penalises a hang-up in PvP: 25–50% of max HP and up to 3 items.",
            HangupPenaltyNotice.ForHangup(Penalised(pve: true), pvp: true, inCombat: true));
    }

    [Fact]
    public void LogLine_InCombatWithMonsters_GivesThePveSide()
    {
        Assert.Equal(
            "This realm penalises a hang-up in combat with monsters: 10–20% of max HP.",
            HangupPenaltyNotice.ForHangup(Penalised(pve: true), pvp: false, inCombat: true));
    }

    // The board penalises PvP only: a hang-up from a fight with a monster costs
    // nothing, so nothing is logged.
    [Fact]
    public void LogLine_InCombatWithMonsters_PveSwitchOff_IsNothing()
    {
        Assert.Null(HangupPenaltyNotice.ForHangup(Penalised(pve: false), pvp: false, inCombat: true));
    }

    // Neither a PvP fight nor InCombat: the client can't tell whether something
    // is attacking, so the line gives the rule and claims no side.
    [Fact]
    public void LogLine_FightUnknown_GivesTheRuleWithoutPickingASide()
    {
        Assert.Equal(
            "This realm penalises a hang-up in PvP (25–50% of max HP and up to 3 items) "
            + "and in combat with monsters (10–20% of max HP).",
            HangupPenaltyNotice.ForHangup(Penalised(pve: true), pvp: false, inCombat: false));
        Assert.Equal(
            "This realm penalises a hang-up in PvP only: 25–50% of max HP and up to 3 items.",
            HangupPenaltyNotice.ForHangup(Penalised(pve: false), pvp: false, inCombat: false));
    }

    [Fact]
    public void LogLine_MasterSwitchOff_OrNoRealm_IsNothing()
    {
        RealmProfile off = Penalised(pve: true);
        off.HangupPenaltyEnabled = false;

        Assert.Null(HangupPenaltyNotice.ForHangup(off, pvp: true, inCombat: true));
        Assert.Null(HangupPenaltyNotice.ForHangup(off, pvp: false, inCombat: false));
        Assert.Null(HangupPenaltyNotice.ForHangup(null, pvp: true, inCombat: true));
    }

    [Theory]
    [InlineData(25, 50, 0, "25–50% of max HP")]
    [InlineData(30, 30, 0, "30% of max HP")]                       // a fixed share
    [InlineData(0, 40, 1, "0–40% of max HP and up to 1 item")]
    [InlineData(0, 0, 5, "up to 5 items")]
    [InlineData(0, 0, 0, "no HP or items set")]
    [InlineData(60, 50, 0, "60% of max HP")]                       // a hand-edited file, read through the limits
    public void LogLine_WordsTheShareAndTheItems(int from, int to, int items, string want)
    {
        RealmProfile realm = new()
        {
            HangupPenaltyEnabled = true,
            HangupPvpHpFromPercent = from,
            HangupPvpHpToPercent = to,
            HangupPvpItemsDropped = items,
        };

        Assert.Equal(
            $"This realm penalises a hang-up in PvP: {want}.",
            HangupPenaltyNotice.ForHangup(realm, pvp: true, inCombat: false));
    }

    // ----- The bug-report phrase ---------------------------------------

    [Fact]
    public void Describe_ListsBothSides_OrNone()
    {
        Assert.Equal("none", HangupPenaltyNotice.Describe(null));
        Assert.Equal("none", HangupPenaltyNotice.Describe(new RealmProfile()));
        Assert.Equal(
            "PvP: 25–50% of max HP and up to 3 items; PvE: 10–20% of max HP",
            HangupPenaltyNotice.Describe(Penalised(pve: true)));
        Assert.Equal(
            "PvP: 25–50% of max HP and up to 3 items; PvE: not penalised",
            HangupPenaltyNotice.Describe(Penalised(pve: false)));
    }

    // A realm set as penalising every hang-up says so in both places; the box
    // means nothing without the PvE side it takes its figures from.
    [Fact]
    public void ARealmThatPenalisesEveryHangUp_SaysSo()
    {
        RealmProfile every = Penalised(pve: true);
        every.HangupOutsideFightPenaltyEnabled = true;

        Assert.Equal(
            "PvP: 25–50% of max HP and up to 3 items; PvE: 10–20% of max HP; outside a fight too, as PvE",
            HangupPenaltyNotice.Describe(every));
        Assert.Equal(
            "This realm penalises every hang-up: in PvP 25–50% of max HP and up to 3 items, otherwise 10–20% of max HP.",
            HangupPenaltyNotice.ForHangup(every, pvp: false, inCombat: false));

        RealmProfile pvpOnly = Penalised(pve: false);
        pvpOnly.HangupOutsideFightPenaltyEnabled = true;
        Assert.Equal(
            "PvP: 25–50% of max HP and up to 3 items; PvE: not penalised",
            HangupPenaltyNotice.Describe(pvpOnly));
        Assert.Equal(
            "This realm penalises a hang-up in PvP only: 25–50% of max HP and up to 3 items.",
            HangupPenaltyNotice.ForHangup(pvpOnly, pvp: false, inCombat: false));
    }

    // ----- Settings → BBS staging --------------------------------------

    // The realm fields are staged per board as they're edited: an edit on one
    // board has to survive a click to another and land on OK, and the board
    // clicked through must not pick the edit up.
    [Fact]
    public void BbsTab_EditOnOneBoard_SurvivesABoardSwitch_AndSavesOnApply()
    {
        string a = NewBoard();
        string b = NewBoard();
        BbsProfileStore store = new();
        store.Save(new BbsProfile { Name = a, Host = "a.example", Port = 23 });
        store.Save(new BbsProfile { Name = b, Host = "b.example", Port = 23 });
        // Started on a test board, so the view model never opens (and writes back)
        // another test's.
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile(onBbs: a);
        using BbsSectionViewModel vm = new(store, profile, new PasswordProtector(), new DisplayConfig(), new SettingsService());

        vm.SelectedBbsName = a;
        Assert.False(vm.HangupPenaltyEnabled);
        vm.HangupPenaltyEnabled = true;
        vm.HangupPvpHpFromPercent = 10;
        vm.HangupPvpHpToPercent = 30;
        vm.HangupPvpItemsDropped = 2;
        vm.HangupPvePenaltyEnabled = true;
        vm.HangupPveHpFromPercent = 5;
        vm.HangupPveHpToPercent = 15;
        vm.HangupPveItemsDropped = 1;
        vm.HangupOutsideFightPenaltyEnabled = true;
        Assert.True(vm.IsDirty);

        vm.SelectedBbsName = b;
        Assert.False(vm.HangupPenaltyEnabled);
        Assert.Equal(25, vm.HangupPvpHpFromPercent);
        Assert.Equal(50, vm.HangupPvpHpToPercent);

        vm.SelectedBbsName = a;
        Assert.True(vm.HangupPenaltyEnabled);
        Assert.Equal(10, vm.HangupPvpHpFromPercent);
        Assert.Equal(15, vm.HangupPveHpToPercent);

        vm.Apply();

        RealmProfile savedA = new BbsProfileStore().Get(a)!.Realms[0];
        Assert.True(savedA.HangupPenaltyEnabled);
        Assert.Equal(10, savedA.HangupPvpHpFromPercent);
        Assert.Equal(30, savedA.HangupPvpHpToPercent);
        Assert.Equal(2, savedA.HangupPvpItemsDropped);
        Assert.True(savedA.HangupPvePenaltyEnabled);
        Assert.Equal(5, savedA.HangupPveHpFromPercent);
        Assert.Equal(15, savedA.HangupPveHpToPercent);
        Assert.Equal(1, savedA.HangupPveItemsDropped);
        Assert.True(savedA.HangupOutsideFightPenaltyEnabled);

        RealmProfile savedB = new BbsProfileStore().Get(b)!.Realms[0];
        Assert.False(savedB.HangupPenaltyEnabled);
        Assert.False(savedB.HangupOutsideFightPenaltyEnabled);
        Assert.Equal(25, savedB.HangupPvpHpFromPercent);
    }

    // Moving one end of the HP share past the other carries the other along, so
    // the pair on screen is the pair that is saved.
    [Fact]
    public void BbsTab_HpShare_ToNeverUnderFrom()
    {
        string board = NewBoard();
        BbsProfileStore store = new();
        store.Save(new BbsProfile { Name = board, Host = "a.example", Port = 23 });
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile(onBbs: board);
        using BbsSectionViewModel vm = new(store, profile, new PasswordProtector(), new DisplayConfig(), new SettingsService());
        vm.SelectedBbsName = board;

        vm.HangupPvpHpFromPercent = 70;      // past the 50 it started under
        Assert.Equal(70, vm.HangupPvpHpToPercent);

        vm.HangupPveHpToPercent = 10;        // under the 25 it started over
        Assert.Equal(10, vm.HangupPveHpFromPercent);

        vm.Apply();

        RealmProfile saved = new BbsProfileStore().Get(board)!.Realms[0];
        Assert.Equal((70, 70), (saved.HangupPvpHpFromPercent, saved.HangupPvpHpToPercent));
        Assert.Equal((10, 10), (saved.HangupPveHpFromPercent, saved.HangupPveHpToPercent));
    }
}
