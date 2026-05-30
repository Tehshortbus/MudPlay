using System.Collections.Generic;
using System.Collections.ObjectModel;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Aliases tab. Surfaces the active character's
/// user-defined aliases from <see cref="AliasEngine"/> — the
/// outgoing-text mirror of the Triggers tab.
/// </summary>
public sealed class AliasesSectionViewModel : GameDataTableSectionViewModel
{
    private readonly AliasEngine _engine;

    public override string Id => "aliases";
    public override string Title => "Aliases";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Enabled", "Name", "Expansion",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "alias", "shortcut", "command",
    };

    public AliasesSectionViewModel(AliasEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _engine.Aliases.CollectionChanged += (_, _) => Reload();
        Reload();
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        foreach (Alias a in _engine.Aliases)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"]   = a.Enabled ? "✓" : "",
                ["Name"]      = a.Name,
                ["Expansion"] = a.Expansion,
            };
            rows.Add(GameDataRow.FromDictionary(dict, Columns));
        }
    }
}
