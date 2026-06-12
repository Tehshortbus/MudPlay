using System.Text;
using FujinTerm.Game.Combat;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game;

/// <summary>
/// Auto-greets newly-encountered players. Subscribes to
/// <see cref="RoomEntityClassifier.EntitiesObserved"/> — the single
/// consolidated hook that fires for both <c>Also here:</c> room
/// listings and live arrival lines. For every occupant the classifier
/// tagged <see cref="EntityKind.Player"/> (red-name, non-sneaking — the
/// only kind that produces a visible name), the manager sends
/// <c>greet &lt;name&gt;</c> then <c>look &lt;name&gt;</c> the first time
/// we see them on the current local-calendar day.
/// </summary>
/// <remarks>
/// <para>
/// Once-per-day dedup lives on the per-BBS player observation row
/// (<see cref="PlayerObservation.LastGreetedUtc"/>) via
/// <see cref="PlayerDatabase.GetLastGreetedUtc"/> /
/// <see cref="PlayerDatabase.RecordGreeted"/>. The day boundary is
/// local midnight per user direction — we compare
/// <c>LastGreetedUtc.ToLocalTime().Date</c> against today's local date.
/// </para>
/// <para>
/// Skips: our own character (<see cref="_selfNameProvider"/>) and any
/// current party member (<see cref="PartyState.Members"/>) — greeting
/// either would be noise. Players are addressed by GIVEN name; MajorMUD
/// targets <c>greet</c> / <c>look</c> by the first name token.
/// </para>
/// <para>
/// Off by default — gated on <see cref="Enabled"/>, pushed in from the
/// Settings → Talk "Greet players when first met" checkbox. The
/// wire-sender is bound by <see cref="ViewModels.MainWindowViewModel"/>
/// after telnet connects; before that the manager observes but can't
/// send.
/// </para>
/// </remarks>
public sealed class GreetManager : IDisposable
{
    private readonly RoomEntityClassifier _classifier;
    private readonly PlayerDatabase _players;
    private readonly PartyState _party;
    private readonly Func<string?> _selfNameProvider;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    /// <summary>Master enable — Settings → Talk "Greet players when first met". Default off (opt-in).</summary>
    public bool Enabled { get; set; }

    /// <summary>Test seam for the day-boundary clock. Returns the current UTC time.</summary>
    public Func<DateTime> NowUtcProvider { get; set; } = static () => DateTime.UtcNow;

    public GreetManager(
        RoomEntityClassifier classifier,
        PlayerDatabase players,
        PartyState party,
        Func<string?> selfNameProvider)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(selfNameProvider);
        _classifier = classifier;
        _players = players;
        _party = party;
        _selfNameProvider = selfNameProvider;
        _classifier.EntitiesObserved += OnEntitiesObserved;
    }

    /// <summary>Bind the wire-sender — same shape as every other engine. Without it the manager observes but sends nothing.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _classifier.EntitiesObserved -= OnEntitiesObserved;
    }

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        if (!Enabled) return;
        if (_wireSender is null) return;
        if (obs.Entities is null || obs.Entities.Count == 0) return;

        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Player) continue;
            TryGreet(e.ResolvedName);
        }
    }

    /// <summary>Test seam — runs the per-player greet decision directly.</summary>
    internal void TryGreet(string? resolvedName)
    {
        if (_wireSender is null) return;
        (string given, _) = PlayerObservation.SplitName(resolvedName);
        if (string.IsNullOrEmpty(given)) return;

        if (IsSelf(given)) return;
        if (IsPartyMember(given)) return;

        DateTime nowUtc = NowUtcProvider();
        DateTime? last = _players.GetLastGreetedUtc(given);
        if (last is { } prev && prev.ToLocalTime().Date == nowUtc.ToLocalTime().Date)
            return; // already greeted today (local calendar day)

        _wireSender(Encoding.Latin1.GetBytes($"greet {given}\r"));
        _wireSender(Encoding.Latin1.GetBytes($"look {given}\r"));
        _players.RecordGreeted(given, nowUtc);
    }

    private bool IsSelf(string given)
    {
        (string selfGiven, _) = PlayerObservation.SplitName(_selfNameProvider());
        return !string.IsNullOrEmpty(selfGiven)
            && selfGiven.Equals(given, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsPartyMember(string given)
    {
        foreach (PartyMember m in _party.Members)
        {
            (string memberGiven, _) = PlayerObservation.SplitName(m.Name);
            if (memberGiven.Equals(given, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
