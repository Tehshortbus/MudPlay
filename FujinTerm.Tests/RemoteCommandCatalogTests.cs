using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Pins the bearfather wiki → PlayerRemoteControls mapping so a typo
/// or a future "let's just change @hangup to AlterSettings" edit
/// fails CI before it reaches a user's permissions grid. Sourced
/// from https://kyau.net/wiki/MajorMUD:Remote_Commands — when the
/// wiki adds a new command, add it here too.
/// </summary>
public sealed class RemoteCommandCatalogTests
{
    // ===== Basic Commands =====

    [Theory]
    [InlineData("@version",     PlayerRemoteControls.QueryVersion)]
    [InlineData("@help",        PlayerRemoteControls.QueryVersion)]
    [InlineData("@health",      PlayerRemoteControls.QueryHealthStatus)]
    [InlineData("@status",      PlayerRemoteControls.QueryHealthStatus)]
    [InlineData("@lives",       PlayerRemoteControls.QueryHealthStatus)]
    [InlineData("@exp",         PlayerRemoteControls.QueryExperience)]
    [InlineData("@level",       PlayerRemoteControls.QueryExperience)]
    [InlineData("@where",       PlayerRemoteControls.QueryLocation)]
    [InlineData("@path",        PlayerRemoteControls.QueryLocation)]
    [InlineData("@who",         PlayerRemoteControls.QueryLocation)]
    [InlineData("@what",        PlayerRemoteControls.QueryInventory)]
    [InlineData("@wealth",      PlayerRemoteControls.QueryInventory)]
    [InlineData("@enc",         PlayerRemoteControls.QueryInventory)]
    [InlineData("@have",        PlayerRemoteControls.QueryInventory)]
    public void Query_FamilyCommands_MapToCorrectCategory(string cmd, PlayerRemoteControls expected)
        => Assert.Equal(expected, Lookup(cmd));

    // ===== Action / Inventory bulk =====

    [Theory]
    [InlineData("@get-all")]
    [InlineData("@drop-all")]
    [InlineData("@deposit-all")]
    [InlineData("@do")]
    [InlineData("@trap")]
    [InlineData("@train")]
    [InlineData("@equip")]   // gear-set apply (wire form is @equip-<set>)
    public void ExecuteCommands_BulkActionsAndDo(string cmd)
        => Assert.Equal(PlayerRemoteControls.ExecuteCommands, Lookup(cmd));

    // ===== Party invite signals =====

    [Theory]
    [InlineData("@invite")]
    [InlineData("@join")]
    public void RequestInvite_ApplicableCommands(string cmd)
        => Assert.Equal(PlayerRemoteControls.RequestInvite, Lookup(cmd));

    // ===== Movement / loops =====

    [Theory]
    [InlineData("@goto")]
    [InlineData("@loop")]
    [InlineData("@lair")]
    [InlineData("@stop")]
    [InlineData("@rego")]
    public void MovePlayer_NavigationCommands(string cmd)
        => Assert.Equal(PlayerRemoteControls.MovePlayer, Lookup(cmd));

    [Theory]
    [InlineData("@looponce")]
    [InlineData("@roam")]
    [InlineData("@seen")]
    [InlineData("@panic")]
    [InlineData("@panic!")]
    [InlineData("@attack-last")]
    [InlineData("@blind")]
    [InlineData("@diseased")]
    [InlineData("@held")]
    // @equip-all was a misfit: equipping is per-slot (you can't wear "all"
    // items), and the @equip- namespace now belongs to gear-set apply
    // (@equip-<set>). Dropped in favour of the prefix-routed @equip.
    [InlineData("@equip-all")]
    // @home (mudop-only per the wiki) is dropped — FujinTerm won't wire it,
    // so it must not linger in the catalog advertising an unhandled command.
    [InlineData("@home")]
    public void RemovedFromCatalog(string cmd)
        => Assert.False(RemoteCommandCatalog.TryGetCategory(cmd, out _));

    // ===== Toggle Settings (12 auto-* + @settings + @reset + @atkprio + @atkorder) =====

    [Theory]
    [InlineData("@atkprio")]
    [InlineData("@atkorder")]
    [InlineData("@auto-all")]
    [InlineData("@auto-combat")]
    [InlineData("@auto-nuke")]
    [InlineData("@auto-heal")]
    [InlineData("@auto-bless")]
    [InlineData("@auto-light")]
    [InlineData("@auto-cash")]
    [InlineData("@auto-get")]
    [InlineData("@auto-sneak")]
    [InlineData("@auto-hide")]
    [InlineData("@auto-search")]
    [InlineData("@settings")]
    [InlineData("@reset")]
    public void AlterSettings_TogglesAndState(string cmd)
        => Assert.Equal(PlayerRemoteControls.AlterSettings, Lookup(cmd));

    // ===== Disconnect / divert / sysop =====

    [Theory]
    [InlineData("@hangup")]
    [InlineData("@relog")]
    public void HangupDisconnect_LineKills(string cmd)
        => Assert.Equal(PlayerRemoteControls.HangupDisconnect, Lookup(cmd));

    [Fact]
    public void Divert_RoutesToDivertConversations()
        => Assert.Equal(PlayerRemoteControls.DivertConversations, Lookup("@divert"));

    [Fact]
    public void Suicide_RoutesToSysopCommands()
        // The lone SysopCommands member after @home was dropped — an
        // irreversible action gated under "Elevated Commands".
        => Assert.Equal(PlayerRemoteControls.SysopCommands, Lookup("@suicide"));

    // ===== Party-whitelist (None) =====

    [Theory]
    [InlineData("@wait")]
    [InlineData("@ok")]
    [InlineData("@comeback")]
    [InlineData("@forget")]
    [InlineData("@share")]
    public void PartyWhitelist_NoneCategory(string cmd)
        // None = "any active party member" — engine routes these through
        // the party-whitelist branch instead of the per-player flag.
        // @comeback / @forget additionally honour the reconnect-recovery
        // eligibility hooks for a member no longer sharing a party row.
        => Assert.Equal(PlayerRemoteControls.None, Lookup(cmd));

    [Fact]
    public void Kill_RoutesToExecuteCommands_NotPartyWhitelist()
        // @kill <target> asks a member to attack a named target on the
        // sender's behalf — an action request, not a party coordination
        // signal — so it's per-player ExecuteCommands-gated like @do / @heal.
        => Assert.Equal(PlayerRemoteControls.ExecuteCommands, Lookup("@kill"));

    [Fact]
    public void Heal_RoutesToExecuteCommands_NotPartyWhitelist()
        // @heal asks the receiver to cast a heal on the sender — that's
        // an action request ("do something on my behalf"), not a
        // coordination signal. A sender may legitimately need it even
        // when the receiver's auto-heal thresholds don't naturally
        // pick them up (settings mismatch). HealCommandHandler wires the
        // receive side: a configured healer polls `par` so CastingDirector
        // re-evaluates and heals the requester.
        => Assert.Equal(PlayerRemoteControls.ExecuteCommands, Lookup("@heal"));

    [Fact]
    public void Party_RoutesToQueryHealthStatusWithPartyMemberFallback()
        // @party is QueryHealthStatus tier so non-party callers with
        // that grant can use the no-args form as a status query. The
        // engine adds an @party-specific party-member fallback so the
        // Phase 6 "base @party always allowed inside an active party"
        // rule still holds for members who lack an explicit grant.
        => Assert.Equal(PlayerRemoteControls.QueryHealthStatus, Lookup("@party"));

    [Fact]
    public void TrailingBang_IsStripped_BeforeLookup()
        // An emphatic wire form (e.g. "@stop!") strips the trailing bang
        // so it resolves to the bare command's category.
        => Assert.True(RemoteCommandCatalog.TryGetCategory("@stop!", out PlayerRemoteControls c)
                       && c == PlayerRemoteControls.MovePlayer);

    // ===== Lookup helpers =====

    [Fact]
    public void Unknown_ReturnsFalse()
    {
        Assert.False(RemoteCommandCatalog.TryGetCategory("@nonsense", out _));
        Assert.False(RemoteCommandCatalog.TryGetCategory(string.Empty, out _));
    }

    [Fact]
    public void Lookup_IsCaseInsensitive()
    {
        Assert.True(RemoteCommandCatalog.TryGetCategory("@HEALTH", out PlayerRemoteControls c));
        Assert.Equal(PlayerRemoteControls.QueryHealthStatus, c);
    }

    [Fact]
    public void Catalog_HoldsTheWiredCommandSet_AtMinimum()
    {
        // Floor guard against silent drift. The catalog started from the
        // bearfather wiki baseline, then FujinTerm pruned the ailment /
        // status broadcast tokens (@blind / @diseased / @held — now owned
        // by AilmentSyncEngine, not the permission grid), the abandoned
        // @seen / @panic, and @home (mudop-only, won't-wire), and added
        // FujinTerm-specific verbs (@help, @atkprio, @atkorder, @lair).
        // 56 is the current floor.
        Assert.True(RemoteCommandCatalog.Count >= 56,
            $"Catalog should hold at least 56 entries; has {RemoteCommandCatalog.Count}.");
    }

    private static PlayerRemoteControls Lookup(string cmd)
    {
        Assert.True(RemoteCommandCatalog.TryGetCategory(cmd, out PlayerRemoteControls c),
            $"'{cmd}' missing from RemoteCommandCatalog.Map.");
        return c;
    }

    // ===== PlayerEditDialog tooltips =====
    //
    // The Players-tab edit dialog renders one tooltip per Allowed Remote
    // Control checkbox listing the @-commands each grants. The strings
    // are precomputed from RemoteCommandCatalog so adding a wiki
    // command auto-populates the tooltip — pin a sample category per
    // checkbox so a future "let's simplify" edit can't quietly empty
    // them out.

    [Theory]
    [InlineData(PlayerRemoteControls.QueryHealthStatus, "@health")]
    [InlineData(PlayerRemoteControls.QueryHealthStatus, "@status")]
    [InlineData(PlayerRemoteControls.QueryHealthStatus, "@lives")]
    [InlineData(PlayerRemoteControls.QueryHealthStatus, "@party")]
    [InlineData(PlayerRemoteControls.QueryLocation,     "@where")]
    [InlineData(PlayerRemoteControls.QueryLocation,     "@who")]
    [InlineData(PlayerRemoteControls.MovePlayer,        "@goto")]
    [InlineData(PlayerRemoteControls.MovePlayer,        "@stop")]
    [InlineData(PlayerRemoteControls.ExecuteCommands,   "@do")]
    [InlineData(PlayerRemoteControls.ExecuteCommands,   "@get-all")]
    [InlineData(PlayerRemoteControls.AlterSettings,     "@auto-combat")]
    [InlineData(PlayerRemoteControls.AlterSettings,     "@reset")]
    [InlineData(PlayerRemoteControls.HangupDisconnect,  "@hangup")]
    [InlineData(PlayerRemoteControls.HangupDisconnect,  "@relog")]
    [InlineData(PlayerRemoteControls.SysopCommands,     "@suicide")]
    public void Tooltip_ForCategory_ListsExpectedCommand(PlayerRemoteControls category, string command)
    {
        ViewModels.GameData.Edit.PlayerEditDialogViewModel vm = new(
            new PlayerRecord("Test", string.Empty, null, null, null, null, null, null,
                             DateTime.UtcNow, DateTime.UtcNow));
        string tip = category switch
        {
            PlayerRemoteControls.QueryVersion        => vm.RcQueryVersionTip,
            PlayerRemoteControls.QueryExperience     => vm.RcQueryExperienceTip,
            PlayerRemoteControls.QueryHealthStatus   => vm.RcQueryHealthStatusTip,
            PlayerRemoteControls.QueryLocation       => vm.RcQueryLocationTip,
            PlayerRemoteControls.QueryInventory      => vm.RcQueryInventoryTip,
            PlayerRemoteControls.RequestInvite       => vm.RcRequestInviteTip,
            PlayerRemoteControls.MovePlayer          => vm.RcMovePlayerTip,
            PlayerRemoteControls.ExecuteCommands     => vm.RcExecuteCommandsTip,
            PlayerRemoteControls.HangupDisconnect    => vm.RcHangupDisconnectTip,
            PlayerRemoteControls.AlterSettings       => vm.RcAlterSettingsTip,
            PlayerRemoteControls.DivertConversations => vm.RcDivertConversationsTip,
            PlayerRemoteControls.SysopCommands       => vm.RcSysopCommandsTip,
            _ => throw new InvalidOperationException($"Untested category {category}"),
        };
        Assert.Contains(command, tip);
        Assert.Contains("Ticked grants", tip);
        Assert.Contains("Unticked denies", tip);
    }
}
