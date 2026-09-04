using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.Spells;

namespace MudPlay.Services;

// In-memory index of the active set's room-entry hazards: the harmful spell a
// room casts on the player when they step in (Room.Spell), mapped to the item(s)
// that make the room safe. Backs the navigation hazard-avoidance / gating pass —
// when a planned route would enter a room whose cast-on-enter spell harms and the
// player carries no counter item, the walker routes around it, offers the item as
// a path requirement, or (with the per-item auto-obtain flag) acquires it first.
//
// A room-entry spell is only a hazard when its unprotected effect actually HARMS
// the player — deals damage, kills, or forces movement (a teleport/transfer). A
// spell whose worst outcome is benign (an alignment shift, a flavor message, a
// monster summon, quest-item placement) is NOT a hazard even if it ships a
// `failitem` "counter": gating movement on it is wrong (blackwood forest, the class
// quest-item rooms, the area triggers). Confirmed with the user: a summon is not a
// hazard. Harm is detected by walking the effect chain for a Damage/EndCast ability
// or a `cast`/`teleport`/`transfer`/`checkspell`/`failspell` textblock directive.
//
// A harmful room-entry spell is a *protectable* hazard when the 1.11p data also
// ships an item that counters it. Three protection shapes are decoded, all reached
// by walking the spell's Abil-0..9 chain:
//   • Damage (Abil 1) / EndCast (Abil 151) death-timer chain — countered by an
//     item whose NegateSpell-0..9 cancels the spell or ANY member of its EndCast
//     chain (the gnomish fish-helm negates the black-water / freeze-drown chain).
//   • TextBlock (Abil 148) → TBInfo whose Action leads with `failitem <item#>` —
//     holding any listed item aborts the harmful chain (Silver River's rafts).
//   • TextBlock → TBInfo with `checkspell <spell#>` — a buff gate; safe while the
//     buff is active, which the player gets by carrying+using an item that casts
//     it (the desert's waterskin).
// Spells that harm but ship no counter item are deliberately NOT indexed:
// routing around survivable terrain would strand legitimate paths, and the
// router only ever asks about a room it could actually make safe. (Lava / magma
// heat is NOT such a case — spell 526 is countered by the magma amulet or
// phoenix feather and is indexed like any other protectable hazard.)
//
// Mirrors ShopStockIndex / MonsterDropIndex: subscribes to
// GameDataCache.ActiveSetChanged, reads the raw tables it needs once (Rooms for
// the room-entry spell set, Spells for the ability chains, Items for the
// NegateSpell / CastsSp reverse maps, TBInfo for the failitem / checkspell
// directives), builds the map, and evicts every JsonDocument it touched.
public sealed class RoomHazardIndex
{
    // One protectable room-entry hazard: the player is safe iff they carry at
    // least one item from EVERY requirement group. Multiple groups arise only
    // when a single spell layers protections (a damage negator AND a textblock
    // gate); the common case is a single group. Groups are never empty — an
    // uncounterable hazard is simply not indexed.
    // A checkspell counter: an item that protects NOT by being held but by being
    // `use`d, which raises a timed buff (the desert waterskin → buff 711). The
    // provisioner re-`use`s a source item whenever the buff would have lapsed, so
    // it needs the buff's spell number (identity) and its duration in wall-clock
    // seconds (the refresh interval). SourceItems are the items that cast the buff.
    // LapseSpell is the damage spell the room casts when the buff is ABSENT (the
    // desert's "you need water, soon!" spell 712), reached down the checkspell's
    // buff-absent branch; its game-data message is the reactive re-`use` trigger,
    // since the waterskin buff has no wear-off line to time off. 0 when the chain
    // casts nothing (or the target block couldn't be resolved).
    // ImmunityItems are the passive failure-branch guards (the desert sunstone
    // wristband) that make the whole hazard a no-op just by being held/worn — the
    // provisioner skips the `use` entirely when one is carried, since spending a
    // waterskin charge is pointless while a full-immunity guard is in effect.
    public readonly record struct BuffCounter(
        int BuffSpell, int LapseSpell, int DurationSeconds, IReadOnlyList<int> SourceItems,
        IReadOnlyList<int> ImmunityItems);

    public sealed class RoomHazard
    {
        public IReadOnlyList<IReadOnlyList<int>> RequirementGroups { get; }

        // The room's checkspell (buff-gated) counters, if any. Empty for a hazard
        // whose only protection is passive (held negator / failitem). Drives the
        // active provisioner that keeps the buff up while walking the hazard —
        // carrying a source item satisfies the routing gate (via RequirementGroups),
        // but the buff must actually be raised with `use` to survive entry.
        public IReadOnlyList<BuffCounter> BuffCounters { get; }

        // True when the unprotected outcome is survivable damage — a plain damage
        // hit (a Damage ability, or a textblock `cast` of a damage-only spell) with
        // NO death-timer (EndCast), forced relocation (teleport / transfer), or
        // buff-gate drown / heat chain (checkspell / failspell). Only a survivable
        // hazard is safe to offer a "cross unprotected — take the damage" choice
        // for; a grave hazard (drown, freeze-to-death, a teleport into the deep) is
        // never offered that — the crosser can only pass it with a counter in hand.
        public bool IsSurvivableDamage { get; }

        public RoomHazard(
            IReadOnlyList<IReadOnlyList<int>> groups,
            IReadOnlyList<BuffCounter>? buffCounters = null,
            bool isSurvivableDamage = false)
        {
            RequirementGroups = groups;
            BuffCounters = buffCounters ?? Array.Empty<BuffCounter>();
            IsSurvivableDamage = isSurvivableDamage;
        }

        // Every distinct protecting item across all groups — the set the route
        // requirement display and on-demand acquisition draw from.
        public IReadOnlyList<int> ProtectingItems =>
            RequirementGroups.SelectMany(static g => g).Distinct().ToArray();

        // The counters with no in-group alternative: every requirement group that
        // holds a single item. These are the items the route MUST carry (no
        // substitute exists), so they're the only ones safe to hand the
        // each-required path-item acquisition pipeline — an any-of group (2+
        // options) can't be expressed there without acquiring every option, so it
        // stays a manual choice the route picker still surfaces.
        public IReadOnlyList<int> MandatoryItems =>
            RequirementGroups.Where(static g => g.Count == 1)
                .Select(static g => g[0]).Distinct().ToArray();

        // True when the player carries at least one item from every group.
        public bool IsSatisfiedBy(Func<int, bool> carries)
        {
            ArgumentNullException.ThrowIfNull(carries);
            return RequirementGroups.All(g => g.Any(carries));
        }
    }

    private const int SpellAbilSlots = 10;   // Spells: Abil-0..9
    private const int ItemAbilSlots = 20;    // Items:  Abil-0..19
    private const int NegateSlots = 10;      // Items:  NegateSpell-0..9
    private const int AbilDamage = 1;
    private const int AbilCastsSp = 43;
    private const int AbilTextBlock = 148;
    private const int AbilEndCast = 151;
    private const int MaxChainDepth = 16;

    private readonly GameDataCache _cache;
    private readonly LogService? _log;
    private readonly Dictionary<int, RoomHazard> _hazardBySpell = new();

    // Set the index was last built from, or null if empty.
    public string? ActiveSet { get; private set; }

    // Number of distinct protectable room-entry spells indexed.
    public int HazardCount => _hazardBySpell.Count;

    // Fires after every successful (re)load, including the transition to
    // no-set-active.
    public event Action? StoreReloaded;

    public RoomHazardIndex(GameDataCache cache, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _log = log;
    }

    // Hazard for a room's cast-on-enter spell, or null when the spell is benign,
    // unknown, or ships no counter item. Pass Room.Spell.
    public RoomHazard? HazardForSpell(int spell)
        => spell > 0 && _hazardBySpell.TryGetValue(spell, out RoomHazard? h) ? h : null;

    // True when itemId sits in some indexed hazard's any-of counter group and
    // that group is already satisfied by something carried — i.e. a different
    // member of the same group protects just as well, so a pending
    // acquisition/need pinned to itemId specifically is moot. RouteChoicePrompt
    // and the walker's per-step hazard check both resolve to ONE representative
    // item from a multi-item group (whichever the acquisition pipeline could
    // actually source), so a player who instead equips/acquires a different
    // group member never satisfies that one specific id — this lets callers
    // notice the substitute solved it anyway.
    public bool GroupSatisfiedByAlternative(int itemId, Func<int, bool> carries)
    {
        ArgumentNullException.ThrowIfNull(carries);
        foreach (RoomHazard hazard in _hazardBySpell.Values)
            foreach (IReadOnlyList<int> group in hazard.RequirementGroups)
                if (group.Contains(itemId) && group.Any(carries))
                    return true;
        return false;
    }

    // Reload the index for setName. Pass null to clear. Wired by AppServices to
    // GameDataCache.ActiveSetChanged.
    public void OnActiveSetChanged(string? setName)
    {
        _hazardBySpell.Clear();
        ActiveSet = setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _log?.Info("RoomHazardIndex", "No active set; cleared.");
            StoreReloaded?.Invoke();
            return;
        }

        JsonDocument? rooms = _cache.GetRawTable("Rooms");
        JsonDocument? spells = _cache.GetRawTable("Spells");
        if (rooms is null || spells is null)
        {
            _log?.Info("RoomHazardIndex",
                $"Active set '{setName}' missing Rooms/Spells; empty.");
            StoreReloaded?.Invoke();
            return;
        }

        // Only spells that actually appear as a Room.Spell are candidates — this
        // excludes the (damaging) attack-spell table from the harmful scan.
        HashSet<int> roomSpells = CollectRoomSpells(rooms);
        Dictionary<int, (int[] Abil, int[] Val, int TbBase)> spellAbils = ReadSpellAbils(spells);
        Dictionary<int, int> durationSecondsBySpell = ReadSpellDurations(spells);
        Dictionary<int, List<int>> negatorsBySpell = new();
        Dictionary<int, List<int>> castersBySpell = new();
        ReadItemReverseMaps(negatorsBySpell, castersBySpell);
        Dictionary<int, string> tbActions = ReadTbActions();

        foreach (int spell in roomSpells)
        {
            RoomHazard? hazard = BuildHazard(
                spell, spellAbils, negatorsBySpell, castersBySpell, tbActions, durationSecondsBySpell);
            if (hazard is not null) _hazardBySpell[spell] = hazard;
        }

        _cache.EvictTable("Rooms");
        _cache.EvictTable("Spells");
        _cache.EvictTable("Items");
        _cache.EvictTable("TBInfo");

        _log?.Info("RoomHazardIndex",
            $"Indexed {_hazardBySpell.Count} protectable room-entry hazard(s) from '{setName}'.");

        StoreReloaded?.Invoke();
    }

    // Resolve one room-entry spell to a hazard, or null when it neither harms with
    // a counter nor gates on a held item.
    private RoomHazard? BuildHazard(
        int rootSpell,
        Dictionary<int, (int[] Abil, int[] Val, int TbBase)> spellAbils,
        Dictionary<int, List<int>> negatorsBySpell,
        Dictionary<int, List<int>> castersBySpell,
        Dictionary<int, string> tbActions,
        Dictionary<int, int> durationSecondsBySpell)
    {
        HashSet<int> chain = new();
        List<int> textBlocks = new();
        bool damaging = WalkSpellChain(rootSpell, 0, spellAbils, chain, textBlocks);

        // A room-entry spell is a HAZARD only when its unprotected effect actually
        // harms the player: deals damage / kills (a Damage or EndCast-death ability),
        // or its textblock branch leads to a damaging `cast`, a `teleport`/`transfer`
        // that relocates them (a movement hazard), or a `checkspell`/`failspell`
        // buff-gate whose buff-absent branch does the damage (the desert's heat, the
        // drown chain). A spell whose only effects are benign — an alignment shift
        // (addevil), flavor (message), a monster summon, quest-item placement — is NOT
        // a hazard even when it ships a `failitem` "counter": routing around it or
        // demanding the item is wrong. That over-classification gated blackwood forest
        // (summon only), the class quest-item rooms, and the area triggers. Confirmed
        // with the user: a summon on entry is not a hazard.
        bool harmful = damaging || AnyBranchHarmful(textBlocks, tbActions);

        List<IReadOnlyList<int>> groups = new();
        List<BuffCounter> buffCounters = new();

        // Damage / death-timer path: any item negating any chain member protects.
        if (damaging)
        {
            List<int> negators = new();
            foreach (int member in chain)
            {
                if (!negatorsBySpell.TryGetValue(member, out List<int>? items)) continue;
                foreach (int itemId in items)
                    if (!negators.Contains(itemId)) negators.Add(itemId);
            }
            if (negators.Count > 0) groups.Add(negators);
        }

        // TextBlock paths: failitem (hold to abort) and checkspell (carry+use the
        // buff source) each contribute a requirement group; a checkspell block also
        // records the buff's identity + duration for the active provisioner.
        HashSet<int> visitedTb = new();
        foreach (int tb in textBlocks)
            ScanTextBlock(tb, 0, tbActions, castersBySpell, durationSecondsBySpell, visitedTb, groups, buffCounters);

        // Only index a genuinely harmful spell — a benign one that happens to carry a
        // failitem/checkspell counter is not a hazard (see `harmful` above).
        if (!harmful || groups.Count == 0) return null;

        bool survivable = IsSurvivableHazardDamage(rootSpell, spellAbils, tbActions);
        return new RoomHazard(groups, buffCounters, survivable);
    }

    // Classify a room-entry hazard's unprotected outcome as survivable damage or
    // not. Survivable = the harm reaches a Damage ability (directly, or via a
    // textblock `cast` of a damage-carrying spell) AND never reaches a death-timer
    // (EndCast), a forced relocation (teleport / transfer), or a buff-gate
    // drown/heat chain (checkspell / failspell) — any of which can end or displace
    // the crosser rather than just hurt them. Deliberately conservative: an
    // EndCast delayed effect or a checkspell branch is treated as grave even though
    // some are survivable, because "take the damage" must never be offered for a
    // hazard that might kill. Walks the same spell / textblock chains as the
    // counter decode, bounded by depth + visited sets.
    private bool IsSurvivableHazardDamage(
        int rootSpell,
        Dictionary<int, (int[] Abil, int[] Val, int TbBase)> spellAbils,
        Dictionary<int, string> tbActions)
    {
        bool damage = false, grave = false;
        HashSet<int> seenSpell = new();
        HashSet<int> seenTb = new();

        void WalkSpell(int spell, int depth)
        {
            if (depth > MaxChainDepth || spell <= 0 || !seenSpell.Add(spell)) return;
            if (!spellAbils.TryGetValue(spell, out (int[] Abil, int[] Val, int TbBase) ab)) return;
            for (int k = 0; k < SpellAbilSlots; k++)
            {
                int a = ab.Abil[k], v = ab.Val[k];
                if (a == AbilDamage) damage = true;
                else if (a == AbilEndCast && v > 0) { grave = true; WalkSpell(v, depth + 1); }
                else if (a == AbilTextBlock) WalkTb(v > 0 ? v : ab.TbBase, depth + 1);
            }
        }

        void WalkTb(int tb, int depth)
        {
            if (depth > MaxChainDepth || tb <= 0 || !seenTb.Add(tb)) return;
            if (!tbActions.TryGetValue(tb, out string? action) || string.IsNullOrWhiteSpace(action)) return;
            foreach (string line in action.Split('\n'))
                foreach (string raw in line.Split(':'))
                {
                    string tok = raw.Trim();
                    if (tok.Length == 0) continue;
                    if (StartsWith(tok, "cast")) WalkSpell(FirstIntAfter(tok, "cast"), depth + 1);
                    else if (StartsWith(tok, "teleport") || StartsWith(tok, "transfer")
                        || StartsWith(tok, "checkspell") || StartsWith(tok, "failspell")) grave = true;
                    else
                        foreach (string flow in BranchFlowDirectives)
                            if (StartsWith(tok, flow)) WalkTb(FirstIntAfter(tok, flow), depth + 1);
                }
        }

        WalkSpell(rootSpell, 0);
        return damage && !grave;
    }

    // Directives in a textblock branch that mean the unprotected outcome harms the
    // player: a spell CAST (a damaging outcome), a TELEPORT/TRANSFER that relocates
    // them (a movement hazard), or a CHECKSPELL/FAILSPELL buff-gate whose buff-absent
    // branch deals the damage (desert heat / drowning). Benign directives — addevil,
    // message, summon, nomonsters, takeitem, quest flags — are not here, so a branch
    // built only from those reads as non-harmful.
    private static readonly string[] HarmfulTbDirectives =
        { "cast", "teleport", "transfer", "checkspell", "failspell" };

    // Control-flow directives whose target block continues the same branch.
    private static readonly string[] BranchFlowDirectives =
        { "random", "linkto", "link", "goto" };

    // True when any textblock reachable from these roots contains a harmful directive.
    // Follows the random/link control flow, bounded by depth + a visited set.
    private bool AnyBranchHarmful(IReadOnlyList<int> roots, Dictionary<int, string> tbActions)
    {
        HashSet<int> visited = new();
        foreach (int tb in roots)
            if (BranchHarmful(tb, 0, tbActions, visited)) return true;
        return false;
    }

    private bool BranchHarmful(int tb, int depth, Dictionary<int, string> tbActions, HashSet<int> visited)
    {
        if (depth > MaxChainDepth || tb <= 0 || !visited.Add(tb)) return false;
        if (!tbActions.TryGetValue(tb, out string? action) || string.IsNullOrWhiteSpace(action))
            return false;

        foreach (string line in action.Split('\n'))
            foreach (string raw in line.Split(':'))
            {
                string tok = raw.Trim();
                if (tok.Length == 0) continue;
                foreach (string kw in HarmfulTbDirectives)
                    if (StartsWith(tok, kw)) return true;
                foreach (string flow in BranchFlowDirectives)
                    if (StartsWith(tok, flow)
                        && BranchHarmful(FirstIntAfter(tok, flow), depth + 1, tbActions, visited))
                        return true;
            }
        return false;
    }

    // Depth-first walk of a spell's EndCast chain. Returns true when any chain
    // member deals damage; appends every TextBlock (Abil 148) target it reaches.
    private bool WalkSpellChain(
        int spell, int depth,
        Dictionary<int, (int[] Abil, int[] Val, int TbBase)> spellAbils,
        HashSet<int> chain, List<int> textBlocks)
    {
        if (depth > MaxChainDepth || spell <= 0 || !chain.Add(spell)) return false;
        if (!spellAbils.TryGetValue(spell, out (int[] Abil, int[] Val, int TbBase) ab)) return false;

        bool damaging = false;
        for (int k = 0; k < SpellAbilSlots; k++)
        {
            int a = ab.Abil[k];
            int v = ab.Val[k];
            if (a == AbilDamage) damaging = true;
            else if (a == AbilEndCast && v > 0)
                damaging |= WalkSpellChain(v, depth + 1, spellAbils, chain, textBlocks);
            else if (a == AbilTextBlock)
            {
                // AbilVal names the TBInfo block for most TextBlock spells; when
                // it's 0 the block number lives in MinBase/MaxBase (ab.TbBase) —
                // see ReadSpellAbils. A base that isn't a real TB simply resolves
                // to nothing in ScanTextBlock, so the fallback is safe.
                int tb = v > 0 ? v : ab.TbBase;
                if (tb > 0 && !textBlocks.Contains(tb)) textBlocks.Add(tb);
            }
        }
        return damaging;
    }

    // Scan one TBInfo Action for the two item-gate directives. failitem items form
    // one group; checkspell buff-source items form another. Forwards through an
    // empty Action's LinkTo (dialogue-only blocks) but does not chase the failure
    // branches — those describe what happens WITHOUT protection, not a source of it.
    private void ScanTextBlock(
        int tb, int depth,
        Dictionary<int, string> tbActions,
        Dictionary<int, List<int>> castersBySpell,
        Dictionary<int, int> durationSecondsBySpell,
        HashSet<int> visited, List<IReadOnlyList<int>> groups,
        List<BuffCounter> buffCounters)
    {
        if (depth > MaxChainDepth || tb <= 0 || !visited.Add(tb)) return;
        if (!tbActions.TryGetValue(tb, out string? action) || string.IsNullOrWhiteSpace(action))
            return;

        List<int> failItems = new();
        List<int> checkspellCasters = new();
        List<int> randomTargets = new();
        int buffSpellSeen = 0;
        int lapseSpellSeen = 0;

        foreach (string line in action.Split('\n'))
        {
            foreach (string raw in line.Split(':'))
            {
                string tok = raw.Trim();
                if (tok.Length == 0) continue;
                if (StartsWith(tok, "failitem"))
                {
                    int itemId = FirstIntAfter(tok, "failitem");
                    if (itemId > 0 && !failItems.Contains(itemId)) failItems.Add(itemId);
                }
                else if (StartsWith(tok, "random"))
                {
                    // A buff-gate's damage/teleport outcome table is reached via
                    // `random <block>`; remember the target so we can chase it for a
                    // full-immunity guard item below (see the failure-branch scan).
                    int target = FirstIntAfter(tok, "random");
                    if (target > 0 && !randomTargets.Contains(target)) randomTargets.Add(target);
                }
                else if (StartsWith(tok, "checkspell") || StartsWith(tok, "failspell"))
                {
                    // Both gate the room on the player holding a buff. `checkspell`
                    // branches to a damage block when the buff is ABSENT; `failspell`
                    // fires its damage directly when the buff is absent (the Scorching
                    // Desert's `failspell 711` — no waterskin buff up, heat damage,
                    // user-confirmed). Either way the counter is identical: carry an
                    // item that casts the buff and keep it raised. Both are guarded on
                    // an item actually casting the buff, so a `failspell` on a spell no
                    // carried item raises (e.g. TB 2687's `failspell 734`) is ignored.
                    string kw = StartsWith(tok, "checkspell") ? "checkspell" : "failspell";
                    int buffSpell = FirstIntAfter(tok, kw);
                    if (buffSpell > 0 && castersBySpell.TryGetValue(buffSpell, out List<int>? casters))
                    {
                        buffSpellSeen = buffSpell;
                        // The token's second int is the TB the room jumps to when
                        // the buff is ABSENT; that block's `cast <spell>` is the
                        // lapse-damage spell whose message re-triggers the `use`.
                        if (lapseSpellSeen == 0)
                            lapseSpellSeen = FindCastSpell(SecondIntAfter(tok, kw), tbActions);
                        foreach (int itemId in casters)
                            if (!checkspellCasters.Contains(itemId)) checkspellCasters.Add(itemId);
                    }
                }
            }
        }

        if (failItems.Count > 0) groups.Add(failItems);
        if (checkspellCasters.Count > 0)
        {
            // A buff-gate room's failure branch (the `random`-linked outcome table
            // where the damage/teleport fires without the buff) can itself be guarded
            // by `failitem <item>` — hold that item and the whole interaction is
            // skipped, making it a FULL alternative to the buff. The Scorching Desert's
            // sunstone wristband (failitem 1180, one or two random hops below the
            // failspell) works this way: carry it and no waterskin is needed
            // (user-confirmed — the waterskin only stops the damage, the sunstone stops
            // the damage AND the random teleport). Fold such guards into the SAME
            // requirement group as the buff source so carrying EITHER clears the route.
            List<int> immunityItems = new();
            if (buffSpellSeen > 0 && randomTargets.Count > 0)
            {
                HashSet<int> branchVisited = new();
                foreach (int target in randomTargets)
                    CollectFailureBranchGuards(target, 0, tbActions, branchVisited, immunityItems);
            }

            List<int> group = new(checkspellCasters);
            foreach (int item in immunityItems)
                if (!group.Contains(item)) group.Add(item);
            groups.Add(group);

            durationSecondsBySpell.TryGetValue(buffSpellSeen, out int durSec);
            // Only the buff-SOURCE items drive the use-the-item provisioner; the
            // immunity guards are passive (worn) and route-satisfy without a `use` —
            // but the provisioner still needs to KNOW them, so it can skip the `use`
            // when the player already holds one (a worn sunstone makes the waterskin
            // swig pointless).
            buffCounters.Add(new BuffCounter(
                buffSpellSeen, lapseSpellSeen, durSec, checkspellCasters, immunityItems));
        }
    }

    // Chase a buff-gate's failure branch — the `random`-linked outcome blocks where
    // the damage / teleport fires when the buff is absent — for `failitem <item>`
    // guards. Holding such an item skips that outcome, so it's a full alternative to
    // the buff (the desert sunstone wristband). Follows `random <block>` links only
    // (how the desert's guard is reached: 2653→2655→2700, 2658→2660), bounded by
    // depth + a visited set so a cyclic table can't loop.
    private void CollectFailureBranchGuards(
        int tb, int depth, Dictionary<int, string> tbActions,
        HashSet<int> visited, List<int> into)
    {
        if (depth > MaxChainDepth || tb <= 0 || !visited.Add(tb)) return;
        if (!tbActions.TryGetValue(tb, out string? action) || string.IsNullOrWhiteSpace(action))
            return;

        foreach (string line in action.Split('\n'))
            foreach (string raw in line.Split(':'))
            {
                string tok = raw.Trim();
                if (StartsWith(tok, "failitem"))
                {
                    int itemId = FirstIntAfter(tok, "failitem");
                    if (itemId > 0 && !into.Contains(itemId)) into.Add(itemId);
                }
                else if (StartsWith(tok, "random"))
                    CollectFailureBranchGuards(FirstIntAfter(tok, "random"), depth + 1, tbActions, visited, into);
            }
    }

    // The first `cast <spell>` directive inside a TB action, or 0. Used to reach
    // the checkspell buff-absent branch's damage cast (the desert's spell 712) —
    // that block isn't an Abil-148 target the room reaches on its own, so it's
    // looked up directly rather than via the textblock scan.
    private static int FindCastSpell(int tb, Dictionary<int, string> tbActions)
    {
        if (tb <= 0 || !tbActions.TryGetValue(tb, out string? action)
            || string.IsNullOrWhiteSpace(action))
            return 0;

        foreach (string line in action.Split('\n'))
            foreach (string raw in line.Split(':'))
            {
                string tok = raw.Trim();
                if (StartsWith(tok, "cast"))
                {
                    int spell = FirstIntAfter(tok, "cast");
                    if (spell > 0) return spell;
                }
            }
        return 0;
    }

    private static HashSet<int> CollectRoomSpells(JsonDocument rooms)
    {
        HashSet<int> set = new();
        foreach (JsonElement row in rooms.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (TryReadInt(row, "Spell", out int spell) && spell > 0) set.Add(spell);
        }
        return set;
    }

    private static Dictionary<int, (int[] Abil, int[] Val, int TbBase)> ReadSpellAbils(JsonDocument spells)
    {
        Dictionary<int, (int[], int[], int)> map = new();
        foreach (JsonElement row in spells.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int number) || number <= 0) continue;

            int[] abil = new int[SpellAbilSlots];
            int[] val = new int[SpellAbilSlots];
            for (int k = 0; k < SpellAbilSlots; k++)
            {
                TryReadInt(row, $"Abil-{k}", out abil[k]);
                TryReadInt(row, $"AbilVal-{k}", out val[k]);
            }

            // A TextBlock spell (Abil 148) names its TBInfo block in AbilVal for
            // most spells, but a large class of room-entry hazards (the ice
            // cavern's rope+grapple check, blackwood, graveyard, the highlands /
            // farms, ...) leave AbilVal 0 and stash the block number in the spell's
            // MinBase/MaxBase instead. Capture that base so WalkSpellChain can fall
            // back to it — otherwise those hazards' failitem / checkspell counters
            // are never scanned and the router can't offer their protection.
            TryReadInt(row, "MinBase", out int minBase);
            TryReadInt(row, "MaxBase", out int maxBase);
            map[number] = (abil, val, minBase > 0 ? minBase : maxBase);
        }
        return map;
    }

    // Base wall-clock duration (seconds) of every spell, keyed by number. Computed
    // via the same SpellCalculator the rest of the app uses so the buff-refresh
    // clock matches how buff durations are reckoned elsewhere. Evaluated at each
    // spell's own ReqLevel — a level-independent baseline: when DurInc scales the
    // duration up with level, the real in-play buff lasts at least this long, so
    // using it as the refresh interval never under-covers (it only re-uses a
    // touch early). 0 when the spell confers no timed effect.
    private static Dictionary<int, int> ReadSpellDurations(JsonDocument spells)
    {
        Dictionary<int, int> map = new();
        foreach (JsonElement row in spells.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int number) || number <= 0) continue;

            TryReadInt(row, "Dur", out int dur);
            if (dur <= 0) continue;   // no timed effect — nothing to refresh
            TryReadInt(row, "DurInc", out int durInc);
            TryReadInt(row, "DurIncLVLs", out int durIncLvls);
            TryReadInt(row, "Cap", out int cap);
            TryReadInt(row, "ReqLevel", out int reqLevel);

            SpellFormulaInput formula = new()
            {
                Number = number,
                Dur = dur,
                DurInc = durInc,
                DurIncLVLs = durIncLvls,
                Cap = cap,
                ReqLevel = reqLevel,
            };
            long rounds = SpellCalculator.Duration(formula, reqLevel);
            if (rounds > 0) map[number] = (int)(rounds * SpellCalculator.SpellRoundSeconds);
        }
        return map;
    }

    private void ReadItemReverseMaps(
        Dictionary<int, List<int>> negatorsBySpell,
        Dictionary<int, List<int>> castersBySpell)
    {
        JsonDocument? items = _cache.GetRawTable("Items");
        if (items is null) return;

        foreach (JsonElement row in items.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int itemId) || itemId <= 0) continue;

            for (int k = 0; k < NegateSlots; k++)
            {
                if (TryReadInt(row, $"NegateSpell-{k}", out int spell) && spell > 0)
                    Add(negatorsBySpell, spell, itemId);
            }
            for (int k = 0; k < ItemAbilSlots; k++)
            {
                if (TryReadInt(row, $"Abil-{k}", out int a) && a == AbilCastsSp
                    && TryReadInt(row, $"AbilVal-{k}", out int spell) && spell > 0)
                    Add(castersBySpell, spell, itemId);
            }
        }
    }

    private Dictionary<int, string> ReadTbActions()
    {
        Dictionary<int, string> map = new();
        JsonDocument? tb = _cache.GetRawTable("TBInfo");
        if (tb is null) return map;

        foreach (JsonElement row in tb.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadInt(row, "Number", out int number) || number <= 0) continue;
            if (row.TryGetProperty("Action", out JsonElement el)
                && el.ValueKind == JsonValueKind.String)
            {
                string? action = el.GetString();
                if (!string.IsNullOrEmpty(action)) map[number] = action;
            }
        }
        return map;
    }

    private static void Add(Dictionary<int, List<int>> map, int key, int value)
    {
        if (!map.TryGetValue(key, out List<int>? list)) map[key] = list = new List<int>();
        if (!list.Contains(value)) list.Add(value);
    }

    private static bool StartsWith(string tok, string keyword)
        => tok.StartsWith(keyword, StringComparison.OrdinalIgnoreCase);

    // First contiguous integer following keyword in tok (VB-val style: skip the
    // keyword and any spaces, read the digit run). 0 when none.
    private static int FirstIntAfter(string tok, string keyword)
    {
        int idx = tok.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        int i = idx + keyword.Length;
        while (i < tok.Length && !char.IsDigit(tok[i])) i++;
        int start = i;
        while (i < tok.Length && char.IsDigit(tok[i])) i++;
        return i > start
            ? int.Parse(tok.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture)
            : 0;
    }

    // Second contiguous integer following keyword in tok (skip keyword, the first
    // digit-run, then read the next). 0 when the token holds fewer than two ints —
    // e.g. a bare `checkspell 300` with no jump target.
    private static int SecondIntAfter(string tok, string keyword)
    {
        int idx = tok.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        int i = idx + keyword.Length;
        while (i < tok.Length && !char.IsDigit(tok[i])) i++;   // to first int
        while (i < tok.Length && char.IsDigit(tok[i])) i++;    // past first int
        while (i < tok.Length && !char.IsDigit(tok[i])) i++;   // to second int
        int start = i;
        while (i < tok.Length && char.IsDigit(tok[i])) i++;
        return i > start
            ? int.Parse(tok.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture)
            : 0;
    }

    private static bool TryReadInt(JsonElement row, string property, out int value)
    {
        value = 0;
        return row.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
