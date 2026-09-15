using System.Linq;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Remote;

// Read-only handler for `@token` — with no argument it lists every held token's
// remaining daily charges; with a name it reports just that one ("token of
// Arlysia: 5 uses remaining"). Reads the TokenTracker's last look; never touches
// the wire. Gated as an inventory query (RemoteCommandCatalog). A party member
// uses it before a `@party use token` to see whether the leader still has
// charges; the sender can use the token itself with `@do use <token>`, so there's
// no separate use command.
public sealed class TokenQueryHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@token" };

    private readonly RemoteCommandManager _engine;
    private readonly Tokens.TokenTracker _tokens;
    private bool _disposed;

    public TokenQueryHandler(RemoteCommandManager engine, Tokens.TokenTracker tokens)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(tokens);
        _engine = engine;
        _tokens = tokens;
        Register("@token", OnToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void Register(string command, Action<RemoteCommandContext> handler)
    {
        if (!RemoteCommandCatalog.TryGetCategory(command, out PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(command, category, handler);
    }

    private void OnToken(RemoteCommandContext ctx)
    {
        string query = string.Join(' ', ctx.Args).Trim();

        // Bare @token — report every token whose charges we've read this session.
        if (query.Length == 0)
        {
            IReadOnlyList<Tokens.TokenTracker.TokenCharge> all = _tokens.KnownCharges();
            ctx.Reply(all.Count == 0
                ? "no token charges read yet (hold transport tokens and log in on Paradigm)"
                : "token charges — " + string.Join(", ", all.Select(c => $"{c.Place}: {c.Remaining}")));
            return;
        }

        string place = Tokens.TokenCatalog.PlaceOf(query) ?? query;
        if (_tokens.ChargesFor(query) is { } n)
            ctx.Reply($"token of {place}: {n} use(s) remaining");
        else
            ctx.Reply($"no charge count for a token of {place} — not held, or not read yet");
    }
}
