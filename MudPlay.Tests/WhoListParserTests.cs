using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class WhoListParserTests
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private static readonly DateTime Now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    // The real "Newhaven, Marrow Road" sample from the user's screenshot —
    // keeps the test honest about leading whitespace, double-spaces
    // between fields, and the family-name-touching-marker case
    // (Lenneth BoxOfRocksDumb-).
    private static readonly string[] HavenSample =
    {
        "                 Current Adventurers",
        "                 ===================",
        "        Lawful  Debbie Schwartz       - Magebane  of Mudd Life Crisis",
        "                MudPlay WuzHere         - Apprentice",
        "          Good  Ivy Leaf              - High Druid  of what happen",
        "          Good  Krow GoesKaw          - Warrior Novice",
        "          Good  Lenneth BoxOfRocksDumb- Heroine  of what happen",
        "        Lawful  Maggie May            - Illusionist  of Mudd Life Crisis",
        "        Lawful  Osiyo Myers           - Fighter Priestess  of Mudd Life Crisis",
        "        Lawful  Posc Positis          - Pickpocket  of Mudd Life Crisis",
        "        Lawful  Sabrina Myers         - Duelist  of Mudd Life Crisis",
        "        Lawful  Sister BadTouch       - High Priestess  of what happen",
        "        Lawful  Tabitha Myers         - Pastor  of Mudd Life Crisis",
        "",
    };

    // feed: the parser's own copy of the game's output; the class feed by default.
    private WhoListParser Build(out PlayerDatabase db, ServerFeed? feed = null)
    {
        db = new PlayerDatabase(new ProfileService(), () => null);
        return new WhoListParser(lines: (feed ?? _feed).Lines, db: db);
    }

    [Fact]
    public void ParsesHavenSample_RecordsEveryPlayerWithExpectedFields()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(HavenSample, Now);

        Assert.Equal(11, db.Players.Count);

        // Spot-check the unusual cases.
        AssertHas(db, given: "Debbie",  family: "Schwartz",        align: "Lawful",  title: "Magebane",          gang: "Mudd Life Crisis");
        AssertHas(db, given: "MudPlay",   family: "WuzHere",         align: "Neutral", title: "Apprentice",        gang: null);  // self, no alignment word
        AssertHas(db, given: "Lenneth", family: "BoxOfRocksDumb",  align: "Good",    title: "Heroine",           gang: "what happen");  // marker abuts family
        AssertHas(db, given: "Sister",  family: "BadTouch",        align: "Lawful",  title: "High Priestess",    gang: "what happen");
        AssertHas(db, given: "Osiyo",   family: "Myers",           align: "Lawful",  title: "Fighter Priestess", gang: "Mudd Life Crisis");  // two-word title
    }

    [Fact]
    public void IgnoresLines_OutsideAdventurersBlock()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "Forged Paradigm gossips: hello world",
            "Some random chat line - Magebane of nowhere",  // looks like a row but no header
            "Obvious exits: north, south",
        }, Now);

        Assert.Empty(db.Players);
    }

    [Fact]
    public void ReentersIdle_AfterEndOfBlock_AcceptsSecondWho()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(HavenSample, Now);
        Assert.Equal(11, db.Players.Count);

        // Second `who` call later — fresh table.
        DateTime later = Now.AddHours(1);
        _feed.Rows(new[]
        {
            "                 Current Adventurers",
            "                 ===================",
            "        Lawful  Brand New           - Pickpocket  of Mudd Life Crisis",
            "",
        }, later);

        Assert.Equal(12, db.Players.Count);
        AssertHas(db, given: "Brand", family: "New", align: "Lawful", title: "Pickpocket", gang: "Mudd Life Crisis");
    }

    [Fact]
    public void TrailingRoleMarker_M_S_V_CapturedSeparately()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "                 Current Adventurers",
            "                 ===================",
            "        Lawful  Wizzo TheMudop       - High Wizard  of the Council M",
            "                Sysop Person         - System  of nowhere S",
            "          Good  Vincent Visitor      - Traveller V",
            "",
        }, Now);

        // PlayerDatabase.Players sorts alphabetically by display name on
        // every rebuild, so look up by name rather than by insertion order.
        Assert.Equal(3, db.Players.Count);
        Assert.Equal("M", FindByGiven(db, "Wizzo")  .Role);
        Assert.Equal("S", FindByGiven(db, "Sysop")  .Role);
        Assert.Equal("V", FindByGiven(db, "Vincent").Role);
    }

    // The Stock engine ends some rows with the word EDITED, after the gang when
    // there is one. It belongs to neither the title nor the gang. The marker before
    // the title is `x` for a player with gossip off, and is read past.
    [Fact]
    public void StockRows_TrailingEdited_IsNotPartOfTheTitleOrTheGang()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "         Current Adventurers",
            "         ===================",
            "",
            "    Good Ivy Leaf  -  High Druid of what happen EDITED ",
            "         Krow GoesKaw  x  Warrior Novice EDITED ",
            "  Lawful Maggie May  -  Illusionist of Mudd Life Crisis  ",
            "",
        }, Now);

        Assert.Equal(3, db.Players.Count);
        AssertHas(db, given: "Ivy",    family: "Leaf",    align: "Good",    title: "High Druid",     gang: "what happen");
        AssertHas(db, given: "Krow",   family: "GoesKaw", align: "Neutral", title: "Warrior Novice", gang: null);
        AssertHas(db, given: "Maggie", family: "May",     align: "Lawful",  title: "Illusionist",    gang: "Mudd Life Crisis");
        Assert.Null(FindByGiven(db, "Ivy").Role);
    }

    // Stock's `set style technical` table, as a board prints it (made-up names).
    private static readonly string[] TechnicalSample =
    {
        "Title           Name                    Reputation Gang/Guild",
        "=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=",
        "Canon           Abelard Brightwtr       Lawful     Old People Only",
        "Explorer        Corin Dale              Good       None",
        "Druid Novice    Eddas Fenwicks          Neutral    None",
        "Kai Warrior     Garrick Holme           Good       Old People Only",
        "Fighter Priest  Isolde Jute             Good       None",
        "",
    };

    [Fact]
    public void TechnicalStyle_ReadsTheSameFieldsAsTheFantasyRows()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(TechnicalSample, Now);

        Assert.Equal(5, db.Players.Count);
        AssertHas(db, given: "Abelard", family: "Brightwtr", align: "Lawful",  title: "Canon",          gang: "Old People Only");
        AssertHas(db, given: "Corin",   family: "Dale",      align: "Good",    title: "Explorer",       gang: null);   // "None" is no gang
        AssertHas(db, given: "Eddas",   family: "Fenwicks",  align: "Neutral", title: "Druid Novice",   gang: null);   // Neutral is named outright
        AssertHas(db, given: "Garrick", family: "Holme",     align: "Good",    title: "Kai Warrior",    gang: "Old People Only");
        AssertHas(db, given: "Isolde",  family: "Jute",      align: "Good",    title: "Fighter Priest", gang: null);
    }

    // The same players in fantasy style: a Neutral reputation is a blank left
    // column there, and both styles record the same thing.
    [Fact]
    public void FantasyAndTechnicalStyles_AgreeOnEveryField()
    {
        WhoListParser fantasy = Build(out PlayerDatabase fantasyDb);
        _feed.Rows(new[]
        {
            "         Current Adventurers",
            "         ===================",
            "",
            "  Lawful Abelard Brightwtr     -  Canon  of Old People Only",
            "    Good Corin Dale            -  Explorer",
            "         Eddas Fenwicks        -  Druid Novice",
            "",
        }, Now);
        ServerFeed technicalFeed = new();
        WhoListParser technical = Build(out PlayerDatabase technicalDb, technicalFeed);
        technicalFeed.Rows(TechnicalSample, Now);

        foreach (string given in new[] { "Abelard", "Corin", "Eddas" })
        {
            PlayerRecord f = FindByGiven(fantasyDb, given);
            PlayerRecord t = FindByGiven(technicalDb, given);
            Assert.Equal(f.FamilyName, t.FamilyName);
            Assert.Equal(f.Alignment, t.Alignment);
            Assert.Equal(f.Title, t.Title);
            Assert.Equal(f.Gang, t.Gang);
        }
        Assert.Equal("Neutral", FindByGiven(fantasyDb, "Eddas").Alignment);
    }

    // The engine's own columns (reputation one column left of the capture's), the
    // two flag letters, a name that fills its column, a gang that fills its own with
    // the row's last field after it, and the list's end at a prompt.
    [Fact]
    public void TechnicalStyle_EngineColumns_FlagsAndFullWidthFields()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        int lists = 0;
        p.ListRead += () => lists++;
        // The engine's row format: title 15, name 20, two flags, reputation 10, gang
        // 19, last field 6.
        static string Row(string title, string name, string flags, string rep, string gang, string last) =>
            $"{title,-15} {name,-20}{flags} {rep,-10} {gang,-19} {last,-6}".TrimEnd();
        _feed.Rows(new[]
        {
            $"{"Title",-15} {"Name",-20}   {"Reputation",-10} Gang/Guild",
            "=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=",
            Row("Canon", "Abelard Brightwtr", "g ", "Lawful", "Old People Only", ""),
            Row("High Priestess", "Tessaly Wintermourne", "ga", "FIEND", "The Long Named Gang", "EDITED"),
            Row("Apprentice", "Olwen", " a", "Saint", "None", "EDITED"),
            "this row is not a row at all",
        }, Now);
        Assert.Equal(3, db.Players.Count);
        AssertHas(db, given: "Abelard", family: "Brightwtr",    align: "Lawful", title: "Canon",          gang: "Old People Only");
        AssertHas(db, given: "Tessaly", family: "Wintermourne", align: "Fiend",  title: "High Priestess", gang: "The Long Named Gang");
        AssertHas(db, given: "Olwen",   family: "",             align: "Saint",  title: "Apprentice",     gang: null);
        Assert.Equal(0, lists);                       // one stray row doesn't end the list
    }

    // A second `who` in the other style starts a fresh block of its own kind.
    [Fact]
    public void TechnicalThenFantasy_EachBlockReadInItsOwnStyle()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(TechnicalSample, Now);
        _feed.Rows(new[]
        {
            "         Current Adventurers",
            "         ===================",
            "",
            "    Good Marrow Quill          -  Warrior Novice",
            "",
        }, Now);

        Assert.Equal(6, db.Players.Count);
        AssertHas(db, given: "Marrow", family: "Quill", align: "Good", title: "Warrior Novice", gang: null);
    }

    // The technical header inside chat isn't followed by its rule: no block starts.
    [Fact]
    public void TechnicalHeader_WithoutItsRule_StartsNothing()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "Title           Name                    Reputation Gang/Guild",
            "Canon           Abelard Brightwtr       Lawful     Old People Only",
            "",
        }, Now);
        Assert.Empty(db.Players);
    }

    // Only a trailing EDITED is the engine's word. The same letters inside a gang's
    // name are the gang's.
    [Fact]
    public void GangContainingTheWordEdited_IsKeptWhole()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "         Current Adventurers",
            "         ===================",
            "",
            "         Barry                 -  Menace of The EDITED Ones",
            "         Titus Anaga           -  Grunt of EDITED",
            "         Xeeg Stat             -  Cutthroat of UNEDITED",
            "",
        }, Now);

        Assert.Equal(3, db.Players.Count);
        AssertHas(db, given: "Barry", family: "",      align: "Neutral", title: "Menace",    gang: "The EDITED Ones");
        AssertHas(db, given: "Titus", family: "Anaga", align: "Neutral", title: "Grunt",     gang: "EDITED");
        AssertHas(db, given: "Xeeg",  family: "Stat",  align: "Neutral", title: "Cutthroat", gang: "UNEDITED");
    }

    private static PlayerRecord FindByGiven(PlayerDatabase db, string given)
    {
        foreach (PlayerRecord p in db.Players)
            if (string.Equals(p.GivenName, given, StringComparison.OrdinalIgnoreCase))
                return p;
        throw new InvalidOperationException($"No player with given name '{given}' in database.");
    }

    // Verbatim live-server output (Newhaven, Narrow Road dump). Differs
    // from the earlier HavenSample in three ways the parser
    // must tolerate: (a) a blank line between the ============ separator
    // and the first row, (b) a blank line between the last row and the
    // trailing prompt, (c)   -   (two-space dash two-space) marker
    // rather than -.
    [Fact]
    public void ParsesNewhavenLiveDump_Including_BlankLines_AndTwoSpaceMarker()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "         Current Adventurers",
            "         ===================",
            "",
            "  Lawful Debbie Schwartz         -  Magebane  of Mudd Life Crisis",
            "         MudPlay WuzHere           -  Apprentice",
            "  Lawful Furnagerie Clawful      -  Acolyte  of Mudd Life Crisis",
            "  Lawful Gammi Clawful           -  Duelist  of what happen",
            "  Lawful Gampi Clawful           -  Mercenary  of Mudd Life Crisis",
            "    Good Ivy Leaf                -  High Druid  of what happen",
            "  Lawful Kitti Clawful           -  Fighter Priestess  of what happen",
            "  Lawful Kittigammi Clawful      -  Defender  of what happen",
            "    Good Krow GoesKaw            -  Warrior Novice",
            "    Good Lenneth BoxOfRocksDumb  -  Heroine  of what happen",
            "  Lawful Maggie May              -  Illusionist  of Mudd Life Crisis",
            "  Lawful Meowynator Clawful      -  Herbalist  of Mudd Life Crisis",
            "  Lawful Osiyo Myers             -  Fighter Priestess  of Mudd Life Crisis",
            "  Lawful Posc Positis            -  Pickpocket  of Mudd Life Crisis",
            "  Lawful Sabrina Myers           -  Duelist  of Mudd Life Crisis",
            "  Lawful Sister BadTouch         -  High Priestess  of what happen",
            "  Lawful Tabitha Myers           -  Pastor  of Mudd Life Crisis",
            "",
        }, Now);

        Assert.Equal(17, db.Players.Count);
        AssertHas(db, given: "MudPlay",      family: "WuzHere",        align: "Neutral", title: "Apprentice",       gang: null);
        AssertHas(db, given: "Lenneth",    family: "BoxOfRocksDumb", align: "Good",    title: "Heroine",          gang: "what happen");
        AssertHas(db, given: "Meowynator", family: "Clawful",        align: "Lawful",  title: "Herbalist",        gang: "Mudd Life Crisis");
        AssertHas(db, given: "Kitti",      family: "Clawful",        align: "Lawful",  title: "Fighter Priestess", gang: "what happen");
    }

    [Fact]
    public void RowsRefreshExistingRecord_OnSecondObservation()
    {
        WhoListParser p = Build(out PlayerDatabase db);

        DateTime first = Now;
        DateTime later = Now.AddDays(3);

        _feed.Rows(new[]
        {
            "                 Current Adventurers",
            "                 ===================",
            "          Good  Ivy Leaf             - Druid  of what happen",
            "",
        }, first);

        _feed.Rows(new[]
        {
            "                 Current Adventurers",
            "                 ===================",
            "          Good  Ivy Leaf             - High Druid  of new gang",
            "",
        }, later);

        Assert.Single(db.Players);
        Assert.Equal("High Druid", db.Players[0].Title);
        Assert.Equal("new gang",   db.Players[0].Gang);
        Assert.Equal(first, db.Players[0].FirstSeenUtc);
        Assert.Equal(later, db.Players[0].LastSeenUtc);
    }

    // Real Paradigm "who" output. Differs from the stock/Newhaven format in two
    // ways the parser must tolerate: (a) NO alignment column, and (b) freeform,
    // player-chosen guild names that carry punctuation and digits —
    // "harsh.beast", "MakingTheLogosGay!", "Fuck Commies", "House of Rage".
    // Before the gang class was widened, the first punctuation guild
    // ("harsh.beast") failed to match and aborted the block, so only the three
    // rows above it were recorded.
    [Fact]
    public void ParsesParadigmSample_FreeformGuilds_NoAlignmentColumn()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "         Current Adventurers",
            "         ===================",
            "",
            "         Aberama Gold          -  Scallywag of Harsh Yeast",
            "         Ace                   -  Ninja Novice",
            "         Angruin               -  Footpad of The Coma Machine",
            "         Arax Spindreft        x  Dabbler of harsh.beast",
            "         Barry                 -  Menace of Fuck Commies",
            "         Dumpster OfCum        -  Apprentice of The Coma Machine",
            "         Rust Oleum            -  Grunt",
            "         Titus Anaga           -  Witchunter Novice of House of Rage",
            "         Xeeg Stat             -  Cutthroat of MakingTheLogosGay!",
            "",
        }, Now);

        Assert.Equal(9, db.Players.Count);
        // The row that used to abort the block, and everything after it.
        AssertHas(db, given: "Arax",  family: "Spindreft", align: "Neutral", title: "Dabbler",          gang: "harsh.beast");
        AssertHas(db, given: "Barry", family: "",          align: "Neutral", title: "Menace",           gang: "Fuck Commies");
        AssertHas(db, given: "Titus", family: "Anaga",     align: "Neutral", title: "Witchunter Novice", gang: "House of Rage");
        AssertHas(db, given: "Xeeg",  family: "Stat",      align: "Neutral", title: "Cutthroat",        gang: "MakingTheLogosGay!");
        // Single-word name, no family, no guild.
        AssertHas(db, given: "Rust",  family: "Oleum",     align: "Neutral", title: "Grunt",            gang: null);
    }

    // Paradigm prints the extreme alignments in ALL CAPS ("FIEND"). A case-sensitive
    // align match dropped that row, and a dropped row truncated the whole table — so
    // the list cut off at the first FIEND player (report paradigm-20260827-103227).
    [Fact]
    public void ParsesAllCapsFiendAlignment_NormalizesCasing_AndKeepsReadingPastIt()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "         Current Adventurers",
            "         ===================",
            "",
            "         Angruin               -  Rogue Prince of The Coma Machine",
            "   FIEND Deacon Blue           -  Archbishop of Harsh Yeast",
            "         Durnan                -  Courser of Extra Chromosome",
            "",
        }, Now);

        Assert.Equal(3, db.Players.Count);   // the FIEND row parsed, didn't truncate
        AssertHas(db, given: "Deacon", family: "Blue", align: "Fiend",   title: "Archbishop", gang: "Harsh Yeast");
        AssertHas(db, given: "Durnan", family: "",     align: "Neutral", title: "Courser",    gang: "Extra Chromosome");
    }

    // A lone unparseable row must not truncate the table — it's skipped and reading
    // continues, so a single odd row can't cut the whole list short.
    [Fact]
    public void SingleUnparseableRow_IsSkipped_NotTreatedAsBlockEnd()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "         Current Adventurers",
            "         ===================",
            "",
            "         Alpha                 -  Rogue of Guild A",
            "!!! garbled line that is not a player row at all !!!",
            "         Bravo                 -  Mage of Guild B",
            "",
        }, Now);

        Assert.Equal(2, db.Players.Count);
        AssertHas(db, given: "Alpha", family: "", align: "Neutral", title: "Rogue", gang: "Guild A");
        AssertHas(db, given: "Bravo", family: "", align: "Neutral", title: "Mage",  gang: "Guild B");
    }

    private static void AssertHas(
        PlayerDatabase db,
        string given,
        string family,
        string align,
        string title,
        string? gang)
    {
        PlayerRecord? r = null;
        foreach (PlayerRecord p in db.Players)
        {
            if (string.Equals(p.GivenName, given, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                r = p;
                break;
            }
        }
        Assert.NotNull(r);
        Assert.Equal(align, r!.Alignment);
        Assert.Equal(title, r.Title);
        Assert.Equal(gang,  r.Gang);
    }
}
