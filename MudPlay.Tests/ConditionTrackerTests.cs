using System.Linq;
using MudPlay.Game.Conditions;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// ConditionTracker matches lines against the
// active MessageStore records' AppliedMessage /
// AppliedEndsWith pairs and surfaces the aggregated
// MessageFlags.
public sealed class ConditionTrackerTests
{
    private sealed class Harness : IDisposable
    {
        public LogService Log { get; } = new();
        public MessageStore Messages { get; } = new();
        public ConditionTracker Tracker { get; }
        // The game's output, fed the way the client receives it.
        public ServerFeed Server { get; } = new();
        public List<MessageRecord> Applied { get; } = new();
        public List<MessageRecord> Ended { get; } = new();
        public List<MessageRecord> ActionFailed { get; } = new();

        public Harness()
        {
            Tracker = new ConditionTracker(Messages, Log);
            Tracker.AttachLineExtractor(Server.Lines);
            Tracker.ConditionApplied += Applied.Add;
            Tracker.ConditionEnded   += Ended.Add;
            Tracker.ActionFailed     += ActionFailed.Add;
        }

        public void Feed(string text) => Server.Line(text);

        public void FeedAt(string text, DateTimeOffset when) => Server.Line(text, when);

        public void Dispose() => Tracker.Dispose();
    }

    // ----- one applied line, several spells: the cast line says which ----------

    // The three records that print "You are enveloped in darkness": globe of
    // darkness and darkness take the light away, blind globe is a blindness and
    // prints no cast line of its own.
    private static Harness DarknessHarness()
    {
        Harness h = new();
        h.Messages.Messages.Add(MakeRecord("globe of darkness", MessageFlags.None,
            "You are enveloped in darkness!", "The unnatural darkness lifts.") with
        {
            TargetMessage = "A bubble of pure darkness appears and begins to expand!",
            WitnessMessage = "The {source} makes a clutching gesture!",
        });
        h.Messages.Messages.Add(MakeRecord("darkness", MessageFlags.None,
            "You are enveloped in darkness", "The unnatural darkness lifts") with
        {
            CasterMessage = "Darkness is everywhere, suffocating you like a cloak!",
            TargetMessage = "The {source} exudes a strange, overpowering aura.",
        });
        h.Messages.Messages.Add(MakeRecord("blind globe", MessageFlags.Blinded,
            "You are enveloped in darkness", "The unnatural darkness lifts"));
        return h;
    }

    [Fact]
    public void SharedAppliedLine_TheSpellWhoseCastLineWasJustSeen_IsTheOneApplied()
    {
        // A nanati shadow's globe of darkness set Blinded through blind globe's
        // record, and the party was asked to cure a blindness nobody had.
        using Harness h = DarknessHarness();

        h.Feed("The thin nanati shadow makes a clutching gesture!");
        h.Feed("A bubble of pure darkness appears and begins to expand!");
        h.Feed("You are enveloped in darkness!");

        Assert.False(h.Tracker.IsBlinded);
        Assert.Equal(new[] { "globe of darkness" }, h.Applied.Select(r => r.Name));
    }

    [Fact]
    public void SharedAppliedLine_WithNoCastLineSeen_AppliesEveryRecordAsBefore()
    {
        // blind globe prints nothing when it is cast, so its applied line arrives
        // alone, and is a blindness.
        using Harness h = DarknessHarness();

        h.Feed("You are enveloped in darkness!");

        Assert.True(h.Tracker.IsBlinded);
        Assert.Equal(3, h.Applied.Count);
    }

    [Fact]
    public void SharedAppliedLine_ACastLineFromLongAgo_DoesNotDecide()
    {
        using Harness h = DarknessHarness();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        h.FeedAt("A bubble of pure darkness appears and begins to expand!", t0);
        h.FeedAt("You are enveloped in darkness!", t0.AddSeconds(30));

        Assert.True(h.Tracker.IsBlinded);
    }

    [Fact]
    public void SharedAppliedLine_AfterTheGlobeLifts_ABlindGlobeStillBlinds()
    {
        using Harness h = DarknessHarness();
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        h.FeedAt("A bubble of pure darkness appears and begins to expand!", t0);
        h.FeedAt("You are enveloped in darkness!", t0.AddSeconds(1));
        h.FeedAt("The unnatural darkness lifts.", t0.AddSeconds(2));
        Assert.False(h.Tracker.IsBlinded);

        h.FeedAt("You are enveloped in darkness!", t0.AddSeconds(20));

        Assert.True(h.Tracker.IsBlinded);
    }

    [Fact]
    public void AppliedLineSharedByRecordsWithTheSameFlags_IsNotNarrowedByACastLine()
    {
        // Five buffs print "You feel protected!"; they all latch together, as they
        // always did, whichever one's cast line was seen.
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("armour", MessageFlags.None, "You feel protected!", "The armour fades.") with
        {
            CasterMessage = "You cast armour on {target}!",
        });
        h.Messages.Messages.Add(MakeRecord("shield", MessageFlags.None, "You feel protected!", "The shield fades."));

        h.Feed("You cast armour on Raijin!");
        h.Feed("You feel protected!");

        Assert.Equal(2, h.Applied.Count);
    }

    private static MessageRecord MakeRecord(
        string name,
        MessageFlags flags,
        string applied,
        string endsWith)
    {
        return new MessageRecord(
            Id: MessageRecord.ComputeId(name, "", "", "", applied, endsWith),
            Name: name,
            Flags: flags,
            RawFlagsHex: (ushort)flags,
            CasterMessage: string.Empty,
            TargetMessage: string.Empty,
            WitnessMessage: string.Empty,
            AppliedMessage: applied,
            AppliedEndsWith: endsWith);
    }

    // A wear-off line whose OWN record was never latched left the flag stuck for
    // the session. Reported from play (shard creature, Paradigm): its mid-combat
    // spell 479 "zaps" carries HoldPerson, its message is worded as a stun ("You
    // are stunned by electrical shock!" / "The shock wears off!"), it came up as
    // the wrong condition, and "even once it cleared, it never detected the
    // cleared state, it just sat there."
    //
    // The topology that does it: the applied line latches record A, the wear-off
    // text belongs to record B, and both carry the same flag. `_endsIndex` finds
    // B, `_active.Remove(B)` returns false because A is what latched, and the
    // `continue` skips the group and flag-wide clears below it — so A is never
    // released. There is no time-based expiry anywhere in the tracker, so nothing
    // else ever clears it: only ClearAll (disconnect / death / Reset States).
    //
    // This is the same shape as the two reports already generalized for in
    // OnLine — a confusion fumble sibling (-092219) and 8 "You are blind."
    // aliases (paradigm-20260904-214452) — one step further out: there the
    // wear-off's own record WAS active and siblings were stranded; here the
    // wear-off's record was not active at all.
    // The shard creature's zap, with the two seed records as they stand (spell 479
    // "zaps", a hold; and "stun", one of eight confusion records whose applied text
    // is "You are stunned"). The zap's line holds the shorter text, so it used to set
    // Confused as well as the hold — the wrong condition — and "The shock wears off!"
    // cleared only the hold: Confused stood for the rest of the session.
    [Fact]
    public void AppliedText_InsideALongerAppliedText_DoesNotLatch()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("zaps", MessageFlags.MovementPrevented,
            applied: "You are stunned by electrical shock", endsWith: "The shock wears off"));
        h.Messages.Messages.Add(MakeRecord("stun", MessageFlags.Confused,
            applied: "You are stunned", endsWith: "You are no longer stunned"));

        h.Feed("You are stunned by electrical shock!");
        Assert.True(h.Tracker.IsMovementPrevented);
        Assert.False(h.Tracker.IsConfused);

        h.Feed("The shock wears off!");
        Assert.False(h.Tracker.IsMovementPrevented);
        Assert.False(h.Tracker.IsConfused);

        h.Feed("You are stunned!");                 // the stun's own line still latches it
        Assert.True(h.Tracker.IsConfused);
    }

    // A spell's own cast line can hold its applied text with more words after it —
    // the poison tick "You are poisoned for 12 damage!" — and the condition is read
    // off that line too. Only another record's longer APPLIED text shadows.
    [Fact]
    public void AppliedText_InsideALongerLineThatIsNotAnAppliedText_StillLatches()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("poison", MessageFlags.Poisoned,
            applied: "You are poisoned", endsWith: "You feel better"));

        h.Feed("You are poisoned for 12 damage!");
        Assert.True(h.Tracker.IsPoisoned);
    }

    // A record with the bare text and one with its punctuation are the same message:
    // "You are blind!" latches both.
    [Fact]
    public void AppliedText_ThatOnlyAddsPunctuation_ShadowsNothing()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("blind", MessageFlags.Blinded,
            applied: "You are blind", endsWith: "You can see again"));
        h.Messages.Messages.Add(MakeRecord("sand blind", MessageFlags.MovementPrevented,
            applied: "You are blind!", endsWith: "The sand clears"));

        h.Feed("You are blind!");
        Assert.True(h.Tracker.IsBlinded);
        Assert.True(h.Tracker.IsMovementPrevented);
    }

    // ----- a line that is another spell's own cast line ----------------

    // A record as the seeds carry it: every message slot, and the spells it is for.
    private static MessageRecord Full(
        string name, MessageFlags flags, string caster, string target, string witness,
        string applied, string endsWith, params int[] spells) =>
        new(
            Id: MessageRecord.ComputeId(name, caster, target, witness, applied, endsWith),
            Name: name,
            Flags: flags,
            RawFlagsHex: (ushort)flags,
            CasterMessage: caster,
            TargetMessage: target,
            WitnessMessage: witness,
            AppliedMessage: applied,
            AppliedEndsWith: endsWith,
            Links: spells.Select(n => new GameDataLink("Spells", n)).ToArray());

    // Report paradigm-20261011-165726: the Underwater Passage rooms cast freezing
    // water (#511), whose line "You are freezing cold!" holds "You are freezing", the
    // applied text of four other spells, two of them confusions. All four latched on
    // every room, Confused held navigation and had the party told to wait, and no
    // wear-off ever came: the room's spell prints none. Run over both shipped seeds.
    [Theory]
    [InlineData("paradigm")]
    [InlineData("stock")]
    public void ShippedSeed_FreezingWatersLine_LatchesNothing(string realm)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            List<MessageRecord> seed = JsonStore.Load<List<MessageRecord>>(
                Path.Combine(dir, $"Messages.{realm}.seed.json"))!;
            using Harness h = new();
            h.Messages.Messages.ReplaceAll(seed);

            h.Feed("You are freezing cold!");
            Assert.Empty(h.Applied);
            Assert.Equal(MessageFlags.None, h.Tracker.ActiveFlags);

            // The mists' and the storms' own applied line still latches them.
            h.Feed("You are freezing!");
            Assert.True(h.Tracker.IsConfused);
            foreach (string name in new[] { "glacial mist", "galacial mist", "ice storm", "freezing" })
                Assert.Contains(h.Applied, r => r.Name == name);
            Assert.DoesNotContain(h.Applied, r => r.Name == "freezing water");

            h.Feed("You are no longer freezing.");
            Assert.False(h.Tracker.IsConfused);
            Assert.DoesNotContain(h.Applied, r => h.Tracker.IsActiveByName(r.Name));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // Freezing water's record switched off in Game Data still owns its line: the line
    // is the room's spell whether or not the record acts on it.
    [Theory]
    [InlineData("paradigm")]
    [InlineData("stock")]
    public void ShippedSeed_FreezingWatersLine_LatchesNothing_WithItsRecordDisabled(string realm)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            List<MessageRecord> seed = JsonStore.Load<List<MessageRecord>>(
                Path.Combine(dir, $"Messages.{realm}.seed.json"))!;
            int at = seed.FindIndex(r => r.Name == "freezing water");
            seed[at] = seed[at] with { Flags = seed[at].Flags | MessageFlags.Disabled };
            using Harness h = new();
            h.Messages.Messages.ReplaceAll(seed);

            h.Feed("You are freezing cold!");
            Assert.Empty(h.Applied);
            Assert.Equal(MessageFlags.None, h.Tracker.ActiveFlags);

            h.Feed("You are freezing!");
            Assert.True(h.Tracker.IsConfused);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // The same five records by hand, so the rule is pinned without the seeds.
    [Fact]
    public void AppliedText_InsideAnotherSpellsCastLine_DoesNotLatch()
    {
        using Harness h = new();
        h.Messages.Messages.Add(Full("freezing water", MessageFlags.None,
            "{null}", "You are freezing cold!", "{null}", "{null}", "{null}", 511, 5242));
        h.Messages.Messages.Add(Full("glacial mist", MessageFlags.Confused,
            "An opalescent white mist billows through the room!", "An opalescent white mist surrounds you!",
            "An opalescent white mist billows through the room!",
            "You are freezing", "You are no longer freezing", 1042));
        h.Messages.Messages.Add(Full("ice storm", MessageFlags.None,
            "A freezing storm of ice envelops the foe!", "A freezing storm of ice surrounds you!",
            "A freezing storm of ice sweeps through the room!",
            "You are freezing", "You are no longer freezing", 1363));

        h.Log.Diagnostics = new LogDiagnosticState { DebugDiagnostics = true };
        h.Feed("You are freezing cold!");
        Assert.Empty(h.Applied);
        Assert.False(h.Tracker.IsConfused);
        Assert.Contains(h.Log.Snapshot(), e =>
            e.Message.Contains("matched only inside 'You are freezing cold!'", StringComparison.Ordinal)
            && e.Message.Contains("freezing water", StringComparison.Ordinal));

        h.Feed("You are freezing");
        Assert.True(h.Tracker.IsConfused);
        Assert.Equal(2, h.Applied.Count);

        h.Feed("You are no longer freezing");
        Assert.False(h.Tracker.IsConfused);
    }

    // A spell's own cast line can hold its applied text, and so can the cast line of
    // a record that carries the same applied text: mesmerize's "You are mesmerized by
    // the movements!" is the onset for it and for strut, which shares the text.
    [Fact]
    public void AppliedText_InsideItsOwnSpellsCastLine_StillLatches()
    {
        using Harness h = new();
        h.Messages.Messages.Add(Full("mesmerize", MessageFlags.Confused,
            "{null}", "You are mesmerized by the movements!", "{null}",
            "You are mesmerized", "You are no longer mesmerized", 1391));
        h.Messages.Messages.Add(Full("strut", MessageFlags.Confused,
            "You strut your stuff with your hot pants!", "{source} struts their stuff with their hot pants!", "{null}",
            "You are mesmerized", "The effects of the hot pants wear off", 1109));

        h.Feed("You are mesmerized by the movements!");

        Assert.True(h.Tracker.IsConfused);
        Assert.Equal(2, h.Applied.Count);
    }

    // Two records for one spell, the cast line on one and the applied text on the
    // other: the line is that spell's, and its condition is read off it.
    [Fact]
    public void AppliedText_InsideACastLineOfARecordForTheSameSpell_StillLatches()
    {
        using Harness h = new();
        h.Messages.Messages.Add(Full("venom (cast)", MessageFlags.None,
            "{null}", "You are poisoned by the serpent's venom!", "{null}", "{null}", "{null}", 900));
        h.Messages.Messages.Add(Full("venom", MessageFlags.Poisoned,
            "{null}", "{null}", "{null}", "You are poisoned", "The venom fades", 900));

        h.Feed("You are poisoned by the serpent's venom!");

        Assert.True(h.Tracker.IsPoisoned);
    }

    // The longer line carries a placeholder: poison hits (#588) is a monster's on-hit
    // damage with no poison in it, and its line holds way of the cobra's applied text.
    [Fact]
    public void AppliedText_InsideAnotherSpellsCastLineWithAPlaceholder_DoesNotLatch()
    {
        using Harness h = new();
        h.Messages.Messages.Add(Full("poison hits", MessageFlags.None,
            "{target} are poisoned for {damage} damage!", "You are poisoned for {damage} damage!",
            "{source} is poisoned for {damage} damage!", "{null}", "{null}", 588));
        h.Messages.Messages.Add(Full("way of the cobra", MessageFlags.Poisoned,
            "{target} is poisoned!", "You are poisoned!", "{target} is poisoned!",
            "You are poisoned", "The effects of the poison wear off", 299));

        h.Feed("You are poisoned for 3 damage!");
        Assert.False(h.Tracker.IsPoisoned);

        h.Feed("You are poisoned!");
        Assert.True(h.Tracker.IsPoisoned);
    }

    // The applied text also stands by itself elsewhere in the line: it still counts.
    [Fact]
    public void AppliedText_AlsoOutsideTheOtherSpellsCastLine_StillLatches()
    {
        using Harness h = new();
        h.Messages.Messages.Add(Full("poison hits", MessageFlags.None,
            "{null}", "You are poisoned for {damage} damage!", "{null}", "{null}", "{null}", 588));
        h.Messages.Messages.Add(Full("freezing water", MessageFlags.None,
            "{null}", "You are freezing cold!", "{null}", "{null}", "{null}", 511));
        h.Messages.Messages.Add(Full("way of the cobra", MessageFlags.Poisoned,
            "{null}", "{null}", "{null}", "You are poisoned", "The effects of the poison wear off", 299));
        h.Messages.Messages.Add(Full("glacial mist", MessageFlags.Confused,
            "{null}", "{null}", "{null}", "You are freezing", "You are no longer freezing", 1042));

        h.Feed("You are poisoned for 3 damage! You are poisoned!");
        Assert.True(h.Tracker.IsPoisoned);

        h.Feed("You are freezing! You are freezing cold!");
        Assert.True(h.Tracker.IsConfused);
    }

    // Hypnotic hands' cast line holds mesmerize's applied text, and hypnotic hands is
    // a confusion as mesmerize is: the line confuses whichever record it is read
    // through, so it still latches, at once. Its own applied line does not always
    // follow straight away (user, 2026-10-11), and a character confused and not
    // known to be walks on.
    [Fact]
    public void AnotherSpellsCastLine_ThatBringsTheSameCondition_StillLatchesAtOnce()
    {
        using Harness h = new();
        h.Messages.Messages.Add(Full("hypnotic hands", MessageFlags.Confused,
            "{target} is mesmerized by the hypnotic movements!", "You are mesmerized by the hypnotic movements!",
            "{target} is mesmerized by the hypnotic movements!",
            "You are confused!", "The effect of hypnotic hands wears off.", 300));
        h.Messages.Messages.Add(Full("mesmerize", MessageFlags.Confused,
            "{null}", "You are mesmerized by the movements!", "{null}",
            "You are mesmerized", "You are no longer mesmerized", 1391));

        h.Feed("You are mesmerized by the hypnotic movements!");
        Assert.Equal(new[] { "mesmerize" }, h.Applied.Select(r => r.Name));
        Assert.True(h.Tracker.IsConfused);

        h.Feed("You are confused!");
        Assert.Equal(new[] { "mesmerize", "hypnotic hands" }, h.Applied.Select(r => r.Name));

        h.Feed("The effect of hypnotic hands wears off.");
        Assert.False(h.Tracker.IsConfused);
        Assert.DoesNotContain(h.Applied, r => h.Tracker.IsActiveByName(r.Name));
    }

    // The line's own spell has to carry EVERY condition of the record it is read
    // through. A confusion's line does not bring on a hold that shares its words.
    [Fact]
    public void AnotherSpellsCastLine_ThatBringsOnlyPartOfTheCondition_DoesNotLatch()
    {
        using Harness h = new();
        h.Messages.Messages.Add(Full("hypnotic hands", MessageFlags.Confused,
            "{null}", "You are mesmerized by the hypnotic movements!", "{null}",
            "You are confused!", "The effect of hypnotic hands wears off.", 300));
        h.Messages.Messages.Add(Full("deep trance (test)", MessageFlags.Confused | MessageFlags.MovementPrevented,
            "{null}", "{null}", "{null}", "You are mesmerized", "You are no longer mesmerized", 31001));
        h.Messages.Messages.Add(Full("fumbled (test)", MessageFlags.LastActionFailed,
            "{null}", "{null}", "{null}", "by the hypnotic", "{null}", 31002));

        h.Feed("You are mesmerized by the hypnotic movements!");

        Assert.Empty(h.Applied);
        Assert.Empty(h.ActionFailed);
        Assert.Equal(MessageFlags.None, h.Tracker.ActiveFlags);
    }

    // The same sequence over the shipped records. What latches and what ends is
    // what it was before any line was held back as another spell's (run both ways,
    // 2026-10-11: the same records applied and ended, line for line, on both seeds).
    [Theory]
    [InlineData("paradigm")]
    [InlineData("stock")]
    public void ShippedSeed_HypnoticHands_ConfusesOnItsCastLine(string realm)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            List<MessageRecord> seed = JsonStore.Load<List<MessageRecord>>(
                Path.Combine(dir, $"Messages.{realm}.seed.json"))!;
            using Harness h = new();
            h.Messages.Messages.ReplaceAll(seed);

            h.Feed("You are mesmerized by the hypnotic movements!");
            Assert.Equal(new[] { "mesmerize", "strut" }, h.Applied.Select(r => r.Name));
            Assert.Equal(MessageFlags.Confused, h.Tracker.ActiveFlags);

            h.Feed("You are confused!");
            Assert.Contains(h.Applied, r => r.Name == "hypnotic hands");
            Assert.All(h.Applied, r => Assert.True(r.Flags.HasFlag(MessageFlags.Confused)));

            h.Feed("The effect of hypnotic hands wears off.");
            Assert.Equal(new[] { "hypnotic hands" }, h.Ended.Select(r => r.Name));
            Assert.Equal(MessageFlags.None, h.Tracker.ActiveFlags);
            Assert.DoesNotContain(h.Applied, r => h.Tracker.IsActiveByName(r.Name));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // What the condition exemption must not let through: freezing water and poison
    // hits carry no condition, so their lines bring on nothing in another's name.
    [Theory]
    [InlineData("paradigm")]
    [InlineData("stock")]
    public void ShippedSeed_APoisonHitsLine_AndAColdHitsLine_LatchNothing(string realm)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            List<MessageRecord> seed = JsonStore.Load<List<MessageRecord>>(
                Path.Combine(dir, $"Messages.{realm}.seed.json"))!;
            using Harness h = new();
            h.Messages.Messages.ReplaceAll(seed);

            h.Feed("You are poisoned for 3 damage!");
            h.Feed("You are chilled for 2 damage!");

            Assert.Empty(h.Applied);
            Assert.Equal(MessageFlags.None, h.Tracker.ActiveFlags);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ----- a cast the target negated ---------------------------------

    // The three lines of one negated cast (report paradigm-20261011-165726: holding
    // breath, stopped by a worn fish-helm; the names are stand-ins). Each is tied to
    // the spell's record in the log, and none applies anything.
    [Theory]
    [InlineData("Aldric negates Aldric's cast of holding breath!", "Aldric cast 'holding breath' at Aldric")]
    [InlineData("You negate Aldric's cast of holding breath!", "Aldric cast 'holding breath' at You")]
    [InlineData("You attempt to cast holding breath at Aldric, but it's negated!", "You cast 'holding breath' at Aldric")]
    public void ANegatedCast_IsTiedToItsSpellRecord_AndAppliesNothing(string line, string said)
    {
        using Harness h = new();
        h.Log.Diagnostics = new LogDiagnosticState { DebugDiagnostics = true };
        h.Messages.Messages.Add(Full("holding breath", MessageFlags.None,
            "{null}", "{null}", "{null}", "{null}", "{null}", 512));

        h.Feed(line);

        Assert.Empty(h.Applied);
        Assert.Contains(h.Log.Snapshot(), e =>
            e.Message.Contains("negated cast", StringComparison.Ordinal)
            && e.Message.Contains(said, StringComparison.Ordinal)
            && e.Message.Contains("spell record 'holding breath' (#512)", StringComparison.Ordinal));
    }

    [Fact]
    public void ANegatedCast_OfASpellWithNoRecord_IsSaidToHaveNone()
    {
        using Harness h = new();
        h.Log.Diagnostics = new LogDiagnosticState { DebugDiagnostics = true };

        h.Feed("You negate Aldric's cast of zzyzx ward!");

        Assert.Contains(h.Log.Snapshot(), e =>
            e.Message.Contains("Aldric cast 'zzyzx ward' at You", StringComparison.Ordinal)
            && e.Message.Contains("no spell record of that name", StringComparison.Ordinal));
    }

    // A name several records carry is counted, not listed: the list says no more of
    // which one was cast.
    [Fact]
    public void ANegatedCast_OfANameSeveralRecordsCarry_CountsThem()
    {
        using Harness h = new();
        h.Log.Diagnostics = new LogDiagnosticState { DebugDiagnostics = true };
        foreach (int spell in new[] { 135, 1363, 5027 })
            h.Messages.Messages.Add(Full("ice storm", MessageFlags.None,
                "{null}", "{null}", "{null}", "{null}", "{null}", spell));

        h.Feed("You negate Aldric's cast of ice storm!");

        LogEntry said = Assert.Single(h.Log.Snapshot(), e => e.Message.Contains("negated cast", StringComparison.Ordinal));
        Assert.Contains("Aldric cast 'ice storm' at You", said.Message, StringComparison.Ordinal);
        Assert.Contains("3 spell records carry that name", said.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("#1363", said.Message, StringComparison.Ordinal);
    }

    // The line names the spell, and a record's applied or wear-off text can be words
    // of that name. The cast did nothing: neither is read off it.
    [Fact]
    public void ANegatedCast_NeitherStartsNorEndsACondition()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("sticky web", MessageFlags.MovementPrevented,
            applied: "sticky web", endsWith: "You pull free of the sticky web"));
        h.Messages.Messages.Add(MakeRecord("fear", MessageFlags.Fear,
            applied: "You are afraid!", endsWith: "cast of courage"));

        h.Feed("You negate giant spider's cast of sticky web!");
        Assert.Empty(h.Applied);
        Assert.False(h.Tracker.IsMovementPrevented);

        h.Feed("You are afraid!");
        h.Feed("Aldric negates Borin's cast of courage!");
        Assert.True(h.Tracker.IsFeared);
        Assert.Empty(h.Ended);

        h.Feed("The giant spider covers you in a sticky web!");   // the spell's own line still applies it
        Assert.True(h.Tracker.IsMovementPrevented);
    }

    [Theory]
    [InlineData("paradigm")]
    [InlineData("stock")]
    public void ShippedSeed_ANegatedHoldingBreath_AppliesAndEndsNothing(string realm)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            List<MessageRecord> seed = JsonStore.Load<List<MessageRecord>>(
                Path.Combine(dir, $"Messages.{realm}.seed.json"))!;
            using Harness h = new();
            h.Log.Diagnostics = new LogDiagnosticState { DebugDiagnostics = true };
            h.Messages.Messages.ReplaceAll(seed);

            h.Feed("Aldric negates Aldric's cast of holding breath!");
            h.Feed("You negate Aldric's cast of holding breath!");
            h.Feed("You attempt to cast holding breath at Aldric, but it's negated!");

            Assert.Empty(h.Applied);
            Assert.Empty(h.Ended);
            Assert.Equal(3, h.Log.Snapshot().Count(e =>
                e.Message.Contains("spell record 'holding breath' (#512)", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // An applied line and the wear-off the game prints can sit on different records
    // that share a flag. The wear-off's own record was never latched, and its line
    // still has to release the flag: nothing else in the tracker ever would.
    [Fact]
    public void WearOff_WhoseOwnRecordNeverLatched_StillClearsTheFlag()
    {
        using Harness h = new();
        // A: what the applied line actually latches. Its own wear-off never arrives.
        h.Messages.Messages.Add(MakeRecord("shock (stun wording)",
            MessageFlags.MovementPrevented,
            applied: "You are stunned by electrical shock!",
            endsWith: "You shake off the stun."));
        // B: carries the wear-off the game really prints, under the same flag.
        h.Messages.Messages.Add(MakeRecord("shock (wear-off)",
            MessageFlags.MovementPrevented,
            applied: "A jolt courses through you!",
            endsWith: "The shock wears off!"));

        h.Feed("You are stunned by electrical shock!");
        Assert.True(h.Tracker.IsMovementPrevented, "the stun latched");

        h.Feed("The shock wears off!");
        Assert.False(h.Tracker.IsMovementPrevented,
            "a wear-off under the same flag must release it even though its own "
            + "record was not the one latched — otherwise it sticks for the session");
    }

    // ----- applied / ends pair ---------------------------------------

    [Fact]
    public void AppliedMessage_SetsActiveFlags()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned,
            applied: "You have been poisoned!",
            endsWith: "The poison wears off."));

        h.Feed("You have been poisoned!");

        Assert.True(h.Tracker.IsPoisoned);
        Assert.Single(h.Applied);
    }

    // A corrupt 1-char applied/ends pair ("n"/"E", seen in some older MDB imports) would
    // Contains-match almost every line — a room display like "Stone Chamber, Entrance" holds
    // both an 'n' and an 'E', so it would apply AND end the effect every line. The tracker must
    // refuse to index such a too-short pattern, so it never latches. (report paradigm-20260911-004201)
    [Fact]
    public void TooShortPattern_NeverIndexed_NoSpuriousToggle()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("amethyst pendant (corrupt)",
            MessageFlags.None, applied: "n", endsWith: "E"));
        // A healthy record alongside it must still work.
        h.Messages.Messages.Add(MakeRecord("Poison", MessageFlags.Poisoned,
            applied: "You have been poisoned!", endsWith: "The poison wears off."));

        h.Feed("Stone Chamber, Entrance");        // has both 'n' and 'E'
        h.Feed("Also here: angry kobold slave.");
        h.Feed("[HP=324/MA=48]:e");

        Assert.Empty(h.Applied);   // the 1-char pattern never latched anything
        Assert.Empty(h.Ended);

        // The healthy record is unaffected by the guard.
        h.Feed("You have been poisoned!");
        Assert.Single(h.Applied);
        Assert.True(h.Tracker.IsPoisoned);
    }

    // Fear is its own tracked condition (spell 430, terror beast): "You are
    // afraid!" latches it, "The effects of fear wear off!" clears it. It isn't a
    // hold — fear has its own movement wait (SelfFearMovementGate) — so a feared
    // character doesn't read as movement-prevented.
    [Fact]
    public void FearLine_SetsAndClearsIsFeared()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("fear",
            MessageFlags.Fear,
            applied: "You are afraid",
            endsWith: "The effects of fear wear off"));

        h.Feed("You are afraid!");
        Assert.True(h.Tracker.IsFeared);
        Assert.False(h.Tracker.IsMovementPrevented);

        h.Feed("The effects of fear wear off!");
        Assert.False(h.Tracker.IsFeared);
    }

    // A hold whose wear-off line never arrives is dropped on outside proof (a move
    // that landed), leaving unrelated conditions latched. (report paradigm-20261003-161904)
    [Fact]
    public void ClearFlag_DropsOnlyRecordsCarryingThatFlag()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("hold person", MessageFlags.MovementPrevented,
            applied: "Your legs are paralyzed!", endsWith: "You can move again!"));
        h.Messages.Messages.Add(MakeRecord("Poison", MessageFlags.Poisoned,
            applied: "You have been poisoned!", endsWith: "The poison wears off."));
        h.Feed("Your legs are paralyzed!");
        h.Feed("You have been poisoned!");

        h.Tracker.ClearFlag(MessageFlags.MovementPrevented, "test");

        Assert.False(h.Tracker.IsMovementPrevented);
        Assert.True(h.Tracker.IsPoisoned);
        Assert.Empty(h.Ended);

        // The record is free to latch again on its next applied line.
        h.Feed("Your legs are paralyzed!");
        Assert.True(h.Tracker.IsMovementPrevented);
    }

    [Fact]
    public void AbsentSentinelLine_CompilesNoPattern()
    {
        // A {null}/{void}/{empty} sentinel means "this spell has no such line" — it must
        // never index as a literal matcher, so even feeding the sentinel text verbatim
        // latches nothing.
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Sentinel poison",
            MessageFlags.Poisoned,
            applied: "{void}",
            endsWith: "{null}"));

        h.Feed("{void}");

        Assert.False(h.Tracker.IsPoisoned);
        Assert.Empty(h.Applied);
    }

    [Fact]
    public void DisabledRecord_IsIgnoredWholesale()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison (off)",
            MessageFlags.Poisoned | MessageFlags.Disabled,
            applied: "You have been poisoned!",
            endsWith: "The poison wears off."));

        h.Feed("You have been poisoned!");

        // A Disabled record never indexes: no apply event, no flag latched.
        Assert.Empty(h.Applied);
        Assert.False(h.Tracker.IsPoisoned);
        Assert.Equal(MessageFlags.None, h.Tracker.ActiveFlags);
    }

    // Stock's `stat` lists an active effect as its bare applied line; inside the stat
    // screen it's a readout, not a cast. The same line outside it still applies.
    [Fact]
    public void StockStatReadout_InsideTheStatScreen_DoesNotApply()
    {
        using Harness h = new();
        bool inStat = true;
        h.Tracker.SetStatScreenProbe(() => inStat);
        h.Messages.Messages.Add(MakeRecord("Mageshield",
            MessageFlags.None,
            applied: "You feel protected!",
            endsWith: "Your mageshield shimmers and fades."));

        h.Feed("You feel protected!");
        Assert.Empty(h.Applied);

        inStat = false;
        h.Feed("You feel protected!");
        Assert.Single(h.Applied);
    }

    [Theory]
    [InlineData("You feel lucky! (411s)")]     // seconds readout (from report 232454)
    [InlineData("You feel lucky! (6m 51s)")]    // longer buff, minutes+seconds form
    public void StatReadout_WithRemainingTime_DoesNotApply(string readout)
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Bless",
            MessageFlags.None,
            applied: "You feel lucky!",
            endsWith: "You no longer feel lucky!"));

        // A `stat` status readout of an already-up buff (trailing "(Ns)") is NOT a fresh
        // cast — its shared effect text can't identify which buff is up, and matching it
        // falsely latched buffs on login and suppressed the real cast's confirm.
        h.Feed(readout);
        Assert.Empty(h.Applied);
        Assert.False(h.Tracker.IsActiveByName("Bless"));

        // The bare effect line — a genuine fresh cast, no parenthetical — still applies.
        h.Feed("You feel lucky!");
        Assert.Single(h.Applied);
        Assert.True(h.Tracker.IsActiveByName("Bless"));
    }

    // ----- LastActionFailed (confusion fumble) ------------------------

    [Fact]
    public void Fumble_FiresActionFailed()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("fumble",
            MessageFlags.Confused | MessageFlags.LastActionFailed,
            applied: "You fumble in confusion",
            endsWith: "The effects of confusion wear off"));

        h.Feed("You fumble in confusion!");

        Assert.Single(h.ActionFailed);
        Assert.Equal("fumble", h.ActionFailed[0].Name);
    }

    // ----- ConfuseFumbleLine (data-driven movement-refusal source) ----

    [Fact]
    public void IsConfuseFumbleLine_MatchesConfiguredWordings_WholeLineOnly()
    {
        using Harness h = new();
        // A Confused record carrying two fumble wordings, one per line.
        h.Messages.Messages.Add(new MessageRecord(
            Id: "conv", Name: "convulsions",
            Flags: MessageFlags.Confused, RawFlagsHex: 2,
            CasterMessage: "", TargetMessage: "", WitnessMessage: "",
            AppliedMessage: "You are in convulsions!", AppliedEndsWith: "",
            Links: null,
            ConfuseFumbleLine: "You fumble in confusion!\nYou convulse violently"));

        // Whole-line match, tolerant of a trailing '!'/'.' and case.
        Assert.True(h.Tracker.IsConfuseFumbleLine("You fumble in confusion!"));
        Assert.True(h.Tracker.IsConfuseFumbleLine("you fumble in confusion."));
        Assert.True(h.Tracker.IsConfuseFumbleLine("  You convulse violently  "));
        // The onset line isn't a fumble wording, and a chat line quoting one must not
        // match (whole-line, not substring).
        Assert.False(h.Tracker.IsConfuseFumbleLine("You are in convulsions!"));
        Assert.False(h.Tracker.IsConfuseFumbleLine("Bob says 'You fumble in confusion!'"));
    }

    // Paradigm answers a held character's move with the hold's own applied line, so
    // the line counts as a refusal only while that hold is on.
    [Fact]
    public void IsActiveHoldLine_OnlyWhileTheHoldIsOn_WholeLineOnly()
    {
        using Harness h = new();
        h.Messages.Messages.Add(new MessageRecord(
            Id: "roar", Name: "roar",
            Flags: MessageFlags.MovementPrevented, RawFlagsHex: 16,
            CasterMessage: "", TargetMessage: "", WitnessMessage: "",
            AppliedMessage: "You are too scared to flee", AppliedEndsWith: "You feel braver",
            Links: null));
        h.Messages.Messages.Add(new MessageRecord(
            Id: "pois", Name: "poison",
            Flags: MessageFlags.Poisoned, RawFlagsHex: 4,
            CasterMessage: "", TargetMessage: "", WitnessMessage: "",
            AppliedMessage: "You are poisoned!", AppliedEndsWith: "",
            Links: null));

        Assert.False(h.Tracker.IsActiveHoldLine("You are too scared to flee!"));

        h.Feed("You are too scared to flee!");
        h.Feed("You are poisoned!");
        Assert.True(h.Tracker.IsActiveHoldLine("You are too scared to flee!"));
        Assert.False(h.Tracker.IsActiveHoldLine("Bob says 'You are too scared to flee!'"));
        Assert.False(h.Tracker.IsActiveHoldLine("You are poisoned!"));

        h.Feed("You feel braver.");
        Assert.False(h.Tracker.IsActiveHoldLine("You are too scared to flee!"));
    }

    [Fact]
    public void IsConfuseFumbleLine_IgnoresFumbleLineOnNonConfusedRecord()
    {
        using Harness h = new();
        // ConfuseFumbleLine only contributes from a Confused record.
        h.Messages.Messages.Add(new MessageRecord(
            Id: "x", Name: "x",
            Flags: MessageFlags.Poisoned, RawFlagsHex: 4,
            CasterMessage: "", TargetMessage: "", WitnessMessage: "",
            AppliedMessage: "You are poisoned!", AppliedEndsWith: "",
            Links: null,
            ConfuseFumbleLine: "You fumble in confusion!"));

        Assert.False(h.Tracker.IsConfuseFumbleLine("You fumble in confusion!"));
    }

    [Fact]
    public void ConfuseFumbleLine_FiresActionFailed_EvenWithoutADedicatedLastActionFailedRecord()
    {
        // The shipped "convulsions" record carries Confused + ConfuseFumbleLine but
        // no separate LastActionFailed-flagged record for its fumble wordings (no
        // shipped record ever has). A fumble line must still re-send the swing:
        // report paradigm-20260908-211659, "sit here and get beat on".
        using Harness h = new();
        h.Messages.Messages.Add(new MessageRecord(
            Id: "conv", Name: "convulsions",
            Flags: MessageFlags.Confused, RawFlagsHex: 2,
            CasterMessage: "", TargetMessage: "", WitnessMessage: "",
            AppliedMessage: "You are in convulsions!", AppliedEndsWith: "Your body returns to normal.",
            Links: null,
            ConfuseFumbleLine: "You fumble in confusion!\nYou convulse violently"));

        h.Feed("You are in convulsions!");
        h.Feed("You convulse violently!");
        h.Feed("You convulse violently!");

        Assert.True(h.Tracker.IsConfused);
        Assert.Equal(2, h.ActionFailed.Count);        // every fumble line, not deduped
        Assert.All(h.ActionFailed, r => Assert.Equal("convulsions", r.Name));
    }

    [Fact]
    public void Fumble_FiresEveryLine_NotJustFirst()
    {
        // Confusion fumbles command after command while it lasts; the condition
        // is "applied" only once, but the retry must ride every fumble line.
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("fumble",
            MessageFlags.Confused | MessageFlags.LastActionFailed,
            applied: "You fumble in confusion",
            endsWith: "The effects of confusion wear off"));

        h.Feed("You fumble in confusion!");
        h.Feed("You fumble in confusion!");
        h.Feed("You fumble in confusion!");

        Assert.Equal(3, h.ActionFailed.Count);   // per-line, not deduped
        Assert.Single(h.Applied);                // condition applied only once
    }

    [Fact]
    public void NonActionFailedCondition_DoesNotFireActionFailed()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned,
            applied: "You have been poisoned!",
            endsWith: "The poison wears off."));

        h.Feed("You have been poisoned!");

        Assert.Empty(h.ActionFailed);
    }

    [Fact]
    public void EndsWith_ClearsActiveFlags()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned,
            applied: "You have been poisoned!",
            endsWith: "The poison wears off."));

        h.Feed("You have been poisoned!");
        Assert.True(h.Tracker.IsPoisoned);

        h.Feed("The poison wears off.");
        Assert.False(h.Tracker.IsPoisoned);
        Assert.Single(h.Ended);
    }

    [Fact]
    public void SharedAppliedLine_WearOff_FiresEndedForPrimaryOnly()
    {
        // Two DISTINCT buffs that share an applied line ("You feel protected!") but
        // carry their OWN wear-off lines (the mageshield / holy-armour family). When
        // one wears off, ConditionEnded must fire for THAT record only, not the
        // sibling — its sole consumer is the self-buff recast timers, which anchor on
        // each buff's own cast code, so a shared line must not clear a different buff
        // we cast. The sibling is still dropped from _active (it was co-latched on the
        // shared applied line), so flags stay honest.
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("mageshield", MessageFlags.None,
            applied: "You feel protected!", endsWith: "Your mageshield shimmers and fades."));
        h.Messages.Messages.Add(MakeRecord("holy armour", MessageFlags.None,
            applied: "You feel protected!", endsWith: "Your holy armour fades."));

        h.Feed("You feel protected!");        // both latch on the shared applied line
        Assert.Equal(2, h.Applied.Count);
        h.Ended.Clear();

        h.Feed("Your holy armour fades.");    // only holy armour's OWN wear-off fires

        Assert.Single(h.Ended);
        Assert.Equal("holy armour", h.Ended[0].Name);
    }

    [Fact]
    public void ConfusionWearOff_ClearsAllConfusedSources_IncludingCoLatchedFumble()
    {
        // A monster confuse with its own wear-off, plus the generic "you fumble in
        // confusion" (which also carries Confused so it can flag a confuse whose
        // set-line we missed), both latch. When the monster confuse wears off, the
        // fumble-held confusion must clear too — otherwise the flag (and the nav
        // pause it drives) stays stuck, because fumble's own generic wear-off never
        // fired. The two records do NOT share an applied line, so the alias-group
        // clear alone wouldn't reach fumble.
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("high-pitched scream",
            MessageFlags.Confused,
            applied: "You feel confused!",
            endsWith: "The effects of the death dog's shriek wear off!"));
        h.Messages.Messages.Add(MakeRecord("fumble",
            MessageFlags.Confused | MessageFlags.LastActionFailed,
            applied: "You fumble in confusion",
            endsWith: "The effects of confusion wear off"));

        h.Feed("You feel confused!");         // spell latches Confused
        h.Feed("You fumble in confusion!");   // fumble co-latches Confused
        Assert.True(h.Tracker.IsConfused);

        h.Feed("The effects of the death dog's shriek wear off!");   // spell wears off

        Assert.False(h.Tracker.IsConfused);   // both Confused sources cleared
    }

    [Fact]
    public void FlagWideClear_GeneralizesBeyondConfused_ToAnySharedFlag()
    {
        // The exact real-world shape (report paradigm-20260904-214452): several
        // records with UNRELATED applied lines all carry Blinded ("black curse" vs.
        // the generic "You are blind." family). The alias-group clear can't reach
        // across different applied lines, so only the flag-wide clear does. When the
        // curse's own wear-off fires, the "blind" sibling — latched on a totally
        // different applied line, with its own wear-off text that never arrives —
        // must clear too, or Blinded (and any auto-cure loop reading it) sticks
        // forever.
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("black curse", MessageFlags.Blinded,
            applied: "A black curse is upon you", endsWith: "Your vision returns to normal"));
        h.Messages.Messages.Add(MakeRecord("blind", MessageFlags.Blinded,
            applied: "You are blind", endsWith: "You can see again"));

        h.Feed("A black curse is upon you");   // curse latches Blinded
        h.Feed("You are blind");               // ambiguous generic line co-latches Blinded
        Assert.True(h.Tracker.IsBlinded);

        h.Feed("Your vision returns to normal");   // only the curse's OWN wear-off fires

        Assert.False(h.Tracker.IsBlinded);   // both Blinded sources cleared, not just the curse
    }

    [Fact]
    public void MultipleConditions_OrFlags()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));
        h.Messages.Messages.Add(MakeRecord("Blind",
            MessageFlags.Blinded,  "blinded!",  "vision returns."));

        h.Feed("You have been poisoned!");
        h.Feed("You have been blinded!");

        Assert.True(h.Tracker.IsPoisoned);
        Assert.True(h.Tracker.IsBlinded);
        Assert.Equal(MessageFlags.Poisoned | MessageFlags.Blinded,
                     h.Tracker.ActiveFlags);
    }

    [Fact]
    public void EndsOneCondition_KeepsOthers()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));
        h.Messages.Messages.Add(MakeRecord("Blind",
            MessageFlags.Blinded,  "blinded!",  "vision returns."));

        h.Feed("You have been poisoned!");
        h.Feed("You have been blinded!");

        h.Feed("Your vision returns.");

        Assert.True(h.Tracker.IsPoisoned);
        Assert.False(h.Tracker.IsBlinded);
    }

    // ----- idempotency -----------------------------------------------

    [Fact]
    public void SameAppliedTwice_NoDuplicateEvent()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));

        h.Feed("You have been poisoned!");
        h.Feed("You have been poisoned!");

        Assert.Single(h.Applied);
    }

    [Fact]
    public void EndsWithBeforeApplied_NoOp()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));

        h.Feed("The poison wears off.");

        Assert.False(h.Tracker.IsPoisoned);
        Assert.Empty(h.Ended);
    }

    [Fact]
    public void MultipleRecordsSameLine_CollapsesLog_ButFiresEachEvent()
    {
        using Harness h = new();
        // Three catalogue records that all emit the same effect text — models the
        // bless spell plus bless-proc items all reporting "You feel lucky!".
        h.Messages.Messages.Add(MakeRecord("bless",
            MessageFlags.None, "You feel lucky", "The effects of bless wear off"));
        h.Messages.Messages.Add(MakeRecord("black warhammer",
            MessageFlags.None, "You feel lucky", "The effects of bless wear off"));
        h.Messages.Messages.Add(MakeRecord("dark blessing",
            MessageFlags.None, "You feel lucky", "The blessing fades"));

        h.Feed("You feel lucky!");

        // Every record still fires ConditionApplied — CastingDirector needs to see
        // them all to pick out the one that maps to a cast code.
        Assert.Equal(3, h.Applied.Count);

        // But the program log collapses to a single applied row, not one per record.
        int appliedRows = h.Log.Snapshot()
            .Count(e => e.Source == ConditionTracker.LogCategory
                        && e.Message.Contains("condition applied"));
        Assert.Equal(1, appliedRows);
    }

    // ----- index rebuild on store changes ----------------------------

    [Fact]
    public void IndexRebuilds_OnStoreAdd()
    {
        using Harness h = new();
        h.Feed("You have been poisoned!");     // no record yet → no fire
        Assert.False(h.Tracker.IsPoisoned);

        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));
        h.Feed("You have been poisoned!");      // now matches

        Assert.True(h.Tracker.IsPoisoned);
    }

    [Fact]
    public void IndexRebuilds_DropsStaleActiveOnStoreReplace()
    {
        using Harness h = new();
        MessageRecord poison = MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off.");
        h.Messages.Messages.Add(poison);

        h.Feed("You have been poisoned!");
        Assert.True(h.Tracker.IsPoisoned);

        h.Messages.Messages.Clear();    // user deleted the record
        Assert.False(h.Tracker.IsPoisoned);
        Assert.Equal(MessageFlags.None, h.Tracker.ActiveFlags);
    }

    // ----- manual clear ----------------------------------------------

    [Fact]
    public void ClearAll_Resets()
    {
        using Harness h = new();
        h.Messages.Messages.Add(MakeRecord("Poison",
            MessageFlags.Poisoned, "poisoned!", "poison wears off."));

        h.Feed("You have been poisoned!");
        Assert.True(h.Tracker.IsPoisoned);

        h.Tracker.ClearAll();
        Assert.False(h.Tracker.IsPoisoned);
        Assert.Equal(MessageFlags.None, h.Tracker.ActiveFlags);
    }

    // ----- clobber-clear latch release -------------------------------

    [Fact]
    public void ReleaseApplied_LetsAReCastReConfirm()
    {
        // A fresh-cast applied line latches once; an identical repeat is deduped (fires
        // nothing) while the record stays active. But when a landing buff clobbers this
        // one (RemovesSpell), the CastingDirector releases its latch — and the buff's
        // RE-CAST must then re-confirm, so it can drive its own clobber-clear against
        // whatever replaced it. Report paradigm-20260910-012303: a re-cast greater bless
        // never dropped an active chant because gbls stayed latched from before chant
        // clobbered it, so its "You feel VERY lucky!" re-cast was deduped forever.
        using Harness h = new();
        MessageRecord gbls = MakeRecord("greater bless", MessageFlags.None,
            applied: "You feel VERY lucky!", endsWith: "");
        h.Messages.Messages.Add(gbls);

        h.Feed("You feel VERY lucky!");                    // first cast — latches, fires
        Assert.Single(h.Applied);

        h.Feed("You feel VERY lucky!");                    // re-cast while latched — deduped
        Assert.Single(h.Applied);

        h.Tracker.ReleaseApplied(r => r.Id == gbls.Id);    // a clobber released the latch

        h.Feed("You feel VERY lucky!");                    // re-cast now re-confirms
        Assert.Equal(2, h.Applied.Count);
    }

    [Fact]
    public void ReleaseApplied_NonMatch_LeavesLatchIntact()
    {
        using Harness h = new();
        MessageRecord gbls = MakeRecord("greater bless", MessageFlags.None,
            applied: "You feel VERY lucky!", endsWith: "");
        h.Messages.Messages.Add(gbls);

        h.Feed("You feel VERY lucky!");
        Assert.Single(h.Applied);

        h.Tracker.ReleaseApplied(_ => false);              // nothing matches
        h.Feed("You feel VERY lucky!");                    // still latched → still deduped
        Assert.Single(h.Applied);
    }
    // Report paradigm-20261002-120516: `use deck` drew the Priest, but every card
    // record shared the draw's "The gaze of luck is upon you." line, so all thirteen
    // latched and card-void's Confused flag held navigation. In the shipped seeds each
    // card is known by its own text; the shared line belongs to the deck's record.
    [Theory]
    [InlineData("paradigm")]
    [InlineData("stock")]
    public void ShippedSeed_ADeckDraw_LatchesTheCardDrawn_NotEveryCard(string realm)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            List<MessageRecord> seed = JsonStore.Load<List<MessageRecord>>(
                Path.Combine(dir, $"Messages.{realm}.seed.json"))!;
            using Harness h = new();
            h.Messages.Messages.ReplaceAll(seed);

            h.Feed("You grab your deck of cards and draw...");
            h.Feed("The gaze of luck is upon you.");
            Assert.False(h.Tracker.IsConfused);
            Assert.DoesNotContain(h.Applied, r => r.Name.StartsWith("card-", StringComparison.Ordinal) && r.Name != "card-draw");

            h.Feed(" \"When the Priest is played, wisdom and insight are paramount. Faith and");
            h.Feed("  perseverance shall be yours.\"");
            Assert.Contains(h.Applied, r => r.Name == "card-priest");
            Assert.DoesNotContain(h.Applied, r => r.Name == "card-void");
            Assert.False(h.Tracker.IsConfused);

            // The Void (a Stock draw) does confuse, and the deck's wear-off clears it.
            h.Feed(" \"Unending darkness is the hallmark of the Void. You will be wrapped in a");
            Assert.True(h.Tracker.IsConfused);
            h.Feed("The gaze of luck is no longer upon you!");
            Assert.False(h.Tracker.IsConfused);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
