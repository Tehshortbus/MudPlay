using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class LookParserTests
{
    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    private static readonly DateTime Now = new(2026, 5, 30, 14, 0, 0, DateTimeKind.Utc);

    private LookParser Build(out PlayerDatabase db)
    {
        db = new PlayerDatabase(new ProfileService(), () => null);
        return new LookParser(lines: _feed.Lines, db: db);
    }

    // The header does not always end at the "]". Stock LOOK appends an
    // optional red " -- Immortal !" and an optional " (<gang>)", and the
    // header pattern used to anchor straight to end-of-line — so a gang
    // member's look block was skipped entirely and RecordLook never fired.
    // On a realm where everyone is in a gang that meant NOBODY was ever
    // recorded, and TrapDelegationManager (which probes an unknown member's
    // race with `look <member>` and waits for this pipeline) re-probed on
    // every join — the "it keeps looking" loop.
    [Theory]
    [InlineData("[ MudPlay WuzHere ]",                          null)]
    [InlineData("[ MudPlay WuzHere ] (Knights of Chaos)",       "Knights of Chaos")]
    [InlineData("[ MudPlay WuzHere ] -- Immortal !",            null)]
    [InlineData("[ MudPlay WuzHere ] -- Immortal ! (Knights of Chaos)",
                                                                "Knights of Chaos")]
    public void ParsesHeaderSuffixes_RecordsLookAndGang(string header, string? gang)
    {
        LookParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            header,
            "MudPlay is a healthy, well built Dark-Elf Mystic with short black hair and black",
            "eyes.  He is unwounded.",
            "",
            "He is equipped with:",
            "quarterstaff                    (Weapon Hand)",
        }, Now);
        _feed.Line("[HP=33]:", Now);

        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal("MudPlay",  r.GivenName);
        Assert.Equal("WuzHere",  r.FamilyName);
        Assert.Equal("Dark-Elf", r.Race);
        Assert.Equal("Mystic",   r.Class);
        Assert.Equal(gang,       r.Gang);
    }

    // The round-totals table's header row reads like a look header; report
    // paradigm-20260929-233636 logged a look block started on it every round.
    [Fact]
    public void RoundTotalsTable_IsNotALookResponse()
    {
        LookParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "[Round 1 ----------------------------]",
            "[ Combatant             Dealt  Taken ]",
            "[ You                     147     51 ]",
            "[ Thresh                   55      0 ]",
            "A Mage stands here.",
        }, Now);
        _feed.Line("[HP=199/MA=230]:", Now);

        Assert.Empty(db.Players);
    }

    // A look that carries no gang must not erase one a WHO row already
    // taught us — the suffix being absent means "not seen", not "none".
    [Fact]
    public void LookWithoutGang_LeavesAKnownGangAlone()
    {
        LookParser p = Build(out PlayerDatabase db);
        db.RecordLook("MudPlay", race: null, @class: null,
            gang: "Knights of Chaos", equipment: null, nowUtc: Now);

        _feed.Rows(new[]
        {
            "[ MudPlay ]",
            "MudPlay is a healthy, well built Dark-Elf Mystic with short black hair and black",
            "eyes.  He is unwounded.",
            "",
            "He is equipped with:",
            "",
            "Nothing",
        }, Now);
        _feed.Line("[HP=33]:", Now);

        PlayerRecord r = Assert.Single(db.Players);
        Assert.Equal("Knights of Chaos", r.Gang);
        Assert.Equal("Dark-Elf", r.Race);
    }

    // The strictness that keeps unrelated bracketed lines (chat tags, status
    // codes) from false-triggering has to survive the new optional suffixes.
    [Theory]
    [InlineData("[ Global ] Someone says hello")]
    [InlineData("[ MudPlay ] extra words")]
    [InlineData("[MudPlay]")]
    [InlineData("[ MudPlay ] (unclosed gang")]
    public void NonHeaderBracketedLines_DoNotStartABlock(string line)
    {
        LookParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            line,
            "Someone is a healthy, well built Dark-Elf Mystic with short black hair.",
            "",
            "He is equipped with:",
            "quarterstaff                    (Weapon Hand)",
        }, Now);
        _feed.Line("[HP=33]:", Now);

        Assert.Empty(db.Players);
    }

    // Verbatim "look mudplay" against Playpen BBS — equipped variant
    // from the user's session. Exercises: bracketed name header,
    // wrapped description sentence, race/class detection via the
    // longest-match token list (Dark-Elf beats Elf), the
    // `   (<slot>)` row shape, and block termination on prompt.
    [Fact]
    public void ParsesEquippedLook_RecordsRaceClassAndAllSlots()
    {
        LookParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "[ MudPlay WuzHere ]",
            "MudPlay is a healthy, well built Dark-Elf Mystic with short black hair and black",
            "eyes.  He moves very swiftly, and is quite unfriendly and aloof.  MudPlay",
            "appears to be bright and seems sullen and impulsive.  He is unwounded.",
            "",
            "He is equipped with:",
            "padded vest                     (Torso)",
            "padded pants                    (Legs)",
            "padded helm                     (Head)",
            "padded gloves                   (Hands)",
            "padded boots                    (Feet)",
            "quarterstaff                    (Weapon Hand)",
        }, Now);
        // Prompt closes the block since we never see a blank-after-content.
        _feed.Line("[HP=33]:", Now);

        Assert.Single(db.Players);
        PlayerRecord r = db.Players[0];
        Assert.Equal("MudPlay",    r.GivenName);
        Assert.Equal("WuzHere",  r.FamilyName);
        Assert.Equal("Dark-Elf", r.Race);
        Assert.Equal("Mystic",   r.Class);

        Assert.NotNull(r.Equipment);
        Assert.Equal(6, r.Equipment!.Count);
        Assert.Contains(r.Equipment, e => e.SlotLabel == "Torso"        && e.ItemName == "padded vest");
        Assert.Contains(r.Equipment, e => e.SlotLabel == "Legs"         && e.ItemName == "padded pants");
        Assert.Contains(r.Equipment, e => e.SlotLabel == "Head"         && e.ItemName == "padded helm");
        Assert.Contains(r.Equipment, e => e.SlotLabel == "Hands"        && e.ItemName == "padded gloves");
        Assert.Contains(r.Equipment, e => e.SlotLabel == "Feet"         && e.ItemName == "padded boots");
        Assert.Contains(r.Equipment, e => e.SlotLabel == "Weapon Hand"  && e.ItemName == "quarterstaff");
    }

    // Empty-loadout case: explicit "Nothing" line means the player
    // was equipped with nothing. Equipment list should be empty
    // (not null) so the dialog can show "(none)" rather than "—".
    [Fact]
    public void ParsesEmptyLoadout_RecordsExplicitNothing()
    {
        LookParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "[ MudPlay WuzHere ]",
            "MudPlay is a healthy, well built Dark-Elf Mystic with short black hair and black",
            "eyes.  He moves very swiftly, and is quite unfriendly and aloof.  MudPlay",
            "appears to be bright and seems sullen and impulsive.  He is unwounded.",
            "",
            "He is equipped with:",
            "",
            "Nothing",
        }, Now);
        _feed.Line("[HP=33]:", Now);

        PlayerRecord r = db.Players[0];
        Assert.NotNull(r.Equipment);
        Assert.Empty(r.Equipment!);
    }

    [Fact]
    public void LongestRaceMatch_PicksDarkElf_OverElf()
    {
        LookParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "[ Tester One ]",
            "Tester is a healthy, slim Dark-Elf Mage with green eyes.  She is unwounded.",
            "He is equipped with:",
            "Nothing",
        }, Now);
        _feed.Line("[HP=99]:", Now);

        Assert.Equal("Dark-Elf", db.Players[0].Race);
        Assert.Equal("Mage",     db.Players[0].Class);
    }

    [Fact]
    public void LongestRaceMatch_PicksHalfElf_OverElfOrHalf()
    {
        LookParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "[ Beta Tester ]",
            "Beta is a wiry Half-Elf Druid with bright blue eyes.  She is unwounded.",
            "He is equipped with:",
            "Nothing",
        }, Now);
        _feed.Line("[HP=99]:", Now);

        Assert.Equal("Half-Elf", db.Players[0].Race);
        Assert.Equal("Druid",    db.Players[0].Class);
    }

    [Fact]
    public void MergesWithExistingWhoObservation_KeepsAlignmentAndTitle()
    {
        LookParser look = Build(out PlayerDatabase db);
        // Pre-existing who row (alignment + title).
        db.RecordObservation("MudPlay WuzHere", null, null, "Neutral", "Apprentice", null, null, Now);

        _feed.Rows(new[]
        {
            "[ MudPlay WuzHere ]",
            "MudPlay is a healthy, well built Dark-Elf Mystic with short black hair.",
            "He is equipped with:",
            "Nothing",
        }, Now);
        _feed.Line("[HP=33]:", Now);

        PlayerRecord r = db.Players[0];
        Assert.Equal("Dark-Elf", r.Race);          // from look
        Assert.Equal("Mystic",   r.Class);         // from look
        Assert.Equal("Neutral",  r.Alignment);     // preserved from who
        Assert.Equal("Apprentice", r.Title);       // preserved from who
        Assert.NotNull(r.Equipment);
        Assert.Empty(r.Equipment!);
    }

    [Fact]
    public void TruncatedBlock_NoEquipmentMarker_DoesNotZeroOutPreviousEquipment()
    {
        LookParser look = Build(out PlayerDatabase db);
        // First look: full block with equipment.
        _feed.Rows(new[]
        {
            "[ MudPlay WuzHere ]",
            "MudPlay is a wiry Dark-Elf Mystic.",
            "He is equipped with:",
            "padded vest                     (Torso)",
        }, Now);
        _feed.Line("[HP=33]:", Now);
        Assert.NotNull(db.Players[0].Equipment);
        Assert.Single(db.Players[0].Equipment!);

        // Second look interrupted before equipment marker — prompt
        // arrives mid-description. Equipment should stay attached.
        _feed.Rows(new[]
        {
            "[ MudPlay WuzHere ]",
            "MudPlay is a wiry Dark-Elf Mystic.",
        }, Now.AddSeconds(10));
        _feed.Line("[HP=33]:", Now.AddSeconds(10));

        // Equipment slot still populated from the earlier complete look.
        Assert.NotNull(db.Players[0].Equipment);
        Assert.Single(db.Players[0].Equipment!);
    }

    [Fact]
    public void EquipmentLineRegex_HandlesTwoHandedSlotLabel()
    {
        LookParser p = Build(out PlayerDatabase db);
        _feed.Rows(new[]
        {
            "[ Tester Two ]",
            "Tester is a wiry Human Warrior with stern eyes.",
            "He is equipped with:",
            "nexus spear                     (Two Handed)",
        }, Now);
        _feed.Line("[HP=99]:", Now);

        Assert.Equal("Two Handed", db.Players[0].Equipment![0].SlotLabel);
        Assert.Equal("nexus spear", db.Players[0].Equipment![0].ItemName);
    }
}
