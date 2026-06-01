using FujinTerm.Game;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class LookParserTests
{
    private static readonly DateTime Now = new(2026, 5, 30, 14, 0, 0, DateTimeKind.Utc);

    private static LookParser Build(out PlayerDatabase db)
    {
        db = new PlayerDatabase();
        return new LookParser(
            lines: new Terminal.LineExtractor(new Terminal.TerminalEmulator(80, 25)),
            db:    db);
    }

    /// <summary>
    /// Verbatim "look fujin" against Playpen BBS — equipped variant
    /// from the user's session. Exercises: bracketed name header,
    /// wrapped description sentence, race/class detection via the
    /// longest-match token list (Dark-Elf beats Elf), the
    /// `<item>   (<slot>)` row shape, and block termination on prompt.
    /// </summary>
    [Fact]
    public void ParsesEquippedLook_RecordsRaceClassAndAllSlots()
    {
        LookParser p = Build(out PlayerDatabase db);
        p.FeedTestLines(new[]
        {
            "[ Fujin WuzHere ]",
            "Fujin is a healthy, well built Dark-Elf Mystic with short black hair and black",
            "eyes.  He moves very swiftly, and is quite unfriendly and aloof.  Fujin",
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
        p.FeedPromptLine("[HP=33]:", Now);

        Assert.Single(db.Players);
        PlayerRecord r = db.Players[0];
        Assert.Equal("Fujin",    r.GivenName);
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

    /// <summary>
    /// Empty-loadout case: explicit "Nothing" line means the player
    /// was equipped with nothing. Equipment list should be empty
    /// (not null) so the dialog can show "(none)" rather than "—".
    /// </summary>
    [Fact]
    public void ParsesEmptyLoadout_RecordsExplicitNothing()
    {
        LookParser p = Build(out PlayerDatabase db);
        p.FeedTestLines(new[]
        {
            "[ Fujin WuzHere ]",
            "Fujin is a healthy, well built Dark-Elf Mystic with short black hair and black",
            "eyes.  He moves very swiftly, and is quite unfriendly and aloof.  Fujin",
            "appears to be bright and seems sullen and impulsive.  He is unwounded.",
            "",
            "He is equipped with:",
            "",
            "Nothing",
        }, Now);
        p.FeedPromptLine("[HP=33]:", Now);

        PlayerRecord r = db.Players[0];
        Assert.NotNull(r.Equipment);
        Assert.Empty(r.Equipment!);
    }

    [Fact]
    public void LongestRaceMatch_PicksDarkElf_OverElf()
    {
        LookParser p = Build(out PlayerDatabase db);
        p.FeedTestLines(new[]
        {
            "[ Tester One ]",
            "Tester is a healthy, slim Dark-Elf Mage with green eyes.  She is unwounded.",
            "He is equipped with:",
            "Nothing",
        }, Now);
        p.FeedPromptLine("[HP=99]:", Now);

        Assert.Equal("Dark-Elf", db.Players[0].Race);
        Assert.Equal("Mage",     db.Players[0].Class);
    }

    [Fact]
    public void LongestRaceMatch_PicksHalfElf_OverElfOrHalf()
    {
        LookParser p = Build(out PlayerDatabase db);
        p.FeedTestLines(new[]
        {
            "[ Beta Tester ]",
            "Beta is a wiry Half-Elf Druid with bright blue eyes.  She is unwounded.",
            "He is equipped with:",
            "Nothing",
        }, Now);
        p.FeedPromptLine("[HP=99]:", Now);

        Assert.Equal("Half-Elf", db.Players[0].Race);
        Assert.Equal("Druid",    db.Players[0].Class);
    }

    [Fact]
    public void MergesWithExistingWhoObservation_KeepsAlignmentAndTitle()
    {
        LookParser look = Build(out PlayerDatabase db);
        // Pre-existing who row (alignment + title).
        db.RecordObservation("Fujin WuzHere", null, null, "Neutral", "Apprentice", null, null, Now);

        look.FeedTestLines(new[]
        {
            "[ Fujin WuzHere ]",
            "Fujin is a healthy, well built Dark-Elf Mystic with short black hair.",
            "He is equipped with:",
            "Nothing",
        }, Now);
        look.FeedPromptLine("[HP=33]:", Now);

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
        look.FeedTestLines(new[]
        {
            "[ Fujin WuzHere ]",
            "Fujin is a wiry Dark-Elf Mystic.",
            "He is equipped with:",
            "padded vest                     (Torso)",
        }, Now);
        look.FeedPromptLine("[HP=33]:", Now);
        Assert.NotNull(db.Players[0].Equipment);
        Assert.Single(db.Players[0].Equipment!);

        // Second look interrupted before equipment marker — prompt
        // arrives mid-description. Equipment should stay attached.
        look.FeedTestLines(new[]
        {
            "[ Fujin WuzHere ]",
            "Fujin is a wiry Dark-Elf Mystic.",
        }, Now.AddSeconds(10));
        look.FeedPromptLine("[HP=33]:", Now.AddSeconds(10));

        // Equipment slot still populated from the earlier complete look.
        Assert.NotNull(db.Players[0].Equipment);
        Assert.Single(db.Players[0].Equipment!);
    }

    [Fact]
    public void EquipmentLineRegex_HandlesTwoHandedSlotLabel()
    {
        LookParser p = Build(out PlayerDatabase db);
        p.FeedTestLines(new[]
        {
            "[ Tester Two ]",
            "Tester is a wiry Human Warrior with stern eyes.",
            "He is equipped with:",
            "nexus spear                     (Two Handed)",
        }, Now);
        p.FeedPromptLine("[HP=99]:", Now);

        Assert.Equal("Two Handed", db.Players[0].Equipment![0].SlotLabel);
        Assert.Equal("nexus spear", db.Players[0].Equipment![0].ItemName);
    }
}
