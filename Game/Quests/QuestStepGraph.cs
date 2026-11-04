using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Drafts an ordered, followable step checklist for a single quest flag by walking
// the active set's TBInfo chains — the auto baseline behind QuestDefinition.Steps
// that a user then refines. Stateless and recomputed per call, matching
// QuestCrawler's scan.
//
// Every chain that terminally advances the flag (its last giveability names flag)
// becomes one QuestStep, ordered by the chain's give-step — the quest's own progress
// counter. The crawl surfaces the player command, the location, and the items the
// step checks / takes / gives; identical steps (the same chain echoed from multiple
// rooms) collapse to one entry. A chain that checks the flag and hands an item over
// without changing the flag is a gather step (QuestStep.Gathers), drafted ahead of
// the step that takes the item.
public static class QuestStepGraph
{
    // Build the ordered step draft for flag in the active set. Empty when no set is
    // active, TBInfo is missing, or the flag has no chains. Steps are de-duplicated
    // and ordered by give-step, then location.
    //
    // byAbilityValue selects the progress axis. Default (false): a chain is a step
    // only when its terminal giveability advances the flag, and the give-step orders
    // it (single-part and give-step-laddered quests). true: a chain is a step when it
    // advances or gates the flag via any give/add/check/test directive, ordered by
    // the ability value it lands on — the shape of an addability-advanced quest
    // (MageBane) whose later tiers carry no giveability of their own.
    //
    // Built once per game-data set for each flag and axis (GameDataCache.Derived): it
    // walks every TBInfo block, and the Quest tab asks for each of its quests every
    // time the Workshop opens.
    public static IReadOnlyList<QuestStep> Build(GameDataCache cache, int flag, bool byAbilityValue = false)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return cache.Derived(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"quest steps {flag} {byAbilityValue}"),
            () => BuildSteps(cache, flag, byAbilityValue));
    }

    private static IReadOnlyList<QuestStep> BuildSteps(GameDataCache cache, int flag, bool byAbilityValue)
    {
        GameTable? tbinfo = cache.GetTable("TBInfo");
        if (tbinfo is null) return Array.Empty<QuestStep>();

        var steps = new List<QuestStep>();
        var gathers = new List<QuestStep>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // A chain is a step only through a directive whose first number is the flag,
        // so a chain that doesn't hold the flag's digits anywhere is none, and neither
        // is any chain of a block that doesn't. Both are passed over before anything
        // is made of them (the block's script is searched as stored, not as a
        // string): a draft is built for every quest in turn, each walking every block
        // of the set, and nearly none of those blocks is about the quest in hand.
        string flagDigits = Math.Abs((long)flag).ToString(System.Globalization.CultureInfo.InvariantCulture);
        byte[] flagDigitsUtf8 = System.Text.Encoding.ASCII.GetBytes(flagDigits);

        foreach (GameRow block in tbinfo.Rows)
        {
            if (!block.TryGetProperty("Action", out GameValue actionEl)) continue;
            if (actionEl.ValueKind != JsonValueKind.String) continue;
            if (!actionEl.ValueContains(flagDigitsUtf8)) continue;
            string? action = actionEl.GetString();
            if (string.IsNullOrEmpty(action) || action == "\0") continue;

            string? location = ReadLocation(block);

            foreach (string raw in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!raw.Contains(flagDigits, StringComparison.Ordinal)) continue;
                QuestStep? step = byAbilityValue ? BuildValueStep(raw, flag, location) : BuildStep(raw, flag, location);
                if (step is null)
                {
                    // The value axis already takes every line that gates on the flag.
                    if (!byAbilityValue && BuildGatherStep(raw, flag, location) is { } gather)
                        gathers.Add(ResolveAsk(gather, cache, tbinfo));
                    continue;
                }
                step = ResolveAsk(step, cache, tbinfo);
                if (seen.Add(CanonicalKey(step))) steps.Add(step);
            }
        }

        // A gather line joins the draft only when a step of the quest asks for what it
        // hands over, and it goes just ahead of the first such step past its gate: the
        // Conquest hands come off the four chieftains while the flag is at 1, and the
        // turn-in that moves it to 2 takes them.
        foreach (QuestStep gather in gathers)
        {
            QuestStep? consumer = steps
                .Where(s => s.Order > gather.Order && gather.GrantedItems.Any(i => s.TurnInItems.Contains(i) || s.RequiredItems.Contains(i)))
                .OrderBy(s => s.Order)
                .FirstOrDefault();
            if (consumer is null) continue;
            QuestStep placed = gather with { Order = consumer.Order };
            if (seen.Add(CanonicalKey(placed))) steps.Add(placed);
        }

        return steps
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Gathers ? 0 : 1)
            .ThenBy(s => s.Location, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Parse one chain into a step when its terminal (last) giveability advances
    // `flag`; else null. The give-step of that grant orders the step.
    private static QuestStep? BuildStep(string raw, int flag, string? location)
    {
        string[] segments = raw.Split(':');
        if (segments.Length == 0) return null;

        int? terminalFlag = null;
        int terminalStep = 0;
        var requiredItems = new List<int>();
        var turnInItems = new List<int>();
        var grantedItems = new List<int>();

        foreach (string segment in segments)
        {
            string[] p = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;

            switch (p[0].ToLowerInvariant())
            {
                case "giveability" when p.Length >= 3
                    && int.TryParse(p[1], out int gf) && int.TryParse(p[2], out int gs):
                    // Last grant wins: the chain belongs to the flag it terminally
                    // advances, matching the crawler's identity rule.
                    terminalFlag = gf; terminalStep = gs;
                    break;
                case "checkitem" when p.Length >= 2 && int.TryParse(p[1], out int ci):
                    if (!requiredItems.Contains(ci)) requiredItems.Add(ci);
                    break;
                case "takeitem" when p.Length >= 2 && int.TryParse(p[1], out int ti):
                    if (!turnInItems.Contains(ti)) turnInItems.Add(ti);
                    break;
                case "giveitem" when p.Length >= 2 && int.TryParse(p[1], out int gi):
                    if (!grantedItems.Contains(gi)) grantedItems.Add(gi);
                    break;
            }
        }

        if (terminalFlag != flag) return null;

        return new QuestStep(terminalStep, ReadCommand(segments), location, requiredItems, turnInItems, grantedItems);
    }

    // Value-axis variant: a chain is a step when it touches `flag` via any give/add/
    // check/test ability directive, ordered by the ability value it lands on
    // (QuestCrawler.AbilityValueOf — shared so the draft and the crawl band agree). A
    // "have the flag at all" gate (value 0) folds into band 1, matching the crawler.
    private static QuestStep? BuildValueStep(string raw, int flag, string? location)
    {
        string[] segments = raw.Split(':');
        if (segments.Length == 0) return null;

        bool touches = false;
        var requiredItems = new List<int>();
        var turnInItems = new List<int>();
        var grantedItems = new List<int>();

        foreach (string segment in segments)
        {
            string[] p = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;

            switch (p[0].ToLowerInvariant())
            {
                case "giveability" or "addability" or "checkability" or "testability"
                    when p.Length >= 3 && int.TryParse(p[1], out int af) && af == flag:
                    touches = true;
                    break;
                case "checkitem" when p.Length >= 2 && int.TryParse(p[1], out int ci):
                    if (!requiredItems.Contains(ci)) requiredItems.Add(ci);
                    break;
                case "takeitem" when p.Length >= 2 && int.TryParse(p[1], out int ti):
                    if (!turnInItems.Contains(ti)) turnInItems.Add(ti);
                    break;
                case "giveitem" when p.Length >= 2 && int.TryParse(p[1], out int gi):
                    if (!grantedItems.Contains(gi)) grantedItems.Add(gi);
                    break;
            }
        }

        if (!touches) return null;

        int value = QuestCrawler.AbilityValueOf(raw, flag);
        return new QuestStep(value <= 0 ? 1 : value, ReadCommand(segments), location, requiredItems, turnInItems, grantedItems);
    }

    // A line that hands over an item for the quest, leaving the flag alone: it checks
    // the flag and gives an item, no step of it gives, adds to or clears the flag, and it
    // checks no other flag.
    // The check is checkability / testability while the quest is under way, or
    // failability before it starts (`failability 130:failitem 3867:giveitem 3867`, the
    // chalice Blood Champ's hand-in takes). Its Order is, for now, the value the check
    // asks for (the checkability floor, else the testability ceiling, else 0 for not
    // holding the flag), which the caller places the line after. Null for any other line.
    private static QuestStep? BuildGatherStep(string raw, int flag, string? location)
    {
        string[] segments = raw.Split(':');
        if (segments.Length == 0) return null;

        int? floor = null, ceiling = null;
        bool without = false;
        var requiredItems = new List<int>();
        var turnInItems = new List<int>();
        var grantedItems = new List<int>();
        foreach (string segment in segments)
        {
            string[] p = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            bool onFlag = p.Length >= 2 && int.TryParse(p[1], out int af) && af == flag;
            int value = p.Length >= 3 && int.TryParse(p[2], out int v) ? v : 0;
            switch (p[0].ToLowerInvariant())
            {
                case "giveability" or "addability" or "removeability" when onFlag:
                    return null;
                case "checkability" when onFlag && p.Length >= 3:
                    floor = floor is int f ? Math.Max(f, value) : value;
                    break;
                case "testability" when onFlag && p.Length >= 3:
                    ceiling = ceiling is int c ? Math.Min(c, value) : value;
                    break;
                case "failability" when onFlag:
                    without = true;
                    break;
                // A line that also asks for another flag's progress belongs to that
                // quest; its give is gated on it, not on this one.
                case "checkability" or "testability":
                    return null;
                case "checkitem" when p.Length >= 2 && int.TryParse(p[1], out int ci):
                    if (!requiredItems.Contains(ci)) requiredItems.Add(ci);
                    break;
                case "takeitem" when p.Length >= 2 && int.TryParse(p[1], out int ti):
                    if (!turnInItems.Contains(ti)) turnInItems.Add(ti);
                    break;
                case "giveitem" when p.Length >= 2 && int.TryParse(p[1], out int gi):
                    if (!grantedItems.Contains(gi)) grantedItems.Add(gi);
                    break;
            }
        }

        if (grantedItems.Count == 0 || (floor ?? ceiling ?? (without ? 0 : null)) is not int gate) return null;
        return new QuestStep(gate, ReadCommand(segments), location, requiredItems, turnInItems, grantedItems, Gathers: true);
    }

    // The first segment is a player command only when it doesn't lead with a known
    // guard/effect directive — otherwise the chain is reached by dialogue branch. A
    // leading number is a line of a `random` roll table (the roll runs the first line
    // whose number is above it), so it isn't typed either.
    internal static string? ReadCommand(string[] segments)
    {
        string first = segments[0].Trim();
        if (first.Length == 0) return null;
        if (int.TryParse(first, out _)) return null;
        string kw = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        return DirectiveKeywords.Contains(kw) ? null : first;
    }

    private static string? ReadLocation(GameRow block)
    {
        if (!block.TryGetProperty("Called From", out GameValue cf)) return null;
        if (cf.ValueKind != JsonValueKind.String) return null;
        string? s = cf.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // Enrich a guard-led step anchored on a textblock with the player command that
    // reaches it: an "ask <npc> <keyword>" recovered by walking the Called-From chain
    // up to the NPC whose dialogue keyword branches into the step, re-anchoring the
    // step's location on that NPC so the guide links its room. This is how the crawl
    // recovers the real quest flow — a MajorMUD quest advances through NPC dialogue
    // (ask keyword → textblock → giveability), not standalone room commands. Verb-led
    // steps, and steps rooted on a room / monster / spell rather than a textblock, are
    // left untouched.
    private static QuestStep ResolveAsk(QuestStep step, GameDataCache cache, GameTable tbinfo)
    {
        if (step.Command is not null) return step;
        if (ParseTextblockRef(step.Location) is null) return step;

        var hit = ResolveAskSource(step.Location, tbinfo);
        if (hit is null) return step;

        (int monster, string keyword) = hit.Value;
        string npc = cache.FindNameByNumber("Monsters", monster)
            ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"monster #{monster}");
        return step with
        {
            Command = $"ask {npc.ToLowerInvariant()} {keyword}",
            Location = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Monster #{monster}"),
        };
    }

    // Walk a step's Called-From textblock chain up to the NPC that dispatches into it,
    // returning that NPC's monster number and the ask-keyword whose branch reaches the
    // step. Null when the chain doesn't root at an askable NPC keyword — an auto-shown
    // greeting textblock, a spell/room root, the NPC being the step's direct parent
    // (no intermediate textblock names the keyword), or an unresolvable ref.
    //
    // A block is looked up by its textblock number in the table's own index (first
    // row wins — a number is unique in practice), which every quest's draft shares.
    private static (int Monster, string Keyword)? ResolveAskSource(
        string? stepCalledFrom, GameTable tbinfo)
    {
        int? cur = ParseTextblockRef(stepCalledFrom);
        if (cur is null) return null;

        int child = -1;
        var seen = new HashSet<int>();
        int guard = 0;
        while (cur is int c && seen.Add(c) && guard++ < 64)
        {
            if (tbinfo.FindByNumber(c) is not { } block) return null;
            string? parent = ReadStringProp(block, "Called From");

            if (ParseMonsterRef(parent) is int monster)
            {
                if (child <= 0) return null;   // NPC root is the step's direct parent —
                                               // no intermediate textblock to key on.
                string? kw = FindDispatchKeywords(ReadStringProp(block, "Action"), child).FirstOrDefault();
                if (kw is null || IsAutoShownKeyword(kw)) return null;
                return (monster, kw);
            }

            int? parentTb = ParseTextblockRef(parent);
            if (parentTb is null) return null;   // parent is a spell / room / nothing.
            child = c;
            cur = parentTb;
        }
        return null;
    }

    // In an NPC root block's keyword-dispatch action ("crystal:7018\nreturn:7020"),
    // the keywords whose branch targets `textblock`, in the block's order (an NPC often
    // lists several words for one reply); empty when none maps to it.
    internal static List<string> FindDispatchKeywords(string? dispatch, int textblock)
    {
        var keywords = new List<string>();
        if (string.IsNullOrEmpty(dispatch)) return keywords;
        foreach (string line in dispatch.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(':');
            if (parts.Length < 2) continue;
            string kw = parts[0].Trim();
            if (kw.Length == 0) continue;
            for (int i = 1; i < parts.Length; i++)
            {
                string tok = parts[i].Trim();
                int sp = tok.IndexOf(' ');
                if (sp >= 0) tok = tok[..sp];
                if (int.TryParse(tok, out int n) && n == textblock)
                {
                    keywords.Add(kw);
                    break;
                }
            }
        }
        return keywords;
    }

    private static string? ReadStringProp(GameRow block, string prop)
        => block.TryGetProperty(prop, out GameValue el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int? ParseTextblockRef(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        Match m = TextblockRefRe.Match(s);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : null;
    }

    private static int? ParseMonsterRef(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        Match m = MonsterRefRe.Match(s);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : null;
    }

    // "Textblock #7018" / "Textblock(rndm) #7018" and "Monster #1266" refs inside a
    // Called-From string.
    private static readonly Regex TextblockRefRe =
        new(@"Textblock[^#]*#(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MonsterRefRe =
        new(@"Monster\s*#(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Auto-shown dialogue blocks the player never types a keyword to reach — a step
    // gated behind one of these has no "ask" command, so it isn't drafted as one.
    // `message` is typed like any other keyword: the ethereal figure's `ask ethereal
    // message` is a step of the Mandos quest.
    private static readonly HashSet<string> GreetingKeywords =
        new(StringComparer.OrdinalIgnoreCase) { "text", "greeting" };

    internal static bool IsAutoShownKeyword(string keyword) => GreetingKeywords.Contains(keyword);

    // Stable structural fingerprint so the same step echoed from many rooms folds
    // to one entry (record equality is reference-based for the list fields).
    private static string CanonicalKey(QuestStep s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Order).Append('|').Append(s.Gathers).Append('|').Append(s.Command).Append('|').Append(s.Location).Append('|');
        sb.AppendJoin(',', s.RequiredItems).Append('|');
        sb.AppendJoin(',', s.TurnInItems).Append('|');
        sb.AppendJoin(',', s.GrantedItems);
        return sb.ToString();
    }

    // Guard / effect directive keywords — a chain that leads with one of these is
    // reached by dialogue branch, so its first segment is not a player command.
    private static readonly HashSet<string> DirectiveKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "message", "text", "takeitem", "giveitem", "checkitem", "roomitem", "failitem",
        "clearitem", "failroomitem", "teleport", "cast", "class", "race", "random",
        "failability", "checkability", "testability", "giveability", "addability",
        "removeability", "goodability", "goodaligned", "evilaligned", "neutralaligned",
        "minlevel", "maxlevel", "levelcheck", "price", "buy", "give", "givecoins",
        "summon", "monsters", "nomonsters", "needmonster", "adddelay", "delay",
        "addexp", "addevil", "testskill", "checkspell", "learnspell", "remoteaction",
        "check",
    };
}
