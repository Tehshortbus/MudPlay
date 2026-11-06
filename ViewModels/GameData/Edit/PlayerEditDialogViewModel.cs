using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// Per-record edit dialog for the Game Data Browser → Players tab. Surfaces the
// engine-observed fields (Given / Family name + Last Seen timestamp displayed read-only)
// plus the user-editable behavior toggles and the 16 Allowed Remote Control
// checkboxes (the MegaMUD-documented categories plus MudPlay's own app-specific
// extensions — boss timers, deaths, item location, quests). Save produces a fresh
// PlayerRecord the caller splits back into PlayerDatabase's customization,
// account-name and relationship writes.
public sealed partial class PlayerEditDialogViewModel : ObservableObject, IDialogViewModel<PlayerEditResult>
{
    public event Action<PlayerEditResult?>? CloseRequested;

    private readonly PlayerRecord _original;

    [ObservableProperty] private bool _inviteToPartyIfSeen;
    [ObservableProperty] private bool _joinPartyIfInvited;
    [ObservableProperty] private bool _dontAutoDelete;

    // BBS account name, when it differs from the in-game name. Left blank on
    // the common boards where account == character; filled in only for boards
    // (like Playpen) whose logon/logoff presence lines key on the account name,
    // so the disconnect-watcher can map the captured name back to this player.
    [ObservableProperty] private string? _accountName;

    // How we stand with this player, and the response to them in place of the PvP
    // settings' general one. Both are stored on the realm's player record, so every
    // character we play on the realm shares them. Index 0 of the response list is
    // "use the PvP settings".
    public IReadOnlyList<string> RelationshipChoices { get; } =
        PvpLabels.Relationships.Select(PvpLabels.Of).ToList();
    public IReadOnlyList<string> PvpResponseChoices { get; } =
        new[] { PvpLabels.UseGeneral }.Concat(PvpLabels.Actions.Select(PvpLabels.Of)).ToList();
    [ObservableProperty] private int _relationshipIndex;
    [ObservableProperty] private int _pvpResponseIndex;

    // ----- 16 remote-control checkboxes (mirror PlayerRemoteControls flags) -----

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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcQueryBossTimers;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcQueryDeaths;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcQueryItemLocation;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcQueryQuests;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllowsAll))] private bool _rcQuerySpells;

    // ----- @dupe lock (shown under the Elevated Commands checkbox) -----
    // The once-only lock on this player's @dupe. Like every other edit here, a reset
    // is deferred to Save (Cancel discards it); the result's ResetDupe flag tells the
    // caller to clear the LIVE lock, which the editor never writes back itself.

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DupeSpent), nameof(DupeStatusText))] private DateTime? _dupeUsedAtUtc;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DupeStatusText))] private string? _dupedPlayer;

    private bool _resetDupeRequested;

    public bool DupeSpent => DupeUsedAtUtc is not null;

    public string DupeStatusText => DupeUsedAtUtc is { } used
        ? $"@dupe used {used.ToLocalTime():yyyy-MM-dd HH:mm}"
          + (string.IsNullOrEmpty(DupedPlayer) ? "" : $" on {DupedPlayer}")
          + " — locked until reset."
        : "@dupe available (one use).";

    // Re-arm this player's one-time @dupe. Only reachable here, in the client — nothing
    // sent over chat can reset it.
    [RelayCommand]
    private void ResetDupe()
    {
        DupeUsedAtUtc = null;
        DupedPlayer = null;
        _resetDupeRequested = true;
    }

    // True when every remote-control checkbox is checked — drives the master toggle's IsChecked.
    public bool AllowsAll =>
        RcQueryVersion && RcQueryExperience && RcQueryHealthStatus && RcQueryLocation &&
        RcQueryInventory && RcRequestInvite && RcMovePlayer && RcExecuteCommands &&
        RcHangupDisconnect && RcAlterSettings && RcDivertConversations && RcSysopCommands &&
        RcQueryBossTimers && RcQueryDeaths && RcQueryItemLocation && RcQueryQuests && RcQuerySpells;

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
    public string RcQueryBossTimersTip     { get; } = BuildTip(PlayerRemoteControls.QueryBossTimers);
    public string RcQueryDeathsTip         { get; } = BuildTip(PlayerRemoteControls.QueryDeaths);
    public string RcQueryItemLocationTip   { get; } = BuildTip(PlayerRemoteControls.QueryItemLocation);
    public string RcQueryQuestsTip         { get; } = BuildTip(PlayerRemoteControls.QueryQuests);
    public string RcQuerySpellsTip         { get; } = BuildTip(PlayerRemoteControls.QuerySpells);

    // Build the per-category tooltip text. Lists every @-command the catalog maps to
    // category, sorted, with a clear "ticked → grants / unticked → denies" framing so the
    // user knows which side of the box does what. Empty-category fallback is a (no commands)
    // placeholder so a future enum value that isn't yet in the catalog renders something
    // instead of throwing.
    private static string BuildTip(PlayerRemoteControls category)
    {
        IEnumerable<string> catalog = RemoteCommandCatalog.Map
            .Where(kv => kv.Value == category)
            .Select(kv => kv.Key);
        // Relay-back isn't a catalog command, but this grant is what lets it through.
        if (category == RemoteCommandManager.RelayBackCategory)
            catalog = catalog.Append(RemoteCommandManager.RelayBackPrefix + "<command>");
        string[] cmds = catalog.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        if (cmds.Length == 0) return "(no @-commands in this category yet)";
        // @relation rides on Alter settings (user, 2026-10-25), so the box warns what
        // it hands over.
        string warning = cmds.Contains(MonsterRelationshipHandler.Command)
            ? $"\nCareful: {MonsterRelationshipHandler.Command} can set a monster to Hangup or Flee."
            : "";
        return $"Ticked grants: {string.Join("  ", cmds)}\n"
             + $"Unticked denies the same.{warning}";
    }

    // Window title — shows the player's current display name.
    public string Title => $"Player — {(_original.DisplayName.Length > 0 ? _original.DisplayName : "(new)")}";

    // Read-only display strings for the observation footer.
    public string FirstSeenText => _original.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string LastSeenText  => _original.LastSeenUtc .ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    // Client version from the @version party probe, and when we last partied — both
    // null until the party stats probe records them.
    public string? VersionText     => _original.Version;
    public string? LastPartiedText => _original.LastPartiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
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
    // when the player has never answered one — OR when the title band has clearly moved above
    // a now-stale exact reading (the player trained since we last asked), per the shared
    // PartyLevelEstimate reconciliation rule. null when neither is known.
    public string? LevelText
    {
        get
        {
            (int Min, int Max)? range = ClassTitleTable.LookupLevelRange(_original.Title);
            var estimate = new MudPlay.Game.Map.PartyLevelEstimate(_original.Level, range);
            if (estimate.Low is not { } lo || estimate.High is not { } hi) return null;
            return ClassTitleTable.FormatLevelRange((lo, hi));
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
        AccountName         = original.AccountName;
        RelationshipIndex   = Math.Max(0, PvpLabels.Relationships.ToList().IndexOf(original.Relationship));
        PvpResponseIndex    = original.PvpResponse is { } own ? PvpLabels.Actions.ToList().IndexOf(own) + 1 : 0;
        DupeUsedAtUtc       = original.DupeUsedAtUtc;
        DupedPlayer         = original.DupedPlayer;

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
        RcQueryBossTimers     = rc.HasFlag(PlayerRemoteControls.QueryBossTimers);
        RcQueryDeaths         = rc.HasFlag(PlayerRemoteControls.QueryDeaths);
        RcQueryItemLocation   = rc.HasFlag(PlayerRemoteControls.QueryItemLocation);
        RcQueryQuests         = rc.HasFlag(PlayerRemoteControls.QueryQuests);
        RcQuerySpells         = rc.HasFlag(PlayerRemoteControls.QuerySpells);
    }

    // Toggle every remote-control checkbox in one shot (the "All" button).
    [RelayCommand]
    private void ToggleAll()
    {
        bool target = !AllowsAll;
        RcQueryVersion = RcQueryExperience = RcQueryHealthStatus = RcQueryLocation =
        RcQueryInventory = RcRequestInvite = RcMovePlayer = RcExecuteCommands =
        RcHangupDisconnect = RcAlterSettings = RcDivertConversations = RcSysopCommands =
        RcQueryBossTimers = RcQueryDeaths = RcQueryItemLocation = RcQueryQuests = RcQuerySpells = target;
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
        if (RcQueryBossTimers)     rc |= PlayerRemoteControls.QueryBossTimers;
        if (RcQueryDeaths)         rc |= PlayerRemoteControls.QueryDeaths;
        if (RcQueryItemLocation)   rc |= PlayerRemoteControls.QueryItemLocation;
        if (RcQueryQuests)         rc |= PlayerRemoteControls.QueryQuests;
        if (RcQuerySpells)         rc |= PlayerRemoteControls.QuerySpells;

        PlayerRecord updated = _original with
        {
            // Name fields stay as observed — the dialog doesn't expose
            // them for edit (the title bar shows the character name).
            RemoteControls      = rc,
            InviteToPartyIfSeen = InviteToPartyIfSeen,
            JoinPartyIfInvited  = JoinPartyIfInvited,
            DontAutoDelete      = DontAutoDelete,
            // AccountName is a BBS-tier observation field, not part of the
            // customization slice — the caller persists it separately via
            // PlayerDatabase.SetAccountName. Carried on the result record so
            // that write has the value.
            AccountName         = string.IsNullOrWhiteSpace(AccountName) ? null : AccountName.Trim(),
            // Realm-tier too: written through PlayerDatabase.SetRelationship.
            Relationship        = PvpLabels.Relationships[Math.Clamp(RelationshipIndex, 0, PvpLabels.Relationships.Count - 1)],
            PvpResponse         = PvpResponseIndex >= 1 && PvpResponseIndex <= PvpLabels.Actions.Count
                ? PvpLabels.Actions[PvpResponseIndex - 1] : null,
        };
        CloseRequested?.Invoke(new PlayerEditResult(updated, _resetDupeRequested));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

// Result returned from PlayerEditDialogViewModel on Save: the updated record. ResetDupe
// is true only when the user pressed Reset @dupe; the updated record's own @dupe lock is
// never trusted (see PlayerCustomization.KeepDupeLockFrom).
public sealed record PlayerEditResult(PlayerRecord Updated, bool ResetDupe);
