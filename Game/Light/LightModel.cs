namespace MudPlay.Game.Light;

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

    // Extra illumination that must be added to just reach visibility (V == -150),
    // or 0 when the room is already seeable. Also the minimum light-source Strength
    // needed to see a room at a given worn illumination — pass charIllu = wornIllu
    // and read the gap.
    public static int IlluGapToSee(int charIllu, int roomLight)
    {
        int v = charIllu + roomLight;
        return v < SeeThreshold ? SeeThreshold - v : 0;
    }

    // Every room-light line opens with this, then the band's wording. Split out so
    // Describe (what we show) and IsRoomLightPhrase (what we recognize off the wire)
    // read the same band vocabulary and can't drift apart.
    private const string RoomLightPrefix = "The room is ";

    private static string BandWording(LightVisibility visibility) => visibility switch
    {
        LightVisibility.PitchBlack    => "pitch black",
        LightVisibility.VeryDark      => "very dark",
        LightVisibility.BarelyVisible => "barely visible",
        LightVisibility.DimlyLit      => "dimly lit",
        _                             => string.Empty,
    };

    // The room-light phrase MajorMUD prints for a visibility band, or the empty
    // string for Normal (no line).
    public static string Describe(LightVisibility visibility) => visibility switch
    {
        // Very-dark is the only band that spells out the consequence.
        LightVisibility.VeryDark => RoomLightPrefix + "very dark — you can't see anything",
        LightVisibility.Normal   => string.Empty,
        _ => BandWording(visibility) is { Length: > 0 } w ? RoomLightPrefix + w : string.Empty,
    };

    // True when the line is the room's light announcement for any visibility band.
    // Matched as a prefix because the tail varies per realm — a trailing period,
    // and very-dark's "you can't see anything" clause with either a hyphen or an
    // em dash. Room light is fully known from game data, so the unrecognized-line
    // capture uses this to keep light announcements out of the review queue.
    public static bool IsRoomLightPhrase(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        string text = line.Trim();
        if (!text.StartsWith(RoomLightPrefix, StringComparison.Ordinal)) return false;

        string rest = text[RoomLightPrefix.Length..];
        foreach (LightVisibility band in Enum.GetValues<LightVisibility>())
            if (BandWording(band) is { Length: > 0 } wording
                && rest.StartsWith(wording, StringComparison.Ordinal))
                return true;
        return false;
    }
}
