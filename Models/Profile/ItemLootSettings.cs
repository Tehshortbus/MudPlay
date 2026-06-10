namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "Item Loot" settings — drives
/// <see cref="Game.Inventory.AutoGetItemsManager"/>'s collect-timing
/// behaviour. Stored as the <c>"ItemLoot"</c> entry in
/// <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// Per-item collect/ignore is NOT here — that decision lives on each
/// item's game-data <see cref="Models.GameData.ItemOverlay.AutoCollect"/>
/// flag (resolved per character through the 4-tier hierarchy). This DTO
/// carries only the room-wide timing toggle. The master on/off switch
/// is <see cref="AutoActionDefaults.AutoGetItems"/> on the General tab.
/// </remarks>
public sealed class ItemLootSettings
{
    /// <summary>
    /// Defer item gets until the room's combat is finished (every
    /// engageable hostile is dead). When off, gets fire as soon as the
    /// room survey is parsed, regardless of combat. Mirrors the Cash
    /// tab's same-named toggle.
    /// </summary>
    public bool CollectAfterCombatFinished { get; set; }
}
