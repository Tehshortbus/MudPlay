using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game.Combat;

// Observes mid-room arrival lines and appends the new entity to
// RoomEntityClassifier.Current. "<name> <verb> into the room from <dir>." fires
// when a monster spawns (server-driven respawn or script spawn) OR when a player
// walks into our room without us triggering a full room re-display.
//
// Some monsters carry a CUSTOM, per-monster spawn line with no "into the room" /
// "from <dir>" marker at all — "A muckworm darts out of the mud!" — so the two
// structured patterns above can't match them (the wording is arbitrary and
// unexported). Those are caught by a colour scan instead (OnLineScan): every
// monster-enter line paints the monster's NAME the yellow palette index (3/11) —
// the one signal that's server-set and survives the user's client palette — so a
// line that ISN'T already claimed by a structured pattern, whose fully-yellow
// word span resolves to a known monster not already in the room, is treated as an
// arrival. The colour is read as the palette INDEX, not the rendered RGB, so a
// custom palette that remaps yellow doesn't change recognition; a plain room
// description (default-colour, even if it names a monster) never matches because
// the name isn't yellow-indexed.
//
// Critical for combat / health gating: without this watcher, CombatStateTracker
// only re-evaluates the Combat gate on Also-Here observations. A mob spawning
// into our room mid-walk would NOT pause the walker until the next move's room
// re-display — too late, we'd already be hit. Appending the arrival to
// RoomEntityClassifier.Current + re-firing its EntitiesObserved drives the
// tracker's gate decision immediately.
//
// Classification: the wire colours the arrival name yellow for monsters, red for
// players. Watcher reads the first non-space cell's foreground colour off the
// line's Attributes as a hint when the name doesn't match the active game-data
// tables. When both a name lookup and the colour hint agree, the lookup wins
// (gives us the monster number → priority for ordering). When they disagree, the
// colour wins (server is authoritative on the "what kind of thing arrived"
// question) but we log a Warn so the user knows their data is stale.
//
// Article-stripping: arrival lines lead with "A ", "An ", or "The " for monsters
// ("A fierce lashworm…"). Also-Here lines don't carry the article, so the
// classifier's prefix-strip alone wouldn't match. Watcher peels the article
// before classification.
public sealed class RoomEntryWatcher : IDisposable
{
    // LogService category — appears as [RoomEntry] rows per observed arrival +
    // classification mismatches.
    public const string LogCategory = "RoomEntry";

    private static readonly string[] LeadingArticles =
        new[] { "A ", "An ", "The " };

    private readonly MessageRouter _router;
    private readonly RoomEntityClassifier _classifier;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly IDisposable _arrivalSub;
    private readonly IDisposable _sneakArrivalSub;
    private readonly IDisposable _spawnArrivalSub;
    private bool _disposed;
    // ConditionTracker.IsConditionLine; built after this watcher, so set later.
    private Func<string, bool>? _isConditionLine;

    public void SetConditionLineProbe(Func<string, bool> isConditionLine)
        => _isConditionLine = isConditionLine ?? throw new ArgumentNullException(nameof(isConditionLine));

    // Fires after each arrival is parsed + classified + appended to the
    // classifier. Independent of RoomEntityClassifier.EntitiesObserved —
    // consumers that only care about per-arrival deltas subscribe here instead of
    // folding observation diffs.
    public event Action<RoomEntryArrivalEvent>? ArrivalObserved;

    public RoomEntryWatcher(
        MessageRouter router,
        RoomEntityClassifier classifier,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(classifier);
        _router = router;
        _classifier = classifier;
        _log = log;
        _now = now ?? (static () => DateTimeOffset.Now);
        _arrivalSub = router.Subscribe(KnownPatterns.RoomEntryArrival, OnArrival);
        _sneakArrivalSub = router.Subscribe(KnownPatterns.SneakArrivalNotice, OnSneakArrival);
        // Ahead of CombatManager's handler for the same line: a monster put on the
        // roster here is engaged from the roster, and only a roster still short asks
        // for the room.
        _spawnArrivalSub = router.Subscribe(KnownPatterns.RoomSpawnArrival, OnSpawnArrival, tieBreak: 10);
        // Colour-scan fallback for custom, unstructured spawn lines (see class note).
        _router.LineDispatched += OnLineScan;
    }

    private void OnArrival(MatchResult match)
    {
        // (?<name>.+?) at positional 0, (?<direction>[\w-]+) at 1.
        if (match.Groups.Count < 2) return;
        string nameWithArticle = match.Groups[0];
        string direction = match.Groups[1];
        if (nameWithArticle.Length == 0) return;

        string name = StripLeadingArticle(nameWithArticle);
        if (name.Length == 0) return;

        // Colour-hint classification — read the first non-space cell's
        // foreground. Standard ANSI palette: index 1 = red, index 3 =
        // yellow; bright variants 9 and 11. Either matches; the wire
        // typically uses the bright variant.
        EntityKind colorHint = ResolveColorHint(match.Line);

        // Run the classifier's lookup. Monster first (matches the
        // Also-Here precedence), player fallback, Unknown when neither.
        // Try the article-stripped name first — game-data records store
        // bare names, so this is the common hit. If stripping turned a
        // record whose name legitimately *begins* with "The "/"A "/"An "
        // into a miss (stock monster 251 is the lone "The …"-prefixed
        // entry), retry with the article intact so we don't drop the
        // monster number a do-not-attack rule keys on. The retry only
        // fires when stripping changed the string AND the bare form
        // missed — negligible cost on the common path.
        RoomEntity classified = _classifier.Classify(name);
        if (classified.Kind == EntityKind.Unknown &&
            !string.Equals(name, nameWithArticle, StringComparison.Ordinal))
        {
            RoomEntity retained = _classifier.Classify(nameWithArticle);
            if (retained.Kind != EntityKind.Unknown)
            {
                classified = retained;
                name = nameWithArticle;
            }
        }

        EntityKind finalKind;
        if (classified.Kind != EntityKind.Unknown)
        {
            // Known entity — trust the lookup (gives us the monster
            // number → priority info). Cross-check with the colour
            // hint and log if they disagree.
            finalKind = classified.Kind;
            if (colorHint != EntityKind.Unknown && colorHint != classified.Kind)
            {
                _log?.Warn(LogCategory,
                    $"arrival '{name}' — colour says {colorHint} but lookup matched {classified.Kind}; trusting lookup",
                    context: match.Text);
            }
        }
        else
        {
            // Unknown to our data. Trust the colour hint; fall back to
            // Monster (the more common case for the combat-gating
            // consumer) when colour is also missing.
            finalKind = colorHint switch
            {
                EntityKind.Player  => EntityKind.Player,
                EntityKind.Monster => EntityKind.Monster,
                _                   => EntityKind.Monster,
            };
            classified = new RoomEntity(
                RawName:       name,
                ResolvedName:  finalKind == EntityKind.Player ? FirstToken(name) : name,
                Kind:          finalKind,
                MonsterNumber: null);
            _log?.Warn(LogCategory,
                $"unknown arrival '{name}' — treating as {finalKind} ({(colorHint == EntityKind.Unknown ? "no colour hint" : "colour hint")})",
                context: match.Text);
            // It goes on the roster under a name nothing can be looked up by.
            if (finalKind == EntityKind.Monster)
                _classifier.NoteUnlistedArrival($"an arrival the game data has no monster for: '{name}'");
        }

        _classifier.AppendArrivalEntity(classified, rawWireLine: match.Text);

        _log?.Info(LogCategory,
            $"arrival kind={finalKind} name={name} direction={direction}");
        ArrivalObserved?.Invoke(new RoomEntryArrivalEvent(
            Name: name, Kind: finalKind, Direction: direction, At: _now()));
    }

    // An arrival worded without a name the pattern can lift out ("The banshee floats
    // after you!", "A shade materializes in the room."): nobody is added to the
    // roster, and from here it is known to be short of someone. A monster's own
    // appear line names nobody either, but the message table says whose it is, so a
    // line only one monster prints puts that monster on the roster, unless it is worded
    // as one already here coming to life (MonsterAppearLines.Replaces): the room is
    // asked for then, so what it was listed as before doesn't linger beside it.
    private void OnSpawnArrival(MatchResult match)
    {
        MonsterAppearLines.Entry? appear = MonsterAppearLines.Find(match.Text);
        if (appear is { MonsterName: { } name, Replaces: false }
            && _classifier.Classify(name) is { Kind: EntityKind.Monster } monster)
        {
            _classifier.AppendArrivalEntity(monster, rawWireLine: match.Text);
            _log?.Info(LogCategory,
                $"arrival kind=Monster name={monster.ResolvedName} (its appear line, message {appear.MessageNumber})");
            ArrivalObserved?.Invoke(new RoomEntryArrivalEvent(
                Name: monster.ResolvedName, Kind: EntityKind.Monster, Direction: "nowhere", At: _now()));
            return;
        }
        _classifier.NoteUnlistedArrival(appear is { Replaces: true }
            ? $"the {appear.MonsterName}'s appear line, worded as one already here (message {appear.MessageNumber}): '{match.Text}'"
            : $"an arrival that names nobody: '{match.Text}'");
    }

    // "You notice <name> sneaking in from the <dir>." — a player who failed a
    // sneak into our room. Monsters never emit this line, so we classify Player
    // unconditionally and ignore the colour hint: the wire paints this line the
    // monster hue, which previously tagged the sneaker a null-numbered Monster
    // whose fail-open engageability held the Combat gate open forever (no death
    // or departure line ever arrives to clear it). A player never gates combat,
    // so tracking it as Player is the fix.
    private void OnSneakArrival(MatchResult match)
    {
        // (?<name>\w+) at positional 0, (?<direction>[\w-]+) at 1.
        if (match.Groups.Count < 2) return;
        string name = match.Groups[0];
        string direction = match.Groups[1];
        if (name.Length == 0) return;

        RoomEntity player = new(
            RawName:       name,
            ResolvedName:  name,
            Kind:          EntityKind.Player,
            MonsterNumber: null);
        _classifier.AppendArrivalEntity(player, rawWireLine: match.Text);

        _log?.Info(LogCategory,
            $"sneak arrival kind=Player name={name} direction={direction}");
        ArrivalObserved?.Invoke(new RoomEntryArrivalEvent(
            Name: name, Kind: EntityKind.Player, Direction: direction, At: _now()));
    }

    // Colour-scan fallback (see class note). Fires for a custom, unstructured
    // spawn line — no "into the room" / "from <dir>", so the structured patterns
    // miss it — by finding a fully-yellow known-monster name on a line no other
    // pattern claims. Runs on EVERY dispatched line, so it's ordered cheap-guard
    // first: bail immediately unless the line carries a yellow-indexed cell (nearly
    // every line doesn't), only then pay for the AnyPatternMatches / name scan.
    internal void OnLineScan(LineExtractor.EmittedLine line)
    {
        if (_disposed) return;
        string text = line.Text;
        if (text.Length == 0) return;

        // The one palette-stable signal: the monster name is painted the yellow
        // index. No yellow cell → nothing to find.
        if (!HasYellowCell(line)) return;

        // A line a structured pattern already owns (the two arrival patterns, chat,
        // prompts, combat status, …) is handled elsewhere — don't double-process.
        if (_router.AnyPatternMatches(line)) return;

        // The roster + look-notice lines can carry a monster name; skip them
        // explicitly so a palette that tinted their name yellow can't leak in
        // (Also-Here is the classifier's own job; "You notice" is a reveal/notice).
        if (text.StartsWith("Also here:", StringComparison.Ordinal)
            || text.StartsWith("You notice ", StringComparison.Ordinal)) return;

        // The client's own notices are yellow and can name a monster ("[Round 1
        // dealt: You 59, small green slime 0]") — reading one as an arrival
        // re-engaged a monster we'd just killed (report paradigm-20260928-230226).
        if (ClientNotice.IsNotice(text)) return;

        // Another player changing gear is painted yellow too, and an item can carry a
        // monster's name: "Francisco removes shadow bracelet!" put a 'shadow' on the
        // roster and the attack spell went out at "removes shadow" (report
        // paradigm-20261020-203400).
        if (BenignChatterMatcher.IsOtherPlayerGearSwap(text, IsPlayerName)) return;

        // A spell's line on the character can hold a monster's name too: "Your vision
        // returns to normal." put a 'Your vision' on the roster, whose `a` had no
        // effect, and the room never read as empty while a real attacker hit us for
        // 48 minutes (report stock-20261127-175832).
        if (_isConditionLine?.Invoke(text) == true)
        {
            _log?.Debug(LogCategory, $"colour scan: '{text}' is a condition's own line — not an arrival");
            return;
        }

        if (!TryFindYellowMonster(line, out RoomEntity monster)) return;

        // A later yellow mention of a monster already in the room isn't a new
        // arrival — only append one we don't already have.
        if (AlreadyPresent(monster)) return;

        _classifier.AppendArrivalEntity(monster, rawWireLine: text);
        _log?.Info(LogCategory,
            $"colour-scan arrival kind=Monster name={monster.ResolvedName} (custom spawn line)");
        ArrivalObserved?.Invoke(new RoomEntryArrivalEvent(
            Name: monster.ResolvedName, Kind: EntityKind.Monster, Direction: "nowhere", At: _now()));
    }

    // Scan for a monster name whose cells are ALL yellow-indexed. The name may be
    // the only yellow span (name-yellow constructor) or sit inside an all-yellow
    // line (whole-line-yellow constructor); either way only a window of consecutive
    // fully-yellow words is tried, so a default-colour room-description mention of
    // the same monster is never matched. Windows are 1–4 words (monster names are
    // short); Classify's single-arg overload is used so failed probes don't spam
    // the unknown-entity log.
    private bool TryFindYellowMonster(LineExtractor.EmittedLine line, out RoomEntity monster)
    {
        monster = default!;
        string text = line.Text;

        // Split into words, tagging each as fully-yellow or not.
        List<(string Word, bool Yellow)> words = new();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ') { i++; continue; }
            int start = i;
            while (i < text.Length && text[i] != ' ') i++;
            bool allYellow = true;
            for (int k = start; k < i; k++)
                if (!IsYellow(line, k)) { allYellow = false; break; }
            words.Add((text[start..i], allYellow));
        }

        // Slide 1–4-word windows over each run of consecutive yellow words.
        int w = 0;
        while (w < words.Count)
        {
            if (!words[w].Yellow) { w++; continue; }
            int runStart = w;
            while (w < words.Count && words[w].Yellow) w++;
            int runEnd = w;   // exclusive

            for (int a = runStart; a < runEnd; a++)
            {
                for (int len = 1; len <= 4 && a + len <= runEnd; len++)
                {
                    string candidate = string.Join(' ', words.GetRange(a, len).ConvertAll(x => x.Word));
                    candidate = StripLeadingArticle(candidate).TrimEnd('.', '!', ',', ';', ':');
                    if (candidate.Length == 0) continue;
                    RoomEntity e = _classifier.Classify(candidate);
                    if (e.Kind == EntityKind.Monster) { monster = e; return true; }
                }
            }
        }
        return false;
    }

    // A player on the room roster, or one the players-seen list knows. Only someone in
    // the room can be seen changing gear, but they may have walked in since the roster
    // was read, so the list backs the roster up.
    private bool IsPlayerName(string name)
    {
        if (_classifier.Current is { } cur)
            foreach (RoomEntity e in cur.Entities)
                if (e.Kind == EntityKind.Player
                    && string.Equals(FirstToken(e.ResolvedName), name, StringComparison.OrdinalIgnoreCase))
                    return true;
        return _classifier.Classify(name).Kind == EntityKind.Player;
    }

    private bool AlreadyPresent(RoomEntity monster)
    {
        if (_classifier.Current is not { } cur) return false;
        foreach (RoomEntity e in cur.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            if (monster.MonsterNumber is not null && e.MonsterNumber == monster.MonsterNumber) return true;
            if (string.Equals(e.ResolvedName, monster.ResolvedName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasYellowCell(LineExtractor.EmittedLine line)
    {
        for (int i = 0; i < line.Attributes.Length; i++)
            if (IsYellow(line, i)) return true;
        return false;
    }

    // The yellow monster hue is palette index 3 (standard) or 11 (bright). Read as
    // the stored INDEX, not the rendered colour, so a custom palette can't shift it.
    private static bool IsYellow(LineExtractor.EmittedLine line, int i)
    {
        if (i >= line.Attributes.Length) return false;
        TerminalColor fg = line.Attributes[i].Foreground;
        return fg.Kind == ColorKind.Indexed && (fg.Value == 3 || fg.Value == 11);
    }

    // Read the foreground of the first non-space cell off the line's attribute
    // strip. Returns Monster for yellow (indexed 3 or 11), Player for red
    // (indexed 1 or 9), Unknown for anything else (including default-colour / RGB
    // / out-of-range palette indices).
    private static EntityKind ResolveColorHint(LineExtractor.EmittedLine line)
    {
        if (line.Attributes.Length == 0) return EntityKind.Unknown;
        for (int i = 0; i < line.Text.Length && i < line.Attributes.Length; i++)
        {
            if (line.Text[i] == ' ') continue;
            TerminalColor fg = line.Attributes[i].Foreground;
            if (fg.Kind != ColorKind.Indexed) return EntityKind.Unknown;
            return fg.Value switch
            {
                3 or 11 => EntityKind.Monster,
                1 or 9  => EntityKind.Player,
                _        => EntityKind.Unknown,
            };
        }
        return EntityKind.Unknown;
    }

    private static string StripLeadingArticle(string name)
    {
        foreach (string article in LeadingArticles)
        {
            if (name.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                return name[article.Length..];
        }
        return name;
    }

    private static string FirstToken(string s)
    {
        int sp = s.IndexOf(' ');
        return sp >= 0 ? s[..sp] : s;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLineScan;
        _arrivalSub.Dispose();
        _sneakArrivalSub.Dispose();
        _spawnArrivalSub.Dispose();
    }
}
