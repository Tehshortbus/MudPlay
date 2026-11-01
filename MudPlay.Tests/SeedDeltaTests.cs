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

// The per-set message files store only the user's delta against the shipped seed, so
// seed fixes keep reaching every record the user hasn't changed. Exercises the real
// MessageStore / MonsterMessageStore delta specs against scratch files in a temp dir.
public sealed class SeedDeltaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-seeddelta-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "messages.json");

    private static SeedDelta<MessageRecord> Delta => MessageStore.Delta;

    public SeedDeltaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static MessageRecord Rec(string name, string caster, int spell,
        MessageFlags flags = MessageFlags.None, string? id = null) => new(
        Id: id ?? MessageRecord.ComputeId(name, caster, "", "", "", ""),
        Name: name, Flags: flags, RawFlagsHex: (ushort)flags,
        CasterMessage: caster, TargetMessage: "", WitnessMessage: "",
        AppliedMessage: "", AppliedEndsWith: "",
        Links: new[] { new GameDataLink("Spells", spell) });

    // What a user edit of the text produces: a new Id, same links.
    private static MessageRecord EditText(MessageRecord r, string caster) => Rec(r.Name, caster, r.Links![0].Number, r.Flags);

    private static MessageRecord? Find(IEnumerable<MessageRecord> records, int spell)
        => records.FirstOrDefault(r => r.Links!.Any(l => l.Number == spell));

    private List<MessageRecord> SaveThenLoad(IEnumerable<MessageRecord> current, IReadOnlyList<MessageRecord> seedAtSave,
        IReadOnlyList<MessageRecord> seedAtLoad)
    {
        Delta.Save(File_, current, seedAtSave, log: null, "Messages");
        return Delta.Load(File_, seedAtLoad, log: null, "Messages")!;
    }

    [Fact]
    public void Save_WritesOnlyTheDelta_AndLoadRebuildsTheCatalogue()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        MessageRecord b = Rec("bravo", "You cast bravo.", 2);
        MessageRecord c = Rec("charlie", "You cast charlie.", 3);
        MessageRecord[] seed = { a, b, c };

        MessageRecord bFlagged = b with { Flags = MessageFlags.Poisoned, RawFlagsHex = 4 };
        MessageRecord d = Rec("delta", "You cast delta.", 4);
        MessageRecord[] current = { a, bFlagged, d };   // c deleted, d added

        SeedDelta<MessageRecord>.DeltaFile delta = Delta.Diff(current, seed);
        Assert.Equal(2, delta.Format);
        Assert.Equal(d, Assert.Single(delta.Records));
        Assert.Equal(MessageFlags.Poisoned, Assert.Single(delta.Overrides).Flags);
        Assert.Equal($"{c.Id}|spells#3", Assert.Single(delta.Removed));

        List<MessageRecord> loaded = SaveThenLoad(current, seed, seed);
        Assert.Equal(new[] { "alpha", "bravo", "delta" }, loaded.Select(r => r.Name));
        Assert.Equal(MessageFlags.Poisoned, Find(loaded, 2)!.Flags);
    }

    [Fact]
    public void UntouchedSeedRecord_ReceivesInPlaceSeedTextFix()
    {
        MessageRecord a = Rec("alpha", "old alpha line", 1);
        MessageRecord b = Rec("bravo", "You cast bravo.", 2);
        MessageRecord[] oldSeed = { a, b };
        MessageRecord[] current = { a, EditText(b, "user bravo line") };

        // The seed fixes alpha's text but keeps its Id — the arcane-assault shape.
        MessageRecord[] newSeed = { a with { CasterMessage = "fixed alpha line" }, b };

        List<MessageRecord> loaded = SaveThenLoad(current, oldSeed, newSeed);
        Assert.Equal("fixed alpha line", Find(loaded, 1)!.CasterMessage);
        Assert.Equal("user bravo line", Find(loaded, 2)!.CasterMessage);
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public void UserTextEdit_SurvivesASeedChangeToTheSameRecord()
    {
        MessageRecord a = Rec("alpha", "seed alpha line", 1);
        MessageRecord[] current = { EditText(a, "my alpha line") };
        MessageRecord[] newSeed = { a with { CasterMessage = "seed fixed alpha line" } };

        List<MessageRecord> loaded = SaveThenLoad(current, new[] { a }, newSeed);
        MessageRecord only = Assert.Single(loaded);
        Assert.Equal("my alpha line", only.CasterMessage);
    }

    [Fact]
    public void UserDeletion_StaysDeleted_WhileNewSeedRecordsArrive()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        MessageRecord b = Rec("bravo", "You cast bravo.", 2);
        MessageRecord e = Rec("echo", "You cast echo.", 5);
        MessageRecord[] newSeed = { a, b with { CasterMessage = "fixed bravo" }, e };

        List<MessageRecord> loaded = SaveThenLoad(new[] { a }, new[] { a, b }, newSeed);
        Assert.Equal(new[] { "alpha", "echo" }, loaded.Select(r => r.Name));
    }

    [Fact]
    public void FlagOverride_KeepsTheSeedsFixedText()
    {
        MessageRecord a = Rec("alpha", "old alpha line", 1);
        MessageRecord[] current = { a with { Flags = MessageFlags.Blinded, RawFlagsHex = 1, CastResponse = "^M" } };
        MessageRecord[] newSeed = { a with { CasterMessage = "fixed alpha line" } };

        MessageRecord only = Assert.Single(SaveThenLoad(current, new[] { a }, newSeed));
        Assert.Equal("fixed alpha line", only.CasterMessage);
        Assert.Equal(MessageFlags.Blinded, only.Flags);
        Assert.Equal("^M", only.CastResponse);
    }

    [Fact]
    public void SharedSeedId_IsAddressedByItsLinks()
    {
        // The seeds reuse one Id across copies that differ only in Links (priest + druid).
        MessageRecord priest = Rec("barkskin", "You feel strange.", 34);
        MessageRecord druid = priest with { Links = new[] { new GameDataLink("Spells", 422) } };
        Assert.Equal(priest.Id, druid.Id);
        MessageRecord[] seed = { priest, druid };

        MessageRecord druidFlagged = druid with { Flags = MessageFlags.Diseased, RawFlagsHex = 0x40 };
        List<MessageRecord> loaded = SaveThenLoad(new[] { druidFlagged }, seed, seed);

        MessageRecord only = Assert.Single(loaded);
        Assert.Equal(422, only.Links![0].Number);
        Assert.Equal(MessageFlags.Diseased, only.Flags);
    }

    [Fact]
    public void OverrideFollowsAUniqueSeedRecordTheSeedReLinked()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        MessageRecord[] current = { a with { Flags = MessageFlags.Confused, RawFlagsHex = 2 } };
        MessageRecord[] newSeed = { a with { Links = new[] { new GameDataLink("Spells", 1), new GameDataLink("Spells", 9) } } };

        MessageRecord only = Assert.Single(SaveThenLoad(current, new[] { a }, newSeed));
        Assert.Equal(MessageFlags.Confused, only.Flags);
    }

    [Fact]
    public void OwnRecordFoldedIntoTheSeed_IsNotListedTwice()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        MessageRecord mine = Rec("zulu", "You cast zulu.", 26);

        List<MessageRecord> loaded = SaveThenLoad(new[] { a, mine }, new[] { a }, new[] { a, mine });
        Assert.Equal(new[] { "alpha", "zulu" }, loaded.Select(r => r.Name));
    }

    [Fact]
    public void LegacySnapshot_MigratesToDelta_WithBackup()
    {
        const string aslt = "27edbd2edd40c1cb";
        MessageRecord oldAslt = Rec("arcane assault", "old caster line", 5083, id: aslt);
        MessageRecord seedS = Rec("sierra", "You cast sierra.", 19);
        MessageRecord editedS = EditText(seedS, "my sierra line");          // same Name + Links, new Id
        MessageRecord mine = Rec("uniform", "You cast uniform.", 21);
        File.WriteAllText(File_, System.Text.Json.JsonSerializer.Serialize(
            new List<MessageRecord> { oldAslt, editedS, mine }, JsonStore.Options));
        string legacyText = File.ReadAllText(File_);

        MessageRecord newAslt = oldAslt with { CasterMessage = "Pure energy rips through {target}!" };
        MessageRecord addedToSeed = Rec("november", "You cast november.", 14);
        MessageRecord renamedLinks = Rec("sierra", "You cast sierra two.", 91);   // same Name, other Links
        MessageRecord[] seed = { newAslt, seedS, addedToSeed, renamedLinks };

        List<MessageRecord> loaded = Delta.Load(File_, seed, log: null, "Messages")!;

        Assert.Equal("Pure energy rips through {target}!", Find(loaded, 5083)!.CasterMessage);
        Assert.Equal("my sierra line", Find(loaded, 19)!.CasterMessage);      // seed copy removed, user copy kept
        Assert.Single(loaded, r => r.Links!.Any(l => l.Number == 19));
        Assert.NotNull(Find(loaded, 21));                                     // user's own kept
        Assert.NotNull(Find(loaded, 14));                                     // added to the seed since
        Assert.NotNull(Find(loaded, 91));                                     // same Name, other Links → kept
        Assert.Equal(5, loaded.Count);

        Assert.Equal(legacyText, File.ReadAllText(File_ + ".pre-delta"));
        SeedDelta<MessageRecord>.DeltaFile rewritten = JsonStore.Load<SeedDelta<MessageRecord>.DeltaFile>(File_)!;
        Assert.Equal(2, rewritten.Format);
        Assert.Equal(new[] { "my sierra line", "You cast uniform." }, rewritten.Records.Select(r => r.CasterMessage));
        Assert.Equal($"{seedS.Id}|spells#19", Assert.Single(rewritten.Removed));

        // Reloading the rewritten file gives the same catalogue.
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(loaded, JsonStore.Options),
            System.Text.Json.JsonSerializer.Serialize(Delta.Load(File_, seed, log: null, "Messages"), JsonStore.Options));
    }

    [Fact]
    public void LegacySnapshot_WithNoSeed_LoadsAsIs_AndIsNotRewritten()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        JsonStore.Save(File_, new List<MessageRecord> { a });
        string before = File.ReadAllText(File_);

        Assert.Equal(a.Id, Assert.Single(Delta.Load(File_, Array.Empty<MessageRecord>(), log: null, "Messages")!).Id);
        Assert.Equal(before, File.ReadAllText(File_));
        Assert.False(File.Exists(File_ + ".pre-delta"));
    }

    // Where file names ignore case, a database's own Messages table was imported over
    // messages.json, and its rows were migrated as the user's records with no Id: the
    // Spells tab's seed comparison then threw on opening (Crash-20261101-094634 / -094635,
    // Crash-20261101-100406, Crash-20261101-100632).
    private const string TableRows = """[{ "Number": 1, "Line1": "a table row" }, { "Number": 2, "Line1": "another" }]""";

    [Fact]
    public void TableRowsMigratedAsOwnRecords_AreDropped_AndTheFileWrittenBack()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        MessageRecord mine = Rec("uniform", "You cast uniform.", 21);
        File.WriteAllText(File_, $$"""{ "Format": 2, "Removed": [], "Overrides": [], "Records": [ {{System.Text.Json.JsonSerializer.Serialize(mine, JsonStore.Options)}}, {{TableRows[1..^1]}} ] }""");

        List<MessageRecord> loaded = Delta.Load(File_, new[] { a }, log: null, "Messages")!;

        Assert.Equal(new[] { "alpha", "uniform" }, loaded.Select(r => r.Name));
        Assert.Equal(SeedDifferenceKind.Added, Assert.Single(Delta.Compare(loaded, new[] { a })).Kind);
        Assert.Equal(mine.Id, Assert.Single(JsonStore.Load<SeedDelta<MessageRecord>.DeltaFile>(File_)!.Records).Id);
    }

    [Fact]
    public void ATableImportedOverTheFile_LoadsAsTheSeed()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        File.WriteAllText(File_, TableRows);

        List<MessageRecord> loaded = Delta.Load(File_, new[] { a }, log: null, "Messages")!;

        Assert.Equal(a, Assert.Single(loaded));
        Assert.Empty(Delta.Compare(loaded, new[] { a }));
    }

    [Fact]
    public void MissingOrCorruptFile_ReturnsNull()
    {
        Assert.Null(Delta.Load(File_, Array.Empty<MessageRecord>(), log: null, "Messages"));
        File.WriteAllText(File_, "{ not json");
        Assert.Null(Delta.Load(File_, Array.Empty<MessageRecord>(), log: null, "Messages"));
    }

    [Fact]
    public void MonsterNames_UserPlaceholderSurvives_SeedAdditionsArrive()
    {
        SeedDelta<MonsterMessageRecord> monsters = MonsterMessageStore.Delta;
        MonsterMessageRecord rat = new("r1", "giant rat", new[] { new GameDataLink("Monsters", 1) });
        MonsterMessageRecord placeholder = new("p1", "odd thing", null);
        MonsterMessageRecord newcomer = new("n1", "new beast", new[] { new GameDataLink("Monsters", 2) });

        monsters.Save(File_, new[] { rat, placeholder }, new[] { rat }, log: null, "MonsterMessages");
        List<MonsterMessageRecord> loaded = monsters.Load(File_, new[] { rat, newcomer }, log: null, "MonsterMessages")!;

        Assert.Equal(new[] { "giant rat", "new beast", "odd thing" }, loaded.Select(r => r.Name));
    }

    [Fact]
    public void ShippedSeeds_UneditedCatalogue_SavesAnEmptyDelta()
    {
        AppPaths.ExtractEmbeddedSeeds(_dir);
        foreach (string realm in new[] { "stock", "paradigm" })
        {
            List<MessageRecord> seed = JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, $"Messages.{realm}.seed.json"))!;
            SeedDelta<MessageRecord>.DeltaFile delta = Delta.Diff(seed, seed);
            Assert.Empty(delta.Records);
            Assert.Empty(delta.Overrides);
            Assert.Empty(delta.Removed);
            Assert.Equal(seed, Delta.Merge(seed, delta).Records);
        }

        List<MonsterMessageRecord> monsters =
            JsonStore.Load<List<MonsterMessageRecord>>(Path.Combine(_dir, "MonsterMessages.seed.json"))!;
        SeedDelta<MonsterMessageRecord>.DeltaFile monsterDelta = MonsterMessageStore.Delta.Diff(monsters, monsters);
        Assert.Empty(monsterDelta.Records);
        Assert.Empty(monsterDelta.Overrides);
        Assert.Empty(monsterDelta.Removed);
    }

    // ----- Compare with seed / revert ---------

    [Fact]
    public void Compare_ListsEachKindOfDifference()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        MessageRecord b = Rec("bravo", "You cast bravo.", 2);
        MessageRecord c = Rec("charlie", "You cast charlie.", 3);
        MessageRecord d = Rec("delta", "You cast delta.", 4);
        MessageRecord e = Rec("echo", "You cast echo.", 5);
        MessageRecord[] seed = { a, b, c, d, e };

        MessageRecord bEdited = EditText(b, "my bravo line");
        MessageRecord cFlagged = c with { Flags = MessageFlags.Blinded, RawFlagsHex = 1 };
        MessageRecord mine = Rec("zulu", "You cast zulu.", 26);
        // Name and text both edited: only the shared Links tie it to echo.
        MessageRecord eRenamed = Rec("echo two", "You cast echo two.", 5);
        MessageRecord[] current = { a, bEdited, cFlagged, mine, eRenamed };   // d deleted

        List<SeedDelta<MessageRecord>.Difference> diffs = Delta.Compare(current, seed);

        Assert.Equal(5, diffs.Count);
        Assert.Equal(new SeedDelta<MessageRecord>.Difference(SeedDifferenceKind.Edited, b, bEdited), diffs[0]);
        Assert.Equal(new SeedDelta<MessageRecord>.Difference(SeedDifferenceKind.Override, c, cFlagged), diffs[1]);
        Assert.Equal(new SeedDelta<MessageRecord>.Difference(SeedDifferenceKind.Added, null, mine), diffs[2]);
        Assert.Equal(new SeedDelta<MessageRecord>.Difference(SeedDifferenceKind.Edited, e, eRenamed), diffs[3]);
        Assert.Equal(new SeedDelta<MessageRecord>.Difference(SeedDifferenceKind.Removed, d, null), diffs[4]);
    }

    [Fact]
    public void Compare_UneditedCatalogue_HasNoDifferences()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        MessageRecord b = Rec("bravo", "You cast bravo.", 2);
        Assert.Empty(Delta.Compare(new[] { a, b }, new[] { a, b }));
    }

    [Fact]
    public void Compare_SharedSeedId_TellsTheCopiesApartByLinks()
    {
        MessageRecord priest = Rec("barkskin", "You feel strange.", 34);
        MessageRecord druid = priest with { Links = new[] { new GameDataLink("Spells", 422) } };
        MessageRecord[] seed = { priest, druid };

        // The druid copy's flags edited; the priest copy deleted.
        MessageRecord druidFlagged = druid with { Flags = MessageFlags.Diseased, RawFlagsHex = 0x40 };
        List<SeedDelta<MessageRecord>.Difference> diffs = Delta.Compare(new[] { druidFlagged }, seed);

        Assert.Equal(2, diffs.Count);
        Assert.Equal(new SeedDelta<MessageRecord>.Difference(SeedDifferenceKind.Override, druid, druidFlagged), diffs[0]);
        Assert.Equal(new SeedDelta<MessageRecord>.Difference(SeedDifferenceKind.Removed, priest, null), diffs[1]);
    }

    [Fact]
    public void Compare_LinksAlone_DontPairWhenAmbiguous()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 7);
        MessageRecord b = Rec("bravo", "You cast bravo.", 7);
        MessageRecord mine = Rec("xray", "You cast xray.", 7);

        List<SeedDelta<MessageRecord>.Difference> diffs = Delta.Compare(new[] { mine }, new[] { a, b });

        Assert.Equal(new[] { SeedDifferenceKind.Added, SeedDifferenceKind.Removed, SeedDifferenceKind.Removed },
            diffs.Select(d => d.Kind));
    }

    [Fact]
    public void Revert_UseSeed_PutsEachMessageBackAndShrinksTheDelta()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        MessageRecord b = Rec("bravo", "You cast bravo.", 2);
        MessageRecord c = Rec("charlie", "You cast charlie.", 3);
        MessageRecord d = Rec("delta", "You cast delta.", 4);
        MessageRecord[] seed = { a, b, c, d };
        MessageRecord mine = Rec("zulu", "You cast zulu.", 26);
        MessageRecord[] current = { a, EditText(b, "my bravo line"), c with { CastResponse = "^M" }, mine };

        List<SeedDelta<MessageRecord>.Difference> diffs = Delta.Compare(current, seed);
        Assert.Equal(4, diffs.Count);

        SeedDelta<MessageRecord>.RevertResult all = Delta.Revert(current, seed, diffs);
        Assert.Equal(4, all.Reverted);
        Assert.Equal(new[] { "alpha", "bravo", "charlie", "delta" }, all.Records.Select(r => r.Name));
        Assert.Equal(seed.OrderBy(r => r.Name), all.Records.OrderBy(r => r.Name));
        Assert.Empty(Delta.Compare(all.Records, seed));
        SeedDelta<MessageRecord>.DeltaFile empty = Delta.Diff(all.Records, seed);
        Assert.Empty(empty.Records);
        Assert.Empty(empty.Overrides);
        Assert.Empty(empty.Removed);

        // Reverting only the edited copy: bravo follows the seed again; the rest stay the user's.
        SeedDelta<MessageRecord>.RevertResult one = Delta.Revert(current, seed, new[] { diffs[0] });
        Assert.Equal(1, one.Reverted);
        Assert.Same(b, one.Records[1]);
        SeedDelta<MessageRecord>.DeltaFile delta = Delta.Diff(one.Records, seed);
        Assert.DoesNotContain(delta.Records, r => r.Name == "bravo");
        Assert.DoesNotContain($"{b.Id}|spells#2", delta.Removed);
        Assert.Equal(mine, Assert.Single(delta.Records));
        Assert.Equal("^M", Assert.Single(delta.Overrides).CastResponse);
        Assert.Equal($"{d.Id}|spells#4", Assert.Single(delta.Removed));
    }

    [Fact]
    public void Revert_RestoresASharedIdSeedCopy_ByLinks()
    {
        MessageRecord priest = Rec("barkskin", "You feel strange.", 34);
        MessageRecord druid = priest with { Links = new[] { new GameDataLink("Spells", 422) } };
        MessageRecord[] seed = { priest, druid };
        MessageRecord[] current = { druid };

        SeedDelta<MessageRecord>.Difference removed = Assert.Single(Delta.Compare(current, seed));
        SeedDelta<MessageRecord>.RevertResult result = Delta.Revert(current, seed, new[] { removed });

        Assert.Equal(1, result.Reverted);
        Assert.Empty(Delta.Diff(result.Records, seed).Removed);
        Assert.Equal(2, Delta.Merge(seed, Delta.Diff(result.Records, seed)).Records.Count);
    }

    [Fact]
    public void Revert_SkipsAnEntryTheCatalogueHasMovedOnFrom()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        MessageRecord[] seed = { a };
        MessageRecord edited = EditText(a, "my alpha line");
        SeedDelta<MessageRecord>.Difference diff = Assert.Single(Delta.Compare(new[] { edited }, seed));

        // Edited again after the comparison was taken: the listed copy is gone.
        MessageRecord[] now = { EditText(a, "my newer alpha line") };
        SeedDelta<MessageRecord>.RevertResult stale = Delta.Revert(now, seed, new[] { diff });
        Assert.Equal(0, stale.Reverted);
        Assert.Equal(now, stale.Records);

        // The set switched: the seed record isn't in this seed.
        MessageRecord[] otherSeed = { Rec("alpha", "You cast alpha.", 1) };
        Assert.Equal(0, Delta.Revert(new[] { edited }, otherSeed, new[] { diff }).Reverted);
    }

    [Fact]
    public void SpellsTab_SeedStatePerSpell_CoversEachKindAndBothSidesLinks()
    {
        MessageRecord a = Rec("alpha", "You cast alpha.", 1);
        MessageRecord b = Rec("bravo", "You cast bravo.", 2);
        MessageRecord c = Rec("charlie", "You cast charlie.", 3);
        MessageRecord d = Rec("delta", "You cast delta.", 4);
        MessageRecord[] seed = { a, b, c, d };
        MessageRecord itemProc = Rec("proc", "Your blade flares.", 0) with { Links = new[] { new GameDataLink("Items", 9) } };
        // An extra message the user added for spell 1 beside its seed record.
        MessageRecord mineForAlpha = Rec("alpha", "You cast alpha loudly.", 1);
        MessageRecord[] current = { a, EditText(b, "my bravo"), c with { CastResponse = "^M" }, mineForAlpha, itemProc };

        List<SeedDelta<MessageRecord>.Difference> diffs = Delta.Compare(current, seed);
        IReadOnlyDictionary<int, string> states = SpellsSectionViewModel.SeedStatesBySpell(diffs);

        Assert.Equal("yours only", states[1]);
        Assert.Equal("text edited", states[2]);
        Assert.Equal("fields edited", states[3]);
        Assert.Equal("removed", states[4]);
        Assert.Equal(4, states.Count);   // the item-linked record belongs to no spell
        Assert.Empty(SpellsSectionViewModel.LinkedSpells(diffs.Single(x => ReferenceEquals(x.Current, itemProc))));
    }

    [Fact]
    public void MonsterNames_CompareListsAPlaceholder()
    {
        SeedDelta<MonsterMessageRecord> monsters = MonsterMessageStore.Delta;
        MonsterMessageRecord rat = new("r1", "giant rat", new[] { new GameDataLink("Monsters", 1) });
        MonsterMessageRecord placeholder = new("p1", "odd thing", null);

        SeedDelta<MonsterMessageRecord>.Difference only = Assert.Single(monsters.Compare(new[] { rat, placeholder }, new[] { rat }));
        Assert.Equal(SeedDifferenceKind.Added, only.Kind);
        Assert.Same(placeholder, only.Current);
    }

    [Fact]
    public void EditDialog_FlagOnlyEdit_KeepsAStaleSeedId_TextEditReIds()
    {
        // A seed record whose text was fixed in place: its Id no longer hashes from its text.
        MessageRecord stale = Rec("arcane assault", "Pure energy rips through {target}!", 5083, id: "27edbd2edd40c1cb");

        MessageRecord SaveVia(Action<MessageEditDialogViewModel> edit)
        {
            MessageEditDialogViewModel vm = new(stale, new[] { stale }, isNew: false, cache: null);
            MessageEditResult? result = null;
            vm.CloseRequested += r => result = r;
            edit(vm);
            vm.SaveCommand.Execute(null);
            return Assert.IsType<MessageEditResult>(result).Updated;
        }

        Assert.Equal("27edbd2edd40c1cb", SaveVia(vm => vm.FlagBlinded = true).Id);
        MessageRecord retexted = SaveVia(vm => vm.CasterMessage = "something else");
        Assert.Equal(MessageRecord.ComputeId("arcane assault", "something else", "", "", "", ""), retexted.Id);
    }
}
