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
// One writer + one timer per app session, instantiated once in AppServices and
// living for the whole process. The file is covered by the same
// DebugLogWriter.PruneOldLogs retention sweep as every other .log, and each line
// is flushed immediately (DebugLogWriter runs AutoFlush) so a hang / kill -9
// still leaves every sample up to the crash on disk.
public sealed class MemoryUsageLog : IAsyncDisposable
{
    // A sample a minute keeps an all-night file tiny (~500 lines over 8 h) while
    // still resolving a slow crawl.
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    private readonly DebugLogWriter _writer;
    private readonly Process _process;
    private readonly System.Threading.Timer _timer;
    private bool _broken;

    public MemoryUsageLog()
    {
        _writer = new DebugLogWriter("memory");
        _process = Process.GetCurrentProcess();
        _writer.WriteLine(
            "# columns: working-set private managed-heap gc-heap committed fragmented " +
            "gen2-size loh-size loh-frag poh-size gen0/1/2-collections");
        Sample(null);   // t0 baseline before the first interval elapses
        _timer = new System.Threading.Timer(Sample, null, SampleInterval, SampleInterval);
    }

    // Full path of the on-disk memory log for this session.
    public string Path => _writer.Path;

    // Runs on a threadpool thread (Timer callback). DebugLogWriter is internally
    // locked, so no marshalling is needed.
    private void Sample(object? _)
    {
        if (_broken) return;
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
        }
    }

    private static string Mb(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "MB";

    public async ValueTask DisposeAsync()
    {
        await _timer.DisposeAsync();
        _process.Dispose();
        await _writer.DisposeAsync();
    }
}
