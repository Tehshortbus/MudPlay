using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for the persistable shape of <see cref="PartySettings"/>.
/// The Settings.Party section VM itself does I/O through
/// <see cref="Services.ProfileService"/> and the global
/// <see cref="Services.AppServices"/> singleton — exercising it cleanly
/// from a unit test would require swapping the singleton, which is out
/// of scope. The settings round-trip is the load-bearing piece worth
/// pinning here.
/// </summary>
public sealed class PartySectionViewModelTests
{
    [Fact]
    public void PartySettings_RoundTripsThroughJson()
    {
        PartySettings src = new()
        {
            ParPollFrequencySec        = 12,
            AutoInviteReconnecting     = false,
            ResetStatisticsOnLoopStart = false,
            Rank                       = PartyRank.Back,
        };

        string json = JsonSerializer.Serialize(src);
        PartySettings? back = JsonSerializer.Deserialize<PartySettings>(json);

        Assert.NotNull(back);
        Assert.Equal(12,           back!.ParPollFrequencySec);
        Assert.False(back.AutoInviteReconnecting);
        Assert.False(back.ResetStatisticsOnLoopStart);
        Assert.Equal(PartyRank.Back, back.Rank);
    }

    [Fact]
    public void PartySettings_Defaults_MatchPhase6Spec()
    {
        // Phase 6 spec: par poll 5 s, auto-invite on, auto-exp-reset on,
        // rank Mid. The defaults need to be stable because a freshly
        // loaded profile with no Party entry falls through to these
        // values for every consumer (Settings.Party UI + every Phase 6
        // service the VM pushes to).
        PartySettings dto = new();
        Assert.Equal(5,            dto.ParPollFrequencySec);
        Assert.True(dto.AutoInviteReconnecting);
        Assert.True(dto.ResetStatisticsOnLoopStart);
        Assert.Equal(PartyRank.Mid, dto.Rank);
        // "If leading, wait only" — drives the disconnect grace window
        // used by the Re-invite lost party members flow. Default 90 s.
        Assert.Equal(90,           dto.IfLeadingWaitTotalSec);
        // Vitals gate disabled by default — 0 means PartyVitalsWatcher
        // never holds the loop until the user sets a threshold.
        Assert.Equal(0,            dto.WaitIfMemberBelowPercent);
        // Party-scoped max-monsters default mirrors the Combat cap (no-op
        // until tightened).
        Assert.Equal(20,           dto.MaxMonstersWhenPartying);
    }

    [Fact]
    public void IfLeadingWaitTotalSec_RoundTripsThroughJson()
    {
        PartySettings src = new() { IfLeadingWaitTotalSec = 305 };
        string json = JsonSerializer.Serialize(src);
        PartySettings? back = JsonSerializer.Deserialize<PartySettings>(json);
        Assert.NotNull(back);
        Assert.Equal(305, back!.IfLeadingWaitTotalSec);
    }

    [Fact]
    public void WaitIfMemberBelowPercent_RoundTripsThroughJson()
    {
        PartySettings src = new() { WaitIfMemberBelowPercent = 65 };
        string json = JsonSerializer.Serialize(src);
        PartySettings? back = JsonSerializer.Deserialize<PartySettings>(json);
        Assert.NotNull(back);
        Assert.Equal(65, back!.WaitIfMemberBelowPercent);
    }
}
