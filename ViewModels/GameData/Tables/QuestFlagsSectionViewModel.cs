using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Quests;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Quest Flags tab. A computed view over QuestFlagIndex: every quest-flag
// reference in the active set's TBInfo, one row per script line — the flag, how the line
// relates to it (grants / gates / advances / clears), how the line is set off and what it asks
// for, and the NPC / room / spell that reaches it, resolved via the block's Called-From
// provenance. Not backed by a JSON table (no such table exists); it recomputes from the loaded
// set and rebuilds on a set swap, like the engine-backed tabs.
//
// Double-click a row → the Quest Flag Steps window for that row's flag. One window serves the
// table: double-clicking another row swaps it to that flag.
public sealed class QuestFlagsSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly QuestFlagIndex _index;
    private readonly GameDataCache _cache;
    private readonly DialogService? _dialogs;
    private readonly Action<string?> _activeSetHandler;
    private QuestFlagStepsViewModel? _openSteps;

    // The flag numbers the loaded rows carry, so a number typed into the filter box can be
    // told from any other digits. Swapped whole by PopulateRows, which runs off the UI thread.
    private HashSet<string> _flagNumbers = new(StringComparer.Ordinal);

    public QuestFlagsSectionViewModel(QuestFlagIndex index, GameDataCache cache, DialogService? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(cache);
        _index = index;
        _cache = cache;
        _dialogs = dialogs;
        _activeSetHandler = _ => { if (IsLoaded) Reload(); };
        _cache.ActiveSetChanged += _activeSetHandler;
        OpenStepsCommand = new AsyncRelayCommand<GameDataRow?>(
            OpenStepsAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
    }

    public override string Id => "questflags";
    public override string Title => "Quest Flags";

    // Derived from the MDB data, so it belongs with the tables; single-tier, so no "Use" badge.
    public override bool ShowInTableGroup => true;
    public override bool ShowUseColumn => false;

    // "Step" (the directive's second argument — the give value / required value / addability
    // delta) sits right after the name so a flag's step reads at a glance beside what it is.
    // Command / Level / Class / Race / Items describe the script line the directive sits on.
    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Flag", "Name", "Step", "Relationship",
        "Command", "Level", "Class", "Race", "Items",
        "Kind", "Source", "Location",
    };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "quest", "flag", "ability", "giveability", "checkability", "testability",
    };

    public override string? BannerText =>
        "Every quest-flag reference in this set's TBInfo, one row per script line — what grants / " +
        "gates / advances / clears each flag, how the line is set off and what it asks for, " +
        "resolved to its NPC, room, or spell. Double-click a row for that flag's steps in order.";

    public override string? FilterHint =>
        "A flag number (133) shows that flag's rows only. Anything else matches the text of any column: " +
        "a flag name, an NPC, a command, an item.";

    public IAsyncRelayCommand<GameDataRow?> OpenStepsCommand { get; }
    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenStepsCommand;

    // Lazy first-load, deferred a tick so the DataGrid columns exist before rows arrive
    // (mirrors JsonTableSectionViewModel.OnActivated).
    public override void OnActivated()
    {
        if (IsLoaded) return;
        Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        HashSet<string> flagNumbers = new(StringComparer.Ordinal);
        foreach (QuestFlagRef r in _index.Entries)
        {
            string flag = r.Flag.ToString(CultureInfo.InvariantCulture);
            flagNumbers.Add(flag);
            Dictionary<string, string?> cells = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Flag"]         = flag,
                ["Name"]         = r.FlagName,
                ["Step"]         = r.Value != 0 ? r.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                ["Relationship"] = RelationLabel(r.Relation),
                ["Command"]      = r.Command,
                ["Level"]        = r.Line.Level,
                ["Class"]        = r.Line.Classes,
                ["Race"]         = r.Line.Races,
                ["Items"]        = r.Line.Items,
                ["Kind"]         = r.SourceKind.ToString(),
                ["Source"]       = r.SourceName,
                ["Location"]     = r.Map > 0 ? $"{r.Map}/{r.Room}" : string.Empty,
            };
            rows.Add(GameDataRow.FromDictionary(cells, Columns));
        }
        _flagNumbers = flagNumbers;
    }

    // A bare number that is one of the table's flags lists that flag's rows only: as a
    // substring "13" would also pull in 113, 130–139, 213 and every room, level and item
    // count holding those digits. Any other text falls through to the usual match.
    protected override bool RowMatches(GameDataRow row, string filter)
    {
        if (_flagNumbers.Contains(filter))
            return base.RowMatches(row, string.Empty) && string.Equals(row.Get("Flag"), filter, StringComparison.Ordinal);
        return base.RowMatches(row, filter);
    }

    private static string RelationLabel(QuestFlagRelation rel) => rel switch
    {
        QuestFlagRelation.Grants   => "Grants",
        QuestFlagRelation.Advances => "Advances",
        QuestFlagRelation.Requires => "Requires",
        QuestFlagRelation.Tests    => "Tests",
        QuestFlagRelation.Gate     => "Gate (must not have)",
        QuestFlagRelation.Clears   => "Clears",
        _                          => rel.ToString(),
    };

    // No-op without a DialogService (design-time).
    private async Task OpenStepsAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        if (!int.TryParse(row.Get("Flag"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int flag))
            return;

        if (_openSteps is { } open)
        {
            open.Show(flag);
            _dialogs.RaiseIfOpen(open);
            return;
        }

        QuestFlagStepsViewModel vm = new(
            _index, _cache, flag,
            monsterRooms: () => AppServices.CurrentOrNull?.RoomSearch?.QuestKillRooms(),
            questName: (f, step) => AppServices.CurrentOrNull?.Quests.Resolve(f, step).Name,
            log: AppServices.CurrentOrNull?.Log);
        _openSteps = vm;
        try
        {
            await _dialogs.OpenWindowAsync<QuestFlagStepsViewModel, bool>(vm);
        }
        finally
        {
            _openSteps = null;
        }
    }

    public override void Dispose()
    {
        // The steps window belongs to this table: left open past the browser, the next
        // browser's table wouldn't know it and would open a second one.
        _openSteps?.CloseCommand.Execute(null);
        _cache.ActiveSetChanged -= _activeSetHandler;
        base.Dispose();
    }
}
