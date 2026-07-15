namespace FujinTerm.Services;

// A line-capped append log persisted to a single file. Retains only the last
// MaxLines lines on disk — the tail rolls forward as new lines arrive, so a
// long-lived log can't grow without bound. Content survives process restarts:
// Open reloads the existing tail and appends continue from there.
//
// Thread-safe behind a single lock. The whole file is rewritten on every append
// so the on-disk line count never overshoots the cap even mid-session; chat and
// transaction lines arrive at human speed, so the full rewrite is off any hot
// path. I/O failures (a locked file, a permission flap) are swallowed — a log
// that can't be written must never take down the feature it's recording.
public sealed class RollingLogFile
{
    private readonly object _gate = new();
    private readonly List<string> _lines = new();
    private string? _path;
    private int _maxLines = 1;

    public bool IsOpen
    {
        get { lock (_gate) { return _path is not null; } }
    }

    // A point-in-time copy of the retained tail, oldest line first — the same
    // lines an Open reloaded from disk. Lets a caller replay the persisted
    // history back into an in-memory store on reconnect without re-reading the
    // file itself.
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) { return _lines.ToArray(); }
    }

    // Point the log at path with the given cap, loading any existing tail so
    // appends continue across restarts. Re-opening at a different path drops the
    // previous file's in-memory tail first. maxLines <= 0 is clamped to 1.
    public void Open(string path, int maxLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            _path = path;
            _maxLines = Math.Max(1, maxLines);
            _lines.Clear();
            try
            {
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadLines(path)) _lines.Add(line);
                    TrimLocked();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Stop writing; the file keeps its content on disk for the next session.
    public void Close()
    {
        lock (_gate)
        {
            _path = null;
            _lines.Clear();
        }
    }

    // Adjust the cap live (the shared line-count picker changed). Trims + flushes
    // only when the new cap actually sheds rows.
    public void SetMaxLines(int maxLines)
    {
        lock (_gate)
        {
            _maxLines = Math.Max(1, maxLines);
            if (TrimLocked()) FlushLocked();
        }
    }

    public void Append(string line)
    {
        lock (_gate)
        {
            if (_path is null) return;
            _lines.Add(line);
            TrimLocked();
            FlushLocked();
        }
    }

    // Wipe the file and the in-memory tail. Driven by the user's explicit Clear
    // (Clear chatlog menu / Transaction-history Clear button), never by a session
    // boundary — the whole point of the log is to persist across those.
    public void Truncate()
    {
        lock (_gate)
        {
            _lines.Clear();
            FlushLocked();
        }
    }

    private bool TrimLocked()
    {
        bool trimmed = false;
        while (_lines.Count > _maxLines)
        {
            _lines.RemoveAt(0);
            trimmed = true;
        }
        return trimmed;
    }

    private void FlushLocked()
    {
        if (_path is null) return;
        try
        {
            File.WriteAllLines(_path, _lines);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
