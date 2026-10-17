using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MudPlay.Game.Map;

namespace MudPlay.Game.Remote;

// Chat-safe text encoding for a saved loop sent by `@loop send`. The loop is written
// as compact JSON (short keys, defaults omitted), deflated, base64url'd, and cut into
// chunks that each fit one telepath. Unlike the @roomba sync lines the chunks are NOT
// self-contained — a loop is only useful whole — so the receiver reassembles all of
// them before decoding.
//
// What defines the route travels: name, notes, and each waypoint's room / command /
// delay / no-rest / no-attack. So do the sending character's two loop settings (only
// attack in lair rooms, the lair wait), which the receiving character takes as its
// own for the loop when it saves it (LoopShareReceiver). Favourite and folder are the
// receiver's own choices and stay behind. A loop's signature (LoopSignature) does not
// travel: each side works it out from what it holds.
//
// Decoding is hostile-input safe: the inflated size is capped (a tiny blob can't
// expand into a huge allocation), the waypoint count and field lengths are bounded,
// and every room must parse — anything else is a FormatException.
public static class LoopShareCodec
{
    // Base64url characters per chunk — the same budget the @roomba sync lines use, so
    // a chunk plus its "@loopdata <id> i/n" prefix and the reply braces stays well
    // inside one MajorMUD input line.
    public const int MaxChunkChars = 200;

    // A loop needs at least two rooms to form a cycle; the cap keeps a crafted
    // payload from building an absurd one.
    private const int MaxWaypoints = 2000;
    private const int MaxInflatedBytes = 512 * 1024;
    private const int MaxNameChars = 120;
    private const int MaxCommandChars = 250;
    private const int MaxNotesChars = 4000;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private sealed record Payload(
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("o")] string? Notes,
        [property: JsonPropertyName("l")] bool OnlyAttackInLairRooms,
        [property: JsonPropertyName("w")] List<Waypoint> Waypoints,
        // Added after the first share format; an older client just ignores it.
        [property: JsonPropertyName("ld")] int LairEntryDebuff = 0);

    private sealed record Waypoint(
        [property: JsonPropertyName("r")] string Room,
        [property: JsonPropertyName("c")] string? Command,
        [property: JsonPropertyName("d")] long DelayMs,
        [property: JsonPropertyName("nr")] bool DoNotRest,
        [property: JsonPropertyName("na")] bool DoNotAttack,
        // Added after the first share format; an older client just ignores them.
        [property: JsonPropertyName("rh")] bool RestHereHp = false,
        [property: JsonPropertyName("rm")] bool RestHereMana = false);

    public static IReadOnlyList<string> Encode(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        Payload payload = new(
            loop.Name,
            string.IsNullOrEmpty(loop.Notes) ? null : loop.Notes,
            loop.OnlyAttackInLairRooms,
            loop.Waypoints.Select(w => new Waypoint(
                w.Room,
                string.IsNullOrEmpty(w.Command) ? null : w.Command,
                w.DelayMs, w.DoNotRest, w.DoNotAttack, w.RestHereHp, w.RestHereMana)).ToList(),
            (int)loop.LairEntryDebuff);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        using MemoryStream packed = new();
        using (DeflateStream deflate = new(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json);
        string blob = Base64Url.EncodeToString(packed.ToArray());

        List<string> chunks = new();
        for (int i = 0; i < blob.Length; i += MaxChunkChars)
            chunks.Add(blob.Substring(i, Math.Min(MaxChunkChars, blob.Length - i)));
        return chunks;
    }

    // lairWaitUnknown: the loop named a way of waiting this client does not know (a
    // later version's) and was given none. It then is not the loop the sender
    // offered, in that one setting, and cannot be checked against the sender's code.
    public static Loop Decode(IEnumerable<string> chunks, out bool lairWaitUnknown)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        lairWaitUnknown = false;
        byte[] packed;
        try { packed = Base64Url.DecodeFromChars(string.Concat(chunks)); }
        catch (FormatException ex) { throw new FormatException("not base64url", ex); }

        byte[] json = Inflate(packed);
        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(json, Json); }
        catch (JsonException ex) { throw new FormatException($"bad loop JSON: {ex.Message}", ex); }
        if (payload is null) throw new FormatException("empty loop payload");

        string name = payload.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > MaxNameChars) throw new FormatException("bad loop name");
        // The name goes into a file name, the Loops list and the notice written to
        // the terminal: an escape sequence in it would be run there.
        if (name.Any(char.IsControl)) throw new FormatException("control character in the loop name");
        if (payload.Waypoints is not { Count: >= 2 and <= MaxWaypoints })
            throw new FormatException("a loop needs 2 or more rooms");
        if (payload.Notes is { Length: > MaxNotesChars }) throw new FormatException("notes too long");

        List<LoopWaypoint> waypoints = new(payload.Waypoints.Count);
        foreach (Waypoint w in payload.Waypoints)
        {
            if (w is null || !RoomKey.TryParseWire(w.Room, out RoomKey key))
                throw new FormatException($"bad room '{w?.Room}'");
            if (w.Command is { Length: > MaxCommandChars }) throw new FormatException("command too long");
            // A room's command is sent to the game when the loop gets there. Several
            // commands are chained with ';' or the two characters '^M', never with a
            // real line end, so a control character here is no command anyone wrote:
            // a carriage return would slip a line of its own to the game.
            if (w.Command is { } command && command.Any(char.IsControl))
                throw new FormatException("control character in a room's command");
            if (w.DelayMs < 0) throw new FormatException("negative delay");
            waypoints.Add(new LoopWaypoint(key, w.Command, w.DelayMs, w.DoNotRest, w.DoNotAttack)
            {
                RestHereHp = w.RestHereHp,
                RestHereMana = w.RestHereMana,
            });
        }

        lairWaitUnknown = !Enum.IsDefined(typeof(LairEntryDebuffMode), payload.LairEntryDebuff);
        return new Loop(name, waypoints)
        {
            Notes = payload.Notes ?? string.Empty,
            OnlyAttackInLairRooms = payload.OnlyAttackInLairRooms,
            LairEntryDebuff = lairWaitUnknown ? LairEntryDebuffMode.Off : (LairEntryDebuffMode)payload.LairEntryDebuff,
        };
    }

    // Inflate with a hard output cap so a deflate bomb is refused rather than
    // allocated.
    private static byte[] Inflate(byte[] packed)
    {
        try
        {
            using DeflateStream inflate = new(new MemoryStream(packed), CompressionMode.Decompress);
            using MemoryStream output = new();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = inflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxInflatedBytes) throw new FormatException("loop payload too large");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException ex) { throw new FormatException("corrupt loop payload", ex); }
    }
}
