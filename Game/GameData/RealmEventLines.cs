using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// The lines of a set's Messages table that belong to no spell and that the client acts
// on (GAME_MECHANICS "The export's `Messages` table (Nightmare Redux 1.8.4)"):
//   - a monster's death text, printed to the room when it dies (Effects "Death");
//   - a chase line, printed when someone leaves through a command exit (Effects
//     "Chase", the leaver as {target});
//   - a monster entrance, a monster appearing or changing form (Effects "CheckRoom");
//   - a trap going off, on walking into it or on a failed disarm (Effects "Trap"; the
//     table has both lines of each trapped exit and can't tell them apart).
// A set exported without the table has none, and these lines stay unknown to it.
public sealed class RealmEventLines
{
    public static RealmEventLines None { get; } = new([], [], [], [], [], "none: the set has no Messages table");

    // Death text → the monster it is the death of; null when the text is several
    // monsters' (the export then names it by its words, not by a monster).
    private readonly Dictionary<string, string?> _deaths;
    private readonly List<CasterMessageMatcher> _chases;
    private readonly HashSet<string> _entrances;
    private readonly HashSet<string> _traps;
    // The few trap lines that name who they hit or the damage.
    private readonly List<CasterMessageMatcher> _trapTemplates;

    public string Summary { get; }

    private RealmEventLines(Dictionary<string, string?> deaths, List<CasterMessageMatcher> chases,
        HashSet<string> entrances, HashSet<string> traps, List<CasterMessageMatcher> trapTemplates, string summary)
    {
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
            return new([], [], [], [], [], $"none: the set's Messages table isn't one this client reads ({why})");

        Dictionary<string, string?> deaths = new(StringComparer.Ordinal);
        List<CasterMessageMatcher> chases = [];
        HashSet<string> chaseTemplates = new(StringComparer.Ordinal);
        HashSet<string> entrances = new(StringComparer.Ordinal);
        HashSet<string> traps = new(StringComparer.Ordinal);
        List<CasterMessageMatcher> trapTemplates = [];
        foreach (MdbMessageRow row in MdbMessageRow.Read(table))
        {
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
        return new(deaths, chases, entrances, traps, trapTemplates,
            $"from the set's Messages table: {deaths.Count} death lines ({named} naming their monster), "
            + $"{chases.Count} chase lines, {entrances.Count} monster entrances, {traps.Count + trapTemplates.Count} trap lines");
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

    // Whether line is one of these, for telling it from an unrecognized line.
    public bool IsKnown(string line) =>
        IsDeath(line, out _) || IsEntrance(line) || IsTrap(line) || IsChase(line, out _);
}
