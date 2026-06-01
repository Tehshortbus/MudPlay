using FujinTerm.Game;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class WhoListParserTests
{
    private static readonly DateTime Now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The real "Newhaven, Marrow Road" sample from the user's screenshot —
    /// keeps the test honest about leading whitespace, double-spaces
    /// between fields, and the family-name-touching-marker case
    /// (Lenneth BoxOfRocksDumb-).
    /// </summary>
    private static readonly string[] HavenSample =
    {
        "                 Current Adventurers",
        "                 ===================",
        "        Lawful  Debbie Schwartz       - Magebane  of Mudd Life Crisis",
        "                Fujin WuzHere         - Apprentice",
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

    private static WhoListParser Build(out PlayerDatabase db)
    {
        db = new PlayerDatabase();
        return new WhoListParser(
            // Test hook bypasses the real LineExtractor — pass null safely
            // since FeedTestLines drives HandleLine directly.
            lines: new Terminal.LineExtractor(new Terminal.TerminalEmulator(80, 25)),
            db:    db);
    }

    [Fact]
    public void ParsesHavenSample_RecordsEveryPlayerWithExpectedFields()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        p.FeedTestLines(HavenSample, Now);

        Assert.Equal(11, db.Players.Count);

        // Spot-check the unusual cases.
        AssertHas(db, given: "Debbie",  family: "Schwartz",        align: "Lawful",  title: "Magebane",          gang: "Mudd Life Crisis");
        AssertHas(db, given: "Fujin",   family: "WuzHere",         align: "Neutral", title: "Apprentice",        gang: null);  // self, no alignment word
        AssertHas(db, given: "Lenneth", family: "BoxOfRocksDumb",  align: "Good",    title: "Heroine",           gang: "what happen");  // marker abuts family
        AssertHas(db, given: "Sister",  family: "BadTouch",        align: "Lawful",  title: "High Priestess",    gang: "what happen");
        AssertHas(db, given: "Osiyo",   family: "Myers",           align: "Lawful",  title: "Fighter Priestess", gang: "Mudd Life Crisis");  // two-word title
    }

    [Fact]
    public void IgnoresLines_OutsideAdventurersBlock()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        p.FeedTestLines(new[]
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
        p.FeedTestLines(HavenSample, Now);
        Assert.Equal(11, db.Players.Count);

        // Second `who` call later — fresh table.
        DateTime later = Now.AddHours(1);
        p.FeedTestLines(new[]
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
        p.FeedTestLines(new[]
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

    private static PlayerRecord FindByGiven(PlayerDatabase db, string given)
    {
        foreach (PlayerRecord p in db.Players)
            if (string.Equals(p.GivenName, given, StringComparison.OrdinalIgnoreCase))
                return p;
        throw new InvalidOperationException($"No player with given name '{given}' in database.");
    }

    /// <summary>
    /// Verbatim live-server output (Newhaven, Narrow Road dump). Differs
    /// from the earlier <see cref="HavenSample"/> in three ways the parser
    /// must tolerate: (a) a blank line between the ============ separator
    /// and the first row, (b) a blank line between the last row and the
    /// trailing prompt, (c) <c>  -  </c> (two-space dash two-space) marker
    /// rather than <c>-</c>.
    /// </summary>
    [Fact]
    public void ParsesNewhavenLiveDump_Including_BlankLines_AndTwoSpaceMarker()
    {
        WhoListParser p = Build(out PlayerDatabase db);
        p.FeedTestLines(new[]
        {
            "         Current Adventurers",
            "         ===================",
            "",
            "  Lawful Debbie Schwartz         -  Magebane  of Mudd Life Crisis",
            "         Fujin WuzHere           -  Apprentice",
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
        AssertHas(db, given: "Fujin",      family: "WuzHere",        align: "Neutral", title: "Apprentice",       gang: null);
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

        p.FeedTestLines(new[]
        {
            "                 Current Adventurers",
            "                 ===================",
            "          Good  Ivy Leaf             - Druid  of what happen",
            "",
        }, first);

        p.FeedTestLines(new[]
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
