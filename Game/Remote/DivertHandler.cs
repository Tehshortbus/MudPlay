using System.Text;
using FujinTerm.Models.GameData;

namespace FujinTerm.Game.Remote;

// The @divert command (DivertConversations permission category). While
// diverting, every incoming telepath is repeated to a chosen target player so an
// away-from-keyboard handler can read the user's mail.
//
// @divert <player> starts diverting and replies {Now diverting telepaths to:
// <player>}. Bare @divert stops and replies {No longer diverting telepaths}. The
// reply routes back on the channel the command arrived on (engine wraps it in
// { }); the forwarded telepaths are plain conversation —
// <sender> telepathed: <message> — sent verbatim with no braces.
//
// Forwarding subscribes to ChatRouter.EntryClassified and fires only for
// TelepathIncoming entries.
//   - The divert target's OWN telepaths are skipped — forwarding a telepath back
//     to the person it'd be diverted to is a pointless echo and risks a loop if
//     they also divert.
//   - Telepaths that are themselves @-commands (e.g. @where) ARE forwarded — the
//     local engine still processes them, the forward is just an extra copy. The
//     sole exception is the @divert control verb itself: the engine subscribes to
//     ChatRouter.EntryClassified before this handler, so the telepath that just
//     flipped divert state would otherwise be forwarded to the new target.
//     Skipping any @divert message drops that leak independently of subscription
//     order.
public sealed class DivertHandler : IDisposable
{
    private readonly RemoteCommandManager _engine;
    private readonly ChatRouter _chat;
    private Action<byte[]>? _wireSender;
    // Given name of the current divert target, or null when not diverting.
    private string? _target;
    private bool _disposed;

    // Test seam — current divert target given name (or null).
    internal string? TargetForTests => _target;

    // Test-visible record of every forwarded wire payload.
    internal List<byte[]> LastSentForTests { get; } = new();

    public DivertHandler(RemoteCommandManager engine, ChatRouter chat)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(chat);
        _engine = engine;
        _chat = chat;

        if (!RemoteCommandCatalog.TryGetCategory("@divert", out PlayerRemoteControls category))
            throw new InvalidOperationException("RemoteCommandCatalog missing entry for '@divert'.");
        _engine.RegisterHandler("@divert", category, OnDivert);
        _chat.EntryClassified += OnChatEntry;
    }

    // Bind the wire-sender — same shape as the other connection-affecting
    // handlers. MainWindowViewModel supplies the gate-wrapped SendUserInput.
    // Without it the command still authorises and toggles divert state (and
    // replies), but no telepaths are forwarded.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= OnChatEntry;
        _engine.UnregisterHandler("@divert");
    }

    private void OnDivert(RemoteCommandContext ctx)
    {
        if (ctx.Args.Count == 0)
        {
            _target = null;
            ctx.Reply("No longer diverting telepaths");
            return;
        }

        // Telepath recipients are a single given name; ignore any extra
        // tokens. Store the raw token for both the wire prefix and reply.
        string target = ctx.Args[0];
        _target = target;
        ctx.Reply($"Now diverting telepaths to: {target}");
    }

    private void OnChatEntry(ChatLogEntry entry)
    {
        if (_target is null) return;
        if (entry.Channel != ChatChannel.TelepathIncoming) return;
        if (string.IsNullOrEmpty(entry.Speaker) || string.IsNullOrEmpty(entry.Message)) return;

        // Don't forward the @divert control verb itself (see class remarks).
        if (entry.Message.StartsWith("@divert", StringComparison.OrdinalIgnoreCase)) return;

        // Skip the divert target's own telepaths — no echo back to them.
        if (GivenName(entry.Speaker).Equals(GivenName(_target), StringComparison.OrdinalIgnoreCase))
            return;

        Forward(entry.Speaker, entry.Message);
    }

    private void Forward(string sender, string message)
    {
        if (_wireSender is null) return;
        string given = GivenName(_target!);
        string wire = $"/{given} {sender} telepathed: {message}";
        byte[] bytes = Encoding.Latin1.GetBytes(wire + "\r");
        LastSentForTests.Add(bytes);
        _wireSender(bytes);
    }

    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
