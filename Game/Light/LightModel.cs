namespace FujinTerm.Game.Light;

/// <summary>
/// The MajorMUD room-visibility model, ported verbatim from MMUD Explorer's
/// light threshold table (<c>frmMain.frm:33293-33300</c>). A room's visibility
/// is a function of the single combined value <c>V = charIllu + roomLight</c>,
/// where <c>charIllu</c> is the player's carried illumination (worn +illu gear
/// plus a readied light's projected strength) and <c>roomLight</c> is the
/// room's signed <c>Rooms.Light</c> offset. All members are pure so the same
/// formula drives both the live map tooltip and the auto-light route predictor.
/// </summary>
public static class LightModel
{
    /// <summary>
    /// The visibility floor: a room is seeable exactly when
    /// <c>V &gt;= -150</c>. Below it the server hides contents and applies the
    /// can't-see state.
    /// </summary>
    public const int SeeThreshold = -150;

    /// <summary>Classify a room's visibility from the player's illumination and
    /// the room's light offset.</summary>
    public static LightVisibility Classify(int charIllu, int roomLight)
    {
        int v = charIllu + roomLight;
        if (v < -200) return LightVisibility.PitchBlack;
        if (v < -150) return LightVisibility.VeryDark;
        if (v < -100) return LightVisibility.BarelyVisible;
        if (v < 0)    return LightVisibility.DimlyLit;
        return LightVisibility.Normal;
    }

    /// <summary><c>true</c> when the room renders (V ≥ <see cref="SeeThreshold"/>).
    /// Pitch-black and very-dark rooms return <c>false</c>.</summary>
    public static bool CanSee(int charIllu, int roomLight)
        => charIllu + roomLight >= SeeThreshold;

    /// <summary>
    /// Extra illumination that must be added to just reach visibility
    /// (<c>V == -150</c>), or <c>0</c> when the room is already seeable. Also
    /// the minimum light-source <c>Strength</c> needed to see a room at a given
    /// worn illumination — pass <c>charIllu = wornIllu</c> and read the gap.
    /// </summary>
    public static int IlluGapToSee(int charIllu, int roomLight)
    {
        int v = charIllu + roomLight;
        return v < SeeThreshold ? SeeThreshold - v : 0;
    }

    /// <summary>The room-light phrase MajorMUD prints for a visibility band, or
    /// the empty string for <see cref="LightVisibility.Normal"/> (no line).</summary>
    public static string Describe(LightVisibility visibility) => visibility switch
    {
        LightVisibility.PitchBlack    => "The room is pitch black",
        LightVisibility.VeryDark      => "The room is very dark — you can't see anything",
        LightVisibility.BarelyVisible => "The room is barely visible",
        LightVisibility.DimlyLit      => "The room is dimly lit",
        _                             => string.Empty,
    };
}
