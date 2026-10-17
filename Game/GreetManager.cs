using System.Text;
using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game;

// Auto-greets newly-encountered players. Subscribes to
// RoomEntityClassifier.EntitiesObserved — the single consolidated hook that
// fires for both `Also here:` room listings and live arrival lines. For every
// occupant the classifier tagged EntityKind.Player (red-name, non-sneaking —
// the only kind that produces a visible name), the manager sends `greet <name>`
// then `look <name>` the first time we see them on the current local-calendar
// day.
//
// Once-per-day dedup lives on the per-BBS player observation row
// (PlayerObservation.LastGreetedUtc) via PlayerDatabase.GetLastGreetedUtc /
// RecordGreeted. The day boundary is local midnight — we compare
// LastGreetedUtc.ToLocalTime().Date against today's local date.
//
// Skips: our own character (_selfNameProvider) and any current party member
// (PartyState.Members) — greeting either would be noise. Players are addressed
// by GIVEN name; MajorMUD targets `greet` / `look` by the first name token.
//
// Off by default — gated on Enabled, pushed in from the Settings → Talk "Greet
// players when first met" checkbox. The wire-sender is bound by
// MainWindowViewModel after telnet connects; before that the manager observes
// but can't send.
public sealed class GreetManager : IDisposable
{
    private readonly RoomEntityClassifier _classifier;
    private readonly PlayerDatabase _players;
    private readonly PartyState _party;
    private readonly Func<string?> _selfNameProvider;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // Master enable — Settings → Talk "Greet players when first met". Default
    // off (opt-in).
    public bool Enabled { get; set; }

    private readonly Func<DateTime> _now;

    public GreetManager(
        RoomEntityClassifier classifier,
        PlayerDatabase players,
        PartyState party,
        Func<string?> selfNameProvider,
        Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
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

    // Bind the wire-sender — same shape as every other engine. Without it the
    // manager observes but sends nothing.
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

    // The master switch (true = off): off, nobody is greeted. A greet is only
    // recorded when sent, so one skipped here is made at the next sighting.
    public Func<bool>? MasterSwitchOff { get; set; }

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        if (!Enabled) return;
        if (_wireSender is null) return;
        if (MasterSwitchOff?.Invoke() == true) return;
        if (obs.Entities is null || obs.Entities.Count == 0) return;

        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Player) continue;
            TryGreet(e.ResolvedName);
        }
    }

    // The per-player greet decision.
    internal void TryGreet(string? resolvedName)
    {
        if (_wireSender is null) return;
        (string given, _) = PlayerObservation.SplitName(resolvedName);
        if (string.IsNullOrEmpty(given)) return;

        if (IsSelf(given)) return;
        if (IsPartyMember(given)) return;

        DateTime nowUtc = _now();
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
