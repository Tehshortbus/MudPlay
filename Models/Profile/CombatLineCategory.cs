namespace MudPlay.Models.Profile;

// The kinds of line a fight prints, as the Settings → Combat "Hide combat lines" boxes
// name them. Stored by name in CombatSettings.HiddenCombatLines.
public enum CombatLineCategory
{
    YourHits,
    YourMisses,
    PartyHits,
    PartyMisses,
    PlayerHits,
    PlayerMisses,
    MonsterHitsOnYou,
    MonsterMissesOnYou,
    MonsterHitsOnOthers,
    MonsterMissesOnOthers,
    DamageNoAttacker,
    Reflects,
    AttackStarts,
    CombatMarkers,
    Experience,
    CoinDrops,
    Unknown,
}
