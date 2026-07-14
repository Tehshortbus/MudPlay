using System.Diagnostics;

namespace FujinTerm.Services;

// Samples the process's memory footprint to its own rolling on-disk file so an
// all-night session leaves a trail of how memory moved over time. That trail is
// what tells a real managed-heap leak (the GC heap climbs sample over sample)
// apart from working-set creep (the OS working set grows while the managed heap
// stays flat — the runtime holding pages it hasn't returned, not a leak).
//
// The per-generation split (committed / gen2-size / loh-size / loh-frag) further
// separates the two look-alikes that both read as "ws climbing": a Large-Object-
// Heap fragmentation ratchet (committed climbs and loh-frag is high while
// gen2-size stays flat — workstation GC holding decommitted LOH pages) vs. a
// genuine gen2 leak (gen2-size itself climbs sample over sample). Without the
// split the raw gc-heap number can't tell them apart.
//
// Deliberately kept OUT of the program log: a sample a minute all night would
// bury the operator-facing entries, and these numbers are only interesting when
// chasing memory. It lands in its own Data/Logs/{ts}-memory.log instead, so the
// program log stays clean while the memory history is a file away.
//
// Gated by LogDiagnosticState.AutoCollectLogs: the timer always ticks, but each
// sample is dropped while the flag is off (the default), so a normal session
// writes no file. Flipping the Log-pane "Auto-collect logs" toggle on opens a
// fresh file with the header + a t0 baseline sample; off closes it. One
// instance per app session, instantiated once in AppServices.
public sealed class MemoryUsageLog : IAsyncDisposable
{
    // A sample a minute keeps an all-night file tiny (~500 lines over 8 h) while
    // still resolving a slow crawl.
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    private const string HeaderLine =
        "# columns: working-set private managed-heap gc-heap committed fragmented " +
        "gen2-size loh-size loh-frag poh-size gen0/1/2-collections";

    private readonly LogDiagnosticState _diagnostics;
    private readonly Process _process;
    private readonly System.Threading.Timer _timer;

    // Guards the writer reference against the sampling threadpool thread racing
    // a toggle-driven open/close.
    private readonly object _gate = new();
    private DebugLogWriter? _writer;
    private bool _broken;

    public MemoryUsageLog(LogDiagnosticState diagnostics)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _process = Process.GetCurrentProcess();
        _diagnostics.Changed += OnDiagnosticsChanged;
        // Timer runs for the whole session; Sample no-ops while the writer is
        // closed, so the cost of collection is only paid when AutoCollectLogs is on.
        _timer = new System.Threading.Timer(Sample, null, SampleInterval, SampleInterval);
        SyncWriter();
    }

    // True while the on-disk file is open and receiving samples.
    public bool IsCollecting
    {
        get { lock (_gate) { return _writer is not null; } }
    }

    // Open or close the writer to match AutoCollectLogs. On open, write the
    // header and take an immediate t0 baseline so the file has a datum before
    // the first interval elapses. Once _broken we stay closed for the session.
    private void SyncWriter()
    {
        bool opened = false;
        lock (_gate)
        {
            if (_broken) return;
            bool want = _diagnostics.AutoCollectLogs;
            if (want && _writer is null)
            {
                try
                {
                    _writer = new DebugLogWriter("memory");
                    _writer.WriteLine(HeaderLine);
                    opened = true;
                }
                catch { _broken = true; }
            }
            else if (!want && _writer is not null)
            {
                _writer.Dispose();
                _writer = null;
            }
        }
        if (opened) Sample(null);   // t0 baseline immediately after opening
    }

    private void OnDiagnosticsChanged() => SyncWriter();

    // Runs on a threadpool thread (Timer callback). The _gate lock guards the
    // writer swap, so no marshalling is needed. No-op while the writer is closed.
    private void Sample(object? _)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                _process.Refresh();   // WorkingSet64 / PrivateMemorySize64 are cached until this
                GCMemoryInfo gc = GC.GetGCMemoryInfo();
                ReadOnlySpan<GCGenerationInfo> gens = gc.GenerationInfo;
                // GenerationInfo is [gen0, gen1, gen2, LOH, POH] on .NET Core 3.0+;
                // the length guards keep a leaner runtime from throwing. Sizes are
                // "after the last GC of this kind", which is exactly the settled
                // high-water we want to trend.
                long gen2Size = gens.Length > 2 ? gens[2].SizeAfterBytes : 0;
                long lohSize  = gens.Length > 3 ? gens[3].SizeAfterBytes : 0;
                long lohFrag  = gens.Length > 3 ? gens[3].FragmentationAfterBytes : 0;
                long pohSize  = gens.Length > 4 ? gens[4].SizeAfterBytes : 0;
                _writer.WriteLine(
                    $"ws={Mb(_process.WorkingSet64)} priv={Mb(_process.PrivateMemorySize64)} " +
                    $"managed={Mb(GC.GetTotalMemory(false))} heap={Mb(gc.HeapSizeBytes)} " +
                    $"committed={Mb(gc.TotalCommittedBytes)} frag={Mb(gc.FragmentedBytes)} " +
                    $"gen2sz={Mb(gen2Size)} loh={Mb(lohSize)} lohfrag={Mb(lohFrag)} poh={Mb(pohSize)} " +
                    $"gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)}");
            }
            catch
            {
                // Disk full / handle lost / permission flap. Stop sampling and stay
                // silent — same rationale as ProgramLogFile: losing the trail is
                // acceptable, wedging on it is not.
                _broken = true;
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    private static string Mb(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "MB";

    public async ValueTask DisposeAsync()
    {
        _diagnostics.Changed -= OnDiagnosticsChanged;
        await _timer.DisposeAsync();
        _process.Dispose();
        DebugLogWriter? writer;
        lock (_gate)
        {
            writer = _writer;
            _writer = null;
        }
        if (writer is not null) await writer.DisposeAsync();
    }
}
