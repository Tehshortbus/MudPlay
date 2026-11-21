using System.Collections.Generic;
using MudPlay.Game.Remote;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// What the leader's client has handed to party members for a gate, kept while the
// party lasts. A joined member who doesn't answer the @have count is taken to hold
// none, so the leader fetches a copy and hands it over. With nothing kept, the same
// member was fetched another copy on every trip through the gate, though they held
// the one from the trip before. So a hand-over the game confirmed is remembered,
// and a member who stays silent at a later count is credited with it (user,
// 2026-10-09). The cost the user accepted: a hand-over that was confirmed and then
// didn't stick is not made again until the party changes.
//
// Only what the game confirmed. The leader's own `give` is noted when it goes out
// and remembered when the game's line comes back naming that item and that member.
// A give the game refused leaves nothing, and neither does one a member was told
// to make (`@do give`): its line goes to that member, not to the leader.
//
// A member's own word outranks it. An answer replaces what was remembered for that
// item, up or down, and it is the answer a later silence is credited with.
//
// An item with a limited number of uses. A gate may use such an item up, so the
// member holding it on the last trip says nothing about this one. Its hand-over is
// remembered for the trip it was made on and no further, and for less when the
// leader crosses an exit that needs the item before that trip ends. An item whose
// uses can't be read is treated the same way.
//
// Walking on without a count. When a member who didn't answer can't be fetched a
// copy (no source can be reached), the user is asked whether to walk on regardless
// (user, 2026-11-20). A yes credits that member with one copy, for that trip only:
// not a hand-over, so their own answer drops it rather than replacing it, and the
// next trip asks again.
//
// Never saved: the party doesn't outlive the session either.
public sealed class PartyHandOverMemory
{
    private const string LogCategory = "AutoSearch";

    // How long a give that went out waits for the game's line. Past it, a line
    // naming the same item and member is a give somebody typed, not this one.
    // Inside it the two can't be told apart, and needn't be: either way the game
    // has said that member was handed that item.
    private static readonly TimeSpan ConfirmWithin = TimeSpan.FromSeconds(30);

    private sealed record Awaited(
        int ItemId, string ItemName, string Recipient, bool Silent, object? Trip, DateTimeOffset SentAt);

    // Trip is the journey a limited-use item was handed over on; null for an item
    // that is kept, whose hand-over stands until the party changes. Silent says
    // the member's last word on the item was no answer at all, so that they hold
    // it rests on the hand-over alone. Vouched is the user's walk-on, not a
    // hand-over, and is always for its trip alone.
    private sealed record Remembered(string ItemName, int Copies, object? Trip, bool Silent, bool Vouched = false);

    private readonly Func<int, bool> _hasLimitedUses;
    private readonly Func<object?> _journey;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly object _gate = new();
    private readonly List<Awaited> _awaited = new();
    private readonly Dictionary<string, Dictionary<int, Remembered>> _byMember =
        new(StringComparer.OrdinalIgnoreCase);

    public PartyHandOverMemory(
        // True unless the item is known to have unlimited uses.
        Func<int, bool> hasLimitedUses,
        // The trip under way, by reference; null when there is none.
        Func<object?> journey,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(hasLimitedUses);
        ArgumentNullException.ThrowIfNull(journey);
        _hasLimitedUses = hasLimitedUses;
        _journey = journey;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _log = log;
    }

    // The leader's own `give <item> to <member>` went out for a gate.
    // recipientSilent says the member didn't answer the count that led to it.
    public void NoteGiveSent(int itemId, string itemName, string recipient, bool recipientSilent)
    {
        if (itemId <= 0 || string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(recipient)) return;
        object? trip = _journey();
        DateTimeOffset now = _now();
        List<Awaited> lapsed;
        lock (_gate)
        {
            lapsed = TakeLapsed(now);
            _awaited.Add(new Awaited(itemId, itemName, recipient, recipientSilent, trip, now));
        }
        LogLapsed(lapsed);
    }

    // The game confirmed a hand-over of ours (InventoryManager.ItemGivenAway). It
    // is remembered when it is one this client sent for a gate; a give the user
    // typed is theirs.
    public void OnItemGivenAway(string itemName, int copies, string recipient)
    {
        if (copies <= 0) return;
        DateTimeOffset now = _now();
        List<Awaited> lapsed;
        Awaited? sent = null;
        lock (_gate)
        {
            if (_awaited.Count == 0) return;
            lapsed = TakeLapsed(now);
            int at = _awaited.FindIndex(a =>
                a.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase)
                && a.Recipient.Equals(recipient, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                sent = _awaited[at];
                _awaited.RemoveAt(at);
            }
        }
        LogLapsed(lapsed);
        if (sent is null) return;

        bool limited = _hasLimitedUses(sent.ItemId);
        if (limited && sent.Trip is null)
        {
            _log?.Info(LogCategory,
                $"path item {sent.ItemId} ('{sent.ItemName}'): handed to {sent.Recipient}, not remembered — "
                + "it has a limited number of uses and no trip is under way to remember it for");
            return;
        }

        object? trip = limited ? sent.Trip : null;
        int total;
        lock (_gate)
        {
            if (!_byMember.TryGetValue(sent.Recipient, out Dictionary<int, Remembered>? items))
                _byMember[sent.Recipient] = items = new Dictionary<int, Remembered>();
            // Copies handed over on an earlier trip went with that trip, and a
            // walk-on was no copy at all.
            int before = items.TryGetValue(sent.ItemId, out Remembered? known) && ReferenceEquals(known.Trip, trip)
                && !known.Vouched
                ? known.Copies : 0;
            total = before + copies;
            // A member credited at the count is not among its unanswered, and is
            // silent all the same.
            items[sent.ItemId] = new Remembered(sent.ItemName, total, trip, sent.Silent || known is { Silent: true });
        }
        _log?.Info(LogCategory,
            $"path item {sent.ItemId} ('{sent.ItemName}'): the game confirmed the hand-over to {sent.Recipient} — "
            + $"remembered as holding {total}, "
            + (limited ? "for this trip only (it has a limited number of uses)" : "until the party changes"));
    }

    // Read one count of an item against what was handed over. A member who
    // answered is taken at their word, and the answer replaces what was remembered
    // for them. A member who didn't is credited with what they were handed, in
    // place of none, and is no longer among the unanswered. The credit stops at the
    // per-person quota: nothing can be asked of a silent member, so a copy above
    // their own could never be sent on to anyone. A route card reads the answers
    // its walk will read again a moment later, and names the credited members in
    // its own line, so it passes noteCredits false and the credit is logged once.
    public PartyInventoryProbe.PartyItemResult Reconcile(
        PartyInventoryProbe.PartyItemResult counted, int perPerson, bool noteCredits = true)
    {
        // A default result has no members in it at all, and a null Unanswered.
        if (counted.CountsByMember is null) return counted;
        IReadOnlyCollection<string> silentMembers = counted.Unanswered ?? Array.Empty<string>();
        int id = counted.ItemId;
        int quota = Math.Max(1, perPerson);
        object? trip = _journey();
        DateTimeOffset now = _now();
        List<string>? notes = null;
        Dictionary<string, int>? credited = null;
        List<Awaited> lapsed;
        lock (_gate)
        {
            lapsed = TakeLapsed(now);
            foreach (KeyValuePair<string, int> answer in counted.CountsByMember)
            {
                if (Find(answer.Key, id) is not { } known) continue;
                if (known.Vouched)
                {
                    Forget(answer.Key, id);
                    (notes ??= new()).Add(
                        $"path item {id} ('{known.ItemName}'): {answer.Key} answers {answer.Value} — "
                        + "taken at their word over the walk-on that credited them");
                    continue;
                }
                if (answer.Value > 0)
                    _byMember[answer.Key][id] = known with { Copies = answer.Value, Silent = false };
                else Forget(answer.Key, id);
                if (known.Copies == answer.Value) continue;
                (notes ??= new()).Add(
                    $"path item {id} ('{known.ItemName}'): {answer.Key} answers {answer.Value} — "
                    + $"taken at their word over the {known.Copies} handed over earlier");
            }
            foreach (string silent in silentMembers)
            {
                if (Find(silent, id) is not { } known) continue;
                if (known.Trip is not null && !ReferenceEquals(known.Trip, trip))
                {
                    Forget(silent, id);
                    (notes ??= new()).Add(
                        $"path item {id} ('{known.ItemName}'): the trip it was handed to {silent} on is over, and it has "
                        + "a limited number of uses — no longer remembered");
                    continue;
                }
                int credit = Math.Min(known.Copies, quota);
                (credited ??= new(StringComparer.OrdinalIgnoreCase))[silent] = credit;
                if (!known.Silent) _byMember[silent][id] = known with { Silent = true };
                if (!noteCredits) continue;
                (notes ??= new()).Add(known.Vouched
                    ? $"path item {id} ('{known.ItemName}'): {silent} didn't answer — credited with one, since you said "
                        + "to walk on, so none is fetched for them"
                    : $"path item {id} ('{known.ItemName}'): {silent} didn't answer — credited with the {credit} "
                        + "handed over earlier, so none is fetched for them");
            }
        }
        LogLapsed(lapsed);
        if (notes is not null)
            foreach (string note in notes) _log?.Info(LogCategory, note);
        if (credited is null) return counted;

        var counts = new Dictionary<string, int>(counted.CountsByMember, StringComparer.OrdinalIgnoreCase);
        int total = counted.TotalCount;
        foreach (KeyValuePair<string, int> kv in credited)
        {
            counts[kv.Key] = kv.Value;
            total += kv.Value;
        }
        var unanswered = new List<string>();
        foreach (string member in silentMembers)
            if (!credited.ContainsKey(member)) unanswered.Add(member);
        return counted with { TotalCount = total, CountsByMember = counts, Unanswered = unanswered };
    }

    // The leader went through an exit that needs this item. A copy with a limited
    // number of uses may have been used up by it, in every pack that crossed. True
    // when a hand-over was forgotten for it: a count that credited it is then wrong.
    public bool OnGateCrossed(int itemId)
    {
        List<string>? spent = null;
        lock (_gate)
        {
            if (_byMember.Count == 0) return false;
            // A walk-on for an item that is kept says the same after the gate as
            // before it.
            foreach (KeyValuePair<string, Dictionary<int, Remembered>> kv in _byMember)
                if (kv.Value.TryGetValue(itemId, out Remembered? known) && known.Trip is not null
                    && (!known.Vouched || _hasLimitedUses(itemId)))
                    (spent ??= new()).Add(kv.Key);
            if (spent is null) return false;
            foreach (string member in spent) Forget(member, itemId);
        }
        _log?.Info(LogCategory,
            $"path item {itemId}: an exit that needs it was crossed, and it has a limited number of uses — "
            + $"its hand-over to {string.Join(", ", spent)} is no longer remembered");
        return true;
    }

    // How many copies of the item this member is remembered to hold, from a
    // hand-over or a count they answered; null when nothing is remembered of it.
    public int? CopiesRememberedFor(string member, int itemId)
    {
        lock (_gate)
        {
            return _byMember.TryGetValue(member, out Dictionary<int, Remembered>? items)
                && items.TryGetValue(itemId, out Remembered? known)
                ? known.Copies
                : null;
        }
    }

    // The members taken to hold this item on the strength of a hand-over alone:
    // they never answered a count of it. A gate they were refused at is not
    // something the leader is always told about, so they are the ones worth
    // checking the party list for once the leader has crossed.
    public IReadOnlyList<string> SilentHoldersOf(int itemId)
    {
        lock (_gate)
        {
            List<string>? silent = null;
            foreach (KeyValuePair<string, Dictionary<int, Remembered>> kv in _byMember)
                if (kv.Value.TryGetValue(itemId, out Remembered? known) && known.Silent)
                    (silent ??= new()).Add(kv.Key);
            return silent is null ? Array.Empty<string>() : silent;
        }
    }

    // A walk ended. A limited-use hand-over made on a trip that is now over would
    // not be credited again, so it is dropped here rather than at the next count,
    // and the bug report doesn't go on listing it. A leg ending leaves its trip
    // standing, and its hand-overs with it.
    public void ForgetEndedTrips()
    {
        object? trip = _journey();
        List<(string Member, int ItemId, string ItemName)>? over = null;
        lock (_gate)
        {
            foreach (KeyValuePair<string, Dictionary<int, Remembered>> member in _byMember)
                foreach (KeyValuePair<int, Remembered> kv in member.Value)
                    if (kv.Value.Trip is not null && !ReferenceEquals(kv.Value.Trip, trip))
                        (over ??= new()).Add((member.Key, kv.Key, kv.Value.ItemName));
            if (over is null) return;
            foreach ((string member, int itemId, _) in over) Forget(member, itemId);
        }
        _log?.Info(LogCategory,
            $"the trip is over: the hand-over of {string.Join(", ", over.Select(o => $"{o.ItemName} to {o.Member}"))} "
            + "is no longer remembered (a limited number of uses)");
    }

    // The user said to walk on although these members didn't answer the count of
    // the item and none could be fetched for them: each is credited with one for
    // the trip under way. With no trip, there is nothing to walk on.
    public void Vouch(int itemId, string itemName, IReadOnlyCollection<string> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (itemId <= 0 || string.IsNullOrWhiteSpace(itemName) || members.Count == 0) return;
        if (_journey() is not { } trip) return;
        lock (_gate)
        {
            foreach (string member in members)
            {
                if (!_byMember.TryGetValue(member, out Dictionary<int, Remembered>? items))
                    _byMember[member] = items = new Dictionary<int, Remembered>();
                items[itemId] = new Remembered(itemName, 1, trip, Silent: true, Vouched: true);
            }
        }
        _log?.Info(LogCategory,
            $"path item {itemId} ('{itemName}'): you said to walk on — {string.Join(", ", members)} taken to hold one "
            + "for this trip");
    }

    // The game refused a give, naming the player it was for when its line does. The
    // give still waiting for that player's line (the oldest, when the line names
    // nobody) is the one refused; with none waiting, the give was somebody's own.
    public void OnGiveRefused(string? recipient)
    {
        Awaited? refused = null;
        lock (_gate)
        {
            // A two-word name is matched on its first, as the give was addressed.
            string? given = recipient?.Split(' ', 2)[0];
            int at = given is null ? (_awaited.Count > 0 ? 0 : -1)
                : _awaited.FindIndex(a => a.Recipient.Equals(given, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return;
            refused = _awaited[at];
            _awaited.RemoveAt(at);
        }
        _log?.Info(LogCategory,
            $"path item {refused.ItemId} ('{refused.ItemName}'): the game refused the hand-over to {refused.Recipient} — "
            + "not remembered");
    }

    // The roster changed: what was handed to a member who is no longer in it goes
    // with them. A member joining takes nothing from the others.
    public void KeepOnly(IReadOnlyCollection<string> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var still = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
        List<string>? gone = null;
        lock (_gate)
        {
            foreach (string member in _byMember.Keys)
                if (!still.Contains(member)) (gone ??= new()).Add(member);
            if (gone is not null)
                foreach (string member in gone) _byMember.Remove(member);
            _awaited.RemoveAll(a => !still.Contains(a.Recipient));
        }
        if (gone is not null)
            _log?.Info(LogCategory,
                $"gate items handed to {string.Join(", ", gone)} are no longer remembered — they left the party");
    }

    // The party is not the one the hand-overs were made in (disbanded, another
    // leader, a disconnect, another character), or the user reset the client's state.
    public void Clear(string why)
    {
        bool any;
        lock (_gate)
        {
            any = _byMember.Count > 0;
            _byMember.Clear();
            _awaited.Clear();
        }
        if (any) _log?.Info(LogCategory, $"gate items handed to party members are no longer remembered — {why}");
    }

    // What is remembered, and the gives still waiting for the game's line (bug report).
    public string Summary
    {
        get
        {
            DateTimeOffset now = _now();
            lock (_gate)
            {
                var parts = new List<string>();
                foreach (KeyValuePair<string, Dictionary<int, Remembered>> member in _byMember)
                    foreach (Remembered r in member.Value.Values)
                        parts.Add($"{member.Key}: {r.ItemName} x{r.Copies}"
                            + (r.Vouched ? " (walked on without a count, this trip only)" : r.Trip is null ? "" : " (this trip only)"));
                foreach (Awaited a in _awaited)
                    parts.Add($"{a.Recipient}: {a.ItemName} (sent {(now - a.SentAt).TotalSeconds:0}s ago, not confirmed)");
                return parts.Count == 0 ? "(none)" : string.Join("; ", parts);
            }
        }
    }

    private Remembered? Find(string member, int itemId) =>
        _byMember.TryGetValue(member, out Dictionary<int, Remembered>? items)
        && items.TryGetValue(itemId, out Remembered? known) ? known : null;

    private void Forget(string member, int itemId)
    {
        if (!_byMember.TryGetValue(member, out Dictionary<int, Remembered>? items)) return;
        items.Remove(itemId);
        if (items.Count == 0) _byMember.Remove(member);
    }

    // Gives whose line never came, taken out of the waiting list. Called under the lock.
    private List<Awaited> TakeLapsed(DateTimeOffset now)
    {
        List<Awaited> lapsed = _awaited.FindAll(a => now - a.SentAt > ConfirmWithin);
        if (lapsed.Count > 0) _awaited.RemoveAll(a => now - a.SentAt > ConfirmWithin);
        return lapsed;
    }

    private void LogLapsed(List<Awaited> lapsed)
    {
        foreach (Awaited a in lapsed)
            _log?.Info(LogCategory,
                $"path item {a.ItemId} ('{a.ItemName}'): the game never confirmed the hand-over to {a.Recipient} — "
                + "not remembered");
    }
}
