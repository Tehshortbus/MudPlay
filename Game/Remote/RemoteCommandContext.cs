namespace FujinTerm.Game.Remote;

// Per-invocation context handed to each RemoteCommandManager handler. Carries
// the sender / parsed args / channel + a Reply callback that routes back through
// the same channel the command arrived on (telepath in → telepath reply, etc.).
//
// Handlers never talk to Net.TelnetClient directly — they call Reply for
// chat-channel responses or any other service-mediated path. Keeping the handler
// ignorant of the wire lets the engine swap reply formatting per channel without
// every handler having to know the wire-syntax for each chat type.
//
// Args is the post-command tokens split on whitespace. For @goto Newhaven Cabin
// the args are ["Newhaven", "Cabin"]; for @party rest the args are ["rest"] and
// the @party <sub> handler inspects args[0] to route.
//
//   Sender          — player name as classified by ChatRouter.
//   Command         — the @-prefixed command verbatim (lowercased; e.g. "@health").
//   Args            — post-command tokens split on whitespace; empty when the
//                     command stands alone.
//   OriginalMessage — the full chat-message text after the speaker prefix (e.g.
//                     "@goto Newhaven Cabin").
//   Channel         — which channel the command arrived on; determines where
//                     Reply routes.
//   Reply           — routes Reply(text) back through Channel. Engine-supplied —
//                     handlers shouldn't construct their own.
public readonly record struct RemoteCommandContext(
    string Sender,
    string Command,
    IReadOnlyList<string> Args,
    string OriginalMessage,
    RemoteChannel Channel,
    Action<string> Reply);
