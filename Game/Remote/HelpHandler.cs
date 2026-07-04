using System.Text;
using FujinTerm.Models.GameData;

namespace FujinTerm.Game.Remote;

// @help (QueryVersion). Replies to the sender with the flat list of remote
// commands they're permitted to issue, derived from their merged per-player
// permission grant via RemoteCommandManager.GetPermittedCommands.
//
// Party-whitelist commands (@wait / @ok / @comeback / @share) are excluded —
// those aren't permission-gated, so they don't belong in a per-permission help
// list. The list is split across multiple telepath replies when it exceeds
// MaxPayloadChars so no single reply risks server-side truncation on a long
// input line. Replies use ctx.Reply, so the engine handles the wire format and
// channel routing — no separate wire-sender needed.
public sealed class HelpHandler : IDisposable
{
    // Max characters of bare list text per telepath chunk, before the engine
    // wraps it in { } and prefixes the recipient. Keeps each reply comfortably
    // inside a MajorMUD input line.
    private const int MaxPayloadChars = 200;

    private readonly RemoteCommandManager _engine;
    private bool _disposed;

    public HelpHandler(RemoteCommandManager engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        if (!RemoteCommandCatalog.TryGetCategory("@help", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@help'.");
        _engine.RegisterHandler("@help", category, OnHelp);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@help");
    }

    private void OnHelp(RemoteCommandContext ctx)
    {
        IReadOnlyList<string> commands = _engine.GetPermittedCommands(ctx.Sender);
        foreach (string chunk in Chunk(commands, MaxPayloadChars))
            ctx.Reply(chunk);
    }

    // Greedily pack items into comma-separated chunks no longer than maxLen. A
    // single item longer than the limit becomes its own (over-length) chunk — no
    // catalog command name approaches the cap, so this only matters as a safety
    // net.
    private static IEnumerable<string> Chunk(IReadOnlyList<string> items, int maxLen)
    {
        StringBuilder sb = new();
        foreach (string item in items)
        {
            int projected = sb.Length == 0 ? item.Length : sb.Length + 2 + item.Length;
            if (sb.Length > 0 && projected > maxLen)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(item);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
