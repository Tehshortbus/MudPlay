using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

/// <summary>
/// Game Data Browser → Conditions tab. Surfaces the standalone
/// non-spell condition messages (blinded / poisoned / paralyzed /
/// confused / diseased / regenerating / etc.) with their
/// effect-flag bitfield + action enum (Ignore / Recheck / Wait /
/// RestHp / RestMana / DontRestRun / Hangup) — per MudProxy's
/// <c>GameMessages_Plan.md</c>.
/// </summary>
/// <remarks>
/// Per master plan, this catalogue is "built-in pre-seeded; user can
/// add/edit". PR 5.9 ships the listing surface and the data shape;
/// the pre-seed comes with the v1.11p starter bundle (PR 5.25), and
/// the editor lives in the per-row edit dialog landing alongside the
/// other tabs' editors after the read-only listings are in place.
/// </remarks>
public sealed class ConditionsSectionViewModel : GameDataTableSectionViewModel
{
    public override string Id => "conditions";
    public override string Title => "Conditions";

    protected override string TableName => "Conditions";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Name",
        "Action",
        "EffectFlags",
        "Pattern",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "condition", "blinded", "poisoned", "paralyzed",
        "confused", "diseased", "regenerating",
    };

    public ConditionsSectionViewModel(GameDataCache cache) : base(cache) { }
}
