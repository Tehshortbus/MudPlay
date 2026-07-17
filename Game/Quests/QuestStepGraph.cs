using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FujinTerm.Services;

namespace FujinTerm.Game.Quests;

// Drafts an ordered, followable step checklist for a single quest flag by walking
// the active set's TBInfo chains — the auto baseline behind QuestDefinition.Steps
// that a user then refines. Stateless and recomputed per call, matching
// QuestCrawler's scan.
//
// Every chain that terminally advances the flag (its last giveability names flag)
// becomes one QuestStep, ordered by the chain's give-step — the quest's own progress
// counter. The crawl surfaces the player command, the location, and the items the
// step checks / takes / gives; identical steps (the same chain echoed from multiple
// rooms) collapse to one entry.
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
    public static IReadOnlyList<QuestStep> Build(GameDataCache cache, int flag, bool byAbilityValue = false)
    {
        ArgumentNullException.ThrowIfNull(cache);

        JsonDocument? tbinfo = cache.GetRawTable("TBInfo");
        if (tbinfo is null) return Array.Empty<QuestStep>();

        var steps = new List<QuestStep>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Index every block by its textblock number so a guard-led (command-less) step
        // can walk its Called-From chain up to the NPC whose ask-keyword branches to it.
        Dictionary<int, JsonElement> byNumber = IndexByNumber(tbinfo);

        foreach (JsonElement block in tbinfo.RootElement.EnumerateArray())
        {
            if (!block.TryGetProperty("Action", out JsonElement actionEl)) continue;
            if (actionEl.ValueKind != JsonValueKind.String) continue;
            string? action = actionEl.GetString();
            if (string.IsNullOrEmpty(action) || action == "\0") continue;

            string? location = ReadLocation(block);

            foreach (string raw in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                QuestStep? step = byAbilityValue ? BuildValueStep(raw, flag, location) : BuildStep(raw, flag, location);
                if (step is null) continue;
                step = ResolveAsk(step, cache, byNumber);
                if (seen.Add(CanonicalKey(step))) steps.Add(step);
            }
        }

        return steps
            .OrderBy(s => s.Order)
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

    // The first segment is a player command only when it doesn't lead with a known
    // guard/effect directive — otherwise the chain is reached by dialogue branch.
    private static string? ReadCommand(string[] segments)
    {
        string first = segments[0].Trim();
        if (first.Length == 0) return null;
        string kw = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        return DirectiveKeywords.Contains(kw) ? null : first;
    }

    private static string? ReadLocation(JsonElement block)
    {
        if (!block.TryGetProperty("Called From", out JsonElement cf)) return null;
        if (cf.ValueKind != JsonValueKind.String) return null;
        string? s = cf.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // Index blocks by textblock number (first row wins — a number is unique in
    // practice) so ResolveAsk can hop a step's Called-From chain by number.
    private static Dictionary<int, JsonElement> IndexByNumber(JsonDocument tbinfo)
    {
        var map = new Dictionary<int, JsonElement>();
        foreach (JsonElement block in tbinfo.RootElement.EnumerateArray())
        {
            if (block.TryGetProperty("Number", out JsonElement n)
                && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int num))
                map.TryAdd(num, block);
        }
        return map;
    }

    // Enrich a guard-led step anchored on a textblock with the player command that
    // reaches it: an "ask <npc> <keyword>" recovered by walking the Called-From chain
    // up to the NPC whose dialogue keyword branches into the step, re-anchoring the
    // step's location on that NPC so the guide links its room. This is how the crawl
    // recovers the real quest flow — a MajorMUD quest advances through NPC dialogue
    // (ask keyword → textblock → giveability), not standalone room commands. Verb-led
    // steps, and steps rooted on a room / monster / spell rather than a textblock, are
    // left untouched.
    private static QuestStep ResolveAsk(QuestStep step, GameDataCache cache,
        Dictionary<int, JsonElement> byNumber)
    {
        if (step.Command is not null) return step;
        if (ParseTextblockRef(step.Location) is null) return step;

        var hit = ResolveAskSource(step.Location, byNumber);
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
    private static (int Monster, string Keyword)? ResolveAskSource(
        string? stepCalledFrom, Dictionary<int, JsonElement> byNumber)
    {
        int? cur = ParseTextblockRef(stepCalledFrom);
        if (cur is null) return null;

        int child = -1;
        var seen = new HashSet<int>();
        int guard = 0;
        while (cur is int c && seen.Add(c) && guard++ < 64)
        {
            if (!byNumber.TryGetValue(c, out JsonElement block)) return null;
            string? parent = ReadStringProp(block, "Called From");

            if (ParseMonsterRef(parent) is int monster)
            {
                if (child <= 0) return null;   // NPC root is the step's direct parent —
                                               // no intermediate textblock to key on.
                string? kw = FindDispatchKeyword(ReadStringProp(block, "Action"), child);
                if (kw is null || GreetingKeywords.Contains(kw)) return null;
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
    // the keyword whose branch targets `textblock`; null when none maps to it.
    private static string? FindDispatchKeyword(string? dispatch, int textblock)
    {
        if (string.IsNullOrEmpty(dispatch)) return null;
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
                if (int.TryParse(tok, out int n) && n == textblock) return kw;
            }
        }
        return null;
    }

    private static string? ReadStringProp(JsonElement block, string prop)
        => block.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String
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
    private static readonly HashSet<string> GreetingKeywords =
        new(StringComparer.OrdinalIgnoreCase) { "message", "text", "greeting" };

    // Stable structural fingerprint so the same step echoed from many rooms folds
    // to one entry (record equality is reference-based for the list fields).
    private static string CanonicalKey(QuestStep s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Order).Append('|').Append(s.Command).Append('|').Append(s.Location).Append('|');
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
