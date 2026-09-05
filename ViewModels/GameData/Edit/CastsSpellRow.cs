using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// One "casts" entry in an item's read-only MDB info: a spell the item delivers —
// a command-activated "use <item>" cast or a per-swing / per-kill combat proc —
// rendered as a clickable link to that spell's record (Message / Game-Data dialog).
// The spell record is where the cast's on-use / proc wording lives, shared across
// every item that casts the same spell. Effect is the pre-rendered damage / heal
// figure at the item's required level (blank when the spell yields no figure).
public sealed class CastsSpellRow
{
    public string Label { get; }        // "Casts (on use)" / "Casts (40%/swing)"
    public string SpellName { get; }     // resolved Spells.Name, with the number: "major weapon bless (#114)"
    public string Effect { get; }        // "Dmg 10–40", or "" when none
    public bool HasEffect => Effect.Length > 0;
    public ICommand Open { get; }

    public CastsSpellRow(string label, int spellNumber, string spellName, string effect)
    {
        Label = label;
        SpellName = spellNumber > 0 ? $"{spellName} (#{spellNumber})" : spellName;
        Effect = effect ?? string.Empty;
        Open = new AsyncRelayCommand(() => AppServices.Current.OpenSpellRecordAsync(spellNumber));
    }
}
