namespace FujinTerm.Game.Light;

/// <summary>
/// How visible a room is to the player, from the combined illumination
/// <c>V = charIllu + roomLight</c> against MME's band table
/// (<c>frmMain.frm:33293-33300</c>). <see cref="PitchBlack"/> and
/// <see cref="VeryDark"/> mean the server hides room contents and can't-see
/// applies; the rest render, with a melee penalty easing as V rises.
/// </summary>
public enum LightVisibility
{
    /// <summary><c>V &lt; -200</c> — can't see, contents hidden.</summary>
    PitchBlack,

    /// <summary><c>-200 &lt;= V &lt; -150</c> — can't see, contents hidden.</summary>
    VeryDark,

    /// <summary><c>-150 &lt;= V &lt; -100</c> — visible, heavy melee penalty.</summary>
    BarelyVisible,

    /// <summary><c>-100 &lt;= V &lt; 0</c> — visible, slight penalty.</summary>
    DimlyLit,

    /// <summary><c>V &gt;= 0</c> — fully lit, no penalty.</summary>
    Normal,
}
