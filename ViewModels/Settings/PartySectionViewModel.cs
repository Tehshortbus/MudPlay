using Avalonia.Controls;
using FujinTerm.Views.Settings;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// "Party" tab — bespoke layout. Rank picker at the top, party-heal
/// rows with inline spell + threshold, 10 party-bless slots (no
/// timeouts — the bless engine will be timed), then options and
/// capacity / cadence knobs. The cross-cutting "spell type priority"
/// list now lives on the Spells tab so one config governs both self
/// and party casts.
/// </summary>
public sealed class PartySectionViewModel : SettingsSectionViewModel
{
    private Control? _view;

    public override string Id => "party";
    public override string Title => "Party";

    public string PhaseTag => "Phase 6 (PartyManager) + Phase 13 PR 13.D (CastingDirector — party)";

    public string Description =>
        "Party-coordination knobs plus the party-cast spell picks. Heal rows put the spell and threshold side by " +
        "side; bless takes 10 slots without per-slot timeouts (the bless engine handles re-cast cadence on its " +
        "own). Cure / buff / heal priority is configured once on the Spells tab and applies to both self and party.";

    public override Control View => _view ??= new PartySectionView { DataContext = this };

    public override IEnumerable<string> SearchableLabels => new[]
    {
        "Party", "Rank", "Front", "Mid", "Back",
        "Minor heal", "Major heal", "Request healing",
        "Bless", "Auto-share cash", "Help leader bash doors",
        "Auto-invite", "Auto-Exp-Reset", "par frequency",
        "Wait for members", "Max monsters", "Max monster experience",
        "Attack last in party", "Attack in reverse order",
        "Attack what other members attack", "Request party health",
        "Ignore party when following", "Auto-collect when following",
        "Say emote", "Go @panic when injured",
    };
}
