using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Combat;

/// <summary>
/// Phase 9 PR 9.A — auto-attack engine. Subscribes to
/// <see cref="RoomEntityClassifier.EntitiesObserved"/> and, while
/// <see cref="CombatSettings.MasterAutoAttackEnabled"/> is on, picks
/// a target per <see cref="CombatSettings.TargetOrder"/> and sends
/// the configured attack command. The server auto-repeats swings
/// each 5-second round until the target dies; CombatManager re-picks
/// only when the room re-displays without the current target.
/// </summary>
/// <remarks>
/// <para>
/// First-cut scope: target selection + initial swing send + room-clear
/// detection via classifier re-emit. Refinement PRs add weapon-swap
/// matrix, attack-timing re-fire (Default / AttackLastParty /
/// AttackLastRoom / AttackAfter), polite-mode behaviours, multi-attack
/// room spells, and per-monster failure tracking. Each of those
/// builds on the same classifier-driven target loop.
/// </para>
/// <para>
/// Target name on the wire is the monster's <b>base name</b>
/// (<see cref="RoomEntity.ResolvedName"/>), not the prefixed display.
/// MajorMUD resolves <c>attack giant rat</c> against any
/// <c>nasty giant rat</c> / <c>fat giant rat</c> in the room; sending
/// the prefixed form risks no-match on realms that filter strictly.
/// </para>
/// <para>
/// "Engageable" is the same DeathLine-non-empty filter
/// <see cref="CombatStateTracker"/> uses for the
/// <see cref="MovementCoordinator.CombatGate"/>. Shopkeepers and
/// quest-givers carry empty DeathLine lists and are skipped.
/// </para>
/// </remarks>
public sealed class CombatManager : IDisposable
{
    /// <summary>LogService category — appears as <c>[Combat]</c> rows
    /// per swing decision + target swap.</summary>
    public const string LogCategory = "Combat";

    private readonly RoomEntityClassifier _classifier;
    private readonly MonsterMessageStore _monsters;
    private readonly Func<CombatSettings> _readSettings;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private string? _currentTarget;
    private bool _disposed;

    public CombatManager(
        RoomEntityClassifier classifier,
        MonsterMessageStore monsters,
        Func<CombatSettings> readSettings,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(monsters);
        ArgumentNullException.ThrowIfNull(readSettings);
        _classifier = classifier;
        _monsters   = monsters;
        _readSettings = readSettings;
        _log = log;
        _classifier.EntitiesObserved += OnEntitiesObserved;
    }

    /// <summary>Bind the wire sender — typically the
    /// <c>TelnetClient.SendAsync</c> wrapper that
    /// <see cref="MainWindowViewModel"/> exposes. Until set,
    /// CombatManager silently no-ops on its outbound side (state
    /// transitions still log).</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>The monster name we last sent <c>attack</c> against,
    /// or <c>null</c> when no fight is in flight. Exposed for the
    /// LogPane + tests.</summary>
    public string? CurrentTarget => _currentTarget;

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        CombatSettings settings = _readSettings();
        if (!settings.MasterAutoAttackEnabled)
        {
            // Auto-attack off → engine is dormant; drop any stale
            // target reference so toggling on later starts clean.
            _currentTarget = null;
            return;
        }

        List<RoomEntity> engageable = new();
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            if (!IsEngageable(e)) continue;
            engageable.Add(e);
        }

        if (engageable.Count == 0)
        {
            if (_currentTarget is not null)
                _log?.Info(LogCategory, $"room cleared — was=target={_currentTarget}");
            _currentTarget = null;
            return;
        }

        // Server auto-attacks the named target each round; re-sending
        // the same command mid-fight would burn a swing on the prompt
        // echo. So if the existing target is still in the room (even
        // as a different prefix-variant instance), keep going.
        if (_currentTarget is { } current &&
            engageable.Any(e => string.Equals(e.ResolvedName, current,
                                              StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Pick next target per TargetOrder. Until per-monster
        // AttackPriority wires through game data, "priority" is the
        // appearance order in the Also-Here line.
        RoomEntity picked = settings.TargetOrder == TargetOrder.Reverse
            ? engageable[^1]
            : engageable[0];

        SendAttack(settings.NormalAttackCommand, picked.ResolvedName);
        _currentTarget = picked.ResolvedName;
    }

    private bool IsEngageable(RoomEntity e)
    {
        if (e.MonsterNumber is not int n) return true;
        MonsterMessageRecord? rec = _monsters.FindByMonsterNumber(n);
        if (rec is null) return true;
        return rec.DeathLine.Count > 0;
    }

    private void SendAttack(string command, string target)
    {
        string verb = string.IsNullOrWhiteSpace(command) ? "a" : command.Trim();
        string line = $"{verb} {target}";
        _log?.Info(LogCategory, $"attack target={target} cmd={verb}");
        if (_wireSender is null) return;
        byte[] bytes = Encoding.Latin1.GetBytes(line + "\r");
        _wireSender(bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _classifier.EntitiesObserved -= OnEntitiesObserved;
    }
}
