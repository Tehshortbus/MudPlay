namespace MudPlay.Models.GameData;

// A raw wire line Game.MessageCandidateWatcher captured because it matched no
// MessageRecord slot and no registered MessageRouter pattern — a candidate for
// a new or changed Messages catalogue entry, staged for human review rather
// than auto-committed (a bare line can't say which perspective slot it
// belongs in or what MessageFlags apply — that's always a person's call).
//
// Storage lives at Data/game data/{set}/message-candidates.json — pure
// runtime-observed state, not curated data, so unlike MessageRecord there is
// no seed-file fallback (see Services.MessageCandidateStore).
//
// Identity rule: Id is ComputeId(RawText), mirroring MessageRecord.ComputeId's
// SHA1-truncated-to-16-hex-chars shape but over the single RawText field —
// the raw line IS the whole identity here, there's no multi-slot bundle yet.
public sealed record MessageCandidateRecord(
    string         Id,
    string         RawText,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int            Occurrences,
    bool           Dismissed,
    // Map / room the line was FIRST seen in (null when position wasn't yet known),
    // a locator hint for where an unrecognized line came from. Not part of Id — the
    // same line seen in two rooms stays one candidate, tagged with the first sighting.
    // Trailing + nullable so older message-candidates.json (no location) still loads.
    int?           Map = null,
    int?           Room = null)
{
    public static string ComputeId(string rawText)
    {
        byte[] buf = System.Text.Encoding.UTF8.GetBytes(rawText);
        byte[] hash = System.Security.Cryptography.SHA1.HashData(buf);
        System.Text.StringBuilder sb = new(16);
        for (int i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
