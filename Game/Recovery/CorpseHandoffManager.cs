using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Recovery;

// Keeps what came out of another player's corpse so it can be handed back.
//
// `recover corpse <name> <password>` naming someone other than us arms it; the
// recovery's own lines (`You begin to pick through the corpse of <Name>...`, a
// `You picked up N <coin>.` per denomination, a `You took <item>.` per copy, and
// `You have recovered the corpse of <Name>.`) fill the record, which is kept on the
// character until it is handed back or cleared. A failed recovery prints none of
// them, so nothing is recorded: a refusal (`You may not recover the corpse of
// <Name>.`) drops the arm, and an arm nothing answers runs out. Our own corpse never
// needs a password and is never handed back (GAME_MECHANICS "Corpse recovery
// (`recover corpse`)").
//
// The handoff sends a give per item and per coin through the item-sweep pacer and
// strikes each off as the game confirms it; a refusal, or a give the game never
// answered, leaves the item listed.
//
// The password is the other player's secret: it is kept in memory for this session
// only, so a bug report can mask it (BugReportBuilder.BuildScrubber), and is never
// logged, stored or shown.
public sealed class CorpseHandoffManager
{
    public const string LogCategory = "CorpseHandoff";

    // From the command going out to the first recovery line.
    public static readonly TimeSpan ArmWindow = TimeSpan.FromSeconds(20);

    // From the first recovery line to the closing one. The lines come in one burst;
    // past this the closing line was missed, and taking more `You took` lines into
    // the record would hand away our own loot.
    public static readonly TimeSpan CloseWindow = TimeSpan.FromSeconds(5);

    // A handoff with nothing sent or answered for this long is over: whatever is
    // still out was never answered. Longer than the pacer's own answer timeout and
    // rate-limit back-off, so a paced run is not cut short between two sends.
    public static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(8);

    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private readonly Func<string?> _selfName;
    private readonly Func<(RoomRef? Room, string? Name)> _currentRoom;
    private readonly Func<string, string> _canonicalCurrency;
    private readonly Func<bool> _isParadigm;
    private readonly Func<string> _runicName;
    private readonly Action<IReadOnlyList<string>, object, Func<string, bool>> _sendPaced;
    private readonly Action<object> _recallPaced;
    private readonly Action<TimeSpan, Action> _scheduleAfter;
    private readonly Func<DateTimeOffset> _now;

    // Added on the UI thread, read by a hang report's scrub off it: guarded.
    private readonly object _passwordsLock = new();
    private readonly HashSet<string> _sessionPasswords = new(StringComparer.Ordinal);

    // Each name typed in a command not yet answered by its recovery, and when it
    // went out: several can be sent in a row (`;`), and each is answered in turn.
    private readonly List<(string Name, DateTimeOffset At)> _armed = new();

    // A recovery under way: the owner as printed, and what has come out so far.
    private sealed class Collecting(string owner)
    {
        public string Owner { get; } = owner;
        public List<CorpseHandoffItem> Items { get; } = new();
        public CurrencyHoldings Coins { get; set; } = CurrencyHoldings.Empty;
    }
    private Collecting? _collecting;

    private sealed class Handoff(CorpseHandoffRecord record, string recipient, IReadOnlyList<CorpseHandoffPlan.Give> gives)
    {
        public CorpseHandoffRecord Record { get; } = record;
        public string Recipient { get; } = recipient;
        public Queue<CorpseHandoffPlan.Give> ToSend { get; } = new(gives);
        public List<CorpseHandoffPlan.Give> Outstanding { get; } = new();
        public int Given { get; set; }
        public List<string> Refused { get; } = new();
        public int Generation { get; set; }
    }
    private Handoff? _handoff;

    public CorpseHandoffManager(
        ProfileService profile,
        Func<string?> selfName,
        Func<(RoomRef? Room, string? Name)> currentRoom,
        Func<string, string> canonicalCurrency,
        Func<bool> isParadigm,
        Func<string> runicName,
        Action<IReadOnlyList<string>, object, Func<string, bool>> sendPaced,
        Action<object> recallPaced,
        Action<TimeSpan, Action> scheduleAfter,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _selfName = selfName ?? throw new ArgumentNullException(nameof(selfName));
        _currentRoom = currentRoom ?? throw new ArgumentNullException(nameof(currentRoom));
        _canonicalCurrency = canonicalCurrency ?? throw new ArgumentNullException(nameof(canonicalCurrency));
        _isParadigm = isParadigm ?? throw new ArgumentNullException(nameof(isParadigm));
        _runicName = runicName ?? throw new ArgumentNullException(nameof(runicName));
        _sendPaced = sendPaced ?? throw new ArgumentNullException(nameof(sendPaced));
        _recallPaced = recallPaced ?? throw new ArgumentNullException(nameof(recallPaced));
        _scheduleAfter = scheduleAfter ?? throw new ArgumentNullException(nameof(scheduleAfter));
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        // The new character's profile is not where an unfinished record belongs.
        _profile.ProfileLoaded += _ => Reset("another character was loaded");
    }

    // Raised when the list, or a record on it, changes.
    public event Action? Changed;

    // Newest first.
    public IReadOnlyList<CorpseHandoffRecord> Records =>
        _profile.Current?.CorpseHandoffs is { } list
            ? list.OrderByDescending(r => r.At).ToList()
            : Array.Empty<CorpseHandoffRecord>();

    // Passwords typed this session, for the bug report's mask. Never written anywhere.
    public IReadOnlyCollection<string> SessionPasswords
    {
        get { lock (_passwordsLock) return _sessionPasswords.ToArray(); }
    }

    public bool HandoffActive => _handoff is not null;

    // ----- Recording ---------------------------------------------------------

    // Every line on its way out: only `recover corpse <name> <password>` matters.
    // usersOwn: typed by the user at the terminal or the Conversation window. Only
    // such a line arms a record (user, 2026-10-17); one an engine, a macro or a
    // relayed `@do` sent still has its password remembered for the bug report's mask.
    public void ObserveOutbound(ReadOnlySpan<byte> data, bool usersOwn)
    {
        if (data.Length is 0 or > 300) return;
        foreach (string line in Encoding.Latin1.GetString(data).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            ObserveCommand(line, usersOwn);
    }

    public void ObserveCommand(string command, bool usersOwn)
    {
        if (!CorpseRecoveryLines.TryParseRecoverWithPassword(command, out string name, out string password)) return;
        lock (_passwordsLock) _sessionPasswords.Add(password.Trim());
        if (!usersOwn) return;
        if (IsSelf(name))
        {
            _log?.Debug(LogCategory, "the corpse named is this character's own: not handed back");
            return;
        }
        _armed.Add((name, _now()));
        _log?.Debug(LogCategory, $"{name}'s corpse: recovery sent, waiting for its lines");
    }

    // Bound per connection, so the previous extractor is let go.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnEmitted;
        _lines = lines;
        _lines.LineEmitted += OnEmitted;
    }

    private LineExtractor? _lines;
    private void OnEmitted(LineExtractor.EmittedLine line) => OnLine(line.Text);

    public void OnLine(string text)
    {
        if (CorpseRecoveryLines.Begun(text) is { } begun)
        {
            FinishCollecting("another recovery began");
            DateTimeOffset now = _now();
            _armed.RemoveAll(a => now - a.At > ArmWindow);
            // Our own corpse (an auto-recovery) answers none of the arms.
            if (IsSelf(begun)) return;
            string given = CorpseRecoveryLines.GivenName(begun);
            int at = _armed.FindIndex(a => given.StartsWith(a.Name, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return;
            _armed.RemoveAt(at);
            Collecting started = new(begun);
            _collecting = started;
            _scheduleAfter(CloseWindow, () =>
            {
                if (ReferenceEquals(_collecting, started)) FinishCollecting($"no closing line within {CloseWindow.TotalSeconds:0} s");
            });
            return;
        }
        if (CorpseRecoveryLines.Refused(text) is { } refused)
        {
            string who = CorpseRecoveryLines.GivenName(refused);
            int at = _armed.FindIndex(a => who.StartsWith(a.Name, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return;
            _armed.RemoveAt(at);
            _log?.Debug(LogCategory, $"{refused}'s corpse: the game refused the recovery, nothing to record");
            return;
        }
        if (_collecting is not { } c) return;
        if (CorpseRecoveryLines.Took(text) is { } took)
        {
            (int count, string name) = CountedCommand.SplitLeadingCount(took.Trim());
            c.Items.Add(new CorpseHandoffItem(name, count));
            return;
        }
        if (CorpseRecoveryLines.Completed(text) is { } done
            && string.Equals(done, c.Owner, StringComparison.OrdinalIgnoreCase))
            FinishCollecting(null);
    }

    // CashManager.CoinCollected: a `You picked up N <coin>.` line, as a currency word.
    public void NoteCoinsPicked(string currency, int count)
    {
        if (_collecting is not { } c || count <= 0) return;
        c.Coins = CorpseHandoffPlan.Add(c.Coins, _canonicalCurrency(currency), count);
    }

    // A recovery is over by the time the character walks on. An arm is not dropped
    // here: a move typed just before the command (`nw` then the recover) lands after
    // the arm and before the recovery's lines. An arm that draws nothing runs out, or
    // goes with the game's refusal.
    public void NoteRoomChanged() => FinishCollecting("the character moved on");

    // missing: why the closing line was not seen, or null when it was.
    private void FinishCollecting(string? missing)
    {
        if (_collecting is not { } c) return;
        _collecting = null;
        List<CorpseHandoffItem> items = CorpseHandoffPlan.Merge(c.Items);
        if (items.Count == 0 && c.Coins.TotalCoinCount == 0) return;
        if (_profile.Current is not { } profile) return;
        (RoomRef? room, string? roomName) = _currentRoom();
        CorpseHandoffRecord record = new()
        {
            Owner = c.Owner,
            At = _now(),
            Room = room,
            RoomName = roomName,
            Items = items,
            Coins = c.Coins,
            Note = missing is null ? null : $"The game's closing line wasn't seen ({missing}); this is what the lines before it named.",
        };
        (profile.CorpseHandoffs ??= new()).Add(record);
        _profile.Save();
        int copies = items.Sum(i => i.Count);
        _log?.Info(LogCategory, $"recorded {copies} item(s) from {c.Owner}'s corpse"
            + (c.Coins.TotalCoinCount > 0 ? $" and {string.Join(", ", DeathRecord.DescribeCoins(c.Coins))}" : string.Empty)
            + (missing is null ? string.Empty : $" (no closing line: {missing})"));
        Changed?.Invoke();
    }

    private bool IsSelf(string name)
    {
        string self = CorpseRecoveryLines.GivenName(_selfName());
        return self.Length > 0 && string.Equals(CorpseRecoveryLines.GivenName(name), self, StringComparison.OrdinalIgnoreCase);
    }

    // ----- Handing back ------------------------------------------------------

    // Sends the gives. Returns why it couldn't start, or null when it did.
    public string? StartHandoff(CorpseHandoffRecord record, string recipient)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(recipient)) return "Type the name of the player to hand the items to.";
        recipient = recipient.Trim();
        if (recipient.Contains(' ')) return "Type one name.";
        if (_handoff is not null) return "A handoff is already under way.";
        IReadOnlyList<CorpseHandoffPlan.Give> gives =
            CorpseHandoffPlan.Build(record.Items, record.Coins, recipient, _isParadigm(), _runicName());
        if (gives.Count == 0) return "Nothing is left to hand back.";
        Handoff h = new(record, recipient, gives);
        _handoff = h;
        record.Note = $"Handing to {recipient}…";
        _log?.Info(LogCategory, $"handoff of {record.Owner}'s corpse to {recipient} started: {gives.Count} give(s)");
        Changed?.Invoke();
        ArmQuiet(h);
        _sendPaced(gives.Select(g => g.Command).ToList(), h, command => MayGo(h, command));
        return null;
    }

    // Asked by the pacer just before each give goes out.
    private bool MayGo(Handoff h, string command)
    {
        if (!ReferenceEquals(_handoff, h) || h.ToSend.Count == 0) return false;
        CorpseHandoffPlan.Give next = h.ToSend.Dequeue();
        if (next.Command != command) _log?.Debug(LogCategory, $"pacer sent '{command}' where '{next.Command}' was next");
        h.Outstanding.Add(next);
        ArmQuiet(h);
        return true;
    }

    // InventoryManager.ItemGivenAway.
    public void OnItemGivenAway(string item, int copies, string recipient)
    {
        if (_handoff is not { } h || !NameMatches(recipient, h.Recipient)) return;
        string key = ItemNameStore.Normalize(item);
        int at = h.Outstanding.FindIndex(g => g.What == CorpseHandoffPlan.Kind.Item && ItemNameStore.Normalize(g.Name) == key);
        if (at < 0) return;
        h.Outstanding.RemoveAt(at);
        if (h.Record.Items.FirstOrDefault(i => ItemNameStore.Normalize(i.Name) == key) is { } left)
        {
            left.Count = Math.Max(0, left.Count - copies);
            if (left.Count == 0) h.Record.Items.Remove(left);
        }
        h.Given += copies;
        _profile.Save();
        Answered(h);
    }

    // InventoryManager.CoinsGivenAway: the coins the game counted, in copper.
    public void OnCoinsGivenAway(string recipient, long copper)
    {
        if (_handoff is not { } h || !NameMatches(recipient, h.Recipient)) return;
        int at = h.Outstanding.FindIndex(g => g.What == CorpseHandoffPlan.Kind.Coin);
        if (at < 0) return;
        CorpseHandoffPlan.Give give = h.Outstanding[at];
        h.Outstanding.RemoveAt(at);
        long unit = CurrencyHoldings.ToCopper(give.Name, 1);
        int count = unit > 0 ? (int)Math.Min(give.Count, copper / unit) : give.Count;
        h.Record.Coins = CorpseHandoffPlan.Add(h.Record.Coins, give.Name, -count);
        h.Given += count;
        if (count < give.Count)
            _log?.Info(LogCategory, $"{h.Recipient} took {count} of {give.Count} {give.Name}: the rest stays listed");
        _profile.Save();
        Answered(h);
    }

    // InventoryManager.GiveRefused: the player the line names, or null. The game
    // answers gives in order, so it is the oldest one still out.
    public void OnGiveRefused(string? by)
    {
        if (_handoff is not { } h || h.Outstanding.Count == 0) return;
        if (by is not null && !NameMatches(by, h.Recipient)) return;
        Refuse(h, 0);
    }

    // InventoryManager.CoinGiveMisaimed / CoinGiveLackedCoins: a coin give turned down.
    public void OnCoinGiveRefused()
    {
        if (_handoff is not { } h) return;
        int at = h.Outstanding.FindIndex(g => g.What == CorpseHandoffPlan.Kind.Coin);
        if (at >= 0) Refuse(h, at);
    }

    private void Refuse(Handoff h, int at)
    {
        CorpseHandoffPlan.Give give = h.Outstanding[at];
        h.Outstanding.RemoveAt(at);
        h.Refused.Add(give.Command);
        _log?.Info(LogCategory, $"the game refused '{give.Command}': it stays listed");
        Answered(h);
    }

    private void Answered(Handoff h)
    {
        Changed?.Invoke();
        if (h.ToSend.Count == 0 && h.Outstanding.Count == 0) Finish(h);
        else ArmQuiet(h);
    }

    private void ArmQuiet(Handoff h)
    {
        int generation = ++h.Generation;
        _scheduleAfter(QuietWindow, () =>
        {
            if (ReferenceEquals(_handoff, h) && h.Generation == generation) Finish(h);
        });
    }

    private void Finish(Handoff h, string? stoppedBecause = null)
    {
        if (!ReferenceEquals(_handoff, h)) return;
        _handoff = null;
        int notSent = h.ToSend.Count;
        if (notSent > 0) _recallPaced(h);
        int unanswered = h.Outstanding.Count;
        CorpseHandoffRecord record = h.Record;
        // A record of the character just swapped out, or one just cleared, is not
        // the loaded profile's to save.
        if (_profile.Current?.CorpseHandoffs is not { } list || !list.Contains(record))
        {
            _log?.Info(LogCategory, $"handoff of {record.Owner}'s corpse to {h.Recipient} stopped ({stoppedBecause}): {h.Given} given");
            return;
        }
        if (record.IsEmpty)
        {
            list.Remove(record);
            _log?.Info(LogCategory, $"handoff of {record.Owner}'s corpse to {h.Recipient} done: everything handed back, record cleared");
        }
        else
        {
            List<string> why = new();
            if (h.Refused.Count > 0) why.Add($"{h.Refused.Count} refused");
            if (unanswered > 0) why.Add($"{unanswered} not answered");
            if (notSent > 0) why.Add($"{notSent} not sent{(stoppedBecause is null ? string.Empty : $" ({stoppedBecause})")}");
            record.Note = $"Handed to {h.Recipient} {_now().LocalDateTime:yyyy-MM-dd HH:mm}: {h.Given} given"
                + (why.Count > 0 ? $"; {string.Join(", ", why)}. What is listed is still to hand back." : ".");
            _log?.Info(LogCategory, $"handoff of {record.Owner}'s corpse to {h.Recipient} done: {h.Given} given"
                + (why.Count > 0 ? $", {string.Join(", ", why)}" : string.Empty));
        }
        _profile.Save();
        Changed?.Invoke();
    }

    // Removes a record without handing anything back.
    public void Clear(CorpseHandoffRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_handoff is { } h && ReferenceEquals(h.Record, record)) Finish(h, "the record was cleared");
        if (_profile.Current?.CorpseHandoffs is not { } list || !list.Remove(record)) return;
        _profile.Save();
        _log?.Info(LogCategory, $"record of {record.Owner}'s corpse cleared by the user");
        Changed?.Invoke();
    }

    // A disconnect: a recording under way keeps what its lines named, and what is
    // still queued would go to a game that isn't there.
    public void NotifyDisconnected()
    {
        FinishCollecting("the link dropped");
        Reset("the link dropped");
    }

    private void Reset(string why)
    {
        _armed.Clear();
        _collecting = null;
        if (_handoff is { } h) Finish(h, why);
        Changed?.Invoke();
    }

    // The game prints a player by their first name in full; the user may have typed
    // part of it.
    private static bool NameMatches(string printed, string typed) =>
        CorpseRecoveryLines.GivenName(printed).StartsWith(CorpseRecoveryLines.GivenName(typed), StringComparison.OrdinalIgnoreCase)
        || CorpseRecoveryLines.GivenName(typed).StartsWith(CorpseRecoveryLines.GivenName(printed), StringComparison.OrdinalIgnoreCase);

    // For the bug report's pending-work list: what is armed, collecting or being
    // handed back. Names and items only.
    public string? DescribePending()
    {
        DateTimeOffset now = _now();
        _armed.RemoveAll(a => now - a.At > ArmWindow);
        List<string> parts = new();
        foreach ((string name, DateTimeOffset at) in _armed)
            parts.Add($"waiting for the recovery lines of {name}'s corpse (sent {at.LocalDateTime:HH:mm:ss})");
        if (_collecting is { } c) parts.Add($"recording {c.Owner}'s corpse: {c.Items.Count} item line(s) so far");
        if (_handoff is { } h)
            parts.Add($"handing {h.Record.Owner}'s corpse to {h.Recipient}: {h.Given} given, {h.Outstanding.Count} awaiting an answer, "
                + $"{h.ToSend.Count} still to send, {h.Refused.Count} refused");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
