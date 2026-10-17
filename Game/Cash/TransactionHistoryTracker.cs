namespace MudPlay.Game.Cash;

// A ledger of cash/item offloads and shop trades for the Session Stats →
// Transaction history window. Records a TransactionEntry per bank `dep`osit, per
// stash-room `hide` (coin or item) and per shop visit, each with the wall-clock time, the store
// kind, a rendered description of what was put away, and the room it happened in
// ("Name (map/room)" — the bank used or the stash room).
//
// Sourced from the server's own confirmation echoes so a MANUAL `dep` / `hide`
// typed by the user is captured the same as an automated reroute: deposits from
// InventoryManager.BankDeposited (`You deposit …`), coin stashes from
// CashManager.CoinHidden (`You hid N <coin>.`), item stashes from
// InventoryManager.ItemHidden (`You hid <item>.`).
//
// Coin stashed in a room is ONE row per room, not one per echo: a loop passing its
// stash every lap would otherwise bury the ledger in "Hid 94 silver" rows. The row
// carries the last visit's amount and the number of stashes, the average per stash
// and the running total (see FormatCoinRow), takes the latest time and moves to the
// newest end on each stash. Item hides stay one row each.
//
// A stash transfer draws on that same row (NoteStashWithdrawal): Last becomes what
// was taken ("Last: took …"), and Total becomes what the search showed still there
// afterwards, so Total is the stash as it was last seen rather than the sum of
// everything ever hidden. The average stays the average hide, kept on its own.
//
// Selling and buying are one row per shop visit: the items and what they came to
// ("Sold orc-head ×5, club for 12 gold, 5 silver"), from InventoryManager.ItemSold /
// ItemBought. A sell run answers one line per item type; lines at the same shop
// within a minute extend the visit's row.
//
// Owns no source subscriptions — AppServices wires those echo events to the Note*
// forwarders — matching SessionActivityTracker and keeping the tracker
// dependency-free behind an injectable clock. Every write and
// Snapshot runs on the marshalled dispatch thread (the sources all fire there),
// so the list is lock-free. Reset on the same session boundary as the other
// session-stats trackers (connect / character switch, the window "Reset session"
// button, and @reset).
public sealed class TransactionHistoryTracker
{
    // Hard cap on retained entries; the oldest is evicted past this so a long
    // session can't grow the ledger unbounded.
    public const int MaxEntries = 500;

    private readonly Func<DateTimeOffset> _clock;
    private readonly List<TransactionEntry> _entries = new();

    // Coin echoes this close together are one visit to the stash (the game answers a
    // multi-denomination stash with one line per coin type), so they share the row's
    // "last" amount; a later one starts it afresh.
    private static readonly TimeSpan VisitWindow = TimeSpan.FromSeconds(10);

    // Sale / purchase lines at one shop this close together are one visit.
    private static readonly TimeSpan TradeWindow = TimeSpan.FromSeconds(60);

    // The shop visit still being added to, if any.
    private Trade? _trade;

    private sealed class Trade
    {
        public TransactionKind Kind;
        public string? Location;
        public DateTimeOffset LastAt;
        public long Copper;
        public readonly List<(string Item, int Count)> Items = new();
        public TransactionEntry Entry;
    }

    // Where a detail breaks onto a new line in the window. The saved log holds one
    // line per entry, so the break is this marker there.
    public const string LineBreak = " | ";

    private const string LastPrefix = "Last: ";
    private const string TookPrefix = "took ";
    private const string NothingLeft = "nothing";
    private const string VisitsInfix = " - Total Stashes: ";
    private const string AveragePrefix = "Avg: ";
    private const string TotalPrefix = "Total: ";

    // The running coin tally behind each room's row, keyed by its location label.
    private readonly Dictionary<string, StashTally> _stashByLocation = new(StringComparer.Ordinal);

    private sealed class StashTally
    {
        public DateTimeOffset LastAt;
        public int Visits;
        public readonly Dictionary<string, long> Last = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, long> Total = new(StringComparer.OrdinalIgnoreCase);
        // Last was a withdrawal (a stash transfer's load) rather than a hide.
        public bool LastTaken;
        // The copper value of every hide, for the average: Total stops being that
        // sum once a withdrawal has set it to what the stash holds.
        public long HiddenCopper;
        public TransactionEntry Entry;
    }

    // Raised after any input records or clears an entry, so the Transaction
    // history VM can rebuild. Fires on the dispatch thread.
    public event Action? Changed;

    // Raised with each newly recorded entry (not on Reset), so a persistent log
    // can append it. Kept separate from Changed because the bounded ledger
    // evicts its oldest row past MaxEntries — diffing snapshots would miss the
    // append, so the tracker hands the fresh entry over directly.
    public event Action<TransactionEntry>? EntryAdded;

    // Raised when a row is replaced in place — a room's coin row updated by a stash,
    // or a "keep" mark set or cleared: the row it replaces, then the new one. The
    // persistent log rewrites itself.
    public event Action<TransactionEntry, TransactionEntry>? EntryReplaced;

    public TransactionHistoryTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.Now);
    }

    // Record a bank deposit of the given copper wealth value. Non-positive
    // amounts are ignored (nothing was deposited). location is the bank room
    // ("Name (map/room)") captured at echo time, or null when unknown.
    public void NoteBankDeposit(long copper, string? location = null)
    {
        if (copper <= 0) return;
        Add(TransactionKind.Bank, $"Deposited {copper:N0} wealth", location);
    }

    // Record a stash-room hide of the given per-denomination coin amounts and
    // item names. A dispatch with neither coins nor items is ignored (the stash
    // event never fires empty, but the guard keeps the ledger clean). location
    // is the stash room ("Name (map/room)") captured at echo time, or null when
    // unknown.
    public void NoteStash(
        IReadOnlyList<(string Currency, long Amount)> currencies,
        IReadOnlyList<string> items,
        string? location = null)
    {
        ArgumentNullException.ThrowIfNull(currencies);
        ArgumentNullException.ThrowIfNull(items);
        if (currencies.Count == 0 && items.Count == 0) return;
        if (items.Count > 0) Add(TransactionKind.Stash, FormatItems(items), location);
        if (currencies.Count > 0) AddCoinStash(currencies, location);
    }

    // Record items sold to / bought from a shop: the item's name, how many, and the
    // copper they fetched or cost in total. location is the shop room, or null.
    public void NoteSale(string item, int count, long copper, string? location = null) =>
        NoteTrade(TransactionKind.Sold, item, count, copper, location);

    public void NotePurchase(string item, int count, long copper, string? location = null) =>
        NoteTrade(TransactionKind.Bought, item, count, copper, location);

    private void NoteTrade(TransactionKind kind, string item, int count, long copper, string? location)
    {
        if (count <= 0 || string.IsNullOrWhiteSpace(item)) return;
        DateTimeOffset now = _clock();
        bool extending = _trade is { } open && open.Kind == kind && open.Location == location
            && now - open.LastAt <= TradeWindow && _entries.Contains(open.Entry);
        if (!extending) _trade = new Trade { Kind = kind, Location = location };
        Trade trade = _trade!;

        TransactionEntry old = trade.Entry;
        if (extending) _entries.Remove(old);
        int at = trade.Items.FindIndex(i => string.Equals(i.Item, item, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) trade.Items[at] = (trade.Items[at].Item, trade.Items[at].Count + count);
        else trade.Items.Add((item, count));
        trade.Copper += Math.Max(0, copper);
        trade.LastAt = now;

        string items = string.Join(", ", trade.Items.Select(i => i.Count > 1 ? $"{i.Item} ×{i.Count}" : i.Item));
        string verb = kind == TransactionKind.Sold ? "Sold" : "Bought";
        string price = trade.Copper > 0 ? FormatValue(trade.Copper) : "nothing";
        trade.Entry = new TransactionEntry(now, kind, $"{verb} {items} for {price}", location,
            Keep: extending && old.Keep);
        _entries.Add(trade.Entry);
        Evict();

        if (extending) EntryReplaced?.Invoke(old, trade.Entry);
        else EntryAdded?.Invoke(trade.Entry);
        Changed?.Invoke();
    }

    private void AddCoinStash(IReadOnlyList<(string Currency, long Amount)> currencies, string? location)
    {
        DateTimeOffset now = _clock();
        string key = location ?? string.Empty;
        bool replacing = _stashByLocation.TryGetValue(key, out StashTally? tally);
        tally ??= _stashByLocation[key] = new StashTally();

        TransactionEntry old = tally.Entry;
        if (replacing) _entries.Remove(old);
        if (!replacing || tally.LastTaken || now - tally.LastAt > VisitWindow)
        {
            tally.Last.Clear();
            tally.LastTaken = false;
            tally.Visits++;
        }
        foreach ((string currency, long amount) in currencies)
        {
            if (amount <= 0) continue;
            tally.Last[currency] = tally.Last.GetValueOrDefault(currency) + amount;
            tally.Total[currency] = tally.Total.GetValueOrDefault(currency) + amount;
            tally.HiddenCopper += amount * Denominations[Math.Max(0, Rank(currency))].Copper;
        }
        tally.LastAt = now;
        tally.Entry = new TransactionEntry(now, TransactionKind.Stash, FormatCoinRow(tally), location,
            Keep: replacing && old.Keep);
        _entries.Add(tally.Entry);
        Evict();

        if (replacing) EntryReplaced?.Invoke(old, tally.Entry);
        else EntryAdded?.Invoke(tally.Entry);
        Changed?.Invoke();
    }

    // A stash transfer searched this room's stash and took a load: the row's Last
    // becomes what was taken and its Total what the search showed left. With nothing
    // taken (the search found the stash empty) only the Total is corrected. A room
    // with no coin row gets none: there is no hide on record to draw against.
    public void NoteStashWithdrawal(
        IReadOnlyList<(string Currency, long Amount)> took,
        IReadOnlyList<(string Currency, long Amount)> left,
        string? location)
    {
        ArgumentNullException.ThrowIfNull(took);
        ArgumentNullException.ThrowIfNull(left);
        if (!_stashByLocation.TryGetValue(location ?? string.Empty, out StashTally? tally)) return;

        DateTimeOffset now = _clock();
        TransactionEntry old = tally.Entry;
        _entries.Remove(old);
        if (took.Any(t => t.Amount > 0))
        {
            tally.Last.Clear();
            tally.LastTaken = true;
            foreach ((string currency, long amount) in took)
                if (amount > 0) tally.Last[currency] = tally.Last.GetValueOrDefault(currency) + amount;
        }
        tally.Total.Clear();
        foreach ((string currency, long amount) in left)
            if (amount > 0) tally.Total[currency] = tally.Total.GetValueOrDefault(currency) + amount;
        tally.LastAt = now;
        tally.Entry = new TransactionEntry(now, TransactionKind.Stash, FormatCoinRow(tally), location, Keep: old.Keep);
        _entries.Add(tally.Entry);

        EntryReplaced?.Invoke(old, tally.Entry);
        Changed?.Invoke();
    }

    // Mark or unmark a row "keep". The row stays where it is; EntryReplaced carries
    // the change to the saved log. Returns the row as it now stands (unchanged when
    // it is no longer in the ledger).
    public TransactionEntry SetKeep(TransactionEntry entry, bool keep)
    {
        int at = _entries.IndexOf(entry);
        if (at < 0 || entry.Keep == keep) return entry;
        TransactionEntry marked = entry with { Keep = keep };
        _entries[at] = marked;
        if (_stashByLocation.TryGetValue(entry.Location ?? string.Empty, out StashTally? tally)
            && tally.Entry == entry)
            tally.Entry = marked;
        if (_trade is { } trade && trade.Entry == entry) trade.Entry = marked;
        EntryReplaced?.Invoke(entry, marked);
        return marked;
    }

    // Point-in-time copy of the ledger, oldest entry first.
    public IReadOnlyList<TransactionEntry> Snapshot() => _entries.ToArray();

    // Replace the ledger wholesale with persisted history read back from disk on
    // reconnect. Fires Changed (so the window rebuilds) but deliberately NOT
    // EntryAdded — these rows are already on disk, and re-firing the append hook
    // would write them a second time. A full replace is safe under repeated
    // profile / BBS re-fires: each call reflects the current disk tail exactly.
    public void Hydrate(IReadOnlyList<TransactionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries.Clear();
        _stashByLocation.Clear();
        _trade = null;
        // Fold every coin row for a room into that room's one row — a log written
        // before the roll-up holds one per echo. A rolled-up row brings its own total
        // and visit count; an older one is a single echo, and echoes a moment apart
        // are one visit.
        foreach (TransactionEntry e in entries)
        {
            if (e.Kind != TransactionKind.Stash || !TryParseCoinRow(e.Detail, out CoinRow row))
            {
                _entries.Add(e);
                continue;
            }
            string key = e.Location ?? string.Empty;
            bool known = _stashByLocation.TryGetValue(key, out StashTally? tally);
            tally ??= _stashByLocation[key] = new StashTally();
            if (known) _entries.Remove(tally.Entry);

            bool sameVisit = known && row.Visits is null && e.Time - tally.LastAt <= VisitWindow;
            if (!sameVisit)
            {
                tally.Last.Clear();
                tally.Visits += row.Visits ?? 1;
            }
            foreach ((string c, long n) in row.Last) tally.Last[c] = tally.Last.GetValueOrDefault(c) + n;
            foreach ((string c, long n) in row.Total) tally.Total[c] = tally.Total.GetValueOrDefault(c) + n;
            tally.LastTaken = row.Taken;
            tally.HiddenCopper += row.HiddenCopper;
            tally.LastAt = e.Time;
            tally.Entry = new TransactionEntry(e.Time, TransactionKind.Stash, FormatCoinRow(tally), e.Location,
                Keep: e.Keep || (known && tally.Entry.Keep));
            _entries.Add(tally.Entry);
        }
        Evict();
        Changed?.Invoke();
    }

    // Clear the ledger — called on the connect / character-switch boundary and by
    // the manual / remote session reset, matching the other session-stats
    // trackers.
    public void Reset()
    {
        _entries.Clear();
        _stashByLocation.Clear();
        _trade = null;
        Changed?.Invoke();
    }

    private void Add(TransactionKind kind, string detail, string? location = null)
    {
        TransactionEntry entry = new(_clock(), kind, detail, location);
        _entries.Add(entry);
        Evict();
        EntryAdded?.Invoke(entry);
        Changed?.Invoke();
    }

    // Drop the oldest rows past the cap; a room's coin row that ages out takes its
    // tally with it, so the next stash there starts a new one.
    private void Evict()
    {
        while (_entries.Count > MaxEntries)
        {
            TransactionEntry gone = _entries[0];
            _entries.RemoveAt(0);
            string key = gone.Location ?? string.Empty;
            if (_stashByLocation.TryGetValue(key, out StashTally? tally) && tally.Entry == gone)
                _stashByLocation.Remove(key);
        }
    }

    // "Hid a torch ×3" — identical item tokens fold into "name ×N" (MajorMUD lists
    // each carried copy separately).
    private static string FormatItems(IReadOnlyList<string> items)
    {
        List<string> parts = new();
        foreach (IGrouping<string, string> g in items.GroupBy(i => i, StringComparer.Ordinal))
        {
            int n = g.Count();
            parts.Add(n > 1 ? $"{g.Key} ×{n}" : g.Key);
        }
        return $"Hid {string.Join(", ", parts)}";
    }

    // The room's coin row, three lines in the window:
    //   Last: 1 gold, 115 silver - Total Stashes: 48
    //   Avg: 12 gold, 7 silver
    //   Total: 34 gold, 5,210 silver (≈ 5.6 platinum)
    // Last and Total stay in the coins that were hidden; Avg is worked out by value
    // and shown in the highest coins, and Total adds what it all comes to. Stored on one line with LineBreak between them.
    // After a stash transfer's load: "Last: took 2 platinum, 2,998 silver", and Total
    // is what was left ("nothing" once it is empty).
    private static string FormatCoinRow(StashTally tally)
    {
        long average = tally.HiddenCopper / Math.Max(1, tally.Visits);
        string total = tally.Total.Count > 0 ? FormatCoins(tally.Total) + FormatWorth(tally.Total) : NothingLeft;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{LastPrefix}{(tally.LastTaken ? TookPrefix : "")}{FormatCoins(tally.Last)}{VisitsInfix}{tally.Visits:N0}"
            + $"{LineBreak}{AveragePrefix}{FormatValue(average)}"
            + $"{LineBreak}{TotalPrefix}{total}");
    }

    // " (≈ 8.7 platinum)" — the total's value in the highest coin it reaches, to one
    // decimal. Left off when the total is already a single pile of that coin.
    private static string FormatWorth(Dictionary<string, long> total)
    {
        long copper = CopperValue(total);
        (string name, long worth) = Denominations.FirstOrDefault(d => copper >= d.Copper, Denominations[^1]);
        if (total.Count == 1 && Rank(total.Keys.First()) == Rank(name)) return string.Empty;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $" (≈ {(double)copper / worth:0.#} {name})");
    }

    // The five denominations, highest first, with what each is worth in copper.
    private static readonly (string Name, long Copper)[] Denominations =
    {
        ("runic", 1_000_000), ("platinum", 10_000), ("gold", 100), ("silver", 10), ("copper", 1),
    };

    private static int Rank(string coin) =>
        Array.FindIndex(Denominations, d => d.Name.Equals(coin, StringComparison.OrdinalIgnoreCase));

    // A word outside the standard five is a board's own name for its top coin.
    private static long CopperValue(Dictionary<string, long> coins)
    {
        long total = 0;
        foreach ((string coin, long n) in coins)
            total += n * Denominations[Math.Max(0, Rank(coin))].Copper;
        return total;
    }

    private static string FormatValue(long copper)
    {
        List<string> parts = new();
        foreach ((string name, long worth) in Denominations)
        {
            long n = copper / worth;
            if (n <= 0) continue;
            copper -= n * worth;
            parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{n:N0} {name}"));
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "0 copper";
    }

    // Highest denomination first; a board's own top-coin word leads.
    private static string FormatCoins(Dictionary<string, long> coins) =>
        string.Join(", ", coins
            .OrderBy(kv => Rank(kv.Key))
            .Select(kv => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{kv.Value:N0} {kv.Key}")));

    // One coin row read back. Visits is null for a log's older one-echo "Hid 115
    // silver", whose amount is both its last and its total.
    // Taken marks a Last that was a withdrawal; HiddenCopper is the value of the
    // hides behind the average.
    private readonly record struct CoinRow(
        Dictionary<string, long> Last, Dictionary<string, long> Total, int? Visits,
        bool Taken = false, long HiddenCopper = 0);

    // False for an item row or anything that isn't purely "<count> <coin>" parts.
    private static bool TryParseCoinRow(string detail, out CoinRow row)
    {
        row = default;
        Dictionary<string, long> last = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, long> total = new(StringComparer.OrdinalIgnoreCase);

        if (detail.StartsWith(LastPrefix, StringComparison.Ordinal))
        {
            // "Last: [took ]<coins> - Total Stashes: N | Avg: <value> | Total: <coins>".
            // The average is read back as the value of the hides (average × stashes):
            // after a withdrawal the total no longer says what was hidden.
            string[] lines = detail.Split(LineBreak);
            int infix = lines[0].IndexOf(VisitsInfix, StringComparison.Ordinal);
            if (lines.Length != 3 || infix < 0
                || !lines[1].StartsWith(AveragePrefix, StringComparison.Ordinal)
                || !lines[2].StartsWith(TotalPrefix, StringComparison.Ordinal)
                || !TryParseCount(lines[0][(infix + VisitsInfix.Length)..], out int visits))
                return false;
            string lastText = lines[0][LastPrefix.Length..infix];
            bool taken = lastText.StartsWith(TookPrefix, StringComparison.Ordinal);
            if (!TryParseCoins(taken ? lastText[TookPrefix.Length..] : lastText, last)) return false;
            string totalText = WithoutWorth(lines[2][TotalPrefix.Length..]);
            if (totalText != NothingLeft && !TryParseCoins(totalText, total)) return false;
            Dictionary<string, long> average = new(StringComparer.OrdinalIgnoreCase);
            TryParseCoins(lines[1][AveragePrefix.Length..], average);     // "0 copper" reads as none
            row = new CoinRow(last, total, visits, taken, CopperValue(average) * visits);
            return true;
        }

        if (!detail.StartsWith("Hid ", StringComparison.Ordinal)) return false;
        // An earlier build's roll-up: "Hid <coins> — avg … over N stashes — total <coins>".
        string[] parts = detail[4..].Split(" — ");
        if (!TryParseCoins(parts[0], last)) return false;
        if (parts.Length == 1)
        {
            row = new CoinRow(last, last, null, HiddenCopper: CopperValue(last));
            return true;
        }
        if (parts.Length != 3 || !parts[2].StartsWith("total ", StringComparison.Ordinal)) return false;
        int over = parts[1].LastIndexOf(" over ", StringComparison.Ordinal);
        if (over < 0) return false;
        string count = parts[1][(over + 6)..];
        int end = count.IndexOf(' ');
        if (end > 0) count = count[..end];
        if (!TryParseCount(count, out int oldVisits) || !TryParseCoins(parts[2][6..], total)) return false;
        row = new CoinRow(last, total, oldVisits, HiddenCopper: CopperValue(total));
        return true;
    }

    // Drops the trailing " (≈ 8.7 platinum)"; it is recomputed from the coins.
    private static string WithoutWorth(string totalLine)
    {
        int paren = totalLine.IndexOf(" (", StringComparison.Ordinal);
        return paren < 0 ? totalLine : totalLine[..paren];
    }

    private static bool TryParseCount(string text, out int count) =>
        int.TryParse(text, System.Globalization.NumberStyles.AllowThousands,
            System.Globalization.CultureInfo.InvariantCulture, out count) && count > 0;

    private static bool TryParseCoins(string text, Dictionary<string, long> into)
    {
        // The thousands separator is a comma too, so parts split on ", " — a comma
        // inside a number is never followed by a space.
        foreach (string part in text.Split(", "))
        {
            int space = part.IndexOf(' ');
            if (space <= 0 || part.IndexOf(' ', space + 1) >= 0) return false;
            if (!long.TryParse(part[..space], System.Globalization.NumberStyles.AllowThousands,
                    System.Globalization.CultureInfo.InvariantCulture, out long n) || n <= 0)
                return false;
            string coin = part[(space + 1)..];
            into[coin] = into.GetValueOrDefault(coin) + n;
        }
        return into.Count > 0;
    }
}
