using MudPlay.Game.Map;

namespace MudPlay.ViewModels.Navigation;

// What a movement engine is doing right now, for the Navigation top bar.
public enum NavActivityKind { None, Moving, Fighting, Waiting, Paused }

// How a top-bar chip after the status line reads: a hold that stops movement, or an
// errand trip the walker is on.
public enum NavChipTone { Wait, Trip }

// Who a party hold is about, and what kind of cast a stealth stop is for, so the
// chips can name them ("@Wait Bob", "Healing").
public sealed record NavHoldNames(
    IReadOnlyCollection<string> WaitingFor,
    IReadOnlyCollection<string> Disconnected,
    IReadOnlyCollection<string> Invited,
    IReadOnlyCollection<string> Downed,
    string? HeldCast,
    // The move the user typed that paused navigation, while that pause stands.
    string? TypedMove = null,
    // The route items a walk is waiting on the party's count of.
    IReadOnlyCollection<string>? CountedItems = null)
{
    public static readonly NavHoldNames None = new([], [], [], [], null);
}

// Pure mapping from the live MovementCoordinator gate state to a plain-English
// "what is the engine doing / why is it held" phrase. Split out of
// NavigationViewModel. Every MovementCoordinator *Gate constant must resolve to a
// real label here, so a NEW gate can't silently slip through to a raw internal
// name on the UI.
//
// The order is a priority scan: the most important reason a human wants to see
// wins (an explicit user pause, then combat, then severe self-states, then
// recovery / party holds, then the brief engine-wait beats). It mirrors the gate
// tiers on MovementCoordinator; keep new gates in the tier that matches their
// urgency rather than appending blindly.
public static class NavActivity
{
    // Every hold the engine can be under, in priority order, with its plain label.
    // Moving-kind entries are the brief per-room settle beats: a moment in the
    // middle of moving, not a real stop, so they sit last and any real wait wins.
    // Chip: whether it shows as a chip after the status line — the user picked
    // which are worth seeing (2026-09-30); the rest still name a queued route's hold.
    private static readonly (string Gate, string Label, NavActivityKind Kind, bool Chip)[] Holds =
    [
        (MovementCoordinator.AbandonedCombatGate, "leaving a fight", NavActivityKind.Waiting, false),
        (MovementCoordinator.MortallyWoundedGate, "Mortally Wounded", NavActivityKind.Waiting, true),
        // Our own confusion holds navigation locally (the leader/solo analogue of a
        // confused follower's @wait) — our own affliction, same tier as held.
        (MovementCoordinator.ConfusionGate, "Confused", NavActivityKind.Waiting, true),
        (MovementCoordinator.HeldGate, "Held", NavActivityKind.Waiting, true),
        (MovementCoordinator.FearGate, "Feared", NavActivityKind.Waiting, true),
        (MovementCoordinator.TooHeavyGate, "Too heavy", NavActivityKind.Waiting, true),
        // Recovery holds — resting / meditating below a rest floor.
        (MovementCoordinator.HealthRecoveryGate, "Low HP", NavActivityKind.Waiting, true),
        (MovementCoordinator.ManaRecoveryGate, "Low MANA", NavActivityKind.Waiting, true),
        (MovementCoordinator.CorpseRecoveryGate, "Corpse Recovery", NavActivityKind.Waiting, true),
        // Party holds — waiting on other members. The named ones expand per member.
        (MovementCoordinator.PartyWaitGate, "@Wait", NavActivityKind.Waiting, true),
        (MovementCoordinator.AllyDownGate, "Downed Ally", NavActivityKind.Waiting, true),
        (MovementCoordinator.PartyVitalsGate, "party member hurt", NavActivityKind.Waiting, false),
        (MovementCoordinator.MemberDisconnectGate, "member disconnected", NavActivityKind.Waiting, true),
        (MovementCoordinator.ReconnectReformGate, "re-forming the party", NavActivityKind.Waiting, false),
        (MovementCoordinator.HangupItemCheckGate, "checking what a hang-up cost", NavActivityKind.Waiting, false),
        (MovementCoordinator.PartyInviteGate, "waiting on an invite", NavActivityKind.Waiting, true),
        // A walk that hasn't set off: the one hold that answers "why isn't it moving?"
        // at the very start, so it gets a chip though it lasts seconds.
        (MovementCoordinator.PartyItemCountGate, "asking the party who holds an item", NavActivityKind.Waiting, true),
        // A walk standing at a toll gate with the party: the same question there.
        (MovementCoordinator.PartyTollGate, "checking the party can pay a toll", NavActivityKind.Waiting, true),
        (MovementCoordinator.FollowerGate, "following leader", NavActivityKind.Waiting, false),
        // Auto-engines kill switch off — a queued walk / loop / lair is planned but
        // held here until Auto-All is restored; the single most common "why isn't it
        // moving?" for a queued route.
        (MovementCoordinator.AutoAllGate, "Auto-all is off", NavActivityKind.Waiting, true),
        // In-room engine actions after a fight clears.
        (MovementCoordinator.SearchGate, "Searching Room", NavActivityKind.Waiting, true),
        (MovementCoordinator.AcquisitionGate, "looting", NavActivityKind.Waiting, false),
        (MovementCoordinator.GhSortGate, "Roomba", NavActivityKind.Waiting, true),
        (MovementCoordinator.GearSwapGate, "changing gear", NavActivityKind.Waiting, false),
        (MovementCoordinator.CounterWearGate, "putting on a room-spell counter", NavActivityKind.Waiting, false),
        (MovementCoordinator.SneakCooldownGate, "Waiting to Sneak", NavActivityKind.Waiting, true),
        (MovementCoordinator.SneakSettleGate, "sneaking", NavActivityKind.Waiting, false),
        (MovementCoordinator.SneakCastGate, "Buffing", NavActivityKind.Waiting, true),
        (MovementCoordinator.SellingGate, "selling", NavActivityKind.Waiting, false),
        (MovementCoordinator.SellDetourGate, "stopping to go sell", NavActivityKind.Waiting, false),
        (MovementCoordinator.DarkRoomSettleGate, "checking the dark", NavActivityKind.Moving, false),
        (MovementCoordinator.CombatRedisplaySettleGate, "checking for an ambush", NavActivityKind.Moving, false),
        (MovementCoordinator.SummonDeathSettleGate, "checking for a summon", NavActivityKind.Moving, false),
        (MovementCoordinator.DeathStallGate, "waiting to loot", NavActivityKind.Waiting, false),
        (MovementCoordinator.LairDebuffGate, "Waiting to Debuff", NavActivityKind.Waiting, true),
        (MovementCoordinator.LairDebuffManaGate, "Waiting for Mana to Debuff", NavActivityKind.Waiting, true),
        (MovementCoordinator.HazardBuffGate, "raising a hazard buff", NavActivityKind.Waiting, false),
    ];

    public static (string Text, NavActivityKind Kind) Describe(
        IReadOnlyCollection<string> gates, bool isPaused, bool isMovementPrevented)
    {
        ArgumentNullException.ThrowIfNull(gates);

        // User pause and combat outrank everything: an explicit pause is the user's
        // own doing, and mid-fight "Fighting" is the more useful readout than a hold.
        if (gates.Contains(MovementCoordinator.UserGate)) return ("Paused", NavActivityKind.Paused);
        if (gates.Contains(MovementCoordinator.CombatGate)) return ("Fighting", NavActivityKind.Fighting);

        // Our own held/mortally-wounded state stops movement server-side. The
        // condition flag is authoritative (SelfHeldResponder asserts HeldGate off the
        // same edge), so it's checked ahead of the gate scan.
        if (isMovementPrevented) return ("Waiting — Held", NavActivityKind.Waiting);

        // Nothing is gating and we're not held → genuinely moving.
        if (!isPaused) return ("Moving", NavActivityKind.Moving);

        foreach ((string gate, string label, NavActivityKind kind, _) in Holds)
            if (gates.Contains(gate)) return ($"{kind} — {label}", kind);

        string first = gates.FirstOrDefault() ?? "?";
        return ($"Waiting — {first}", NavActivityKind.Waiting);
    }

    // The chips for the holds in force right now, in priority order. User pause and
    // combat are left out (the live state chip already reads Paused / Fighting), bar
    // a pause that a typed move caused, and so is every hold the user doesn't want
    // to see. The held condition flag stands
    // in for HeldGate, since it flips first and the two describe the same thing.
    public static IReadOnlyList<(string Label, NavChipTone Tone)> ActiveHolds(
        IReadOnlyCollection<string> gates, bool isMovementPrevented, NavHoldNames? names = null)
    {
        ArgumentNullException.ThrowIfNull(gates);
        names ??= NavHoldNames.None;
        List<(string, NavChipTone)> holds = [];
        // A pause nobody pressed Pause for. The state chip only says "Paused".
        if (names.TypedMove is { Length: > 0 } typed)
            holds.Add(($"You typed '{typed}' - Resume to go on", NavChipTone.Wait));
        if (isMovementPrevented) holds.Add(("Held", NavChipTone.Wait));
        foreach ((string gate, string label, _, bool chip) in Holds)
        {
            if (!chip || !gates.Contains(gate)) continue;
            if (isMovementPrevented && gate == MovementCoordinator.HeldGate) continue;
            switch (gate)
            {
                case MovementCoordinator.PartyWaitGate:
                    Named(holds, names.WaitingFor, n => $"@Wait {n}", label);
                    break;
                case MovementCoordinator.MemberDisconnectGate:
                    Named(holds, names.Disconnected, n => $"{n} disconnected", label);
                    break;
                case MovementCoordinator.AllyDownGate:
                    Named(holds, names.Downed, n => $"Downed Ally {n}", label);
                    break;
                case MovementCoordinator.PartyInviteGate:
                    Named(holds, names.Invited, n => $"Waiting on {n} to join", label);
                    break;
                case MovementCoordinator.PartyItemCountGate:
                    Named(holds, names.CountedItems ?? [], n => $"Asking who holds {n}", label);
                    break;
                case MovementCoordinator.SneakCastGate:
                    holds.Add((names.HeldCast ?? label, NavChipTone.Wait));
                    break;
                default:
                    holds.Add((label, NavChipTone.Wait));
                    break;
            }
        }
        foreach (string gate in gates)
            if (!IsMapped(gate)) holds.Add((gate, NavChipTone.Wait));
        return holds;
    }

    // Whether a gate has a reading here (pause and combat via the state chip, the
    // rest via the hold table). An unmapped gate shows as its raw name.
    public static bool IsMapped(string gate) =>
        gate is MovementCoordinator.UserGate or MovementCoordinator.CombatGate
        || Array.Exists(Holds, h => h.Gate == gate);

    // One chip per named member, or the plain label when no name is known.
    private static void Named(List<(string, NavChipTone)> holds, IReadOnlyCollection<string> who,
        Func<string, string> label, string fallback)
    {
        if (who.Count == 0) { holds.Add((fallback, NavChipTone.Wait)); return; }
        foreach (string n in who) holds.Add((label(n), NavChipTone.Wait));
    }

    // The specific wait reason to fold into the top-bar status line (e.g. "Walking
    // to X … — resting (low HP)"). Only a Waiting carries detail the state chip
    // doesn't already show — the chip's short word already says "Fighting" / "Paused"
    // / "Moving", so folding those onto the line would just repeat the chip beside
    // it. Null for everything but Waiting, so the line stays clean.
    public static string? HoldSuffix(string text, NavActivityKind kind)
    {
        const string waitingPrefix = "Waiting — ";
        if (kind != NavActivityKind.Waiting) return null;
        return text.StartsWith(waitingPrefix, StringComparison.Ordinal)
            ? text[waitingPrefix.Length..]
            : text;
    }
}
