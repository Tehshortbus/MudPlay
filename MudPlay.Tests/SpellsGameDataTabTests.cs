using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// Spells tab dialog behaviour: the default-selected tab (player-castable
// spells open on User Definitions; room/item/monster spells open on Game
// Data) and the plain-English "Effect" summary row.
public sealed class SpellsGameDataTabTests : IDisposable
{
    private readonly string _root;
    private readonly GameDataCache _cache;

    public SpellsGameDataTabTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-spelltab-" + Path.GetRandomFileName());
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
        Id: string.Empty, Name: name, Flags: MessageFlags.None, RawFlagsHex: 0, CasterMessage: string.Empty, TargetMessage: string.Empty,
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
            Blank("fireball"), Array.Empty<MessageRecord>(),
            isNew: false, cache: null, gameDataInfo: SomeInfo);
        Assert.Equal(0, vm.InitialTabIndex);
    }

    [Fact]
    public void DefaultTab_NonPlayerSpell_OpensGameData()
    {
        // No message (room/item/monster cast) but Game Data exists → tab 1.
        var vm = new MessageEditDialogViewModel(
            Blank("freezing water"), Array.Empty<MessageRecord>(),
            isNew: true, cache: null, gameDataInfo: SomeInfo);
        Assert.Equal(1, vm.InitialTabIndex);
    }

    [Fact]
    public void DefaultTab_NoGameDataTab_StaysOnUserDefinitions()
    {
        // Plain Messages-tab edit (no Game Data tab) always stays on tab 0,
        // even for a new record.
        var vm = new MessageEditDialogViewModel(
            Blank("greeting"), Array.Empty<MessageRecord>(),
            isNew: true, cache: null, gameDataInfo: NoInfo);
        Assert.Equal(0, vm.InitialTabIndex);
    }

    // ----- Effect summary row -----

    [Fact]
    public void GameDataRows_DamageSpell_OmitsStaticDamageRows_ForTheCalculator()
    {
        // A damage spell's damage is shown by the interactive calculator (built
        // on the dialog from the formula), so the static rows suppress it: no
        // "Effect" summary, no magnitude range, no per-level "Level Scaling" row.
        Seed("Spells", "[{\"Number\":50,\"Name\":\"zap\",\"MinBase\":10,\"MaxBase\":20," +
                       "\"MaxInc\":2,\"MaxIncLVLs\":1,\"Cap\":20,\"Abil-0\":1,\"AbilVal-0\":0}]");
        _cache.SwitchSet("v1.11p");
        IReadOnlyList<GameDataInfoRow> rows = new SpellInfoRowsBuilder(_cache).Build(50);

        Assert.DoesNotContain(rows, r => r.Label == "Effect");
        Assert.DoesNotContain(rows, r => r.Label == "Level Scaling");
        Assert.DoesNotContain(rows, r => r.Label == "Damage");   // magnitude row suppressed
        Assert.DoesNotContain(rows, r => r.Value.Contains("Dmg"));
        // The plain fields still render.
        Assert.Contains(rows, r => r.Label == "Name");
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
        IReadOnlyList<GameDataInfoRow> rows = new SpellInfoRowsBuilder(_cache).Build(753);

        GameDataInfoRow avoided = rows.First(r => r.Label == "Avoided by carrying (failitem)");
        Assert.Equal("log raft [#690], wooden skiff [#691], silverbark canoe [#1181]", avoided.Value);
        // The bare "TextBlock 2750" record number is not shown.
        Assert.DoesNotContain(rows, r => r.Label == "TextBlock");
    }

    [Fact]
    public void GameDataRows_SurfaceSummons_AcrossRandomChain()
    {
        // Blackwood-forest shape: spell → textblock 9404 → random 9405 →
        // random 9406 which finally summons a monster. The walk must follow
        // the random jumps and surface the spawn.
        Seed("Spells", "[{\"Number\":1040,\"Name\":\"blackwood forest\",\"Abil-0\":148,\"AbilVal-0\":9404}]");
        Seed("TBInfo", "[" +
            "{\"Number\":9404,\"Action\":\"failitem 185:random 9405\"}," +
            "{\"Number\":9405,\"Action\":\"50:addevil 0\\n100:random 9406\"}," +
            "{\"Number\":9406,\"Action\":\"100:summon 877\"}]");
        Seed("Monsters", "[{\"Number\":877,\"Name\":\"dark treant\"}]");
        Seed("Items", "[{\"Number\":185,\"Name\":\"manhole\"}]");
        _cache.SwitchSet("v1.11p");
        IReadOnlyList<GameDataInfoRow> rows = new SpellInfoRowsBuilder(_cache).Build(1040);

        Assert.Equal("dark treant [#877]", rows.First(r => r.Label == "Summons").Value);
        Assert.Equal("manhole [#185]", rows.First(r => r.Label == "Avoided by carrying (failitem)").Value);
        // The unhelpful raw "TextBlock 9404" effect row is suppressed.
        Assert.DoesNotContain(rows, r => r.Label == "Effect" && r.Value.StartsWith("TextBlock"));
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
        IReadOnlyList<GameDataInfoRow> rows = new SpellInfoRowsBuilder(_cache).Build(760);

        Assert.DoesNotContain(rows, r => r.Label is "Avoided by carrying (failitem)" or "Requires carrying (checkitem)");
    }

    [Fact]
    public void CastBy_DropsRedundantRoomTokens_KeepsMonsterCaster()
    {
        // "Cast By" room tokens are now rendered in full by "Cast in rooms", so they're
        // dropped here — only the monster caster remains. The column stores rooms in the
        // map/room form ("Room 17/1418"), not "Room #N".
        Seed("Spells", "[{\"Number\":800,\"Name\":\"sea 1\",\"Casted By\":\"Room 17/1418, Room 17/1419, Monster #877\"}]");
        Seed("Monsters", "[{\"Number\":877,\"Name\":\"dark treant\"}]");
        _cache.SwitchSet("v1.11p");

        IReadOnlyList<GameDataInfoRow> rows = new SpellInfoRowsBuilder(_cache).Build(800);

        GameDataInfoRow castBy = rows.First(r => r.Label == "Cast By");
        Assert.Contains("dark treant", castBy.Value);
        Assert.DoesNotContain("Room", castBy.Value);
    }

    [Fact]
    public void CastBy_RoomsOnly_DropsWholeRow()
    {
        // When "Cast By" held only rooms (and a "+ more" cap), the row vanishes entirely —
        // the complete list lives in "Cast in rooms".
        Seed("Spells", "[{\"Number\":801,\"Name\":\"sea 2\",\"Casted By\":\"Room 17/1418, Room 17/1419, +\"}]");
        _cache.SwitchSet("v1.11p");

        IReadOnlyList<GameDataInfoRow> rows = new SpellInfoRowsBuilder(_cache).Build(801);
        Assert.DoesNotContain(rows, r => r.Label == "Cast By");
    }

    [Fact]
    public void CastInRooms_ShowsFirstTwentyInline_RestInOverflow()
    {
        string rooms = "[" + string.Join(",", Enumerable.Range(1, 25).Select(i =>
            $"{{\"Number\":{i},\"Map Number\":17,\"Room Number\":{1000 + i},\"Spell\":802,\"Name\":\"Crystal Lake\"}}")) + "]";
        Seed("Spells", "[{\"Number\":802,\"Name\":\"sea 3\"}]");
        Seed("Rooms", rooms);
        _cache.SwitchSet("v1.11p");

        IReadOnlyList<GameDataInfoRow> rows = new SpellInfoRowsBuilder(_cache).Build(802);

        GameDataInfoRow cir = rows.First(r => r.Label.StartsWith("Cast in rooms"));
        Assert.Equal("Cast in rooms (25)", cir.Label);
        Assert.Equal(20, cir.Links!.Count);
        Assert.True(cir.HasOverflow);
        Assert.Equal(5, cir.Overflow!.Count);
    }
}
