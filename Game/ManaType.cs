namespace FujinTerm.Game;

/// <summary>
/// Which mana-pool flavour the local character runs on. The status line
/// emits one of these tags between brackets (<c>MA=...</c> for mana-using
/// classes, <c>KAI=...</c> for monks); <see cref="None"/> covers classes
/// with no mana pool (warriors, etc.) where the statline omits the tag.
/// </summary>
public enum ManaType
{
    None,
    Mana,
    Kai,
}
