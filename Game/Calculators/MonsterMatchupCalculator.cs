namespace FujinTerm.Game.Calculators;

/// <summary>
/// Composes a both-directions combat preview between the player and one
/// monster, on top of <see cref="CombatCalculator.CalculateHitChance"/>:
/// <list type="bullet">
/// <item><b>Player → monster</b> — normal-attack hit chance vs the monster's
/// AC (monsters carry no dodge / alignment ward), per-hit damage after the
/// monster's damage-resist, then DPS (<c>hit% × dmg/hit × swings</c>) and
/// rounds-to-kill.</item>
/// <item><b>Monster → player</b> — the monster's primary physical attack's
/// hit chance vs our AC + dodge (+ our prot-evil / prot-good only when the
/// monster is evil / good), and its per-hit damage after our damage-resist.
/// Physical attacks reduce by DR; elemental / magic resist doesn't apply to
/// the physical melee slot.</item>
/// </list>
/// Pure math — the caller extracts the monster row and current player profile
/// and passes typed values in.
/// </summary>
public static class MonsterMatchupCalculator
{
    /// <summary>Run the matchup for the supplied player and monster profiles.</summary>
    public static MonsterMatchupResult Compute(PlayerMatchupProfile player, MonsterMatchupProfile monster)
    {
        RealmType realm = player.Realm;

        // Player → monster. Monsters have no Dodge field and no prot wards.
        HitCalcResult playerHit = CombatCalculator.CalculateHitChance(
            attackerAccuracy: player.NormalAccuracy,
            defenderAC: monster.ArmourClass,
            defenderDodge: 0,
            realmType: realm);

        int playerDmgPerHit = System.Math.Max(0, player.AvgWeaponDamage - monster.DamageResist);

        double dps = player.HasWeapon
            ? playerHit.OverallHitPercent / 100.0 * playerDmgPerHit * player.SwingsPerRound
            : 0;
        // RoundsToKill is 0 when the player can't out-damage a kill (no weapon
        // or zero effective DPS) — the UI renders that as "—".
        int roundsToKill = dps > 0 ? (int)System.Math.Ceiling(monster.Hp / dps) : 0;

        // Monster → player. Only the primary physical slot is previewed.
        int monsterHit = 0;
        int monsterDmgPerHit = 0;
        if (monster.HasPhysicalAttack)
        {
            HitCalcResult mHit = CombatCalculator.CalculateHitChance(
                attackerAccuracy: monster.AttackAccuracy,
                defenderAC: player.ArmourClass,
                defenderDodge: player.Dodge,
                protEvil: monster.IsEvil ? player.ProtEvil : 0,
                protGood: monster.IsGood ? player.ProtGood : 0,
                realmType: realm);
            monsterHit = mHit.OverallHitPercent;
            monsterDmgPerHit = System.Math.Max(0, monster.AvgAttackDamage - player.DamageResist);
        }

        return new MonsterMatchupResult(
            PlayerHitPercent: playerHit.OverallHitPercent,
            PlayerDamagePerHit: playerDmgPerHit,
            PlayerSwingsPerRound: player.HasWeapon ? player.SwingsPerRound : 0,
            PlayerDps: dps,
            RoundsToKill: roundsToKill,
            HasWeapon: player.HasWeapon,
            MonsterHasPhysicalAttack: monster.HasPhysicalAttack,
            MonsterHitPercent: monsterHit,
            MonsterDamagePerHit: monsterDmgPerHit);
    }
}

/// <summary>
/// Player-side inputs to <see cref="MonsterMatchupCalculator.Compute"/> —
/// the offensive numbers (normal-attack accuracy, average weapon damage,
/// swings/round) and the defensive numbers (AC, dodge, prot wards, DR).
/// </summary>
/// <param name="Realm">Active realm — selects the Stock / ParaMUD hit formula.</param>
/// <param name="NormalAccuracy">Computed normal-attack accuracy (the to-hit number).</param>
/// <param name="AvgWeaponDamage">Average of the normal-attack min/max damage, before the monster's DR.</param>
/// <param name="SwingsPerRound">Swings landed per round with the current weapon.</param>
/// <param name="HasWeapon">False when unarmed — gates DPS / rounds-to-kill.</param>
/// <param name="ArmourClass">Player AC, the monster swings against.</param>
/// <param name="Dodge">Player raw dodge value (not a percentage).</param>
/// <param name="ProtEvil">Prot-evil ward, applied only when the monster is evil.</param>
/// <param name="ProtGood">Prot-good ward, applied only when the monster is good.</param>
/// <param name="DamageResist">Player DR, subtracted from each monster hit.</param>
public readonly record struct PlayerMatchupProfile(
    RealmType Realm,
    int NormalAccuracy,
    int AvgWeaponDamage,
    double SwingsPerRound,
    bool HasWeapon,
    int ArmourClass,
    int Dodge,
    int ProtEvil,
    int ProtGood,
    int DamageResist);

/// <summary>
/// Monster-side inputs to <see cref="MonsterMatchupCalculator.Compute"/> —
/// defense (AC / DR / HP) and the primary physical attack slot (accuracy +
/// average damage), plus the evil / good flags that gate the player's wards.
/// </summary>
/// <param name="ArmourClass">Monster AC the player swings against.</param>
/// <param name="DamageResist">Monster DR, subtracted from each player hit.</param>
/// <param name="Hp">Monster max HP, the rounds-to-kill denominator.</param>
/// <param name="HasPhysicalAttack">True when the monster has a melee / rob slot to preview.</param>
/// <param name="AttackAccuracy">Primary physical slot's to-hit accuracy.</param>
/// <param name="AvgAttackDamage">Average of the primary physical slot's min/max damage, before player DR.</param>
/// <param name="IsEvil">Monster is evil (Align ∈ {1,2,5,6}) — enables the player's prot-evil ward.</param>
/// <param name="IsGood">Monster is good (Align ∈ {0,4}) — enables the player's prot-good ward.</param>
public readonly record struct MonsterMatchupProfile(
    int ArmourClass,
    int DamageResist,
    int Hp,
    bool HasPhysicalAttack,
    int AttackAccuracy,
    int AvgAttackDamage,
    bool IsEvil,
    bool IsGood);

/// <summary>
/// Output of <see cref="MonsterMatchupCalculator.Compute"/> — both hit
/// directions plus the player's DPS / rounds-to-kill projection.
/// </summary>
/// <param name="PlayerHitPercent">Player normal-attack hit chance vs the monster (dodge is N/A for monsters).</param>
/// <param name="PlayerDamagePerHit">Player average damage per landed hit, after the monster's DR.</param>
/// <param name="PlayerSwingsPerRound">Swings/round used in the DPS projection (0 when unarmed).</param>
/// <param name="PlayerDps">Projected damage per round: <c>hit% × dmg/hit × swings</c>.</param>
/// <param name="RoundsToKill">Rounds to drop the monster at the projected DPS; 0 when not killable (no weapon / zero DPS).</param>
/// <param name="HasWeapon">Whether the player had a weapon equipped (gates the DPS fields in the UI).</param>
/// <param name="MonsterHasPhysicalAttack">Whether the monster has a physical slot to preview the return direction.</param>
/// <param name="MonsterHitPercent">Monster's primary-physical hit chance vs the player, dodge-adjusted.</param>
/// <param name="MonsterDamagePerHit">Monster average damage per landed hit, after the player's DR.</param>
public readonly record struct MonsterMatchupResult(
    int PlayerHitPercent,
    int PlayerDamagePerHit,
    double PlayerSwingsPerRound,
    double PlayerDps,
    int RoundsToKill,
    bool HasWeapon,
    bool MonsterHasPhysicalAttack,
    int MonsterHitPercent,
    int MonsterDamagePerHit);
