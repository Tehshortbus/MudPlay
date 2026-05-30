using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

/// <summary>
/// Parses MegaMUD's legacy <c>messages.md</c> format into
/// <see cref="MessageRecord"/> rows. The format is line-oriented ASCII;
/// each record is up to 4 logical lines plus a blank separator:
/// <code>
///   name:flagsHex:action:response   (required header)
///   message pattern                 (required)
///   ends-with pattern               (optional — line may be empty OR omitted)
///   (blank line)                    (optional separator)
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Real-world legacy files freely mix styles: some records have 4
/// lines, some have 2 (no ends-with, no blank), some omit the blank
/// separator after a 3-line record. The parser detects the next
/// record by shape (<c>name:HEX4:DIGIT:</c>) so an omitted ends-with
/// or blank doesn't accidentally swallow it. Never throws on
/// malformed records — collects failures and resyncs to the next
/// blank line.
/// </para>
/// <para>
/// Header field semantics:
/// <list type="bullet">
///   <item><c>flags</c> = 4-hex-digit bitfield, see <see cref="MessageFlags"/>. Bit <c>0x0800</c> is reserved/unmapped in the legacy format; preserve verbatim via <see cref="MessageRecord.RawFlagsHex"/>.</item>
///   <item><c>action</c> = single decimal digit 0–6, see <see cref="MessageAction"/>.</item>
///   <item><c>response</c> = command string, may contain colons. Multiple commands are separated by literal <c>^M</c> or raw CR.</item>
/// </list>
/// </para>
/// <para>
/// Algorithm is a faithful port of the MudProxy <c>GameMessageImporter</c>
/// state machine; the per-record state names + resync rules are the
/// same. Vendored / re-implemented in modern C# so we don't take a
/// runtime dep on MudProxy.
/// </para>
/// </remarks>
public static class MegaMudMessagesImporter
{
    /// <summary>
    /// Header-shape probe — <c>name:HEX4:DIGIT:</c>. ANY line matching
    /// this is treated as a new record header, even mid-record. The
    /// trailing colon is required (the response field always follows,
    /// even when empty).
    /// </summary>
    private static readonly Regex HeaderShapeRegex = new(
        @"^[^:]+:[0-9a-fA-F]{4}:[0-9]:",
        RegexOptions.Compiled);

    /// <summary>Parse a <c>messages.md</c> file from disk.</summary>
    public static MessageImportResult Parse(string filePath)
        => ParseText(File.ReadAllText(filePath), filePath);

    /// <summary>Parse a <c>messages.md</c> file from in-memory text.</summary>
    public static MessageImportResult ParseText(string content, string sourcePath)
    {
        MessageImportResult result = new() { SourcePath = sourcePath };
        // Split on CRLF / LF. Bare CRs inside a line stay intact so
        // response parsing can still see them as command separators.
        string[] lines = content.Replace("\r\n", "\n").Split('\n');

        State state = State.ExpectHeader;
        Partial? current = null;
        int headerLineNumber = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int lineNo = i + 1;

            switch (state)
            {
                case State.ExpectHeader:
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    current = TryParseHeader(line, lineNo, result);
                    if (current is null)
                    {
                        // Resync: advance past the next blank line so a
                        // single bad record doesn't cascade.
                        while (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]))
                            i++;
                        continue;
                    }
                    headerLineNumber = lineNo;
                    state = State.ExpectMessage;
                    break;

                case State.ExpectMessage:
                    // Edge case: a header with no message line. Treat
                    // the next-header-shaped line as a new record and
                    // finalize this one with empty Message/EndsWith.
                    if (LooksLikeHeader(line))
                    {
                        Finalize(current!, message: "", endsWith: "", result);
                        current = null;
                        i--;
                        state = State.ExpectHeader;
                        break;
                    }
                    current!.Message = line;
                    state = State.ExpectEndsWith;
                    break;

                case State.ExpectEndsWith:
                    // Legacy authors often omit ends-with when there's
                    // no end pattern. If the next line looks like a
                    // header, finalize with empty EndsWith and reprocess.
                    if (LooksLikeHeader(line))
                    {
                        Finalize(current!, current!.Message, endsWith: "", result);
                        current = null;
                        i--;
                        state = State.ExpectHeader;
                        break;
                    }
                    Finalize(current!, current!.Message, endsWith: line, result);
                    current = null;
                    state = State.ExpectBlank;
                    break;

                case State.ExpectBlank:
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        state = State.ExpectHeader;
                    }
                    else
                    {
                        // Missing blank separator — reprocess this line
                        // as the next header (ExpectHeader validates
                        // gracefully if it isn't actually one).
                        i--;
                        state = State.ExpectHeader;
                    }
                    break;
            }
        }

        // EOF handling:
        //   ExpectEndsWith: legacy 2-line record at EOF — finalize with empty EndsWith.
        //   ExpectMessage: header with no message line — genuinely truncated, fail.
        if (state == State.ExpectEndsWith && current is not null)
        {
            Finalize(current, current.Message, endsWith: "", result);
        }
        else if (state == State.ExpectMessage)
        {
            result.Failures.Add(new MessageImportFailure
            {
                LineNumber = headerLineNumber,
                RawLine    = string.Empty,
                Reason     = "Truncated record — missing message line at EOF.",
            });
        }

        return result;
    }

    // ----- internals --------------------------------------------------------

    private static bool LooksLikeHeader(string line) => HeaderShapeRegex.IsMatch(line);

    private static Partial? TryParseHeader(string line, int lineNo, MessageImportResult result)
    {
        // Split on ':' max 4 — response field may contain colons.
        string[] parts = line.Split(':', 4);
        if (parts.Length < 3)
        {
            result.Failures.Add(new MessageImportFailure
            {
                LineNumber = lineNo, RawLine = line,
                Reason = "Header missing required fields (expected name:flags:action[:response]).",
            });
            return null;
        }

        string name       = parts[0];
        string flagsStr   = parts[1];
        string actionStr  = parts[2];
        string responseStr = parts.Length >= 4 ? parts[3] : string.Empty;

        if (!ushort.TryParse(flagsStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort rawFlags))
        {
            result.Failures.Add(new MessageImportFailure
            {
                LineNumber = lineNo, RawLine = line,
                Reason = $"Invalid hex flags field '{flagsStr}'.",
            });
            return null;
        }

        if (!int.TryParse(actionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int actionInt)
            || actionInt < 0 || actionInt > 6)
        {
            result.Failures.Add(new MessageImportFailure
            {
                LineNumber = lineNo, RawLine = line,
                Reason = $"Invalid action field '{actionStr}' (expected 0–6).",
            });
            return null;
        }

        return new Partial
        {
            Name             = name,
            RawFlagsHex      = rawFlags,
            Action           = (MessageAction)actionInt,
            ResponseCommands = ParseResponseCommands(responseStr),
        };
    }

    private static void Finalize(Partial p, string message, string endsWith, MessageImportResult result)
    {
        string id = ComputeId(p.Name, message, endsWith);
        result.Messages.Add(new MessageRecord(
            Id:               id,
            Name:             p.Name,
            Message:          message,
            EndsWith:         endsWith,
            Action:           p.Action,
            Flags:            MaskKnown(p.RawFlagsHex),
            RawFlagsHex:      p.RawFlagsHex,
            ResponseCommands: p.ResponseCommands));
    }

    private static MessageFlags MaskKnown(ushort raw)
    {
        const ushort knownMask =
            (ushort)MessageFlags.Blinded             | (ushort)MessageFlags.Confused            |
            (ushort)MessageFlags.Poisoned            | (ushort)MessageFlags.LosingHp            |
            (ushort)MessageFlags.MovementPrevented   | (ushort)MessageFlags.AttackPrevented     |
            (ushort)MessageFlags.Diseased            | (ushort)MessageFlags.HpRegenerating      |
            (ushort)MessageFlags.FindInConversations | (ushort)MessageFlags.ManaRegenerating    |
            (ushort)MessageFlags.FindInText          | (ushort)MessageFlags.EndsCombat          |
            (ushort)MessageFlags.LastActionFailed    | (ushort)MessageFlags.UseWhenChasing      |
            (ushort)MessageFlags.Disabled;
        return (MessageFlags)(raw & knownMask);
    }

    private static IReadOnlyList<string> ParseResponseCommands(string response)
    {
        if (string.IsNullOrEmpty(response)) return Array.Empty<string>();
        // Legacy files may encode the separator as the literal two-char
        // sequence "^M" or as a raw CR. Normalize both to CR, then split.
        string normalized = response.Replace("^M", "\r");
        string[] parts = normalized.Split('\r');
        List<string> result = new(parts.Length);
        foreach (string part in parts)
        {
            string trimmed = part.TrimEnd();
            if (trimmed.Length > 0) result.Add(trimmed);
        }
        return result;
    }

    /// <summary>
    /// Stable content hash used as <see cref="MessageRecord.Id"/>.
    /// SHA1 of <c>name|message|endsWith</c>, truncated to 16 lowercase hex chars.
    /// </summary>
    public static string ComputeId(string name, string message, string endsWith)
    {
        byte[] buf = Encoding.UTF8.GetBytes($"{name}|{message}|{endsWith}");
        byte[] hash = SHA1.HashData(buf);
        StringBuilder sb = new(16);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private enum State { ExpectHeader, ExpectMessage, ExpectEndsWith, ExpectBlank }

    private sealed class Partial
    {
        public string Name = "";
        public ushort RawFlagsHex;
        public MessageAction Action;
        public IReadOnlyList<string> ResponseCommands = Array.Empty<string>();
        public string Message = "";
    }
}

/// <summary>Outcome of one <see cref="MegaMudMessagesImporter.Parse"/> call.</summary>
public sealed class MessageImportResult
{
    public string SourcePath { get; set; } = string.Empty;
    public List<MessageRecord> Messages { get; set; } = new();
    public List<MessageImportFailure> Failures { get; set; } = new();
}

/// <summary>One per-record import failure (skipped, importer continues).</summary>
public sealed class MessageImportFailure
{
    public int LineNumber { get; set; }
    public string RawLine { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
