using System.Collections.Generic;
using System.Text;
using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

public sealed class AutoSearchManagerTests
{
    private static string Decode(byte[] b) => Encoding.Latin1.GetString(b).TrimEnd('\r');

    [Fact]
    public void OnRoomChanged_WhenEnabled_SendsBareSea()
    {
        var mgr = new AutoSearchManager(isEnabled: () => true);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();

        Assert.Single(mgr.LastSentForTests);
        Assert.Equal("sea", Decode(mgr.LastSentForTests[0]));
    }

    [Fact]
    public void OnRoomChanged_WhenDisabled_SendsNothing()
    {
        var mgr = new AutoSearchManager(isEnabled: () => false);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void OnRoomChanged_FiresOncePerCall()
    {
        var mgr = new AutoSearchManager(isEnabled: () => true);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();
        mgr.OnRoomChanged();
        mgr.OnRoomChanged();

        Assert.Equal(3, mgr.LastSentForTests.Count);
        Assert.All(mgr.LastSentForTests, b => Assert.Equal("sea", Decode(b)));
    }

    [Fact]
    public void OnRoomChanged_ReadsGateLive()
    {
        bool enabled = false;
        var mgr = new AutoSearchManager(isEnabled: () => enabled);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();          // off — no send
        enabled = true;
        mgr.OnRoomChanged();          // on — one send

        Assert.Single(mgr.LastSentForTests);
    }

    [Fact]
    public void Send_ReachesBoundSink()
    {
        var sink = new List<byte[]>();
        var mgr = new AutoSearchManager(isEnabled: () => true);
        mgr.SetWireSender(sink.Add);

        mgr.OnRoomChanged();

        Assert.Single(sink);
        Assert.Equal("sea", Decode(sink[0]));
    }

    [Fact]
    public void OnRoomChanged_MasterOffDemandActive_SendsSea()
    {
        var mgr = new AutoSearchManager(
            isEnabled: () => false,
            isDemandActive: () => true);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();

        Assert.Single(mgr.LastSentForTests);
        Assert.Equal("sea", Decode(mgr.LastSentForTests[0]));
    }

    [Fact]
    public void OnRoomChanged_BothGatesOff_SendsNothing()
    {
        var mgr = new AutoSearchManager(
            isEnabled: () => false,
            isDemandActive: () => false);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void OnRoomChanged_ReadsDemandGateLive()
    {
        bool demand = false;
        var mgr = new AutoSearchManager(
            isEnabled: () => false,
            isDemandActive: () => demand);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();          // no demand — no send
        demand = true;
        mgr.OnRoomChanged();          // demand — one send

        Assert.Single(mgr.LastSentForTests);
    }
}
