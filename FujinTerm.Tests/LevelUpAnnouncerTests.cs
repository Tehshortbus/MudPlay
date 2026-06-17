using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR 10.8 — <see cref="LevelUpAnnouncer"/>: broadcasts "I can now train to
/// level: N" when a live "You gain N experience." line pushes a Level-Projection
/// row's "Exp to level" to 0. Verifies the anti-login-spam baseline (pre-banked
/// levels stay silent), the multi-level burst, the enable gate, and the
/// channel→wire-verb mapping.
/// </summary>
public sealed class LevelUpAnnouncerTests
{
    // ExpTable 100 (class) + 0 (race) → CalcExpChart(100,0) = 200, which spreads the
    // low-level thresholds well apart. Realm defaults to Stock (no Info table).
    private const int Chart = 200;

    private static long Threshold(int level) =>
        ExperienceTableCalculator.CalcExpNeeded(level, Chart, RealmType.Stock);

    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        public PlayerStats Stats { get; }
        public StatParser Parser { get; }
        public ProfileService Profile { get; }
        public LevelUpAnnouncer Announcer { get; }
        public List<byte[]> Wire { get; } = new();

        public Harness(int level, long exp, bool announce, AnnounceChannel channel = AnnounceChannel.Gangpath)
        {
            _root = Path.Combine(Path.GetTempPath(), "fterm-levelup-" + Guid.NewGuid().ToString("N"));
            string dir = Path.Combine(_root, "set");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Classes.json"), "[{\"Name\":\"Warrior\",\"ExpTable\":100,\"Number\":1}]");
            File.WriteAllText(Path.Combine(dir, "Races.json"), "[{\"Name\":\"Human\",\"ExpTable\":0}]");
            var gameData = new GameDataCache(_root);
            gameData.SwitchSet("set");

            // Establish the character BEFORE constructing the announcer so its ctor
            // seeds the baseline from these values (the login-restore equivalent).
            Stats = new PlayerStats { Class = "Warrior", Race = "Human", Level = level, Exp = (int)exp };
            Parser = new StatParser(Stats);

            Profile = new ProfileService();
            Profile.LoadBlank();
            Profile.Current!.Settings = new Dictionary<string, JsonElement>
            {
                ["AutoTrainer"] = JsonSerializer.SerializeToElement(
                    new AutoTrainerSettings { AnnounceLevelUps = announce, AnnounceChannel = channel }),
            };

            Announcer = new LevelUpAnnouncer(Stats, Parser, gameData, Profile, null);
            Announcer.SetWireSender(Wire.Add);
        }

        /// <summary>Feed a combat exp-gain line through the parser's always-on accrual path.</summary>
        public void Gain(long amount) => Parser.FeedTestLine($"You gain {amount} experience.");

        public IReadOnlyList<string> Sent =>
            Wire.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public void Dispose()
        {
            Announcer.Dispose();
            Parser.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void GainCrossingThreshold_AnnouncesNextLevel()
    {
        // One exp short of level 6, then gain exactly enough to reach it.
        using var h = new Harness(level: 5, exp: Threshold(6) - 1, announce: true);

        h.Gain(1);

        Assert.Equal(new[] { "gang I can now train to level: 6" }, h.Sent);
    }

    [Fact]
    public void SingleGain_BankingMultipleLevels_AnnouncesEach()
    {
        using var h = new Harness(level: 5, exp: Threshold(6) - 1, announce: true);

        // Jump from just-below-6 to at-or-past-8 in one gain.
        h.Gain(Threshold(8) - (Threshold(6) - 1));

        Assert.Equal(
            new[]
            {
                "gang I can now train to level: 6",
                "gang I can now train to level: 7",
                "gang I can now train to level: 8",
            },
            h.Sent);
    }

    [Fact]
    public void PreBankedLevels_AtLogin_AreNotAnnounced()
    {
        // Already sitting on enough exp for level 8 at construction (login restore).
        using var h = new Harness(level: 5, exp: Threshold(8), announce: true);

        // A gain that stays below the level-9 threshold must announce nothing —
        // levels 6..8 were pre-banked, so they belong to the silent baseline.
        h.Gain(1);

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void PreBankedThenGainPastBaseline_AnnouncesOnlyNewLevel()
    {
        using var h = new Harness(level: 5, exp: Threshold(8), announce: true);

        // Cross the level-9 threshold — only 9 is new; 6..8 stay silent.
        h.Gain(Threshold(9) - Threshold(8));

        Assert.Equal(new[] { "gang I can now train to level: 9" }, h.Sent);
    }

    [Fact]
    public void DisabledToggle_SuppressesAnnounce_ButStillTracksBaseline()
    {
        using var h = new Harness(level: 5, exp: Threshold(6) - 1, announce: false);

        h.Gain(Threshold(7) - (Threshold(6) - 1));   // crosses 6 and 7 while disabled

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void AlreadyAnnouncedLevel_DoesNotRepeatOnFurtherGains()
    {
        using var h = new Harness(level: 5, exp: Threshold(6) - 1, announce: true);

        h.Gain(1);                                   // reach 6 → announce
        h.Gain(Threshold(7) - Threshold(6) - 1);     // still short of 7 → nothing new

        Assert.Equal(new[] { "gang I can now train to level: 6" }, h.Sent);
    }

    // gangpath / gossip are word-verbs (trailing space before the message);
    // yell / say are punctuation-prefixed channels in MajorMUD ('"' yells, '.'
    // says to the room) with no separating space.
    [Theory]
    [InlineData(AnnounceChannel.Gangpath, "gang ")]
    [InlineData(AnnounceChannel.Gossip, "gos ")]
    [InlineData(AnnounceChannel.Yell, "\"")]
    [InlineData(AnnounceChannel.Say, ".")]
    public void AnnounceChannel_MapsToWireForm(AnnounceChannel channel, string prefix)
    {
        using var h = new Harness(level: 5, exp: Threshold(6) - 1, announce: true, channel: channel);

        h.Gain(1);

        Assert.Equal(new[] { $"{prefix}I can now train to level: 6" }, h.Sent);
    }

    [Fact]
    public void ProfileSwap_ResetsBaseline_SoNewCharactersBankIsSilent()
    {
        using var h = new Harness(level: 5, exp: Threshold(6) - 1, announce: true);

        // Swap characters (LoadBlank fires ProfileClosed + ProfileLoaded) — the
        // baseline drops, then re-seeds from the new character's exp.
        h.Profile.LoadBlank();
        h.Profile.Current!.Settings = new Dictionary<string, JsonElement>
        {
            ["AutoTrainer"] = JsonSerializer.SerializeToElement(
                new AutoTrainerSettings { AnnounceLevelUps = true }),
        };
        h.Stats.Exp = (int)Threshold(8);

        h.Gain(1);              // first gain for the new character → silent re-seed
        Assert.Empty(h.Sent);

        h.Gain(Threshold(9) - Threshold(8));   // now a genuine crossing
        Assert.Equal(new[] { "gang I can now train to level: 9" }, h.Sent);
    }
}
