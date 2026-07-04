using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Combat;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Live player state for the connected character — HP / mana / position /
// mana type. Updated by PromptParser on every status-line match; consumed
// by the status bar, the Workshop STATS section, and any automation engine
// that gates on HP / MP / position.
//
// Ownership model: every observable-backed field declares its sole writer.
// PromptParser is the only class allowed to mutate these fields (the IL-scan
// test enforces this at build time). Consumers — including the status bar
// view-model — bind to these properties; they never call setters.
//
// MaxHp / MaxMa are populated from the largest observed running value, since
// the baseline MajorMUD statline doesn't emit a denominator. Settings →
// Statline lets users build a richer wildcard string that includes %H / %M;
// the parser will start consuming those captures from there.
public sealed partial class PlayerState : ObservableObject
{
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private int _hp;
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private int _maxHp;
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private int _ma;
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private int _maxMa;
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private ManaType _manaType;
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private PlayerPosition _position;

    // True once PromptParser has observed at least one status line. UI
    // surfaces show — placeholders when this is false so the first-launch /
    // pre-connect state doesn't render as HP 0/0.
    [ObservableProperty] [field: Owner(typeof(PromptParser))] private bool _hasPromptData;

    // Latest observed encumbrance bracket from the enc line. Stays Unknown
    // until the player runs enc (or it gets reported during an inventory).
    // Drives the Auto-Lair scheduler's per-hop travel-cost lookup and the
    // hop-timing calibrator's bucket tag.
    [ObservableProperty] [field: Owner(typeof(EncumbranceParser))] private EncumbranceLevel _encumbrance;

    // True while the live combat state is "engaged" — set when the server
    // reports *Combat Engaged* OR a damage line fires; cleared on
    // *Combat Off*. Cross-cuts every engine that gates on "are we currently
    // fighting": CombatManager only swings while true, HealthManager won't
    // initiate rest while true, CashManager defers auto-pickup while true.
    // RoundDamageTracker drives the damage-line-driven decay.
    [ObservableProperty] [field: Owner(typeof(CombatStateTracker))] private bool _inCombat;

    // True while we're confirmed sneaking — the server emitted Sneaking...
    // on the last room entry. Cleared on "You make a sound as you enter the
    // room!" OR when a room-entry doesn't carry the sneak confirmation
    // (silent loss). Drives CombatManager's pre-attack-buff suppression so
    // we don't burn the backstab window casting buffs.
    [ObservableProperty] [field: Owner(typeof(Game.Stealth.StealthManager))] private bool _isSneaking;

    // True while we're confirmed hidden — server confirmed the hide command.
    // Cleared on any action that breaks hide (move, attack, cast). Same role
    // as IsSneaking: read by CombatManager's backstab-window logic.
    [ObservableProperty] [field: Owner(typeof(Game.Stealth.StealthManager))] private bool _isHidden;
}
