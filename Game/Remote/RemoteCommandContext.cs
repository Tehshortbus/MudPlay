namespace FujinTerm.Game.Remote;

/// <summary>
/// Per-invocation context handed to each <see cref="RemoteCommandManager"/>
/// handler. Carries the sender / parsed args / channel + a
/// <see cref="Reply"/> callback that routes back through the same channel
/// the command arrived on (telepath in → telepath reply, etc.).
/// </summary>
/// <remarks>
/// <para>
/// Handlers never talk to <see cref="Net.TelnetClient"/> directly — they
/// call <see cref="Reply"/> for chat-channel responses or any
/// other service-mediated path. Keeping the handler ignorant of the wire
/// lets the engine swap reply formatting per channel without every
/// handler having to know the wire-syntax for each chat type.
/// </para>
/// <para>
/// <see cref="Args"/> is the post-command tokens split on whitespace. For
/// <c>@goto Newhaven Cabin</c> the args are <c>["Newhaven", "Cabin"]</c>;
/// for <c>@party rest</c> the args are <c>["rest"]</c> and the
/// <c>@party &lt;sub&gt;</c> handler inspects <c>args[0]</c> to route.
/// </para>
/// </remarks>
/// <param name="Sender">Player name as classified by <see cref="ChatRouter"/>.</param>
/// <param name="Command">The @-prefixed command verbatim (lowercased; e.g. <c>"@health"</c>).</param>
/// <param name="Args">Post-command tokens split on whitespace; empty when the command stands alone.</param>
/// <param name="OriginalMessage">The full chat-message text after the speaker prefix (e.g. <c>"@goto Newhaven Cabin"</c>).</param>
/// <param name="Channel">Which channel the command arrived on; determines where <see cref="Reply"/> routes.</param>
/// <param name="Reply">
/// Routes <paramref name="Reply"/>(text) back through <see cref="Channel"/>.
/// Engine-supplied — handlers shouldn't construct their own.
/// </param>
public readonly record struct RemoteCommandContext(
    string Sender,
    string Command,
    IReadOnlyList<string> Args,
    string OriginalMessage,
    RemoteChannel Channel,
    Action<string> Reply);
