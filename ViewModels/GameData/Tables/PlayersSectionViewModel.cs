using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Players tab. Surfaces the rows held by PlayerDatabase. Engine-backed;
// reloads on every CollectionChanged from the database so the grid mirrors live observations.
// Double-click a row to open PlayerEditDialogViewModel for the behavior toggles + 12-category
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
        "Given Name", "Family Name", "@'s", "Last Seen",
    };

    public override string SearchKeyColumn => "Given Name";

    // Engine-backed (BBS-tier observations + Char-tier customisations) — see
    // GameDataTableSectionViewModel.ShowUseColumn.
    public override bool ShowUseColumn => false;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "player", "name", "remote", "@", "permissions",
    };

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    public IRelayCommand AddAsyncCommand { get; }
    public IAsyncRelayCommand RemoveSelectedCommand { get; }

    ICommand  IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;
    ICommand? IEditableTableSectionViewModel.AddCommand      => AddAsyncCommand;
    ICommand? IEditableTableSectionViewModel.RemoveCommand   => RemoveSelectedCommand;

    // Stored as a field so Dispose can detach — the database singleton
    // otherwise pins every section VM ever created across browser opens.
    private readonly NotifyCollectionChangedEventHandler _handler;
    private readonly Action<Models.Profile.CharacterProfile>? _profileSwapHandler;
    private readonly Action<Models.Profile.CharacterProfile>? _profileMutatedHandler;
    private readonly Action? _profileClosedHandler;

    public PlayersSectionViewModel(
        PlayerDatabase db,
        DialogService? dialogs = null,
        ProfileService? profile = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _dialogs = dialogs;
        _profile = profile;
        _handler = (_, _) => Reload();
        _db.Players.CollectionChanged += _handler;
        // Refresh the filter when the loaded character swaps so a
        // freshly-loaded Raijin doesn't keep Fujin's row hidden (and
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
        string? selfGiven = ExtractGiven(_profile?.Current?.Name);
        foreach (PlayerRecord p in _db.Players)
        {
            if (selfGiven is not null
                && p.GivenName.Equals(selfGiven, StringComparison.OrdinalIgnoreCase))
                continue;
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Given Name"]  = p.GivenName,
                ["Family Name"] = p.FamilyName,
                ["@'s"]         = RemoteControlsLabel(p.RemoteControls),
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
        // toggles follow the player across train-stats renames; the
        // dialog's OriginalDisplayName carries the given as its first
        // whitespace-delimited token and EditCustomization extracts it.
        _db.EditCustomization(record.GivenName, result.Updated.ToCustomization());
        // AccountName lives on the BBS-tier observation, not the customization
        // slice, so it takes its own write path.
        _db.SetAccountName(record.GivenName, result.Updated.AccountName);
        Reload();
    }
}
