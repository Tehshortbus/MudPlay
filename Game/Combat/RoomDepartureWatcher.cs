using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Combat;

// Observes mid-room departure lines and removes the departing monster from
// RoomEntityClassifier.Current. "<name> walks out of the room to <dir>." and
// "<name> exits the room to <dir>." fire when a monster leaves our room — most
// often when a fleeing player drags the mob we were engaged with out with them
// (see the 180449 + paradigm drag-out captures).
//
// Critical for combat gating: CombatStateTracker only re-evaluates the Combat
// gate on RoomEntityClassifier.EntitiesObserved. A departure produces no room
// re-display and no Also-Here line, so without this watcher the gate the
// departed mob held stays asserted forever — the walker freezes and the client
// keeps swinging at empty air ("Your command had no effect."). Removing the
// entity + re-firing EntitiesObserved drops the gate when the last hostile
// leaves.
//
// Article-stripping mirrors RoomEntryWatcher: departure lines lead with "A ",
// "An ", or "The " ("The orc rogue walks out…"), but the classifier's stored
// entity names are bare, so peel the article before matching.
//
// Only monster-kind entities hold the Combat gate, so RemoveDepartedEntity is a
// no-op for a departing player — a player's stale entry in Current is harmless.
public sealed class RoomDepartureWatcher : IDisposable
{
    public const string LogCategory = "RoomDeparture";

    private static readonly string[] LeadingArticles =
        new[] { "A ", "An ", "The " };

    private readonly RoomEntityClassifier _classifier;
    private readonly LogService? _log;
    private readonly IDisposable _departureSub;
    private bool _disposed;

    public RoomDepartureWatcher(
        MessageRouter router,
        RoomEntityClassifier classifier,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(classifier);
        _classifier = classifier;
        _log = log;
        _departureSub = router.Subscribe(KnownPatterns.RoomEntryDeparture, OnDeparture);
    }

    private void OnDeparture(MatchResult match)
    {
        // (?<name>.+?) at positional 0, (?<direction>[\w-]+) at 1.
        if (match.Groups.Count < 2) return;
        string nameWithArticle = match.Groups[0];
        string direction = match.Groups[1];
        if (nameWithArticle.Length == 0) return;

        string name = StripLeadingArticle(nameWithArticle);
        if (name.Length == 0) return;

        // Try the bare (article-stripped) form first — the classifier stores bare
        // names. If that misses, retry with the article intact for the lone
        // "The …"-named monster edge case (mirrors RoomEntryWatcher's retry).
        bool removed = _classifier.RemoveDepartedEntity(name);
        if (!removed && !string.Equals(name, nameWithArticle, StringComparison.Ordinal))
            removed = _classifier.RemoveDepartedEntity(nameWithArticle);

        if (removed)
            _log?.Info(LogCategory,
                $"departure name={name} direction={direction} — cleared from room");
        else
            _log?.Debug(LogCategory,
                $"departure name={name} direction={direction} — no matching monster in room");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _departureSub.Dispose();
    }
}
