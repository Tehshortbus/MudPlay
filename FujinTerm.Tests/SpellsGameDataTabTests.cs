using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;
using FujinTerm.ViewModels.GameData.Tables;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Spells tab dialog behaviour: the default-selected tab (player-castable
/// spells open on User Definitions; room/item/monster spells open on Game
/// Data) and the plain-English "Effect" summary row.
/// </summary>
public sealed class SpellsGameDataTabTests : IDisposable
{
    private readonly string _root;
    private readonly GameDataCache _cache;

    public SpellsGameDataTabTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-spelltab-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _cache = new GameDataCache(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Seed(string table, string json)
    {
        string dir = Path.Combine(_root, "v1.11p");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{table}.json"), json);
    }

    // ----- default tab (MessageEditDialogViewModel.InitialTabIndex) -----

    private static MessageRecord Blank(string name) => new(
        Id: string.Empty, Name: name, Action: MessageAction.Ignore,
        Flags: MessageFlags.None, RawFlagsHex: 0, Response: string.Empty,
        CasterMessage: string.Empty, TargetMessage: string.Empty,
        WitnessMessage: string.Empty, AppliedMessage: string.Empty,
        AppliedEndsWith: string.Empty, Links: null);

    private static readonly IReadOnlyList<GameDataInfoRow> SomeInfo =
        new[] { new GameDataInfoRow("Effect", "Dmg 1–4") };
    private static readonly IReadOnlyList<GameDataInfoRow> NoInfo =
        Array.Empty<GameDataInfoRow>();

    [Fact]
    public void DefaultTab_PlayerCastableSpell_OpensUserDefinitions()
    {
        // Existing message (player casts it) → User Definitions (tab 0).
        var vm = new MessageEditDialogViewModel(
            Blank("fireball"), SettingsTier.Defaults, Array.Empty<MessageRecord>(),
            isNew: false, cache: null, gameDataInfo: SomeInfo);
        Assert.Equal(0, vm.InitialTabIndex);
    }

    [Fact]
    public void DefaultTab_NonPlayerSpell_OpensGameData()
    {
        // No message (room/item/monster cast) but Game Data exists → tab 1.
        var vm = new MessageEditDialogViewModel(
            Blank("freezing water"), SettingsTier.Defaults, Array.Empty<MessageRecord>(),
            isNew: true, cache: null, gameDataInfo: SomeInfo);
        Assert.Equal(1, vm.InitialTabIndex);
    }

    [Fact]
    public void DefaultTab_NoGameDataTab_StaysOnUserDefinitions()
    {
        // Plain Messages-tab edit (no Game Data tab) always stays on tab 0,
        // even for a new record.
        var vm = new MessageEditDialogViewModel(
            Blank("greeting"), SettingsTier.Defaults, Array.Empty<MessageRecord>(),
            isNew: true, cache: null, gameDataInfo: NoInfo);
        Assert.Equal(0, vm.InitialTabIndex);
    }

    // ----- Effect summary row -----

    [Fact]
    public void GameDataRows_LeadWithPlainEnglishEffect()
    {
        // A damage spell renders an "Effect" row translating the ability
        // codes ("Dmg 10–20") instead of only raw fields.
        Seed("Spells", "[{\"Number\":50,\"Name\":\"zap\",\"MinBase\":10,\"MaxBase\":20," +
                       "\"Abil-0\":1,\"AbilVal-0\":0}]");
        _cache.SwitchSet("v1.11p");

        var vm = new SpellsSectionViewModel(_cache);
        IReadOnlyList<GameDataInfoRow> rows = vm.BuildSpellInfoRowsForTests(50);

        GameDataInfoRow effect = rows.First(r => r.Label == "Effect");
        Assert.Contains("Dmg", effect.Value);
        Assert.Contains("10", effect.Value);
        Assert.Contains("20", effect.Value);
    }

    // ----- textblock item gate (the "silver river" case) -----

    [Fact]
    public void GameDataRows_SurfaceTextblockItemGate()
    {
        // silver-river shape: spell executes textblock 2750, whose action
        // casts "battered" UNLESS the player holds a raft (failitem). The
        // raft items must surface as "Avoided by carrying".
        Seed("Spells", "[{\"Number\":753,\"Name\":\"silver river\",\"Abil-0\":148,\"AbilVal-0\":2750}]");
        Seed("TBInfo", "[{\"Number\":2750,\"Action\":\"failitem 690:failitem 691:failitem 1181:message 2096:cast 754\"}]");
        Seed("Items", "[{\"Number\":690,\"Name\":\"log raft\"}," +
                      "{\"Number\":691,\"Name\":\"wooden skiff\"}," +
                      "{\"Number\":1181,\"Name\":\"silverbark canoe\"}]");
        _cache.SwitchSet("v1.11p");

        var vm = new SpellsSectionViewModel(_cache);
        IReadOnlyList<GameDataInfoRow> rows = vm.BuildSpellInfoRowsForTests(753);

        GameDataInfoRow avoided = rows.First(r => r.Label == "Avoided by carrying");
        Assert.Equal("log raft, wooden skiff, silverbark canoe", avoided.Value);
        // The bare "TextBlock 2750" record number is not shown.
        Assert.DoesNotContain(rows, r => r.Label == "TextBlock");
    }

    [Fact]
    public void GameDataRows_NoItemGate_WhenTextblockDoesntCast()
    {
        // A textblock that only gives an item (no cast) is a quest hook, not
        // a damage gate — its checkitem/failitem must NOT surface here.
        Seed("Spells", "[{\"Number\":760,\"Name\":\"quest hook\",\"Abil-0\":148,\"AbilVal-0\":3000}]");
        Seed("TBInfo", "[{\"Number\":3000,\"Action\":\"checkitem 690 5:giveitem 700:message 9\"}]");
        Seed("Items", "[{\"Number\":690,\"Name\":\"log raft\"}]");
        _cache.SwitchSet("v1.11p");

        var vm = new SpellsSectionViewModel(_cache);
        IReadOnlyList<GameDataInfoRow> rows = vm.BuildSpellInfoRowsForTests(760);

        Assert.DoesNotContain(rows, r => r.Label is "Avoided by carrying" or "Requires carrying");
    }
}
