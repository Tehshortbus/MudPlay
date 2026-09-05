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
        public SysStatusProbe Probe { get; }
        public List<string> Sent { get; } = new();
        public bool CapabilityEnabled { get; set; } = true;

        // Fresh per probe: a reused completion source would leave the second
        // query pre-timed-out by the first.
        private TaskCompletionSource _currentDelay = new();

        public Harness()
        {
            Probe = new SysStatusProbe(Parser, () => CapabilityEnabled)
            {
                // The "timeout" completes only when a test fires it explicitly,
                // so a slow machine can never flake these.
                DelayProvider = _ =>
                {
                    _currentDelay = new TaskCompletionSource();
                    return _currentDelay.Task;
                },
            };
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
            Parser.FeedTestLine($"Room {room}  Map: {map}");
            Parser.FeedTestLine("Monsters: None");
            Parser.FeedTestLine("[HP=100]:", isPromptLine: true);
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
}
