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
    [InlineData("@health",      PlayerRemoteControls.QueryHealthStatus)]
    [InlineData("@status",      PlayerRemoteControls.QueryHealthStatus)]
    [InlineData("@lives",       PlayerRemoteControls.QueryHealthStatus)]
    [InlineData("@par",         PlayerRemoteControls.QueryHealthStatus)]
    [InlineData("@exp",         PlayerRemoteControls.QueryExperience)]
    [InlineData("@level",       PlayerRemoteControls.QueryExperience)]
    [InlineData("@where",       PlayerRemoteControls.QueryLocation)]
    [InlineData("@path",        PlayerRemoteControls.QueryLocation)]
    [InlineData("@seen",        PlayerRemoteControls.QueryLocation)]
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
    [InlineData("@equip-all")]
    [InlineData("@deposit-all")]
    [InlineData("@do")]
    public void ExecuteCommands_BulkActionsAndDo(string cmd)
        => Assert.Equal(PlayerRemoteControls.ExecuteCommands, Lookup(cmd));

    // ===== Party invite signals =====

    [Theory]
    [InlineData("@invite")]
    [InlineData("@join")]
    [InlineData("@forget")]
    public void RequestInvite_ApplicableCommands(string cmd)
        => Assert.Equal(PlayerRemoteControls.RequestInvite, Lookup(cmd));

    // ===== Movement / loops =====

    [Theory]
    [InlineData("@goto")]
    [InlineData("@loop")]
    [InlineData("@looponce")]
    [InlineData("@roam")]
    [InlineData("@stop")]
    [InlineData("@rego")]
    public void MovePlayer_NavigationCommands(string cmd)
        => Assert.Equal(PlayerRemoteControls.MovePlayer, Lookup(cmd));

    // ===== Toggle Settings (12 auto-* + @settings + @reset + @attack-last) =====

    [Theory]
    [InlineData("@attack-last")]
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
    public void Home_RoutesToSysopCommands_PerBearfatherWiki()
        // Documented as mudop-only on the wiki — only sysop-tier players
        // grant this. Default-deny for everyone else.
        => Assert.Equal(PlayerRemoteControls.SysopCommands, Lookup("@home"));

    // ===== Party-whitelist (None) =====

    [Theory]
    [InlineData("@wait")]
    [InlineData("@ok")]
    [InlineData("@comeback")]
    [InlineData("@heal")]
    [InlineData("@blind")]
    [InlineData("@diseased")]
    [InlineData("@held")]
    [InlineData("@kill")]
    [InlineData("@share")]
    [InlineData("@panic")]
    public void PartyWhitelist_NoneCategory(string cmd)
        // None = "any active party member" — engine routes these through
        // the party-whitelist branch instead of the per-player flag.
        => Assert.Equal(PlayerRemoteControls.None, Lookup(cmd));

    [Fact]
    public void Party_RoutesToQueryHealthStatusWithPartyMemberFallback()
        // @party is QueryHealthStatus tier so non-party callers with
        // that grant can use it as a status-query alias for @par. The
        // engine adds an @party-specific party-member fallback so the
        // Phase 6 "base @party always allowed inside an active party"
        // rule still holds for members who lack an explicit grant.
        => Assert.Equal(PlayerRemoteControls.QueryHealthStatus, Lookup("@party"));

    [Fact]
    public void PanicWithBang_AlsoResolves()
        // Wiki form is `@panic!`; the catalog strips the trailing bang
        // so both spellings classify under None.
        => Assert.True(RemoteCommandCatalog.TryGetCategory("@panic!", out PlayerRemoteControls c)
                       && c == PlayerRemoteControls.None);

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
    public void Catalog_IncludesEveryWikiCommand_AtMinimum()
    {
        // Belt-and-braces — every command the existing test suite
        // references plus a few inline. If the wiki adds commands the
        // catalog must too; this floor guards against silent drift.
        // 57 is the bearfather wiki count as of the catalog seed.
        Assert.True(RemoteCommandCatalog.Count >= 57,
            $"Catalog should hold at least 57 entries (bearfather wiki baseline); has {RemoteCommandCatalog.Count}.");
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
    [InlineData(PlayerRemoteControls.QueryHealthStatus, "@par")]
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
    [InlineData(PlayerRemoteControls.SysopCommands,     "@home")]
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
