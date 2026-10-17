using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Train;

// One party member's money, as the funding plan sees it. Trains marks a member this
// trip trains (so it has a Cost); anyone may donate from Spare, which the member
// computed as what it can hand over and still pay for itself and keep its
// keep-on-hand floor.
public readonly record struct PartyTrainFunder(
    string Name,
    bool Trains,
    long CostCopper,
    long CashCopper,
    long SpareCopper,
    long BankCopper,
    string? BankName);

public readonly record struct PartyCoinTransfer(string From, string To, long Copper);
public readonly record struct PartyBankWithdrawal(string Name, long Copper);

// BankName is the branch the trip stops at first (null = no bank stop). Unfunded
// members can't be covered this trip and are dropped from it.
public readonly record struct PartyTrainFundingOutcome(
    IReadOnlyList<PartyCoinTransfer> Transfers,
    string? BankName,
    IReadOnlyList<PartyBankWithdrawal> Withdrawals,
    IReadOnlyList<string> Unfunded);

// Decides how the party pays for a trip, in the user's order:
//
//   1. Everyone's purse covers their own train → nothing to do.
//   2. The party's spare coin covers every shortfall → share it (gives happen where
//      the party already stands; no detour).
//   3. Otherwise → one bank stop. A short member with enough on deposit at that
//      branch withdraws its own shortfall ("covers its own fee"); the branch chosen
//      is the one that covers the most shortfall. Anyone still short is covered from
//      the party's spare as far as it goes, and whoever's left sits the trip out.
//
// A withdraw only works at a branch you hold a deposit at, so each member reports
// its largest single branch; a member whose money is at a different branch than the
// stop can't draw there and falls through to sharing.
//
// Pure — no wire, no map.
public static class PartyTrainFundingPlanner
{
    public static PartyTrainFundingOutcome Plan(IReadOnlyList<PartyTrainFunder> party)
    {
        System.ArgumentNullException.ThrowIfNull(party);

        Dictionary<string, long> need = new(System.StringComparer.OrdinalIgnoreCase);
        foreach (PartyTrainFunder f in party)
        {
            if (!f.Trains) continue;
            long gap = f.CostCopper - f.CashCopper;
            if (gap > 0) need[f.Name] = gap;
        }
        if (need.Count == 0) return Empty(null);

        Dictionary<string, long> spare = party
            .Where(f => f.SpareCopper > 0 && !need.ContainsKey(f.Name))
            .ToDictionary(f => f.Name, f => f.SpareCopper, System.StringComparer.OrdinalIgnoreCase);

        // Step 2: sharing alone covers everyone.
        if (spare.Values.Sum() >= need.Values.Sum())
            return new PartyTrainFundingOutcome(Share(need, spare), null, [], []);

        // Step 3: a bank stop. Pick the branch that covers the most shortfall among
        // members who can cover their whole gap from it.
        string? bank = party
            .Where(f => need.TryGetValue(f.Name, out long gap)
                        && !string.IsNullOrWhiteSpace(f.BankName)
                        && f.BankCopper >= gap)
            .GroupBy(f => f.BankName!, System.StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(f => need[f.Name]))
            .Select(g => g.Key)
            .FirstOrDefault();

        List<PartyBankWithdrawal> withdrawals = new();
        if (bank is not null)
        {
            foreach (PartyTrainFunder f in party)
            {
                if (!need.TryGetValue(f.Name, out long gap)) continue;
                if (!string.Equals(f.BankName, bank, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (f.BankCopper < gap) continue;
                withdrawals.Add(new PartyBankWithdrawal(f.Name, gap));
                need.Remove(f.Name);
            }
        }

        List<PartyCoinTransfer> transfers = Share(need, spare);
        List<string> unfunded = need.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        return new PartyTrainFundingOutcome(transfers, withdrawals.Count > 0 ? bank : null, withdrawals, unfunded);
    }

    // Greedy: biggest shortfall first, drawn from the biggest spare first — the
    // fewest separate `give`s. Mutates both maps: covered needs drop to 0, donors are
    // drawn down, so a caller can see what's left uncovered afterwards.
    private static List<PartyCoinTransfer> Share(Dictionary<string, long> need, Dictionary<string, long> spare)
    {
        List<PartyCoinTransfer> transfers = new();
        foreach (string taker in need.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList())
        {
            foreach (string giver in spare.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList())
            {
                if (need[taker] <= 0) break;
                long give = System.Math.Min(need[taker], spare[giver]);
                if (give <= 0) continue;
                transfers.Add(new PartyCoinTransfer(giver, taker, give));
                need[taker] -= give;
                spare[giver] -= give;
            }
        }
        return transfers;
    }

    private static PartyTrainFundingOutcome Empty(string? bank) => new([], bank, [], []);
}
