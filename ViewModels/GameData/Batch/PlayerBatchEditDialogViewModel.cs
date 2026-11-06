using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Batch;

// Player-record batch changes: tri-state behaviour toggles + per-permission
// Grant/Revoke/Leave. ApplyTo folds them onto one player's customization, so
// batching "grant Query-Roomba" never disturbs a player's other permissions.
public sealed class PlayerBatchChanges
{
    public BatchToggle InviteToPartyIfSeen { get; init; } = BatchToggle.Leave;
    public BatchToggle JoinPartyIfInvited { get; init; } = BatchToggle.Leave;
    public BatchToggle DontAutoDelete { get; init; } = BatchToggle.Leave;

    // Each of the 16 remote-control categories, in the same order the single editor
    // and the flags enum use. On = grant the bit, Off = revoke it, Leave = keep.
    public IReadOnlyList<(PlayerRemoteControls Flag, BatchToggle Toggle)> Permissions { get; init; }
        = Array.Empty<(PlayerRemoteControls, BatchToggle)>();

    public bool AnyFieldChosen =>
        InviteToPartyIfSeen != BatchToggle.Leave
        || JoinPartyIfInvited != BatchToggle.Leave
        || DontAutoDelete != BatchToggle.Leave
        || Permissions.Any(p => p.Toggle != BatchToggle.Leave);

    public PlayerCustomization ApplyTo(PlayerCustomization existing)
    {
        PlayerRemoteControls rc = existing.RemoteControls;
        foreach ((PlayerRemoteControls flag, BatchToggle toggle) in Permissions)
        {
            if (toggle == BatchToggle.On) rc |= flag;
            else if (toggle == BatchToggle.Off) rc &= ~flag;
        }
        return existing with
        {
            RemoteControls = rc,
            InviteToPartyIfSeen = InviteToPartyIfSeen.Apply(existing.InviteToPartyIfSeen),
            JoinPartyIfInvited = JoinPartyIfInvited.Apply(existing.JoinPartyIfInvited),
            DontAutoDelete = DontAutoDelete.Apply(existing.DontAutoDelete),
        };
    }
}

// A single permission row in the batch dialog (label + its flag + its tri-state).
public sealed partial class PlayerBatchPermissionRow : ObservableObject
{
    public string Label { get; }
    public PlayerRemoteControls Flag { get; }
    public IReadOnlyList<BatchToggle> ToggleOptions { get; } = new[] { BatchToggle.Leave, BatchToggle.On, BatchToggle.Off };
    [ObservableProperty] private BatchToggle _toggle = BatchToggle.Leave;

    public PlayerBatchPermissionRow(string label, PlayerRemoteControls flag)
    {
        Label = label;
        Flag = flag;
    }
}

public sealed record PlayerBatchResult(PlayerBatchChanges Changes);

public sealed partial class PlayerBatchEditDialogViewModel
    : ObservableObject, IDialogViewModel<PlayerBatchResult>
{
    public event Action<PlayerBatchResult?>? CloseRequested;

    public int Count { get; }
    public string Heading { get; }
    public IReadOnlyList<BatchToggle> ToggleOptions { get; } = new[] { BatchToggle.Leave, BatchToggle.On, BatchToggle.Off };
    public IReadOnlyList<PlayerBatchPermissionRow> Permissions { get; }

    [ObservableProperty] private BatchToggle _inviteToPartyIfSeen = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _joinPartyIfInvited = BatchToggle.Leave;
    [ObservableProperty] private BatchToggle _dontAutoDelete = BatchToggle.Leave;

    // Master "set every permission" — pushing a value onto all rows at once.
    [ObservableProperty] private BatchToggle _allPermissions = BatchToggle.Leave;

    public PlayerBatchEditDialogViewModel(int count)
    {
        Count = count;
        Heading = $"Batch edit {count} players";
        Permissions = new[]
        {
            new PlayerBatchPermissionRow("Query version", PlayerRemoteControls.QueryVersion),
            new PlayerBatchPermissionRow("Query experience", PlayerRemoteControls.QueryExperience),
            new PlayerBatchPermissionRow("Query health / status", PlayerRemoteControls.QueryHealthStatus),
            new PlayerBatchPermissionRow("Query location", PlayerRemoteControls.QueryLocation),
            new PlayerBatchPermissionRow("Query inventory", PlayerRemoteControls.QueryInventory),
            new PlayerBatchPermissionRow("Request invite", PlayerRemoteControls.RequestInvite),
            new PlayerBatchPermissionRow("Move player", PlayerRemoteControls.MovePlayer),
            new PlayerBatchPermissionRow("Execute commands", PlayerRemoteControls.ExecuteCommands),
            new PlayerBatchPermissionRow("Hangup / disconnect", PlayerRemoteControls.HangupDisconnect),
            new PlayerBatchPermissionRow("Alter settings", PlayerRemoteControls.AlterSettings),
            new PlayerBatchPermissionRow("Divert conversations", PlayerRemoteControls.DivertConversations),
            new PlayerBatchPermissionRow("Sysop commands", PlayerRemoteControls.SysopCommands),
            new PlayerBatchPermissionRow("Query boss timers", PlayerRemoteControls.QueryBossTimers),
            new PlayerBatchPermissionRow("Query deaths", PlayerRemoteControls.QueryDeaths),
            new PlayerBatchPermissionRow("Query item location (@roomba)", PlayerRemoteControls.QueryItemLocation),
            new PlayerBatchPermissionRow("Query quests (@quest)", PlayerRemoteControls.QueryQuests),
            new PlayerBatchPermissionRow("Query spells (@spells)", PlayerRemoteControls.QuerySpells),
        };
    }

    // Pushing the master toggle sets every permission row to the same tri-state
    // (Leave leaves them alone — it's the neutral resting value, not a "clear all").
    partial void OnAllPermissionsChanged(BatchToggle value)
    {
        if (value == BatchToggle.Leave) return;
        foreach (PlayerBatchPermissionRow row in Permissions) row.Toggle = value;
    }

    private PlayerBatchChanges BuildChanges() => new()
    {
        InviteToPartyIfSeen = InviteToPartyIfSeen,
        JoinPartyIfInvited = JoinPartyIfInvited,
        DontAutoDelete = DontAutoDelete,
        Permissions = Permissions.Select(r => (r.Flag, r.Toggle)).ToArray(),
    };

    [RelayCommand]
    private void Apply() => CloseRequested?.Invoke(new PlayerBatchResult(BuildChanges()));

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
