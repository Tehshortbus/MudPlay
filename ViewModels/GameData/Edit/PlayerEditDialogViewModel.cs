using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Edit;

// Per-record edit dialog for the Game Data Browser → Players tab. Surfaces the
// engine-observed fields (Given / Family name + Last Seen timestamp displayed read-only)
// plus the user-editable behavior toggles and the 12 MegaMUD-grouped Allowed Remote
// Control checkboxes. Save produces a fresh PlayerRecord the caller writes back through
// PlayerDatabase.EditRecord.
public sealed partial class PlayerEditDialogViewModel : ObservableObject, IDialogViewModel<PlayerEditResult>
{
    public event Action<PlayerEditResult?>? CloseRequested;

    private readonly PlayerRecord _original;

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

    // True when every remote-control checkbox is checked — drives the master toggle's IsChecked.
    public bool AllowsAll =>
        RcQueryVersion && RcQueryExperience && RcQueryHealthStatus && RcQueryLocation &&
        RcQueryInventory && RcRequestInvite && RcMovePlayer && RcExecuteCommands &&
        RcHangupDisconnect && RcAlterSettings && RcDivertConversations && RcSysopCommands;

    // ----- Tooltips per checkbox ----------------------------------------
    // Precomputed once from RemoteCommandCatalog so the checkbox tooltip
    // names every @-command the player gains (or loses) when the
    // category toggles. Single source of truth — adding an entry to the
    // catalog automatically populates the right tooltip without touching
    // this VM.

    public string RcQueryVersionTip        { get; } = BuildTip(PlayerRemoteControls.QueryVersion);
    public string RcQueryExperienceTip     { get; } = BuildTip(PlayerRemoteControls.QueryExperience);
    public string RcQueryHealthStatusTip   { get; } = BuildTip(PlayerRemoteControls.QueryHealthStatus);
    public string RcQueryLocationTip       { get; } = BuildTip(PlayerRemoteControls.QueryLocation);
    public string RcQueryInventoryTip      { get; } = BuildTip(PlayerRemoteControls.QueryInventory);
    public string RcRequestInviteTip       { get; } = BuildTip(PlayerRemoteControls.RequestInvite);
    public string RcMovePlayerTip          { get; } = BuildTip(PlayerRemoteControls.MovePlayer);
    public string RcExecuteCommandsTip     { get; } = BuildTip(PlayerRemoteControls.ExecuteCommands);
    public string RcHangupDisconnectTip    { get; } = BuildTip(PlayerRemoteControls.HangupDisconnect);
    public string RcAlterSettingsTip       { get; } = BuildTip(PlayerRemoteControls.AlterSettings);
    public string RcDivertConversationsTip { get; } = BuildTip(PlayerRemoteControls.DivertConversations);
    public string RcSysopCommandsTip       { get; } = BuildTip(PlayerRemoteControls.SysopCommands);

    // Build the per-category tooltip text. Lists every @-command the catalog maps to
    // category, sorted, with a clear "ticked → grants / unticked → denies" framing so the
    // user knows which side of the box does what. Empty-category fallback is a (no commands)
    // placeholder so a future enum value that isn't yet in the catalog renders something
    // instead of throwing.
    private static string BuildTip(PlayerRemoteControls category)
    {
        string[] cmds = RemoteCommandCatalog.Map
            .Where(kv => kv.Value == category)
            .Select(kv => kv.Key)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();
        if (cmds.Length == 0) return "(no @-commands in this category yet)";
        return $"Ticked grants: {string.Join("  ", cmds)}\n"
             + $"Unticked denies the same.";
    }

    // Window title — shows the player's current display name.
    public string Title => $"Player — {(_original.DisplayName.Length > 0 ? _original.DisplayName : "(new)")}";

    // Read-only display strings for the observation footer.
    public string FirstSeenText => _original.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string LastSeenText  => _original.LastSeenUtc .ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string? Race           => _original.Race;
    public string? Alignment      => _original.Alignment;
    // In-game class title (renamed to avoid colliding with the Window-bound Title).
    public string? ObservedTitle  => _original.Title;
    public string? Gang           => _original.Gang;

    // Display value for the Class row. Falls through in this order: (1) an
    // explicitly-recorded PlayerRecord.Class (from a future @health / @stat parser),
    // (2) class inferred from the in-game title via ClassTitleTable. Single match shows the
    // class with a "(by title)" hint; a universally-shared title (every class has it —
    // Apprentice at level 1) shows "Unknown" rather than spamming every class name; a
    // partial multi-match (rare) lists the candidates.
    public string? ClassText
    {
        get
        {
            if (!string.IsNullOrEmpty(_original.Class)) return _original.Class;
            IReadOnlyList<string> inferred = ClassTitleTable.LookupClasses(_original.Title);
            if (inferred.Count == 0) return null;
            if (inferred.Count == 1) return $"{inferred[0]} (by title)";
            if (inferred.Count >= ClassTitleTable.ClassCount) return "Unknown";
            return $"{string.Join(" / ", inferred)} (by title)";
        }
    }

    // Display value for the Level row. Prefers the exact level learned from an @level probe
    // reply (PlayerRecord.Level); falls back to the title-derived range from ClassTitleTable
    // when the player has never answered one. null when neither is known.
    public string? LevelText
    {
        get
        {
            if (_original.Level is { } exact) return exact.ToString();
            (int min, int max)? range = ClassTitleTable.LookupLevelRange(_original.Title);
            return range is null ? null : ClassTitleTable.FormatLevelRange(range.Value);
        }
    }

    // Equipment slot summary for the Observed pane — one per slot, formatted as
    // "item name (Slot)" to mirror exactly what the game prints in the look <player>
    // response. Empty list ("Nothing") reports as a single "(none)" placeholder so the user
    // can tell "explicitly naked" apart from "never looked at" (which shows "—" because
    // HasEquipment is false).
    public IReadOnlyList<string> EquipmentLines
    {
        get
        {
            IReadOnlyList<Models.GameData.EquipmentItem>? eq = _original.Equipment;
            if (eq is null) return Array.Empty<string>();
            if (eq.Count == 0) return new[] { "(none)" };
            return eq.Select(e => $"{e.ItemName} ({e.SlotLabel})").ToArray();
        }
    }

    // True when a look observation has populated equipment (empty list still counts).
    public bool HasEquipment => _original.Equipment is not null;

    public string? Role           => _original.Role switch
                                      {
                                          "M" => "Mudop",
                                          "S" => "Sysop",
                                          "V" => "Visitor",
                                          _   => null,   // Regular players hide the row entirely.
                                      };

    // True only when Role has a non-Regular value to show. Most players are Regular, so the
    // row would be noise otherwise — the Observed pane collapses the row to zero height via
    // IsVisible on both the label and value cells.
    public bool HasRole => Role is not null;

    public PlayerEditDialogViewModel(PlayerRecord original)
    {
        _original           = original;
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

    // Toggle every remote-control checkbox in one shot (the "All" button).
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
            // Name fields stay as observed — the dialog doesn't expose
            // them for edit (the title bar shows the character name).
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

// Result returned from PlayerEditDialogViewModel on Save. Carries the original display name
// (so PlayerDatabase.EditRecord can locate the right record even if the user renamed) plus
// the updated record.
public sealed record PlayerEditResult(string OriginalDisplayName, PlayerRecord Updated);
