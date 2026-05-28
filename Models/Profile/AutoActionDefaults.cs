namespace FujinTerm.Models.Profile;

/// <summary>
/// Initial state for the Action-menu auto-toggles when the character
/// logs in. Lives on <see cref="GeneralSettings"/> twice — once per
/// Manual-Mode column and once per Auto-Mode column — so the user can
/// pick which engines come up engaged depending on the play mode.
/// The engines themselves wire in Phase 13 and read these flags as
/// their boot-up state.
/// </summary>
public sealed class AutoActionDefaults
{
    public bool AutoCombat { get; set; } = true;
    public bool AutoNuke { get; set; } = true;
    public bool AutoHealRest { get; set; } = true;
    public bool AutoBless { get; set; } = true;
    public bool AutoLight { get; set; } = true;
}
