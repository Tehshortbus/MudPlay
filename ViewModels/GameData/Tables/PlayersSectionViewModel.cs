using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Players tab. Surfaces the rows held by
/// <see cref="PlayerDatabase"/>. Engine-backed; reloads on every
/// <see cref="System.Collections.ObjectModel.ObservableCollection{T}.CollectionChanged"/>
/// from the database so the grid mirrors live observations.
/// </summary>
public sealed class PlayersSectionViewModel : GameDataTableSectionViewModel
{
    private readonly PlayerDatabase _db;

    public override string Id => "players";
    public override string Title => "Players";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Name", "Class", "Race", "Alignment", "Title", "First Seen", "Last Seen",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "player", "name", "class", "race", "alignment",
    };

    public PlayersSectionViewModel(PlayerDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _db.Players.CollectionChanged += (_, _) => Reload();
        Reload();
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        foreach (PlayerRecord p in _db.Players)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"]       = p.Name,
                ["Class"]      = p.Class,
                ["Race"]       = p.Race,
                ["Alignment"]  = p.Alignment,
                ["Title"]      = p.Title,
                ["First Seen"] = p.FirstSeenUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["Last Seen"]  = p.LastSeenUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            rows.Add(GameDataRow.FromDictionary(dict, Columns));
        }
    }
}
