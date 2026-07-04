namespace FujinTerm.Game.Inventory;

// Outcome of an @equip-<set> / set-apply request, so callers (EquipHandler) can
// craft the right reply.
public enum EquipResult
{
    // A wear sequence started (or a virtual-slot write landed).
    Applied,

    // The set was found but the character already wears it — no-op.
    NoChange,

    // No set matched the keyword (or set name).
    NotFound,

    // An equip sequence is already in flight; the request was declined.
    Busy,
}
