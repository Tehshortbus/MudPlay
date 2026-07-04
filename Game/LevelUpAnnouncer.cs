using System;
using System.Collections.Generic;
using System.Text.Json;
using FujinTerm.Game.Calculators;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Broadcasts "I can now train to level: N" on a chosen chat channel the moment a
// live experience gain pushes a Level-Projection row's "Exp to level" to 0 — the
// same cumulative-threshold test the projection grid
// (ExperienceTableCalculator.CalcExpNeeded) and the auto-trainer use. Driven by
// the Settings → Auto-Trainer "Announce level-ups" toggle + channel.
//
// No login spam. Banked levels already trainable when a character loads (or when
// a stat/exp poll re-anchors the total) are absorbed into a silent baseline:
// StatParser.ScreenParsed and ProfileService.ProfileLoaded move the high-water
// mark without announcing. Only StatParser.ExperienceGained — a genuine "You
// gain N experience." accrual — can announce, and only for levels strictly above
// the high-water mark. A gain that arrives before any baseline is itself treated
// as the silent seed.
//
// The high-water mark resets per character (ProfileService.ProfileClosed) so a
// profile swap never carries one character's announced levels into another.
// Disabled crossings still advance the high-water mark, so toggling the feature
// on mid-session announces future transitions only — never a backlog.
public sealed class LevelUpAnnouncer : IDisposable
{
    // Safety cap on the per-gain threshold scan — a single gain can bank several
    // levels at once, but never an unbounded number.
    private const int MaxLevelScan = 60;

    private readonly PlayerStats _stats;
    private readonly StatParser _statParser;
    private readonly GameDataCache _gameData;
    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    private int _announcedThrough;   // highest level already known-reachable (announced or pre-banked)
    private bool _haveBaseline;      // false until a baseline is seeded for the current character
    private bool _disposed;

    public LevelUpAnnouncer(PlayerStats stats, StatParser statParser, GameDataCache gameData,
                            ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(statParser);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(profile);
        _stats = stats;
        _statParser = statParser;
        _gameData = gameData;
        _profile = profile;
        _log = log;

        _statParser.ExperienceGained += OnExperienceGained;
        _statParser.ScreenParsed += OnScreenParsed;
        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosed;

        // Seed from whatever's already loaded (a profile may be live before this
        // service is constructed). A zeroed / unresolved chart leaves the baseline
        // un-established until the first real reading.
        SeedBaseline(_stats.Level, _stats.Exp, _stats.Class, _stats.Race);
    }

    // Bind the gate-wrapped wire sender (set in MainWindowViewModel after telnet
    // connects).
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — every buffer the announcer pushed to the wire, in order.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // A new character (or a profile close before a swap) — drop the baseline so the
    // next character re-seeds from its own banked exp rather than inheriting ours.
    private void OnProfileClosed()
    {
        _haveBaseline = false;
        _announcedThrough = 0;
    }

    // Re-seed silently from the restored snapshot. Reads the event's snapshot (not
    // live _stats, which a sibling ProfileLoaded handler hydrates separately) so the
    // baseline is correct regardless of subscriber ordering.
    private void OnProfileLoaded(CharacterProfile profile)
    {
        if (profile.LastKnownStats is not { } snap)
        {
            _haveBaseline = false;
            _announcedThrough = 0;
            return;
        }
        SeedBaseline(snap.Level, snap.Exp, snap.Class, snap.Race);
    }

    // An authoritative stat/exp poll re-anchored the total — re-seed the high-water
    // mark to whatever's reachable now, silently. Exp climbs during normal play, so
    // this usually moves the mark up or holds it. It moves DOWN only when the total
    // genuinely shrank: a character remade after losing all lives / rerolling keeps
    // just a fraction of earned exp, and re-anchoring then lets levels announce again
    // as the remade character re-earns them. (It can also drop to correct a live
    // counter that over-counted a gain line.)
    private void OnScreenParsed(LastKnownStats snapshot) =>
        SeedBaseline(snapshot.Level, snapshot.Exp, snapshot.Class, snapshot.Race);

    private void SeedBaseline(int level, int exp, string className, string raceName)
    {
        int highest = HighestTrainable(level, exp, className, raceName);
        if (highest < 0) return;   // chart/level not resolvable yet — wait for a real reading
        _announcedThrough = highest;
        _haveBaseline = true;
    }

    private void OnExperienceGained(int newTotal)
    {
        int highest = HighestTrainable(_stats.Level, _stats.Exp, _stats.Class, _stats.Race);
        if (highest < 0) return;   // can't resolve the chart yet

        if (!_haveBaseline)
        {
            // First observation for this character arrived as a gain — establish the
            // baseline silently rather than dumping the pre-banked backlog.
            _announcedThrough = highest;
            _haveBaseline = true;
            return;
        }

        if (highest <= _announcedThrough) return;

        AutoTrainerSettings s = ReadSettings();
        if (s.AnnounceLevelUps && _wire.IsBound)
            for (int lvl = _announcedThrough + 1; lvl <= highest; lvl++)
                Announce(lvl, s.AnnounceChannel);

        // Advance the high-water mark even when announcing is off, so a later
        // toggle-on doesn't replay levels that were crossed while it was off.
        _announcedThrough = highest;
    }

    private void Announce(int level, AnnounceChannel channel)
    {
        string msg = $"I can now train to level: {level}";
        // MajorMUD chat channels: gossip / gangpath are word-verbs, but say and
        // yell are punctuation-prefixed (a leading '.' speaks to the room, a
        // leading '"' yells) — there is no `say`/`yell` keyword, so prefixing
        // one just speaks the literal text. See AliasEngine.NameConflictReason
        // for the same channel-prefix table.
        string wire = channel switch
        {
            AnnounceChannel.Gossip => $"gos {msg}",
            AnnounceChannel.Yell   => $"\"{msg}",
            AnnounceChannel.Say    => $".{msg}",
            _                      => $"gang {msg}",   // Gangpath (default)
        };
        _wire.Send(wire);
        _log?.Info("LevelUp", $"Announced trainable level {level} on {channel}.");
    }

    // Highest level the running exp already meets the cumulative threshold for — the
    // top Level-Projection row whose "Exp to level" is 0. Returns the current level
    // when nothing above is banked, or -1 when the chart can't be resolved yet.
    private int HighestTrainable(int level, int exp, string className, string raceName)
    {
        if (level <= 0) return -1;
        int chart = ExperienceTableCalculator.CalcExpChart(
            GetInt(_gameData.FindRowByName("Classes", className), "ExpTable"),
            GetInt(_gameData.FindRowByName("Races", raceName), "ExpTable"));
        if (chart <= 0) return -1;

        RealmType realm = _gameData.ActiveRealm;
        int highest = level;
        for (int n = level + 1; n <= level + MaxLevelScan; n++)
        {
            if (exp < ExperienceTableCalculator.CalcExpNeeded(n, chart, realm)) break;
            highest = n;
        }
        return highest;
    }

    private AutoTrainerSettings ReadSettings()
    {
        if (_profile.Current?.Settings is { } settings && settings.TryGetValue("AutoTrainer", out JsonElement json))
        {
            try { return JsonSerializer.Deserialize<AutoTrainerSettings>(json) ?? new AutoTrainerSettings(); }
            catch { /* malformed settings → defaults (announce off) */ }
        }
        return new AutoTrainerSettings();
    }

    private static int GetInt(JsonElement? rowOpt, string prop)
    {
        if (rowOpt is not JsonElement row || row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(prop, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statParser.ExperienceGained -= OnExperienceGained;
        _statParser.ScreenParsed -= OnScreenParsed;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _profile.ProfileClosed -= OnProfileClosed;
    }
}
