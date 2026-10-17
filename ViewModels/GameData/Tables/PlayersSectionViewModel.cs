using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Batch;
using MudPlay.ViewModels.GameData.Edit;
using MudPlay.ViewModels.Guides;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Players tab. Surfaces the rows held by PlayerDatabase. Engine-backed;
// reloads on every CollectionChanged from the database so the grid mirrors live observations.
// Double-click a row to open PlayerEditDialogViewModel for the behavior toggles + 16-category
// remote-control bitmask.
public sealed class PlayersSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly PlayerDatabase _db;
    private readonly DialogService? _dialogs;
    private readonly ProfileService? _profile;

    public override string Id => "players";
    public override string Title => "Players";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Given Name", "Family Name", "Gang", "Relationship", "@'s", TogglesColumn, "Last Seen",
    };

    // Engine-backed (BBS-tier observations + Char-tier customisations) — see
    // GameDataTableSectionViewModel.ShowUseColumn.
    public override bool ShowUseColumn => false;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "player", "name", "remote", "@", "permissions", "relationship", "friend", "enemy", "pvp",
    };

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    public IRelayCommand AddAsyncCommand { get; }
    public IAsyncRelayCommand RemoveSelectedCommand { get; }

    public IAsyncRelayCommand BatchEditAsyncCommand { get; }

    ICommand  IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;
    ICommand? IEditableTableSectionViewModel.AddCommand      => AddAsyncCommand;
    ICommand? IEditableTableSectionViewModel.RemoveCommand   => RemoveSelectedCommand;
    ICommand? IEditableTableSectionViewModel.BatchEditCommand => BatchEditAsyncCommand;
    string?   IEditableTableSectionViewModel.BatchEditLabel  => "Batch edit";

    // Stored as a field so Dispose can detach — the database singleton
    // otherwise pins every section VM ever created across browser opens.
    private readonly NotifyCollectionChangedEventHandler _handler;
    private readonly Action<Models.Profile.CharacterProfile>? _profileSwapHandler;
    private readonly Action<Models.Profile.CharacterProfile>? _profileMutatedHandler;
    private readonly Action? _profileClosedHandler;

    // Live self-name source (given/full), preferred over the stored profile name
    // for the self-row filter. Null falls back
    // to the profile name.
    private readonly Func<string?>? _liveSelfName;

    public PlayersSectionViewModel(
        PlayerDatabase db,
        DialogService? dialogs = null,
        ProfileService? profile = null,
        Func<string?>? liveSelfName = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _dialogs = dialogs;
        _profile = profile;
        _liveSelfName = liveSelfName;
        _handler = (_, _) => Reload();
        _db.Players.CollectionChanged += _handler;
        // Refresh the filter when the loaded character swaps so a
        // freshly-loaded Raijin doesn't keep MudPlay's row hidden (and
        // vice versa). Drafts (no loaded profile) reload to "show
        // everyone".
        if (_profile is not null)
        {
            _profileSwapHandler    = _ => Reload();
            _profileMutatedHandler = _ => Reload();
            _profileClosedHandler  = ()  => Reload();
            _profile.ProfileLoaded  += _profileSwapHandler;
            _profile.ProfileMutated += _profileMutatedHandler;
            _profile.ProfileClosed  += _profileClosedHandler;
        }
        OpenEditAsyncCommand  = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
        AddAsyncCommand       = new AsyncRelayCommand(AddAsync);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => SelectedRow is not null);
        BatchEditAsyncCommand = new AsyncRelayCommand(BatchEditAsync);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedRow))
                RemoveSelectedCommand.NotifyCanExecuteChanged();
        };

        Reload();
    }

    public override void Dispose()
    {
        _db.Players.CollectionChanged -= _handler;
        if (_profile is not null)
        {
            if (_profileSwapHandler    is not null) _profile.ProfileLoaded  -= _profileSwapHandler;
            if (_profileMutatedHandler is not null) _profile.ProfileMutated -= _profileMutatedHandler;
            if (_profileClosedHandler  is not null) _profile.ProfileClosed  -= _profileClosedHandler;
        }
        base.Dispose();
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        // Hide the loaded character's own row — granting permissions to
        // yourself doesn't make sense. Match on given name (the first
        // whitespace-delimited token) so the filter still catches the
        // row whether the BBS rendered it with or without the family
        // suffix this session. Null / blank when no profile is loaded
        // (draft) means "show everyone".
        //
        // Use the LIVE character name (from `stat`) first, the stored profile name
        // only as a fallback: a profile copied from another character keeps that
        // character's stored Name, and trusting it would hide the REAL other player
        // (report stock-20260828-104653 — a profile copied from "Fujin" hid Fujin
        // from the player records, so their permissions couldn't be set).
        string? selfGiven = ExtractGiven(_liveSelfName?.Invoke() ?? _profile?.Current?.Name);
        foreach (PlayerRecord p in _db.Players)
        {
            if (selfGiven is not null
                && p.GivenName.Equals(selfGiven, StringComparison.OrdinalIgnoreCase))
                continue;
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Given Name"]  = p.GivenName,
                ["Family Name"] = p.FamilyName,
                // Last-seen gang from the `who` parse (preserved across sightings that
                // don't re-report it); blank when never seen in a gang.
                ["Gang"]        = p.Gang ?? string.Empty,
                // Blank for the Neutral everyone starts as, so a Friend or an Enemy
                // stands out; an Enemy with its own response shows it.
                ["Relationship"] = p.Relationship == PlayerRelationship.Neutral ? string.Empty
                    : p.PvpResponse is { } own ? $"{PvpLabels.Of(p.Relationship)} ({PvpLabels.Of(own)})"
                    : PvpLabels.Of(p.Relationship),
                ["@'s"]         = RemoteControlsLabel(p.RemoteControls),
                // The settings we've configured for this player: the party-behaviour
                // toggles, then each granted remote-control permission (the @'s column
                // keeps the quick None/Some/All glance).
                [TogglesColumn] = FormatToggleSummary(PlayerToggles(p).ToArray()),
                ["Last Seen"]   = p.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            };
            rows.Add(GameDataRow.FromDictionary(dict, Columns));
        }
    }

    // First whitespace-delimited token of a display name, lower-cased for the comparer. null
    // when the input is null or whitespace-only — caller uses that sentinel to mean "no
    // self-filter".
    private static string? ExtractGiven(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        int space = displayName.IndexOf(' ');
        return space >= 0 ? displayName[..space] : displayName;
    }

    // "None" / "Some" / "All" summary of the remote-control bitmask for the table cell.
    private static string RemoteControlsLabel(PlayerRemoteControls rc)
    {
        if (rc == PlayerRemoteControls.None) return "None";
        if (rc == PlayerRemoteControls.All)  return "All";
        return "Some";
    }

    // Everything we've configured for this player, in checkbox order, for the Toggles
    // column: the three party-behaviour flags, then each granted remote-control
    // permission (collapsed to one entry when they're all granted).
    private static IEnumerable<(bool On, string Label)> PlayerToggles(PlayerRecord p)
    {
        yield return (p.InviteToPartyIfSeen, "Invite-if-seen");
        yield return (p.JoinPartyIfInvited,  "Join-if-invited");
        yield return (p.DontAutoDelete,      "Don't-delete");
        foreach ((bool On, string Label) perm in PermissionToggles(p.RemoteControls))
            yield return perm;
    }

    // The granted remote-control categories as toggle entries, using the same labels the
    // edit dialog's permission checkboxes carry. None yields nothing; a full grant collapses
    // to a single "All @-permissions" entry so the cell doesn't list all sixteen.
    private static IEnumerable<(bool On, string Label)> PermissionToggles(PlayerRemoteControls rc)
    {
        if (rc == PlayerRemoteControls.None) yield break;
        if (rc == PlayerRemoteControls.All) { yield return (true, "All @-permissions"); yield break; }
        yield return (rc.HasFlag(PlayerRemoteControls.QueryVersion),        "Query version");
        yield return (rc.HasFlag(PlayerRemoteControls.QueryExperience),     "Query experience");
        yield return (rc.HasFlag(PlayerRemoteControls.QueryHealthStatus),   "Query health/status");
        yield return (rc.HasFlag(PlayerRemoteControls.QueryLocation),       "Query location");
        yield return (rc.HasFlag(PlayerRemoteControls.QueryInventory),      "Query inventory");
        yield return (rc.HasFlag(PlayerRemoteControls.QueryBossTimers),     "Query boss timers");
        yield return (rc.HasFlag(PlayerRemoteControls.QueryDeaths),         "Query deaths");
        yield return (rc.HasFlag(PlayerRemoteControls.QueryItemLocation),   "Query Roomba");
        yield return (rc.HasFlag(PlayerRemoteControls.QueryQuests),         "Query quests");
        yield return (rc.HasFlag(PlayerRemoteControls.RequestInvite),       "Request invite");
        yield return (rc.HasFlag(PlayerRemoteControls.MovePlayer),          "Move player");
        yield return (rc.HasFlag(PlayerRemoteControls.ExecuteCommands),     "Execute commands");
        yield return (rc.HasFlag(PlayerRemoteControls.HangupDisconnect),    "Hangup/disconnect");
        yield return (rc.HasFlag(PlayerRemoteControls.AlterSettings),       "Alter settings");
        yield return (rc.HasFlag(PlayerRemoteControls.DivertConversations), "Divert conversations");
        yield return (rc.HasFlag(PlayerRemoteControls.SysopCommands),       "Elevated Commands");
    }

    private async Task AddAsync()
    {
        if (_dialogs is null) return;
        PlayerAddDialogViewModel vm = new(_db);
        PlayerAddResult? created = await _dialogs.OpenWindowAsync<PlayerAddDialogViewModel, PlayerAddResult>(vm);
        if (created is null) return;
        // Mint a fresh observation at "now" — the user can refine flags
        // afterward via the existing edit dialog (double-click the row).
        _db.AddManual(created.GivenName, created.FamilyName, DateTime.UtcNow);
    }

    private async Task RemoveSelectedAsync()
    {
        // Customizations stay attached to the profile so a future
        // re-observation auto-rebinds the user's flags. Removing an
        // observation is therefore safely reversible by next `who`.
        IReadOnlyList<GameDataRow> selection = SelectedRows.Count > 0
            ? SelectedRows.ToList()
            : (SelectedRow is null ? Array.Empty<GameDataRow>() : new[] { SelectedRow });
        if (selection.Count == 0) return;
        string what = selection.Count == 1 ? "this player record" : $"{selection.Count} player records";
        if (!await AppServices.Current.Confirm.ConfirmDeleteAsync(what)) return;

        List<string> givens = new();
        foreach (GameDataRow row in selection)
        {
            string given = row.Get("Given Name") ?? string.Empty;
            if (!string.IsNullOrEmpty(given)) givens.Add(given);
        }
        foreach (string g in givens) _db.RemoveByGivenName(g);
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        string given  = row.Get("Given Name") ?? string.Empty;
        string family = row.Get("Family Name") ?? string.Empty;
        string displayName = string.IsNullOrEmpty(family) ? given : $"{given} {family}";
        if (string.IsNullOrEmpty(displayName)) return;

        // Locate the live record by display name. Case-insensitive match
        // — mirrors the database's lookup contract.
        PlayerRecord? record = null;
        foreach (PlayerRecord p in _db.Players)
        {
            if (string.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
            {
                record = p;
                break;
            }
        }
        if (record is null) return;

        PlayerEditDialogViewModel vm = new(record);
        PlayerEditResult? result = await _dialogs.OpenWindowAsync<PlayerEditDialogViewModel, PlayerEditResult>(vm);
        if (result is null) return;

        // Save only the customization slice — observed fields stay
        // observation-only and never get stomped by the dialog. The
        // customization layer is keyed on given name so the user's
        // toggles follow the player across train-stats renames.
        // The @dupe lock is engine-owned: take it from the LIVE record (a @dupe may have
        // landed while the dialog was open) and clear it only if the user pressed Reset.
        PlayerCustomization live = (_db.Find(record.GivenName) ?? record).ToCustomization();
        _db.EditCustomization(record.GivenName,
            result.Updated.ToCustomization().KeepDupeLockFrom(live, result.ResetDupe));
        // AccountName lives on the BBS-tier observation, not the customization
        // slice, so it takes its own write path.
        _db.SetAccountName(record.GivenName, result.Updated.AccountName);
        _db.SetRelationship(record.GivenName, result.Updated.Relationship, result.Updated.PvpResponse);
        GuideActions.Report(GuideActions.PlayerPermissionsSaved);
        Reload();
    }

    // Batch edit every selected player: fold the chosen behaviour toggles and
    // permission grants/revokes onto each player's existing customization. Permissions
    // and behaviours not opted into stay exactly as each player already has them.
    private async Task BatchEditAsync()
    {
        if (_dialogs is null) return;
        List<PlayerRecord> records = new();
        foreach (GameDataRow row in SelectedRows)
        {
            string given = row.Get("Given Name") ?? string.Empty;
            string family = row.Get("Family Name") ?? string.Empty;
            string displayName = string.IsNullOrEmpty(family) ? given : $"{given} {family}";
            if (string.IsNullOrEmpty(displayName)) continue;
            foreach (PlayerRecord p in _db.Players)
            {
                if (string.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                {
                    records.Add(p);
                    break;
                }
            }
        }
        if (records.Count < 2) return;

        PlayerBatchEditDialogViewModel vm = new(records.Count);
        PlayerBatchResult? result = await _dialogs.OpenWindowAsync<PlayerBatchEditDialogViewModel, PlayerBatchResult>(vm);
        if (result is null || !result.Changes.AnyFieldChosen) return;

        foreach (PlayerRecord record in records)
        {
            // Fold onto the live customization, not the snapshot from before the dialog
            // opened, so a @dupe that landed meanwhile keeps its lock.
            PlayerRecord live = _db.Find(record.GivenName) ?? record;
            _db.EditCustomization(record.GivenName, result.Changes.ApplyTo(live.ToCustomization()));
        }
        Reload();
    }
}
