namespace FujinTerm.Game.Cash;

// Which store a TransactionEntry came from — a cash-only bank `dep`osit, or a
// stash room `hide` (cash and/or items).
public enum TransactionKind
{
    // An auto-deposit reroute dropped excess wealth at a bank.
    Bank,

    // A stash room hid excess coin and/or auto-stash items.
    Stash,
}

// One recorded cash/item offload for the Session Stats → Transaction history
// window: the time it happened, whether it was a bank deposit or a stash-room
// hide, a human-readable description of what was put away (e.g. "Deposited
// 12,300 wealth" or "Hid a torch, 400 gold"), and where it happened — the bank
// / stash room rendered as "Name (map/room)". Location is null when the current
// room isn't known at echo time.
public readonly record struct TransactionEntry(
    DateTimeOffset Time,
    TransactionKind Kind,
    string Detail,
    string? Location = null);
