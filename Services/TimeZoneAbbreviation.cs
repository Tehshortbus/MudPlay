namespace MudPlay.Services;

// Best-effort short timezone abbreviation ("PST"/"MST"/etc.) for chat-facing
// timestamps, where a numeric UTC offset ("-07:00") is technically correct but
// reads worse than the name people actually use. .NET has no built-in
// abbreviation API — IANA tzdata deliberately omits them as ambiguous
// worldwide (multiple zones share "CST") — so this maps the local zone's IANA
// id (Linux/macOS) or Windows id to the common North American abbreviation,
// DST-aware via TimeZoneInfo.Local.IsDaylightSavingTime. A zone outside this
// table falls back to the numeric offset rather than guessing.
public static class TimeZoneAbbreviation
{
    private static readonly Dictionary<string, (string Standard, string Daylight)> ById =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // IANA ids (Linux/macOS)
        ["America/Los_Angeles"] = ("PST", "PDT"),
        ["America/Vancouver"] = ("PST", "PDT"),
        ["America/Denver"] = ("MST", "MDT"),
        ["America/Phoenix"] = ("MST", "MST"),      // Arizona doesn't observe DST
        ["America/Chicago"] = ("CST", "CDT"),
        ["America/New_York"] = ("EST", "EDT"),
        ["America/Anchorage"] = ("AKST", "AKDT"),
        ["Pacific/Honolulu"] = ("HST", "HST"),

        // Windows ids
        ["Pacific Standard Time"] = ("PST", "PDT"),
        ["Mountain Standard Time"] = ("MST", "MDT"),
        ["US Mountain Standard Time"] = ("MST", "MST"),   // Windows' Arizona-equivalent zone
        ["Central Standard Time"] = ("CST", "CDT"),
        ["Eastern Standard Time"] = ("EST", "EDT"),
        ["Alaskan Standard Time"] = ("AKST", "AKDT"),
        ["Hawaiian Standard Time"] = ("HST", "HST"),
    };

    // zone's abbreviation for the given instant (standard or daylight,
    // whichever applies at `at`), or that instant's numeric UTC offset
    // ("-07:00") when the zone isn't in the table above. Defaults to the local
    // machine's zone.
    public static string For(DateTimeOffset at, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        if (ById.TryGetValue(zone.Id, out (string Standard, string Daylight) names))
            return zone.IsDaylightSavingTime(at) ? names.Daylight : names.Standard;
        return at.ToString("zzz");
    }
}
