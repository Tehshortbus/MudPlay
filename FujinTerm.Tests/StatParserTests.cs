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
    public void Class_SingleSpaceBeforeNextLabel_StillCaptured()
    {
        // Real wire layout (MMUD Reborn): the Class column is only a
        // SINGLE space from the following "Level:" label, unlike the
        // 2-space gutters on Name / Race. The old 2-space-only
        // terminator silently dropped Class here, so a rerolled
        // character's class never updated in the profile.
        var (p, s) = Setup();
        p.FeedTestLine("Class: Missionary Level: 2             Stealth:        65");
        Assert.Equal("Missionary", s.Class);
        Assert.Equal(2, s.Level);
        Assert.Equal(65, s.Stealth);
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

    // ===== Live experience accrual =====

    [Fact]
    public void ExperienceGainLine_AccruesOntoExp_WithoutStatGate()
    {
        // "You gain N experience." is server-emitted combat reward — it
        // must accrue live, with no outbound `stat`/`exp` poll, so the
        // banked-exp total stays current between manual checks.
        PlayerStats stats = new();
        StatParser parser = new(stats);
        // No TestArm — the stat gate is closed.
        parser.FeedTestLine("You gain 1500 experience.");
        Assert.Equal(1500, stats.Exp);
        Assert.True(parser.HasParsed);
    }

    [Fact]
    public void ExperienceGainLine_AddsToRunningTotal()
    {
        var (p, s) = Setup();
        p.FeedTestLine("Exp: 0  Level: 2  Exp needed for next level: 100 (200) [50%]");
        Assert.Equal(0, s.Exp);
        p.FeedTestLine("You gain 800 experience.");
        p.FeedTestLine("You gain 200 experience.");
        Assert.Equal(1000, s.Exp);   // accrued, not overwritten
    }

    [Fact]
    public void StatRead_ReanchorsExp_AfterLiveGains()
    {
        // A fresh stat/exp poll is authoritative — it overwrites the
        // accrued total rather than adding to it (re-anchor).
        var (p, s) = Setup();
        p.FeedTestLine("You gain 5000 experience.");
        Assert.Equal(5000, s.Exp);
        p.FeedTestLine("Exp: 12345  Level: 3  Exp needed for next level: 10 (20) [50%]");
        Assert.Equal(12345, s.Exp);
    }

    [Fact]
    public void ExperienceGainLine_IgnoresUnrelatedAndThirdParty()
    {
        PlayerStats stats = new();
        StatParser parser = new(stats);
        parser.FeedTestLine("Fujin gains a level!");
        parser.FeedTestLine("Bob gains 999 experience.");
        Assert.Equal(0, stats.Exp);
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

    // ===== Fix C: idle settle-close (stat screen with no trailing prompt) =====

    [Fact]
    public void IdleStatScreen_NoTrailingPrompt_SettleCloseFiresScreenParsed()
    {
        // The auto-train relay depends on ScreenParsed firing after a
        // `train` → `stat` round-trip. But a stat screen the player is
        // parked in front of (sitting at a trainer right after `train`)
        // emits no terminating prompt and no further inbound line, so
        // the reactive gate-close never runs and ScreenParsed never
        // fired — `train stats` was therefore never sent. The idle
        // settle-close closes the gate off the last captured field.
        var (p, s) = Setup();
        FujinTerm.Models.Profile.LastKnownStats? captured = null;
        p.ScreenParsed += snap => captured = snap;

        p.FeedTestLine("Class: Mystic        Level: 2        Stealth:            65");
        Assert.Equal(2, s.Level);
        Assert.Null(captured);          // no prompt, no further line → gate still open

        p.SettleNowForTests();          // the 750 ms idle timer fires

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Level);
        Assert.Equal("Mystic", captured.Class);
    }

    [Fact]
    public void SettleClose_OnEmptyWindow_FiresNothing()
    {
        // The settle-close honours the same no-capture guard as every
        // other close path — a window that matched no fields must not
        // churn a ScreenParsed snapshot.
        var (p, _) = Setup();
        bool fired = false;
        p.ScreenParsed += _ => fired = true;

        p.SettleNowForTests();          // armed via Setup's TestArm, but nothing captured

        Assert.False(fired);
    }

    [Fact]
    public void ReactiveClose_ThenSettle_DoesNotDoubleFire()
    {
        // A reactive close (prompt after capture) already fired
        // ScreenParsed and dropped the window; a stale settle-close
        // arriving afterwards must find the window closed and no-op,
        // never emitting a second snapshot.
        var (p, _) = Setup();
        int count = 0;
        p.ScreenParsed += _ => count++;

        p.FeedTestLine("Strength: 60");                  // capture
        p.FeedTestLine("[HP=22]:", isPromptLine: true);  // reactive close → fires once
        Assert.Equal(1, count);

        p.SettleNowForTests();                           // window already closed → no-op
        Assert.Equal(1, count);
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

    // ===== Exp command =====

    [Fact]
    public void ExpLine_FullCapture()
    {
        // Line transcribed from the user's screenshot, fresh char:
        //   "Exp: 0 Level: 1 Exp needed for next level: 2950 (2950) [0%]"
        // Exp = cumulative total earned; LevelExpSpan = total exp
        // delta to span this level (constant per level); ExpToNext
        // is currently 2950 because no progress has been made.
        var (p, s) = Setup();
        p.FeedTestLine("Exp: 0 Level: 1 Exp needed for next level: 2950 (2950) [0%]");
        Assert.Equal(0,    s.Exp);
        Assert.Equal(1,    s.Level);
        Assert.Equal(2950, s.ExpToNext);
        Assert.Equal(2950, s.LevelExpSpan);
        Assert.Equal(0,    s.LevelPercent);
    }

    [Fact]
    public void ExpLine_PartialProgressCaptures()
    {
        // Halfway through level 1 — earned 1475 of the 2950-exp
        // span, so ExpToNext is 1475 remaining and the line reads
        // "Exp: 1475 ... 1475 (2950) [50%]". The Exp field is the
        // cumulative total; at level 1 with no prior levels, it
        // equals the within-level earned.
        var (p, s) = Setup();
        p.FeedTestLine("Exp: 1475 Level: 1 Exp needed for next level: 1475 (2950) [50%]");
        Assert.Equal(1475, s.Exp);
        Assert.Equal(1,    s.Level);
        Assert.Equal(1475, s.ExpToNext);
        Assert.Equal(2950, s.LevelExpSpan);
        Assert.Equal(50,   s.LevelPercent);
    }

    [Fact]
    public void ExpLine_OvershootClampedAtZero()
    {
        // Server clamps ExpToNext at 0 when the character has
        // overshot the level threshold (mass-XP drop without an
        // auto-train, etc.). Percent saturates at 100 in that
        // window. Pin both behaviours.
        var (p, s) = Setup();
        p.FeedTestLine("Exp: 5000 Level: 1 Exp needed for next level: 0 (2950) [100%]");
        Assert.Equal(5000, s.Exp);
        Assert.Equal(0,    s.ExpToNext);
        Assert.Equal(2950, s.LevelExpSpan);
        Assert.Equal(100,  s.LevelPercent);
    }

    [Theory]
    [InlineData("exp")]         // minimum prefix
    [InlineData("expe")]
    [InlineData("exper")]
    [InlineData("experi")]
    [InlineData("experie")]
    [InlineData("experien")]
    [InlineData("experienc")]
    [InlineData("experience")]  // full word
    public void OutboundExpPrefix_ArmsGate(string command)
    {
        // Every prefix from 3 chars up arms the scan window — matches
        // MajorMUD's partial-command behaviour visible in the
        // screenshot.
        PlayerStats stats = new();
        StatParser parser = new(stats);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));
        parser.FeedTestLine("Exp: 0 Level: 1 Exp needed for next level: 2950 (2950) [0%]");
        Assert.Equal(2950, stats.LevelExpSpan);
    }

    [Theory]
    [InlineData("ex")]         // 2-char "say ex" — must NOT arm
    [InlineData("ezpense")]    // not a prefix
    [InlineData("examine")]    // shares 'ex' but not a prefix of 'experience'
    [InlineData("expansion")]  // shares 'exp' but diverges
    public void OutboundNonExpCommand_DoesNotArmGate(string command)
    {
        // Only true prefixes of "experience" arm the gate. Random
        // ex* commands fall through.
        PlayerStats stats = new();
        StatParser parser = new(stats);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));
        parser.FeedTestLine("Exp: 0 Level: 1 Exp needed for next level: 2950 (2950) [0%]");
        Assert.Equal(0, stats.LevelExpSpan);
    }

    [Fact]
    public void ExpLine_WithoutOutbound_IsIgnored()
    {
        // Chat-noise protection — the same gate that protects stat
        // fields covers exp fields too.
        PlayerStats stats = new();
        StatParser parser = new(stats);
        parser.FeedTestLine("Exp: 0 Level: 1 Exp needed for next level: 2950 (2950) [0%]");
        Assert.Equal(0, stats.LevelExpSpan);
    }

    [Fact]
    public void ExpLine_AnchoredAtLineStart_ChatCannotFake()
    {
        // The exp regex is anchored to line start — a chat line
        // embedding the entire payload can't write to the fields
        // even with the gate open.
        var (p, s) = Setup();
        p.FeedTestLine("Foo gossips: Exp: 0 Level: 1 Exp needed for next level: 2950 (2950) [0%]");
        Assert.Equal(0, s.LevelExpSpan);
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
