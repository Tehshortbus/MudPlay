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
// Surfaces the editable overlay fields (Use-tier, Name, Landmass / Region / Area,
// Relationship, Priority, override spell slots, DontBackstab, KillOnSight).
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

    // Where the monster lives. Blank = not set. Free text with typeahead over the labels
    // already in use, so filing a new monster reuses a name instead of a near-miss spelling.
    [ObservableProperty] private string _landmass = string.Empty;
    [ObservableProperty] private string _region = string.Empty;
    [ObservableProperty] private string _area = string.Empty;

    public MonsterLocationSuggestions LocationSuggestions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKillOnSight))]
    private MonsterRelationship _relationship = MonsterRelationship.Enemy;
    [ObservableProperty] private MonsterAttackPriority _priority = MonsterAttackPriority.Normal;

    // ----- Debuff (single target) rung -----
    [ObservableProperty] private string _preAttackSpellId = string.Empty;
    // Per-room cast cap — null (blank) = unlimited, matching CombatSpellSlot.MaxCastsPerRoom
    // and the Settings → Combat NumericUpDown.
    [ObservableProperty] private int? _preAttackCount;
    // Minimum mana to cast — 0 = no floor. Interpreted per the char's Combat-tab mana
    // mode; SpellManaMax + PreAttackMinManaConverted mirror the Settings → Combat
    // spell-slot control. Mirrors CombatSpellSlot.MinManaPerCast.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreAttackMinManaConverted))]
    private int _preAttackMinMana;

    // ----- Normal attack spell rung -----
    // Spell-only: a Spell.Number, or a cast-code that resolves to one (see
    // ResolveSpellOverride). A raw attack verb belongs in the Physical attack box.
    [ObservableProperty] private string _normalSpellId = string.Empty;
    [ObservableProperty] private int? _normalCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NormalMinManaConverted))]
    private int _normalMinMana;

    // ----- Alternate attack spell rung -----
    // Spell-only, same as Normal — occupies the alternate rung of the same cascade.
    [ObservableProperty] private string _altSpellId = string.Empty;
    [ObservableProperty] private int? _altCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AltMinManaConverted))]
    private int _altMinMana;

    // ----- Physical attack rung -----
    // A raw verb ("attack", "bash") that replaces the weapon command on a round the
    // engine already chose physical. No mana / cap gating.
    [ObservableProperty] private string _physicalCommand = string.Empty;

    // Min-mana control parity with Settings → Combat: SpellManaMax caps the NumericUpDown
    // (100 in % mode, the absolute ceiling in Value mode) and the Converted strings show
    // the %↔value equivalent against the character's live max mana (snapshot at open).
    private readonly bool _manaModePercentage;
    private readonly int _liveMaxMa;

    public decimal SpellManaMax => _manaModePercentage ? 100 : 100_000;
    public string PreAttackMinManaConverted => FormatMana(PreAttackMinMana);
    public string NormalMinManaConverted    => FormatMana(NormalMinMana);
    public string AltMinManaConverted        => FormatMana(AltMinMana);

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

    // Spell typeahead for the three spell-override pickers (debuff / normal / alternate)
    // — the character's castable spells (SpellbookState.AvailablePicks), same source the
    // Settings → Combat spell slots use. Empty in headless tests. The box commits the
    // pick's Short cast-code.
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

    // The installed-defaults location labels. A box that still shows the seed's label is
    // stored as null (no override), so a later corrected seed reaches every monster the
    // user hasn't deliberately re-filed; only a label the user changed is written.
    private readonly (string? Landmass, string? Region, string? Area) _installedLocation;

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
        int liveMaxMa = 0,
        MonsterLocationSuggestions? locationSuggestions = null)
    {
        LocationSuggestions = locationSuggestions ?? MonsterLocationSuggestions.Empty;
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

        Landmass = existing?.Landmass ?? string.Empty;
        Region   = existing?.Region   ?? string.Empty;
        Area     = existing?.Area     ?? string.Empty;

        Relationship = existing?.Relationship ?? MonsterRelationship.Enemy;
        Priority     = existing?.Priority     ?? MonsterAttackPriority.Normal;

        // Show a stored spell override as its cast-code — round-trips a typed "agon"
        // back to "agon" rather than the internal number (report
        // paradigm-20260813-131658) — falling back to the bare number when it can't be
        // resolved (the game-data set changed since the override was saved).
        string SpellBox(int? id) => id is { } n
            ? (_resolveSpellNumber?.Invoke(n) ?? n.ToString())
            : string.Empty;

        PreAttackSpellId = SpellBox(existing?.OverridePreAttackSpellId);
        PreAttackCount   = existing?.OverridePreAttackCount;
        PreAttackMinMana = existing?.OverridePreAttackMinMana ?? 0;

        NormalSpellId = SpellBox(existing?.OverrideAttackSpellId);
        NormalCount   = existing?.OverrideAttackCount;
        NormalMinMana = existing?.OverrideAttackMinMana ?? 0;

        AltSpellId = SpellBox(existing?.OverrideAltAttackSpellId);
        AltCount   = existing?.OverrideAltAttackCount;
        AltMinMana = existing?.OverrideAltAttackMinMana ?? 0;

        PhysicalCommand = existing?.OverridePhysicalCommand ?? string.Empty;

        DontBackstab = existing?.DontBackstab ?? false;
        KillOnSight  = existing?.KillOnSight  ?? false;

        _installedLocation = (installedDefaults?.Landmass, installedDefaults?.Region, installedDefaults?.Area);

        // What Compose would produce from the installed-defaults values, derived with
        // the SAME SpellBox round-trip the field init above uses — so an unedited (or
        // edited-back) record compares equal to it.
        _defaultsBaseline = Compose(new OverlayFields(
            installedDefaults?.Name ?? mdbName,
            installedDefaults?.Relationship ?? MonsterRelationship.Enemy,
            installedDefaults?.Priority ?? MonsterAttackPriority.Normal,
            SpellBox(installedDefaults?.OverridePreAttackSpellId),
            installedDefaults?.OverridePreAttackCount,
            installedDefaults?.OverridePreAttackMinMana ?? 0,
            SpellBox(installedDefaults?.OverrideAttackSpellId),
            installedDefaults?.OverrideAttackCount,
            installedDefaults?.OverrideAttackMinMana ?? 0,
            SpellBox(installedDefaults?.OverrideAltAttackSpellId),
            installedDefaults?.OverrideAltAttackCount,
            installedDefaults?.OverrideAltAttackMinMana ?? 0,
            installedDefaults?.OverridePhysicalCommand ?? string.Empty,
            installedDefaults?.DontBackstab ?? false,
            installedDefaults?.KillOnSight ?? false,
            installedDefaults?.Landmass ?? string.Empty,
            installedDefaults?.Region ?? string.Empty,
            installedDefaults?.Area ?? string.Empty),
            _resolveSpellShort, _installedLocation);
    }

    [RelayCommand]
    private void Save()
    {
        MonsterOverlay overlay = Compose(LiveFields(), _resolveSpellShort, _installedLocation);

        // A record (value-equal) comparison against the defaults baseline: true means
        // the user dragged everything back to the seed, so the caller clears the tier's
        // now-redundant override instead of writing it.
        bool equalsInstalledDefaults = overlay.Equals(_defaultsBaseline);
        CloseRequested?.Invoke(new MonsterEditResult(WccNoStr, overlay, UseTier, equalsInstalledDefaults));
    }

    private OverlayFields LiveFields() => new(
        Name, Relationship, Priority,
        PreAttackSpellId, PreAttackCount, PreAttackMinMana,
        NormalSpellId, NormalCount, NormalMinMana,
        AltSpellId, AltCount, AltMinMana,
        PhysicalCommand, DontBackstab, KillOnSight,
        Landmass, Region, Area);

    // Single overlay construction, shared by Save (live fields) and the ctor's
    // defaults-baseline capture, so the two are guaranteed to compare apples-to-apples.
    // The three spell boxes are spell-only (ResolveSpellOverride → Spell.Number); the
    // physical box is a raw command trimmed to null when blank.
    private static MonsterOverlay Compose(
        OverlayFields f, Func<string, int?>? resolveSpellShort,
        (string? Landmass, string? Region, string? Area) installedLocation)
    {
        string? physical = string.IsNullOrWhiteSpace(f.PhysicalCommand) ? null : f.PhysicalCommand.Trim();
        return new MonsterOverlay
        {
            Name                     = string.IsNullOrWhiteSpace(f.Name) ? null : f.Name,
            Landmass                 = LocationOverride(f.Landmass, installedLocation.Landmass),
            Region                   = LocationOverride(f.Region, installedLocation.Region),
            Area                     = LocationOverride(f.Area, installedLocation.Area),
            Relationship             = f.Relationship,
            Priority                 = f.Priority,
            OverridePreAttackSpellId = ResolveSpellOverride(f.PreAttackSpellId, resolveSpellShort),
            OverridePreAttackCount   = f.PreAttackCount,
            OverridePreAttackMinMana = f.PreAttackMinMana > 0 ? f.PreAttackMinMana : null,
            OverrideAttackSpellId    = ResolveSpellOverride(f.NormalSpellId, resolveSpellShort),
            OverrideAttackCount      = f.NormalCount,
            OverrideAttackMinMana    = f.NormalMinMana > 0 ? f.NormalMinMana : null,
            OverrideAltAttackSpellId = ResolveSpellOverride(f.AltSpellId, resolveSpellShort),
            OverrideAltAttackCount   = f.AltCount,
            OverrideAltAttackMinMana = f.AltMinMana > 0 ? f.AltMinMana : null,
            OverridePhysicalCommand  = physical,
            DontBackstab             = f.DontBackstab,
            KillOnSight              = f.KillOnSight,
        };
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    // A blank box, or one that still shows the installed label, stores nothing (null) so the
    // tier never carries an empty string or a copy of the seed that would mask a lower
    // tier; only a label the user typed differently becomes an override.
    private static string? LocationOverride(string? box, string? installed)
    {
        if (string.IsNullOrWhiteSpace(box)) return null;
        string trimmed = box.Trim();
        return string.Equals(trimmed, installed, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    // Resolve a spell box's text to a Spell.Number, or null when blank / not a spell.
    // A positive integer is a Spell.Number directly; other text is looked up as a
    // cast-code via resolveSpellShort — someone types the code they'd actually cast
    // in-game (e.g. "agon"), not an internal database id they have no way to know
    // (report paradigm-20260813-070249). SPELL-ONLY: text that matches no known spell
    // yields null (no override) — raw attack verbs belong in the Physical attack box,
    // not a spell rung.
    public static int? ResolveSpellOverride(string? text, Func<string, int?>? resolveSpellShort = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (int.TryParse(trimmed, out int n)) return n > 0 ? n : null;
        return resolveSpellShort?.Invoke(trimmed);
    }

    // Bundles the editable fields so Save (live) and the ctor's defaults-baseline
    // capture run through the exact same Compose.
    private readonly record struct OverlayFields(
        string Name, MonsterRelationship Relationship, MonsterAttackPriority Priority,
        string PreAttackSpellId, int? PreAttackCount, int PreAttackMinMana,
        string NormalSpellId, int? NormalCount, int NormalMinMana,
        string AltSpellId, int? AltCount, int AltMinMana,
        string PhysicalCommand, bool DontBackstab, bool KillOnSight,
        string Landmass, string Region, string Area);
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
