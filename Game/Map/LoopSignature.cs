using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MudPlay.Game.Map;

// What a loop is, as one value: two loops with the same signature walk the same rooms
// in the same order, do the same in each, and are run with the same two loop-wide
// settings. `@loop send` uses it to tell a loop we already have from a different one
// that only shares its name (user, 2026-10-11).
//
// It is worked out when asked for and stored nowhere: not in the loop file, not in
// what `@loop send` puts on the wire. Each side computes it from the loop it holds.
//
// What counts: the waypoints in order (room, command, delay, and the four per-room
// marks) and the two loop-wide settings handed in. What does not: the loop's name,
// folder, notes and favourite flag, which are labels on a loop and not the loop.
//
// The signature is SHA-256 over the bytes below. The form is fixed: any change to it
// changes every signature, so two versions of the client would stop agreeing on
// which loops are the same. A change needs a new tag on the first line.
//
//   "MudPlay loop signature 1\n"        ASCII, the form's tag
//   only attack in lair rooms            1 byte, 0 or 1
//   the lair wait, by its name           text (LairEntryDebuffMode: "Off", "WaitForSpells", ...)
//   number of waypoints                  int
//   then for each waypoint, in order:
//     map number, room number            int, int
//     command                            text; none and empty are the same
//     delay in milliseconds              int
//     marks                              1 byte: 1 no rest, 2 no attack, 4 rest here HP, 8 rest here mana,
//                                        16 a lap wait follows
//     lap wait in milliseconds           int, int (high, low); only when mark 16 is set, so a
//                                        loop with no lap wait keeps the signature it had
//
// An int is 4 bytes, little-endian, two's complement. A text is its UTF-8 byte count
// as an int, then the bytes. Every field is fixed in size or carries its length, so
// no two different loops can come out as the same bytes. Nothing is formatted as
// text by the machine's culture. A room is taken as its two numbers, not as the
// loop file spells it, so "1/5" and "1/05" are one room; a room that doesn't parse
// is 0/0 here as it is to everything else that reads the loop.
public static class LoopSignature
{
    private const string FormTag = "MudPlay loop signature 1\n";

    // Hex characters of the short code shown to people. For the eye only: two loops
    // are the same when the whole signature is.
    public const int ShortLength = 8;

    // The signature of loop as the loaded character runs it: its waypoints with the
    // two settings stamped on it.
    public static string Of(Loop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        return Of(loop.Waypoints, loop.OwnSettings);
    }

    // 64 lower-case hex characters.
    public static string Of(IReadOnlyList<LoopWaypoint> waypoints, LoopOwnSettings settings)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        using MemoryStream form = new();
        form.Write(Encoding.ASCII.GetBytes(FormTag));
        form.WriteByte(settings.OnlyAttackInLairRooms ? (byte)1 : (byte)0);
        WriteText(form, settings.LairEntryDebuff.ToString());
        WriteInt(form, waypoints.Count);
        foreach (LoopWaypoint w in waypoints)
        {
            RoomKey room = w.Key;
            WriteInt(form, room.Map);
            WriteInt(form, room.Room);
            WriteText(form, w.Command ?? string.Empty);
            // A delay that fits an int is written as it always was, so a loop's
            // signature doesn't change; a longer one (minutes, past ~24 days of ms)
            // follows a -1 no stored delay can be.
            if (w.DelayMs <= int.MaxValue) WriteInt(form, (int)w.DelayMs);
            else
            {
                WriteInt(form, -1);
                WriteInt(form, (int)(w.DelayMs >> 32));
                WriteInt(form, (int)w.DelayMs);
            }
            form.WriteByte((byte)((w.DoNotRest ? 1 : 0) | (w.DoNotAttack ? 2 : 0)
                                  | (w.RestHereHp ? 4 : 0) | (w.RestHereMana ? 8 : 0)
                                  | (w.LapWaitMs > 0 ? 16 : 0) | (w.StartHere ? 32 : 0)));
            if (w.LapWaitMs > 0)
            {
                WriteInt(form, (int)(w.LapWaitMs >> 32));
                WriteInt(form, (int)w.LapWaitMs);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(form.GetBuffer().AsSpan(0, (int)form.Length)));
    }

    // The first few characters of a signature, to show beside a loop's name.
    public static string Short(string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return signature.Length <= ShortLength ? signature : signature[..ShortLength];
    }

    public static string ShortOf(Loop loop) => Short(Of(loop));

    private static void WriteInt(Stream form, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        form.Write(bytes);
    }

    private static void WriteText(Stream form, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        WriteInt(form, bytes.Length);
        form.Write(bytes);
    }
}
