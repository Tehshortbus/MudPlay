using MudPlay.Game.GameData;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A set exported with a Messages table (Nightmare Redux 1.8.4) lays its realm's own
// lines over the built-in Stock catalogue, the way the client loads a set: the table
// file beside the set's other tables, the catalogue built by MessageStore.Load.
public sealed class MdbMessageOverlayTests : IDisposable
{
    private readonly string _set = "mdb-messages-" + Guid.NewGuid().ToString("N");
    private readonly string _dir;

    public MdbMessageOverlayTests()
    {
        ScratchRoot.Require();
        AppPaths.ExtractEmbeddedSeeds(AppPaths.BundledSeedsDir);
        _dir = AppPaths.GameDataSetDir(_set);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Spells.json"), Spells);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    // Stock's bless (14) and resist cold (6), a hold of a realm of its own (5000), a
    // monster's spell whose cast line is nearly all placeholders (5001), and a second
    // bless under a number the built-in catalogue links nothing to (5002).
    private const string Spells =
        """
        [{"Number":14,"Name":"bless","Abil-0":0},
         {"Number":6,"Name":"resist cold","Abil-0":0},
         {"Number":5000,"Name":"frost chains","Abil-0":74,"AbilVal-0":0,"Abil-1":71,"AbilVal-1":100},
         {"Number":5001,"Name":"evil random","Abil-0":1},
         {"Number":5002,"Name":"bless","Abil-0":0}]
        """;

    private static string Row(string name, string kind, string msg, string endMsg, int spell, string effects = None) =>
        $$"""{"ID":1,"Name":"{{name}}","Kind":"{{kind}}","Msg":"{{msg}}","EndMsg":"{{endMsg}}","Effects":"{{effects}}","Spell":{{spell}}}""";

    // The export's empty value, a lone NUL, as the importer writes it.
    private const string None = "\\u0000";

    private MessageStore Load(params string[] rows)
    {
        if (rows.Length > 0)
            File.WriteAllText(Path.Combine(_dir, MdbMessageOverlay.TableName + ".json"), "[" + string.Join(",", rows) + "]");
        GameDataCache cache = new();
        cache.SwitchSet(_set);
        MessageStore store = new(new LogService(), cache);
        store.Load(_set);
        return store;
    }

    private static MessageRecord Linked(MessageStore store, int spell) =>
        store.Messages.First(m => m.Links?.Any(l => l.Table == "Spells" && l.Number == spell) == true);

    [Fact]
    public void ALineTheRealmWordsItsOwnWay_ReplacesTheBuiltInOne_AndTheBystandersLineStays()
    {
        MessageRecord builtIn = Linked(Load(), 6);

        MessageStore store = Load(
            Row("resist cold", "Self", "A frigid shield of ice forms around your body!",
                "The shield suddenly vanishes with an audible pop!", 6));

        MessageRecord record = Linked(store, 6);
        Assert.Equal("A frigid shield of ice forms around your body!", record.AppliedMessage);
        Assert.Equal("The shield suddenly vanishes with an audible pop!", record.AppliedEndsWith);
        Assert.Equal(builtIn.WitnessMessage, record.WitnessMessage);
        Assert.Equal(builtIn.CasterMessage, record.CasterMessage);
        Assert.Equal(builtIn.Id, record.Id);
        Assert.StartsWith("from the set's Messages table: 2 lines on 1 records", store.RealmLines, StringComparison.Ordinal);
    }

    // The table writes the spell's name out and keeps the closing "!"; the built-in
    // record says the same with {spellname} and without it, and is kept as it is.
    [Fact]
    public void ALineThatReadsTheSame_KeepsTheBuiltInForm()
    {
        MessageRecord builtIn = Linked(Load(), 14);

        MessageStore store = Load(
            Row("bless", "Cast", "You cast bless on {target}!", None, 14),
            Row("bless", "Self", "You feel lucky!", "The effects of bless wear off!", 14));

        MessageRecord record = Linked(store, 14);
        Assert.Equal(builtIn.Id, record.Id);
        Assert.Equal(builtIn.CasterMessage, record.CasterMessage);
        Assert.Equal(builtIn.AppliedMessage, record.AppliedMessage);
        Assert.Equal(builtIn.AppliedEndsWith, record.AppliedEndsWith);
        Assert.StartsWith("from the set's Messages table: 0 lines on 0 records", store.RealmLines, StringComparison.Ordinal);
    }

    [Fact]
    public void ACastLineThatDiffers_PinsTheSpellByName()
    {
        MessageRecord record = Linked(Load(Row("bless", "Cast", "You call down a blessing on {target}, bless!", None, 14)), 14);

        Assert.Equal("You call down a blessing on {target}, {spellname}!", record.CasterMessage);
    }

    // The user's edit is kept against the built-in record's Id, which the realm's
    // line leaves alone.
    [Fact]
    public void TheUsersEditToARecord_StillAppliesOverTheRealmsLine()
    {
        MessageStore before = Load();
        MessageRecord builtIn = Linked(before, 6);
        int at = before.Messages.IndexOf(builtIn);
        before.Messages[at] = builtIn with { Flags = MessageFlags.Disabled };
        before.Save();

        MessageRecord record = Linked(Load(
            Row("resist cold", "Self", "A frigid shield of ice forms around your body!",
                "The shield suddenly vanishes with an audible pop!", 6)), 6);

        Assert.Equal("A frigid shield of ice forms around your body!", record.AppliedMessage);
        Assert.Equal(MessageFlags.Disabled, record.Flags);
    }

    // A spell of the realm's own, with HoldPerson and Confusion: a hold, not a
    // confusion (GAME_MECHANICS "Condition Effects flags derive from the linked
    // spell's ability codes").
    [Fact]
    public void ASpellTheCatalogueLacks_GetsARecord_WithTheFlagsItsCodesProve()
    {
        MessageStore store = Load(
            Row("frost chains", "Cast", "You cast frost chains on {target} for {dmg} damage!", None, 5000),
            Row("frost chains", "Self", "Chains of frost bind your legs!", "The frost chains melt away.", 5000));

        MessageRecord record = Linked(store, 5000);
        Assert.Equal("frost chains", record.Name);
        Assert.Equal("You cast {spellname} on {target} for {damage} damage!", record.CasterMessage);
        Assert.Equal("Chains of frost bind your legs!", record.AppliedMessage);
        Assert.Equal("The frost chains melt away.", record.AppliedEndsWith);
        Assert.Equal(MessageFlags.MovementPrevented, record.Flags);
        Assert.EndsWith("1 records added", store.RealmLines, StringComparison.Ordinal);
    }

    // A spell the catalogue links no record to, named like one it has, starts from
    // that record: the lines the table doesn't carry (the bystander's) stay.
    [Fact]
    public void ASpellNamedLikeABuiltInRecord_KeepsThatRecordsOtherLines()
    {
        MessageRecord builtIn = Linked(Load(), 14);

        MessageRecord record = Linked(Load(Row("bless", "Cast", "You bless {target} with holy water!", None, 5002)), 5002);

        Assert.Equal("You {spellname} {target} with holy water!", record.CasterMessage);
        Assert.Equal(builtIn.WitnessMessage, record.WitnessMessage);
        Assert.Equal(builtIn.AppliedMessage, record.AppliedMessage);
    }

    // A second cast line (the one printed once the spell lands) gets a record of its
    // own, after the one the spell's lines already have, which keeps the line it
    // reads the same as wherever the table puts it.
    [Fact]
    public void ASecondCastLine_IsKeptBesideTheFirst()
    {
        MessageStore store = Load(
            Row("bless", "Cast", "You raise a hand to the heavens!", None, 14),
            Row("bless", "Cast", "You cast bless on {target}!", None, 14));

        List<MessageRecord> bless = store.Messages
            .Where(m => m.Links?.Any(l => l.Table == "Spells" && l.Number == 14) == true).ToList();
        Assert.Equal(2, bless.Count);
        Assert.Equal("You cast {spellname} on {target}!", bless[0].CasterMessage);
        Assert.Equal("You raise a hand to the heavens!", bless[1].CasterMessage);
    }

    // Lines that name no spell (a trap here) are left out, as is a line with almost
    // no text of its own, which would match nearly any line.
    [Fact]
    public void RowsOfNoSpell_AndLinesOfNearlyNoText_AreLeftOut()
    {
        int builtIn = Load().Messages.Count;

        MessageStore store = Load(
            Row("blades", "Self", "As you walk through the passage, blades sweep out and slice you!", None, 0, "Damage,Trap"),
            Row("evil random", "Cast", "evil random {target} {dmg}", None, 5001));

        Assert.Equal(builtIn, store.Messages.Count);
        Assert.EndsWith("0 records added", store.RealmLines, StringComparison.Ordinal);
    }

    // Another exporter's table of that name, with other columns, is passed over.
    [Fact]
    public void ATableOfAnotherShape_IsPassedOver()
    {
        int builtIn = Load().Messages.Count;
        File.WriteAllText(Path.Combine(_dir, MdbMessageOverlay.TableName + ".json"),
            """[{"Number":1,"Name":"hello","Text":"You feel lucky!"}]""");

        MessageStore store = Load();

        Assert.Equal(builtIn, store.Messages.Count);
        Assert.Contains("isn't one this client reads (it has no Kind column)", store.RealmLines, StringComparison.Ordinal);
    }
}
