using System.Collections.Generic;
using System.Text;
using FujinTerm.Game.Remote;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

/// <summary>
/// Party-first stage of the path-item demand pipeline. Sits between the
/// walker's walk-start item announcer and
/// <see cref="PathItemDemandTracker"/>: when a planned route crosses an
/// <c>(Item: N)</c> / <c>(Ticket: N)</c> gate whose per-member item we lack,
/// this asks the party (via <see cref="PartyInventoryProbe"/>) before spending
/// a search / shop / hunt on it. If a member is holding a spare, we have them
/// hand it over (<c>@party give</c> / <c>@do give</c>) and the need is never
/// posted; only a genuine shortfall — no one has a spare — falls through to
/// <see cref="PathItemDemandTracker.OnPathItemsRequired"/>. Backs the
/// Settings → Other "defer to party inventory" affordance.
/// </summary>
/// <remarks>
/// <para>
/// The hand-off obeys MajorMUD's two remote verbs: when only one member holds
/// the item, <c>@party give &lt;item&gt; to &lt;self&gt;</c> is safe and
/// permission-free (the game routes it to the whole party, but only the holder
/// can act on it, so there's no over-giving); when several members hold copies,
/// a targeted <c>/&lt;holder&gt; @do give &lt;item&gt; to &lt;self&gt;</c> asks
/// exactly one so we don't collect a duplicate from everyone. "Spare" means a
/// count of at least two — the holder keeps one for their own gate crossing.
/// </para>
/// <para>
/// This is E1 of the party-inventory slice: it covers only the per-member
/// Item / Ticket gate with a give-from-surplus hand-off. Single-key gates
/// (one key for the whole party), level-gated teleports (every member must
/// qualify), buy-the-shortfall quantities, and the follower-silent-fail
/// movement gate land in later slices. When the feature is off or we're solo,
/// the announced list passes straight through to the demand tracker unchanged,
/// so PRs B / C / D behave exactly as before.
/// </para>
/// <para>
/// The probe round-trip is inherently async (a telepath each way), so the
/// give-or-forward decision is deferred off the walker's <c>WalkTo</c> call
/// stack through <see cref="_post"/> — mirroring how the shop / monster-drop
/// routers detach their reactions. Because the decision lands after a short
/// window, the walker may already be en route; the item arrives (or the need
/// posts) before it reaches the gate that requires it.
/// </para>
/// </remarks>
public sealed class PartyPathItemGate
{
    private const string LogCategory = "AutoSearch";

    private readonly Func<int, bool> _isCarried;
    private readonly Func<int, string, Task<PartyInventoryProbe.PartyItemResult>> _query;
    private readonly Func<int, string?> _itemName;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _inParty;
    private readonly Func<string?> _selfGivenName;
    private readonly Action<IReadOnlyList<int>> _forward;
    private readonly Action<Action> _post;
    private readonly LogService? _log;
    private Action<byte[]>? _wireSender;

    public PartyPathItemGate(
        Func<int, bool> isCarried,
        Func<int, string, Task<PartyInventoryProbe.PartyItemResult>> query,
        Func<int, string?> itemName,
        Func<bool> isEnabled,
        Func<bool> inParty,
        Func<string?> selfGivenName,
        Action<IReadOnlyList<int>> forward,
        Action<Action> post,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(isCarried);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(inParty);
        ArgumentNullException.ThrowIfNull(selfGivenName);
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(post);
        _isCarried = isCarried;
        _query = query;
        _itemName = itemName;
        _isEnabled = isEnabled;
        _inParty = inParty;
        _selfGivenName = selfGivenName;
        _forward = forward;
        _post = post;
        _log = log;
    }

    /// <summary>Bind the wire-sender for the give hand-off. MainWindowVM supplies the engine-gated <c>SendUserInput</c>.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Walk-start callback (re-points
    /// <see cref="AutoWalkManager.SetPathItemAnnouncer"/> ahead of the demand
    /// tracker): every item id gating an Item / Ticket exit along the planned
    /// route. When the feature is off or we're solo, forwards the list to the
    /// demand tracker unchanged; otherwise probes the party per distinct
    /// uncarried id and either arranges a give or forwards the shortfall.
    /// </summary>
    public void OnPathItemsRequired(IReadOnlyList<int> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (!_isEnabled() || !_inParty())
        {
            _forward(itemIds);
            return;
        }

        var considered = new HashSet<int>();
        foreach (int id in itemIds)
        {
            if (id <= 0 || !considered.Add(id)) continue;
            if (_isCarried(id)) continue;
            _post(() => _ = CheckThenActAsync(id));
        }
    }

    private async Task CheckThenActAsync(int id)
    {
        string? name = _itemName(id);
        if (string.IsNullOrWhiteSpace(name)) { _forward(new[] { id }); return; }

        PartyInventoryProbe.PartyItemResult result = await _query(id, name).ConfigureAwait(true);

        // Might have arrived between the announce and the reply window (an
        // earlier give, a floor pickup) — nothing to do then.
        if (_isCarried(id)) return;

        string? holder = null;
        int holderCount = 0;
        int membersWithAny = 0;
        foreach (KeyValuePair<string, int> kv in result.CountsByMember)
        {
            if (kv.Value > 0) membersWithAny++;
            // Spare = 2+ (the holder keeps one for their own gate crossing).
            if (kv.Value >= 2 && kv.Value > holderCount)
            {
                holder = kv.Key;
                holderCount = kv.Value;
            }
        }

        // No member has a spare to give — post the need so demand-driven
        // search / shop / hunt take over.
        if (holder is null) { _forward(new[] { id }); return; }

        string self = _selfGivenName() ?? string.Empty;
        if (self.Length == 0 || _wireSender is null) { _forward(new[] { id }); return; }

        if (membersWithAny == 1)
            // Only the holder has any copies: @party give is permission-free
            // and only they can act on it, so there's no risk of over-giving.
            SendRaw($"@party give {name} to {self}");
        else
            // Several members hold copies: target the chosen holder so we
            // don't collect a duplicate from every one of them.
            SendRaw($"/{holder} @do give {name} to {self}");

        _log?.Info(LogCategory,
            $"party holds {name} (x{holderCount} on {holder}); requested a give — path-item search deferred.");
    }

    private void SendRaw(string command)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(command + "\r"));
    }
}
