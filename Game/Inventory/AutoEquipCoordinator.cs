using System;
using System.ComponentModel;
using System.Linq;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game.Inventory;

/// <summary>
/// Watches the live character posture / combat state and auto-swaps the
/// trigger-purposed gear sets at the moments the Equipment Manager arms. It is a
/// pure <i>subscriber</i> — it reads <see cref="PlayerState"/> (position / combat)
/// and the <see cref="Game.Health.HealthManager"/>'s recovery gates, and never
/// writes any observed field, so it sits outside the single-writer ownership
/// model. When a moment fires it resolves the matching enabled
/// <see cref="EquipmentSet"/> through the live <see cref="EquipmentSettings"/> and
/// hands its id to <see cref="EquipmentManager.ApplyBySetId"/>.
/// </summary>
/// <remarks>
/// Signal mapping:
/// <list type="bullet">
/// <item><b>Pre-rest HP / Mana</b> — position goes to <see cref="PlayerPosition.Resting"/>;
/// the held recovery gate (HP vs MA) disambiguates which set is wanted.
/// <see cref="PlayerPosition.Meditating"/> is a mana recovery, so it maps to
/// Pre-rest Mana.</item>
/// <item><b>Default</b> — <see cref="PlayerState.InCombat"/> goes true (re-entering
/// normal combat), or the character stands up out of a rest (back to baseline).</item>
/// </list>
/// The <b>Backstab</b> set isn't auto-fired here yet — it needs the combat
/// engine's "room clear → sneak → surprise round" sequencing, which lands in a
/// later PR; until then Backstab is editable and manually / remotely appliable.
/// The decision halves (<see cref="ClassifyRest"/>, <see cref="ResolveTarget"/>)
/// are pure and unit-tested; the subscription plumbing is smoke-tested live.
/// <para>
/// A fired moment is <i>held</i> until the worn loadout is known
/// (<see cref="Game.Inventory.InventoryManager.IsLoaded"/> — at least one full
/// <c>i</c> dump parsed). The apply engine diffs the desired set against the live
/// worn set, and an empty worn set reads as "nothing worn" rather than "unknown",
/// so firing before the first dump would emit a <c>wear</c> for every set item —
/// including one already worn (the game answers "You do not have X left
/// unequipped."). Manual applies (the Workshop button, <c>@equip-&lt;set&gt;</c>)
/// carry explicit intent and aren't gated here.
/// </para>
/// </remarks>
public sealed class AutoEquipCoordinator : IDisposable
{
    private readonly PlayerState _player;
    private readonly Func<EquipmentSettings> _readEquipment;
    private readonly Func<bool> _hpGateAsserted;
    private readonly Func<bool> _maGateAsserted;
    private readonly Func<string, EquipResult> _applyBySetId;
    private readonly Func<bool> _wornLoadoutKnown;
    private readonly LogService? _log;

    private PlayerPosition _lastPosition;
    private bool _lastInCombat;

    public AutoEquipCoordinator(
        PlayerState player,
        Func<EquipmentSettings> readEquipment,
        Func<bool> hpGateAsserted,
        Func<bool> maGateAsserted,
        Func<string, EquipResult> applyBySetId,
        Func<bool> wornLoadoutKnown,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(readEquipment);
        ArgumentNullException.ThrowIfNull(hpGateAsserted);
        ArgumentNullException.ThrowIfNull(maGateAsserted);
        ArgumentNullException.ThrowIfNull(applyBySetId);
        ArgumentNullException.ThrowIfNull(wornLoadoutKnown);
        _player = player;
        _readEquipment = readEquipment;
        _hpGateAsserted = hpGateAsserted;
        _maGateAsserted = maGateAsserted;
        _applyBySetId = applyBySetId;
        _wornLoadoutKnown = wornLoadoutKnown;
        _log = log;

        _lastPosition = player.Position;
        _lastInCombat = player.InCombat;
        _player.PropertyChanged += OnPlayerChanged;
    }

    // ----- pure decision logic (unit-tested) ------------------------------

    /// <summary>
    /// The pre-rest trigger a transition <i>into</i> <paramref name="to"/> implies,
    /// or null when the new posture isn't a rest / meditate. A held HP gate marks an
    /// HP rest; otherwise a held MA gate marks a mana rest; a plain rest with neither
    /// gate still known defaults to HP (the common case). Meditation is always a mana
    /// recovery.
    /// </summary>
    internal static EquipTriggerType? ClassifyRest(PlayerPosition to, bool hpGate, bool maGate) => to switch
    {
        PlayerPosition.Meditating => EquipTriggerType.PreRestMana,
        PlayerPosition.Resting when maGate && !hpGate => EquipTriggerType.PreRestMana,
        PlayerPosition.Resting => EquipTriggerType.PreRestHp,
        _ => null,
    };

    /// <summary>
    /// The set id a <paramref name="type"/> moment should apply given the live
    /// config, or null when it shouldn't fire — no set exists for the type, the set
    /// is disabled, or it has no stable id.
    /// </summary>
    internal static string? ResolveTarget(EquipmentSettings cfg, EquipTriggerType type)
    {
        EquipmentSet? set = cfg.Sets.FirstOrDefault(s => s.Trigger == type);
        if (set is not { Enabled: true }) return null;
        return string.IsNullOrEmpty(set.Id) ? null : set.Id;
    }

    // ----- signal subscription (UI plumbing) ------------------------------

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerState.Position):
                OnPositionChanged(_lastPosition, _player.Position);
                _lastPosition = _player.Position;
                break;
            case nameof(PlayerState.InCombat):
                OnInCombatChanged(_lastInCombat, _player.InCombat);
                _lastInCombat = _player.InCombat;
                break;
        }
    }

    private void OnPositionChanged(PlayerPosition from, PlayerPosition to)
    {
        if (from == to) return;
        if (ClassifyRest(to, _hpGateAsserted(), _maGateAsserted()) is { } restType)
            Fire(restType);
        else if (to == PlayerPosition.Standing && IsRestPosture(from))
            Fire(EquipTriggerType.Default);   // stood up from a rest — back to baseline
    }

    private void OnInCombatChanged(bool from, bool to)
    {
        if (from != to && to) Fire(EquipTriggerType.Default);
    }

    private static bool IsRestPosture(PlayerPosition p) =>
        p is PlayerPosition.Resting or PlayerPosition.Meditating;

    private void Fire(EquipTriggerType type)
    {
        if (ResolveTarget(_readEquipment(), type) is not { } setId) return;
        // Hold the fire until a full 'i' has established the worn set. Diffing a
        // set against an empty (never-parsed) loadout treats every item as unworn
        // and emits redundant `wear`s — e.g. re-wielding the weapon already held.
        if (!_wornLoadoutKnown())
        {
            _log?.Debug(EquipmentManager.LogCategory,
                $"auto-equip '{type}' held: worn loadout unknown (no inventory dump yet)");
            return;
        }
        if (_applyBySetId(setId) == EquipResult.Applied)
            _log?.Info(EquipmentManager.LogCategory, $"auto-equip '{type}' applied its set");
    }

    public void Dispose() => _player.PropertyChanged -= OnPlayerChanged;
}
