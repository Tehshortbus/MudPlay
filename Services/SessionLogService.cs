using FujinTerm.Game;
using FujinTerm.Game.Cash;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Persists the Conversation window and the Transaction-history ledger to
// per-character rolling files under Data/Logs, so both survive restarts and the
// in-memory line caps. Filenames are <char>.<bbs>.talk.log and
// <char>.<bbs>.transactions.log; logging is suppressed until BOTH the profile
// name and the BBS name resolve — pre-login chatter and no-BBS drafts aren't
// filed. The two Log* toggles and the shared line cap come from the loaded
// character's Talk settings, re-read on every profile change and pushed live
// from the Settings → Talk tab via ApplySettings.
public sealed class SessionLogService : IDisposable
{
    private readonly ProfileService _profile;
    private readonly ChatRouter _chat;
    private readonly ChatHistoryStore _chatHistory;
    private readonly TransactionHistoryTracker _transactions;
    private readonly LogService _log;
    private readonly Func<TalkSettings> _readSettings;

    private readonly RollingLogFile _talk = new();
    private readonly RollingLogFile _txns = new();

    private bool _logConversations = true;
    private bool _logTransactions = true;
    private int _maxLines = 2000;
    private bool _disposed;

    // The in-memory chat store is app-lifetime and never cleared on profile
    // swap, so its persisted history is replayed once per app run — a second
    // seed would duplicate rows. The transaction ledger, by contrast, is reset
    // per character and re-hydrated (a full replace) on every reopen.
    private bool _chatSeeded;

    public SessionLogService(
        ProfileService profile,
        ChatRouter chat,
        ChatHistoryStore chatHistory,
        TransactionHistoryTracker transactions,
        LogService log,
        Func<TalkSettings> readSettings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(chatHistory);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(readSettings);
        _profile = profile;
        _chat = chat;
        _chatHistory = chatHistory;
        _transactions = transactions;
        _log = log;
        _readSettings = readSettings;

        _profile.ProfileLoaded += OnProfileChanged;
        _profile.BbsPinApplied += OnProfileChanged;
        _profile.ProfileClosed += OnProfileClosed;
        _chat.EntryClassified += OnChat;
        _transactions.EntryAdded += OnTransaction;

        ReopenFiles();
    }

    // Live-apply the Talk settings (both toggles + the shared cap) without a
    // file reopen — the Settings → Talk tab calls this on Save.
    public void ApplySettings(TalkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logConversations = settings.LogConversations;
        _logTransactions = settings.LogTransactions;
        _maxLines = Math.Max(1, settings.LogMaxLines);
        _talk.SetMaxLines(_maxLines);
        _txns.SetMaxLines(_maxLines);
    }

    // Wipe the persisted conversation log — the "Clear chatlog" menu item.
    public void TruncateConversations() => _talk.Truncate();

    // Wipe the persisted transaction log — the Transaction-history Clear button.
    public void TruncateTransactions() => _txns.Truncate();

    private void OnProfileChanged(CharacterProfile _) => ReopenFiles();
    private void OnProfileClosed() => CloseFiles();

    // Local wall-clock stamp on every persisted line. The date is kept (not just
    // the time) so a reload on reconnect restores each row's real day, not
    // "today". Legacy time-only logs still parse — see TryParseStamp.
    private const string StampFormat = "yyyy-MM-dd HH:mm:ss";

    private void OnChat(ChatLogEntry entry)
    {
        if (!_logConversations || !_talk.IsOpen) return;
        _talk.Append(FormatChatLine(entry));
    }

    private void OnTransaction(TransactionEntry entry)
    {
        if (!_logTransactions || !_txns.IsOpen) return;
        _txns.Append(FormatTxnLine(entry));
    }

    internal static string FormatChatLine(ChatLogEntry entry)
    {
        string speaker = entry.Speaker is null ? string.Empty : $" {entry.Speaker}";
        return $"[{entry.Timestamp.ToLocalTime().ToString(StampFormat)}] {entry.Channel}{speaker}: {entry.Message}";
    }

    internal static string FormatTxnLine(TransactionEntry entry)
    {
        string where = string.IsNullOrEmpty(entry.Location) ? string.Empty : $" @ {entry.Location}";
        return $"[{entry.Time.ToLocalTime().ToString(StampFormat)}] {entry.Kind} {entry.Detail}{where}";
    }

    private void ReopenFiles()
    {
        ApplySettings(_readSettings());

        string? name = _profile.CurrentProfileName;
        string? bbs = _profile.CurrentBbsName;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(bbs))
        {
            CloseFiles();
            return;
        }

        string stem = $"{Sanitize(name)}.{Sanitize(bbs)}";
        _talk.Open(Path.Combine(AppPaths.LogsDir, $"{stem}.talk.log"), _maxLines);
        _txns.Open(Path.Combine(AppPaths.LogsDir, $"{stem}.transactions.log"), _maxLines);

        // Replay the just-loaded disk tail into the in-memory stores the windows
        // bind to, so prior-session history shows on reconnect. Transactions
        // replace wholesale (the ledger was Reset on profile load and is per
        // character); chat seeds once per run into the app-lifetime store.
        HydrateTransactions();
        if (!_chatSeeded)
        {
            HydrateChat();
            _chatSeeded = true;
        }

        _log.Info(
            "SessionLog",
            $"Session logs open ({stem}): conversations={_logConversations}, " +
            $"transactions={_logTransactions}, cap={_maxLines}");
    }

    private void HydrateChat()
    {
        List<ChatLogEntry> entries = new();
        foreach (string line in _talk.Snapshot())
            if (TryParseChatLine(line, out ChatLogEntry entry)) entries.Add(entry);
        _chatHistory.Seed(entries);
    }

    private void HydrateTransactions()
    {
        List<TransactionEntry> entries = new();
        foreach (string line in _txns.Snapshot())
            if (TryParseTxnLine(line, out TransactionEntry entry)) entries.Add(entry);
        _transactions.Hydrate(entries);
    }

    // Inverse of OnChat: "[stamp] Channel[ speaker]: message". The head before
    // the first ": " is the channel name plus an optional space-separated
    // speaker; everything after is the message (which may itself contain ": ").
    internal static bool TryParseChatLine(string line, out ChatLogEntry entry)
    {
        entry = default;
        if (!TrySplitStamp(line, out DateTimeOffset stamp, out string rest)) return false;

        int colon = rest.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return false;
        string head = rest[..colon];
        string message = rest[(colon + 2)..];

        int sp = head.IndexOf(' ');
        string channelName = sp < 0 ? head : head[..sp];
        string? speaker = sp < 0 ? null : head[(sp + 1)..];
        if (!Enum.TryParse(channelName, out ChatChannel channel)) return false;

        entry = new ChatLogEntry(stamp, channel, speaker, message, message);
        return true;
    }

    // Inverse of OnTransaction: "[stamp] Kind detail[ @ location]". The first
    // token is the kind; the remainder is the detail, with an optional trailing
    // " @ location" split off the last such marker (details never contain one).
    internal static bool TryParseTxnLine(string line, out TransactionEntry entry)
    {
        entry = default;
        if (!TrySplitStamp(line, out DateTimeOffset stamp, out string rest)) return false;

        int sp = rest.IndexOf(' ');
        if (sp < 0) return false;
        if (!Enum.TryParse(rest[..sp], out TransactionKind kind)) return false;
        string body = rest[(sp + 1)..];

        string? location = null;
        int at = body.LastIndexOf(" @ ", StringComparison.Ordinal);
        if (at >= 0)
        {
            location = body[(at + 3)..];
            body = body[..at];
        }

        entry = new TransactionEntry(stamp, kind, body, location);
        return true;
    }

    // Peel the leading "[stamp] " off a persisted line and parse the stamp.
    private static bool TrySplitStamp(string line, out DateTimeOffset stamp, out string rest)
    {
        stamp = default;
        rest = string.Empty;
        if (line.Length == 0 || line[0] != '[') return false;
        int close = line.IndexOf(']');
        if (close < 0 || close + 2 > line.Length) return false;
        if (!TryParseStamp(line[1..close], out stamp)) return false;
        rest = line[(close + 2)..];
        return true;
    }

    // Parse a persisted stamp as local wall-clock. Current logs carry the full
    // date; legacy time-only logs fall back to today's date so their rows still
    // load (their real day is unrecoverable from the line alone).
    private static bool TryParseStamp(string raw, out DateTimeOffset stamp)
    {
        stamp = default;
        if (DateTime.TryParseExact(raw, StampFormat,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime dt))
        {
            stamp = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local));
            return true;
        }
        if (DateTime.TryParseExact(raw, "HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime t))
        {
            DateTime today = DateTime.Today.Add(t.TimeOfDay);
            stamp = new DateTimeOffset(DateTime.SpecifyKind(today, DateTimeKind.Local));
            return true;
        }
        return false;
    }

    private void CloseFiles()
    {
        _talk.Close();
        _txns.Close();
    }

    // Reduce a profile / BBS name to a filesystem-safe token. Invalid filename
    // chars and '.' become '_' — the latter so the '.' delimiters in the
    // <char>.<bbs>.talk.log stem stay unambiguous whatever the BBS reports.
    private static string Sanitize(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        System.Text.StringBuilder sb = new(raw.Length);
        foreach (char c in raw)
            sb.Append(c == '.' || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        string s = sb.ToString().Trim();
        return s.Length == 0 ? "_" : s;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _profile.ProfileLoaded -= OnProfileChanged;
        _profile.BbsPinApplied -= OnProfileChanged;
        _profile.ProfileClosed -= OnProfileClosed;
        _chat.EntryClassified -= OnChat;
        _transactions.EntryAdded -= OnTransaction;
        CloseFiles();
    }
}
