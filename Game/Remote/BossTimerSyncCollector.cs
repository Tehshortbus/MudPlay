using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// What a responder's set is to the requests the user made while collecting.
public enum TimerSyncStanding
{
    // No answer to a request of the user's: it came on a channel only the client
    // asked on, for another player's remote command, or by telepath from a player
    // the user did not ask. Nothing of it is written without a pick.
    NotAsked,
    // An answer to a request of the user's that several players can answer, or
    // did: on gangpath, say or the broadcast channel, or by telepath when more than
    // one player was asked or a shared channel is being listened on as well.
    OneOfSeveral,
    // The answer of the one player the user telepathed, with nothing else asked.
    TheOneAsked,
}

// One responder's fully-reassembled, decoded timer set, the channel it came by, and
// what it is to the requests the user made.
public readonly record struct BossTimerSyncResponse(
    string Sender, IReadOnlyList<BossTimerSyncRecord> Records, RemoteChannel Channel, TimerSyncStanding Standing);

// Requester side of @timer sync: scrapes the `@timerdata <i>/<n> <blob>` reply lines off
// ChatRouter (they ride the chat as ordinary text — the remote engine ignores the
// leading @timerdata, see AppServices RegisterIgnored), reassembles each sender's chunks
// in order, decodes, and raises ResponseReceived once a sender's set is complete.
// Correlation is by responder name (every reply line carries it), so a shared gang/local
// channel just yields one set per responder — no request token needed. Collection is
// transient: Begin on send, Dispose when the merge window closes.
//
// Data is taken only on a channel a request of ours went out on. Nothing here turns
// a responder away for who they are (answering at all is their grant to us), so a
// channel we did not ask on is a way in for anyone who can reach us there while the
// window is open: a request to the gang must not let a line on the public broadcast
// channel in. What a set is taken as is told with it: the answer of the one player a
// telepath asked, or one of several that may come (BossTimerSyncResponse).
public sealed class BossTimerSyncCollector : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly LogService? _log;
    private readonly Func<bool> _broadcastOff;
    private readonly Func<string, bool> _isOurName;
    private bool _collecting;
    private bool _disposed;

    private readonly Dictionary<string, Partial> _partial = new(StringComparer.OrdinalIgnoreCase);

    // The channels our requests have gone out on while collecting, whoever they
    // were sent for: these are listened on. Of them, the shared ones the user
    // asked on; and the players a telepath of the user's reached, by given name.
    private readonly HashSet<RemoteChannel> _askedOn = new();
    private readonly HashSet<RemoteChannel> _usersAskedOn = new();
    private readonly HashSet<string> _telepathed = new(StringComparer.OrdinalIgnoreCase);

    public event Action<BossTimerSyncResponse>? ResponseReceived;

    // A request of ours went out, or a line of some responder's set came in. The
    // merge window waits for these to go quiet before it judges together the
    // answers of a request several players can answer.
    public event Action? Activity;

    // A request of the user's own went out. The merge window notes what timers it
    // holds at the first one: a timer of ours that changes after the user asked is
    // never written over as an answer arrives. Raised as the line goes out, when
    // the user pressed Enter or the button sent it, and again when the game shows
    // it (before Activity then): a kill of ours in between is a change after asking.
    public event Action? UserAsked;

    // A telepath request of the user's that was waiting for the game's answer has
    // had it: the game showed it sent, or it reached nobody.
    public event Action? TelepathRequestAnswered;

    // broadcastOff: the Talk tab's "Disallow @commands from broadcast". Ticked, the
    // broadcast channel is not listened to at all, for answers no more than for
    // requests: it is public, and off until the user opens it.
    // isOurName: whether a speaker is our own character. A shared channel can echo
    // our own lines back under our name: a request we typed, or data we sent in
    // answer to someone else's request while this window is open.
    public BossTimerSyncCollector(
        ChatRouter chat, LogService? log = null, Func<bool>? broadcastOff = null, Func<string, bool>? isOurName = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
        _log = log;
        _broadcastOff = broadcastOff ?? (static () => false);
        _isOurName = isOurName ?? (static _ => false);
        _chat.EntryClassified += Ingest;
        _chat.UsersLineSent += OnUsersLineSent;
        _chat.UnsentTelepathDropped += OnUnsentTelepathDropped;
    }

    // A request of the user's seen as it goes out, whether or not anything is being
    // collected yet: a window opened from the Bosses tab may be asked by a line
    // typed at the terminal. Where its answers would not be read, it asks nothing.
    private void OnUsersLineSent(string line)
    {
        if (AsksForTimerSync(line, _broadcastOff)) UserAsked?.Invoke();
    }

    // Whether a line as it went out asks for a timer sync on a channel whose
    // answers would be read: one a timer sync may go by, and not the broadcast
    // channel while it is switched off. A form whose channel is not recorded may
    // be any, and counts.
    public static bool AsksForTimerSync(string sentLine, Func<bool> broadcastOff)
    {
        if (TransferChannels.RequestIn(sentLine) is not { Transfer: Transfer.TimerSync, Channel: var channel }) return false;
        return channel is not { } on
               || (TransferChannels.Allows(Transfer.TimerSync, on) && !(on == RemoteChannel.Broadcast && broadcastOff()));
    }

    private static bool IsTimerSyncRequest(string sentLine) =>
        TransferChannels.RequestIn(sentLine) is { Transfer: Transfer.TimerSync };

    private void OnUnsentTelepathDropped(string line)
    {
        if (IsTimerSyncRequest(line)) TelepathRequestAnswered?.Invoke();
    }

    // True while a telepath request of the user's has gone out and the game has
    // not yet shown it sent, or said it reached nobody, for as long as a
    // confirmation can still come. Until then the one player already asked may
    // not stay the only one. Asked of the chat log's pending sends, not of what
    // this collector saw go out: a window opened by the first request's
    // confirmation came into being after a second request was already on its way.
    public bool AwaitingTelepathSent => _chat.UsersTelepathAwaiting(IsTimerSyncRequest);

    // A request of ours went out on askedOn. Its answers are taken from here on,
    // with those of every request made before it while collecting: a second
    // request on another channel adds that channel, and late answers to the first
    // are still read. Whether the request came from the window's button or was
    // typed by hand makes no difference; Ingest tells of the typed ones.
    //
    // byUser: the request was the user's own, not one the client sent for another
    // player's remote command (a relay-back, a `@do`, a `@party` relay). The channel
    // is listened on either way; only an answer to a request of the user's is ever
    // written without a pick.
    //
    // telepathReached: the player a telepath of the user's reached, as the game's
    // own `--- Telepath Sent to <Name> ---` names them. Never the name as typed: a
    // typed name may be the first letters of a player's, the game says whose it
    // took them for, and a telepath whose letters fit two players reaches neither
    // (GAME_MECHANICS "Telepath throttle and per-telepath acknowledgement"). Until
    // the game has named who a telepath reached, nobody was asked by telepath.
    //
    // A responder whose set has been delivered may send again for the new request.
    // One whose set is half here keeps what has arrived.
    public void Begin(RemoteChannel askedOn, string? telepathReached = null, bool byUser = true)
    {
        _collecting = true;
        _askedOn.Add(askedOn);
        if (askedOn != RemoteChannel.Telepath)
        {
            if (byUser) _usersAskedOn.Add(askedOn);
        }
        else if (byUser && !string.IsNullOrWhiteSpace(telepathReached))
        {
            _telepathed.Add(RemoteCommandManager.GivenName(telepathReached.Trim()));
        }
        foreach (string delivered in _partial.Where(p => p.Value.Done).Select(p => p.Key).ToList())
            _partial.Remove(delivered);
        if (byUser) UserAsked?.Invoke();
        Activity?.Invoke();
    }

    // True while the one thing asked is a telepath, of one player the game has
    // named: the case in which that player's answer replaces a timer of ours that
    // differs, with no pick.
    public bool OnePlayerAsked =>
        _askedOn.Count == 1 && _askedOn.Contains(RemoteChannel.Telepath) && _telepathed.Count == 1;

    // True while a request of the user's is out that several players can answer:
    // one on a shared channel, or telepaths to players none of whom is the one
    // player asked. Their answers are gathered and judged together.
    public bool SeveralMayAnswer => _usersAskedOn.Count > 0 || (_telepathed.Count > 0 && !OnePlayerAsked);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= Ingest;
        _chat.UsersLineSent -= OnUsersLineSent;
        _chat.UnsentTelepathDropped -= OnUnsentTelepathDropped;
    }

    private sealed class Partial
    {
        public required int N;
        public required string?[] Chunks;
        public bool Done;
        // A piece of the set came on the broadcast channel.
        public bool OnBroadcast;
    }

    private void Ingest(ChatLogEntry e)
    {
        // A request of ours seen going out: one typed by hand while the window is
        // open, or the button's own. A telepath's echo names who it went to. Taken
        // before anything has been asked in the window too: an open window opens
        // no second one, so a request typed into a window opened from the Bosses
        // tab is this window's to collect.
        if (TransferChannels.OwnRequest(e, _isOurName) is { Transfer: Transfer.TimerSync } own)
        {
            if (!TransferChannels.Allows(Transfer.TimerSync, own.Channel)) return;
            // Nor while the broadcast channel is switched off: every answer there
            // would be passed over.
            if (own.Channel == RemoteChannel.Broadcast && _broadcastOff()) return;
            // Whose request it was is asked of the one place that knows, the lines
            // the user sent as they went out (TransferChannels.IsUsersOwn): for a
            // telepath, its confirmation is of a send of the user's to the player
            // it names; for a shared channel, the user sent these words there just
            // now. One the client sent for another player (a relay-back, a `@do`)
            // asks nobody on the user's behalf: its channel is listened on, and
            // nothing that answers it is written without a pick.
            bool byUser = TransferChannels.IsUsersOwn(e, own.Channel, _chat);
            Begin(own.Channel, own.Channel == RemoteChannel.Telepath ? e.Speaker : null, byUser);
            if (own.Channel == RemoteChannel.Telepath) TelepathRequestAnswered?.Invoke();
            return;
        }
        if (!_collecting) return;

        if (TransferChannels.Of(e.Channel) is not { } heardOn || !AnswersAreTakenOn(heardOn)) return;
        if (heardOn == RemoteChannel.Broadcast && _broadcastOff()) return;
        if (e.Speaker is not { Length: > 0 } sender) return;
        // Our own data, echoed back to us by the channel, is no one's answer.
        if (_isOurName(sender)) return;
        if (!TryParse(e.Message, out int index, out int count, out string blob)) return;

        if (!_partial.TryGetValue(sender, out Partial? p) || p.N != count)
        {
            p = new Partial { N = count, Chunks = new string?[count] };
            _partial[sender] = p;
        }
        if (p.Done) return;                 // already delivered this sender's set
        p.Chunks[index - 1] = blob;
        p.OnBroadcast |= heardOn == RemoteChannel.Broadcast;
        _log?.Debug("BossTimerSync", $"received line {index}/{count} from {sender} ({blob.Length} chars)");
        Activity?.Invoke();
        if (p.Chunks.Any(c => c is null)) return;   // still missing pieces

        p.Done = true;
        IReadOnlyList<BossTimerSyncRecord> records;
        try
        {
            records = BossTimerSyncCodec.Decode(string.Concat(p.Chunks));
        }
        catch (FormatException ex)
        {
            _log?.Warn("BossTimerSync", $"discarding malformed timer-sync payload from {sender}: {ex.Message}");
            return;
        }
        _log?.Info("BossTimerSync",
            $"received {records.Count} timer(s) from {sender} in {p.N} line(s): {Describe(records)}");
        // A set is as public as its most public piece: one line of it on the
        // broadcast channel, and it is a set heard there, wherever the last came.
        RemoteChannel cameBy = p.OnBroadcast ? RemoteChannel.Broadcast : heardOn;
        ResponseReceived?.Invoke(new BossTimerSyncResponse(sender, records, cameBy, StandingOf(cameBy, sender)));
    }

    // An answer comes back on the channel it was asked on (RemoteCommandManager
    // answers there), with one exception: a responder who is sneaking answers a said
    // request by telepath, since speaking aloud would end the sneak. So telepath is
    // taken after a said request too. Gangpath and broadcast end no sneak and are
    // never rerouted (GAME_MECHANICS "Channel broadcast").
    private bool AnswersAreTakenOn(RemoteChannel heardOn) =>
        _askedOn.Contains(heardOn)
        || (heardOn == RemoteChannel.Telepath && _askedOn.Contains(RemoteChannel.Local));

    // What a set is to the requests the user made (TimerSyncStanding), as things
    // stand when it is complete.
    //
    // On a shared channel it answers the user's request there, or nobody's. By
    // telepath it is the asked player's own answer when that player is the only
    // thing asked; one of several when more players were asked, when a shared
    // channel is listened on as well, or when the user said the request aloud (a
    // sneaking client answers a say by telepath, and anyone in the room may be
    // that client); and no answer at all from a player the user did not ask.
    private TimerSyncStanding StandingOf(RemoteChannel heardOn, string sender)
    {
        if (heardOn != RemoteChannel.Telepath)
            return _usersAskedOn.Contains(heardOn) ? TimerSyncStanding.OneOfSeveral : TimerSyncStanding.NotAsked;
        bool asked = _telepathed.Contains(RemoteCommandManager.GivenName(sender));
        if (asked && OnePlayerAsked) return TimerSyncStanding.TheOneAsked;
        return asked || _usersAskedOn.Contains(RemoteChannel.Local)
            ? TimerSyncStanding.OneOfSeveral
            : TimerSyncStanding.NotAsked;
    }

    // Parse "@timerdata <i>/<n> <blob>", tolerating the {} the remote reply path wraps a
    // reply in. The blob is a single spaceless base64url token, so a 3-way split keeps it
    // whole.
    private static bool TryParse(string message, out int index, out int count, out string blob)
    {
        index = count = 0; blob = string.Empty;
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();

        string[] parts = body.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!parts[0].Equals(BossTimerQueryHandler.SyncResponseToken, StringComparison.OrdinalIgnoreCase)) return false;

        string[] frac = parts[1].Split('/');
        if (frac.Length != 2 || !int.TryParse(frac[0], out index) || !int.TryParse(frac[1], out count)) return false;
        if (index < 1 || count < 1 || index > count) return false;
        blob = parts[2];
        return true;
    }

    // Compact one-line summary of a decoded set for the program log.
    private static string Describe(IReadOnlyList<BossTimerSyncRecord> records)
        => records.Count == 0 ? "(no active timers)" : string.Join(", ", records.Select(r => r.Describe()));
}
