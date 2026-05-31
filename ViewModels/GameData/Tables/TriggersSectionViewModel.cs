using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
        "Enabled", "Name", "Scope", "Match", "Pattern", "Response",
    };

    public override string SearchKeyColumn => "Name";

    /// <summary>Engine-backed table — see <see cref="GameDataTableSectionViewModel.ShowUseColumn"/>.</summary>
    public override bool ShowUseColumn => false;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "trigger", "pattern", "match", "response",
    };

    private readonly NotifyCollectionChangedEventHandler _handler;

    public TriggersSectionViewModel(TriggerEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _handler = (_, _) => Reload();
        _engine.Triggers.CollectionChanged += _handler;
        Reload();
    }

    public override void Dispose()
    {
        _engine.Triggers.CollectionChanged -= _handler;
        base.Dispose();
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        foreach (Trigger t in _engine.Triggers)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"]  = t.Enabled ? "✓" : "",
                ["Name"]     = t.Name,
                ["Scope"]    = FormatScope(t.Scope),
                ["Match"]    = t.MatchType.ToString(),
                ["Pattern"]  = t.Pattern,
                ["Response"] = string.IsNullOrEmpty(t.Response) ? "(CR)" : t.Response,
            };
            rows.Add(GameDataRow.FromDictionary(dict, Columns));
        }
    }

    /// <summary>
    /// Friendly column label for the scope enum — the underlying values
    /// are PascalCase (<c>GameMessages</c>, <c>ChatTelepath</c>) which
    /// reads awkwardly in a table.
    /// </summary>
    private static string FormatScope(TriggerScope scope) => scope switch
    {
        TriggerScope.GameMessages  => "Game messages",
        TriggerScope.ChatAny       => "Chat (any)",
        TriggerScope.ChatSay       => "Say",
        TriggerScope.ChatYell      => "Yell",
        TriggerScope.ChatGossip    => "Gossip",
        TriggerScope.ChatTelepath  => "Telepath",
        TriggerScope.ChatGangpath  => "Gangpath",
        TriggerScope.ChatBroadcast => "Broadcast",
        TriggerScope.SystemLog     => "System log",
        _                          => scope.ToString(),
    };
}
