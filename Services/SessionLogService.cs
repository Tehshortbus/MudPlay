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
    private readonly TransactionHistoryTracker _transactions;
    private readonly LogService _log;
    private readonly Func<TalkSettings> _readSettings;

    private readonly RollingLogFile _talk = new();
    private readonly RollingLogFile _txns = new();

    private bool _logConversations = true;
    private bool _logTransactions = true;
    private int _maxLines = 2000;
    private bool _disposed;

    public SessionLogService(
        ProfileService profile,
        ChatRouter chat,
        TransactionHistoryTracker transactions,
        LogService log,
        Func<TalkSettings> readSettings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(readSettings);
        _profile = profile;
        _chat = chat;
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

    private void OnChat(ChatLogEntry entry)
    {
        if (!_logConversations || !_talk.IsOpen) return;
        string speaker = entry.Speaker is null ? string.Empty : $" {entry.Speaker}";
        _talk.Append($"[{entry.Timestamp.ToLocalTime():HH:mm:ss}] {entry.Channel}{speaker}: {entry.Message}");
    }

    private void OnTransaction(TransactionEntry entry)
    {
        if (!_logTransactions || !_txns.IsOpen) return;
        string where = string.IsNullOrEmpty(entry.Location) ? string.Empty : $" @ {entry.Location}";
        _txns.Append($"[{entry.Time.ToLocalTime():HH:mm:ss}] {entry.Kind} {entry.Detail}{where}");
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
        _log.Info(
            "SessionLog",
            $"Session logs open ({stem}): conversations={_logConversations}, " +
            $"transactions={_logTransactions}, cap={_maxLines}");
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
