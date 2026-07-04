namespace FujinTerm.Game.Cash;

/// <summary>
/// Which store a <see cref="TransactionEntry"/> came from — a cash-only
/// bank <c>dep</c>osit, or a stash room <c>hide</c> (cash and/or items).
/// </summary>
public enum TransactionKind
{
    /// <summary>An auto-deposit reroute dropped excess wealth at a bank.</summary>
    Bank,

    /// <summary>A stash room hid excess coin and/or auto-stash items.</summary>
    Stash,
}

/// <summary>
/// One recorded cash/item offload for the Session Stats → Transaction
/// history window: the time it happened, whether it was a bank deposit or a
/// stash-room hide, and a human-readable description of what was put away
/// (e.g. <c>"Deposited 12,300 wealth"</c> or <c>"Hid a torch, 400 gold"</c>).
/// </summary>
public readonly record struct TransactionEntry(
    DateTimeOffset Time,
    TransactionKind Kind,
    string Detail);
