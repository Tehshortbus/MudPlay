using System.Text;
using MudPlay.Game;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Coverage for the in-game `stat` screen parser. Real screen layout
// has multiple labels per row separated by 2+ spaces — every regex
// runs against every emitted line; this pins the all-fields capture.
public sealed class StatParserTests
{
    // The parser's idle settle-close timer.
    private readonly ManualTimers _statTimers = new();

    // The game's output, fed the way the client receives it.
    private readonly ServerFeed _feed = new();

    // An armed parser on the class feed: `stat` sent, so its screen is read.
    private (StatParser parser, PlayerStats stats) Setup()
    {
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        return (parser, stats);
    }

    [Fact]
    public void ParsesFullScreenFromScreenshot()
    {
        var (p, s) = Setup();
        // Lines transcribed from the user's screenshot of MudPlay's
        // stat output. Multiple labels per row, 2+ space gutters.
        string[] lines =
        {
            "Name: MudPlay WuzHere                  Lives/CP:           9/0",
            "Race: Dark-Elf       Exp: 0          Perception:         50",
            "Class: Mystic        Level: 1        Stealth:            65",
            "Hits:  22/22         Armour Class:  0/0    Thievery:     0",
            "Kai:   0/0                                  Traps:        0",
            "                                            Picklocks:    0",
            "Strength:   60       Agility:   80          Tracking:     0",
            "Intellect:  60       Health:    30          Martial Arts: 118",
            "Willpower:  30       Charm:     40          MagicRes:     37",
        };
        foreach (string l in lines) _feed.Line(l);

        Assert.Equal("MudPlay WuzHere", s.Name);
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
        _feed.Line("Class: Missionary Level: 2             Stealth:        65");
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
        _feed.Line("Strength: *80   Agility: *95");
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
        _feed.Line("Mana:  120/120");
        Assert.Equal(120, s.Mana);
        Assert.Equal(120, s.MaxMana);
        Assert.Equal(0,   s.Kai);
    }

    [Fact]
    public void SpellcastingField_Captured()
    {
        var (p, s) = Setup();
        _feed.Line("Spellcasting: 75");
        Assert.Equal(75, s.Spellcasting);
    }

    [Fact]
    public void NoOutboundStat_LinesAreIgnored()
    {
        // Without an outbound `stat`, every line is
        // a no-op. Protects against chat noise.
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        // No outbound `stat` — the gate is closed.
        _feed.Line("Strength: 60");
        _feed.Line("Lives/CP: 9/0");
        Assert.Equal(0, stats.Strength);
        Assert.Equal(0, stats.Lives);
        Assert.False(parser.HasParsed);
    }

    [Fact]
    public void OutboundStatCommand_ArmsTheGate()
    {
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        _feed.Line("Strength: 60");
        Assert.Equal(60, stats.Strength);
    }

    [Fact]
    public void OutboundOtherCommand_DoesNotArmGate()
    {
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes("look\r"));
        _feed.Line("Strength: 60");
        Assert.Equal(0, stats.Strength);
    }

    [Theory]
    [InlineData("st")]      // minimum abbreviation — displays the same stat screen
    [InlineData("sta")]
    [InlineData("stat")]    // full word
    public void OutboundStatPrefix_ArmsGate(string command)
    {
        // Every prefix of "stat" from 2 chars up arms the scan window — MajorMUD
        // shows the identical stat screen for `st`, `sta`, and `stat`.
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));
        _feed.Line("Strength: 60");
        Assert.Equal(60, stats.Strength);
    }

    [Theory]
    [InlineData("s")]       // 1-char — too short, ambiguous
    [InlineData("sto")]     // shares "st" but diverges — not a prefix of "stat"
    [InlineData("stand")]   // longer than "stat" and diverges
    [InlineData("steal")]
    public void OutboundNonStatCommand_DoesNotArmGate(string command)
    {
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));
        _feed.Line("Strength: 60");
        Assert.Equal(0, stats.Strength);
    }

    [Fact]
    public void GateExpiresAfterWindow()
    {
        // Mutable clock — line past the window is ignored.
        PlayerStats stats = new();
        DateTime t = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        StatParser parser = new(stats, now: () => t, schedule: _statTimers.Arm);
        parser.ExpectingScreenWindow = TimeSpan.FromSeconds(5);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        t = t.AddSeconds(6);   // past window
        _feed.Line("Strength: 60");
        Assert.Equal(0, stats.Strength);
    }

    // A stat already on its way lets another caller wait for its screen instead of
    // sending a second (report paradigm-20260928-231447).
    [Fact]
    public void ScreenExpected_FromOutboundStat_UntilTheWindowLapses()
    {
        PlayerStats stats = new();
        DateTime t = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        StatParser parser = new(stats, now: () => t, schedule: _statTimers.Arm);
        parser.ExpectingScreenWindow = TimeSpan.FromSeconds(5);
        Assert.False(parser.ScreenExpected);

        parser.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        Assert.True(parser.ScreenExpected);

        t = t.AddSeconds(6);
        Assert.False(parser.ScreenExpected);
    }

    [Fact]
    public void LivesRemainingLine_UpdatesLivesEvenWithoutStatGate()
    {
        // "You have N lives left." fires after a miracle save and is
        // always-on — no scan window needed. The hard-block needs to
        // see fresh lives counts immediately after life-loss events.
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        // No outbound `stat`.
        _feed.Line("You have 7 lives left.");
        Assert.Equal(7, stats.Lives);
        Assert.True(parser.HasParsed);
    }

    [Fact]
    public void LivesRemainingLine_SingleLifeForm_Parses()
    {
        // Singular grammar: "You have 1 life left." (no 's').
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        _feed.Line("You have 1 life left.");
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
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        _feed.Line(text);
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
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        // No outbound `stat` — the stat gate is closed.
        _feed.Line("You gain 1500 experience.");
        Assert.Equal(1500, stats.Exp);
        Assert.True(parser.HasParsed);
    }

    // A hand `train` awards CP ("You gain 15 CPs") that no stat poll has counted yet;
    // Auto-train stats budgets against Stats.Cp, so the gain must land live (reports
    // paradigm-20260924-132158 / -132507).
    [Fact]
    public void TrainCpGainLine_AccruesOntoCp()
    {
        var (p, s) = Setup();
        s.Cp = 3;
        _feed.Line("You gain 0 additional lives.");   // not a CP line
        _feed.Line("You gain 15 CPs");
        Assert.Equal(18, s.Cp);
    }

    [Fact]
    public void ExperienceGainLine_AddsToRunningTotal()
    {
        var (p, s) = Setup();
        _feed.Line("Exp: 0  Level: 2  Exp needed for next level: 100 (200) [50%]");
        Assert.Equal(0, s.Exp);
        _feed.Line("You gain 800 experience.");
        _feed.Line("You gain 200 experience.");
        Assert.Equal(1000, s.Exp);   // accrued, not overwritten
    }

    [Fact]
    public void StatRead_ReanchorsExp_AfterLiveGains()
    {
        // A fresh stat/exp poll is authoritative — it overwrites the
        // accrued total rather than adding to it (re-anchor).
        var (p, s) = Setup();
        _feed.Line("You gain 5000 experience.");
        Assert.Equal(5000, s.Exp);
        _feed.Line("Exp: 12345  Level: 3  Exp needed for next level: 10 (20) [50%]");
        Assert.Equal(12345, s.Exp);
    }

    [Fact]
    public void ExperienceGainLine_IgnoresUnrelatedAndThirdParty()
    {
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        _feed.Line("MudPlay gains a level!");
        _feed.Line("Bob gains 999 experience.");
        Assert.Equal(0, stats.Exp);
    }

    [Fact]
    public void ExperienceGainLine_DecrementsExpToNext_KeepsToNextFresh()
    {
        // With a real exp-screen baseline, a live gain also draws down "to next level"
        // and re-derives the percent, so @exp Needed / @level to-next stay current.
        var (p, s) = Setup();
        _feed.Line("Exp: 0  Level: 2  Exp needed for next level: 100 (200) [50%]");
        Assert.Equal(100, s.ExpToNext);

        _feed.Line("You gain 30 experience.");
        Assert.Equal(70, s.ExpToNext);            // 100 - 30
        Assert.Equal(65, s.LevelPercent);         // (200 - 70) / 200

        // Never goes negative once the level's worth of exp is banked.
        _feed.Line("You gain 500 experience.");
        Assert.Equal(0, s.ExpToNext);
    }

    // ===== Live level-up on a train-success line =====

    [Fact]
    public void TrainSuccess_Stock_SetsAttainedLevel_WithoutStatGate()
    {
        // Stock line carries the attained level — set it absolutely, no poll needed.
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        _feed.Line("You hand over 1 gold crown and you receive training to attain level 5.");
        Assert.Equal(5, stats.Level);
        Assert.True(parser.HasParsed);
    }

    [Fact]
    public void TrainSuccess_Paradigm_IncrementsKnownLevel_AndClearsToNext()
    {
        // Paradigm line has no number — infer current + 1 from the known level, and clear
        // the old level's to-next (the new level's span isn't known until the next poll).
        var (p, s) = Setup();
        _feed.Line("Exp: 500  Level: 3  Exp needed for next level: 40 (200) [80%]");
        Assert.Equal(3, s.Level);

        _feed.Line("You hand over 350 copper farthings to train to the next level!");
        Assert.Equal(4, s.Level);
        Assert.Equal(0, s.ExpToNext);
        Assert.Equal(0, s.LevelExpSpan);
    }

    [Fact]
    public void TrainSuccess_Paradigm_WithoutKnownLevel_LeavesLevelUnset()
    {
        // No baseline level to infer from → don't fabricate one; a stat poll establishes it.
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        _feed.Line("You hand over 350 copper farthings to train to the next level!");
        Assert.Equal(0, stats.Level);
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
        _feed.Line("Strength: 60");                 // capture
        Assert.Equal(60, s.Strength);
        _feed.Line("[HP=22]:"); // prompt after capture → close

        // Subsequent line — even with a juicy match — must NOT commit.
        _feed.Line("Strength: 0");
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
        _feed.Line("[HP=22]:stat");   // pre-capture prompt: ignored
        _feed.Line("Strength: 60");                       // capture
        Assert.Equal(60, s.Strength);
        _feed.Line("Charm: 40");                          // still in window
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
        MudPlay.Models.Profile.LastKnownStats? captured = null;
        p.ScreenParsed += snap => captured = snap;

        _feed.Line("Class: Mystic        Level: 2        Stealth:            65");
        Assert.Equal(2, s.Level);
        Assert.Null(captured);          // no prompt, no further line → gate still open

        _statTimers.RunAll();          // the 750 ms idle timer fires

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

        _statTimers.RunAll();          // armed by Setup's outbound `stat`, but nothing captured

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

        _feed.Line("Strength: 60");                  // capture
        _feed.Line("[HP=22]:");  // reactive close → fires once
        Assert.Equal(1, count);

        _statTimers.RunAll();                           // window already closed → no-op
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
        _feed.Line("Foo gossips: my Strength: 999 is great");
        Assert.Equal(0, s.Strength);
    }

    [Fact]
    public void SelfChatEchoEmbeddingStatLabel_IsIgnored()
    {
        // User's own outgoing `gossip` echoes back as
        // `MudPlay gossips: "..."`. Same shape, same protection.
        var (p, s) = Setup();
        _feed.Line("MudPlay gossips: \"my Lives/CP: 0/0 is sad\"");
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
        _feed.Line($"{prefix} Strength: 100 is a thing I have");
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
        _feed.Line("Exp: 0 Level: 1 Exp needed for next level: 2950 (2950) [0%]");
        Assert.Equal(0,    s.Exp);
        Assert.Equal(1,    s.Level);
        Assert.Equal(2950, s.ExpToNext);
        Assert.Equal(2950, s.LevelExpSpan);
        Assert.Equal(0,    s.LevelPercent);
    }

    [Fact]
    public void ExpLine_ParadigmScale_ExceedsInt32_CapturedAsLong()
    {
        // Paradigm exp passes int32 (2,147,483,647) into the tens of billions — the
        // whole reason Exp / ExpToNext / LevelExpSpan are long. Line transcribed from
        // report paradigm-20260916-081052: the int parse used to reject the (M) total
        // (7,411,379,231) so the whole line failed and Exp drifted off gain-line accrual.
        var (p, s) = Setup();
        _feed.Line("Exp: 7050294193 Level: 64 Exp needed for next level: 361085038 (7411379231) [95%]");
        Assert.Equal(7050294193L, s.Exp);
        Assert.Equal(64,          s.Level);
        Assert.Equal(361085038L,  s.ExpToNext);
        Assert.Equal(7411379231L, s.LevelExpSpan);
        Assert.Equal(95,          s.LevelPercent);
    }

    [Fact]
    public void ExperienceGainLine_PastInt32_NoClamp()
    {
        // A gain that pushes the total past int.MaxValue must accrue in full, not clamp
        // to 2,147,483,647 (the old int-field bug).
        var (p, s) = Setup();
        _feed.Line("Exp: 2100000000 Level: 64 Exp needed for next level: 10 (20) [50%]");
        _feed.Line("You gain 200000000 experience.");
        Assert.Equal(2300000000L, s.Exp);
    }

    [Fact]
    public void StatScreenInlineExp_ParadigmScale_CapturedAsLong()
    {
        // The stat screen packs Exp inline ("Race: Gaunt One   Exp: 7049892943
        // Perception: 188") — the same >int32 value must parse there too.
        var (p, s) = Setup();
        _feed.Line("Race: Gaunt One   Exp: 7049892943      Perception:    188");
        Assert.Equal(7049892943L, s.Exp);
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
        _feed.Line("Exp: 1475 Level: 1 Exp needed for next level: 1475 (2950) [50%]");
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
        _feed.Line("Exp: 5000 Level: 1 Exp needed for next level: 0 (2950) [100%]");
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
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));
        _feed.Line("Exp: 0 Level: 1 Exp needed for next level: 2950 (2950) [0%]");
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
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));
        _feed.Line("Exp: 0 Level: 1 Exp needed for next level: 2950 (2950) [0%]");
        Assert.Equal(0, stats.LevelExpSpan);
    }

    [Fact]
    public void ExpLine_WithoutOutbound_IsIgnored()
    {
        // Chat-noise protection — the same gate that protects stat
        // fields covers exp fields too.
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        _feed.Line("Exp: 0 Level: 1 Exp needed for next level: 2950 (2950) [0%]");
        Assert.Equal(0, stats.LevelExpSpan);
    }

    [Fact]
    public void ExpLine_AnchoredAtLineStart_ChatCannotFake()
    {
        // The exp regex is anchored to line start — a chat line
        // embedding the entire payload can't write to the fields
        // even with the gate open.
        var (p, s) = Setup();
        _feed.Line("Foo gossips: Exp: 0 Level: 1 Exp needed for next level: 2950 (2950) [0%]");
        Assert.Equal(0, s.LevelExpSpan);
    }

    [Fact]
    public void NonChatLine_StillCaptures()
    {
        // Sanity — the chat-shape guard mustn't false-positive on
        // legitimate stat-screen lines (no chat-verb-shape prefix).
        var (p, s) = Setup();
        _feed.Line("Strength:   60       Agility:   80          Tracking:     0");
        Assert.Equal(60, s.Strength);
        Assert.Equal(80, s.Agility);
        Assert.Equal(0,  s.Tracking);
    }

    // ===== Compact `health`-command re-anchor =====

    // Arm ONLY the health gate — the stat-screen gate stays closed, exactly as a
    // real `health` send leaves it. That closed stat gate is what stops the
    // health line's HP value bleeding into the stat sheet's `Health:` attribute
    // (a core stat; the sheet labels the HP pool `Hits:`).
    private (StatParser parser, PlayerStats stats) HealthSetup()
    {
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes("health\r"));
        return (parser, stats);
    }

    [Fact]
    public void HealthCommand_Stock_Kai_ReanchorsHpAndKai()
    {
        // Stock realm: "Health: 91/91 [100%] Kai: 6/10 [60%]".
        var (p, s) = HealthSetup();
        (int MaxHits, int PoolMax) captured = default;
        bool fired = false;
        p.HealthReanchored += (maxHits, poolMax) => { captured = (maxHits, poolMax); fired = true; };

        _feed.Line("Health:      91/91  [100%]  Kai:  6/10  [60%]");

        Assert.Equal(91, s.Hits);
        Assert.Equal(91, s.MaxHits);
        Assert.Equal(6,  s.Kai);
        Assert.Equal(10, s.MaxKai);
        Assert.Equal(0,  s.Mana);          // a kai class never writes the mana pool
        Assert.True(fired);
        Assert.Equal((91, 10), captured);  // poolMax is the kai ceiling
        Assert.Equal(0, s.Health);         // the Health core-stat attribute is NOT clobbered by the HP value
        Assert.True(p.HasParsed);
    }

    [Fact]
    public void HealthCommand_Paradigm_Mana_ReanchorsHpAndMana()
    {
        // Paradigm realm: "Health: 593/593 [100%] Mana: 619/619 [100%]".
        var (p, s) = HealthSetup();
        (int MaxHits, int PoolMax) captured = default;
        p.HealthReanchored += (maxHits, poolMax) => captured = (maxHits, poolMax);

        _feed.Line("Health:    593/593    [100%]    Mana:    619/619    [100%]");

        Assert.Equal(593, s.Hits);
        Assert.Equal(593, s.MaxHits);
        Assert.Equal(619, s.Mana);
        Assert.Equal(619, s.MaxMana);
        Assert.Equal(0,   s.Kai);            // a mana class never writes the kai pool
        Assert.Equal((593, 619), captured);
        Assert.Equal(0,   s.Health);
    }

    [Fact]
    public void HealthCommand_NoPoolClass_ReanchorsHpOnly()
    {
        // A no-pool class (only HP) shows just the HP field: "Health: 137/137 [100%]".
        var (p, s) = HealthSetup();
        int poolMaxSeen = -1;
        p.HealthReanchored += (_, poolMax) => poolMaxSeen = poolMax;

        _feed.Line("Health:    137/137    [100%]");

        Assert.Equal(137, s.Hits);
        Assert.Equal(137, s.MaxHits);
        Assert.Equal(0, poolMaxSeen);        // no pool field → poolMax 0, which ApplyStatScreenMax ignores
    }

    [Theory]
    [InlineData("health")]
    [InlineData("healt")]
    [InlineData("heal")]
    [InlineData("hea")]
    public void OutboundHealthPrefix_ArmsHealthGate(string command)
    {
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));
        _feed.Line("Health:  91/91  [100%]  Kai:  6/10  [60%]");
        Assert.Equal(91, stats.MaxHits);
    }

    [Fact]
    public void OutboundHealth_DoesNotArmStatGate()
    {
        // `health` opens only the compact readout — it must not arm the full
        // stat-screen scan, so a following stat-sheet line stays untouched.
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes("health\r"));
        _feed.Line("Strength: 60");
        Assert.Equal(0, stats.Strength);
    }

    [Fact]
    public void HealthCommand_OutsideWindow_Ignored()
    {
        PlayerStats stats = new();
        DateTime t = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        StatParser parser = new(stats, now: () => t, schedule: _statTimers.Arm);
        parser.ExpectingHealthWindow = TimeSpan.FromSeconds(5);
        parser.ObserveOutbound(Encoding.Latin1.GetBytes("health\r"));
        t = t.AddSeconds(6);   // past the window
        _feed.Line("Health:  91/91  [100%]  Kai:  6/10  [60%]");
        Assert.Equal(0, stats.Hits);
        Assert.Equal(0, stats.MaxHits);
    }

    [Fact]
    public void HealthCommand_ChatEcho_Ignored()
    {
        // A gossip embedding the readout can't commit the re-anchor — both the
        // chat-shape guard and the line-start anchor reject it.
        var (p, s) = HealthSetup();
        _feed.Line("Bob gossips: Health: 91/91 [100%] Kai: 6/10 [60%]");
        Assert.Equal(0, s.Hits);
        Assert.Equal(0, s.MaxHits);
    }

    [Fact]
    public void HealthCommand_NoArm_Ignored()
    {
        // Without an outbound `health`, the readout is a no-op — nothing arms.
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        _feed.Line("Health:  91/91  [100%]  Kai:  6/10  [60%]");
        Assert.Equal(0, stats.Hits);
        Assert.False(parser.HasParsed);
    }

    // ===== Self-arm on the stat-screen header (outbound gate missed) =====

    [Fact]
    public void SelfArmsOnHeader_WhenOutboundGateNeverFired()
    {
        // Reproduces report paradigm-20260906-090057: `stat` was typed while the
        // `train stats` form still owned the keyboard in character-mode, so the
        // command went out a byte at a time and the outbound gate never armed.
        // The screen must still parse off its own header line. NOTE: no outbound `stat`.
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);   // deliberately un-armed
        parser.AttachLineExtractor(_feed.Lines);
        string[] lines =
        {
            "Name: Client Tester                    Lives/CP:      9/5",
            "Race: Gaunt One   Exp: 54605302        Perception:    136",
            "Class: Mage       Level: 25            Stealth:         0",
            "Hits:   219/220   Armour Class:  61/4  Thievery:        0",
            "Mana: * 204/204   Spellcasting: 180    Traps:           0",
            "Strength:  50     Agility: 50          Tracking:        0",
            "Intellect: 137    Health:  67          Martial Arts:   30",
            "Willpower: 50     Charm:   31          MagicRes:       86",
        };
        foreach (string l in lines) _feed.Line(l);

        Assert.True(parser.HasParsed);
        Assert.Equal("Client Tester", stats.Name);
        Assert.Equal(25,  stats.Level);          // the reported symptom
        Assert.Equal(220, stats.MaxHits);
        Assert.Equal(204, stats.MaxMana);
        Assert.Equal(137, stats.Intellect);
        Assert.Equal(9,   stats.Lives);
        Assert.Equal(5,   stats.Cp);
    }

    // Stock lists active effects inside the stat screen as bare applied lines; the
    // screen reads as printing from its header (however `stat` was sent) until the
    // closing prompt, so the condition tracker can tell them from casts.
    [Fact]
    public void InStatScreen_FromTheHeaderUntilTheClosingPrompt()
    {
        StatParser parser = new(new PlayerStats(), schedule: _statTimers.Arm);   // un-armed: a stat the gate never saw
        parser.AttachLineExtractor(_feed.Lines);
        Assert.False(parser.InStatScreen);

        _feed.Line("Name: Client Tester                    Lives/CP:      9/5");
        _feed.Line("Willpower: 50     Charm:   31          MagicRes:       86");
        Assert.True(parser.InStatScreen);

        _feed.Line("[HP=219/MA=204]:");
        Assert.False(parser.InStatScreen);

        // A later screen in the same session closes on its own prompt too.
        _feed.Line("Name: Client Tester                    Lives/CP:      9/5");
        Assert.True(parser.InStatScreen);
        _feed.Line("[HP=219/MA=204]:");
        Assert.False(parser.InStatScreen);
    }

    [Fact]
    public void ChatLineMimickingHeader_DoesNotSelfArm()
    {
        // A gossip embedding "Name: … Lives/CP: N/M" must NOT self-arm: the
        // header regex is anchored at line start, and a chat line opens with
        // "<who> <verb>:". Nothing may commit off it.
        PlayerStats stats = new();
        StatParser parser = new(stats, schedule: _statTimers.Arm);
        parser.AttachLineExtractor(_feed.Lines);
        _feed.Line("Villain gossips: Name: Loot Goblin  Lives/CP: 9/9 haha");
        _feed.Line("Class: Mage       Level: 25            Stealth:         0");
        Assert.False(parser.HasParsed);
        Assert.Equal(0, stats.Level);
    }

    [Theory]
    // Every row of the stat sheet is recognized so the unrecognized-line capture
    // never stages it. Exact lines from a real `stat` export.
    [InlineData("Name: Fujin              Lives/CP: 9/2")]
    [InlineData("Race: Kang               Exp: 1234567         Perception: 40")]
    [InlineData("Class: Paladin           Level: 28            Stealth: 0")]
    [InlineData("Hits: 324/324            Armour Class: 78/16  Thievery: 0")]
    [InlineData("Mana: * 56/72            Spellcasting: 94     Traps: 0")]
    [InlineData("Picklocks: 0")]
    // The single-line `exp` and compact `health` readouts count too.
    [InlineData("Exp: 1234567 Level: 28 Exp needed for next level: 500 (2000000) [75%]")]
    [InlineData("Health: 324/324 [100%] Mana: 56/72 [77%]")]
    public void IsStatScreenLine_RecognizesSheetRows(string line) =>
        Assert.True(StatParser.IsStatScreenLine(line));

    [Theory]
    // A genuine server message must NOT be mistaken for the stat sheet, and a
    // gossip embedding a stat label stays out (the chat guard rejects it).
    [InlineData("An ominous wind blows through the trees")]
    [InlineData("The goblin swings at you with its club!")]
    [InlineData("Villain gossips: my Strength: 60 is low")]
    [InlineData("")]
    public void IsStatScreenLine_RejectsNonStatLines(string line) =>
        Assert.False(StatParser.IsStatScreenLine(line));

    // Report paradigm-20260928-223148: an `exp` screen shows no Hits / Mana, so its
    // capture mustn't count as reading the maxima (the snapshot still holds an older
    // `stat`'s, read under other gear).
    [Fact]
    public void ExpScreen_DoesNotCountAsReadingTheMaxima()
    {
        var (p, _) = Setup();
        _feed.Line("Exp: 12345  Level: 3  Exp needed for next level: 10 (20) [50%]");
        _feed.Line("[HP=10/MA=5]:");

        Assert.False(p.LastCaptureReadHits);
        Assert.False(p.LastCaptureReadPool);
    }

    [Fact]
    public void StatScreen_CountsAsReadingTheMaxima()
    {
        var (p, _) = Setup();
        _feed.Line("Hits:    86/86    Armour Class:  30/3  Thievery:       62");
        _feed.Line("Mana:    38/38    Spellcasting: 74     Traps:          65");
        _feed.Line("[HP=86/MA=38]:");

        Assert.True(p.LastCaptureReadHits);
        Assert.True(p.LastCaptureReadPool);
        Assert.False(p.LastCaptureReadLives);
    }

    // The lives a reconnect is judged on must be this connection's: a capture
    // says whether its screen gave them, since the count itself is also carried
    // over from the profile.
    [Fact]
    public void StatScreen_SaysWhetherItGaveTheLives()
    {
        var (p, s) = Setup();
        _feed.Line("Name: MudPlay WuzHere                  Lives/CP:           6/0");
        _feed.Line("Hits:    86/86    Armour Class:  30/3  Thievery:       62");
        _feed.Line("[HP=86/MA=38]:");

        Assert.True(p.LastCaptureReadLives);
        Assert.Equal(6, s.Lives);

        // The next capture is an `exp` screen: no lives on it.
        p.ObserveOutbound(Encoding.Latin1.GetBytes("stat\r"));
        _feed.Line("Exp: 12345  Level: 3  Exp needed for next level: 10 (20) [50%]");
        _feed.Line("[HP=86/MA=38]:");

        Assert.False(p.LastCaptureReadLives);
    }
}
