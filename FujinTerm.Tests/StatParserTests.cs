using System.Text;
using FujinTerm.Game;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for the in-game `stat` screen parser. Real screen layout
/// has multiple labels per row separated by 2+ spaces — every regex
/// runs against every emitted line; this pins the all-fields capture.
/// </summary>
public sealed class StatParserTests
{
    private static (StatParser parser, PlayerStats stats) Setup()
    {
        PlayerStats stats = new();
        StatParser parser = new(stats);
        // No real LineExtractor — tests use TestArm + FeedTestLine.
        parser.TestArm();
        return (parser, stats);
    }

    [Fact]
    public void ParsesFullScreenFromScreenshot()
    {
        var (p, s) = Setup();
        // Lines transcribed from the user's screenshot of Fujin's
        // stat output. Multiple labels per row, 2+ space gutters.
        string[] lines =
        {
            "Name: Fujin WuzHere                  Lives/CP:           9/0",
            "Race: Dark-Elf       Exp: 0          Perception:         50",
            "Class: Mystic        Level: 1        Stealth:            65",
            "Hits:  22/22         Armour Class:  0/0    Thievery:     0",
            "Kai:   0/0                                  Traps:        0",
            "                                            Picklocks:    0",
            "Strength:   60       Agility:   80          Tracking:     0",
            "Intellect:  60       Health:    30          Martial Arts: 118",
            "Willpower:  30       Charm:     40          MagicRes:     37",
        };
        foreach (string l in lines) p.FeedTestLine(l);

        Assert.Equal("Fujin WuzHere", s.Name);
        Assert.Equal("Dark-Elf",      s.Race);
        Assert.Equal("Mystic",        s.Class);
        Assert.Equal(9,   s.Lives);
        Assert.Equal(0,   s.Cp);
        Assert.Equal(0,   s.Exp);
        Assert.Equal(1,   s.Level);
        Assert.Equal(22,  s.Hits);
        Assert.Equal(22,  s.MaxHits);
        Assert.Equal(0,   s.Kai);
        Assert.Equal(0,   s.MaxKai);
        Assert.Equal(0,   s.Mana);     // mystic has no Mana
        Assert.Equal(0,   s.ArmourClass);
        Assert.Equal(0,   s.MaxArmourClass);
        Assert.Equal(60,  s.Strength);
        Assert.Equal(60,  s.Intellect);
        Assert.Equal(30,  s.Willpower);
        Assert.Equal(80,  s.Agility);
        Assert.Equal(30,  s.Health);
        Assert.Equal(40,  s.Charm);
        Assert.Equal(50,  s.Perception);
        Assert.Equal(65,  s.Stealth);
        Assert.Equal(0,   s.Thievery);
        Assert.Equal(0,   s.Traps);
        Assert.Equal(0,   s.Picklocks);
        Assert.Equal(0,   s.Tracking);
        Assert.Equal(118, s.MartialArts);
        Assert.Equal(37,  s.MagicRes);
        Assert.True(p.HasParsed);
    }

    [Fact]
    public void AlteredStatAsterisk_IsTolerated()
    {
        // Buffed Strength shows as "Strength: *80". The `\*?`
        // tolerance in every numeric regex strips it; we capture
        // the post-buff value (the altered-or-not distinction
        // isn't surfaced anywhere yet).
        var (p, s) = Setup();
        p.FeedTestLine("Strength: *80   Agility: *95");
        Assert.Equal(80, s.Strength);
        Assert.Equal(95, s.Agility);
    }

    [Fact]
    public void ManaClass_ParsesManaInsteadOfKai()
    {
        // Wizards / casters have "Mana:" instead of "Kai:" — same row
        // shape, different label. Both regexes coexist; only one
        // matches per character.
        var (p, s) = Setup();
        p.FeedTestLine("Mana:  120/120");
        Assert.Equal(120, s.Mana);
        Assert.Equal(120, s.MaxMana);
        Assert.Equal(0,   s.Kai);
    }

    [Fact]
    public void SpellcastingField_Captured()
    {
        var (p, s) = Setup();
        p.FeedTestLine("Spellcasting: 75");
        Assert.Equal(75, s.Spellcasting);
    }

    [Fact]
    public void NoOutboundStat_LinesAreIgnored()
    {
        // Without TestArm (or a real outbound `stat`), every line is
        // a no-op. Protects against chat noise.
        PlayerStats stats = new();
        StatParser parser = new(stats);
        // No TestArm — the gate is closed.
        parser.FeedTestLine("Strength: 60");
        parser.FeedTestLine("Lives/CP: 9/0");
        Assert.Equal(0, stats.Strength);
        Assert.Equal(0, stats.Lives);
        Assert.False(parser.HasParsed);
    }

    [Fact]
    public void OutboundStatCommand_ArmsTheGate()
    {
        PlayerStats stats = new();
        StatParser parser = new(stats);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        parser.FeedTestLine("Strength: 60");
        Assert.Equal(60, stats.Strength);
    }

    [Fact]
    public void OutboundOtherCommand_DoesNotArmGate()
    {
        PlayerStats stats = new();
        StatParser parser = new(stats);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes("look\r"));
        parser.FeedTestLine("Strength: 60");
        Assert.Equal(0, stats.Strength);
    }

    [Fact]
    public void GateExpiresAfterWindow()
    {
        // Mutable clock — line past the window is ignored.
        PlayerStats stats = new();
        DateTime t = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        StatParser parser = new(stats) { NowProvider = () => t };
        parser.ExpectingScreenWindow = TimeSpan.FromSeconds(5);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        t = t.AddSeconds(6);   // past window
        parser.FeedTestLine("Strength: 60");
        Assert.Equal(0, stats.Strength);
    }

    [Fact]
    public void LivesRemainingLine_UpdatesLivesEvenWithoutStatGate()
    {
        // "You have N lives left." fires after a miracle save and is
        // always-on — no scan window needed. The hard-block needs to
        // see fresh lives counts immediately after life-loss events.
        PlayerStats stats = new();
        StatParser parser = new(stats);
        // No TestArm.
        parser.FeedTestLine("You have 7 lives left.");
        Assert.Equal(7, stats.Lives);
        Assert.True(parser.HasParsed);
    }

    [Fact]
    public void LivesRemainingLine_SingleLifeForm_Parses()
    {
        // Singular grammar: "You have 1 life left." (no 's').
        PlayerStats stats = new();
        StatParser parser = new(stats);
        parser.FeedTestLine("You have 1 life left.");
        Assert.Equal(1, stats.Lives);
    }

    [Theory]
    [InlineData("You now have 7 lives remaining.", 7)]   // post-suicide form (the bug)
    [InlineData("You now have 1 life remaining.", 1)]    // singular post-suicide
    [InlineData("You have 4 lives left.", 4)]            // miracle-save form
    [InlineData("You have 1 life left.", 1)]             // singular miracle-save
    public void LivesUpdateLine_HandlesBothPhrasings(string text, int expected)
    {
        // Regression: the @suicide hard-block was bypassed because
        // remote chained @suicides emit "You now have N lives
        // remaining." (not "You have N lives left.") and the parser
        // only matched the latter form. Both phrasings must update
        // Lives so LivesProvider returns the fresh count to the
        // hard-block.
        PlayerStats stats = new();
        StatParser parser = new(stats);
        parser.FeedTestLine(text);
        Assert.Equal(expected, stats.Lives);
    }

    // ===== Fix A: close-on-prompt-after-capture =====

    [Fact]
    public void PromptAfterCapture_ClosesGateImmediately()
    {
        // Once a field has committed this arm, the next prompt line
        // closes the gate — even if the configured window hasn't
        // expired. Protects against the user's own typed-command
        // echo (which is a prompt line) corrupting stat fields.
        var (p, s) = Setup();
        p.FeedTestLine("Strength: 60");                 // capture
        Assert.Equal(60, s.Strength);
        p.FeedTestLine("[HP=22]:", isPromptLine: true); // prompt after capture → close

        // Subsequent line — even with a juicy match — must NOT commit.
        p.FeedTestLine("Strength: 0");
        Assert.Equal(60, s.Strength);
    }

    [Fact]
    public void PromptBeforeAnyCapture_DoesNotClose()
    {
        // Useful edge case — the user's own `[HP=22]:stat` echo is a
        // prompt line that fires before any stat data arrives. The
        // gate must NOT close on that one (nothing's been captured
        // yet). Only the SECOND prompt (the one after the stat
        // burst) should close.
        var (p, s) = Setup();
        p.FeedTestLine("[HP=22]:stat", isPromptLine: true);   // pre-capture prompt: ignored
        p.FeedTestLine("Strength: 60");                       // capture
        Assert.Equal(60, s.Strength);
        p.FeedTestLine("Charm: 40");                          // still in window
        Assert.Equal(40, s.Charm);
    }

    // ===== Fix B: chat-line shape skip =====

    [Fact]
    public void ChatLineEmbeddingStatLabel_IsIgnored()
    {
        // The big threat — another player's gossip embedding a stat
        // label lands inside our scan window. The ChatLineRx pre-check
        // skips the whole line so the field regexes never run.
        var (p, s) = Setup();
        p.FeedTestLine("Foo gossips: my Strength: 999 is great");
        Assert.Equal(0, s.Strength);
    }

    [Fact]
    public void SelfChatEchoEmbeddingStatLabel_IsIgnored()
    {
        // User's own outgoing `gossip` echoes back as
        // `Fujin gossips: "..."`. Same shape, same protection.
        var (p, s) = Setup();
        p.FeedTestLine("Fujin gossips: \"my Lives/CP: 0/0 is sad\"");
        Assert.Equal(0, s.Lives);
        Assert.Equal(0, s.Cp);
    }

    [Theory]
    [InlineData("Foo telepaths:")]
    [InlineData("Bar yells:")]
    [InlineData("Baz says:")]
    [InlineData("Qux auctions:")]
    [InlineData("Quux gangpaths:")]
    [InlineData("Quuux broadcasts:")]
    public void AllChatVerbs_AreSkipped(string prefix)
    {
        var (p, s) = Setup();
        p.FeedTestLine($"{prefix} Strength: 100 is a thing I have");
        Assert.Equal(0, s.Strength);
    }

    [Fact]
    public void NonChatLine_StillCaptures()
    {
        // Sanity — the chat-shape guard mustn't false-positive on
        // legitimate stat-screen lines (no chat-verb-shape prefix).
        var (p, s) = Setup();
        p.FeedTestLine("Strength:   60       Agility:   80          Tracking:     0");
        Assert.Equal(60, s.Strength);
        Assert.Equal(80, s.Agility);
        Assert.Equal(0,  s.Tracking);
    }
}
