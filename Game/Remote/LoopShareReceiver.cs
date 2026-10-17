using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Requester side of `@loop send`: collects the `@loopdata <id> <i>/<n> <chunk>` lines
// a player sends after we answered their offer with `@loop send yes`, reassembles
// them (LoopShareCodec — a loop is only useful whole), and saves the loop to our
// catalogue.
//
// A loop goes by telepath only (TransferChannels): the offer, the data and our
// answer. Lines are adopted only inside a window our OWN telepathed `@loop send yes`
// opens (NoteSendConfirmed, from the outbound-chat watcher), and only from the
// player we said it to. A loop-send line heard on any other channel is an older
// client's and is passed over. The permission gate is on the sender, who only
// offered because they grant us @loop; we just confirm we asked.
//
// A received loop never overwrites one of ours, nor changes how we run one (user,
// 2026-10-11). It is compared by signature (LoopSignature: the route, every room's
// settings, and the two loop-wide settings, the sender's against this character's)
// with the loop we hold of that name and with the numbered copies saved beside it.
// The same as any of them: rejected, nothing saved or changed. Different from all:
// saved under the name when it is free, else as new under "<name> 2" (the next free
// number), and the sender's two loop settings become this character's for the loop
// saved alone.
//
// Before any of that the loop is checked against the code the sender put on its
// offer, and when the transfer is over the sender is told, once, what became of it
// (Tell): the two ends can see they hold the same loop.
public sealed class LoopShareReceiver : IDisposable
{
    // Long enough for a big loop's paced lines (~800ms apart) and a slow sender.
    private static readonly TimeSpan AcceptWindow = TimeSpan.FromMinutes(2);

    // A loop worth sending fits well under this; the cap bounds what a stream of
    // bogus lines can make us buffer.
    private const int MaxChunks = 100;

    private readonly ChatRouter _chat;
    private readonly LoopManager _loops;
    private readonly Action<string> _notice;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly Dictionary<(string Sender, string Id), string?[]> _partial = new();

    // One offer a player telepathed us: its code (null: it had none, as an older
    // client's has) and when.
    private readonly record struct Offer(string? Code, DateTimeOffset At);

    // Every offer each player made us lately, by given name, oldest first. All of
    // them, not the last: a second ask while the first loop is still arriving
    // brings a second offer, and the loop on its way must not be turned away as not
    // the one offered.
    private readonly Dictionary<string, List<Offer>> _offered = new(StringComparer.OrdinalIgnoreCase);

    // An offer lapses on the sender's side after two minutes and a transfer has
    // AcceptWindow to arrive; an offer older than both was for some earlier ask.
    private static readonly TimeSpan OfferKept = TimeSpan.FromMinutes(5);

    // What a talkative player, or many of them, can make us keep.
    private const int MaxOffersPerPlayer = 8;
    private const int MaxPlayersWithOffers = 32;

    private DateTimeOffset _acceptUntil = DateTimeOffset.MinValue;
    private string? _expectedSender;
    private bool _disposed;

    // Telepaths the sender our one answer when a transfer is over: their name, the
    // text, and whether it is a failure. False when it was held back (the master
    // switch, or the Warn setting for a failure): RemoteCommandManager.SendOutcome.
    private readonly Func<string, string, bool, bool>? _tell;

    public LoopShareReceiver(ChatRouter chat, LoopManager loops, Action<string> notice,
        LogService? log = null, Func<DateTimeOffset>? clock = null,
        Func<string, string, bool, bool>? tell = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(notice);
        _chat = chat;
        _loops = loops;
        _notice = notice;
        _log = log;
        _now = clock ?? (() => DateTimeOffset.Now);
        _tell = tell;
        _chat.EntryClassified += Ingest;
    }

    // Tell the sender what became of the loop, by telepath as it came, once. A
    // sender on this version shows it beside its own code for the loop, so both
    // players see the two ends hold the same loop, or that it has to be sent again
    // (user, 2026-10-11). An older sender's client shows the line and does nothing
    // with it.
    //
    // Every transfer that completes gets exactly one answer, the two that fail
    // before a loop is in hand included: lines that could not be put together, and
    // nowhere to save (decided 2026-10-11). With the master switch off the loop is
    // still taken, the user having typed the yes, and the sender is not told: with
    // that switch off nothing goes out by itself (decided 2026-10-11).
    private void Tell(string sender, string? code, string outcome, bool failed = false)
    {
        if (_tell is null) return;
        bool told = _tell(sender, LoopShareHandler.ResultLine(code, outcome), failed);
        _log?.Info("LoopShare", told
            ? $"told {sender}: {outcome}"
            : $"{sender} was not told ({outcome}): the master switch is off"
              + (failed ? ", or answers about a failed command are switched off in Settings → Talk" : string.Empty) + ".");
    }

    // We just telepathed `@loop send yes` to sender: their loop is taken, from them
    // alone, for the next two minutes. A yes on any other channel opens nothing
    // (the caller tells the user to use telepath): a loop goes by telepath only.
    public void NoteSendConfirmed(string sender)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sender);
        _acceptUntil = _now() + AcceptWindow;
        _expectedSender = sender;
        _partial.Clear();
    }

    private void RememberOffer(string sender, string? code)
    {
        DateTimeOffset now = _now();
        string given = Given(sender);
        if (!_offered.TryGetValue(given, out List<Offer>? offers)) _offered[given] = offers = new List<Offer>();
        offers.RemoveAll(o => now - o.At > OfferKept);
        offers.Add(new Offer(code, now));
        if (offers.Count > MaxOffersPerPlayer) offers.RemoveAt(0);

        if (_offered.Count <= MaxPlayersWithOffers) return;
        // Over the bound: the player whose newest offer is the oldest goes.
        string stalest = _offered.MinBy(p => p.Value[^1].At).Key;
        _offered.Remove(stalest);
    }

    private List<Offer> LiveOffers(string sender)
    {
        if (!_offered.TryGetValue(Given(sender), out List<Offer>? offers)) return new List<Offer>();
        DateTimeOffset now = _now();
        offers.RemoveAll(o => now - o.At > OfferKept);
        return offers;
    }

    // The code among that player's offers that the loop in hand answers to, taken
    // off the list: an offer answers one transfer. matched is false with offered
    // set when they offered codes and this loop has none of them; offered is null
    // when no offer of theirs carried a code.
    private bool TakeOfferFor(string sender, string signature, out string? offered)
    {
        List<Offer> offers = LiveOffers(sender);
        int match = offers.FindIndex(o => o.Code is { } code && signature.StartsWith(code, StringComparison.Ordinal));
        if (match >= 0)
        {
            offered = offers[match].Code;
            offers.RemoveAt(match);
            return true;
        }
        string[] codes = offers.Where(o => o.Code is not null).Select(o => o.Code!).Distinct().ToArray();
        if (codes.Length > 0)
        {
            offered = string.Join(" or ", codes);
            return false;
        }
        // An older sender's offer, with no code, is used up by the loop it led to.
        if (offers.Count > 0) offers.RemoveAt(0);
        offered = null;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= Ingest;
    }

    private void Ingest(ChatLogEntry e)
    {
        // By telepath or not at all: an offer or a data line on a shared channel is
        // an older client's, and neither is taken.
        if (!TransferChannels.Allows(Transfer.LoopSend, e.Channel)) return;
        if (e.Speaker is not { Length: > 0 } sender) return;
        if (LoopShareHandler.TryParseOffer(e.Message, out string? offeredCode))
        {
            // Kept whether or not we go on to say yes: the code is what the loop
            // that arrives is checked against.
            RememberOffer(sender, offeredCode);
            return;
        }
        if (!TryParse(e.Message, out string id, out int index, out int count, out string chunk)) return;
        if (_now() > _acceptUntil)
        {
            _log?.Debug("LoopShare", $"ignoring @loopdata from {sender} — no @loop send yes sent recently");
            return;
        }
        // Lines from anyone else do not close the window: the loop we said yes to
        // may still be on its way.
        if (_expectedSender is not { } expected || !SameGiven(expected, sender))
        {
            _log?.Debug("LoopShare", $"ignoring @loopdata from {sender} — we confirmed with {_expectedSender}");
            return;
        }

        (string, string) key = (sender, id);
        if (!_partial.TryGetValue(key, out string?[]? parts))
        {
            parts = new string?[count];
            _partial[key] = parts;
        }
        else if (parts.Length != count)
        {
            _log?.Warn("LoopShare", $"discarding @loopdata from {sender} — its line count changed mid-transfer");
            _partial.Remove(key);
            return;
        }
        parts[index - 1] = chunk;
        if (parts.Any(p => p is null)) return;

        _partial.Remove(key);
        _acceptUntil = DateTimeOffset.MinValue;   // one transfer per yes
        Loop loop;
        bool lairWaitUnknown;
        try { loop = LoopShareCodec.Decode(parts!, out lairWaitUnknown); }
        catch (FormatException ex)
        {
            _log?.Warn("LoopShare", $"loop from {sender} couldn't be read: {ex.Message}");
            _notice($"[Loop from {sender} arrived damaged, not saved]");
            Tell(sender, code: null, "not saved: it arrived damaged - send it again", failed: true);
            return;
        }
        Save(loop, sender, lairWaitUnknown);
    }

    private void Save(Loop loop, string sender, bool lairWaitUnknown)
    {
        // The sender's loop as the sender runs it: its route with the two settings it
        // sent. Ours, further down, is a loop here as this character runs it.
        string theirs = LoopSignature.Of(loop);
        string theirCode = LoopSignature.Short(theirs);
        int rooms = loop.Waypoints.Count;

        // The loop that arrived against the codes the sender put on its offers: the
        // one it made us is among them. It matches none of them when the transfer
        // was damaged past what the packing catches, or when lines of two transfers
        // were mixed: what arrived is then not what we said yes to, and it is not
        // saved.
        string verification;
        string? offered = null;
        if (lairWaitUnknown)
        {
            // Its lair wait is a later version's and was read as off, so the loop in
            // hand differs from the one offered in that setting, knowingly. Taken as
            // it always was, unchecked, and the notice says why (decided 2026-10-11;
            // the other course was to turn it away as not the loop offered).
            verification = "not verified: it uses a setting this version does not know";
        }
        else if (TakeOfferFor(sender, theirs, out offered))
        {
            verification = "verified";
        }
        else if (offered is null)
        {
            verification = "not verified: no code came with the offer";
        }
        else
        {
            _log?.Warn("LoopShare",
                $"loop '{loop.Name}' from {sender} is not the loop offered [received {theirCode}, offered {offered}] — not saved.");
            _notice($"[Received loop '{loop.Name}' {rooms} rooms from {sender} does not match the loop offered "
                + $"(received {theirCode}, offered {offered}), not saved]");
            Tell(sender, theirCode, $"not saved: it is not the loop offered ({offered}) - send it again", failed: true);
            return;
        }
        string checkedAs = $"({theirCode}, {verification})";

        if (_loops.SetName is null)
        {
            _log?.Warn("LoopShare", $"loop '{loop.Name}' from {sender} not saved — no game-data set is active");
            _notice($"[Received loop '{loop.Name}' {rooms} rooms from {sender}, not saved: no game data set is loaded]");
            Tell(sender, theirCode, "not saved: no game data set is loaded here", failed: true);
            return;
        }

        // A loop of that name another client saved a moment ago counts for the
        // clash checks below.
        _loops.TakeInOutsideChanges();

        // Held already, under that name or under one of the numbered names an
        // earlier copy of it was saved as: rejected. Without the numbered ones, the
        // same loop sent twice to someone whose own loop of that name differs piled
        // up as "<name> 2", "<name> 3" (user, 2026-10-11). Each is compared as this
        // character runs it, with its own two settings for that loop.
        if (_loops.WithNumberedCopies(loop.Name).FirstOrDefault(held => LoopSignature.Of(held) == theirs) is { } same)
        {
            _log?.Info("LoopShare",
                $"loop '{loop.Name}' {checkedAs} from {sender} is the loop we have as '{same.Name}' — nothing saved, nothing changed.");
            _notice($"[Received loop '{loop.Name}' {rooms} rooms from {sender} {checkedAs}, already have it as '{same.Name}']");
            Tell(sender, theirCode, $"already have it as '{same.Name}'");
            return;
        }

        if (_loops.Get(loop.Name) is not { } existing)
        {
            // Save keeps the two loop settings the sender offered as this character's
            // for the loop, and the rest in the loop file.
            _loops.Save(loop);
            _log?.Info("LoopShare", $"saved loop '{loop.Name}' {checkedAs} ({rooms} rooms) received from {sender}.");
            _notice($"[Received loop '{loop.Name}' {rooms} rooms from {sender} {checkedAs}]");
            Tell(sender, theirCode, $"saved as '{loop.Name}'");
            return;
        }

        // The name is taken by a different loop: other rooms, other room settings or
        // other loop settings. Ours stays as it is, with this character's settings
        // for it, and theirs is saved beside it under a numbered name. Taking their
        // two settings for the loop we have would change a run already under way.
        string original = loop.Name;
        loop.Name = _loops.NumberedName(original);
        _loops.Save(loop);
        string ourCode = LoopSignature.ShortOf(existing);
        _log?.Info("LoopShare",
            $"saved loop '{loop.Name}' {checkedAs} ({rooms} rooms) received from {sender}: "
            + $"'{existing.Name}' here is a different loop [{ourCode}] and is left as it is.");
        _notice($"[Received loop '{original}' {rooms} rooms from {sender} {checkedAs}, saved as '{loop.Name}': "
            + $"your '{existing.Name}' is a different loop ({ourCode})]");
        Tell(sender, theirCode, $"saved as '{loop.Name}'");
    }

    // Parse "@loopdata <id> <i>/<n> <chunk>", tolerating the {} a remote reply is
    // wrapped in.
    private static bool TryParse(string message, out string id, out int index, out int count, out string chunk)
    {
        id = chunk = string.Empty;
        index = count = 0;
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();

        string[] parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return false;
        if (!parts[0].Equals(LoopShareHandler.DataToken, StringComparison.OrdinalIgnoreCase)) return false;
        string[] fraction = parts[2].Split('/');
        if (fraction.Length != 2
            || !int.TryParse(fraction[0], out index) || !int.TryParse(fraction[1], out count)
            || count < 1 || count > MaxChunks || index < 1 || index > count)
            return false;
        id = parts[1];
        chunk = parts[3];
        return true;
    }

    private static bool SameGiven(string a, string b) =>
        Given(a).Equals(Given(b), StringComparison.OrdinalIgnoreCase);

    private static string Given(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
