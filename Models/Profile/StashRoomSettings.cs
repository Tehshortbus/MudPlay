namespace FujinTerm.Models.Profile;

/// <summary>
/// Per-character "stash room" registry — drives
/// <see cref="Game.Cash.StashRoomManager"/>'s on-entry stash plan.
/// Stored as the <c>"StashRooms"</c> entry in
/// <see cref="CharacterProfile.Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// A stash room is a user-marked room (typically a hideout) where the
/// character automatically dumps excess currency on entry via
/// <c>hide N &lt;coin&gt;</c>. v1 covers cash only — item-level stash
/// rules (dump excess potions / etc.) land when the inventory
/// subsystem ships.
/// </para>
/// <para>
/// Settings shape mirrors <see cref="CashSettings"/> per the MudProxy
/// audit so the future StashRoomManager + the existing CashManager
/// can share parse / dispatch primitives without divergent DTOs.
/// </para>
/// </remarks>
public sealed class StashRoomSettings
{
    /// <summary>Registered stash rooms. Look-up is by
    /// <see cref="StashRoom.Room"/> key on
    /// <c>RoomTracker.StateChanged</c>.</summary>
    public List<StashRoom> Rooms { get; set; } = new();
}

/// <summary>One stash room + the per-currency dump rules that fire
/// on entry.</summary>
public sealed class StashRoom
{
    /// <summary>Map + room number the user marked. Matched against
    /// <c>RoomTracker.State.CurrentRoom.Key</c>.</summary>
    public RoomRef? Room { get; set; }

    /// <summary>Free-text label shown in the UI ("Sewers hideout",
    /// "Treetop cache"). Pure user convenience.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Per-currency dump rules. Each rule fires its own
    /// <c>hide N &lt;coin&gt;</c> command on entry.</summary>
    public List<StashCurrencyRule> CurrencyRules { get; set; } = new();
}

/// <summary>Single per-currency dump rule on a
/// <see cref="StashRoom"/>.</summary>
public sealed class StashCurrencyRule
{
    /// <summary>Currency name as the wire emits it ("gold",
    /// "platinum", "runic", etc.). Matched case-insensitive against
    /// the keys in <c>CashManager._held</c>.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>How much to leave in hand after the stash. The dump
    /// amount is <c>HeldCoin - KeepAtLeast</c> (skipped if non-
    /// positive). <c>0</c> dumps the entire held tally; sensible
    /// default for a pure cache room.</summary>
    public long KeepAtLeast { get; set; }
}
