using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FujinTerm.Game.Map.MpFile;

// Pure structural parser for MegaMUD .mp loop files. Reads the text into an
// MpLoopFile with no graph resolution and no RoomKey assignment — that's
// MpFileImporter's job. Path-style files (start != end hash) are rejected here so
// the importer doesn't have to.
public static partial class MpFileParser
{
    // Parse the file at path. Throws MpFileFormatException on any structural
    // problem.
    public static MpLoopFile ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            throw new MpFileFormatException($"file not found: {path}");
        return Parse(File.ReadAllText(path));
    }

    // Parse text as a .mp loop file. Same error semantics as ParseFile.
    public static MpLoopFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Pull non-empty lines preserving order. MegaMUD writes CRLF;
        // in-the-wild files may also be plain LF.
        List<string> lines = new();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            lines.Add(line);
        }

        if (lines.Count < 3)
            throw new MpFileFormatException(
                "file too short — need at least a label line, a header room line, and a metadata line");

        // ---- line 1: [label][author] -------------------------------
        // Two consecutive bracketed tokens with no separator.
        Match hdr1 = HeaderLine1Regex().Match(lines[0]);
        if (!hdr1.Success)
            throw new MpFileFormatException(
                $"line 1 isn't a [label][author] pair: '{lines[0]}'");

        string label  = hdr1.Groups["label"].Value;
        string author = hdr1.Groups["author"].Value;

        // ---- header room line(s) -----------------------------------
        // V4 generator writes two ([start][end]); in-the-wild loop
        // files often have just one. Both shapes are valid for us
        // because the metadata line tells us they're identical when
        // it's a loop.
        int cursor = 1;
        Match? roomHdr = HeaderRoomRegex().Match(lines[cursor]);
        if (!roomHdr.Success)
            throw new MpFileFormatException(
                $"line {cursor + 1} isn't a [CODE:Group:Name] header: '{lines[cursor]}'");
        string code4    = roomHdr.Groups["code"].Value;
        string group    = roomHdr.Groups["group"].Value;
        string roomName = roomHdr.Groups["name"].Value;
        cursor++;

        // Optional second header room (V4 generator emits this).
        // Tolerate it but verify it's a loop (start code == end code).
        if (cursor < lines.Count && HeaderRoomRegex().IsMatch(lines[cursor]))
        {
            Match second = HeaderRoomRegex().Match(lines[cursor]);
            string secondCode4 = second.Groups["code"].Value;
            if (!string.Equals(secondCode4, code4, StringComparison.OrdinalIgnoreCase))
                throw new MpFileFormatException(
                    "this is a path-style .mp file (start and end rooms differ); only loop files are supported");
            cursor++;
        }

        // ---- metadata line -----------------------------------------
        if (cursor >= lines.Count)
            throw new MpFileFormatException("missing metadata line");
        string metaLine = lines[cursor++];
        string[] metaParts = metaLine.Split(':');
        if (metaParts.Length < 4)
            throw new MpFileFormatException(
                $"metadata line malformed: '{metaLine}' — expected at least 'startHash:endHash:N:-1'");

        string startHash = metaParts[0].Trim();
        string endHash   = metaParts[1].Trim();
        if (!string.Equals(startHash, endHash, StringComparison.OrdinalIgnoreCase))
            throw new MpFileFormatException(
                $"this is a path-style .mp file (start hash {startHash} != end hash {endHash}); only loop files are supported");
        if (startHash.Length != 8)
            throw new MpFileFormatException(
                $"start hashExits should be 8 hex chars, got '{startHash}' ({startHash.Length} chars)");

        if (!int.TryParse(metaParts[2].Trim(), out int declaredStepCount) || declaredStepCount < 0)
            throw new MpFileFormatException(
                $"step count token '{metaParts[2]}' isn't a non-negative integer");

        // metaParts[3] is the literal "-1" marker; rest are
        // gold/item/failPath/successPath which we don't consume.

        // ---- step rows ---------------------------------------------
        List<MpStep> steps = new(declaredStepCount);
        for (; cursor < lines.Count; cursor++)
        {
            string row = lines[cursor];
            string[] parts = row.Split(':');
            if (parts.Length < 3)
                throw new MpFileFormatException(
                    $"step row {steps.Count + 1} malformed: '{row}' — expected 'hashExits:options:dir'");

            string hashExits = parts[0].Trim();
            // parts[1] is the STEPF bitmask — discarded per UX.
            string dirRaw    = parts[2].Trim();

            if (hashExits.Length != 8)
                throw new MpFileFormatException(
                    $"step row {steps.Count + 1} hashExits '{hashExits}' isn't 8 hex chars");

            // Compass token → MpStep with Compass set. Anything else
            // (e.g. "go path", "climb wall", "open door") is preserved
            // verbatim as ActionText; the resolver picks the right
            // exit by matching the next step's hashExits against the
            // current room's neighbours, since our graph keys exits
            // by Direction and stores the verb text as RoomExit
            // metadata.
            Direction? compass = TryParseDirection(dirRaw, out Direction parsed)
                ? parsed
                : (Direction?)null;
            string actionText = compass.HasValue ? dirRaw.ToLowerInvariant() : dirRaw;

            steps.Add(new MpStep(hashExits.ToUpperInvariant(), compass, actionText));
        }

        if (steps.Count != declaredStepCount)
            throw new MpFileFormatException(
                $"metadata declared {declaredStepCount} step(s) but found {steps.Count} step row(s)");
        if (steps.Count < 2)
            throw new MpFileFormatException(
                $"loop has {steps.Count} step(s); need at least 2 to form a cycle");

        // First step's hashExits must equal startHashExits — that's
        // the room we're standing in for step 0. The V4 generator
        // enforces this; in-the-wild files honour it.
        if (!string.Equals(steps[0].HashExits, startHash, StringComparison.OrdinalIgnoreCase))
            throw new MpFileFormatException(
                $"first step hashExits '{steps[0].HashExits}' doesn't match metadata start hashExits '{startHash}'");

        return new MpLoopFile(
            Label:           label,
            Author:          author,
            Code4:           code4,
            GroupName:       group,
            RoomName:        roomName,
            StartHashExits:  startHash.ToUpperInvariant(),
            Steps:           steps);
    }

    private static bool TryParseDirection(string raw, out Direction dir)
    {
        switch (raw.Trim().ToUpperInvariant())
        {
            case "N":  dir = Direction.N;  return true;
            case "S":  dir = Direction.S;  return true;
            case "E":  dir = Direction.E;  return true;
            case "W":  dir = Direction.W;  return true;
            case "NE": dir = Direction.NE; return true;
            case "NW": dir = Direction.NW; return true;
            case "SE": dir = Direction.SE; return true;
            case "SW": dir = Direction.SW; return true;
            case "U":  dir = Direction.U;  return true;
            case "D":  dir = Direction.D;  return true;
            default:   dir = Direction.N;  return false;
        }
    }

    // ----- regexes -------------------------------------------------

    // [label][author] — labels can contain ] in some files (rare), so
    // we anchor on the closing ][ pair instead of greedy [^]] groups.
    [GeneratedRegex(@"^\[(?<label>[^\]]*)\]\[(?<author>[^\]]*)\]$",
        RegexOptions.Compiled)]
    private static partial Regex HeaderLine1Regex();

    // [CODE:Group:Name] — code is up to 4 chars, but the parser is
    // permissive and just takes everything before the first colon.
    [GeneratedRegex(@"^\[(?<code>[^:\]]+):(?<group>[^:\]]*):(?<name>[^\]]+)\]$",
        RegexOptions.Compiled)]
    private static partial Regex HeaderRoomRegex();
}

// Raised for any structural error in a .mp file — path-style instead of loop,
// malformed line, step count mismatch, unknown direction token, etc. Importer
// surfaces the message directly to the user via the editor's SaveError-style
// banner.
public sealed class MpFileFormatException : Exception
{
    public MpFileFormatException(string message) : base(message) { }
}
