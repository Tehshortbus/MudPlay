using FujinTerm.Terminal;

namespace FujinTerm.Services;

// Tee the live terminal transcript to a file. Each completed row (the same rows
// the Backscroll window sees) is appended as [HH:mm:ss] <ansi-encoded-text>\n so
// the file replays its colours through any ANSI-aware viewer (less -R, modern
// terminals, etc.). Replaces the old timed binary capture format —
// replay-into-the-emulator isn't a feature the app needs.
//
// Thread-safety: ScrollbackBuffer.RowAdded fires on the emulator's UI-thread
// Feed path in production. Start and Stop are also UI-thread calls. No internal
// locking needed.
public sealed class CaptureSession : IAsyncDisposable
{
    private readonly ScrollbackBuffer _source;
    private StreamWriter? _writer;
    private bool _subscribed;

    // Path of the currently-open file, or null if inactive.
    public string? FilePath { get; private set; }

    // true between Start and Stop.
    public bool IsActive => _writer is not null;

    public CaptureSession(ScrollbackBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    // Begin appending captured rows to path. Overwrites any existing file with
    // the same name. Idempotent: starting again while active rotates to the new
    // path.
    public void Start(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Stop();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        FilePath = path;

        _source.RowAdded += OnRowAdded;
        _subscribed = true;

        _writer.WriteLine($"# FujinTerm capture — started {DateTimeOffset.Now:O}");
    }

    // Stop the capture and close the file. No-op when inactive.
    public void Stop()
    {
        if (_subscribed)
        {
            _source.RowAdded -= OnRowAdded;
            _subscribed = false;
        }

        if (_writer is not null)
        {
            try { _writer.WriteLine($"# capture stopped — {DateTimeOffset.Now:O}"); }
            catch (ObjectDisposedException) { /* already torn down */ }
            _writer.Dispose();
            _writer = null;
        }

        FilePath = null;
    }

    private void OnRowAdded(ScrollbackBuffer.Row row)
    {
        if (_writer is null) return;

        try
        {
            _writer.Write('[');
            _writer.Write(row.Timestamp.ToLocalTime().ToString("HH:mm:ss"));
            _writer.Write("] ");
            _writer.WriteLine(AnsiEncoder.EncodeRow(row.Cells));
        }
        catch (ObjectDisposedException)
        {
            // Writer closed between the event firing and our write — Stop()
            // racing with a tail-end row. Safe to drop.
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
