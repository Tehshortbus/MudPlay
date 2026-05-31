using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

    /// <summary>Engine-backed table — see <see cref="GameDataTableSectionViewModel.ShowUseColumn"/>.</summary>
    public override bool ShowUseColumn => false;

    /// <summary>
    /// Surfaced banner — tells the user that aliases only expand from
    /// the Conversation window's input field today, not from the
    /// terminal canvas. The canvas would need client-side line-mode
    /// (local echo + telnet ECHO negotiation) to participate; that
    /// work is intentionally deferred until usage demand justifies it.
    /// </summary>
    public override string? BannerText =>
        "Aliases fire only when you press Enter in the Conversation window's input field. " +
        "Typing in the main terminal sends each keystroke directly to the game and bypasses alias expansion.";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "alias", "shortcut", "command",
    };

    private readonly NotifyCollectionChangedEventHandler _handler;

    public AliasesSectionViewModel(AliasEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _handler = (_, _) => Reload();
        _engine.Aliases.CollectionChanged += _handler;
        Reload();
    }

    public override void Dispose()
    {
        _engine.Aliases.CollectionChanged -= _handler;
        base.Dispose();
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
