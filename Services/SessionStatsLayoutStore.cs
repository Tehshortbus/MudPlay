using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// Per-character memory of the Session Stats window's panel order + hidden set.
// The SessionStatsViewModel reads Resolve when the window opens and pushes
// changes back through Update as the user drags panels around or toggles them
// via the context menu.
//
// Mirrors WindowLayoutStore: the live layout is hydrated from
// CharacterProfile.SessionStatsLayout on ProfileService.ProfileLoaded, cleared
// on ProfileClosed, and snapshotted back on ProfileSaving. Update additionally
// calls ProfileService.Save straight away so a reorder / hide survives even if
// the session ends without another save — the same write-through the settings
// resolver uses.
public sealed class SessionStatsLayoutStore
{
    // Canonical panel ids in their default top-to-bottom order. The graphs lead,
    // then the three stat sections — the window's original layout.
    public static readonly IReadOnlyList<string> DefaultOrder = new[]
    {
        "KillsGraph",
        "ExpGraph",
        "PlayerStatistics",
        "TimeAnalysis",
        "SessionStatistics",
    };

    private readonly ProfileService _profile;
    private SessionStatsLayout _layout = new();

    public SessionStatsLayoutStore(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        profile.ProfileLoaded += p => _layout = Clone(p.SessionStatsLayout);
        profile.ProfileClosed += () => _layout = new();
        profile.ProfileSaving += p => p.SessionStatsLayout = Clone(_layout);
    }

    // The resolved panel list for the live profile — every known panel, in the
    // user's saved order (unknown ids dropped, new panels appended in their
    // default spot), with each panel's current visibility.
    public IReadOnlyList<(string Id, bool Visible)> Resolve() => Resolve(_layout);

    // Persist a new order + hidden set and write the profile through immediately.
    public void Update(IEnumerable<string> order, IEnumerable<string> hidden)
    {
        _layout = new SessionStatsLayout
        {
            Order = order.ToList(),
            Hidden = hidden.ToList(),
        };
        // Mirror the profile field eagerly too, so a Save triggered elsewhere
        // before the next ProfileSaving snapshot still carries the change.
        if (_profile.Current is { } current) current.SessionStatsLayout = Clone(_layout);
        _profile.Save();
    }

    // Pure resolution of a saved layout against DefaultOrder: saved ids first
    // (in their saved order, unknown ones discarded), then any known panel the
    // save predates appended in default order. Each panel is visible unless its
    // id is in the saved hidden set. Static + side-effect free so the ordering
    // invariants can be unit-tested without a profile.
    public static IReadOnlyList<(string Id, bool Visible)> Resolve(SessionStatsLayout? saved)
    {
        HashSet<string> known = new(DefaultOrder, StringComparer.Ordinal);
        HashSet<string> hidden = saved?.Hidden is { } h
            ? new HashSet<string>(h, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        List<string> ordered = new();
        HashSet<string> placed = new(StringComparer.Ordinal);
        if (saved?.Order is { } savedOrder)
        {
            foreach (string id in savedOrder)
                if (known.Contains(id) && placed.Add(id))
                    ordered.Add(id);
        }
        foreach (string id in DefaultOrder)
            if (placed.Add(id))
                ordered.Add(id);

        return ordered.Select(id => (id, !hidden.Contains(id))).ToList();
    }

    private static SessionStatsLayout Clone(SessionStatsLayout? src) => new()
    {
        Order = src?.Order is { } o ? new List<string>(o) : null,
        Hidden = src?.Hidden is { } h ? new List<string>(h) : null,
    };
}
