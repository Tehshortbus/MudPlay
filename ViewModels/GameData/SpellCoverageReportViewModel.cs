using System.Collections.ObjectModel;
using System.Threading.Tasks;
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
            SummaryText =
                $"The Spells listed below from Game Data set \"{result.SetName}\" has " +
                $"{result.UnanchoredCount} of {result.ConsideredCount} player-facing spells with no Message Anchor.";
            WindowTitle = $"Spell coverage — {result.SetName}";
        });
    }

    /// <summary>Force a fresh audit run. Useful when the user just edited the messages tab.</summary>
    [RelayCommand]
    private void Refresh() => _auditor.Run();

    /// <summary>
    /// Double-click drilldown — opens the Message edit dialog with
    /// a fresh record whose Name is the spell's Name and whose Links
    /// already point at <c>(Spells, spell.Number)</c>. The user fills
    /// in Message / EndsWith / flags / Action / Response and saves;
    /// the audit fires after the save and the spell disappears from
    /// the table on the next Refresh.
    /// </summary>
    [RelayCommand]
    private async Task CreateMessageForSpellAsync(UnanchoredSpell? spell)
    {
        if (spell is null) return;
        DialogService dialogs = AppServices.Current.Dialogs;
        MessageStore  store   = AppServices.Current.Messages;
        GameDataCache cache   = AppServices.Current.GameData;

        MessageRecord blank = new(
            Id:          string.Empty,
            Name:        spell.Name,
            Message:     string.Empty,
            EndsWith:    string.Empty,
            Action:      MessageAction.Ignore,
            Flags:       MessageFlags.None,
            RawFlagsHex: 0,
            Response:    string.Empty,
            Links:       new[] { new GameDataLink("Spells", spell.Number) });

        MessageEditDialogViewModel vm = new(
            blank,
            currentTier:     SettingsTier.Defaults,
            existingRecords: store.Messages,
            isNew:           true,
            cache:           cache);
        MessageEditResult? result = await dialogs
            .OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        if (result is null) return;

        // Mirror MessagesSectionViewModel.ApplyResult so the same
        // Id-based de-dup + Save semantics apply.
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
