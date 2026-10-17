using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// PartySettings' and a party BuffSlot's defaults: what a profile with no Party
// entry runs on.
public sealed class PartySettingsDefaultsTests
{
    [Fact]
    public void PartySettings_Defaults_MatchSpec()
    {
        // Spec: par poll 5 s, auto-invite on, auto-exp-reset on,
        // rank Mid. The defaults need to be stable because a freshly
        // loaded profile with no Party entry falls through to these
        // values for every consumer (Settings.Party UI + every party
        // service the VM pushes to).
        PartySettings dto = new();
        Assert.Equal(5,            dto.ParPollFrequencySec);
        // par on the timer alone: a profile saved before the round triggers
        // existed keeps polling exactly as it did.
        Assert.True(dto.ParPollOnTimer);
        Assert.False(dto.ParPollAfterCombatRound);
        Assert.False(dto.ParPollIncludeWitnessedRounds);
        Assert.False(dto.ParPollOnUnknownDamage);
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
        // Party-bless gating defaults OFF — both are opt-in overrides, matching
        // self-bless. The normal cadence buffs the party while moving / idle /
        // idly resting and holds only during combat and a triggered recovery
        // rest unless the user opts in.
        Assert.False(dto.BlessWhileResting);
        Assert.False(dto.BlessDuringCombat);
        // Both nag master-switches default ON: @join follow-ups and on-join
        // @health round-trips go out unless the user turns them off.
        Assert.True(dto.SendJoinToInvited);
        Assert.True(dto.SendHealthToMembers);
    }

    [Fact]
    public void PartyBuffSlot_Defaults()
    {
        BuffSlot s = new();
        Assert.Null(s.Spell);
        Assert.True(s.WholePartyOn);             // whole-party buffs default on
        Assert.False(s.AllMembers);
        Assert.Empty(s.Targets);
        Assert.Equal(SpellsSettings.DefaultBlessRecastMarginSec, s.RecastMarginSec);
    }
}
