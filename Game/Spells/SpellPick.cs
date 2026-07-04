namespace FujinTerm.Game.Spells;

// One entry in a Settings spell-picker typeahead: the 4-letter Short cast-code
// the game actually recognises, paired with the full Name for readability.
//
// The picker commits Short so the stored — and ultimately cast — value is the
// code the game accepts; typing the full name only ever speaks it. The dropdown
// shows Display ("code — name") and filtering matches either field, so the user
// can still find a slot by name.
public readonly record struct SpellPick(string Short, string Name)
{
    // Dropdown label: e.g. swan — way of the swan.
    public string Display => $"{Short} — {Name}";
}
