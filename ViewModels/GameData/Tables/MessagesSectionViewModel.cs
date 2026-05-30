using System.Collections.Generic;
using System.Collections.ObjectModel;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Messages tab. Surfaces the active game-data
/// set's Messages/Responses catalogue from <see cref="MessageStore"/>.
/// Records are paired per set: importing a MegaMUD <c>messages.md</c>
/// file lands as <c>Data/Global/Messages/{set-name}.json</c>, and
/// switching the active set swaps the catalogue in real time.
/// </summary>
public sealed class MessagesSectionViewModel : GameDataTableSectionViewModel
{
    private readonly MessageStore _store;

    public override string Id => "messages";
    public override string Title => "Messages";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Name", "Action", "Pattern",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "message", "response", "condition", "pattern",
        "blinded", "poisoned", "paralyzed", "confused", "diseased", "regenerating",
    };

    public MessagesSectionViewModel(MessageStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _store.Messages.CollectionChanged += (_, _) => Reload();
        Reload();
    }

    protected override void PopulateRows(ObservableCollection<GameDataRow> rows)
    {
        foreach (MessageRecord m in _store.Messages)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"]    = m.Name,
                ["Action"]  = m.Action.ToString(),
                ["Pattern"] = m.Pattern,
            };
            rows.Add(GameDataRow.FromDictionary(dict, Columns));
        }
    }
}
