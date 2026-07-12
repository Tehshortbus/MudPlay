using System.Text;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Cash;

// Per-currency cash pickup / discard engine. Subscribes to CashOnGround,
// CashPickedUp, CashDropped, and CashFromKill (corpse loot after a monster
// dies), and dispatches based on the per-currency CashPolicy in CashSettings.
//
// Scope:
//   - CashOnGround → policy dispatch. Collect → "get <count> <coin>" with the
//     exact observed count (specific amounts keep encumbrance / weight tracking
//     deterministic). Discard / Ignore → no action.
//   - Encumbrance gates. With a SkipCollectIfMakesLight / Medium / Heavy flag
//     set and a parsed InventorySnapshot, pickups are clamped to the headroom
//     below the configured bracket. DropSmallerForLarger trades held lower-value
//     coin 1:1 to make room for the higher-value pickup. A per-currency
//     in-flight delta (60s timeout) projects pickups already dispatched but not
//     yet confirmed so multi-coin batches and quick re-displays can't
//     over-collect.
//   - CashPickedUp / CashDropped → tally update. Held per-currency counts
//     exposed via HeldCoin feed the stash-room and discard paths.
//   - AutoDeposit trigger. The gates read the authoritative
//     InventorySnapshot.Currency (the 'i'-seeded, delta-tracked holdings — not
//     the local pickup tally): wealth gate against TotalCopperValue (the game's
//     Wealth: line), coin gate against TotalCoinCount. Either crossing fires
//     AutoDepositRequested once per crossing, but only when a bank / stash
//     location (BankRoomKey) is configured — no location, no reroute
//     destination, no fire. Re-evaluated on OnInventoryChanged so buy / sell
//     wealth swings (which this engine's own patterns don't observe) still arm
//     the gate. Subscribers (the walker reroute) decide what to do — this layer
//     only signals.
//
// Per-realm runic naming: the runic denomination's word varies per-BBS
// (Settings → BBS tab). CurrencyNaming resolves the active word; every
// denomination check here folds through _naming.IsRunic, and outgoing
// drop/get commands carry the wire word so the server accepts them.
//
// Not yet handled:
//   - Walker-driven auto-deposit reroute (snapshot activity → pause → walk to
//     bank → deposit → walk back → resume).
//   - Realm-resolved bracket percentages — the gate currently hardcodes the
//     Stock 17 / 34 / 67 starts.
//
// Master switch: AutoActionDefaults.AutoGetCash (shared with the Settings →
// General toggle and the toolbar Toggle command).
public sealed class CashManager : IDisposable
{
    // LogService category — appears as [Cash] rows per dispatch + threshold fire.
    public const string LogCategory = "Cash";

    private readonly Func<CashSettings> _readSettings;
    private readonly Func<bool> _isEnabled;
    private readonly Func<InventorySnapshot> _getSnapshot;
    private readonly Func<bool> _isPeekSuppressed;
    private readonly LogService? _log;
    private readonly CurrencyNaming _naming;
    private readonly IDisposable _groundSub;
    private readonly IDisposable _pickedUpSub;
    private readonly IDisposable _droppedSub;
    private readonly IDisposable _hiddenSub;
    private readonly IDisposable _noticeSub;
    private readonly IDisposable _killDropSub;
    private Terminal.LineExtractor? _lines;
    private string? _noticeBuffer;       // multi-line continuation
    private string? _noticeRawFirst;     // raw first row that started the buffer

    // The four stable single-word denominations (case-insensitive). The fifth,
    // runic, is recognised separately through _naming because a board can rename
    // the runic word per-BBS — IsCashWord folds both checks.
    private static readonly HashSet<string> StableDenominations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "copper", "silver", "gold", "platinum",
        };

    // True when word names any cash denomination — one of the stable four or
    // the active board's runic word (stock "runic" included).
    private bool IsCashWord(string word) =>
        StableDenominations.Contains(word) || _naming.IsRunic(word);

    private Action<byte[]>? _wireSender;
    private Game.Inventory.AcquisitionGate? _gate;
    private readonly Dictionary<string, long> _held = new(StringComparer.OrdinalIgnoreCase);
    private bool _autoDepositFiredThisCrossing;
    // When a fired gate crossing can't complete a deposit (aborted reroute) the
    // single-fire guard re-arms, but retries are held off until this instant so a
    // persistently-unreachable bank can't thrash the movement engine on every
    // inventory line. See NotifyAutoDepositAborted.
    private DateTime _autoDepositRetryNotBefore;
    private const int AutoDepositRetryCooldownMs = 60_000;
    // Clock behind the retry cooldown. Overridable in tests so the re-arm path
    // can be exercised without a real 60-second wait; production reads the wall
    // clock.
    internal Func<DateTime> AutoDepositClock { get; set; } = static () => DateTime.UtcNow;
    private bool _disposed;

    // ----- Encumbrance-gated collection --------------------------------
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
    // Light→Medium at 34%, Medium→Heavy at 67%. These are the current fixed
    // values and match InventoryManager's Stock assumption.
    private const int StockLightStartPct = 17;
    private const int StockMediumStartPct = 34;
    private const int StockHeavyStartPct = 67;

    // Single-word currency names for the get / drop wire shape, indexed by slot
    // (0=copper..4=runic) — same vocabulary the collect path already sends.
    private static readonly string[] SlotCurrencyNames =
        { "copper", "silver", "gold", "platinum", "runic" };

    // Fires whenever a CashOnGround line resolves the per-currency policy
    // decision. Args: currency, count, decided action.
    public event Action<string, int, CashPolicy>? CashDispatched;

    // Fires once when the authoritative held wealth crosses
    // AutoDepositIfWealthExceeds or the held coin count crosses
    // AutoDepositIfCoinsExceed — provided a bank / stash location is configured.
    // Payload is the current wealth value (the game's Wealth: figure).
    // Single-shot per crossing — re-arms only once BOTH gates fall back below
    // their thresholds.
    public event Action<long>? AutoDepositRequested;

    // Fires when the server confirms the player picked up coin (a CashPickedUp
    // line) — auto-collected or manually get'd alike. Args: currency word, coin
    // count. Lets the Session Stats tracker tally how much was gathered without
    // re-parsing the wire.
    public event Action<string, int>? CoinCollected;

    public CashManager(
        MessageRouter router,
        Func<CashSettings> readSettings,
        Func<bool> isEnabled,
        Func<InventorySnapshot>? getSnapshot = null,
        Func<bool>? isPeekSuppressed = null,
        LogService? log = null,
        CurrencyNaming? naming = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _readSettings = readSettings;
        _isEnabled = isEnabled;
        // Resolves the per-BBS runic word; unbound (tests) falls back to stock
        // "runic" so the stable-realm behaviour is unchanged.
        _naming = naming ?? new CurrencyNaming();
        // No snapshot bound (or before an `i` parse) → the encumbrance gate
        // is inert and collection runs the full-pickup path.
        _getSnapshot = getSnapshot ?? (static () => InventorySnapshot.Empty);
        // Null when unbound (tests) → never a peek. A `look <dir>` peek renders
        // a full room display, so gate the "You notice" collect path on it.
        _isPeekSuppressed = isPeekSuppressed ?? (static () => false);
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

    // Bind the wire sender — typically the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the shared AcquisitionGate so collecting cash holds the walker until
    // get-clear (the same gate the item engine feeds). Optional — when unbound
    // the engine doesn't gate movement. Only the Collect path asserts;
    // Discard/Ignore don't gate movement.
    public void SetAcquisitionGate(Game.Inventory.AcquisitionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
    }

    // Current held count of currency as observed via CashPickedUp / CashDropped
    // lines since engine start. Resets on app close; not persisted (the wealth
    // display is authoritative; tracked here for the auto-deposit threshold).
    public long HeldCoin(string currency)
    {
        return _held.TryGetValue(currency, out long count) ? count : 0;
    }

    // Reset held counts (called on profile load to drop the prior character's
    // tallies). Also clears the in-flight coin projection so a pending get/drop
    // from the prior session can't skew the new character's first gate
    // evaluation.
    public void ResetTallies()
    {
        _held.Clear();
        _autoDepositFiredThisCrossing = false;
        _autoDepositRetryNotBefore = default;
        Array.Clear(_inFlightCoinDelta, 0, _inFlightCoinDelta.Length);
        Array.Clear(_inFlightCoinDeltaSetAt, 0, _inFlightCoinDeltaSetAt.Length);
    }

    // Re-evaluate state after a settings edit. Call this when the user changes a
    // per-currency policy (e.g. flips Collect to Discard) or the auto-deposit
    // threshold so the engine reacts immediately instead of waiting for the next
    // CashPickedUp / CashOnGround line.
    public void OnSettingsChanged()
    {
        _log?.Debug(LogCategory, "settings changed — re-evaluating auto-deposit + discard");
        CheckAutoDeposit();
        AuditHeldForDiscard();
    }

    // For any currency whose policy is Discard AND we hold > 0, emit
    // "drop <amount> <type>" (the MajorMUD syntax for currency drops).
    //
    // Holdings come from the authoritative InventorySnapshot.Currency, NOT the
    // local pickup tally (_held): the tally only counts coin observed via
    // CashPickedUp / CashDropped this session, so a carried-over / starting
    // balance it never saw (the exact case a retroactive Collect→Discard flip
    // must handle) would read as zero and never drop. The snapshot is the
    // 'i'-seeded, delta-tracked truth. We still max in the tally so a fresh
    // pickup drops immediately even if the parser's snapshot hasn't yet applied
    // the confirming line. The CashDropped subscription decrements state when
    // the server confirms; we don't optimistically decrement so the audit
    // retries on the next firing if the drop fails.
    private void AuditHeldForDiscard()
    {
        if (!_isEnabled()) return;
        CashSettings settings = _readSettings();
        CurrencyHoldings snap = _getSnapshot().Currency;
        AuditDenominationForDiscard(settings, "copper",   snap.Copper);
        AuditDenominationForDiscard(settings, "silver",   snap.Silver);
        AuditDenominationForDiscard(settings, "gold",     snap.Gold);
        AuditDenominationForDiscard(settings, "platinum", snap.Platinum);
        // Wire runic word so the emitted `drop N <word>` matches what the server
        // accepts; ResolvePolicy/HeldCoin recognise it via _naming.IsRunic.
        AuditDenominationForDiscard(settings, _naming.RunicName, snap.Runic);
    }

    private void AuditDenominationForDiscard(CashSettings settings, string currency, long snapshotCount)
    {
        if (ResolvePolicy(settings, currency) != CashPolicy.Discard) return;
        long count = Math.Max(snapshotCount, HeldCoin(currency));
        if (count <= 0) return;
        _log?.Info(LogCategory, $"discard drop currency={currency} count={count}");
        Send($"drop {count} {currency}");
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

    // Corpse-loot handler — "N <currency> drop to the ground." fires from
    // CashFromKill after a monster dies. Funnels into the same per-currency
    // policy dispatch as room-display cash so kill-loot honours the user's
    // Collect / Discard / Ignore choices.
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
        if (!IsCashWord(currency)) return;
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

    // Stash-room confirmation handler — tally identically to drop. The hide wire
    // shape is what stash-room visits use to dump excess coin / items; without
    // this the auto-deposit threshold drifts stale after a stash run.
    private void OnCashHidden(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;

        _log?.Debug(LogCategory, $"hidden currency={currency} count={count}");
        AdjustHeld(currency, -count);
        DecayInFlight(currency, -count);
        CheckAutoDeposit();
    }

    // Single-line "You notice <list> here." — splits the list and dispatches
    // each recognised cash entry through the same per-currency policy path as
    // OnCashOnGround. The multi-line wrap variant joins through the LineExtractor
    // buffer (see OnLine) and feeds the same parse.
    private void OnYouNoticeRoom(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        DispatchYouNoticeList(m.Groups[0]);
    }

    // Bind the per-session LineExtractor so the manager can stitch wrapped
    // "You notice" lines back together — the same "Also here:" wrap problem
    // the room-entity classifier solves for its own list.
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

    // Split "X gold sovereigns, Y silver nobles, an item, ..." into entries;
    // for each, decide if it's cash (count + recognised denomination) and
    // dispatch through the per-currency policy. Non-cash entries are item
    // references — silently skipped, the item engine handles those.
    private void DispatchYouNoticeList(string list)
    {
        if (!_isEnabled()) return;
        // "You notice" is a room-display line, so a look-direction peek renders
        // it for the adjacent room. Collecting against a room we never entered
        // sends get commands into empty air; skip while the peek window is armed.
        if (_isPeekSuppressed())
        {
            _log?.Debug(LogCategory, "skipped you-notice cash (look-direction peek)");
            return;
        }
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

    // Encumbrance-gated Collect dispatch for one ground currency, holding the
    // walker via the shared AcquisitionGate until get-clear. Single funnel for
    // all three collect sites (room display, corpse drop, "You notice" list) so
    // the gate + acquisition note can't be missed.
    //
    // With no SkipCollectIfMakesLight / Medium / Heavy flag set — or before an
    // `i` parse populates encumbrance — this sends the full `get count currency`
    // ungated. When a gate flag is set and the InventorySnapshot has a known max
    // weight, the pickup is clamped to the headroom below the configured
    // bracket; with DropSmallerForLarger on, lower-value held coin is dropped 1:1
    // to free room for the higher-value pickup (encumbrance-neutral by
    // construction). The in-flight projection threads multi-currency batches and
    // quick re-displays so the budget reflects pickups already dispatched but not
    // yet confirmed.
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
            // Ungated path — nothing to gate against, collect the full amount.
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

    // Slot index (0=copper..4=runic) for a single-word currency, or -1 for an
    // unrecognised denomination.
    private int SlotForCurrency(string currency)
    {
        if (_naming.IsRunic(currency)) return 4;
        return currency.ToLowerInvariant() switch
        {
            "copper"   => 0,
            "silver"   => 1,
            "gold"     => 2,
            "platinum" => 3,
            _          => -1,
        };
    }

    // Tightest encumbrance cap weight across the enabled gate flags. Each gate
    // caps collection at the highest weight that still displays one bracket below
    // it (so a Light gate keeps the character in None). No flags set → full
    // MaxWeight.
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

    // Largest weight whose displayed percent (floor(weight*100/max)) stays
    // strictly below thresholdPercent — i.e. the most a character can carry
    // without tipping into the next bracket. Integer inverse of the game's
    // rounding: (pct*max - 1) / 100.
    private static long GateBoundaryCap(long maxWeight, long thresholdPercent) =>
        Math.Max(0, (thresholdPercent * maxWeight - 1) / 100);

    // Drain the matching in-flight delta toward zero by an observed coin change
    // that agrees with the delta's sign (a confirmed pickup against a pending
    // get, or a confirmed drop against a pending drop). A sign-disagreeing change
    // (a manual get/give while the opposite command was in flight) means the
    // projection is no longer trustworthy — zero the slot and fall back to the
    // parser's snapshot.
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

    // Reset any in-flight delta whose confirming line never arrived within
    // InFlightDeltaTimeoutMs so the projection can't pin the budget against a
    // phantom pending command.
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

    // Recognise "N {denomination} ..." as cash — requires a leading integer + the
    // second word being a CashDenominations entry. Singular form "a gold piece"
    // is also tolerated (count = 1).
    private bool TryParseCashEntry(string raw, out string? currency, out int count)
    {
        currency = null;
        count = 0;
        string[] words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return false;

        // "a <denomination> ..." singular variant
        if (string.Equals(words[0], "a", StringComparison.OrdinalIgnoreCase)
         && IsCashWord(words[1]))
        {
            currency = words[1].ToLowerInvariant();
            count = 1;
            return true;
        }

        // "N <denomination> ..." plural variant
        if (int.TryParse(words[0], out int n) && words.Length >= 2
         && IsCashWord(words[1]))
        {
            currency = words[1].ToLowerInvariant();
            count = n;
            return true;
        }

        return false;
    }

    // Parse (count, currency) from a cash line match. Returns count=1 for
    // singular form ("a gold piece") and the captured digit for plural form.
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

    private CashPolicy ResolvePolicy(CashSettings s, string currency)
    {
        if (_naming.IsRunic(currency)) return s.RunicPolicy;
        return currency.ToLowerInvariant() switch
        {
            "copper"   => s.CopperPolicy,
            "silver"   => s.SilverPolicy,
            "gold"     => s.GoldPolicy,
            "platinum" => s.PlatinumPolicy,
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

    // Re-evaluate the auto-deposit gates against the latest authoritative
    // InventorySnapshot. Wired to InventoryManager.Changed so a wealth swing the
    // CashManager's own patterns never observe — a buy or sell — still arms the
    // gate, and so a get / drop the snapshot processes after our pattern handler
    // (subscription-order dependent) is re-checked once the snapshot is fresh.
    public void OnInventoryChanged()
    {
        CheckAutoDeposit();
        // Re-audit discard against the fresh holdings too. A bank withdrawal /
        // buy / sell changes the coin snapshot without ever emitting a
        // CashPickedUp line, so without this a currency marked Discard whose
        // balance grew off-pattern (the reported "withdrew 9 copper, Discard set,
        // never dropped") would sit un-audited until the next ground pickup.
        AuditHeldForDiscard();
    }

    // Called by the auto-deposit reroute subscriber when a fired gate crossing
    // couldn't complete a deposit — no loop / lair to reroute, an unreachable or
    // unrecognised bank, or a failed detour walk. Re-arms the single-fire guard
    // so a later inventory change can retry; without it the guard stays latched
    // (it clears only when wealth falls below threshold, which an aborted deposit
    // never reaches) and auto-deposit is wedged for the rest of the session. A
    // cooldown keeps a persistently-unreachable bank from re-firing on every
    // subsequent inventory line and thrashing the movement engine.
    public void NotifyAutoDepositAborted()
    {
        _autoDepositFiredThisCrossing = false;
        _autoDepositRetryNotBefore = AutoDepositClock().AddMilliseconds(AutoDepositRetryCooldownMs);
        _log?.Debug(LogCategory,
            $"auto-deposit re-armed after aborted reroute (retry held {AutoDepositRetryCooldownMs / 1000}s)");
    }

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
            // A recent reroute aborted without depositing; hold off re-firing
            // until the cooldown elapses so an unreachable bank can't thrash the
            // movement engine on every inventory line.
            if (AutoDepositClock() < _autoDepositRetryNotBefore) return;

            _autoDepositFiredThisCrossing = true;
            _log?.Info(LogCategory,
                $"auto-deposit triggered wealth={wealthValue} (gate={wealthThreshold}) " +
                $"coins={coinCount} (gate={coinThreshold})");
            AutoDepositRequested?.Invoke(wealthValue);
        }
        else if (!wealthGate && !coinGate)
        {
            // Both gates below threshold: this crossing is over. Drop any pending
            // retry hold from an earlier aborted reroute (unconditionally — the
            // abort already un-latched the guard, so keying off it would strand
            // the cooldown), and reset the single-fire guard if it was latched.
            _autoDepositRetryNotBefore = default;
            if (_autoDepositFiredThisCrossing)
            {
                _autoDepositFiredThisCrossing = false;
                _log?.Debug(LogCategory,
                    $"auto-deposit re-armed wealth={wealthValue} coins={coinCount}");
            }
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
