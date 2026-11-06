using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// The @dupe <player> command (SysopCommands, i.e. Elevated Commands). Lets a trusted
// player bring an alt up to speed with one telepath: it copies the sender's QUERY,
// @roomba, and @quest permissions onto the named player. It rewrites who is trusted,
// which is why only Elevated senders may use it, and why it is fenced in three ways.
//
//   - Narrow. Only the categories in Shareable move. Nothing that acts on the
//     character (move, @do, settings, invites, hangup, divert) and never Elevated
//     Commands, so a duplicated player can't dupe onward and anything beyond queries
//     stays a manual step in the Players tab.
//   - Once. A sender may @dupe a single time. The spent use is stored on their player
//     record and stays spent until the user resets it in the edit dialog, so it can't
//     be re-armed remotely. The use is spent only by a grant that actually happened:
//     a refusal or a no-op leaves it available.
//   - Logged. Every use and every refusal is written to the program log, and the
//     record keeps who was duplicated onto and when.
//
// The copy is additive: the target keeps what it holds and only gains, so @dupe can
// never take a permission away. Only the permission grid moves; the target's party
// toggles and notes stay.
//
// Two more refusals protect the user. The local character is refused because the
// Players tab hides its row and permissions granted to yourself mean nothing. A name
// the client has never seen is refused because a typo would otherwise pre-grant trust
// to whoever later takes that name.
//
// The telepath / gangpath restriction is enforced by the engine through
// RemoteCommandCatalog.IsPathChannelOnly, before this handler runs.
public sealed class DupeHandler : IDisposable
{
    // The read-only query categories plus @roomba (QueryItemLocation), @quest
    // (QueryQuests) and @spells (QuerySpells). Widening this widens what any Elevated player can hand out
    // without the user's say-so.
    internal const PlayerRemoteControls Shareable =
        PlayerRemoteControls.QueryVersion
        | PlayerRemoteControls.QueryExperience
        | PlayerRemoteControls.QueryHealthStatus
        | PlayerRemoteControls.QueryLocation
        | PlayerRemoteControls.QueryInventory
        | PlayerRemoteControls.QueryBossTimers
        | PlayerRemoteControls.QueryDeaths
        | PlayerRemoteControls.QueryItemLocation
        | PlayerRemoteControls.QueryQuests
        | PlayerRemoteControls.QuerySpells;

    private readonly RemoteCommandManager _engine;
    private readonly PlayerDatabase _players;
    private readonly Func<string?> _selfNameProvider;
    private readonly LogService? _log;
    private bool _disposed;

    public DupeHandler(
        RemoteCommandManager engine,
        PlayerDatabase players,
        Func<string?> selfNameProvider,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(selfNameProvider);
        _engine = engine;
        _players = players;
        _selfNameProvider = selfNameProvider;
        _log = log;

        if (!RemoteCommandCatalog.TryGetCategory("@dupe", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@dupe'.");
        _engine.RegisterHandler("@dupe", category, OnDupe);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@dupe");
    }

    private void OnDupe(RemoteCommandContext ctx)
    {
        if (ctx.Args.Count != 1)
        {
            Refuse(ctx, "usage: @dupe <player>");
            return;
        }
        string target = ctx.Args[0];

        PlayerRecord? source = _players.Find(ctx.Sender);
        if (source is null)
        {
            Refuse(ctx, "your player record wasn't found");
            return;
        }
        if (source.DupeUsedAtUtc is not null)
        {
            Refuse(ctx, "your @dupe has already been used");
            return;
        }
        if (SameGivenName(target, ctx.Sender))
        {
            Refuse(ctx, "you already have your own permissions");
            return;
        }
        string? self = _selfNameProvider();
        if (!string.IsNullOrEmpty(self) && SameGivenName(target, self))
        {
            Refuse(ctx, "can't change my own permissions");
            return;
        }
        PlayerRecord? dest = _players.Find(target);
        if (dest is null)
        {
            Refuse(ctx, $"unknown player {target}");
            return;
        }

        PlayerRemoteControls shareable = source.RemoteControls & Shareable;
        if (shareable == PlayerRemoteControls.None)
        {
            Refuse(ctx, "you have no query, roomba, or quest permissions to copy");
            return;
        }
        PlayerRemoteControls gained = shareable & ~dest.RemoteControls;
        if (gained == PlayerRemoteControls.None)
        {
            ctx.Reply($"{dest.GivenName} already has all your query, roomba, and quest permissions");
            return;
        }

        // Spend the sender's use BEFORE granting. Both writes are synchronous, so a
        // failure between them can only cost the sender their use; the other order
        // could hand out a grant that was never counted.
        _players.EditCustomization(source.GivenName, source.ToCustomization() with
        {
            DupeUsedAtUtc = DateTime.UtcNow,
            DupedPlayer   = dest.GivenName,
        });
        PlayerRemoteControls merged = dest.RemoteControls | shareable;
        _players.EditCustomization(dest.GivenName, dest.ToCustomization() with { RemoteControls = merged });

        _log?.Log(LogSeverity.Info, "RemoteCmd",
            $"@dupe from {ctx.Sender} onto {dest.GivenName}: granted {gained} (now {merged}). "
            + $"{ctx.Sender}'s @dupe is spent until reset in the Players tab.");
        ctx.Reply($"{dest.GivenName} now has your query, roomba, and quest permissions");
    }

    // Failure replies are refusals (remote-command reply policy); the log line is
    // written either way so the refusal is visible to the user.
    private void Refuse(RemoteCommandContext ctx, string reason)
    {
        _log?.Log(LogSeverity.Info, "RemoteCmd", $"@dupe from {ctx.Sender} refused: {reason}.");
        ctx.Refuse(reason);
    }

    // Telepaths carry the given name only while player rows may hold "Given Family",
    // so names are compared on the given name — the same key PlayerDatabase uses.
    private static bool SameGivenName(string a, string b) =>
        PlayerObservation.SplitName(a).Given.Equals(
            PlayerObservation.SplitName(b).Given, StringComparison.OrdinalIgnoreCase);
}
