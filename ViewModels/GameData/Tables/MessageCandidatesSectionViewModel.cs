using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Unrecognized Lines tab. Surfaces MessageCandidateStore's staged,
// unrecognized wire lines for batch review — the same records the LogPane's
// "double-click to review" flow (App.axaml.cs) resolves one at a time as they
// arrive. Both surfaces commit through the shared MessageCandidateCommit
// helper so neither duplicates the seed/commit logic.
public sealed class MessageCandidatesSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly MessageCandidateStore _candidates;
    private readonly MessageStore _messages;
    private readonly DialogService? _dialogs;
    private readonly GameDataCache? _cache;
    private readonly MessageCandidateWatcher? _watcher;
    private readonly LogDiagnosticState? _diagnostics;
    // (map, room) -> a "Likely source" hint (spells castable by monsters in that
    // room). Null when no attributor was supplied (tests / no game data).
    private readonly Func<int, int, string?>? _likelySource;

    public override string Id => "message-candidates";
    public override string Title => "Unrecognized Lines";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Raw Text", "Seen In", "Likely source", "Occurrences", "First Seen", "Last Seen", "Status",
    };

    public override string SearchKeyColumn => "Raw Text";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "unrecognized", "candidate", "message",
    };

    // Open the same edit dialog the Messages tab uses, pre-seeded with the raw text.
    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    // Remove = hard-delete the selected row(s) from the catalogue (matches every
    // other table's Remove). A later recurrence re-captures the line as new.
    public IRelayCommand RemoveSelectedCommand { get; }
    // Dismiss = sticky "decided, stop tracking" — the row stays (frozen) and the
    // watcher ignores every future recurrence of that text.
    public IRelayCommand DismissSelectedCommand { get; }
    // Export every non-dismissed candidate to a Desktop file for review.
    public IRelayCommand ExportNonDismissedCommand { get; }
    // Test-only: feed a synthetic unrecognized line through the watcher so a
    // candidate appears here. Null (button absent) unless a watcher was supplied.
    public IRelayCommand? SimulateEntryCommand { get; }

    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;
    // No Add button — a candidate only ever arrives from live capture
    // (Game.MessageCandidateWatcher); there's nothing to hand-add here.
    ICommand? IEditableTableSectionViewModel.AddCommand     => null;
    ICommand? IEditableTableSectionViewModel.RemoveCommand  => RemoveSelectedCommand;
    ICommand? IEditableTableSectionViewModel.DismissCommand => DismissSelectedCommand;
    string?  IEditableTableSectionViewModel.DismissLabel    => "Dismiss";
    ICommand? IEditableTableSectionViewModel.ExportCommand  => ExportNonDismissedCommand;
    string?  IEditableTableSectionViewModel.ExportLabel     => "Export";

    // The far-right "Simulate entry" test button — present only when a watcher
    // is wired, and shown only while the Log pane's Simulate dropdown reveals it
    // (LogDiagnosticState.ShowSimulateUnrecognized; session-only, off by default).
    ICommand? IEditableTableSectionViewModel.SimulateCommand => SimulateEntryCommand;
    string?  IEditableTableSectionViewModel.SimulateLabel    => "Simulate entry";
    bool     IEditableTableSectionViewModel.ShowSimulate     => ShowSimulate;

    // Backing getter for the interface member; raised on the diagnostics toggle
    // so the shared view (which watches this property) shows/hides the button live.
    public bool ShowSimulate => _diagnostics?.ShowSimulateUnrecognized ?? false;

    private readonly NotifyCollectionChangedEventHandler _handler;

    public MessageCandidatesSectionViewModel(
        MessageCandidateStore candidates,
        MessageStore messages,
        DialogService? dialogs = null,
        GameDataCache? cache = null,
        MessageCandidateWatcher? watcher = null,
        LogDiagnosticState? diagnostics = null,
        Func<int, int, string?>? likelySource = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(messages);
        _candidates = candidates;
        _messages = messages;
        _dialogs = dialogs;
        _cache = cache;
        _watcher = watcher;
        _diagnostics = diagnostics;
        _likelySource = likelySource;
        _handler = (_, _) => Reload();
        _candidates.Candidates.CollectionChanged += _handler;
        OpenEditAsyncCommand  = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => SelectedRow is not null);
        DismissSelectedCommand = new RelayCommand(DismissSelected, () => SelectedRow is not null);
        ExportNonDismissedCommand = new RelayCommand(ExportNonDismissed);
        if (_watcher is not null)
            SimulateEntryCommand = new RelayCommand(() => _watcher.SimulateCapture());
        // Mirror the Log pane's Simulate-dropdown toggle so the button appears /
        // hides live while this tab is open.
        if (_diagnostics is not null) _diagnostics.Changed += OnDiagnosticsChanged;

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedRow))
            {
                RemoveSelectedCommand.NotifyCanExecuteChanged();
                DismissSelectedCommand.NotifyCanExecuteChanged();
            }
        };

        Reload();
    }

    private void OnDiagnosticsChanged() =>
        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ShowSimulate)));

    public override void Dispose()
    {
        _candidates.Candidates.CollectionChanged -= _handler;
        if (_diagnostics is not null) _diagnostics.Changed -= OnDiagnosticsChanged;
        base.Dispose();
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        foreach (MessageCandidateRecord c in _candidates.Candidates)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Raw Text"]    = c.RawText,
                // Map:Room where the line was first seen — the locator hint for
                // tracking down its source. Blank when position wasn't yet known.
                ["Seen In"]     = c.Map is { } m && c.Room is { } rm ? $"{m}:{rm}" : "",
                // Spells castable by monsters in that room — a starting point for
                // "which spell's message is this?". Blank when no location / no attributor.
                ["Likely source"] = c.Map is { } lm && c.Room is { } lr ? (_likelySource?.Invoke(lm, lr) ?? "") : "",
                ["Occurrences"] = c.Occurrences.ToString(),
                ["First Seen"]  = c.FirstSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                ["Last Seen"]   = c.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                ["Status"]      = c.Dismissed ? "Dismissed" : "Pending",
            };
            GameDataRow row = GameDataRow.FromDictionary(dict, Columns);
            row.Tag = c;
            rows.Add(row);
        }
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        if (row.Tag is not MessageCandidateRecord candidate) return;

        MessageRecord seed = MessageCandidateCommit.BuildSeedRecord(candidate);
        MessageEditDialogViewModel vm = new(
            seed, SettingsTier.Defaults, _messages.Messages, isNew: true, cache: _cache);
        MessageEditResult? result =
            await _dialogs.OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        if (result is null) return;

        MessageCandidateCommit.Commit(_messages, _candidates, result, candidate.Id);
    }

    // Hard-remove the selected row(s) from the catalogue — matches every other
    // table's Remove. A candidate is transient review data, not curated game
    // data, so this deliberately skips the Confirm.ConfirmDeleteAsync prompt the
    // curated tables use; a later recurrence simply re-captures the line as new.
    private void RemoveSelected() => ForEachSelected(id => _candidates.Remove(id));

    // Dismiss the selected row(s) — sticky "decided, stop tracking": the row
    // stays (frozen) but the watcher then ignores every recurrence of that text
    // (MessageCandidateStore.Dismiss + the watcher's IsDismissed gate). No
    // confirm prompt — nothing is deleted, so a "Delete this?" dialog would mislead.
    private void DismissSelected() => ForEachSelected(id => _candidates.Dismiss(id));

    private void ForEachSelected(Action<string> act)
    {
        IReadOnlyList<GameDataRow> selection = SelectedRows.Count > 0
            ? new List<GameDataRow>(SelectedRows)
            : (SelectedRow is null ? Array.Empty<GameDataRow>() : new[] { SelectedRow });
        foreach (GameDataRow row in selection)
            if (row.Tag is MessageCandidateRecord candidate)
                act(candidate.Id);
    }

    // Write every non-dismissed candidate to a timestamped file on the Desktop —
    // the raw line plus its Seen-In location, occurrence count, and Likely-source
    // shortlist — so a batch of unattributed lines can be handed off for review.
    private void ExportNonDismissed()
    {
        var pending = _candidates.Candidates.Where(c => !c.Dismissed).ToList();
        LogService? log = AppServices.CurrentOrNull?.Log;
        if (pending.Count == 0)
        {
            log?.Info("Unrecognized Lines", "Export: no non-dismissed lines to write.");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("# Unrecognized lines — ").Append(pending.Count)
          .Append(pending.Count == 1 ? " line" : " lines").Append('\n').Append('\n');
        foreach (MessageCandidateRecord c in pending)
        {
            sb.Append("- ").Append(c.RawText).Append('\n');
            string loc = c.Map is { } m && c.Room is { } rm ? $"{m}:{rm}" : "unknown";
            sb.Append("  - seen in: ").Append(loc)
              .Append("  ·  occurrences: ").Append(c.Occurrences).Append('\n');
            if (c.Map is { } lm && c.Room is { } lr
                && _likelySource?.Invoke(lm, lr) is { Length: > 0 } src)
                sb.Append("  - likely source: ").Append(src).Append('\n');
        }

        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            // Date.Now is app-runtime state (not a workflow script) — fine here.
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string path = System.IO.Path.Combine(desktop, $"unrecognized-lines-{stamp}.md");
            System.IO.File.WriteAllText(path, sb.ToString());
            log?.Info("Unrecognized Lines", $"Export: wrote {pending.Count} line(s) to {path}.");
        }
        catch (Exception ex)
        {
            log?.Error("Unrecognized Lines", $"Export: failed to write the file: {ex.Message}");
        }
    }
}
