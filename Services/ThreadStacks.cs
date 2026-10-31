using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace MudPlay.Services;

// The managed call stacks of the client's threads at one moment, for a Hang
// report. The measured scopes name only the code they wrap; a freeze in layout,
// a posted lambda or a wait on a lock falls between them, and the report could
// say no more than "no measured scope was open". The UI thread's stack names the
// method whatever it is.
//
// Windows only: there the OS snapshots the process (copy-on-write, the client
// runs on) and the snapshot is read. On Linux and macOS a snapshot is a core dump
// of the whole process written to disk first, too heavy in a freeze.
//
// The runtime's own debug library (mscordaccore.dll, beside the runtime, or in
// the folder a single-file client unpacks to) reads it, so nothing is fetched
// from a symbol server.
public static class ThreadStacks
{
    public const int UiFramesKept = 80;
    public const int OtherFramesKept = 20;

    // One thread's stack, innermost frame first.
    public sealed record Stack(int ManagedId, uint OsId, bool IsUi, string? Role, IReadOnlyList<string> Frames, int MoreFrames);

    // Stacks is empty when Missing says why none were read.
    public sealed record Capture(IReadOnlyList<Stack> Stacks, string? Missing, TimeSpan Took)
    {
        public Stack? Ui => Stacks.FirstOrDefault(s => s.IsUi);
    }

    // Read on a thread of its own and given up after timeout: the reader is left
    // to finish or not, and the report goes without.
    public static Capture Read(int uiManagedThreadId, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
            return new([], "call stacks are read on Windows only", TimeSpan.Zero);
        Capture? result = null;
        // Not disposed: a reader that outlives the wait still sets it.
        ManualResetEventSlim done = new();
        DateTime started = DateTime.UtcNow;
        System.Threading.Thread reader = new(() =>
        {
            Capture read;
            try { read = ReadNow(uiManagedThreadId, started); }
            // Any failure of the library or the snapshot costs the report its stacks, never the report.
            catch (Exception ex) { read = new([], $"reading them failed ({ex.GetType().Name}: {ex.Message})", DateTime.UtcNow - started); }
            Volatile.Write(ref result, read);
            done.Set();
        })
        { IsBackground = true, Name = "Hang report stacks" };
        reader.Start();
        done.Wait(timeout);
        return Volatile.Read(ref result)
            ?? new([], $"they were not read within {HangReport.Seconds(timeout)}", DateTime.UtcNow - started);
    }

    private static Capture ReadNow(int uiManagedThreadId, DateTime started)
    {
        if (DacPath() is not { } dac)
            return new([], "the runtime's debug library (mscordaccore.dll) is not beside it", DateTime.UtcNow - started);
        using DataTarget target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        if (target.ClrVersions.Length == 0)
            return new([], "the snapshot holds no .NET runtime", DateTime.UtcNow - started);
        using ClrRuntime runtime = target.ClrVersions[0].CreateRuntime(dac);
        List<Stack> stacks = new();
        foreach (ClrThread thread in runtime.Threads)
        {
            if (!thread.IsAlive) continue;
            bool ui = thread.ManagedThreadId == uiManagedThreadId;
            int keep = ui ? UiFramesKept : OtherFramesKept;
            List<string> frames = new();
            int more = 0;
            foreach (ClrStackFrame frame in thread.EnumerateStackTrace())
            {
                if (frames.Count < keep) frames.Add(Describe(frame));
                else more++;
            }
            if (frames.Count == 0 && !ui) continue;
            stacks.Add(new Stack(thread.ManagedThreadId, thread.OSThreadId, ui,
                thread.IsFinalizer ? "finalizer" : thread.IsGc ? "GC" : null, frames, more));
        }
        stacks.Sort((a, b) => a.IsUi != b.IsUi ? (a.IsUi ? -1 : 1) : a.ManagedId.CompareTo(b.ManagedId));
        return new(stacks, null, DateTime.UtcNow - started);
    }

    private static string Describe(ClrStackFrame frame) =>
        frame.Method?.Signature ?? $"[{frame.FrameName ?? frame.Kind.ToString()}]";

    private static string? DacPath()
    {
        foreach (string folder in new[] { RuntimeEnvironment.GetRuntimeDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(folder)) continue;
            string path = Path.Combine(folder, "mscordaccore.dll");
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
