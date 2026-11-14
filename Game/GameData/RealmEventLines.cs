using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// The lines of a set's Messages table that belong to no spell and that the client acts
// on (GAME_MECHANICS "The export's `Messages` table (Nightmare Redux 1.8.4)"):
//   - a monster's death text, printed to the room when it dies (Effects "Death");
//   - a chase line, printed when someone leaves through a command exit (Effects
//     "Chase", the leaver as {target});
//   - a monster entrance, a monster appearing or changing form (Effects "CheckRoom");
//   - a trap going off, on walking into it or on a failed disarm (Effects "Trap"; the
//     table has both lines of each trapped exit and can't tell them apart);
//   - a failure line, printed when an effect (confusion, sleep, fear, petrification…)
//     makes a command fail (Effects "Fail"). A line that is also an effect's start
//     line is left out (user, 2026-11-14): the line alone can't say which it is;
//   - a monster's hit that prints no damage figure, on us or on someone else
//     (Effects "Monster"). The hit lines with "for N damage" are read by shape
//     already (DamageLineAttributor), so only the damage-free ones are kept here.
// A set exported without the table has none, and these lines stay unknown to it.
public sealed class RealmEventLines
{
    public static RealmEventLines None { get; } = new([], [], [], [], [], [], [], [], "none: the set has no Messages table");

    // Death text → the monster it is the death of; null when the text is several
    // monsters' (the export then names it by its words, not by a monster).
    private readonly Dictionary<string, string?> _deaths;
    private readonly List<CasterMessageMatcher> _chases;
    private readonly HashSet<string> _entrances;
    private readonly HashSet<string> _traps;
    // The few trap lines that name who they hit or the damage.
    private readonly List<CasterMessageMatcher> _trapTemplates;
    // Failure line → the record ConditionTracker raises ActionFailed with.
    private readonly Dictionary<string, MessageRecord> _fails;
    private readonly List<CasterMessageMatcher> _hitsOnUs;
    private readonly List<CasterMessageMatcher> _hitsOnOthers;

    public string Summary { get; }

    private RealmEventLines(Dictionary<string, string?> deaths, List<CasterMessageMatcher> chases,
        HashSet<string> entrances, HashSet<string> traps, List<CasterMessageMatcher> trapTemplates,
        Dictionary<string, MessageRecord> fails, List<CasterMessageMatcher> hitsOnUs,
        List<CasterMessageMatcher> hitsOnOthers, string summary)
    {
        _fails = fails;
        _hitsOnUs = hitsOnUs;
        _hitsOnOthers = hitsOnOthers;
        _deaths = deaths;
        _chases = chases;
        _entrances = entrances;
        _traps = traps;
        _trapTemplates = trapTemplates;
        Summary = summary;
    }

    // The lines of table; isMonster says whether a row's name is a monster of the set.
    public static RealmEventLines From(GameTable? table, Func<string, bool> isMonster)
    {
        ArgumentNullException.ThrowIfNull(isMonster);
        if (table is null) return None;
        if (MdbMessageRow.Unreadable(table) is { } why)
            return new([], [], [], [], [], [], [], [], $"none: the set's Messages table isn't one this client reads ({why})");

        Dictionary<string, string?> deaths = new(StringComparer.Ordinal);
        List<CasterMessageMatcher> chases = [];
        HashSet<string> chaseTemplates = new(StringComparer.Ordinal);
        HashSet<string> entrances = new(StringComparer.Ordinal);
        HashSet<string> traps = new(StringComparer.Ordinal);
        List<CasterMessageMatcher> trapTemplates = [];
        List<MdbMessageRow> rows = MdbMessageRow.Read(table).ToList();
        HashSet<string> startLines = rows.Where(r => r.Kind == "Self" && r.EndMsg.Length > 0 && r.Msg.Length > 0)
            .Select(r => r.Msg).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, MessageRecord> fails = new(StringComparer.Ordinal);
        List<CasterMessageMatcher> hitsOnUs = [];
        List<CasterMessageMatcher> hitsOnOthers = [];
        HashSet<string> hitTemplates = new(StringComparer.Ordinal);
        foreach (MdbMessageRow row in rows)
        {
            if (row.HasEffect("Monster") && row.Spell == 0 && !row.Msg.Contains("{dmg}", StringComparison.Ordinal)
                && hitTemplates.Add(row.Kind + row.Msg)
                && CasterMessageMatcher.TryCreate(row.Msg, compiled: false) is { } hit
                && row.Msg.Contains("{source}", StringComparison.Ordinal))
            {
                if (row.Kind == "Self") hitsOnUs.Add(hit);
                else if (row.Kind == "Other" && hit.PinsTarget) hitsOnOthers.Add(hit);
                continue;
            }
            if (row.Kind == "Self" && row.EndMsg.Length == 0 && row.Msg.Length > 0 && row.HasEffect("Fail")
                && !startLines.Contains(row.Msg))
            {
                fails.TryAdd(row.Msg, new MessageRecord(
                    MessageRecord.ComputeId(row.Name, "", row.Msg, "", "", ""), row.Name,
                    MessageFlags.LastActionFailed, (ushort)MessageFlags.LastActionFailed,
                    CasterMessage: "", TargetMessage: row.Msg, WitnessMessage: "", AppliedMessage: "", AppliedEndsWith: ""));
                continue;
            }
            if (row.Spell != 0 || row.Msg.Length == 0) continue;
            if (row.Kind == "Self" && row.EndMsg.Length == 0 && row.HasEffect("Trap"))
            {
                if (CasterMessageMatcher.TryCreate(row.Msg, compiled: false) is { } template) trapTemplates.Add(template);
                else traps.Add(row.Msg);
                continue;
            }
            if (row.Kind != "Other") continue;
            if (row.HasEffect("Death"))
            {
                string? monster = isMonster(row.Name) ? row.Name : null;
                // The same text under two monsters is no one monster's.
                if (deaths.TryGetValue(row.Msg, out string? seen) && seen != monster) monster = null;
                deaths[row.Msg] = monster;
            }
            else if (row.HasEffect("Chase"))
            {
                if (chaseTemplates.Add(row.Msg) && CasterMessageMatcher.TryCreate(row.Msg, compiled: false) is { PinsTarget: true } m)
                    chases.Add(m);
            }
            else if (row.HasEffect("CheckRoom"))
                entrances.Add(row.Msg);
        }
        int named = deaths.Values.Count(n => n is not null);
        return new(deaths, chases, entrances, traps, trapTemplates, fails, hitsOnUs, hitsOnOthers,
            $"from the set's Messages table: {deaths.Count} death lines ({named} naming their monster), "
            + $"{chases.Count} chase lines, {entrances.Count} monster entrances, {traps.Count + trapTemplates.Count} trap lines, "
            + $"{fails.Count} failure lines, {hitsOnUs.Count + hitsOnOthers.Count} damage-free monster hits");
    }

    // Whether line is a death text, and the monster it names (null when shared).
    public bool IsDeath(string line, out string? monster) => _deaths.TryGetValue(line.Trim(), out monster);

    // Whether line is a chase line, and who it says left.
    public bool IsChase(string line, out string who)
    {
        who = "";
        string text = line.Trim();
        foreach (CasterMessageMatcher m in _chases)
        {
            if (!m.TryMatchCaptures(text, out MessageCaptures captures) || captures.Target is not { Length: > 0 } target)
                continue;
            who = target;
            return true;
        }
        return false;
    }

    public bool IsEntrance(string line) => _entrances.Contains(line.Trim());

    public bool IsTrap(string line)
    {
        string text = line.Trim();
        return _traps.Contains(text) || _trapTemplates.Any(t => t.TryMatch(text, out _));
    }

    // A damage-free monster hit on us: [the monster], or null.
    public IReadOnlyList<string>? HitOnUs(string line)
    {
        string text = line.Trim();
        foreach (CasterMessageMatcher m in _hitsOnUs)
            if (m.TryMatchCaptures(text, out MessageCaptures c) && Bare(c.Source) is { } monster)
                return [monster];
        return null;
    }

    // A damage-free monster hit on someone else: [the monster, who it hit], or null.
    public IReadOnlyList<string>? HitOnAnother(string line)
    {
        string text = line.Trim();
        foreach (CasterMessageMatcher m in _hitsOnOthers)
            if (m.TryMatchCaptures(text, out MessageCaptures c) && Bare(c.Source) is { } monster
                && c.Target is { Length: > 0 } target)
                return [monster, target];
        return null;
    }

    // A {source} prints with its own article ("The ogre picks ..."), which the roster
    // doesn't list.
    private static string? Bare(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string n = name.Trim();
        foreach (string article in Articles)
            if (n.StartsWith(article, StringComparison.Ordinal)) return n[article.Length..];
        return n;
    }

    private static readonly string[] Articles = ["The ", "An ", "A "];

    // The failure line's record (flagged LastActionFailed), or null when line isn't one.
    public MessageRecord? FailureOf(string line) => _fails.GetValueOrDefault(line.Trim());

    // Whether line is one of these, for telling it from an unrecognized line.
    public bool IsKnown(string line) =>
        IsDeath(line, out _) || IsEntrance(line) || IsTrap(line) || FailureOf(line) is not null
        || HitOnUs(line) is not null || HitOnAnother(line) is not null || IsChase(line, out _);
}
