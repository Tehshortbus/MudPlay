using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Combat;

// Observes mid-room arrival lines and appends the new entity to
// RoomEntityClassifier.Current. "<name> <verb> into the room from <dir>." fires
// when a monster spawns (server-driven respawn or script spawn) OR when a player
// walks into our room without us triggering a full room re-display.
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

    private readonly RoomEntityClassifier _classifier;
    private readonly LogService? _log;
    private readonly IDisposable _arrivalSub;
    private readonly IDisposable _sneakArrivalSub;
    private bool _disposed;

    // Fires after each arrival is parsed + classified + appended to the
    // classifier. Independent of RoomEntityClassifier.EntitiesObserved —
    // consumers that only care about per-arrival deltas (e.g. spawn counters)
    // subscribe here instead of folding observation diffs.
    public event Action<RoomEntryArrivalEvent>? ArrivalObserved;

    public RoomEntryWatcher(
        MessageRouter router,
        RoomEntityClassifier classifier,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(classifier);
        _classifier = classifier;
        _log = log;
        _arrivalSub = router.Subscribe(KnownPatterns.RoomEntryArrival, OnArrival);
        _sneakArrivalSub = router.Subscribe(KnownPatterns.SneakArrivalNotice, OnSneakArrival);
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
        }

        _classifier.AppendArrivalEntity(classified, rawWireLine: match.Text);

        _log?.Info(LogCategory,
            $"arrival kind={finalKind} name={name} direction={direction}");
        ArrivalObserved?.Invoke(new RoomEntryArrivalEvent(
            Name: name, Kind: finalKind, Direction: direction, At: DateTimeOffset.Now));
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
            Name: name, Kind: EntityKind.Player, Direction: direction, At: DateTimeOffset.Now));
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
        _arrivalSub.Dispose();
        _sneakArrivalSub.Dispose();
    }
}
