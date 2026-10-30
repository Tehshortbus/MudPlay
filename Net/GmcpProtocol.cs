using System.Text;
using System.Text.Json;

namespace MudPlay.Net;

// GMCP framing (telnet option 201): a body is "<package> <json>", sent between IAC SB
// 201 and IAC SE with any 255 byte doubled. The body limits are the contract's.
internal static class GmcpProtocol
{
    public const string ContractVersion = "1.2.0";
    public const int MaxOutgoingBodyBytes = 8192;
    public const int MaxIncomingBodyBytes = 1024 * 1024;
    public static readonly string[] Supports =
    [
        "Anomaly.Char 1", "Anomaly.Room 1", "Anomaly.Mob 1", "Anomaly.Items 1",
        "Anomaly.Shop 1", "Anomaly.Spells 1", "Anomaly.Group 1", "Anomaly.Comm 1", "Anomaly.Quests 1",
    ];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Frame(string package, object payload)
    {
        byte[] body = Utf8.GetBytes(package + " " + JsonSerializer.Serialize(payload));
        if (body.Length > MaxOutgoingBodyBytes)
            throw new ArgumentException("GMCP message exceeds the body limit.", nameof(payload));
        List<byte> frame = [255, 250, 201];
        foreach (byte value in body)
        {
            frame.Add(value);
            if (value == 255) frame.Add(value);
        }
        frame.AddRange([255, 240]);
        return frame.ToArray();
    }

    public static GmcpMessage Parse(ReadOnlySpan<byte> body)
    {
        string value = Utf8.GetString(body);
        int separator = value.IndexOf(' ');
        if (separator <= 0) throw new JsonException("GMCP package and JSON payload required.");
        string package = value[..separator];
        if (package.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.'))
            throw new JsonException("Invalid GMCP package name.");
        using JsonDocument document = JsonDocument.Parse(value[(separator + 1)..],
            new JsonDocumentOptions { MaxDepth = 32 });
        return new(package, document.RootElement.Clone());
    }
}
