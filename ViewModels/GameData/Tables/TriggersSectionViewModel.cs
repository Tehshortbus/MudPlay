using System.Collections.Generic;
using System.Collections.ObjectModel;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Triggers tab. Surfaces the active character's
/// user-defined triggers from <see cref="TriggerEngine"/>. Engine-backed
/// (not from MDB JSON); reloads on every engine CollectionChanged so
/// the grid mirrors the live <see cref="TriggerEngine.Triggers"/>
/// collection.
/// </summary>
public sealed class TriggersSectionViewModel : GameDataTableSectionViewModel
{
    private readonly TriggerEngine _engine;

    public override string Id => "triggers";
    public override string Title => "Triggers";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Enabled", "Name", "MatchType", "Pattern", "Scope",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "trigger", "pattern", "match",
    };

    public TriggersSectionViewModel(TriggerEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _engine.Triggers.CollectionChanged += (_, _) => Reload();
        Reload();
    }

    protected override void PopulateRows(ObservableCollection<GameDataRow> rows)
    {
        foreach (Trigger t in _engine.Triggers)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"]   = t.Enabled ? "✓" : "",
                ["Name"]      = t.Name,
                ["MatchType"] = t.MatchType.ToString(),
                ["Pattern"]   = t.Pattern,
                ["Scope"]     = t.Scope.ToString(),
            };
            rows.Add(GameDataRow.FromDictionary(dict, Columns));
        }
    }
}
