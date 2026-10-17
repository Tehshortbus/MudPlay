using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

public sealed class SysStatusProbeTests
{
    private sealed class Harness
    {
        public SysRoomStatusParser Parser { get; } = new();
        // The game's output, fed the way the client receives it.
        public ServerFeed Feed { get; } = new();
        public SysStatusProbe Probe { get; }
        public List<string> Sent { get; } = new();
        public bool CapabilityEnabled { get; set; } = true;

        // The probe's clock. It moves only when a test moves it, so the back-off
        // window can't open or close on scheduler timing.
        public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        // Fresh per probe: a reused completion source would leave the second
        // query pre-timed-out by the first.
        private TaskCompletionSource _currentDelay = new();

        public Harness()
        {
            Parser.AttachLineExtractor(Feed.Lines);
            // The "timeout" completes only when a test fires it explicitly,
            // so a slow machine can never flake these.
            Probe = new SysStatusProbe(Parser, () => CapabilityEnabled, () => Now, delay: _ =>
            {
                _currentDelay = new TaskCompletionSource();
                return _currentDelay.Task;
            });
            Probe.SetWireSender(bytes =>
            {
                Sent.Add(Encoding.Latin1.GetString(bytes));
                // The real wire path routes outbound bytes past the parser too.
                Parser.ObserveOutbound(bytes);
            });
        }

        public void FireTimeout() => _currentDelay.TrySetResult();

        public void ReplyWithRoom(int map, int room)
        {
            Feed.Line($"Room {room}  Map: {map}");
            Feed.Line("Monsters: None");
            Feed.Line("[HP=100]:");
        }
    }

    [Fact]
    public async Task ResolvesToTheParsedRoom()
    {
        Harness h = new();

        Task<SysRoomStatus?> query = h.Probe.QueryAsync();
        h.ReplyWithRoom(1, 2187);
        SysRoomStatus? result = await query;

        Assert.NotNull(result);
        Assert.Equal(new RoomKey(1, 2187), result!.Room);
        Assert.Equal(new[] { "sys st\r\n" }, h.Sent);
    }

    [Fact]
    public async Task SendsNothingWhenCapabilityDisabled()
    {
        Harness h = new() { CapabilityEnabled = false };

        SysRoomStatus? result = await h.Probe.QueryAsync();

        Assert.Null(result);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task TimeoutAutoDisablesForTheSession()
    {
        Harness h = new();

        Task<SysRoomStatus?> query = h.Probe.QueryAsync();
        h.FireTimeout();

        Assert.Null(await query);
        Assert.True(h.Probe.AutoDisabled);
        Assert.False(h.Probe.Available);
    }

    [Fact]
    public async Task AutoDisabledProbeSendsNothingFurther()
    {
        // The point of auto-disable: one failed probe, not one per recovery.
        Harness h = new();

        Task<SysRoomStatus?> first = h.Probe.QueryAsync();
        h.FireTimeout();
        await first;

        Assert.Null(await h.Probe.QueryAsync());
        Assert.Single(h.Sent);
    }

    [Fact]
    public async Task ResetAutoDisableReEnablesProbing()
    {
        Harness h = new();

        Task<SysRoomStatus?> first = h.Probe.QueryAsync();
        h.FireTimeout();
        await first;
        h.Probe.ResetAutoDisable();

        Assert.True(h.Probe.Available);
        Task<SysRoomStatus?> second = h.Probe.QueryAsync();
        h.ReplyWithRoom(3, 12);

        Assert.Equal(new RoomKey(3, 12), (await second)!.Room);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public async Task ConcurrentQueriesShareOneProbe()
    {
        Harness h = new();

        Task<SysRoomStatus?> a = h.Probe.QueryAsync();
        Task<SysRoomStatus?> b = h.Probe.QueryAsync();
        h.ReplyWithRoom(1, 5);

        Assert.Equal(new RoomKey(1, 5), (await a)!.Room);
        Assert.Equal(new RoomKey(1, 5), (await b)!.Room);
        Assert.Single(h.Sent);
    }

    [Fact]
    public async Task UnavailableWithoutAWireSender()
    {
        SysRoomStatusParser parser = new();
        SysStatusProbe probe = new(parser, () => true);

        Assert.False(probe.Available);
        Assert.Null(await probe.QueryAsync());
    }

    [Fact]
    public async Task AutoDisableExpiresSoOneHiccupDoesNotCostTheSession()
    {
        // A single unanswered probe used to switch sysop status off until the
        // profile reloaded. One user lost ~7 hours of position recovery to a
        // reply that arrived slightly late — the same command answered fine 16
        // minutes later. It backs off now, then retries.
        Harness h = new();

        Task<SysRoomStatus?> first = h.Probe.QueryAsync();
        h.FireTimeout();
        Assert.Null(await first);
        Assert.True(h.Probe.AutoDisabled);
        Assert.False(h.Probe.Available);

        // Still off right up to the end of the window...
        h.Now += h.Probe.AutoDisableFor - TimeSpan.FromTicks(1);
        Assert.True(h.Probe.AutoDisabled);
        Assert.False(h.Probe.Available);

        // ...and back on the moment it has run out.
        h.Now += TimeSpan.FromTicks(1);
        Assert.False(h.Probe.AutoDisabled);
        Assert.True(h.Probe.Available);
        Task<SysRoomStatus?> second = h.Probe.QueryAsync();
        h.ReplyWithRoom(2, 40);
        Assert.Equal(new RoomKey(2, 40), (await second)!.Room);
    }

    [Fact]
    public void ScanWindowOutlastsTheProbeTimeout()
    {
        // The parser must not bin a block the probe is still waiting for. A 5s
        // window against a 6s timeout meant a reply landing at 5.5s was discarded
        // and read as "no sysop powers".
        SysRoomStatusParser parser = new();
        SysStatusProbe probe = new(parser, () => true);

        Assert.True(parser.ExpectingBlockWindow >= probe.Timeout);
    }

    [Fact]
    public async Task OnceItHasWorked_ATimeoutNeverDisablesItAgain()
    {
        // If it works at all, it works. A success settles the only question
        // auto-disable asks — does this account have the privilege — so every
        // later timeout is lag or a mangled block, not a missing power.
        Harness h = new();

        Task<SysRoomStatus?> ok = h.Probe.QueryAsync();
        h.ReplyWithRoom(1, 100);
        Assert.NotNull(await ok);

        Task<SysRoomStatus?> late = h.Probe.QueryAsync();
        h.FireTimeout();
        Assert.Null(await late);

        Assert.False(h.Probe.AutoDisabled);
        Assert.True(h.Probe.Available);

        // And it keeps working.
        Task<SysRoomStatus?> again = h.Probe.QueryAsync();
        h.ReplyWithRoom(1, 101);
        Assert.Equal(new RoomKey(1, 101), (await again)!.Room);
    }

    [Fact]
    public async Task AHandTypedSysStAlsoCountsAsProof()
    {
        // The parser reports any block it sees, solicited or not. A user typing
        // `sys st` themselves proves the privilege just as well as our probe.
        Harness h = new();
        h.Parser.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("sys st\r\n"));
        h.ReplyWithRoom(1, 55);

        Task<SysRoomStatus?> timedOut = h.Probe.QueryAsync();
        h.FireTimeout();
        Assert.Null(await timedOut);

        Assert.False(h.Probe.AutoDisabled);
    }

    [Fact]
    public async Task ResetAutoDisable_ClearsTheProvenLatch_SoANewCharacterCanAutoDisableAgain()
    {
        // The probe is app-scoped but sysop power is per-character. Without clearing
        // the "proven" latch on profile load, a non-powered alt inherits a powered
        // character's proof and can NEVER auto-disable — every recovery escalation
        // eats a full probe timeout for the rest of the session. ResetAutoDisable
        // (fired on ProfileLoaded) must clear _provenAvailable, not just the timer.
        Harness h = new();

        // Character A proves the privilege.
        Task<SysRoomStatus?> ok = h.Probe.QueryAsync();
        h.ReplyWithRoom(1, 100);
        Assert.NotNull(await ok);
        Assert.False(h.Probe.AutoDisabled);

        // Switch characters — ProfileLoaded resets the probe.
        h.Probe.ResetAutoDisable();

        // Character B is NOT powered: the first timeout must now auto-disable it
        // (before the fix, the inherited proof kept it permanently enabled).
        Task<SysRoomStatus?> late = h.Probe.QueryAsync();
        h.FireTimeout();
        Assert.Null(await late);
        Assert.True(h.Probe.AutoDisabled);
    }
}
