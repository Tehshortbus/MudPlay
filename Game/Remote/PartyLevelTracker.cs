using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// Keeps the party's level bounds warm for path planning. When this character
// leads a party and the "avoid party-impassable level gates" toggle is on, the
// tracker fires an @level probe whenever the roster changes (debounced by a
// roster signature) so PlayerDatabase holds each member's exact level, and
// exposes the synchronous Bounds that MovementFilter.PartyLevelBoundsProvider
// reads at BFS time to route the party around gates a member can't clear.
//
// The async probe and the synchronous gate check are decoupled through
// PlayerDatabase: the probe (persisting via PlayerDatabase.RecordLevel)
// refreshes the cache in the background; Bounds only ever reads it. A member
// not yet probed contributes their title-derived band — or nothing, when even
// the title is unknown — so planning degrades gracefully until the reply lands
// instead of blocking on a round-trip.
//
// Read-only on party state: subscribes to PartyState.Members collection changes
// and the PartyState.IsInParty / PartyState.SelfIsLeader property changes; never
// writes a party field.
public sealed class PartyLevelTracker : IDisposable
{
    private readonly PartyState _party;
    private readonly PartyLevelProbe _probe;
    private readonly PlayerDatabase _players;
    private readonly Func<int?> _selfLevel;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;
    private string _lastProbedSignature = string.Empty;

    public PartyLevelTracker(
        PartyState party,
        PartyLevelProbe probe,
        PlayerDatabase players,
        Func<int?> selfLevel,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(selfLevel);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _party = party;
        _probe = probe;
        _players = players;
        _selfLevel = selfLevel;
        _isEnabled = isEnabled;
        _log = log;

        _party.Members.CollectionChanged += OnMembersChanged;
        _party.PropertyChanged += OnPartyPropertyChanged;
        MaybeProbe();
    }

    // The party's most-constraining (Low, High) level window, or null when the
    // feature is off, we're not leading a party, or nobody's level is known.
    // Synchronous — reads only the PlayerDatabase cache the probe keeps warm;
    // each member contributes their exact level when probed, else their title
    // band.
    public (int Low, int High)? Bounds()
    {
        if (!_isEnabled()) return null;
        if (!_party.IsInParty || !_party.SelfIsLeader) return null;

        List<PartyLevelEstimate> estimates = new();
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            PlayerRecord? rec = _players.Find(m.Name);
            int? exact = rec?.Level;
            (int Min, int Max)? title = exact is null
                ? ClassTitleTable.LookupLevelRange(rec?.Title)
                : null;
            estimates.Add(new PartyLevelEstimate(exact, title));
        }
        return PartyLevelBounds.Compute(_selfLevel(), estimates);
    }

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e) => MaybeProbe();

    private void OnPartyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PartyState.IsInParty) or nameof(PartyState.SelfIsLeader))
            MaybeProbe();
    }

    // Fire an @level probe when leading a party whose roster changed since the
    // last probe. The roster signature debounces unrelated party-state churn
    // (HP polls, leader-name refresh) down to one probe per genuine membership
    // change. Clears the signature whenever the feature is off or we stop
    // leading, so re-forming the same party re-probes.
    private void MaybeProbe()
    {
        if (!_isEnabled() || !_party.IsInParty || !_party.SelfIsLeader)
        {
            _lastProbedSignature = string.Empty;
            return;
        }

        string signature = RosterSignature();
        if (signature.Length == 0) { _lastProbedSignature = string.Empty; return; }
        if (signature == _lastProbedSignature) return;
        _lastProbedSignature = signature;

        _log?.Info("PartyLevel", "Roster changed — probing party @level.");
        _ = _probe.QueryAsync();   // fire-and-forget; the probe persists levels via RecordLevel
    }

    private string RosterSignature()
    {
        IEnumerable<string> names = _party.Members
            .Where(m => !m.IsSelf && !string.IsNullOrEmpty(m.Name))
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        return string.Join('\n', names);   // a newline can't appear in a player name
    }

    public void Dispose()
    {
        _party.Members.CollectionChanged -= OnMembersChanged;
        _party.PropertyChanged -= OnPartyPropertyChanged;
    }
}
