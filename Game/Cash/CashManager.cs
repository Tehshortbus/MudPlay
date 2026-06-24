using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Cash;

/// <summary>
/// Phase 9 PR 9.E — per-currency cash pickup / discard engine.
/// Subscribes to <see cref="KnownPatterns.CashOnGround"/>,
/// <see cref="KnownPatterns.CashPickedUp"/>,
/// <see cref="KnownPatterns.CashDropped"/>, and
/// <see cref="KnownPatterns.CashFromKill"/> (corpse loot after a
/// monster dies). Dispatches based on <see cref="CashSettings"/>
/// per-currency <see cref="CashPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Scope:
/// </para>
/// <list type="bullet">
/// <item><b>CashOnGround → policy dispatch</b>. Collect →
/// <c>get &lt;count&gt; &lt;coin&gt;</c> with the exact observed
/// count (specific amounts keep encumbrance / weight tracking
/// deterministic). Discard / Ignore → no action.</item>
/// <item><b>Encumbrance gates</b>. With a
/// <see cref="CashSettings.SkipCollectIfMakesLight"/> / Medium /
/// Heavy flag set and a parsed <see cref="InventorySnapshot"/>,
/// pickups are clamped to the headroom below the configured
/// bracket. <see cref="CashSettings.DropSmallerForLarger"/> trades
/// held lower-value coin 1:1 to make room for the higher-value
/// pickup. A per-currency in-flight delta (60s timeout) projects
/// pickups already dispatched but not yet confirmed so multi-coin
/// batches and quick re-displays can't over-collect.</item>
/// <item><b>CashPickedUp / CashDropped → tally update</b>. Held
/// per-currency counts exposed via <see cref="HeldCoin"/> feed the
/// stash-room and discard paths.</item>
/// <item><b>AutoDeposit trigger</b>. The gates read the authoritative
/// <see cref="InventorySnapshot.Currency"/> (the <c>i</c>-seeded,
/// delta-tracked holdings — not the local pickup tally): wealth gate
/// against <see cref="CurrencyHoldings.TotalCopperValue"/> (the
/// game's <c>Wealth:</c> line), coin gate against
/// <see cref="CurrencyHoldings.TotalCoinCount"/>. Either crossing
/// fires <see cref="AutoDepositRequested"/> once per crossing, but
/// only when a bank / stash location (<see cref="CashSettings.BankRoomKey"/>)
/// is configured — no location, no reroute destination, no fire.
/// Re-evaluated on <see cref="OnInventoryChanged"/> so buy / sell
/// wealth swings (which the CashManager's own patterns don't observe)
/// still arm the gate. Subscribers (the walker reroute) decide what
/// to do — this layer only signals.</item>
/// </list>
/// <para>
/// <b>Deferred to follow-ups</b>:
/// </para>
/// <list type="bullet">
/// <item>Walker-driven auto-deposit reroute (snapshot activity →
/// pause → walk to bank → deposit → walk back → resume).</item>
/// <item>Per-realm currency naming (runic in particular varies by
/// BBS — v1 hardcodes the stock set; per-realm renames live on
/// the Phase 4 Settings → BBS tab).</item>
/// <item>RealmType-resolved bracket percentages — gate currently
/// hardcodes the Stock 17 / 34 / 67 starts (Phase 12).</item>
/// </list>
/// <para>
/// Master switch: <see cref="AutoActionDefaults.AutoGetCash"/>
/// (shared with the Settings → General toggle and the toolbar
/// Toggle command).
/// </para>
/// </remarks>
public sealed class CashManager : IDisposable
{
    /// <summary>LogService category — appears as <c>[Cash]</c> rows
    /// per dispatch + threshold fire.</summary>
    public const string LogCategory = "Cash";

    private readonly Func<CashSettings> _readSettings;
    private readonly Func<bool> _isEnabled;
    private readonly Func<InventorySnapshot> _getSnapshot;
    private readonly LogService? _log;
    private readonly IDisposable _groundSub;
    private readonly IDisposable _pickedUpSub;
    private readonly IDisposable _droppedSub;
    private readonly IDisposable _hiddenSub;
    private readonly IDisposable _noticeSub;
    private readonly IDisposable _killDropSub;
    private Terminal.LineExtractor? _lines;
    private string? _noticeBuffer;       // multi-line continuation
    private string? _noticeRawFirst;     // raw first row that started the buffer

    /// <summary>Recognised cash denomination words (case-insensitive).
    /// User's screenshot showed "silver nobles" / "copper farthings";
    /// expand by adding more entries here when realms use unique
    /// denomination words.</summary>
    private static readonly HashSet<string> CashDenominations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "copper", "silver", "gold", "platinum", "runic",
        };

    private Action<byte[]>? _wireSender;
    private Game.Inventory.AcquisitionGate? _gate;
    private readonly Dictionary<string, long> _held = new(StringComparer.OrdinalIgnoreCase);
    private bool _autoDepositFiredThisCrossing;
    private bool _disposed;

    // ----- Encumbrance-gated collection (ported from MudProxy) ----------
    // Per-currency in-flight projection, slot 0=copper..4=runic. A `get`
    // bumps the matching slot up, a `drop` down; the confirming
    // CashPickedUp / CashDropped line decays it back toward zero. While a
    // delta is alive, the gate budget projects the post-command coin weight
    // on top of the parser's snapshot — so a multi-currency batch evaluated
    // before the parser catches up (and a quick same-room redisplay) can't
    // over-collect past the configured encumbrance bracket. Stale entries
    // (parser missed the confirming line, or the MUD rejected the command)
    // self-clear after InFlightDeltaTimeoutMs so the projection can't pin
    // the budget against a phantom pending command forever.
    private readonly long[] _inFlightCoinDelta = new long[5];
    private readonly DateTime[] _inFlightCoinDeltaSetAt = new DateTime[5];
    private const int InFlightDeltaTimeoutMs = 60000;

    // Stock encumbrance bracket start percentages: None→Light at 17%,
    // Light→Medium at 34%, Medium→Heavy at 67%. FujinTerm has no RealmType
    // yet (Phase 12); these match InventoryManager's Stock assumption and
    // become realm-resolved when RealmType lands.
    private const int StockLightStartPct = 17;
    private const int StockMediumStartPct = 34;
    private const int StockHeavyStartPct = 67;

    /// <summary>Single-word currency names for the get / drop wire shape,
    /// indexed by slot (0=copper..4=runic) — same vocabulary the v1 collect
    /// path already sends.</summary>
    private static readonly string[] SlotCurrencyNames =
        { "copper", "silver", "gold", "platinum", "runic" };

    /// <summary>Fires whenever a CashOnGround line resolves the
    /// per-currency policy decision. Args: currency, count, decided
    /// action.</summary>
    public event Action<string, int, CashPolicy>? CashDispatched;

    /// <summary>Fires once when the authoritative held wealth crosses
    /// <see cref="CashSettings.AutoDepositIfWealthExceeds"/> or the held
    /// coin count crosses <see cref="CashSettings.AutoDepositIfCoinsExceed"/>
    /// — provided a bank / stash location is configured. Payload is the
    /// current wealth value (the game's <c>Wealth:</c> figure). Single-shot
    /// per crossing — re-arms only once BOTH gates fall back below their
    /// thresholds.</summary>
    public event Action<long>? AutoDepositRequested;

    /// <summary>Fires when the server confirms the player picked up coin
    /// (a <c>CashPickedUp</c> line) — auto-collected or manually <c>get</c>'d
    /// alike. Args: currency word, coin count. Lets the Session Stats tracker
    /// tally how much was gathered without re-parsing the wire.</summary>
    public event Action<string, int>? CoinCollected;

    public CashManager(
        MessageRouter router,
        Func<CashSettings> readSettings,
        Func<bool> isEnabled,
        Func<InventorySnapshot>? getSnapshot = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _readSettings = readSettings;
        _isEnabled = isEnabled;
        // No snapshot bound (or before an `i` parse) → the encumbrance gate
        // is inert and collection runs the v1 full-pickup path.
        _getSnapshot = getSnapshot ?? (static () => InventorySnapshot.Empty);
        _log = log;

        _groundSub   = router.Subscribe(KnownPatterns.CashOnGround,  OnCashOnGround);
        _pickedUpSub = router.Subscribe(KnownPatterns.CashPickedUp,  OnCashPickedUp);
        _droppedSub  = router.Subscribe(KnownPatterns.CashDropped,   OnCashDropped);
        // `hide N <coin>` is the stash-room verb — same tally semantics
        // as drop. Without this subscription, stashing decrements only
        // via item-hide (UserHides) which isn't currency-aware; the
        // held tally would go stale and AutoDeposit would misfire on
        // phantom wealth. Note for future inventory subsystem: its
        // UserHides handler must skip currency-shape item text so we
        // don't double-decrement here vs. there.
        _hiddenSub   = router.Subscribe(KnownPatterns.CashHidden,    OnCashHidden);
        _noticeSub   = router.Subscribe(KnownPatterns.YouNoticeRoom, OnYouNoticeRoom);
        _killDropSub = router.Subscribe(KnownPatterns.CashFromKill,  OnCashFromKill);
    }

    /// <summary>Bind the wire sender — typically the gate-wrapped
    /// engine pipeline from <c>MainWindowViewModel</c>.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>Bind the shared <see cref="Game.Inventory.AcquisitionGate"/>
    /// so collecting cash holds the walker until get-clear (the same gate
    /// the item engine feeds). Optional — when unbound the engine behaves
    /// exactly as v1 (no movement gate). Only the Collect path asserts;
    /// Discard/Ignore don't gate movement.</summary>
    public void SetAcquisitionGate(Game.Inventory.AcquisitionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
    }

    /// <summary>Current held count of <paramref name="currency"/> as
    /// observed via CashPickedUp / CashDropped lines since engine
    /// start. Resets on app close; not persisted (the wealth display
    /// is authoritative; we track here for the auto-deposit
    /// threshold).</summary>
    public long HeldCoin(string currency)
    {
        return _held.TryGetValue(currency, out long count) ? count : 0;
    }

    /// <summary>Reset held counts (called on profile load to drop
    /// the prior character's tallies). Also clears the in-flight coin
    /// projection so a pending get/drop from the prior session can't
    /// skew the new character's first gate evaluation.</summary>
    public void ResetTallies()
    {
        _held.Clear();
        _autoDepositFiredThisCrossing = false;
        Array.Clear(_inFlightCoinDelta, 0, _inFlightCoinDelta.Length);
        Array.Clear(_inFlightCoinDeltaSetAt, 0, _inFlightCoinDeltaSetAt.Length);
    }

    /// <summary>
    /// Re-evaluate state after a settings edit. Call this when the
    /// user changes a per-currency policy (e.g. flips Collect to
    /// Discard) or the auto-deposit threshold so the engine reacts
    /// immediately instead of waiting for the next CashPickedUp /
    /// CashOnGround line. Mirrors MudProxy's
    /// <c>CashManager.OnSettingsChanged</c> (CashManager.cs:479-497).
    /// </summary>
    public void OnSettingsChanged()
    {
        _log?.Debug(LogCategory, "settings changed — re-evaluating auto-deposit + discard");
        CheckAutoDeposit();
        AuditHeldForDiscard();
    }

    /// <summary>
    /// Walk held tallies; for any currency whose policy is Discard
    /// AND we hold &gt; 0, emit <c>drop &lt;amount&gt; &lt;type&gt;</c>
    /// (the confirmed MajorMUD syntax for currency drops). The
    /// CashDropped subscription decrements <c>_held</c> when the
    /// server confirms; we don't optimistically decrement so the
    /// audit retries on the next firing if the drop fails.
    /// </summary>
    private void AuditHeldForDiscard()
    {
        if (!_isEnabled()) return;
        CashSettings settings = _readSettings();
        foreach ((string currency, long count) in _held.ToList())
        {
            if (count <= 0) continue;
            if (ResolvePolicy(settings, currency) != CashPolicy.Discard) continue;
            _log?.Info(LogCategory, $"discard drop currency={currency} count={count}");
            Send($"drop {count} {currency}");
        }
    }

    // ----- handlers ----------------------------------------------------

    private void OnCashOnGround(MatchResult m)
    {
        if (!_isEnabled()) return;

        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;

        CashSettings settings = _readSettings();
        CashPolicy policy = ResolvePolicy(settings, currency);

        _log?.Info(LogCategory,
            $"on-ground currency={currency} count={count} policy={policy}");
        CashDispatched?.Invoke(currency, count, policy);

        switch (policy)
        {
            case CashPolicy.Collect:
                // Specific amount (not bare `get <currency>` which
                // would grab all available) so encumbrance / weight
                // tracking can do exact arithmetic instead of waiting
                // for the wealth display to refresh.
                CollectCoins(count, currency);
                break;
            case CashPolicy.Discard:
                // Don't pick up; don't react. The drop-held-discard
                // branch fires elsewhere (a held-cash audit).
                break;
            case CashPolicy.Ignore:
                break;
        }
    }

    /// <summary>
    /// Corpse-loot handler — "N &lt;currency&gt; drop to the ground."
    /// fires from <see cref="KnownPatterns.CashFromKill"/> after a
    /// monster dies. Funnels into the same per-currency policy
    /// dispatch as room-display cash so kill-loot honours the user's
    /// Collect / Discard / Ignore choices.
    /// </summary>
    private void OnCashFromKill(MatchResult m)
    {
        if (!_isEnabled()) return;

        // CashFromKill pattern uses 2 groups (count, currency) — the
        // shared ParseCashLine helper handles 3 groups (singular /
        // plural branch), so we parse inline here.
        if (m.Groups.Count < 2) return;
        if (!int.TryParse(m.Groups[0], out int count)) return;
        string currency = m.Groups[1].Trim();
        if (currency.Length == 0) return;
        if (!CashDenominations.Contains(currency)) return;
        currency = currency.ToLowerInvariant();

        CashSettings settings = _readSettings();
        CashPolicy policy = ResolvePolicy(settings, currency);

        _log?.Info(LogCategory,
            $"corpse-drop currency={currency} count={count} policy={policy}");
        CashDispatched?.Invoke(currency, count, policy);

        if (policy == CashPolicy.Collect)
            CollectCoins(count, currency);
    }

    private void OnCashPickedUp(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;

        AdjustHeld(currency, count);
        CoinCollected?.Invoke(currency, count);
        // Confirm our pending `get` — drain the matching in-flight delta so
        // the next gate evaluation works against the parser's fresh view.
        DecayInFlight(currency, count);
        CheckAutoDeposit();
        // Picked up a currency the user marked Discard (or settings
        // changed since the last audit) — drop it.
        AuditHeldForDiscard();
    }

    private void OnCashDropped(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;

        AdjustHeld(currency, -count);
        DecayInFlight(currency, -count);
        CheckAutoDeposit();
    }

    /// <summary>
    /// Stash-room confirmation handler — tally identically to drop.
    /// The <c>hide</c> wire shape is what stash-room visits use to
    /// dump excess coin / items; without this the auto-deposit
    /// threshold drifts stale after a stash run.
    /// </summary>
    private void OnCashHidden(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;

        _log?.Debug(LogCategory, $"hidden currency={currency} count={count}");
        AdjustHeld(currency, -count);
        DecayInFlight(currency, -count);
        CheckAutoDeposit();
    }

    /// <summary>
    /// Single-line "You notice <list> here." — splits the list and
    /// dispatches each recognised cash entry through the same
    /// per-currency policy path as <see cref="OnCashOnGround"/>.
    /// The multi-line wrap variant joins through the LineExtractor
    /// buffer (see <see cref="OnLine"/>) and feeds the same parse.
    /// </summary>
    private void OnYouNoticeRoom(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        DispatchYouNoticeList(m.Groups[0]);
    }

    /// <summary>
    /// Bind the per-session <see cref="Terminal.LineExtractor"/> so
    /// the manager can stitch wrapped "You notice" lines back
    /// together — same shape as the
    /// <see cref="Game.Combat.RoomEntityClassifier"/> fix for
    /// "Also here:".
    /// </summary>
    public void AttachLineExtractor(Terminal.LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    private void OnLine(Terminal.LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        string text = line.Text.TrimEnd();
        if (text.Length == 0) return;

        if (_noticeBuffer is not null)
        {
            _noticeBuffer = _noticeBuffer + " " + text;
            if (text.EndsWith(".", StringComparison.Ordinal))
            {
                string complete = _noticeBuffer;
                _noticeBuffer = null;
                _noticeRawFirst = null;
                ProcessYouNoticeMultiLine(complete);
            }
            return;
        }

        if (text.StartsWith("You notice ", StringComparison.Ordinal))
        {
            if (text.EndsWith(".", StringComparison.Ordinal))
            {
                // Single-line case — pattern subscription already
                // handles it; skip to avoid double-processing.
                return;
            }
            _noticeBuffer = text;
            _noticeRawFirst = line.Text;
        }
    }

    private void ProcessYouNoticeMultiLine(string completeLine)
    {
        // Strip "You notice " prefix and " here." suffix.
        const string prefix = "You notice ";
        if (!completeLine.StartsWith(prefix, StringComparison.Ordinal)) return;
        string body = completeLine[prefix.Length..].TrimEnd();
        const string suffix = " here.";
        if (body.EndsWith(suffix, StringComparison.Ordinal))
            body = body[..^suffix.Length];
        else if (body.EndsWith(".", StringComparison.Ordinal))
            body = body[..^1];
        DispatchYouNoticeList(body);
    }

    /// <summary>
    /// Split "X gold sovereigns, Y silver nobles, an item, ..."
    /// into entries; for each, decide if it's cash (count +
    /// recognised denomination) and dispatch through the
    /// per-currency policy. Non-cash entries are item references —
    /// silently skipped until an items subsystem ships.
    /// </summary>
    private void DispatchYouNoticeList(string list)
    {
        if (!_isEnabled()) return;
        CashSettings settings = _readSettings();

        foreach (string raw in list.Split(',', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) continue;
            if (!TryParseCashEntry(raw, out string? currency, out int count)) continue;

            CashPolicy policy = ResolvePolicy(settings, currency!);
            _log?.Info(LogCategory,
                $"you-notice cash currency={currency} count={count} policy={policy}");
            CashDispatched?.Invoke(currency!, count, policy);

            if (policy == CashPolicy.Collect)
                CollectCoins(count, currency!);
        }
    }

    /// <summary>
    /// Encumbrance-gated Collect dispatch for one ground currency, holding
    /// the walker via the shared <see cref="Game.Inventory.AcquisitionGate"/>
    /// until get-clear. Single funnel for all three collect sites (room
    /// display, corpse drop, "You notice" list) so the gate + acquisition
    /// note can't be missed.
    /// </summary>
    /// <remarks>
    /// With no <see cref="CashSettings.SkipCollectIfMakesLight"/> / Medium /
    /// Heavy flag set — or before an <c>i</c> parse populates encumbrance —
    /// this sends the full <c>get count currency</c> exactly as v1. When a
    /// gate flag is set and the <see cref="InventorySnapshot"/> has a known
    /// max weight, the pickup is clamped to the headroom below the
    /// configured bracket; with <see cref="CashSettings.DropSmallerForLarger"/>
    /// on, lower-value held coin is dropped 1:1 to free room for the
    /// higher-value pickup (encumbrance-neutral by construction). The
    /// in-flight projection threads multi-currency batches and quick
    /// re-displays so the budget reflects pickups already dispatched but not
    /// yet confirmed.
    /// </remarks>
    private void CollectCoins(int count, string currency)
    {
        CashSettings settings = _readSettings();
        int slot = SlotForCurrency(currency);
        InventorySnapshot snap = _getSnapshot();
        EncumbranceReading enc = snap.Encumbrance;

        bool gateActive = slot >= 0
            && enc.MaxWeight > 0
            && (settings.SkipCollectIfMakesLight
             || settings.SkipCollectIfMakesMedium
             || settings.SkipCollectIfMakesHeavy);

        if (!gateActive)
        {
            // v1 path — nothing to gate against, collect the full amount.
            _gate?.NoteGetSent();
            Send($"get {count} {currency}");
            return;
        }

        SweepStaleInFlight();

        long capWeight = ComputeCapWeight(settings, enc);
        CurrencyHoldings c = snap.Currency;
        long[] rawHeld = { c.Copper, c.Silver, c.Gold, c.Platinum, c.Runic };
        long rawTotal = 0;
        for (int k = 0; k < 5; k++) rawTotal += rawHeld[k];
        // nonCoinWeight from the AS-REPORTED currency totals so both sides of
        // the subtraction share the parser's baseline — keeps gear weight
        // exact regardless of the in-flight projection below.
        long nonCoinWeight = Math.Max(0, enc.CurrentWeight - rawTotal / 3);

        // Project the held counts we EXPECT once pending commands confirm.
        long[] held = new long[5];
        for (int k = 0; k < 5; k++)
            held[k] = Math.Max(0, rawHeld[k] + _inFlightCoinDelta[k]);

        long Budget()
        {
            long t = 0;
            for (int k = 0; k < 5; k++) t += held[k];
            long currentWeight = nonCoinWeight + t / 3;
            long headroom = capWeight - currentWeight;
            return headroom > 0 ? headroom * 3 : 0;
        }

        bool cascade = settings.DropSmallerForLarger;
        DateTime now = DateTime.UtcNow;

        long want = count;
        long freePickup = Math.Min(want, Budget());
        long swapNeeded = want - freePickup;
        long swapDone = 0;

        if (swapNeeded > 0 && cascade)
        {
            // Drop lower-value held coin (lowest first) 1:1 to free room for
            // the higher-value pickup. Equal coin counts exchanged → same
            // weight → encumbrance-neutral, so no Budget() recheck between
            // drops. Cascade is a deliberate trade-up: it sacrifices held
            // lower-value coin regardless of that currency's own policy.
            for (int j = 0; j < slot && swapNeeded > 0; j++)
            {
                if (held[j] <= 0) continue;
                long canSwap = Math.Min(swapNeeded, held[j]);
                _log?.Info(LogCategory,
                    $"cascade drop {canSwap} {SlotCurrencyNames[j]} for {canSwap} {currency}");
                Send($"drop {canSwap} {SlotCurrencyNames[j]}");
                held[j] -= canSwap;
                _inFlightCoinDelta[j] -= canSwap;
                _inFlightCoinDeltaSetAt[j] = now;
                swapDone += canSwap;
                swapNeeded -= canSwap;
            }
        }

        long totalPickup = freePickup + swapDone;
        if (totalPickup <= 0)
        {
            _log?.Info(LogCategory,
                $"collect skipped currency={currency} want={count} — at/over encumbrance gate");
            return;
        }

        _gate?.NoteGetSent();
        Send($"get {totalPickup} {currency}");
        _inFlightCoinDelta[slot] += totalPickup;
        _inFlightCoinDeltaSetAt[slot] = now;
    }

    /// <summary>Slot index (0=copper..4=runic) for a single-word currency,
    /// or -1 for an unrecognised denomination.</summary>
    private static int SlotForCurrency(string currency) =>
        currency.ToLowerInvariant() switch
        {
            "copper"   => 0,
            "silver"   => 1,
            "gold"     => 2,
            "platinum" => 3,
            "runic"    => 4,
            _          => -1,
        };

    /// <summary>
    /// Tightest encumbrance cap weight across the enabled gate flags. Each
    /// gate caps collection at the highest weight that still displays one
    /// bracket below it (so a Light gate keeps the character in None). No
    /// flags set → full <see cref="EncumbranceReading.MaxWeight"/>.
    /// </summary>
    private static long ComputeCapWeight(CashSettings s, EncumbranceReading enc)
    {
        long cap = enc.MaxWeight;
        if (s.SkipCollectIfMakesHeavy)
            cap = Math.Min(cap, GateBoundaryCap(enc.MaxWeight, StockHeavyStartPct));
        if (s.SkipCollectIfMakesMedium)
            cap = Math.Min(cap, GateBoundaryCap(enc.MaxWeight, StockMediumStartPct));
        if (s.SkipCollectIfMakesLight)
            cap = Math.Min(cap, GateBoundaryCap(enc.MaxWeight, StockLightStartPct));
        return cap;
    }

    /// <summary>
    /// Largest weight whose displayed percent (<c>floor(weight*100/max)</c>)
    /// stays strictly below <paramref name="thresholdPercent"/> — i.e. the
    /// most a character can carry without tipping into the next bracket.
    /// Integer inverse of the game's rounding: <c>(pct*max - 1) / 100</c>.
    /// </summary>
    private static long GateBoundaryCap(long maxWeight, long thresholdPercent) =>
        Math.Max(0, (thresholdPercent * maxWeight - 1) / 100);

    /// <summary>
    /// Drain the matching in-flight delta toward zero by an observed coin
    /// change that agrees with the delta's sign (a confirmed pickup against
    /// a pending get, or a confirmed drop against a pending drop). A
    /// sign-disagreeing change (a manual get/give while the opposite command
    /// was in flight) means the projection is no longer trustworthy — zero
    /// the slot and fall back to the parser's snapshot.
    /// </summary>
    private void DecayInFlight(string currency, int observedDelta)
    {
        int slot = SlotForCurrency(currency);
        if (slot < 0) return;
        long d = _inFlightCoinDelta[slot];
        if (d == 0 || observedDelta == 0) return;

        if (d > 0 && observedDelta > 0)
            _inFlightCoinDelta[slot] = Math.Max(0, d - observedDelta);
        else if (d < 0 && observedDelta < 0)
            _inFlightCoinDelta[slot] = Math.Min(0, d - observedDelta);
        else
            _inFlightCoinDelta[slot] = 0;

        if (_inFlightCoinDelta[slot] == 0)
            _inFlightCoinDeltaSetAt[slot] = default;
    }

    /// <summary>Reset any in-flight delta whose confirming line never
    /// arrived within <see cref="InFlightDeltaTimeoutMs"/> so the projection
    /// can't pin the budget against a phantom pending command.</summary>
    private void SweepStaleInFlight()
    {
        DateTime now = DateTime.UtcNow;
        for (int k = 0; k < 5; k++)
        {
            if (_inFlightCoinDelta[k] != 0
             && _inFlightCoinDeltaSetAt[k] != default
             && (now - _inFlightCoinDeltaSetAt[k]).TotalMilliseconds > InFlightDeltaTimeoutMs)
            {
                _inFlightCoinDelta[k] = 0;
                _inFlightCoinDeltaSetAt[k] = default;
            }
        }
    }

    /// <summary>Recognise <c>"N {denomination} ..."</c> as cash —
    /// requires a leading integer + the second word being a
    /// <see cref="CashDenominations"/> entry. Singular form
    /// <c>"a gold piece"</c> is also tolerated (count = 1).</summary>
    private static bool TryParseCashEntry(string raw, out string? currency, out int count)
    {
        currency = null;
        count = 0;
        string[] words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return false;

        // "a <denomination> ..." singular variant
        if (string.Equals(words[0], "a", StringComparison.OrdinalIgnoreCase)
         && CashDenominations.Contains(words[1]))
        {
            currency = words[1].ToLowerInvariant();
            count = 1;
            return true;
        }

        // "N <denomination> ..." plural variant
        if (int.TryParse(words[0], out int n) && words.Length >= 2
         && CashDenominations.Contains(words[1]))
        {
            currency = words[1].ToLowerInvariant();
            count = n;
            return true;
        }

        return false;
    }

    /// <summary>Parse (count, currency) from a cash line match.
    /// Returns count=1 for singular form ("a gold piece") and the
    /// captured digit for plural form.</summary>
    private static (string? Currency, int Count) ParseCashLine(MatchResult m)
    {
        // Singular branch: groups[0]="currency" populated, groups[1]+ empty.
        // Plural branch: groups[1]="count", groups[2]="currency2".
        if (m.Groups.Count == 0) return (null, 0);
        string? currency;
        int count;
        if (!string.IsNullOrEmpty(m.Groups[0]))
        {
            currency = m.Groups[0].Trim();
            count = 1;
        }
        else if (m.Groups.Count >= 3
                 && int.TryParse(m.Groups[1], out int n))
        {
            currency = m.Groups[2].Trim();
            count = n;
        }
        else
        {
            return (null, 0);
        }
        return (string.IsNullOrEmpty(currency) ? null : currency, count);
    }

    private static CashPolicy ResolvePolicy(CashSettings s, string currency)
    {
        return currency.ToLowerInvariant() switch
        {
            "copper"   => s.CopperPolicy,
            "silver"   => s.SilverPolicy,
            "gold"     => s.GoldPolicy,
            "platinum" => s.PlatinumPolicy,
            "runic"    => s.RunicPolicy,
            _          => CashPolicy.Ignore,    // unknown currency name → don't touch
        };
    }

    private void AdjustHeld(string currency, int delta)
    {
        if (!_held.TryGetValue(currency, out long current)) current = 0;
        long next = current + delta;
        if (next < 0) next = 0;
        _held[currency] = next;
    }

    /// <summary>
    /// Re-evaluate the auto-deposit gates against the latest authoritative
    /// <see cref="InventorySnapshot"/>. Wired to
    /// <c>InventoryManager.Changed</c> so a wealth swing the CashManager's
    /// own patterns never observe — a buy or sell — still arms the gate, and
    /// so a get / drop the snapshot processes after our pattern handler
    /// (subscription-order dependent) is re-checked once the snapshot is
    /// fresh.
    /// </summary>
    public void OnInventoryChanged() => CheckAutoDeposit();

    private void CheckAutoDeposit()
    {
        CashSettings settings = _readSettings();
        long wealthThreshold = settings.AutoDepositIfWealthExceeds;
        long coinThreshold = settings.AutoDepositIfCoinsExceed;
        // Both gates off → auto-deposit disarmed entirely.
        if (wealthThreshold <= 0 && coinThreshold <= 0) return;

        // Location precondition: with no bank / stash room picked there's
        // nowhere to detour, so the gates can't arm (a fired event with no
        // reroute destination is meaningless). Both a gate AND a location are
        // required — matches the Settings → Cash auto-deposit contract.
        if (string.IsNullOrEmpty(settings.BankRoomKey)) return;

        // Authoritative holdings: wealth gate against the game's "Wealth:"
        // value (TotalCopperValue), coin gate against the raw coin count —
        // not the local pickup tally, which never sees the `i`-seeded
        // starting balance or buy / sell conversions.
        CurrencyHoldings held = _getSnapshot().Currency;
        long wealthValue = held.TotalCopperValue;
        long coinCount = held.TotalCoinCount;

        bool wealthGate = wealthThreshold > 0 && wealthValue > wealthThreshold;
        bool coinGate = coinThreshold > 0 && coinCount > coinThreshold;

        // OR logic: either gate firing triggers the deposit. The single-fire
        // guard re-arms only once BOTH gates fall back below their thresholds,
        // so a deposit that clears wealth but not coin count doesn't re-fire.
        if ((wealthGate || coinGate) && !_autoDepositFiredThisCrossing)
        {
            _autoDepositFiredThisCrossing = true;
            _log?.Info(LogCategory,
                $"auto-deposit triggered wealth={wealthValue} (gate={wealthThreshold}) " +
                $"coins={coinCount} (gate={coinThreshold})");
            AutoDepositRequested?.Invoke(wealthValue);
        }
        else if (!wealthGate && !coinGate && _autoDepositFiredThisCrossing)
        {
            _autoDepositFiredThisCrossing = false;
            _log?.Debug(LogCategory,
                $"auto-deposit re-armed wealth={wealthValue} coins={coinCount}");
        }
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _groundSub.Dispose();
        _pickedUpSub.Dispose();
        _droppedSub.Dispose();
        _hiddenSub.Dispose();
        _noticeSub.Dispose();
        _killDropSub.Dispose();
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}
