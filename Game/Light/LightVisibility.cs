namespace FujinTerm.Game.Light;

// How visible a room is to the player, from the combined illumination
// V = charIllu + roomLight against MajorMUD's band table. PitchBlack and VeryDark
// mean the server hides room contents and can't-see applies; the rest render, with
// a melee penalty easing as V rises.
public enum LightVisibility
{
    // V < -200 — can't see, contents hidden.
    PitchBlack,

    // -200 <= V < -150 — can't see, contents hidden.
    VeryDark,

    // -150 <= V < -100 — visible, heavy melee penalty.
    BarelyVisible,

    // -100 <= V < 0 — visible, slight penalty.
    DimlyLit,

    // V >= 0 — fully lit, no penalty.
    Normal,
}
