using FujinTerm.Game.Combat;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 9.A — <see cref="MonsterDeathWatcher"/> specific-pattern matches
/// (per-monster DeathLine), shared-pattern fan-out, fallback path
/// (experience + Combat Off), and the index rebuild on
/// MonsterMessageStore changes.
/// </summary>
public sealed class MonsterDeathWatcherTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public LineExtractor Lines { get; }
        public LogService Log { get; } = new();
        public MonsterDeathWatcher Watcher { get; }
        public List<MonsterDeathEvent> Events { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            // Build a minimal LineExtractor — it's a Terminal-layer
            // class so we need its host Emulator dependency. Using a
            // fresh Emulator with a tiny screen is enough.
            Terminal.TerminalEmulator emulator = new(80, 24);
            Lines = new LineExtractor(emulator);
            Watcher = new MonsterDeathWatcher(Router, Monsters, Log);
            Watcher.AttachLineExtractor(Lines);
            Watcher.MonsterDied += Events.Add;
        }

        public void AddMonster(int number, string name, params string[] deathLines)
        {
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                HitYou: Array.Empty<string>(),
                HitOther: Array.Empty<string>(),
                DeathLine: deathLines,
                ArmorBlockYou: Array.Empty<string>(),
                ArmorBlockOther: Array.Empty<string>(),
                DodgeYou: Array.Empty<string>(),
                DodgeOther: Array.Empty<string>(),
                MissYou: Array.Empty<string>(),
                MissOther: Array.Empty<string>(),
                FlavorPrefixes: Array.Empty<string>(),
                AllowNoPrefix: true,
                Links: new[] { new GameDataLink("Monsters", number) }));
        }

        /// <summary>
        /// Push a line through the watcher's LineEmitted handler.
        /// Tests don't go through the full Terminal pipeline; they
        /// invoke the event directly so death-pattern matching is
        /// exercised without needing to feed bytes to the emulator.
        /// </summary>
        public void EmitLine(string text)
        {
            // Use reflection to invoke the private LineEmitted event
            // — simpler than wiring the full emulator pipeline.
            System.Reflection.FieldInfo? field = typeof(LineExtractor)
                .GetField("LineEmitted",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(Lines) is Action<LineExtractor.EmittedLine> handler)
            {
                handler(new LineExtractor.EmittedLine(
                    text, Array.Empty<CellAttributes>(),
                    DateTimeOffset.UtcNow, IsPromptLine: false));
            }
        }

        public void FeedRouter(string line)
        {
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public void Dispose() => Watcher.Dispose();
    }

    // ----- specific death pattern ------------------------------------

    [Fact]
    public void MatchesPerMonsterDeathLine()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat",
            "The giant rat falls to the ground with a tortured squeak.");

        h.EmitLine("The giant rat falls to the ground with a tortured squeak.");

        Assert.Single(h.Events);
        MonsterDeathEvent evt = h.Events[0];
        Assert.False(evt.IsFallback);
        Assert.Single(evt.Candidates);
        Assert.Equal("giant rat", evt.Candidates[0].Name);
        Assert.Equal(1, evt.Candidates[0].Number);
    }

    [Fact]
    public void SharedDeathLine_FiresWithMultipleCandidates()
    {
        // Some monsters share the same death wording in stock data.
        // The event carries every candidate so consumers can choose.
        using Harness h = new();
        h.AddMonster(1, "giant rat",  "It dies.");
        h.AddMonster(2, "alley cat",  "It dies.");

        h.EmitLine("It dies.");

        Assert.Single(h.Events);
        Assert.Equal(2, h.Events[0].Candidates.Count);
        Assert.Contains(h.Events[0].Candidates,
            c => c.Name == "giant rat" && c.Number == 1);
        Assert.Contains(h.Events[0].Candidates,
            c => c.Name == "alley cat" && c.Number == 2);
    }

    [Fact]
    public void NonMatchingLine_NoEmit()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat",
            "The giant rat falls to the ground with a tortured squeak.");

        h.EmitLine("Something completely different.");
        h.EmitLine("The acid slime dissolves into a puddle.");

        Assert.Empty(h.Events);
    }

    [Fact]
    public void PromptLine_Ignored()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat",
            "The giant rat falls to the ground with a tortured squeak.");

        // Synthesize a prompt-flagged line — must be ignored even if
        // the text matches.
        System.Reflection.FieldInfo? field = typeof(LineExtractor)
            .GetField("LineEmitted",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(h.Lines) is Action<LineExtractor.EmittedLine> handler)
        {
            handler(new LineExtractor.EmittedLine(
                "The giant rat falls to the ground with a tortured squeak.",
                Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: true));
        }

        Assert.Empty(h.Events);
    }

    [Fact]
    public void PatternsWithPlaceholders_Skipped()
    {
        // 2 of 1100 stock monsters carry {…} placeholders in their
        // DeathLine. Index skips these in v1 — literal-only match.
        using Harness h = new();
        h.AddMonster(1, "giant rat", "The {s} falls to the ground.");

        h.EmitLine("The giant rat falls to the ground.");

        Assert.Empty(h.Events);
    }

    [Fact]
    public void IndexRebuilds_OnStoreChange()
    {
        using Harness h = new();
        h.EmitLine("The giant rat falls to the ground with a tortured squeak.");
        Assert.Empty(h.Events);     // pattern not registered yet

        h.AddMonster(1, "giant rat",
            "The giant rat falls to the ground with a tortured squeak.");
        h.EmitLine("The giant rat falls to the ground with a tortured squeak.");
        Assert.Single(h.Events);    // new pattern picked up after rebuild
    }

    // ----- fallback path (exp + Combat Off) --------------------------

    [Fact]
    public void Fallback_ExpThenCombatOff_FiresWithoutCandidates()
    {
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
        MonsterDeathEvent evt = h.Events[0];
        Assert.True(evt.IsFallback);
        Assert.Empty(evt.Candidates);
        Assert.Equal(9, evt.ExperienceGained);
    }

    [Fact]
    public void Fallback_CombatOffWithoutRecentExp_DoesNotFire()
    {
        using Harness h = new();

        h.FeedRouter("*Combat Off*");

        Assert.Empty(h.Events);
    }

    [Fact]
    public void Fallback_DoesNotDoubleFire_OnSecondCombatOff()
    {
        // Once we've fired the fallback, the recorded exp is consumed
        // — a second Combat Off (e.g. mid-spell) doesn't re-fire.
        using Harness h = new();

        h.FeedRouter("You gain 9 experience.");
        h.FeedRouter("*Combat Off*");
        Assert.Single(h.Events);

        h.FeedRouter("*Combat Off*");
        Assert.Single(h.Events);
    }

    [Fact]
    public void SpecificDeath_SuppressesFallback_SameKill()
    {
        // Real wire shape: death line → exp → Combat Off. The specific
        // path fires; the fallback path must NOT fire on the same
        // sequence (it'd attribute the same kill twice).
        using Harness h = new();
        h.AddMonster(1, "giant rat",
            "The giant rat falls to the ground with a tortured squeak.");

        h.EmitLine("The giant rat falls to the ground with a tortured squeak.");
        h.FeedRouter("You gain 9 experience.");
        h.FeedRouter("*Combat Off*");

        Assert.Single(h.Events);
        Assert.False(h.Events[0].IsFallback);
    }
}
