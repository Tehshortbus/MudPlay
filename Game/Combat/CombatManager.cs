using System.Text;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

/// <summary>
/// Phase 9 PR 9.A — auto-attack engine. Subscribes to
/// <see cref="RoomEntityClassifier.EntitiesObserved"/> and (for
/// re-fire pacing) to <see cref="KnownPatterns.PartyAttackAnnounce"/>.
/// Picks a target per <see cref="MonsterOverlay.Priority"/> +
/// <see cref="CombatSettings.TargetOrder"/>, filters out anything not
/// flagged <see cref="MonsterRelationship.Enemy"/>, and sends the
/// configured attack command. Server auto-repeats swings each
/// 5-second round; CombatManager re-picks only when the room
/// re-displays without the current target.
/// </summary>
/// <remarks>
/// <para>
/// Target selection — single source of truth across the engine:
/// </para>
/// <list type="number">
/// <item>Classifier filters Also-Here to <see cref="EntityKind.Monster"/>.</item>
/// <item>Each monster's <see cref="MonsterOverlay"/> is resolved via
/// <see cref="MonsterOverlaySeedStore"/> (Defaults tier) merged with
/// <see cref="SettingsResolver.ResolveGameData{T}"/> (Global / BBS /
/// Char overrides).</item>
/// <item>Engageable = <see cref="MonsterRelationship.Enemy"/> AND
/// <see cref="MonsterMessageRecord.DeathLine"/> non-empty (i.e. the
/// monster has a known death-line pattern so it's killable).</item>
/// <item>Engageable list is sorted by
/// <see cref="MonsterAttackPriority"/> (First=0 highest, Last=4
/// lowest), tiebreak by appearance order in the Also-Here line.</item>
/// <item><see cref="CombatSettings.TargetOrder"/> picks
/// <c>Normal</c> = first sorted (highest prio) or <c>Reverse</c> =
/// last sorted (lowest prio).</item>
/// </list>
/// <para>
/// AttackTiming re-fire (CombatSettings):
/// </para>
/// <list type="bullet">
/// <item><see cref="AttackTiming.Default"/> — never re-fire.</item>
/// <item><see cref="AttackTiming.AttackLastParty"/> — re-fire on a
/// party member's "moves to attack" announce. Excludes our own
/// character (matched against <see cref="ProfileService.CurrentProfileName"/>)
/// and non-party players.</item>
/// <item><see cref="AttackTiming.AttackLastRoom"/> — re-fire on
/// anyone's announce except our own.</item>
/// <item><see cref="AttackTiming.AttackAfter"/> — re-fire only on
/// the named <see cref="CombatSettings.AttackAfterPlayerName"/>'s
/// announce.</item>
/// </list>
/// <para>
/// All re-fire branches require a non-null
/// <see cref="CurrentTarget"/> — we can only re-issue an attack
/// against a target we already chose.
/// </para>
/// </remarks>
public sealed class CombatManager : IDisposable
{
    /// <summary>LogService category — appears as <c>[Combat]</c> rows
    /// per swing decision + target swap + re-fire.</summary>
    public const string LogCategory = "Combat";

    private readonly RoomEntityClassifier _classifier;
    private readonly MonsterMessageStore _monsters;
    private readonly Func<int, MonsterOverlay> _resolveOverlay;
    private readonly PartyState _party;
    private readonly Func<CombatSettings> _readSettings;
    private readonly Func<bool> _isEnabled;
    private readonly Func<string?> _readOwnGivenName;
    private readonly LogService? _log;

    private readonly IDisposable _announceSub;

    private Action<byte[]>? _wireSender;
    private string? _currentTarget;
    private string? _lastAttackCommand;
    private bool _disposed;

    public CombatManager(
        MessageRouter router,
        RoomEntityClassifier classifier,
        MonsterMessageStore monsters,
        Func<int, MonsterOverlay> resolveOverlay,
        PartyState party,
        Func<CombatSettings> readSettings,
        Func<bool> isEnabled,
        Func<string?> readOwnGivenName,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(monsters);
        ArgumentNullException.ThrowIfNull(resolveOverlay);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(readOwnGivenName);
        _classifier   = classifier;
        _monsters     = monsters;
        _resolveOverlay = resolveOverlay;
        _party        = party;
        _readSettings = readSettings;
        _isEnabled    = isEnabled;
        _readOwnGivenName = readOwnGivenName;
        _log = log;

        _classifier.EntitiesObserved += OnEntitiesObserved;
        _announceSub = router.Subscribe(KnownPatterns.PartyAttackAnnounce, OnAttackAnnounce);
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
    /// or <c>null</c> when no fight is in flight.</summary>
    public string? CurrentTarget => _currentTarget;

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        if (!_isEnabled())
        {
            _currentTarget = null;
            return;
        }
        CombatSettings settings = _readSettings();

        // Score every Monster entity once. We need BOTH names:
        //   RawName       — full prefixed form ("angry kobold thief"),
        //                   used on the wire so the server engages the
        //                   specific instance, not whichever
        //                   "<adj> kobold thief" it happens to pick.
        //   ResolvedName  — base form ("kobold thief"), used for the
        //                   in-room counting / re-pick logic when the
        //                   server auto-continues against the same
        //                   base across multiple identical instances.
        List<EngageableCandidate> engageable = new();
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (e.MonsterNumber is not int n) continue;

            MonsterOverlay overlay = ResolveOverlay(n);
            if ((overlay.Relationship ?? MonsterRelationship.Enemy) != MonsterRelationship.Enemy)
                continue;
            if (!HasDeathLine(n)) continue;

            engageable.Add(new EngageableCandidate(
                RawName:         e.RawName,
                ResolvedName:    e.ResolvedName,
                MonsterNumber:   n,
                Priority:        overlay.Priority ?? MonsterAttackPriority.Normal,
                AppearanceIndex: i));
        }

        if (engageable.Count == 0)
        {
            if (_currentTarget is not null)
                _log?.Info(LogCategory, $"room cleared — was=target={_currentTarget}");
            _currentTarget = null;
            return;
        }

        // Sort by Priority asc (First=0 highest, Last=4 lowest), then
        // by appearance order for stable tiebreak.
        engageable.Sort((a, b) =>
        {
            int p = a.Priority.CompareTo(b.Priority);
            return p != 0 ? p : a.AppearanceIndex.CompareTo(b.AppearanceIndex);
        });

        // TargetOrder.Normal → highest-priority first (sorted[0]);
        // Reverse → lowest-priority first (sorted[^1]).
        EngageableCandidate picked = settings.TargetOrder == TargetOrder.Reverse
            ? engageable[^1]
            : engageable[0];

        // Server auto-attacks the specific named target each round;
        // re-sending the same command mid-fight would burn a swing.
        // If the exact RawName we last sent is still in the engageable
        // list, keep going — the server is still swinging at it.
        if (_currentTarget is { } current &&
            engageable.Any(e => string.Equals(e.RawName, current,
                                              StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SendAttack(settings.NormalAttackCommand, picked.RawName, picked.Priority);
        _currentTarget = picked.RawName;
    }

    /// <summary>
    /// Re-fire dispatch for AttackTiming. Matches whatever announces
    /// against the configured timing mode + our own name + party
    /// membership.
    /// </summary>
    private void OnAttackAnnounce(MatchResult match)
    {
        // (?<player>\w+) at positional 0, (?<target>.+?) at 1.
        if (match.Groups.Count < 2) return;
        string announcer = match.Groups[0];
        string announcedTarget = match.Groups[1].Trim();
        if (announcer.Length == 0 || announcedTarget.Length == 0) return;

        // Never re-fire on our own announce — we already swung.
        string? ownName = _readOwnGivenName();
        if (ownName is { Length: > 0 } &&
            string.Equals(announcer, ownName, StringComparison.OrdinalIgnoreCase))
            return;

        if (!_isEnabled()) return;
        CombatSettings settings = _readSettings();

        // Decide whether to fire AND whether to switch our target. The
        // "mirror" modes follow the announcer's choice (party
        // coordination + named-player follow); "stay" modes keep
        // attacking our own target and just re-issue the command to
        // stay last in initiative.
        (bool fire, bool mirror) = settings.AttackTiming switch
        {
            AttackTiming.Default          => (false, false),
            AttackTiming.AttackLastParty  => (IsPartyMember(announcer), true),
            AttackTiming.AttackLastRoom   => (true,                     false),
            AttackTiming.AttackAfter      => (string.Equals(announcer,
                                                  settings.AttackAfterPlayerName ?? string.Empty,
                                                  StringComparison.OrdinalIgnoreCase),  true),
            _                              => (false, false),
        };

        if (!fire) return;

        string target;
        if (mirror)
        {
            // Switch to the announcer's specific instance. Server
            // resolves `attack large kobold thief` against the right
            // entity even when "angry kobold thief" is also present.
            target = announcedTarget;
            _currentTarget = announcedTarget;
        }
        else if (_currentTarget is { } cur)
        {
            target = cur;
        }
        else
        {
            return;     // re-fire mode without an existing target → nothing to do
        }

        SendAttack(settings.NormalAttackCommand, target, refire: true,
                   refireReason: $"{settings.AttackTiming} announcer={announcer}");
    }

    private bool IsPartyMember(string name)
    {
        foreach (PartyMember m in _party.Members)
        {
            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private MonsterOverlay ResolveOverlay(int monsterNumber)
    {
        try { return _resolveOverlay(monsterNumber) ?? new MonsterOverlay(); }
        catch
        {
            // Resolver failure (no active set, malformed override file)
            // → fall back to defaults so the engine isn't wedged.
            return new MonsterOverlay();
        }
    }

    private bool HasDeathLine(int monsterNumber)
    {
        MonsterMessageRecord? rec = _monsters.FindByMonsterNumber(monsterNumber);
        if (rec is null) return true;            // unknown to message store — defensive engage
        return rec.DeathLine.Count > 0;
    }

    private void SendAttack(string command, string target, MonsterAttackPriority? priority = null)
    {
        string verb = string.IsNullOrWhiteSpace(command) ? "a" : command.Trim();
        string line = $"{verb} {target}";
        if (priority is { } prio)
            _log?.Info(LogCategory, $"attack target={target} cmd={verb} prio={prio}");
        else
            _log?.Info(LogCategory, $"attack target={target} cmd={verb}");
        _lastAttackCommand = line;
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
    }

    private void SendAttack(string command, string target, bool refire, string refireReason)
    {
        string verb = string.IsNullOrWhiteSpace(command) ? "a" : command.Trim();
        string line = $"{verb} {target}";
        _log?.Info(LogCategory,
            $"re-fire target={target} cmd={verb} timing={refireReason}");
        _lastAttackCommand = line;
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        _announceSub.Dispose();
    }

    private readonly record struct EngageableCandidate(
        string RawName,
        string ResolvedName,
        int MonsterNumber,
        MonsterAttackPriority Priority,
        int AppearanceIndex);
}
