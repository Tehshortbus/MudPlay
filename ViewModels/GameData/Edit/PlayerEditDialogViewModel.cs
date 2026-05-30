using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

/// <summary>
/// Per-record edit dialog for the Game Data Browser → Players tab.
/// Surfaces the engine-observed fields (Given / Family name + Last Seen
/// timestamp displayed read-only) plus the user-editable behavior toggles
/// and the 12 MegaMUD-grouped Allowed Remote Control checkboxes. Save
/// produces a fresh <see cref="PlayerRecord"/> the caller writes back
/// through <see cref="PlayerDatabase.EditRecord"/>.
/// </summary>
public sealed partial class PlayerEditDialogViewModel : ObservableObject, IDialogViewModel<PlayerEditResult>
{
    public event Action<PlayerEditResult?>? CloseRequested;

    private readonly PlayerRecord _original;

    [ObservableProperty] private string _givenName = string.Empty;
    [ObservableProperty] private string _familyName = string.Empty;
    [ObservableProperty] private bool _inviteToPartyIfSeen;
    [ObservableProperty] private bool _joinPartyIfInvited;
    [ObservableProperty] private bool _dontAutoDelete;

    // ----- 12 remote-control checkboxes (mirror PlayerRemoteControls flags) -----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcQueryVersion;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcQueryExperience;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcQueryHealthStatus;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcQueryLocation;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcQueryInventory;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcRequestInvite;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcMovePlayer;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcExecuteCommands;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcHangupDisconnect;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcAlterSettings;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcDivertConversations;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcSysopCommands;

    /// <summary>True when every remote-control checkbox is checked — drives the master toggle's IsChecked.</summary>
    public bool AllowsAll =>
        RcQueryVersion && RcQueryExperience && RcQueryHealthStatus && RcQueryLocation &&
        RcQueryInventory && RcRequestInvite && RcMovePlayer && RcExecuteCommands &&
        RcHangupDisconnect && RcAlterSettings && RcDivertConversations && RcSysopCommands;

    /// <summary>Window title — shows the player's current display name.</summary>
    public string Title => $"Player — {(_original.DisplayName.Length > 0 ? _original.DisplayName : "(new)")}";

    /// <summary>Read-only display strings for the observation footer.</summary>
    public string FirstSeenText => _original.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string LastSeenText  => _original.LastSeenUtc .ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string? Class        => _original.Class;
    public string? Race         => _original.Race;
    public string? Alignment    => _original.Alignment;

    public PlayerEditDialogViewModel(PlayerRecord original)
    {
        _original           = original;
        GivenName           = original.GivenName;
        FamilyName          = original.FamilyName;
        InviteToPartyIfSeen = original.InviteToPartyIfSeen;
        JoinPartyIfInvited  = original.JoinPartyIfInvited;
        DontAutoDelete      = original.DontAutoDelete;

        PlayerRemoteControls rc = original.RemoteControls;
        RcQueryVersion        = rc.HasFlag(PlayerRemoteControls.QueryVersion);
        RcQueryExperience     = rc.HasFlag(PlayerRemoteControls.QueryExperience);
        RcQueryHealthStatus   = rc.HasFlag(PlayerRemoteControls.QueryHealthStatus);
        RcQueryLocation       = rc.HasFlag(PlayerRemoteControls.QueryLocation);
        RcQueryInventory      = rc.HasFlag(PlayerRemoteControls.QueryInventory);
        RcRequestInvite       = rc.HasFlag(PlayerRemoteControls.RequestInvite);
        RcMovePlayer          = rc.HasFlag(PlayerRemoteControls.MovePlayer);
        RcExecuteCommands     = rc.HasFlag(PlayerRemoteControls.ExecuteCommands);
        RcHangupDisconnect    = rc.HasFlag(PlayerRemoteControls.HangupDisconnect);
        RcAlterSettings       = rc.HasFlag(PlayerRemoteControls.AlterSettings);
        RcDivertConversations = rc.HasFlag(PlayerRemoteControls.DivertConversations);
        RcSysopCommands       = rc.HasFlag(PlayerRemoteControls.SysopCommands);
    }

    /// <summary>Toggle every remote-control checkbox in one shot (the "All" button).</summary>
    [RelayCommand]
    private void ToggleAll()
    {
        bool target = !AllowsAll;
        RcQueryVersion = RcQueryExperience = RcQueryHealthStatus = RcQueryLocation =
        RcQueryInventory = RcRequestInvite = RcMovePlayer = RcExecuteCommands =
        RcHangupDisconnect = RcAlterSettings = RcDivertConversations = RcSysopCommands = target;
    }

    [RelayCommand]
    private void Save()
    {
        PlayerRemoteControls rc = PlayerRemoteControls.None;
        if (RcQueryVersion)        rc |= PlayerRemoteControls.QueryVersion;
        if (RcQueryExperience)     rc |= PlayerRemoteControls.QueryExperience;
        if (RcQueryHealthStatus)   rc |= PlayerRemoteControls.QueryHealthStatus;
        if (RcQueryLocation)       rc |= PlayerRemoteControls.QueryLocation;
        if (RcQueryInventory)      rc |= PlayerRemoteControls.QueryInventory;
        if (RcRequestInvite)       rc |= PlayerRemoteControls.RequestInvite;
        if (RcMovePlayer)          rc |= PlayerRemoteControls.MovePlayer;
        if (RcExecuteCommands)     rc |= PlayerRemoteControls.ExecuteCommands;
        if (RcHangupDisconnect)    rc |= PlayerRemoteControls.HangupDisconnect;
        if (RcAlterSettings)       rc |= PlayerRemoteControls.AlterSettings;
        if (RcDivertConversations) rc |= PlayerRemoteControls.DivertConversations;
        if (RcSysopCommands)       rc |= PlayerRemoteControls.SysopCommands;

        PlayerRecord updated = _original with
        {
            GivenName           = (GivenName ?? string.Empty).Trim(),
            FamilyName          = (FamilyName ?? string.Empty).Trim(),
            RemoteControls      = rc,
            InviteToPartyIfSeen = InviteToPartyIfSeen,
            JoinPartyIfInvited  = JoinPartyIfInvited,
            DontAutoDelete      = DontAutoDelete,
        };
        CloseRequested?.Invoke(new PlayerEditResult(_original.DisplayName, updated));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

/// <summary>
/// Result returned from <see cref="PlayerEditDialogViewModel"/> on Save.
/// Carries the original display name (so <see cref="PlayerDatabase.EditRecord"/>
/// can locate the right record even if the user renamed) plus the
/// updated record.
/// </summary>
public sealed record PlayerEditResult(string OriginalDisplayName, PlayerRecord Updated);
