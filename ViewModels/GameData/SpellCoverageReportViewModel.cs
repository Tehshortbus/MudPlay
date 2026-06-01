using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData.Edit;

namespace FujinTerm.ViewModels.GameData;

/// <summary>
/// Modeless detail surface for the <see cref="SpellCoverageAuditor"/>
/// — opened from a LogPane double-click on the auditor's summary
/// entry. Shows the active set's full list of player-facing spells
/// with no Message anchor, with a Refresh button that re-runs the
/// audit on demand.
/// </summary>
public sealed partial class SpellCoverageReportViewModel : ObservableObject
{
    private readonly SpellCoverageAuditor _auditor;
    private readonly LogService? _log;

    public ObservableCollection<UnanchoredSpell> Rows { get; } = new();

    /// <summary>
    /// Sortable view wrapper Avalonia's DataGrid needs to honor column-header
    /// clicks. <see cref="ObservableCollection{T}"/> alone is not sortable by
    /// the grid; wrapping in <see cref="DataGridCollectionView"/> hooks up the
    /// SortMemberPath-driven comparer the grid expects.
    /// </summary>
    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private string _summaryText = "(no audit run yet)";
    [ObservableProperty] private string _windowTitle = "Spell coverage";

    public SpellCoverageReportViewModel(SpellCoverageAuditor auditor, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        _auditor = auditor;
        _log     = log;
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

    /// <summary>Force a fresh audit run. Useful when the user just edited the messages tab.</summary>
    [RelayCommand]
    private void Refresh() => _auditor.Run();

    /// <summary>
    /// Dump every currently-listed missing spell into the system log as
    /// individual entries tagged with <see cref="SpellCoverageAuditor.LogSource"/>.
    /// Useful for filtering / copy-pasting the unanchored list out of
    /// the LogPane when working through coverage. A leading summary
    /// line precedes the per-spell entries so the export is
    /// self-bounded in the log.
    /// </summary>
    [RelayCommand]
    private void ExportToLog()
    {
        if (_log is null) return;
        CoverageResult? latest = _auditor.Latest;
        string setName = latest?.SetName ?? "(no set)";
        int considered = latest?.ConsideredCount ?? 0;

        _log.Log(LogSeverity.Info, SpellCoverageAuditor.LogSource,
            $"=== Export: {Rows.Count} of {considered} unanchored spells from set '{setName}' ===");

        foreach (UnanchoredSpell s in Rows)
        {
            string classes     = string.IsNullOrEmpty(s.Classes)     ? "(none)" : s.Classes!;
            string castedBy    = string.IsNullOrEmpty(s.CastedBy)    ? "(none)" : s.CastedBy!;
            string learnedFrom = string.IsNullOrEmpty(s.LearnedFrom) ? "(none)" : s.LearnedFrom!;
            _log.Log(LogSeverity.Info, SpellCoverageAuditor.LogSource,
                $"Missing #{s.Number} {s.Name} | Classes: {classes} | Casted By: {castedBy} | Learned From: {learnedFrom}");
        }

        _log.Log(LogSeverity.Info, SpellCoverageAuditor.LogSource,
            $"=== End export ({Rows.Count} entries) ===");
    }

    /// <summary>
    /// Double-click drilldown — opens the Message edit dialog with a
    /// fresh record whose Name is the spell's Name and whose Links
    /// point at <c>(Spells, spell.Number)</c>. The user fills in the
    /// 5 perspective line slots (caster / target / witness / applied
    /// / stat-line), flags, and Action; on save the audit fires and
    /// the spell drops off the unanchored list on the next refresh.
    /// </summary>
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
            Id:                string.Empty,
            Name:              spell.Name,
            Action:            MessageAction.Ignore,
            Flags:             MessageFlags.None,
            RawFlagsHex:       0,
            Response:          string.Empty,
            CasterMessage:     string.Empty,
            TargetMessage:     string.Empty,
            WitnessMessage:    string.Empty,
            AppliedMessage:    string.Empty,
            AppliedEndsWith:   string.Empty,
            StatusLineMessage: string.Empty,
            Links:             links);

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

    /// <summary>Unsubscribe when the window closes so we don't pin the VM in the auditor's event chain.</summary>
    public void Detach()
    {
        _auditor.ResultAvailable -= OnResultAvailable;
    }

}
