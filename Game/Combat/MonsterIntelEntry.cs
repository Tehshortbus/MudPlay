using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace MudPlay.Game.Combat;

// One row of the Monster Intel master list — a MonsterCatalogEntry plus the
// grid's display-formatted text, mirroring ItemFinderEntry's shape (a thin
// display projection over an already-typed catalog record, not a re-parse of
// raw game data). Deliberately narrow: Monster Intel is a fast pre-fight
// check, not a monster database browser, so this carries only what the
// master list and its filters need — not the full record (that's the Game
// Data Browser's Monsters tab).
// A class, not a record: the three live fields below are mutated in place after
// construction and the row lives in a bound DataGrid, so it needs reference
// identity (stable selection) and change notification (cells re-render when the
// cap / hit% / rounds change) — value equality over mutating fields gives neither.
public sealed class MonsterIntelEntry : INotifyPropertyChanged
{
    public required MonsterCatalogEntry Source { get; init; }

    public int Number => Source.Number;
    public string Name => Source.Name;
    public int Hp => Source.Hp;
    public string HpText => Hp > 0 ? Hp.ToString("N0", Inv) : string.Empty;

    // True experience = raw EXP × ExpMulti (see MonsterCatalogEntry.EffectiveExp),
    // the same product the Game Data Monsters tab shows — the raw Exp field alone
    // undercounts a multiplier monster (aged earth dragon read 65000, not 2.6M).
    public long Exp => Source.EffectiveExp;
    public string ExpText => Exp > 0 ? Exp.ToString("N0", Inv) : string.Empty;

    // The monster's physical-attack accuracies. Accuracy (the majority slot's
    // value) stays the sort key; AccuracyText lists EVERY physical attack's
    // accuracy, most-used first (e.g. "45 / 30 / 20"), so it's clear the "Hits
    // You %" weights all of them, not just the highest. Empty for a monster with
    // no physical attack (Source.PhysicalAccuracy is null / no physical slots).
    public int Accuracy => Source.PhysicalAccuracy?.Majority ?? 0;
    public string AccuracyText => string.Join(" / ",
        Source.PhysicalAttacks
            .OrderByDescending(static p => p.Weight)
            .Select(static p => p.Accuracy.ToString(Inv)));

    // A non-zero RegenTime (hours) means this monster respawns on its own timer
    // — a boss, lair leader, or other timed spawn — rather than freely via the
    // room's regen (RegenTime 0 = "no respawn" per the MDB). Monster Intel's
    // "hide regen timers" filter reads this to drop the timed spawns from the list.
    public bool HasRegenTimer => Source.RegenTime > 0;

    // Chance this monster's own attack lands on the current character, given
    // their live AC/Dodge/wards — the one field on this record that ISN'T a
    // pure projection of Source, since it depends on live player state rather
    // than just this monster's own data. Set (for every entry at once) by
    // MonsterIntelViewModel.RebuildCharacterCapabilities whenever gear
    // changes. -1 = no character context, or the monster has no catalogued
    // physical attack to compute against.
    private int _incomingHitPercent = -1;
    public int IncomingHitPercent
    {
        get => _incomingHitPercent;
        set { if (_incomingHitPercent == value) return; _incomingHitPercent = value; Raise(nameof(IncomingHitPercent)); Raise(nameof(IncomingHitPercentText)); }
    }
    public string IncomingHitPercentText => IncomingHitPercent >= 0 ? $"{IncomingHitPercent}%" : string.Empty;

    // Projected rounds for the player to kill this monster with their current
    // weapon, given live accuracy/damage/swings/crit — the other live,
    // player-dependent field alongside IncomingHitPercent, set the same way
    // by RebuildCharacterCapabilities. -1 = no character context (not yet
    // computed); 0 = computed but not killable (no weapon, or the weapon
    // can't out-damage the monster's regen/HP at all).
    private int _estimatedRoundsToKill = -1;
    public int EstimatedRoundsToKill
    {
        get => _estimatedRoundsToKill;
        set { if (_estimatedRoundsToKill == value) return; _estimatedRoundsToKill = value; Raise(nameof(EstimatedRoundsToKill)); Raise(nameof(EstimatedRoundsToKillText)); }
    }

    // The rounds-to-kill cap is applied as a LIST FILTER in the view model, not
    // a per-row display clamp: a monster over the cap is dropped from the table
    // entirely (see MonsterIntelViewModel.PassesFilter), so this text only ever
    // renders a real number, a "—" for can't-kill, or blank for no-context.
    public string EstimatedRoundsToKillText => EstimatedRoundsToKill switch
    {
        < 0 => string.Empty,
        0 => "—",
        _ => EstimatedRoundsToKill.ToString(Inv),
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IReadOnlyList<MonsterIntelEntry> BuildCatalog(MonsterCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.All
            .Select(static e => new MonsterIntelEntry { Source = e })
            .OrderBy(static e => e.Name, System.StringComparer.OrdinalIgnoreCase)
            .ThenBy(static e => e.Number)
            .ToList();
    }
}

// Ability-code -> element-name map for the five elemental resists — Your
// Matchup's incoming-threat lookup uses CodeForName to match a monster's
// CastsElements name back to the resist code its own gear tracks under.
internal static class ElementalResistIndex
{
    private static readonly (int Code, string Name)[] Elements =
    {
        (3, "Cold"), (5, "Fire"), (65, "Stone"), (66, "Lightning"), (147, "Water"),
    };

    // A display name back to its resist ability code, or -1 for a
    // non-elemental name (Normal, Poison — never resist-indexed, see
    // MonsterResistIndex's own comment).
    public static int CodeForName(string element)
    {
        foreach ((int code, string name) in Elements)
            if (string.Equals(name, element, System.StringComparison.Ordinal)) return code;
        return -1;
    }

    // A resist ability code back to its element display name, or null for a
    // non-elemental code — the reverse of CodeForName, for rendering a monster's own
    // ElementalResists dict (keyed by these codes) as readable weakness/strength lines.
    public static string? NameForCode(int code)
    {
        foreach ((int c, string name) in Elements)
            if (c == code) return name;
        return null;
    }
}
