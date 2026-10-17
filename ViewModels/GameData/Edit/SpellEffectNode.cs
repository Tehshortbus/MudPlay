using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Edit;

// One node in a room spell's decoded effect tree (see SpellEffectTreeDecoder). A
// branch node is a condition header (level gate + carry check) that tints its border
// and holds child outcome nodes; an outcome node carries a weighted percentage and its
// effect as mixed text/link runs (summon → monster record, teleport → map room, cast →
// spell record). Deep or huge sub-trees collapse to a single summary node. Rendered as
// an expandable tree in the spell record's Game Data tab, and reused for a monster's
// greet keywords (see GreetTree).
public sealed partial class SpellEffectNode : ObservableObject
{
    // Weighted chance of this outcome under its parent random block; null on a branch
    // header (a condition, not a roll).
    public int? Percent { get; init; }

    // The line content — plain text with any embedded record/room links.
    public IReadOnlyList<MdbInline> Runs { get; init; } = Array.Empty<MdbInline>();

    public IReadOnlyList<SpellEffectNode> Children { get; init; } = Array.Empty<SpellEffectNode>();

    // "danger" (no counter carried), "accent" (safe / countered), or "neutral".
    public string Tone { get; init; } = "neutral";

    // A condition header (bordered + tinted) vs a plain outcome/effect row.
    public bool IsBranch { get; init; }

    // The tree's live expand state — bound two-way by the view so the Expand all /
    // Collapse all buttons can drive it. Branches and shallow rolls open; deep rolls
    // and the big collapsed room lists start closed so the tree opens tidy.
    [ObservableProperty] private bool _isExpanded = true;

    public bool HasPercent => Percent is not null;
    public string PercentText => Percent is { } p ? $"{p}%" : string.Empty;

    // Tone → the CSS-ish brush keys the view maps to (border + percent colour).
    public bool IsDanger => Tone == "danger";
    public bool IsAccent => Tone == "accent";

    // Drives a tree's Expand all / Collapse all buttons.
    public static void SetExpanded(IReadOnlyList<SpellEffectNode> nodes, bool expanded)
    {
        foreach (SpellEffectNode n in nodes)
        {
            n.IsExpanded = expanded;
            SetExpanded(n.Children, expanded);
        }
    }
}
