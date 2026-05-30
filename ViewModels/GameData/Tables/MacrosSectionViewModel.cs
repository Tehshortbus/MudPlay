using System.Collections.Generic;
using System.Collections.ObjectModel;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Macros tab. Read-only listing of the loaded
/// character's keybinds from <see cref="MacroStore"/>. Per master
/// plan, double-click a row opens the Phase 10 MacroEditDialog —
/// wiring lands in Phase 10 PR 10.3 once that dialog exists.
/// </summary>
public sealed class MacrosSectionViewModel : GameDataTableSectionViewModel
{
    private readonly MacroStore _store;

    public override string Id => "macros";
    public override string Title => "Macros";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Enabled", "Key", "Modifier", "Name", "Command",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "macro", "key", "keybind",
    };

    public MacrosSectionViewModel(MacroStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _store.Macros.CollectionChanged += (_, _) => Reload();
        Reload();
    }

    protected override void PopulateRows(ObservableCollection<GameDataRow> rows)
    {
        foreach (Macro m in _store.Macros)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"]  = m.Enabled ? "✓" : "",
                ["Key"]      = m.Key,
                ["Modifier"] = m.Modifier,
                ["Name"]     = m.Name,
                ["Command"]  = m.Command,
            };
            rows.Add(GameDataRow.FromDictionary(dict, Columns));
        }
    }
}
