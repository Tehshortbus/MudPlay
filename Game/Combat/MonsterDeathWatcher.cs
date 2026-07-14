using System.Collections.Generic;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Combat;

// Recognises monster deaths from MonsterMessageRecord.DeathLine patterns. Builds
// a lookup index from MonsterMessageStore at startup + on set switch and matches
// every emitted line against it. On a match, fires MonsterDied — consumers
// (RoomEntityClassifier in particular) remove the dead mob from the live
// observation so the next arrival event or CombatManager pass picks the right
// next target instead of being blocked by a stale "still in the list" entry.
//
// Fallback path: when no specific pattern matches (152 of 1100 monsters in stock
// data ship with empty DeathLine lists, and some patterns have wording variants
// we haven't seen), the "You gain N experience." + "*Combat Off*" sequence is a
// weaker but always-present signal. The watcher records the most recent
// experience-gain line and, when *Combat Off* fires within a short window, emits
// a fallback MonsterDeathEvent with IsFallback = true and no candidates.
// Consumers (e.g. CombatManager) attribute the death to whatever they were last
// attacking.
//
// Shared death lines: some patterns are reused across multiple monster records —
// the event's Candidates carries every match so downstream consumers can decide
// how to disambiguate (typically by cross-checking the live entity list).
public sealed class MonsterDeathWatcher : IDisposable
{
    // LogService category — appears as [MonsterDeath] rows per specific match +
    // fallback fire.
    public const string LogCategory = "MonsterDeath";

    // Window after a "You gain N exp." line within which a *Combat Off*
    // qualifies as a kill confirmation.
    private static readonly TimeSpan ExpToCombatOffWindow = TimeSpan.FromSeconds(5);

    // Window after a specific death line during which we suppress the fallback
    // path — the specific match has already fired the event; we don't want to
    // also fire the fallback for the same physical death.
    private static readonly TimeSpan SpecificDeathSuppressionWindow = TimeSpan.FromSeconds(3);

    private readonly MonsterMessageStore _monsters;
    private readonly LogService? _log;
    private readonly IDisposable _expSub;
    private readonly IDisposable _combatStatusSub;

    private Dictionary<string, List<MonsterDeathIdentity>> _deathIndex =
        new(StringComparer.Ordinal);

    private DateTimeOffset? _lastExpAt;
    private int? _lastExpAmount;
    private DateTimeOffset _lastSpecificDeathAt = DateTimeOffset.MinValue;

    private LineExtractor? _lines;
    private bool _disposed;

    // Fires once per observed death — specific or fallback. Subscribers run on
    // the line-emitting thread (the classifier remove path posts via the
    // standard PropertyChanged / event mechanism downstream).
    public event Action<MonsterDeathEvent>? MonsterDied;

    public MonsterDeathWatcher(
        MessageRouter router,
        MonsterMessageStore monsters,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(monsters);
        _monsters = monsters;
        _log = log;

        BuildIndex();
        // The store fires one Reset for a bulk (re)load, so this rebuilds once
        // per set switch rather than once per record — see BulkObservableCollection.
        _monsters.Messages.CollectionChanged += (_, _) => BuildIndex();

        _expSub          = router.Subscribe(KnownPatterns.UserGainExperience, OnExp);
        _combatStatusSub = router.Subscribe(KnownPatterns.CombatStatus,        OnCombatStatus);
    }

    // Bind to the per-session LineExtractor so we can match raw lines against the
    // death-pattern index. Idempotent — re-attaching to the same extractor is a
    // no-op. Called by MainWindowViewModel during connection setup.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    private void BuildIndex()
    {
        Dictionary<string, List<MonsterDeathIdentity>> fresh =
            new(StringComparer.Ordinal);

        foreach (MonsterMessageRecord rec in _monsters.Messages)
        {
            int? monsterNumber = ResolveMonsterNumber(rec);
            foreach (string pattern in rec.DeathLine)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                // Patterns with placeholders ({s}, etc.) aren't literal
                // matches; skip them in v1. (2 of 1100 monsters in
                // stock data carry such patterns — future work.)
                if (pattern.Contains('{')) continue;
                string key = pattern.Trim();
                if (!fresh.TryGetValue(key, out List<MonsterDeathIdentity>? list))
                {
                    list = new List<MonsterDeathIdentity>();
                    fresh[key] = list;
                }
                list.Add(new MonsterDeathIdentity(monsterNumber, rec.Name));
            }
        }

        _deathIndex = fresh;
        _log?.Debug(LogCategory, $"death-index built — {fresh.Count} patterns covering up to {_monsters.Messages.Count} records");
    }

    private static int? ResolveMonsterNumber(MonsterMessageRecord m)
    {
        if (m.Links is null) return null;
        foreach (GameDataLink link in m.Links)
        {
            if (string.Equals(link.Table, "Monsters", StringComparison.OrdinalIgnoreCase))
                return link.Number;
        }
        return null;
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        string trimmed = line.Text.TrimEnd();
        if (trimmed.Length == 0) return;
        if (!_deathIndex.TryGetValue(trimmed, out List<MonsterDeathIdentity>? candidates))
            return;

        _lastSpecificDeathAt = DateTimeOffset.Now;
        MonsterDeathEvent evt = new(
            Candidates:       candidates,
            ExperienceGained: null,
            At:               DateTimeOffset.Now,
            IsFallback:       false);
        _log?.Info(LogCategory,
            $"specific death matched — candidates={candidates.Count} first={candidates[0].Name}");
        MonsterDied?.Invoke(evt);
    }

    private void OnExp(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        if (!int.TryParse(m.Groups[0], out int exp)) return;
        _lastExpAt = DateTimeOffset.Now;
        _lastExpAmount = exp;
    }

    private void OnCombatStatus(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        if (!string.Equals(m.Groups[0], "Off", StringComparison.OrdinalIgnoreCase)) return;
        if (_lastExpAt is not { } expAt) return;

        DateTimeOffset now = DateTimeOffset.Now;
        if (now - expAt > ExpToCombatOffWindow) return;
        if (now - _lastSpecificDeathAt < SpecificDeathSuppressionWindow) return;

        MonsterDeathEvent evt = new(
            Candidates:       Array.Empty<MonsterDeathIdentity>(),
            ExperienceGained: _lastExpAmount,
            At:               now,
            IsFallback:       true);
        _log?.Info(LogCategory,
            $"fallback death (exp={_lastExpAmount} + *Combat Off* within {ExpToCombatOffWindow.TotalSeconds:F0}s)");
        MonsterDied?.Invoke(evt);

        // Consumed — clear so a second Combat Off doesn't re-fire on
        // the same exp.
        _lastExpAt = null;
        _lastExpAmount = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _expSub.Dispose();
        _combatStatusSub.Dispose();
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}
