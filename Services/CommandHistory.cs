namespace FujinTerm.Services;

// In-memory ring buffer of the user's most-recently-submitted command lines —
// the terminal line-buffer flushes (Enter) and the Conversation window's sends
// both feed it. Newest entry last. Capped at Capacity; the oldest drops off once
// full. Powers Up / Down recall in the terminal and the Conversation window,
// plus the latter's recall dropdown.
//
// App-session lifetime — deliberately not persisted and not scoped per
// character: it mirrors what the user just typed, the same way a shell's
// in-memory history works within one session. Engine auto-sends, macro
// expansions, and remote-command traffic never reach here; only the two
// human-typed input fields call Record.
public sealed class CommandHistory
{
    // Most commands kept for recall. The user asked for "the last 10".
    public const int Capacity = 10;

    private readonly List<string> _entries = new(Capacity);

    // Recorded commands, oldest first / newest last.
    public IReadOnlyList<string> Entries => _entries;

    // Fires after Record actually mutates the list.
    public event Action? Changed;

    // Append a submitted command. Blank / whitespace-only lines and an immediate
    // repeat of the newest entry are ignored so recall stays useful (re-sending
    // the same move shouldn't bury the history under duplicates). Once at
    // Capacity the oldest entry is dropped.
    public void Record(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (_entries.Count > 0 && _entries[^1] == command) return;
        _entries.Add(command);
        if (_entries.Count > Capacity) _entries.RemoveAt(0);
        Changed?.Invoke();
    }
}
