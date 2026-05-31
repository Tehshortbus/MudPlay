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

/// <summary>
/// Game Data Browser → Players tab. Surfaces the rows held by
/// <see cref="PlayerDatabase"/>. Engine-backed; reloads on every
/// <see cref="System.Collections.ObjectModel.ObservableCollection{T}.CollectionChanged"/>
/// from the database so the grid mirrors live observations. Double-click
/// a row to open <see cref="PlayerEditDialogViewModel"/> for the
/// behavior toggles + 12-category remote-control bitmask.
/// </summary>
public sealed class PlayersSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly PlayerDatabase _db;
    private readonly DialogService? _dialogs;

    public override string Id => "players";
    public override string Title => "Players";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Given Name", "Family Name", "@'s", "Last Seen",
    };

    public override string SearchKeyColumn => "Given Name";

    /// <summary>Engine-backed (BBS-tier observations + Char-tier customisations) — see <see cref="GameDataTableSectionViewModel.ShowUseColumn"/>.</summary>
    public override bool ShowUseColumn => false;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "player", "name", "remote", "@", "permissions",
    };

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;

    // Stored as a field so Dispose can detach — the database singleton
    // otherwise pins every section VM ever created across browser opens.
    private readonly NotifyCollectionChangedEventHandler _handler;

    public PlayersSectionViewModel(PlayerDatabase db, DialogService? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _dialogs = dialogs;
        _handler = (_, _) => Reload();
        _db.Players.CollectionChanged += _handler;
        OpenEditAsyncCommand = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
        Reload();
    }

    public override void Dispose()
    {
        _db.Players.CollectionChanged -= _handler;
        base.Dispose();
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        foreach (PlayerRecord p in _db.Players)
        {
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

    /// <summary>"None" / "Some" / "All" summary of the remote-control bitmask for the table cell.</summary>
    private static string RemoteControlsLabel(PlayerRemoteControls rc)
    {
        if (rc == PlayerRemoteControls.None) return "None";
        if (rc == PlayerRemoteControls.All)  return "All";
        return "Some";
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
        // observation-only and never get stomped by the dialog.
        _db.EditCustomization(result.OriginalDisplayName, result.Updated.ToCustomization());
        Reload();
    }
}
