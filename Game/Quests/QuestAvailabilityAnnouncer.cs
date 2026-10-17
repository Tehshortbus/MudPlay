using System;
using System.Collections.Generic;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Announces "[<quest> Quest is Now Available]" the moment training crosses a quest's
// minimum level, and dumps the full set of currently-available quests once at login.
//
// No login backlog spam: the first stat screen a character sees seeds a silent baseline
// of what's already available, so only genuine future level crossings announce. Training
// a several-level jump announces every quest whose gate falls inside the crossed range.
// The login dump (AnnounceLoginAvailable, fired once after the login sequence settles) is
// the exception — it lists everything available regardless of the baseline. Baseline + the
// announced set reset per character (ProfileService.ProfileClosed) so one character's
// announcements never carry into another.
//
// The available-quest set is supplied by an injected provider (QuestEligibility.Resolve),
// keeping this crossing/dedup state machine apart from the TBInfo crawl. The terminal write is done by the caller subscribing to QuestBecameAvailable.
public sealed class QuestAvailabilityAnnouncer : IDisposable
{
    private readonly StatParser _statParser;
    private readonly ProfileService _profile;
    private readonly Func<int> _currentLevel;
    private readonly Func<int, IReadOnlyList<QuestAvailabilityInfo>> _eligibleAtLevel;
    // True only once the character is actually in the realm (an in-game status
    // line has been observed). Gates the login dump so it can't fire while still
    // parked at the character-select / main menu. Null → treated as
    // in-realm.
    private readonly Func<bool>? _isInRealm;
    private readonly LogService? _log;

    // Quests already announced OR silently baselined this character-session.
    private readonly HashSet<(int Flag, int Step)> _announced = new();
    // Highest level observed; -1 until the first stat screen seeds the baseline.
    private int _baselineLevel = -1;
    private bool _disposed;

    // Raised with a quest's display name when it becomes available. The app subscribes and
    // writes the "[<name> Quest is Now Available]" line to the terminal.
    public event Action<string>? QuestBecameAvailable;

    public QuestAvailabilityAnnouncer(
        StatParser statParser, ProfileService profile, Func<int> currentLevel,
        Func<int, IReadOnlyList<QuestAvailabilityInfo>> eligibleAtLevel,
        Func<bool>? isInRealm = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(statParser);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(currentLevel);
        ArgumentNullException.ThrowIfNull(eligibleAtLevel);
        _statParser = statParser;
        _profile = profile;
        _currentLevel = currentLevel;
        _eligibleAtLevel = eligibleAtLevel;
        _isInRealm = isInRealm;
        _log = log;

        _statParser.ScreenParsed += OnScreenParsed;
        _profile.ProfileClosed += OnProfileClosed;
    }

    private void OnProfileClosed()
    {
        _announced.Clear();
        _baselineLevel = -1;
    }

    // A real stat screen re-anchored the level. First one seeds a silent baseline; a later
    // increase (training) announces the quests whose gate falls in the crossed range.
    private void OnScreenParsed(LastKnownStats snapshot) => Observe(snapshot.Level);

    // Advance the state machine for a level reading (fed from ScreenParsed).
    internal void Observe(int level)
    {
        if (level <= 0) return;

        if (_baselineLevel < 0)
        {
            // First reading — absorb everything already available without announcing.
            foreach (QuestAvailabilityInfo q in _eligibleAtLevel(level)) _announced.Add((q.Flag, q.Step));
            _baselineLevel = level;
            return;
        }

        if (level <= _baselineLevel)
        {
            _baselineLevel = level;   // re-anchor (a reroll can drop it) — silent
            return;
        }

        // Trained up (possibly several levels) — announce each newly-crossed quest once.
        bool enabled = AnnounceEnabled();
        foreach (QuestAvailabilityInfo q in _eligibleAtLevel(level))
            if (_announced.Add((q.Flag, q.Step)) && enabled) Raise(q.Name);
        _baselineLevel = level;
    }

    // Login dump: list every currently-available quest, once, regardless of the baseline.
    // Called after the login sequence settles. Advances the baseline so the follow-on stat
    // polls don't re-announce.
    public void AnnounceLoginAvailable()
    {
        // Don't dump login quests unless we're actually in the realm. Login
        // automation completes at the character-select / main menu; this dump is
        // scheduled a few seconds later on the assumption we've entered by then.
        // If realm auto-entry was suppressed or is slow, that timer would
        // otherwise announce quests while still parked at the menu, not yet in
        // the game (paradigm-20260909-172633).
        if (_isInRealm?.Invoke() == false)
        {
            _log?.Info("Quests", "Login quest dump skipped — not in the realm yet.");
            return;
        }
        int level = _currentLevel();
        if (level <= 0) return;
        bool enabled = AnnounceEnabled();
        foreach (QuestAvailabilityInfo q in _eligibleAtLevel(level))
        {
            _announced.Add((q.Flag, q.Step));
            if (enabled) Raise(q.Name);
        }
        if (_baselineLevel < level) _baselineLevel = level;
    }

    private bool AnnounceEnabled() => _profile.Current?.AnnounceAvailableQuests ?? true;

    private void Raise(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        QuestBecameAvailable?.Invoke(name);
        _log?.Info("Quests", $"Announced available quest: {name}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statParser.ScreenParsed -= OnScreenParsed;
        _profile.ProfileClosed -= OnProfileClosed;
    }
}
