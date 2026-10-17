using System.Text;
using System.Threading.Channels;

namespace MudPlay.Services;

// Keeps the last few minutes of the program log on disk, always, so a hang or a
// forced kill leaves something to send: no bug report can be made of a client
// that is no longer running, and the program log otherwise lives only in memory.
//
// Two files per client process, written in turn. Each takes entries for
// SegmentSpan (or until SegmentMaxBytes); then the other is emptied and takes
// over. What a reader finds after a kill is the full older file and the part-
// written newer one: between one and two spans, three to six minutes, of log.
// Each file opens with the same header (build, system, start time), so either
// one stands on its own. The pair can never pass 2 × SegmentMaxBytes.
//
// Entries are handed to a queue and written by a background task, flushed once
// per batch: the thread that logged (usually the UI thread) never touches the
// file, and a UI thread that has hung doesn't stop what it already logged from
// reaching the disk. A killed process loses at most the batch in flight.
//
// The queue holds QueueDepth entries. While a write is stuck (a disk that has
// stalled, a network folder gone quiet) the oldest waiting entries make room for
// the newest, and the file says how many went when writing resumes. A failure
// nothing here expects is noted once in the program log, the file is closed, and
// the writing is tried again after a retry delay: the pump itself never ends before
// the client does.
//
// Not written while Auto-collect logs is on: the full program log is then on
// disk from whenever it was switched on, and is the record.
public sealed class RecentLogFile : IAsyncDisposable
{
    // What survives is the full older file and the part-written newer one: never
    // less than one span, so at least the last 3 minutes, and up to 6.
    public static readonly TimeSpan SegmentSpan = TimeSpan.FromMinutes(3);

    // A busy session logs about 8 entries of about 100 bytes a second: some
    // 150 KB a span. The cap leaves seven times that for a burst before a file
    // rolls early.
    public const int SegmentMaxBytes = 1024 * 1024;

    // How many client processes' files are kept when a new one starts: this one
    // and the two before it, enough to cover "it hung, I restarted, it hung
    // again".
    public const int ProcessesKept = 3;

    public const string TopicA = "recent-a";
    public const string TopicB = "recent-b";

    private readonly LogService _log;
    private readonly LogDiagnosticState _diagnostics;
    private readonly string[] _paths;
    private readonly Func<IReadOnlyList<string>> _header;
    // Entries that may wait for the disk. About what one file holds at the size
    // of an ordinary entry, so more would only be rolled away again; at a couple
    // of hundred bytes each it is also the most memory a stalled disk can pin.
    public const int QueueDepth = 8192;

    // The source of this class's own notices in the program log.
    public const string LogSource = "RecentLog";

    private readonly Func<string, TextWriter> _open;
    private readonly Channel<Item> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;

    // Touched only by the pump.
    private TextWriter? _writer;
    private volatile int _active = -1;
    private long _segmentBytes;
    private DateTimeOffset _segmentStart;
    private bool _broken;
    private bool _failureNoted;
    private string? _reopenedAfter;
    private volatile bool _paused;
    private volatile int _filesWritten;

    // Entries the full queue let go of since the last one written. Counted on the
    // threads that log, taken by the pump.
    private int _dropped;

    // True on a thread while it is putting this class's own notice in the program
    // log: that entry must not come back here to be written to the file whose
    // failure it reports.
    [ThreadStatic] private static bool t_noting;

    // How long the pump waits after a failure it didn't expect before it writes
    // again.
    private readonly TimeSpan _retryDelay;

    private readonly record struct Item(LogEntry? Entry, TaskCompletionSource? Barrier);

    // pathA / pathB: the two files. header: the lines each file opens with.
    // open: how a file is opened for writing from empty.
    public RecentLogFile(
        LogService log,
        LogDiagnosticState diagnostics,
        string pathA,
        string pathB,
        Func<IReadOnlyList<string>> header,
        Func<string, TextWriter>? open = null,
        TimeSpan? retryDelay = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        ArgumentException.ThrowIfNullOrEmpty(pathA);
        ArgumentException.ThrowIfNullOrEmpty(pathB);
        _paths = [pathA, pathB];
        _header = header ?? throw new ArgumentNullException(nameof(header));
        _open = open ?? OpenTruncated;
        _paused = diagnostics.AutoCollectLogs;
        // DropOldest: a write to a full queue always succeeds at once, so the
        // thread that logs never waits for the disk, and what is kept is the end
        // of the log, which is the part a hang report needs.
        _queue = Channel.CreateBounded<Item>(
            new BoundedChannelOptions(QueueDepth) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
            OnDroppedFromQueue);
        _pump = Task.Run(PumpAsync);
        _log.EntryAdded += OnEntryAdded;
        _diagnostics.Changed += OnDiagnosticsChanged;
    }

    // The files this process has written so far, the one being written last.
    public IReadOnlyList<string> CurrentFiles
    {
        get
        {
            int written = _filesWritten;
            int active = _active;
            if (written == 0 || active < 0) return [];
            return written == 1 ? [_paths[active]] : [_paths[1 - active], _paths[active]];
        }
    }

    private static TextWriter OpenTruncated(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void OnDiagnosticsChanged()
    {
        _paused = _diagnostics.AutoCollectLogs;
        // Wake the pump so it closes the file now, not at the next entry (there
        // won't be one while paused).
        _queue.Writer.TryWrite(default);
    }

    private void OnEntryAdded(LogEntry entry)
    {
        if (t_noting) return;
        Enqueue(entry);
    }

    // On the producer's thread: nothing here but the hand-off, which never waits.
    internal void Enqueue(LogEntry entry)
    {
        if (_paused) return;
        _queue.Writer.TryWrite(new Item(entry, null));
    }

    // The queue was full and let its oldest item go, on the thread that logged.
    private void OnDroppedFromQueue(Item item)
    {
        if (item.Entry is not null) Interlocked.Increment(ref _dropped);
        // Nothing waits for ever on a barrier the queue no longer holds.
        item.Barrier?.TrySetResult();
    }

    // Completes once everything queued before the call has been written and
    // flushed (or let go of: see QueueDepth).
    internal Task WhenWrittenAsync()
    {
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Item(null, barrier))) barrier.TrySetResult();
        return barrier.Task;
    }

    private async Task PumpAsync()
    {
        ChannelReader<Item> reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            List<TaskCompletionSource>? barriers = null;
            Exception? unexpected = null;
            try
            {
                while (reader.TryRead(out Item item))
                {
                    if (item.Barrier is { } barrier) (barriers ??= new()).Add(barrier);
                    else if (item.Entry is { } entry) Write(entry);
                }
                if (_paused) Close();
                else Flush();
            }
            catch (Exception ex)
            {
                // Write and Flush deal with what a disk does. Anything else (the
                // header's maker, a writer that misbehaves) ended this task for
                // good, silently, with the queue still filling behind it.
                unexpected = ex;
            }
            if (barriers is not null)
                foreach (TaskCompletionSource barrier in barriers) barrier.TrySetResult();
            if (unexpected is null) continue;

            Close();
            _reopenedAfter = unexpected.GetType().Name;
            NoteFailureOnce($"the last-minutes log file could not be written ({unexpected.GetType().Name}: {unexpected.Message}); "
                + $"it is tried again every {_retryDelay.TotalSeconds:0.#} s, and this is the only notice of it");
            try { await Task.Delay(_retryDelay, _stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* the client is closing: write what is left, once, and end */ }
        }
        Close();
    }

    // One notice in the program log for this session's first failure. The entry
    // is kept out of this file (t_noting): written here it would fail the same
    // way and report itself again.
    private void NoteFailureOnce(string message)
    {
        if (_failureNoted) return;
        _failureNoted = true;
        t_noting = true;
        try { _log.Warn(LogSource, message); }
        catch (Exception) { /* a reader of the log threw; the entry is in the ring, and the pump must go on */ }
        finally { t_noting = false; }
    }

    private void Write(LogEntry entry)
    {
        if (_broken || _paused) return;
        try
        {
            if (Interlocked.Exchange(ref _dropped, 0) is > 0 and int dropped)
                WriteLine($"# {(dropped == 1 ? "1 entry" : $"{dropped} entries")} dropped while the disk was busy", entry.Timestamp);
            if (_reopenedAfter is { } failure)
            {
                WriteLine($"# writing failed ({failure}) and starts again here: entries before this line may be missing", entry.Timestamp);
                _reopenedAfter = null;
            }
            WriteLine($"{entry.Timestamp:HH:mm:ss.fff} [{entry.Severity}] {entry.Source}: {entry.Message}", entry.Timestamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Disk full, folder gone, no permission: the file is given up for
            // this session.
            _broken = true;
            Close();
            NoteFailureOnce($"the last-minutes log file is no longer written this session ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // One line into the file being written, rolling to the other first when this
    // one has had its span or its size.
    private void WriteLine(string line, DateTimeOffset at)
    {
        int bytes = Encoding.UTF8.GetByteCount(line) + 1;
        bool roll = _writer is null
            || at - _segmentStart >= SegmentSpan
            || _segmentBytes + bytes > SegmentMaxBytes;
        if (roll) Roll(at);
        _writer!.Write(line);
        _writer.Write('\n');
        _segmentBytes += bytes;
    }

    private void Roll(DateTimeOffset at)
    {
        Close();
        // The other file becomes the one being written only once it has opened:
        // CurrentFiles must not name a file that was never made.
        int next = _active == 0 ? 1 : 0;
        _writer = _open(_paths[next]);
        _active = next;
        _segmentStart = at;
        _segmentBytes = 0;
        if (_filesWritten < 2) _filesWritten++;
        foreach (string line in _header())
        {
            _writer.Write(line);
            _writer.Write('\n');
            _segmentBytes += Encoding.UTF8.GetByteCount(line) + 1;
        }
        string opened = $"# this file from {at:yyyy-MM-dd HH:mm:ss zzz}; the client's other last-minutes file holds the minutes before it";
        _writer.Write(opened);
        _writer.Write('\n');
        _segmentBytes += Encoding.UTF8.GetByteCount(opened) + 1;
    }

    private void Flush()
    {
        try { _writer?.Flush(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _broken = true;
            Close();
            NoteFailureOnce($"the last-minutes log file is no longer written this session ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private void Close()
    {
        TextWriter? writer = _writer;
        _writer = null;
        if (writer is null) return;
        try { writer.Dispose(); }
        catch (Exception) { /* a failed last flush loses its batch; nothing more to do with a closing file, whatever it threw */ }
    }

    public async ValueTask DisposeAsync()
    {
        _log.EntryAdded -= OnEntryAdded;
        _diagnostics.Changed -= OnDiagnosticsChanged;
        if (!_queue.Writer.TryComplete()) return;   // disposed already
        // A pump waiting to retry stops waiting: what is queued gets its one
        // last try now.
        _stopping.Cancel();
        await _pump.ConfigureAwait(false);
        _stopping.Dispose();
    }

    // Delete the last-minutes files of all but the newest `keep` client processes
    // (a process's pair shares everything in its names but the a / b). Returns how
    // many files went. A file another running client still holds is left.
    public static int PruneOldProcesses(string directory, int keep = ProcessesKept,
        Func<int, bool>? isLiveClient = null, DateTime? nowUtc = null)
    {
        if (!Directory.Exists(directory)) return 0;
        isLiveClient ??= IsLiveClient;
        DateTime now = nowUtc ?? DateTime.UtcNow;
        List<IGrouping<string, FileInfo>> processes = new DirectoryInfo(directory)
            .EnumerateFiles("*-recent-?.log", SearchOption.TopDirectoryOnly)
            .GroupBy(f => f.Name[..^"-a.log".Length], StringComparer.Ordinal)
            .OrderByDescending(g => g.Max(f => f.LastWriteTimeUtc))
            .ToList();
        int deleted = 0;
        foreach (IGrouping<string, FileInfo> old in processes.Skip(Math.Max(0, keep)))
        {
            // Another client that is still running owns its pair: on Linux a
            // file being written can be deleted from under its writer, so the
            // lock a delete would hit on Windows is no protection. A pair
            // written in the last few minutes is left too, whatever its
            // process number says (numbers are reused).
            if (old.Max(f => f.LastWriteTimeUtc) > now - RecentlyWritten) continue;
            if (ProcessIdOf(old.Key) is { } pid && isLiveClient(pid)) continue;
            foreach (FileInfo file in old)
            {
                try { file.Delete(); deleted++; }
                catch (IOException) { /* held by a client that is still running */ }
                catch (UnauthorizedAccessException) { /* not ours to remove */ }
            }
        }
        return deleted;
    }

    // Twice what a quiet client can go between writes (it logs a status line
    // every three minutes).
    private static readonly TimeSpan RecentlyWritten = TimeSpan.FromMinutes(10);

    // "{timestamp}-p{pid}-recent" → pid.
    internal static int? ProcessIdOf(string pairName)
    {
        int at = pairName.LastIndexOf("-p", pairName.Length - "-recent".Length, StringComparison.Ordinal);
        if (at < 0) return null;
        int end = pairName.IndexOf('-', at + 2);
        return end > at && int.TryParse(pairName.AsSpan(at + 2, end - at - 2), out int pid) ? pid : null;
    }

    private static bool IsLiveClient(int pid)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited
                && process.ProcessName.Contains("MudPlay", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No such process, or one we may not look at: not a client of ours.
            return false;
        }
    }
}
