using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// View-model for the Game Data Browser → Monsters tab's per-record edit dialog.
// Surfaces the editable overlay fields (Use-tier, Name, Relationship, Priority, override
// spell slots, DontBackstab, KillOnSight).
//
// This dialog no longer edits any per-monster combat-message data: hit / miss / dodge /
// death are recognized generically (Game.Combat.CombatLineClassifier + MonsterDeathWatcher),
// and the flavor-adjective vocabulary is a shared per-set list edited in the browser's
// Flavor Prefixes section (Services.FlavorPrefixStore), not per-monster.
public sealed partial class MonsterEditDialogViewModel : ObservableObject, IDialogViewModel<MonsterEditResult>
{
    public event Action<MonsterEditResult?>? CloseRequested;

    public string WccNoStr { get; }
    public int    MonsterNumber { get; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private SettingsTier _useTier = SettingsTier.Character;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKillOnSight))]
    private MonsterRelationship _relationship = MonsterRelationship.Enemy;
    [ObservableProperty] private MonsterAttackPriority _priority = MonsterAttackPriority.Normal;

    [ObservableProperty] private string _preAttackSpellId = string.Empty;
    // Per-room cast cap — null (blank) = unlimited, matching CombatSpellSlot.MaxCastsPerRoom
    // and the Settings → Combat NumericUpDown.
    [ObservableProperty] private int? _preAttackCount;
    // Minimum mana to cast the override pre-attack — 0 = no floor. Interpreted per the
    // char's Combat-tab mana mode; SpellManaMax + PreAttackMinManaConverted mirror the
    // Settings → Combat spell-slot control. Mirrors CombatSpellSlot.MinManaPerCast.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreAttackMinManaConverted))]
    private int _preAttackMinMana;
    // "Override Attack" holds a Spell.Number, OR a spell cast-code that resolves
    // to one (both land on the mana-gated spell rung; Max is an optional per-room
    // cap, blank = unlimited), OR a raw verb like "attack"/"bash" that doesn't
    // resolve to any spell (sent as-is, no gating). See ParseAttackOverride.
    [ObservableProperty] private string _attackOverride = string.Empty;
    // Per-room cast cap — null (blank) = unlimited (matches Combat's NumericUpDown).
    [ObservableProperty] private int? _attackCount;
    // Minimum mana to cast the override attack (same interpretation as pre-attack);
    // ignored when the override resolves to a raw command rather than a spell.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttackMinManaConverted))]
    private int _attackMinMana;

    // Min-mana control parity with Settings → Combat: SpellManaMax caps the NumericUpDown
    // (100 in % mode, the absolute ceiling in Value mode) and the Converted strings show
    // the %↔value equivalent against the character's live max mana (snapshot at open).
    private readonly bool _manaModePercentage;
    private readonly int _liveMaxMa;

    public decimal SpellManaMax => _manaModePercentage ? 100 : 100_000;
    public string PreAttackMinManaConverted => FormatMana(PreAttackMinMana);
    public string AttackMinManaConverted    => FormatMana(AttackMinMana);

    // Percentage mode shows the absolute mana equivalent ("54/66"); Value mode shows the
    // percentage ("82%"). Empty until a prompt has given us a live max mana. Same rule as
    // CombatSectionViewModel.FormatMana.
    private string FormatMana(int value)
    {
        if (_liveMaxMa <= 0) return string.Empty;
        return _manaModePercentage
            ? $"{(int)System.Math.Round(_liveMaxMa * value / 100.0)}/{_liveMaxMa}"
            : $"{(int)System.Math.Round(value * 100.0 / _liveMaxMa)}%";
    }

    // Spell typeahead for the two override pickers — the character's castable spells
    // (SpellbookState.AvailablePicks), same source the Settings → Combat spell slots
    // use. Empty in headless tests. The box commits the pick's Short cast-code.
    public IReadOnlyList<SpellPick> SpellSuggestions { get; }

    // Match typed text against either the cast-code or the spell name, so a slot is
    // findable by code or name even though the box commits the code.
    public AutoCompleteFilterPredicate<object?> SpellSuggestionFilter { get; } =
        static (search, item) =>
            item is SpellPick pick &&
            (string.IsNullOrEmpty(search)
             || pick.Short.Contains(search, StringComparison.OrdinalIgnoreCase)
             || pick.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

    [ObservableProperty] private bool _dontBackstab;

    // Kill this NEUTRAL monster on sight. Neutrals never attack first, so they're
    // normally left alone; checking this makes auto-combat engage it like an enemy
    // while other passive neutrals stay safe to rest among. Only meaningful for a
    // Neutral relationship — the checkbox is shown only then (ShowKillOnSight).
    [ObservableProperty] private bool _killOnSight;

    // The KillOnSight checkbox applies only to Neutral-relationship monsters.
    public bool ShowKillOnSight => Relationship == MonsterRelationship.Neutral;

    public IReadOnlyList<MdbInfoRow> MdbInfo { get; }

    public IReadOnlyList<MonsterRelationship> AvailableRelationships { get; } =
        Enum.GetValues<MonsterRelationship>().ToArray();

    public IReadOnlyList<MonsterAttackPriority> AvailablePriorities { get; } =
        Enum.GetValues<MonsterAttackPriority>().ToArray();

    // Tiers the picker offers. Restricted to tiers the resolver can actually write to in
    // the current session (Global always; BBS only with an active BBS; Character only with
    // a loaded profile) so Save can't land on a tier whose scope is unresolvable.
    // Read-only Defaults is excluded — the MDB is its source.
    public IReadOnlyList<SettingsTier> AvailableTiers { get; }

    public string Title => $"Monster — {(Name.Length > 0 ? Name : $"#{WccNoStr}")}";

    // Resolves a typed cast-code (e.g. "turn") to its Spells.Number, or null
    // when the text doesn't match a known spell — see ParseAttackOverride.
    // Optional so the dialog still works (falling back to numeric-only
    // detection) wherever game data isn't wired, e.g. tests.
    private readonly Func<string, int?>? _resolveSpellShort;

    // The inverse: a stored Spells.Number back to its cast-code, so re-opening
    // the dialog on an override that auto-resolved from a typed code (e.g.
    // "agon") shows "agon" again, not the internal number it resolved to
    // (report paradigm-20260813-131658: "it keeps putting 22 in when I put the
    // spell in"). Falls back to the raw number when unresolvable (e.g. the
    // game-data set changed since the override was saved).
    private readonly Func<int, string?>? _resolveSpellNumber;

    // The overlay the dialog would Save if the user made no change to the installed
    // defaults — built by running the SAME Compose over the defaults-derived field
    // values. On Save an overlay equal to this means "no net change vs the seed", so
    // the caller clears the tier's redundant override (or, at the Defaults tier,
    // resets the record). Captured in the ctor from installedDefaults.
    private readonly MonsterOverlay _defaultsBaseline;

    public MonsterEditDialogViewModel(
        string wccNoStr,
        string mdbName,
        MonsterOverlay? existing,
        SettingsTier currentTier,
        IReadOnlyList<MdbInfoRow> mdbInfo,
        IReadOnlyList<SettingsTier>? writableTiers = null,
        MonsterOverlay? installedDefaults = null,
        Func<string, int?>? resolveSpellShort = null,
        Func<int, string?>? resolveSpellNumber = null,
        IReadOnlyList<SpellPick>? spellSuggestions = null,
        bool manaModePercentage = true,
        int liveMaxMa = 0)
    {
        _resolveSpellShort = resolveSpellShort;
        _resolveSpellNumber = resolveSpellNumber;
        SpellSuggestions = spellSuggestions ?? Array.Empty<SpellPick>();
        _manaModePercentage = manaModePercentage;
        _liveMaxMa = liveMaxMa;
        WccNoStr      = wccNoStr;
        MonsterNumber = int.TryParse(wccNoStr, out int n) ? n : 0;
        Name          = existing?.Name ?? mdbName;
        // The writable tiers (Character / BBS / Global) plus "Installed defaults" as
        // the last option — picking it resets the record (wipes every tier's
        // override). The default selection is always a WRITABLE tier, never Defaults,
        // so a plain edit lands as an override and only an explicit Defaults pick
        // resets (else saving an unchanged Def record would try to reset it).
        IReadOnlyList<SettingsTier> writable = writableTiers is { Count: > 0 }
            ? writableTiers
            : new[] { SettingsTier.Character, SettingsTier.Bbs, SettingsTier.Global };
        AvailableTiers = writable.Append(SettingsTier.Defaults).ToArray();
        UseTier        = writable.Contains(currentTier) ? currentTier : writable[0];
        MdbInfo       = mdbInfo;

        Relationship = existing?.Relationship ?? MonsterRelationship.Enemy;
        Priority     = existing?.Priority     ?? MonsterAttackPriority.Normal;

        PreAttackSpellId = (existing?.OverridePreAttackSpellId is { } pi) ? pi.ToString() : string.Empty;
        PreAttackCount   = existing?.OverridePreAttackCount;
        PreAttackMinMana = existing?.OverridePreAttackMinMana ?? 0;
        // A command override wins the box display; else show the spell's cast-code
        // when it resolves (round-trips a typed "agon" back to "agon", not its
        // internal number), falling back to the bare number when it doesn't.
        AttackOverride   = existing?.OverrideAttackCommand is { Length: > 0 } cmd
            ? cmd
            : (existing?.OverrideAttackSpellId is { } ai
                ? (_resolveSpellNumber?.Invoke(ai) ?? ai.ToString())
                : string.Empty);
        AttackCount      = existing?.OverrideAttackCount;
        AttackMinMana    = existing?.OverrideAttackMinMana ?? 0;

        DontBackstab = existing?.DontBackstab ?? false;
        KillOnSight  = existing?.KillOnSight  ?? false;

        // What Compose would produce from the installed-defaults values, derived with
        // the SAME fallbacks the field init above uses — so an unedited (or edited-back)
        // record compares equal to it.
        _defaultsBaseline = Compose(
            installedDefaults?.Name ?? mdbName,
            installedDefaults?.Relationship ?? MonsterRelationship.Enemy,
            installedDefaults?.Priority ?? MonsterAttackPriority.Normal,
            installedDefaults?.OverridePreAttackSpellId?.ToString() ?? string.Empty,
            installedDefaults?.OverridePreAttackCount,
            installedDefaults?.OverridePreAttackMinMana ?? 0,
            installedDefaults?.OverrideAttackCommand is { Length: > 0 } dc
                ? dc
                : (installedDefaults?.OverrideAttackSpellId is { } dai
                    ? (_resolveSpellNumber?.Invoke(dai) ?? dai.ToString())
                    : string.Empty),
            installedDefaults?.OverrideAttackCount,
            installedDefaults?.OverrideAttackMinMana ?? 0,
            installedDefaults?.DontBackstab ?? false,
            installedDefaults?.KillOnSight ?? false,
            _resolveSpellShort);
    }

    [RelayCommand]
    private void Save()
    {
        MonsterOverlay overlay = Compose(
            Name, Relationship, Priority, PreAttackSpellId, PreAttackCount, PreAttackMinMana,
            AttackOverride, AttackCount, AttackMinMana, DontBackstab, KillOnSight, _resolveSpellShort);

        // A record (value-equal) comparison against the defaults baseline: true means
        // the user dragged everything back to the seed, so the caller clears the tier's
        // now-redundant override instead of writing it.
        bool equalsInstalledDefaults = overlay.Equals(_defaultsBaseline);
        CloseRequested?.Invoke(new MonsterEditResult(WccNoStr, overlay, UseTier, equalsInstalledDefaults));
    }

    // Single overlay construction, shared by Save (live fields) and the ctor's
    // defaults-baseline capture, so the two are guaranteed to compare apples-to-apples.
    private static MonsterOverlay Compose(
        string name, MonsterRelationship relationship, MonsterAttackPriority priority,
        string preAttackSpellId, int? preAttackCount, int preAttackMinMana,
        string attackOverride, int? attackCount, int attackMinMana,
        bool dontBackstab, bool killOnSight, Func<string, int?>? resolveSpellShort)
    {
        (int? attackSpellId, string? attackCommand) = ParseAttackOverride(attackOverride, resolveSpellShort);
        return new MonsterOverlay
        {
            Name                     = string.IsNullOrWhiteSpace(name) ? null : name,
            Relationship             = relationship,
            Priority                 = priority,
            OverridePreAttackSpellId = ParseNullableInt(preAttackSpellId),
            OverridePreAttackCount   = preAttackCount,
            OverridePreAttackMinMana = preAttackMinMana > 0 ? preAttackMinMana : null,
            OverrideAttackSpellId    = attackSpellId,
            OverrideAttackCount      = attackCount,
            OverrideAttackMinMana    = attackMinMana > 0 ? attackMinMana : null,
            OverrideAttackCommand    = attackCommand,
            DontBackstab             = dontBackstab,
            KillOnSight              = killOnSight,
        };
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private static int? ParseNullableInt(string? text)
        => int.TryParse(text, out int n) ? n : null;

    // The "Override Attack" box holds EITHER a Spell.Number (routed through the
    // mana-gated attack-spell rung — needs a Max cast count) OR a raw command /
    // verb like "attack" or "bash" (sent verbatim, no gating). A positive
    // integer reads as a spell id directly; blank is no override.
    //
    // For any other text, resolveSpellShort (when supplied) gets first look: a
    // typed cast-code that matches a known spell (e.g. "turn") resolves to that
    // spell's Number and lands on the SAME mana-gated, cascading rung as typing
    // the number directly — someone reasonably types the code they'd actually
    // cast in-game, not an internal database id they have no way to know,
    // and shouldn't silently lose mana/cap gating for it (report
    // paradigm-20260813-070249: "it just means you use a different spell",
    // not "ignore combat settings completely"). Only text that resolves to no
    // known spell falls through to the raw command path — this is also why
    // typing "attack" persists as a command instead of being silently dropped
    // by an int-only parse (report paradigm-20260809-131642).
    //
    // Exactly one of the pair is set (or both null) — the two are kept mutually
    // exclusive so a species never carries both an id and a command.
    public static (int? SpellId, string? Command) ParseAttackOverride(
        string? text, Func<string, int?>? resolveSpellShort = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        string trimmed = text.Trim();
        if (int.TryParse(trimmed, out int n) && n > 0) return (n, null);
        if (resolveSpellShort?.Invoke(trimmed) is { } resolved) return (resolved, null);
        return (null, trimmed);
    }
}

// Returned by MonsterEditDialogViewModel on Save. WccNoStr is the monster's WCC No as a
// string — primary key for the overlay write; Overlay is the user's edited overlay
// payload; Tier is the tier the overlay should be written at (SettingsTier.Defaults =
// reset the record). EqualsInstalledDefaults is true when the edit matches the seeded
// defaults, so the applier clears the tier's redundant override rather than writing it.
public sealed record MonsterEditResult(
    string         WccNoStr,
    MonsterOverlay Overlay,
    SettingsTier   Tier,
    bool           EqualsInstalledDefaults);
