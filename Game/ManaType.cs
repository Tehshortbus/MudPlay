namespace FujinTerm.Game;

// Which mana-pool flavour the local character runs on. The status line emits
// one of these tags between brackets (MA=... for mana-using classes, KAI=...
// for monks); None covers classes with no mana pool (warriors, etc.) where the
// statline omits the tag.
public enum ManaType
{
    None,
    Mana,
    Kai,
}
