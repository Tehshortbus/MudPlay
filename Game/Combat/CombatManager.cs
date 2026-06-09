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
    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _mobHitsSub;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _targetGoneSub;
    private readonly IDisposable _weaponNoEffectSub;
    private readonly IDisposable _fistsNoEffectSub;

    /// <summary>Minimum gap between safety-net <c>l</c> refreshes. Keeps
    /// a flurry of miss/hit lines from spamming the server.</summary>
    private static readonly TimeSpan RoomRefreshCooldown = TimeSpan.FromSeconds(3);

    private Action<byte[]>? _wireSender;
    private string? _currentTarget;
    private string? _lastAttackCommand;
    private DateTimeOffset _lastRoomRefreshAt = DateTimeOffset.MinValue;
    private bool _disposed;

    // ----- Weapon-swap shadow state -----------------------------------
    // No `inv`/`eq` parse — we shadow-track what we last sent to the
    // server. Cleared on the fists-no-effect recovery path (equipment
    // fell off; re-equip from scratch next attack).

    /// <summary>The weapon name last sent via the equip helper.
    /// <c>null</c> means we haven't sent an equip yet — first attack
    /// will trigger an equip to the configured normal/BS weapon.</summary>
    private string? _lastEquippedWeapon;

    /// <summary>True when we've swapped to the alternate weapon for
    /// the current room (a no-effect line fired against the normal
    /// weapon vs the current target's species). Cleared on
    /// room-cleared.</summary>
    private bool _usingAlternateWeapon;

    /// <summary>Canonical species names that produced a no-effect line
    /// against our normal weapon. Room-scoped — cleared on
    /// room-cleared so a fresh room re-tries the normal weapon. Keyed
    /// to <see cref="EngageableCandidate.ResolvedName"/> (base species,
    /// not the prefixed display name).</summary>
    private readonly HashSet<string> _normalWeaponFailedMonsters =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many no-effect lines a monster species needs to
    /// produce before we add it to <see cref="_normalWeaponFailedMonsters"/>.
    /// Mirrors <see cref="CombatSettings.NoEffectFailureThreshold"/>;
    /// cached here to track per-species count.</summary>
    private readonly Dictionary<string, int> _noEffectCounts =
        new(StringComparer.OrdinalIgnoreCase);

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
        _announceSub  = router.Subscribe(KnownPatterns.PartyAttackAnnounce, OnAttackAnnounce);
        _userHitsSub  = router.Subscribe(KnownPatterns.UserHits,  OnCombatLine);
        _mobHitsSub   = router.Subscribe(KnownPatterns.MobHits,   OnCombatLine);
        _mobMissesSub = router.Subscribe(KnownPatterns.MobMisses, OnCombatLine);
        _targetGoneSub = router.Subscribe(KnownPatterns.TargetNotHere, OnTargetNotHere);
        _weaponNoEffectSub = router.Subscribe(KnownPatterns.WeaponNoEffect, OnWeaponNoEffect);
        _fistsNoEffectSub  = router.Subscribe(KnownPatterns.FistsNoEffect,  OnFistsNoEffect);
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

    /// <summary>
    /// Called by the MonsterDeath subscriber when a death-line match
    /// resolves to a monster whose name might be ours. Clears
    /// <see cref="_currentTarget"/> when the dead monster shares a
    /// name with our current target (either the raw / unflavored
    /// case where two same-name mobs occupy the room, or the flavored
    /// case where the resolved species matches). Without this, the
    /// next <see cref="OnEntitiesObserved"/> sees another live entity
    /// with the same <c>RawName</c> still in the engageable list and
    /// short-circuits ("server still swinging") — so we'd never
    /// re-issue <c>attack</c> against the surviving instance, and
    /// CombatManager goes silent while the other rats keep biting.
    /// </summary>
    /// <param name="deadMonsterName">Base / display name of the dead
    /// monster, lifted from the matched death-line's
    /// <see cref="MonsterDeathIdentity.Name"/>.</param>
    public void NoteMonsterDied(string deadMonsterName)
    {
        if (string.IsNullOrEmpty(deadMonsterName)) return;
        if (_currentTarget is not { } current) return;

        // Direct RawName match — the unflavored case. Two "giant rat"
        // entries: `_currentTarget == "giant rat"` and the dead-line
        // gave us "giant rat". Whichever instance the server was
        // swinging at is the dead one; the other doesn't auto-engage.
        if (string.Equals(current, deadMonsterName, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Info(LogCategory,
                $"target died — clearing _currentTarget='{current}' (raw-name match)");
            _currentTarget = null;
            return;
        }

        // Resolved-name match — the flavored case. _currentTarget is
        // "angry kobold thief" (RawName); the dead-line resolves to
        // "kobold thief" (ResolvedName). The classifier's current
        // observation is the source of truth for the raw → resolved
        // mapping. Look up the entity matching our RawName and
        // compare its ResolvedName.
        if (_classifier.Current is { } obs)
        {
            for (int i = 0; i < obs.Entities.Count; i++)
            {
                RoomEntity e = obs.Entities[i];
                if (e.Kind != EntityKind.Monster) continue;
                if (!string.Equals(e.RawName, current, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(e.ResolvedName, deadMonsterName, StringComparison.OrdinalIgnoreCase)) continue;
                _log?.Info(LogCategory,
                    $"target died — clearing _currentTarget='{current}' (resolved-name match)");
                _currentTarget = null;
                return;
            }
        }
    }

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
            // Engageability is Relationship-based ONLY. Earlier we
            // also required MonsterMessageRecord.DeathLine non-empty
            // as a "killable" proxy, but 152 of 1100 monsters in the
            // stock data set ship with empty DeathLine (incomplete
            // data, not actually unkillable — acid slime, etc.). The
            // overlay seed marks the real friendlies explicitly; if
            // a monster is Enemy / unmarked, it's a target.

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
            OnRoomCleared(settings);
            return;
        }

        // Min/Max monsters gate — skip the room entirely when the
        // engageable count falls outside [Min, Max]. Default settings
        // (Min=0, Max=20) are effectively no-op. The user opts in by
        // tightening either bound. Inverted config (Min > Max) is
        // treated as "no gate" with a single log-once warning rather
        // than silently never engaging.
        int min = Math.Max(0, settings.MinMonstersInRoom);
        int max = settings.MaxMonstersInRoom > 0 ? settings.MaxMonstersInRoom : int.MaxValue;
        if (min > max)
        {
            // Misconfig — treat as off and warn once per room observation.
            _log?.Warn(LogCategory,
                $"MinMonsters={min} > MaxMonsters={max} — gate disabled for this observation");
        }
        else if (engageable.Count < min || engageable.Count > max)
        {
            _log?.Info(LogCategory,
                $"min/max gate skip — count={engageable.Count} window=[{min}..{max}]");
            // Clear target so we don't keep swinging at an old pick
            // that's now out-of-window after a kill.
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

        // Per-target dedup — if the picked species is in this room's
        // failed-vs-normal set, pre-emptively swap to the alternate
        // weapon + use AlternateAttackCommand. Saves a wasted swing.
        bool useAlt = _normalWeaponFailedMonsters.Contains(picked.ResolvedName);
        EquipForAttack(settings, useAlt);
        string verb = useAlt
            ? settings.AlternateAttackCommand
            : settings.NormalAttackCommand;
        SendAttack(verb, picked.RawName, picked.Priority);
        _currentTarget = picked.RawName;
    }

    // ----- Weapon-swap mechanics --------------------------------------

    /// <summary>
    /// Re-equip cascade at end of combat (room cleared). Priority:
    /// BS weapon (when configured) → normal weapon (when we'd
    /// swapped to alt). The fail-set + alt-mode flag clear here so
    /// the next room starts fresh.
    /// </summary>
    private void OnRoomCleared(CombatSettings settings)
    {
        _normalWeaponFailedMonsters.Clear();
        _noEffectCounts.Clear();

        // BS weapon takes precedence — re-equip after every fight so
        // the next room can backstab. If no BS configured but we
        // ended on the alternate, revert to normal.
        if (settings.DoBackstab && !string.IsNullOrWhiteSpace(settings.BackstabWeapon))
        {
            EquipWeapon(settings.BackstabWeapon, settings.BackstabOffHand);
            _usingAlternateWeapon = false;
        }
        else if (_usingAlternateWeapon)
        {
            EquipWeapon(settings.NormalWeapon, settings.NormalOffHand);
            _usingAlternateWeapon = false;
        }
    }

    /// <summary>
    /// Decide which weapon should be on for the next attack and emit
    /// the equip line if it's a change. Called from
    /// <see cref="OnEntitiesObserved"/> just before <see cref="SendAttack"/>.
    /// </summary>
    private void EquipForAttack(CombatSettings settings, bool wantAlternate)
    {
        string? weapon;
        string? offHand;
        if (wantAlternate)
        {
            weapon = settings.AlternateWeapon;
            offHand = settings.AlternateOffHand;
            _usingAlternateWeapon = true;
        }
        else
        {
            weapon = settings.NormalWeapon;
            offHand = settings.NormalOffHand;
            _usingAlternateWeapon = false;
        }
        EquipWeapon(weapon, offHand);
    }

    /// <summary>
    /// Send equip commands for the given weapon + off-hand. Idempotent
    /// vs the shadow state: re-equipping the same weapon no-ops. The
    /// off-hand is unconditional on every call (matches MudProxy —
    /// we don't track off-hand state because cursed off-hands are
    /// rare and the equip is cheap).
    /// </summary>
    private void EquipWeapon(string? weapon, string? offHand)
    {
        if (string.IsNullOrWhiteSpace(weapon)) return;
        if (string.Equals(weapon, _lastEquippedWeapon, StringComparison.OrdinalIgnoreCase))
            return;

        _log?.Info(LogCategory, $"equip weapon={weapon} offhand={offHand ?? "<none>"}");
        Send($"eq {weapon.Trim()}");
        if (!string.IsNullOrWhiteSpace(offHand))
            Send($"eq {offHand.Trim()}");
        _lastEquippedWeapon = weapon;
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    // ----- No-effect handlers -----------------------------------------

    /// <summary>
    /// Server says our weapon has no effect against the current
    /// target. Count the species; once the count crosses
    /// <see cref="CombatSettings.NoEffectFailureThreshold"/>, add it
    /// to the room-scoped fail-set so the next pick swaps preemptively.
    /// If we're already on the alternate weapon when this fires, the
    /// monster is genuinely unhittable for us — log + leave for the
    /// user / future unhittable-set work.
    /// </summary>
    private void OnWeaponNoEffect(MatchResult _)
    {
        if (!_isEnabled()) return;
        if (_currentTarget is null) return;

        // Canonicalize the target to base species — strip any flavor
        // prefix. The classifier's ResolvedName is the canonical form;
        // _currentTarget holds RawName. We resolve by scanning the
        // current observation.
        string species = ResolveSpeciesFromCurrentTarget();
        CombatSettings settings = _readSettings();

        if (_usingAlternateWeapon)
        {
            _log?.Warn(LogCategory,
                $"weapon-no-effect on ALT against {species} — monster unhittable for us");
            return;
        }

        int threshold = Math.Max(1, settings.NoEffectFailureThreshold);
        _noEffectCounts.TryGetValue(species, out int count);
        count++;
        _noEffectCounts[species] = count;
        if (count < threshold)
        {
            _log?.Info(LogCategory,
                $"weapon-no-effect species={species} count={count}/{threshold}");
            return;
        }

        if (_normalWeaponFailedMonsters.Add(species))
            _log?.Info(LogCategory, $"adding {species} to normal-weapon fail-set");

        // Swap NOW and re-send the attack so we don't waste a round.
        EquipForAttack(settings, wantAlternate: true);
        if (_currentTarget is { } tgt)
            SendAttack(settings.AlternateAttackCommand, tgt, priority: null);
    }

    /// <summary>
    /// "Your fists have no effect" — our weapon fell off (server-side
    /// drop / removal we didn't track). Clear the shadow state so the
    /// next attack re-equips from scratch.
    /// </summary>
    private void OnFistsNoEffect(MatchResult _)
    {
        _log?.Warn(LogCategory, "fists-no-effect — clearing equipped-weapon shadow state");
        _lastEquippedWeapon = null;
        _usingAlternateWeapon = false;

        // Force a re-equip on the next attack by triggering a fresh
        // pick. The simplest path: drop _currentTarget so
        // OnEntitiesObserved re-decides + re-equips on the next
        // observation. (The classifier re-fires on every full room
        // display + arrival.)
        _currentTarget = null;
    }

    /// <summary>Map current target's RawName back to its base species
    /// via the live observation. Falls back to <c>_currentTarget</c>
    /// when no match is found (orphaned target).</summary>
    private string ResolveSpeciesFromCurrentTarget()
    {
        if (_currentTarget is not { } tgt) return string.Empty;
        if (_classifier.Current is { } obs)
        {
            foreach (RoomEntity e in obs.Entities)
            {
                if (e.Kind != EntityKind.Monster) continue;
                if (string.Equals(e.RawName, tgt, StringComparison.OrdinalIgnoreCase))
                    return e.ResolvedName;
            }
        }
        return tgt;
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

    /// <summary>
    /// Safety net: a combat line (user hit / mob hit / mob miss) means
    /// something is swinging at us — but if the classifier shows no
    /// engageable monster and we have no current target, our view of
    /// the room is stale (entity dropped after a death, arrival line
    /// lost, prefix not resolved against the overlay, etc.). Send a
    /// bare CR (<c>^M</c>) so the server re-emits a short room view;
    /// the classifier repopulates, OnEntitiesObserved picks a target,
    /// and the next round we swing back. Debounced so a burst of
    /// combat lines doesn't flood the wire.
    /// </summary>
    /// <remarks>
    /// Bare CR is preferred over <c>l</c> because the server's CR
    /// response is the compact "where am I" payload — the Also Here
    /// list plus prompt without the room description, exits block,
    /// and ground-item enumeration that <c>l</c> dumps.
    /// </remarks>
    private void OnCombatLine(MatchResult _)
    {
        if (!_isEnabled()) return;
        if (_currentTarget is not null) return;
        if (_wireSender is null) return;

        if (_classifier.Current is { } cur && HasEngageable(cur)) return;

        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastRoomRefreshAt < RoomRefreshCooldown) return;
        _lastRoomRefreshAt = now;

        _log?.Info(LogCategory,
            "combat-line while room appears empty — sending CR for short re-display");
        _wireSender(Encoding.Latin1.GetBytes("\r"));
    }

    /// <summary>
    /// "You don't see &lt;X&gt; here!" — server can't find the target
    /// we just attacked. Different from MonsterDeathWatcher's path:
    /// catches cases where the death line was missed, the mob fled,
    /// or a partymate killed it between our send and the server's
    /// resolve. Drop the current target and refresh the room so the
    /// next observation picks a fresh target.
    /// </summary>
    private void OnTargetNotHere(MatchResult _)
    {
        if (!_isEnabled()) return;
        if (_wireSender is null) return;
        if (_currentTarget is null) return;

        _log?.Info(LogCategory,
            $"target-not-here — dropping target={_currentTarget} + refreshing room");
        _currentTarget = null;

        // Force a refresh (debounce shared with OnCombatLine so a
        // simultaneous miss-line + target-not-here doesn't double-send).
        // Bare CR — same rationale as OnCombatLine.
        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastRoomRefreshAt < RoomRefreshCooldown) return;
        _lastRoomRefreshAt = now;
        _wireSender(Encoding.Latin1.GetBytes("\r"));
    }

    private bool HasEngageable(RoomEntitiesObservation obs)
    {
        for (int i = 0; i < obs.Entities.Count; i++)
        {
            RoomEntity e = obs.Entities[i];
            if (e.Kind != EntityKind.Monster) continue;
            if (e.MonsterNumber is not int n) return true; // unknown → assume engageable
            MonsterOverlay overlay = ResolveOverlay(n);
            if ((overlay.Relationship ?? MonsterRelationship.Enemy) == MonsterRelationship.Enemy)
                return true;
        }
        return false;
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
        _userHitsSub.Dispose();
        _mobHitsSub.Dispose();
        _mobMissesSub.Dispose();
        _targetGoneSub.Dispose();
        _weaponNoEffectSub.Dispose();
        _fistsNoEffectSub.Dispose();
    }

    private readonly record struct EngageableCandidate(
        string RawName,
        string ResolvedName,
        int MonsterNumber,
        MonsterAttackPriority Priority,
        int AppearanceIndex);
}
