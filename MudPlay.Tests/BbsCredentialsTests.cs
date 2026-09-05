using System.Text.Json;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// The three per-BBS sysop-power flags, plus the one-way migration of the old
// combined "HasSysopPowers" flag that releases through 3.50.x persisted.
public sealed class BbsCredentialsTests
{
    [Fact]
    public void LegacyHasSysopPowers_True_MigratesToSysopStatus()
    {
        BbsCredentials cred = JsonSerializer.Deserialize<BbsCredentials>(
            """{ "HasSysopPowers": true }""")!;
        Assert.True(cred.SysopStatus);   // the one power #461 wired the old flag to
        Assert.False(cred.SysopMap);
        Assert.False(cred.SysopGodLives);
    }

    [Fact]
    public void LegacyHasSysopPowers_False_LeavesAllOff()
    {
        BbsCredentials cred = JsonSerializer.Deserialize<BbsCredentials>(
            """{ "HasSysopPowers": false }""")!;
        Assert.False(cred.SysopStatus);
        Assert.False(cred.SysopMap);
        Assert.False(cred.SysopGodLives);
    }

    [Fact]
    public void ThreeFlags_RoundTrip_AndLegacyNeverWrittenBack()
    {
        var cred = new BbsCredentials { SysopMap = true, SysopStatus = false, SysopGodLives = true };
        string json = JsonSerializer.Serialize(cred);

        BbsCredentials back = JsonSerializer.Deserialize<BbsCredentials>(json)!;
        Assert.True(back.SysopMap);
        Assert.False(back.SysopStatus);
        Assert.True(back.SysopGodLives);

        // The legacy shim is set-only, so a new save never re-emits it.
        Assert.DoesNotContain("HasSysopPowers", json);
    }
}
