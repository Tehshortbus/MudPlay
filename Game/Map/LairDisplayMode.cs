namespace FujinTerm.Game.Map;

// How the Navigation map paints lair rooms. The Lairs chip cycles through
// these in order: Uniform -> Heat -> Off -> Uniform.
public enum LairDisplayMode
{
    // Every lair drawn in the single flat lair colour.
    Uniform,

    // Lairs shaded by respawn time — fast (short delay) rooms run hot (red),
    // slow (long delay) rooms run cold (blue). Rooms whose respawn time can't
    // be resolved fall back to the Uniform colour.
    Heat,

    // Lairs get no special fill; they render like any other room.
    Off,
}
