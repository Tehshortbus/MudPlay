using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The call stacks a Hang report leads with, read from a snapshot of this very
// process. On Windows a thread parked in a method of its own is found in it, by
// name; elsewhere none are read and the capture says so.
public sealed class ThreadStacksTests
{
    [Fact]
    public void A_parked_threads_method_is_named_on_Windows_and_elsewhere_none_are_read()
    {
        using ManualResetEventSlim parked = new();
        using ManualResetEventSlim release = new();
        int parkedId = 0;
        Thread thread = new(() => Parked.Wait(parked, release, ref parkedId)) { IsBackground = true };
        thread.Start();
        parked.Wait(TimeSpan.FromSeconds(10));
        try
        {
            ThreadStacks.Capture capture = ThreadStacks.Read(Volatile.Read(ref parkedId), TimeSpan.FromSeconds(60));

            if (!OperatingSystem.IsWindows())
            {
                Assert.Empty(capture.Stacks);
                Assert.Equal("call stacks are read on Windows only", capture.Missing);
                return;
            }
            Assert.Null(capture.Missing);
            ThreadStacks.Stack ui = Assert.IsType<ThreadStacks.Stack>(capture.Ui);
            Assert.Contains(ui.Frames, f => f.Contains("Parked.Wait", StringComparison.Ordinal));
            Assert.Same(ui, capture.Stacks[0]);
        }
        finally
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    private static class Parked
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Wait(ManualResetEventSlim parked, ManualResetEventSlim release, ref int id)
        {
            id = Environment.CurrentManagedThreadId;
            parked.Set();
            release.Wait();
        }
    }
}
