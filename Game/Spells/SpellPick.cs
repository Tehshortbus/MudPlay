namespace FujinTerm.Game.Spells;

/// <summary>
/// One entry in a Settings spell-picker typeahead: the 4-letter
/// <see cref="Short"/> cast-code the game actually recognises, paired with
/// the full <see cref="Name"/> for readability.
/// </summary>
/// <remarks>
/// The picker commits <see cref="Short"/> (via the box's
/// <c>ValueMemberBinding</c>) so the stored — and ultimately cast — value is
/// the code the game accepts; typing the full name only ever speaks it. The
/// dropdown shows <see cref="Display"/> ("code — name") and filtering matches
/// either field, so the user can still find a slot by name.
/// </remarks>
public readonly record struct SpellPick(string Short, string Name)
{
    /// <summary>Dropdown label: e.g. <c>swan — way of the swan</c>.</summary>
    public string Display => $"{Short} — {Name}";
}
