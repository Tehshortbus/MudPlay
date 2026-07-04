namespace FujinTerm.Game.Light;

// MajorMUD's room-visibility model. A room's visibility is a function of the
// single combined value V = charIllu + roomLight, where charIllu is the player's
// carried illumination (worn +illu gear plus a readied light's projected strength)
// and roomLight is the room's signed Rooms.Light offset. All members are pure so
// the same formula drives both the live map tooltip and the auto-light route
// predictor.
public static class LightModel
{
    // The visibility floor: a room is seeable exactly when V >= -150. Below it the
    // server hides contents and applies the can't-see state.
    public const int SeeThreshold = -150;

    // Classify a room's visibility from the player's illumination and the room's
    // light offset.
    public static LightVisibility Classify(int charIllu, int roomLight)
    {
        int v = charIllu + roomLight;
        if (v < -200) return LightVisibility.PitchBlack;
        if (v < -150) return LightVisibility.VeryDark;
        if (v < -100) return LightVisibility.BarelyVisible;
        if (v < 0)    return LightVisibility.DimlyLit;
        return LightVisibility.Normal;
    }

    // True when the room renders (V >= SeeThreshold). Pitch-black and very-dark
    // rooms return false.
    public static bool CanSee(int charIllu, int roomLight)
        => charIllu + roomLight >= SeeThreshold;

    // Extra illumination that must be added to just reach visibility (V == -150),
    // or 0 when the room is already seeable. Also the minimum light-source Strength
    // needed to see a room at a given worn illumination — pass charIllu = wornIllu
    // and read the gap.
    public static int IlluGapToSee(int charIllu, int roomLight)
    {
        int v = charIllu + roomLight;
        return v < SeeThreshold ? SeeThreshold - v : 0;
    }

    // The room-light phrase MajorMUD prints for a visibility band, or the empty
    // string for Normal (no line).
    public static string Describe(LightVisibility visibility) => visibility switch
    {
        LightVisibility.PitchBlack    => "The room is pitch black",
        LightVisibility.VeryDark      => "The room is very dark — you can't see anything",
        LightVisibility.BarelyVisible => "The room is barely visible",
        LightVisibility.DimlyLit      => "The room is dimly lit",
        _                             => string.Empty,
    };
}
