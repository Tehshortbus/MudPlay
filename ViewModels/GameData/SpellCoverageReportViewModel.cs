using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

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

    public ObservableCollection<UnanchoredSpell> Rows { get; } = new();

    [ObservableProperty] private string _summaryText = "(no audit run yet)";
    [ObservableProperty] private string _windowTitle = "Spell coverage";

    public SpellCoverageReportViewModel(SpellCoverageAuditor auditor)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        _auditor = auditor;
        _auditor.ResultAvailable += OnResultAvailable;
        if (_auditor.Latest is { } current) OnResultAvailable(current);
    }

    private void OnResultAvailable(CoverageResult result)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Rows.Clear();
            foreach (UnanchoredSpell s in result.Unanchored) Rows.Add(s);
            SummaryText = $"Set '{result.SetName}': {result.UnanchoredCount} of {result.ConsideredCount} player-facing spells have no Message anchor.";
            WindowTitle = $"Spell coverage — {result.SetName}";
        });
    }

    /// <summary>Force a fresh audit run. Useful when the user just edited the messages tab.</summary>
    [RelayCommand]
    private void Refresh() => _auditor.Run();

    /// <summary>Unsubscribe when the window closes so we don't pin the VM in the auditor's event chain.</summary>
    public void Detach()
    {
        _auditor.ResultAvailable -= OnResultAvailable;
    }
}
