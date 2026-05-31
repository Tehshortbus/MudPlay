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
                $"The {result.UnanchoredCount} of {result.ConsideredCount} Spells listed below are player-facing spells " +
                $"from Game Data Set: {result.SetName} that are missing an associated Message.";
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

        // Seed Links with every game-data entity that USES this
        // spell in-play — every Monster under Casted By + every Item
        // under Learned From. The Spell itself isn't linked: the user
        // wants the message anchored to the thing the player actually
        // encounters (the monster, the item), not the underlying
        // mechanic. De-duped via HashSet so a row appearing in
        // multiple fields doesn't double-link.
        List<GameDataLink> links = new();
        HashSet<(string, int)> seen = new();
        AppendRefsFromSpellRow(cache, spell.Number, "Casted By",    links, seen);
        AppendRefsFromSpellRow(cache, spell.Number, "Learned From", links, seen);

        MessageRecord blank = new(
            Id:          string.Empty,
            Name:        spell.Name,
            Message:     string.Empty,
            EndsWith:    string.Empty,
            Action:      MessageAction.Ignore,
            Flags:       MessageFlags.None,
            RawFlagsHex: 0,
            Response:    string.Empty,
            Links:       links);

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

    /// <summary>
    /// Re-read the raw Spells row for <paramref name="spellNumber"/>
    /// from the active set's cache, parse the <paramref name="fieldName"/>
    /// for Monster/Item/Spell <c>#N</c> tokens, and append each as a
    /// <see cref="GameDataLink"/> to <paramref name="links"/>. The
    /// resolved display strings on <see cref="UnanchoredSpell"/> can't
    /// be re-parsed (the resolver rewrote them to <c>"Name {N}"</c>
    /// form which loses the original "Monster #" / "Item #" tag), so
    /// we go back to the source.
    /// </summary>
    private static void AppendRefsFromSpellRow(
        GameDataCache cache,
        int spellNumber,
        string fieldName,
        List<GameDataLink> links,
        HashSet<(string, int)> seen)
    {
        System.Text.Json.JsonDocument? doc = cache.GetRawTable("Spells");
        if (doc is null) return;
        foreach (System.Text.Json.JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Number", out System.Text.Json.JsonElement numEl)) continue;
            if (numEl.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
            if (!numEl.TryGetInt32(out int n) || n != spellNumber) continue;
            if (!row.TryGetProperty(fieldName, out System.Text.Json.JsonElement el)) return;
            if (el.ValueKind != System.Text.Json.JsonValueKind.String) return;
            string? raw = el.GetString();
            foreach ((string table, int number) in SpellCoverageAuditor.ParseRefs(raw))
            {
                if (seen.Add((table, number)))
                    links.Add(new GameDataLink(table, number));
            }
            return;
        }
    }
}
