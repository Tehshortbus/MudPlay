namespace FujinTerm.Game.Inventory;

/// <summary>
/// Outcome of an <c>@equip-&lt;set&gt;</c> / set-apply request, so callers
/// (the <see cref="Game.Remote.EquipHandler"/>) can craft the right reply.
/// </summary>
public enum EquipResult
{
    /// <summary>A wear sequence started (or a virtual-slot write landed).</summary>
    Applied,

    /// <summary>The set was found but the character already wears it — no-op.</summary>
    NoChange,

    /// <summary>No set matched the keyword (or set name).</summary>
    NotFound,

    /// <summary>An equip sequence is already in flight; the request was declined.</summary>
    Busy,
}
