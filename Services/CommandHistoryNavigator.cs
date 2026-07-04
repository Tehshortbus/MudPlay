namespace FujinTerm.Services;

// Per-input-field cursor over a shared CommandHistory. Tracks where the user is
// while cycling Up / Down and stashes the in-progress line so Down past the
// newest entry restores what they were typing. Each input widget (the terminal
// control, the Conversation window) owns its own navigator over the one shared
// history.
public sealed class CommandHistoryNavigator
{
    private readonly CommandHistory _history;

    // -1 == sitting on the live (in-progress) line, not browsing history.
    private int _index = -1;
    private string _stash = string.Empty;

    public CommandHistoryNavigator(CommandHistory history) => _history = history;

    // Drop back to the live line and forget the stash. Call when the user edits
    // the line afresh or submits — the next Previous then starts browsing from
    // the newest entry again.
    public void Reset()
    {
        _index = -1;
        _stash = string.Empty;
    }

    // Step to an older entry (Up). Returns the text to display, or null when
    // there's nothing older / no history at all. The first step off the live
    // line stashes currentText so Next can restore it.
    public string? Previous(string currentText)
    {
        IReadOnlyList<string> e = _history.Entries;
        if (e.Count == 0) return null;
        if (_index < 0)
        {
            _stash = currentText;
            _index = e.Count - 1;
            return e[_index];
        }
        // Clamp in case the history changed under us since the last step.
        if (_index >= e.Count)
        {
            _index = e.Count - 1;
            return e[_index];
        }
        if (_index == 0) return null;   // already at the oldest entry
        _index--;
        return e[_index];
    }

    // Step toward a newer entry (Down). Past the newest entry it restores the
    // stashed in-progress line and returns to the live state. Returns null when
    // already on the live line (nothing to do).
    public string? Next()
    {
        if (_index < 0) return null;   // already live
        IReadOnlyList<string> e = _history.Entries;
        if (e.Count == 0)
        {
            _index = -1;
            return _stash;
        }
        if (_index >= e.Count - 1)
        {
            _index = -1;
            return _stash;
        }
        _index++;
        return e[_index];
    }
}
