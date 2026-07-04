using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData;

// Modeless detail surface for the SpellCoverageAuditor — opened from a LogPane
// double-click on the auditor's summary entry. Shows the active set's full list of
// player-facing spells with no Message anchor, with a Refresh button that re-runs the
// audit on demand.
public sealed partial class SpellCoverageReportViewModel : ObservableObject
{
    private readonly SpellCoverageAuditor _auditor;

    public ObservableCollection<UnanchoredSpell> Rows { get; } = new();

    // Sortable view wrapper Avalonia's DataGrid needs to honor column-header clicks. An
    // ObservableCollection alone is not sortable by the grid; wrapping in
    // DataGridCollectionView hooks up the SortMemberPath-driven comparer the grid expects.
    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private string _summaryText = "(no audit run yet)";
    [ObservableProperty] private string _windowTitle = "Spell coverage";

    public SpellCoverageReportViewModel(SpellCoverageAuditor auditor)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        _auditor = auditor;
        RowsView = new DataGridCollectionView(Rows);
        _auditor.ResultAvailable += OnResultAvailable;
        if (_auditor.Latest is { } current) OnResultAvailable(current);
    }

    private void OnResultAvailable(CoverageResult result)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Rows.Clear();
            foreach (UnanchoredSpell s in result.Unanchored) Rows.Add(s);
            SummaryText =
                $"The {result.UnanchoredCount} of {result.ConsideredCount} Spells listed below are player-facing spells " +
                $"from Game Data Set: {result.SetName} that are missing an associated Message.";
            WindowTitle = $"Spell coverage — {result.SetName}";
        });
    }

    // Force a fresh audit run. Useful when the user just edited the messages tab.
    [RelayCommand]
    private void Refresh() => _auditor.Run();

    // Save the currently-listed missing spells to a tab-separated text file via the
    // user's file picker. One row per spell with columns # / Name / Classes / Casted By /
    // Learned From — opens cleanly in any text editor or spreadsheet. A leading comment
    // line records the source set + counts.
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        CoverageResult? latest = _auditor.Latest;
        string setName = latest?.SetName ?? "no-set";
        int considered = latest?.ConsideredCount ?? 0;

        IStorageFile? file = await main.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export spell coverage",
            SuggestedFileName = $"spell-coverage-{setName}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension  = "txt",
            FileTypeChoices   =
            [
                new FilePickerFileType("Plain text (.txt)") { Patterns = ["*.txt"] },
            ],
        });
        if (file is null) return;

        // Tab-separated body — opens in spreadsheets cleanly and stays
        // human-readable in any text editor. Leading "#" lines are
        // comments (TSV consumers skip them, humans read them).
        StringBuilder sb = new(capacity: Rows.Count * 80);
        sb.Append("# Spell coverage export — set '").Append(setName).Append("'\n");
        sb.Append("# ").Append(Rows.Count).Append(" of ").Append(considered)
          .Append(" player-facing spells with no Message anchor\n");
        sb.Append("#\n");
        sb.Append("Number\tName\tClasses\tCasted By\tLearned From\n");
        foreach (UnanchoredSpell s in Rows)
        {
            sb.Append(s.Number).Append('\t')
              .Append(s.Name).Append('\t')
              .Append(string.IsNullOrEmpty(s.Classes)     ? "(none)" : s.Classes).Append('\t')
              .Append(string.IsNullOrEmpty(s.CastedBy)    ? "(none)" : s.CastedBy).Append('\t')
              .Append(string.IsNullOrEmpty(s.LearnedFrom) ? "(none)" : s.LearnedFrom).Append('\n');
        }

        await using Stream stream = await file.OpenWriteAsync();
        await using StreamWriter writer = new(stream);
        await writer.WriteAsync(sb.ToString()).ConfigureAwait(false);
    }

    // Double-click drilldown — opens the Message edit dialog with a fresh record whose
    // Name is the spell's Name and whose Links point at (Spells, spell.Number). The user
    // fills in the 5 perspective line slots (caster / target / witness / applied /
    // stat-line), flags, and Action; on save the audit fires and the spell drops off the
    // unanchored list on the next refresh.
    [RelayCommand]
    private async Task CreateMessageForSpellAsync(UnanchoredSpell? spell)
    {
        if (spell is null) return;
        DialogService dialogs = AppServices.Current.Dialogs;
        MessageStore  store   = AppServices.Current.Messages;
        GameDataCache cache   = AppServices.Current.GameData;

        // Link directly to the spell row — the combat-engine target.
        // Per user direction we no longer seed item / monster caster
        // links here; those belonged to the old multi-link audit
        // concept that's been superseded by the one-record-per-spell
        // model.
        List<GameDataLink> links = new() { new GameDataLink("Spells", spell.Number) };

        MessageRecord blank = new(
            Id:              string.Empty,
            Name:            spell.Name,
            Action:          MessageAction.Ignore,
            Flags:           MessageFlags.None,
            RawFlagsHex:     0,
            Response:        string.Empty,
            CasterMessage:   string.Empty,
            TargetMessage:   string.Empty,
            WitnessMessage:  string.Empty,
            AppliedMessage:  string.Empty,
            AppliedEndsWith: string.Empty,
            Links:           links);

        MessageEditDialogViewModel vm = new(
            blank,
            currentTier:     SettingsTier.Defaults,
            existingRecords: store.Messages,
            isNew:           true,
            cache:           cache);
        MessageEditResult? result = await dialogs
            .OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        if (result is null) return;

        // Mirror MessagesSectionViewModel.ApplyResult — Id-keyed
        // replace, else append, then save.
        int idx = -1;
        for (int i = 0; i < store.Messages.Count; i++)
        {
            if (store.Messages[i].Id == result.Original.Id) { idx = i; break; }
        }
        if (idx >= 0) store.Messages[idx] = result.Updated;
        else          store.Messages.Add(result.Updated);
        store.Save();
    }

    // Unsubscribe when the window closes so we don't pin the VM in the auditor's event chain.
    public void Detach()
    {
        _auditor.ResultAvailable -= OnResultAvailable;
    }

}
