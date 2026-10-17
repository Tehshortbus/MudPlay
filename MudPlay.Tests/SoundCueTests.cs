using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Sounds;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Sound cues: which cue plays and how loud (SoundCueEngine), the built-in tones
// (SoundTones), and the boss-timer cues (SoundBossWatcher). Actual playback goes
// through the operating system's player and isn't exercised here.
public sealed class SoundCueTests : IDisposable
{
    private readonly string _set = "sound-cue-test-" + Path.GetRandomFileName();
    private readonly string _seedPath =
        Path.Combine(Path.GetTempPath(), "snd-" + Path.GetRandomFileName() + ".json");

    public void Dispose()
    {
        try { string d = AppPaths.GameDataSetDir(_set); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        try { if (File.Exists(_seedPath)) File.Delete(_seedPath); } catch { }
    }

    private sealed class Rig
    {
        public SoundSettings Settings = new();
        public DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public readonly List<(string Sound, int Volume)> Played = new();
        public readonly SoundCueEngine Engine;

        public Rig() => Engine = new SoundCueEngine(() => Settings, (s, v) => Played.Add((s, v)), () => Now);

        public void Set(string cue, bool enabled = true, string? sound = null, int volume = 100, int every = 0)
        {
            Settings.Cues[cue] = new SoundCueSettings
            {
                Enabled = enabled, Sound = sound ?? SoundCues.Find(cue)!.DefaultSound, Volume = volume, Every = every,
            };
            Engine.Invalidate();
        }
    }

    // ----- SoundCueEngine ------------------------------------------------------

    [Fact]
    public void EveryCue_StartsOff()
    {
        Rig rig = new();
        foreach (SoundCue cue in SoundCues.All)
        {
            rig.Engine.Fire(cue.Id);
            rig.Engine.FireWith(cue.Id, "/sounds/a.wav");
        }
        Assert.Empty(rig.Played);
    }

    [Fact]
    public void TickedCue_PlaysItsDefaultSound_AtMasterTimesCueVolume()
    {
        Rig rig = new();
        rig.Set(SoundCues.LevelUp);
        rig.Engine.Fire(SoundCues.LevelUp);
        Assert.Equal(new[] { (SoundTones.Ding, 80) }, rig.Played);   // master 80 × cue 100
    }

    [Fact]
    public void MasterSwitchOff_SilencesEveryCue()
    {
        Rig rig = new();
        rig.Set(SoundCues.LevelUp);
        rig.Set(SoundCues.Trigger, sound: string.Empty);
        rig.Settings.Enabled = false;
        rig.Engine.Fire(SoundCues.LevelUp);
        rig.Engine.FireWith(SoundCues.Trigger, "/tmp/x.wav");
        Assert.Empty(rig.Played);
    }

    [Theory]
    [InlineData(100, 100, 100)]
    [InlineData(50, 50, 25)]
    [InlineData(80, 25, 20)]
    public void Volume_IsMasterTimesCue(int master, int cue, int expected)
    {
        Rig rig = new();
        rig.Settings.MasterVolume = master;
        rig.Set(SoundCues.LevelUp, volume: cue);
        rig.Engine.Fire(SoundCues.LevelUp);
        Assert.Equal(expected, Assert.Single(rig.Played).Volume);
    }

    [Fact]
    public void ZeroVolume_DoesNotPlay()
    {
        Rig rig = new();
        rig.Set(SoundCues.LevelUp, volume: 0);
        rig.Engine.Fire(SoundCues.LevelUp);
        Assert.Empty(rig.Played);
    }

    [Fact]
    public void ABurstOfOneCue_IsOneSound_UntilTheGapPasses()
    {
        Rig rig = new();
        rig.Set(SoundCues.LevelUp);
        rig.Set(SoundCues.Death);
        rig.Engine.Fire(SoundCues.LevelUp);
        rig.Now += TimeSpan.FromMilliseconds(400);
        rig.Engine.Fire(SoundCues.LevelUp);
        rig.Engine.Fire(SoundCues.Death);   // a different cue isn't held back
        Assert.Equal(2, rig.Played.Count);

        rig.Now += TimeSpan.FromSeconds(1);
        rig.Engine.Fire(SoundCues.LevelUp);
        Assert.Equal(3, rig.Played.Count);
    }

    [Fact]
    public void SettingsAreKept_UntilInvalidated()
    {
        Rig rig = new();
        rig.Engine.Fire(SoundCues.WalkFinished);
        SoundSettings edited = new();
        edited.Cues[SoundCues.WalkFinished] = new SoundCueSettings { Enabled = true, Sound = SoundTones.Ding };
        rig.Settings = edited;

        rig.Engine.Fire(SoundCues.WalkFinished);
        Assert.Empty(rig.Played);   // still the copy read before the edit

        rig.Engine.Invalidate();
        rig.Engine.Fire(SoundCues.WalkFinished);
        Assert.Single(rig.Played);
    }

    [Fact]
    public void KillMilestone_FiresEveryNthKill()
    {
        Rig rig = new();
        rig.Set(SoundCues.KillMilestone, every: 3);
        for (int i = 0; i < 7; i++)
        {
            rig.Engine.NoteKill();
            rig.Now += TimeSpan.FromSeconds(5);
        }
        Assert.Equal(2, rig.Played.Count);   // kills 3 and 6
    }

    [Fact]
    public void LoopMilestone_FiresOnEveryNthLapOfTheRunnersCount()
    {
        Rig rig = new();
        rig.Set(SoundCues.LoopMilestone, every: 2);
        foreach (int lap in new[] { 1, 2, 3, 4 })
        {
            rig.Engine.NoteLap(lap);
            rig.Now += TimeSpan.FromSeconds(5);
        }
        Assert.Equal(2, rig.Played.Count);   // laps 2 and 4

        rig.Engine.NoteLap(0);   // a fresh loop's count never rings at zero
        Assert.Equal(2, rig.Played.Count);
    }

    [Fact]
    public void Milestone_OffOrZero_NeverFires()
    {
        Rig rig = new();
        rig.Set(SoundCues.KillMilestone, every: 0);
        rig.Engine.NoteKill();
        rig.Set(SoundCues.KillMilestone, enabled: false, every: 1);
        rig.Engine.NoteKill();
        Assert.Empty(rig.Played);
    }

    [Theory]
    [InlineData(SoundCues.Trigger)]
    [InlineData(SoundCues.EventFired)]
    public void NamedSound_PlaysAtItsCueVolume_ThrottledPerSound(string cue)
    {
        Rig rig = new();
        rig.Set(cue, sound: string.Empty, volume: 50);
        rig.Engine.FireWith(cue, "/sounds/a.wav");
        rig.Engine.FireWith(cue, "/sounds/a.wav");   // the same sound inside the gap
        rig.Engine.FireWith(cue, SoundTones.Chime);  // a different one still plays

        Assert.Equal(new[] { ("/sounds/a.wav", 40), (SoundTones.Chime, 40) }, rig.Played);

        rig.Set(cue, enabled: false, sound: string.Empty);
        rig.Now += TimeSpan.FromSeconds(5);
        rig.Engine.FireWith(cue, "/sounds/a.wav");
        Assert.Equal(2, rig.Played.Count);
    }

    [Fact]
    public void CueWithNoSoundOfItsOwn_PlaysNothingByItself()
    {
        Rig rig = new();
        rig.Set(SoundCues.EventFired, sound: string.Empty);
        rig.Engine.Fire(SoundCues.EventFired);
        Assert.Empty(rig.Played);
    }

    [Fact]
    public void Preview_PlaysAtTheCueVolume_WhateverTheSwitchesSay()
    {
        Rig rig = new();
        rig.Settings.Enabled = false;
        rig.Set(SoundCues.EventFired, enabled: false, sound: string.Empty, volume: 50);
        rig.Engine.Preview(SoundCues.EventFired, SoundTones.Coin);
        rig.Engine.Preview(SoundCues.EventFired, SoundTones.Coin);   // no gap on a test button
        Assert.Equal(new[] { (SoundTones.Coin, 40), (SoundTones.Coin, 40) }, rig.Played);
    }

    [Fact]
    public void UnknownCue_IsIgnored()
    {
        Rig rig = new();
        rig.Engine.Fire("no-such-cue");
        Assert.Empty(rig.Played);
    }

    // ----- Catalogue + settings ------------------------------------------------

    [Fact]
    public void CueIds_AreUnique_AndEveryCueWithItsOwnSoundDefaultsToARealTone()
    {
        Assert.Equal(SoundCues.All.Count, SoundCues.All.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (SoundCue cue in SoundCues.All.Where(c => c.HasOwnSound))
            Assert.NotNull(SoundTones.Render(cue.DefaultSound));
    }

    // ----- SoundTones ----------------------------------------------------------

    [Fact]
    public void EveryTone_RendersAPlayableWav()
    {
        foreach ((string id, _) in SoundTones.All)
        {
            byte[] wav = SoundTones.Render(id)!;
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
            Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));

            // Not silent, and never clipped flat against the 16-bit rail.
            int peak = 0;
            for (int i = 44; i + 1 < wav.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav, i)));
            Assert.InRange(peak, 4000, short.MaxValue - 1);
        }
        Assert.Null(SoundTones.Render("tone:no-such-tone"));
    }

    // ----- SoundBossWatcher ----------------------------------------------------

    private (Rig rig, SoundBossWatcher watcher, BossTimerStore timers) BossRig(RealmType realm, BossRespawnType type,
        bool inGame = true)
    {
        string dir = AppPaths.GameDataSetDir(_set);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"),
            JsonSerializer.Serialize(new[] { new { Name = "ogre king", Number = 50, RegenTime = 10, GameLimit = 1 } }));
        File.WriteAllText(Path.Combine(dir, "Info.json"),
            JsonSerializer.Serialize(new[] { new { Legit = realm == RealmType.ParaMud ? 2 : 0 } }));
        JsonStore.Save(_seedPath, new List<BossDef>
        {
            new()
            {
                Name = "ogre king", MonsterNumber = 50, Rooms = new() { "3/300" }, RespawnType = type,
                InStock = true, InParadigm = true,
            },
        });

        GameDataCache cache = new();
        cache.SwitchSet(_set);
        BossStore bosses = new(seedPath: _seedPath);
        bosses.OnRealmChanged(dir);
        BossTimerStore timers = new(bosses, cache);
        timers.OnRealmChanged(dir);

        Rig rig = new();
        rig.Set(SoundCues.BossWindow);
        rig.Set(SoundCues.BossReady);
        SoundBossWatcher watcher = new(rig.Engine, bosses, timers, cache, () => inGame, now: () => rig.Now);
        return (rig, watcher, timers);
    }

    [Fact]
    public void BossTimer_FiresWindowThenReady_OncePerKill()
    {
        var (rig, watcher, timers) = BossRig(RealmType.ParaMud, BossRespawnType.Timed);
        DateTimeOffset killed = rig.Now;
        timers.MarkKilled("ogre king", killed);   // 10 h timer: window at 8 h, respawn at 10 h

        rig.Now = killed + TimeSpan.FromHours(7.9);
        watcher.Evaluate();
        Assert.Empty(rig.Played);

        rig.Now = killed + TimeSpan.FromHours(8) + TimeSpan.FromSeconds(20);
        watcher.Evaluate();
        watcher.Evaluate();
        Assert.Equal(SoundTones.Chime, Assert.Single(rig.Played).Sound);

        // The respawn itself, which is when the store stops listing the timer as running.
        rig.Now = killed + TimeSpan.FromHours(10) + TimeSpan.FromSeconds(20);
        watcher.Evaluate();
        watcher.Evaluate();
        Assert.Equal(new[] { SoundTones.Chime, SoundTones.Alert }, rig.Played.Select(p => p.Sound));

        // A fresh kill arms both again.
        DateTimeOffset again = rig.Now;
        timers.MarkKilled("ogre king", again);
        rig.Now = again + TimeSpan.FromHours(8) + TimeSpan.FromSeconds(20);
        watcher.Evaluate();
        Assert.Equal(3, rig.Played.Count);
    }

    [Fact]
    public void BossTimer_Stock_WindowOpensAt87Point5Percent()
    {
        var (rig, watcher, timers) = BossRig(RealmType.Stock, BossRespawnType.Timed);
        DateTimeOffset killed = rig.Now;
        timers.MarkKilled("ogre king", killed);

        rig.Now = killed + TimeSpan.FromHours(8.5);
        watcher.Evaluate();
        Assert.Empty(rig.Played);

        rig.Now = killed + TimeSpan.FromHours(8.75) + TimeSpan.FromSeconds(20);
        watcher.Evaluate();
        Assert.Single(rig.Played);
    }

    [Fact]
    public void BossTimer_ThatRanOutWhileAway_StaysSilent()
    {
        var (rig, watcher, timers) = BossRig(RealmType.ParaMud, BossRespawnType.Timed);
        DateTimeOffset killed = rig.Now;
        timers.MarkKilled("ogre king", killed);

        rig.Now = killed + TimeSpan.FromHours(14);   // first look, hours after both moments
        watcher.Evaluate();
        Assert.Empty(rig.Played);
    }

    [Fact]
    public void BossTimer_NotInGame_DoesNothing()
    {
        // The same timer through the same window, once out of the game and once in:
        // only the in-game watcher plays.
        foreach (bool inGame in new[] { false, true })
        {
            var (rig, watcher, timers) = BossRig(RealmType.ParaMud, BossRespawnType.Timed, inGame);
            DateTimeOffset killed = rig.Now;
            timers.MarkKilled("ogre king", killed);
            rig.Now = killed + TimeSpan.FromHours(7.9);
            watcher.Evaluate();
            rig.Now = killed + TimeSpan.FromHours(8) + TimeSpan.FromSeconds(20);
            watcher.Evaluate();

            Assert.Equal(inGame ? 1 : 0, rig.Played.Count);
        }
    }
}
