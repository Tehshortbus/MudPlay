using System.Text;
using Avalonia.Threading;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Cash;

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
//   - AutoDeposit trigger. The gates read the authoritative
//     InventorySnapshot.Currency (the 'i'-seeded, delta-tracked holdings):
//     wealth gate against TotalCopperValue (the game's
//     Wealth: line), coin gate against TotalCoinCount. Either crossing fires
//     AutoDepositRequested once per crossing, but only when a bank / stash
//     location (BankRoomKey) is configured — no location, no reroute
//     destination, no fire. Re-evaluated on OnInventoryChanged so buy / sell
//     wealth swings (which this engine's own patterns don't observe) still arm
//     the gate. Subscribers (the walker reroute) decide what to do — this layer
//     only signals.
//
// Per-realm runic naming: the runic denomination's word varies per realm
// (Settings → BBS + Display, the realm's settings). CurrencyNaming resolves the active word; every
// denomination check here folds through _naming.IsRunic, and outgoing
// drop/get commands carry the wire word so the server accepts them.
//
// Not yet handled:
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
    private readonly Func<bool> _collectAfterCombatFinished;
    private readonly Func<bool> _hasEngageableHostiles;
    private readonly Func<InventorySnapshot> _getSnapshot;
    private readonly Func<bool> _isPeekSuppressed;
    // Defers an action one dispatch tick. Used so a room-survey ("You notice ...")
    // collect decision is made AFTER the room's later "Also here:" line has been
    // parsed and the combat gate is current. Default runs synchronously.
    private readonly Action<Action> _post;
    private readonly LogService? _log;
    private readonly CurrencyNaming _naming;
    private readonly Func<string, bool>? _isKnownItem;
    private readonly IDisposable _groundSub;
    private readonly IDisposable _pickedUpSub;
    private readonly IDisposable _droppedSub;
    private readonly IDisposable _dropRefusedSub;
    private readonly IDisposable _trainedSub;
    private readonly IDisposable _trainedNextSub;
    private readonly IDisposable _hiddenSub;
    private readonly IDisposable _noticeSub;
    private readonly IDisposable _killDropSub;
    private Terminal.LineExtractor? _lines;
    private string? _noticeBuffer;       // multi-line continuation
    private string? _noticeRawFirst;     // raw first row that started the buffer

    // The four stable single-word denominations (case-insensitive). The fifth,
    // runic, is recognised separately through _naming because a board can rename
    // the runic word per realm — IsCashWord folds both checks.
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
    private Game.Inventory.RoomRedisplayCoordinator? _redisplay;

    // Collect-after-combat. When CashSettings.CollectAfterCombatFinished is set (the
    // shared Cash + Items timing toggle) and the room still holds engageable
    // hostiles, mid-fight cash is NOT collected — a get landed between two kills eats
    // the next round's pre-attack. We deliberately DON'T queue the observed amounts:
    // a kill's "N drop to the ground" delta and the room's running "You notice N"
    // total are the same coins, and every post-kill re-render re-observes them, so
    // replaying stored amounts double-counts into a flood of gets/drops (and cascade
    // swaps). Instead we just note that cash is waiting; once the room clears
    // (OnRoomObserved) we re-display it with a `look` and collect from the
    // authoritative ground contents — one pass, and correct even if someone else
    // grabbed the pile first. A room change clears the flag: the coin belonged to a
    // room we've left.
    private bool _cashPendingAfterCombat;

    // Per-currency high-water mark of the ground pile we've already collected (or
    // considered and skipped as gated) this room visit — the "decide once" latch. A
    // room re-renders several times after combat (the game's own re-display + our
    // post-combat `look`), and without this each re-render re-ran the collect: with
    // drop-smaller-for-larger it re-swapped the same pile, shedding coin on the
    // redundant attempts (report paradigm-20260804-143020). Keyed by amount, not just
    // presence, so a re-render of the SAME (or smaller) pile is a no-op, but a `search`
    // that reveals a LARGER pile than we took re-arms collection (report
    // paradigm-20260811-065736 — coins a post-combat search surfaced went uncollected).
    // Cleared on room change; a fresh corpse-drop of a currency removes it so
    // post-new-combat cash is collected again.
    private readonly Dictionary<string, int> _collectedGround = new(StringComparer.OrdinalIgnoreCase);

    // True when this currency already had a collect issued this room visit for at
    // least `count` coins — a passive re-render of the same/smaller pile skips, a
    // reveal of more coins (search / fresh drop) falls through and re-collects.
    private bool AlreadyCollected(string currency, int count)
    {
        if (!_collectedGround.TryGetValue(currency, out int had) || had < count) return false;
        if (!GetWentUnanswered(currency)) return true;
        // The pile is still on the floor a moment after we asked for it and the game
        // never said we picked it up: the get was thrown away (a boss's death can
        // leave the room unable to act for a moment). Un-claim it and ask again.
        _log?.Info(LogCategory,
            $"collect currency={currency} — the get was never answered and the coins are still here, asking again");
        _collectedGround.Remove(currency);
        return false;
    }

    // The `get` sent for each currency that the game has not yet answered with a
    // pickup line: when it went out, and how often it has been asked again.
    private readonly Dictionary<string, (DateTime SentAt, int Retries)> _unansweredGets =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxGetRetries = 1;
    // A room display this soon after the get may have been drawn before the get ran.
    private static readonly TimeSpan GetAnswerTime = TimeSpan.FromSeconds(1);

    // Only a get sent around a death that stalls the room counts: any other get the
    // game didn't answer with a pickup was refused for a reason asking again won't
    // change (too heavy to carry it), and a swap of smaller coin for larger would
    // shed coin on each repeat.
    private bool GetWentUnanswered(string currency) =>
        _unansweredGets.TryGetValue(currency, out (DateTime SentAt, int Retries) get)
        && get.Retries < MaxGetRetries
        && _unansweredClock() - get.SentAt >= GetAnswerTime
        && (get.SentAt - _deathStallAt).Duration() <= DeathStallReach;

    private readonly Func<DateTime> _unansweredClock;

    // When a monster last died casting a silent spell that leaves the room unable
    // to act for a moment (FireTempDeathResponse). The coin get goes out on the
    // drop line, just before the death is recognised, hence the reach both ways.
    private DateTime _deathStallAt = DateTime.MinValue;
    private static readonly TimeSpan DeathStallReach = TimeSpan.FromSeconds(5);

    public void NoteDeathStall() => _deathStallAt = _unansweredClock();

    // A coin get is out that the game has not answered with a pickup line.
    public bool HasUnansweredGet => _unansweredGets.Count > 0;

    private void NoteGetSent(string currency)
    {
        int retries = _unansweredGets.TryGetValue(currency, out (DateTime SentAt, int Retries) earlier)
            ? earlier.Retries + 1 : 0;
        _unansweredGets[currency] = (_unansweredClock(), retries);
    }

    // Record a collect of `count` of this currency this visit, keeping the high mark.
    private void MarkCollected(string currency, int count)
    {
        if (!_collectedGround.TryGetValue(currency, out int had) || count > had)
            _collectedGround[currency] = count;
    }

    // Holds the walker across the post-combat `look` round-trip (server latency
    // exceeds the gate's settle window, so the settle can't cover it). Fires once to
    // release the deferred-hold if the re-displayed room turned out to hold no cash;
    // a successful collect re-arms the gate's own settle window before this elapses.
    private DispatcherTimer? _resurveyReleaseTimer;
    // Look round-trip hold. Long enough for
    // the room re-display to arrive and drive the collect.
    private const int ResurveyReleaseMs = 1500;

    private bool _autoDepositFiredThisCrossing;
    // When a fired gate crossing can't complete a deposit (aborted reroute) the
    // single-fire guard re-arms, but retries are held off until this instant so a
    // persistently-unreachable bank can't thrash the movement engine on every
    // inventory line. See NotifyAutoDepositAborted.
    private DateTime _autoDepositRetryNotBefore;
    private const int AutoDepositRetryCooldownMs = 60_000;
    private readonly Func<DateTime> _autoDepositClock;

    // True while standing in a marked stash room with a loop / lair running. Coin
    // auto-collect is suppressed there so a search's "You notice …" survey can't
    // re-expose and re-grab the pile the pass-through stash just hid (report
    // paradigm-20260819-121516). Wired by AppServices to AutoDeposit; defaults to
    // never-suppress.
    internal Func<bool> SuppressCollectInStashRoom { get; set; } = static () => false;

    // A ceiling on the coin VALUE the collect funnel may still take, in copper, for an
    // errand that wants a set sum rather than everything on the floor (auto-train
    // funding). Null = no ceiling: collect as the settings say. Each get sent takes
    // its value off the ceiling; at zero nothing is taken.
    //
    // While a ceiling is set the coin each room survey shows is remembered, by coin
    // (a re-shown pile replaces its earlier count rather than adding to it). That is
    // how the errand reads a stash it searched under a zero ceiling before deciding
    // what, if anything, to take from it (CollectSurveyed).
    private long? _collectLimitCopper;
    public long? CollectLimitCopper => _collectLimitCopper;
    private readonly Dictionary<string, int> _surveyedUnderLimit = new(StringComparer.OrdinalIgnoreCase);

    public long SurveyedCopperUnderLimit
    {
        get
        {
            long total = 0;
            foreach ((string currency, int count) in _surveyedUnderLimit)
                total += CoinValue(SlotForCurrency(currency)) * count;
            return total;
        }
    }

    // The same surveys by coin, for a reader that wants to say what the pile is
    // made of (the stash transfer's progress).
    public IReadOnlyDictionary<CoinDenomination, long> SurveyedCoinsUnderLimit
    {
        get
        {
            Dictionary<CoinDenomination, long> coins = new();
            foreach ((string currency, int count) in _surveyedUnderLimit)
                if (SlotForCurrency(currency) is >= 0 and var slot && count > 0)
                    coins[(CoinDenomination)slot] = coins.GetValueOrDefault((CoinDenomination)slot) + count;
            return coins;
        }
    }

    public void SetCollectLimit(long? copper)
    {
        _collectLimitCopper = copper is { } c ? Math.Max(0, c) : null;
        _surveyedUnderLimit.Clear();
    }

    // Take up to this much copper value of the coin last surveyed, dearest coin
    // first. The errand calls it once it has seen the pile and decided to draw on it.
    // The per-coin Collect / Discard / Ignore rules don't apply: this is our own stash
    // being drawn on for a known bill, not floor loot. The weight limits still do.
    public void CollectSurveyed(long copper)
    {
        _collectLimitCopper = Math.Max(0, copper);
        foreach ((string currency, int count) in _surveyedUnderLimit
                     .OrderByDescending(e => CoinValue(SlotForCurrency(e.Key)))
                     .Select(e => (e.Key, e.Value))
                     .ToList())
            CollectCoins(count, currency);
    }

    private static long CoinValue(int slot) =>
        slot >= 0 ? CurrencyHoldings.CopperUnit((CoinDenomination)slot) : 0;

    private void NoteSurveyed(string currency, int count)
    {
        if (_collectLimitCopper is not null && SlotForCurrency(currency) >= 0)
            _surveyedUnderLimit[currency] = count;
    }

    private void SpendCollectLimit(int slot, long coins)
    {
        if (_collectLimitCopper is { } limit)
            _collectLimitCopper = Math.Max(0, limit - CoinValue(slot) * coins);
    }
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

    // Single-word currency names for the get / drop wire shape, indexed by slot
    // (0=copper..4=runic) — same vocabulary the collect path already sends.
    private static readonly string[] SlotCurrencyNames =
        { "copper", "silver", "gold", "platinum", "runic" };

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

    // Fires when the server confirms coin was stashed (a CashHidden line) — an
    // automated stash-room reroute or a manual `hide` alike. Args: currency word,
    // coin count. Feeds the transaction-history ledger so a hand-typed stash is
    // recorded the same as the engine's.
    public event Action<string, int>? CoinHidden;

    public CashManager(
        MessageRouter router,
        Func<CashSettings> readSettings,
        Func<bool> isEnabled,
        Func<bool> collectAfterCombatFinished,
        Func<bool> hasEngageableHostiles,
        Func<InventorySnapshot>? getSnapshot = null,
        Func<bool>? isPeekSuppressed = null,
        LogService? log = null,
        CurrencyNaming? naming = null,
        Func<string, bool>? isKnownItem = null,
        Action<Action>? post = null,
        Func<DateTime>? unansweredClock = null,
        Func<DateTime>? autoDepositClock = null)
    {
        _autoDepositClock = autoDepositClock ?? (() => DateTime.UtcNow);
        _unansweredClock = unansweredClock ?? (() => DateTime.UtcNow);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(collectAfterCombatFinished);
        ArgumentNullException.ThrowIfNull(hasEngageableHostiles);
        _readSettings = readSettings;
        _collectAfterCombatFinished = collectAfterCombatFinished;
        _hasEngageableHostiles = hasEngageableHostiles;
        _isEnabled = isEnabled;
        // Resolves the per-realm runic word; unbound, it falls back to stock
        // "runic" so the stable-realm behaviour is unchanged.
        _naming = naming ?? new CurrencyNaming();
        // No snapshot bound (or before an `i` parse) → the encumbrance gate
        // is inert and collection runs the full-pickup path.
        _getSnapshot = getSnapshot ?? (static () => InventorySnapshot.Empty);
        // Null when unbound → never a peek. A `look <dir>` peek renders
        // a full room display, so gate the "You notice" collect path on it.
        _isPeekSuppressed = isPeekSuppressed ?? (static () => false);
        // Resolves a survey entry against the active item table (true when it's a
        // real Items.json record). Injected so TryParseCashEntry can settle the
        // "2 gold key" ambiguity; null (no game data) leaves the
        // count+denomination heuristic standing alone.
        _isKnownItem = isKnownItem;
        // Unbound runs inline; AppServices wires Dispatcher.UIThread.Post so
        // the room-survey collect decision lands after the room-display batch settles.
        _post = post ?? (a => a());
        _log = log;

        _groundSub   = router.Subscribe(KnownPatterns.CashOnGround,  OnCashOnGround);
        _pickedUpSub = router.Subscribe(KnownPatterns.CashPickedUp,  OnCashPickedUp);
        _droppedSub  = router.Subscribe(KnownPatterns.CashDropped,   OnCashDropped);
        _dropRefusedSub = router.Subscribe(KnownPatterns.CashDropRefused, OnDropRefused);
        _trainedSub = router.Subscribe(KnownPatterns.TrainAttainLevel, OnTrained);
        _trainedNextSub = router.Subscribe(KnownPatterns.TrainAttainNextLevel, OnTrained);
        // `hide N <coin>` is the stash-room verb, answered like a drop. The
        // item-hide path (UserHides) leaves coin-shaped hides to this one.
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

    // Bind the shared room-redisplay coordinator so the combat-clear re-display
    // coalesces with the item engine's post-kill re-look (one Enter, not two).
    // Optional — unbound, the engine sends its own Enter.
    public void SetRoomRedisplay(Game.Inventory.RoomRedisplayCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _redisplay = coordinator;
    }

    // Called on profile load: re-arms the auto-deposit gate and clears the
    // in-flight coin projection so a pending get/drop from the prior session
    // can't skew the new character's first gate evaluation.
    public void ResetTallies()
    {
        _autoDepositFiredThisCrossing = false;
        _autoDepositRetryNotBefore = default;
        Array.Clear(_inFlightCoinDelta, 0, _inFlightCoinDelta.Length);
        Array.Clear(_inFlightCoinDeltaSetAt, 0, _inFlightCoinDeltaSetAt.Length);
        _collectedGround.Clear();
        // Drop any pending post-combat collect from the prior character and release
        // the gate it was holding — the coin belonged to their session.
        _resurveyReleaseTimer?.Stop();
        if (_cashPendingAfterCombat)
        {
            _cashPendingAfterCombat = false;
            _gate?.NoteDeferredCleared();
        }
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
    // Holdings come from the authoritative InventorySnapshot.Currency alone —
    // the 'i'-seeded, delta-tracked truth. A count of the coin seen picked up and
    // dropped this session misses a carried-over balance and never learns of coin
    // spent, banked or miscounted — maxing it in kept dropping 2182 copper after
    // an `i` showed 671 (report paradigm-20260929-183240). A fresh pickup still drops promptly: the
    // inventory change it causes re-runs this audit (OnInventoryChanged).
    //
    // One drop per currency at a time: until the server answers it (dropped, or
    // refused) or DropReplyWindow passes, a re-audit sends nothing more for it.
    // We don't optimistically decrement, so a drop that fails is retried.
    private static readonly TimeSpan DropReplyWindow = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, DateTime> _dropSentAt = new(StringComparer.OrdinalIgnoreCase);
    // A refused drop means our holdings are stale: re-read them with `i`, at most
    // this often.
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(10);
    private DateTime _lastRefreshAt;
    // The weight estimate has been checked against the game since the last pickup
    // that went through.
    private bool _gateRefusalChecked;

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
        // accepts; ResolvePolicy recognises it via _naming.IsRunic.
        AuditDenominationForDiscard(settings, _naming.RunicName, snap.Runic);
    }

    private void AuditDenominationForDiscard(CashSettings settings, string currency, long snapshotCount)
    {
        if (ResolvePolicy(settings, currency) != CashPolicy.Discard) return;
        if (snapshotCount <= 0) return;
        DateTime now = DateTime.UtcNow;
        string key = _naming.Canonicalize(currency);
        if (_dropSentAt.TryGetValue(key, out DateTime sent) && now - sent < DropReplyWindow) return;
        _dropSentAt[key] = now;
        _log?.Info(LogCategory, $"discard drop currency={currency} count={snapshotCount}");
        Send($"drop {snapshotCount} {_naming.WireNoun(currency)}");
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
        NoteSurveyed(currency, count);

        switch (policy)
        {
            case CashPolicy.Collect:
                // A search in a stash room re-exposed the just-hidden pile — don't
                // grab our own stash back. Only the reveal survey is gated; coin
                // shown on plain room entry (this line) collects normally.
                if (SuppressCollectInStashRoom())
                {
                    _log?.Info(LogCategory,
                        $"stash-room search reveal — not re-grabbing {count} {currency}");
                    break;
                }
                // Specific amount (not bare `get <currency>` which
                // would grab all available) so encumbrance / weight
                // tracking can do exact arithmetic instead of waiting
                // for the wealth display to refresh.
                CollectOrDefer(count, currency);
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

        if (policy == CashPolicy.Collect)
        {
            // Corpse drops are freshly-dropped visible coin, never the stashed pile,
            // so this only trips if a search reveal happens to be in flight as the
            // drop lands — the guard stays for symmetry with the other collect paths.
            if (SuppressCollectInStashRoom())
            {
                _log?.Info(LogCategory,
                    $"stash-room search reveal — not re-grabbing corpse {count} {currency}");
                return;
            }
            // Fresh coin on the ground → re-arm collection of this currency even if
            // we already took an earlier pile in this room (a new mob wandered in).
            _collectedGround.Remove(currency);
            CollectOrDefer(count, currency);
        }
    }

    private void OnCashPickedUp(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;

        _unansweredGets.Remove(currency);
        CoinCollected?.Invoke(currency, count);
        // Confirm our pending `get` — drain the matching in-flight delta so
        // the next gate evaluation works against the parser's fresh view.
        DecayInFlight(currency, count);
        // Tell the shared acquisition gate this coin get resolved so the walker
        // can resume without waiting on the settle fallback. A count of 0 (carry
        // limit hit, took nothing) still resolves the get — there's nothing more
        // to wait for.
        _gate?.NoteGetConfirmed();
        CheckAutoDeposit();
        // A Discard currency picked up is dropped once the inventory snapshot has
        // it (OnInventoryChanged re-audits) — auditing here too sent a second drop.
    }

    private void OnCashDropped(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;
        // As for a hide: "You dropped 3 iron crown." is an item, not coin.
        if (!IsCashWord(currency)) return;

        DecayInFlight(currency, -count);
        ClearDropSent(currency);
        CheckAutoDeposit();
    }

    // "You don't have N <coin> to drop!" / "… to hide!" — the count we dropped or
    // stashed from was stale. Re-read the inventory (`i`), whose fresh coin counts
    // re-run the audit.
    private void OnDropRefused(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;
        ClearDropSent(currency);
        ReReadInventory($"drop or stash of {count} {currency} refused — coin counts are stale");
    }

    // A train went through. The purse took the fee off by value (InventoryManager),
    // but not which coins went, so the next coin decision re-reads the inventory —
    // then, not now: a train can leave the stat-training box open, where a typed
    // `i` would land in the form.
    private void OnTrained(MatchResult _) => _purseStale = true;

    private bool _purseStale;

    // One `i` per cooldown: its fresh counts replace the stale ones.
    private bool ReReadInventory(string why)
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastRefreshAt < RefreshCooldown) return false;
        _lastRefreshAt = now;
        _log?.Warn(LogCategory, $"{why}, re-reading inventory");
        Send("i");
        return true;
    }

    private void ClearDropSent(string currency) => _dropSentAt.Remove(_naming.Canonicalize(currency));

    // Stash-room confirmation handler — answered like a drop. The hide wire shape
    // is what stash-room visits use to dump excess coin / items.
    private void OnCashHidden(MatchResult m)
    {
        (string? currency, int count) = ParseCashLine(m);
        if (currency is null) return;
        // Paradigm's counted echo of an item named after a coin ("You hid 3 iron
        // crown.") has the shape of a coin line and none of its metals.
        if (!IsCashWord(currency)) return;

        _log?.Debug(LogCategory, $"hidden currency={currency} count={count}");
        DecayInFlight(currency, -count);
        CheckAutoDeposit();
        CoinHidden?.Invoke(currency, count);
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

        // A whole single-row list is the pattern subscription's; only a wrapped one
        // is gathered here.
        if (FloorListLine.OpensWrappedList(text))
        {
            _noticeBuffer = text;
            _noticeRawFirst = line.Text;
        }
    }

    private void ProcessYouNoticeMultiLine(string completeLine)
    {
        if (FloorListLine.TryReadList(completeLine, out string body))
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
            NoteSurveyed(currency!, count);

            if (policy == CashPolicy.Collect)
                CollectOrDeferRoomSurvey(count, currency!);
        }
    }

    // ----- collect-after-combat defer ---------------------------------

    // Collect now, or queue for the post-combat flush. With
    // CollectAfterCombatFinished set and the room still holding engageable
    // hostiles, the get is deferred until OnRoomObserved fires clear — a get
    // landed between two kills eats the next round's pre-attack. Otherwise it
    // collects immediately. On defer it asserts the AcquisitionGate (via
    // NoteDeferredPending) so the walker holds across the combat-clear boundary
    // until the flush runs — the same race defence the item engine uses.
    // Room-survey ("You notice ... here.") collect. The survey line is parsed
    // BEFORE the room's "Also here: <monsters>" line, so _hasEngageableHostiles()
    // can still read a stale "clear" for a hostile that's about to reveal — which
    // sent a `get` into a room that then started combat (report
    // stock-20260730-193107). With collect-after-combat on, defer the decision one
    // dispatch tick so the current room-display batch (including Also-here) has
    // been processed and the combat gate is current, THEN collect-or-defer.
    // Collect-after-combat off keeps the immediate collect (nothing to race).
    private void CollectOrDeferRoomSurvey(int count, string currency)
    {
        if (!_collectAfterCombatFinished())
        {
            CollectOrDefer(count, currency);
            return;
        }
        // Already waiting for this room's combat to clear, or already handled this
        // pile this visit → ignore the re-render (a passive re-display, or a second
        // "You notice" for cash we've taken). This is what stops drop-smaller-for-
        // larger re-swapping the same pile on every render.
        if (_cashPendingAfterCombat || AlreadyCollected(currency, count)) return;

        // Claim the pile now (so a concurrent re-render skips) and HOLD the walker
        // across the one-tick decision defer. The defer waits for the room-display
        // batch (Also-here) so the hostile flag is current — but during that tick the
        // loop could otherwise step out of the room, firing the get into the room we
        // just left (which fails and stalls the loop, report -143321) or missing the
        // pile entirely (report -143150). Holding pins us in place until the decision.
        MarkCollected(currency, count);
        _gate?.NoteDeferredPending(1);
        _post(() => CollectDecideHeld(count, currency));
    }

    // The deferred decision, one dispatch tick after "You notice" — the room-display
    // batch is processed now, so the hostile flag is current. Releases the decision
    // hold set by CollectOrDeferRoomSurvey.
    private void CollectDecideHeld(int count, string currency)
    {
        if (_hasEngageableHostiles())
        {
            // Combat revealed after the notice line. Un-claim so the post-combat
            // re-look re-collects from the true ground total, and defer (stay held).
            _collectedGround.Remove(currency);
            if (!_cashPendingAfterCombat)
            {
                _cashPendingAfterCombat = true;
                _log?.Info(LogCategory, "cash on ground during combat — collecting after it clears");
            }
            return;
        }
        CollectCoins(count, currency);
        // Decision done — clear the one-tick hold. If CollectCoins sent a get, its own
        // NoteGetSent settle window keeps the walker held through the pickup; if it
        // sent nothing (gated, nothing to swap), this releases the walker now.
        _gate?.NoteDeferredCleared();
    }

    private void CollectOrDefer(int count, string currency)
    {
        if (_collectAfterCombatFinished() && _hasEngageableHostiles())
        {
            // Note that cash is waiting — but not the amount. The post-combat re-look
            // collects from the room's true ground contents, so mid-fight amounts
            // (which double-count kill deltas against the running total) are ignored.
            // Arm the walker-hold once per combat.
            if (!_cashPendingAfterCombat)
            {
                _cashPendingAfterCombat = true;
                _log?.Info(LogCategory, "cash on ground during combat — collecting after it clears");
                _gate?.NoteDeferredPending(1);
            }
            return;
        }
        // Decide once per pile per room visit — a re-displayed "There are N here" of a
        // pile we've handled must not re-collect it (see _collectedGround). A search
        // that reveals MORE than we took falls through and re-collects the new total.
        if (AlreadyCollected(currency, count))
        {
            _log?.Debug(LogCategory, $"collect skipped currency={currency} — already handled ≥{count} this room visit");
            return;
        }
        MarkCollected(currency, count);
        CollectCoins(count, currency);
    }

    // Called on each room-entity observation (wired after the combat tracker so
    // the hostile flag is current). Flushes the deferred collect queue once no
    // engageable hostiles remain — the "combat finished for this room" signal.
    public void OnRoomObserved()
    {
        if (!_cashPendingAfterCombat) return;
        if (_hasEngageableHostiles()) return;   // still fighting — keep waiting
        FlushDeferredCollects();
    }

    // Called on actual room change. Drops a pending post-combat collect — that coin
    // belonged to the room we just left — and resets the per-visit collect latch so
    // the new room's piles are evaluated fresh.
    public void OnRoomChanged()
    {
        if (_collectedGround.Count > 0)
            _log?.Debug(LogCategory, $"room changed — resetting collect latch (was: {string.Join(", ", _collectedGround.Keys)})");
        _collectedGround.Clear();
        _unansweredGets.Clear();
        CancelDeferredCollect("room changed");
    }

    // Drop a pending post-combat collect and release the acquisition hold WITHOUT
    // collecting — the coin belonged to a room/session we're no longer collecting
    // for. Shared by the room-change path and the reconnect resume: a disconnect
    // mid-defer strands the hold (no self-heal, and the reconnect re-display doesn't
    // reliably flush it), leaving the walker paused until it's dropped.
    // True when a collect was in fact pending.
    public bool CancelDeferredCollect(string reason)
    {
        _resurveyReleaseTimer?.Stop();
        if (!_cashPendingAfterCombat) return false;
        _cashPendingAfterCombat = false;
        _log?.Debug(LogCategory, $"{reason} — dropping pending post-combat collect");
        _gate?.NoteDeferredCleared();
        return true;
    }

    // Combat's done and cash is waiting: re-display the room and collect from what's
    // actually on the ground now, rather than replaying the amounts seen mid-fight.
    // The re-display's "You notice" line drives one collect per currency off the true
    // ground total — no duplicates, no stale gets, and naturally correct if someone
    // grabbed the pile first. A bare Enter (carriage return) re-renders the survey
    // exactly like `look` would but honours the profile's brief mode — no room
    // description / flavor text — matching AutoGetItemsManager.RequestDropReLook. Hold
    // the walker across the round-trip; the release timer frees it if the room turned
    // out empty, and a real collect re-arms the gate's settle window before then.
    private void FlushDeferredCollects()
    {
        _cashPendingAfterCombat = false;
        _log?.Info(LogCategory, "combat cleared — collecting ground cash");
        _gate?.NoteDeferredPending(1);
        // Re-render once. The item engine's post-kill re-look fires the same
        // instant on the last kill; coalesce so the room isn't displayed twice
        // (our collect still runs off the shared "You notice" survey either way).
        if (_redisplay is null || _redisplay.ShouldSend())
            Send("");   // bare carriage return, not `look` — respects brief mode
        ArmResurveyRelease();
    }

    // Arm (or re-arm) the one-shot hold that covers the re-survey `look` round-trip.
    private void ArmResurveyRelease()
    {
        if (_resurveyReleaseTimer is null)
        {
            _resurveyReleaseTimer = new DispatcherTimer();
            _resurveyReleaseTimer.Tick += OnResurveyReleaseElapsed;
        }
        _resurveyReleaseTimer.Interval = TimeSpan.FromMilliseconds(ResurveyReleaseMs);
        _resurveyReleaseTimer.Stop();
        _resurveyReleaseTimer.Start();
    }

    private void OnResurveyReleaseElapsed(object? sender, EventArgs e)
    {
        _resurveyReleaseTimer?.Stop();
        // A new mob may have wandered in and re-armed the pending flag after we sent
        // look; only release the hold when nothing is waiting, so the fresh defer
        // keeps the gate for itself.
        if (!_cashPendingAfterCombat)
            _gate?.NoteDeferredCleared();
    }

    // The coins carried now and how many more the weight limits would let a pickup
    // add. Null when the carry capacity isn't known. A stash transfer reads it to
    // tell a purse that should be banked before it goes for more.
    public (long Held, long Room)? CoinLoad()
    {
        InventorySnapshot snap = _getSnapshot();
        EncumbranceReading enc = snap.Encumbrance;
        if (enc.MaxWeight <= 0) return null;
        long held = snap.Currency.TotalCoinCount;
        long nonCoinWeight = Math.Max(0, enc.CurrentWeight - held / 3);
        return (held, RoomForCoins(CapWeight(_readSettings(), enc), nonCoinWeight, held));
    }

    // The weight a coin pickup may bring us up to: the carry maximum, tightened by
    // the SkipCollect* boxes.
    private static long CapWeight(CashSettings settings, EncumbranceReading enc) =>
        EncumbranceGate.ComputeCapWeight(
            settings.SkipCollectIfMakesLight,
            settings.SkipCollectIfMakesMedium,
            settings.SkipCollectIfMakesHeavy,
            enc,
            settings.SkipCollectPast90Percent);

    // Three coins to the weight unit.
    private static long RoomForCoins(long capWeight, long nonCoinWeight, long heldCoins)
    {
        long headroom = capWeight - (nonCoinWeight + heldCoins / 3);
        return headroom > 0 ? headroom * 3 : 0;
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
        // Stash-room guard at the shared funnel so EVERY collect path is covered —
        // the "You notice N here" room-survey path (what auto-search's `sea`
        // re-render drives) reached here without passing the per-handler guards, so
        // a just-stashed pile got grabbed right back while looping (report
        // paradigm-20260820-055720). The guard fires only while the reveal is in
        // flight, so a plain-entry survey of a stash room still collects visible coin.
        if (SuppressCollectInStashRoom())
        {
            _log?.Info(LogCategory, $"stash-room search reveal — not re-grabbing {count} {currency}");
            return;
        }

        if (_purseStale && ReReadInventory("trained since the last inventory read"))
            _purseStale = false;

        CashSettings settings = _readSettings();
        int slot = SlotForCurrency(currency);

        // An errand's ceiling: take only as many of this coin as still cover the sum
        // it wants (rounded up to a whole coin), and nothing once it is covered. The
        // survey lists the dearest coin first, so the sum is met in the fewest coins.
        if (_collectLimitCopper is { } limit && slot >= 0)
        {
            long each = CoinValue(slot);
            long allowed = limit <= 0 ? 0 : (limit + each - 1) / each;
            if (allowed <= 0)
            {
                _log?.Info(LogCategory,
                    $"collect skipped currency={currency} want={count} — an errand's ceiling leaves nothing more to take");
                return;
            }
            if (allowed < count)
            {
                _log?.Info(LogCategory,
                    $"collect limited currency={currency} {count} → {allowed} — the errand needs {limit:N0} copper more");
                count = (int)allowed;
            }
        }

        InventorySnapshot snap = _getSnapshot();
        EncumbranceReading enc = snap.Encumbrance;

        // The weight budget applies whenever we know the currency slot and the
        // carry capacity — the hard MaxWeight cap is always on. The
        // SkipCollectIfMakes{Light,Medium,Heavy} flags only *tighten* the ceiling
        // to a bracket boundary (via ComputeCapWeight); without them the cap is
        // MaxWeight, so we never send a `get` for coin we can't hold (which the
        // server just rejects — and, because the failed get emits no pickup echo,
        // that also strands the event-driven auto-deposit re-check).
        bool gateActive = slot >= 0 && enc.MaxWeight > 0;

        if (!gateActive)
        {
            // Unknown currency slot or unknown capacity → nothing to budget
            // against, collect the full amount (fail-open).
            _gate?.NoteGetSent();
            _log?.Info(LogCategory, $"collect currency={currency} get={count} (ungated)");
            Send($"get {count} {_naming.WireNoun(currency)}");
            NoteGetSent(currency);
            SpendCollectLimit(slot, count);
            return;
        }

        SweepStaleInFlight();

        long capWeight = CapWeight(settings, enc);
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
            return RoomForCoins(capWeight, nonCoinWeight, t);
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
                Send($"drop {canSwap} {_naming.WireNoun(SlotCurrencyNames[j])}");
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
            // The weight behind this refusal is a running estimate between `i`
            // reads. If it has drifted, nothing else would ever correct it — every
            // pickup is refused, so no line arrives to move it — so check it against
            // the game once per run of refusals. A character that really is full
            // costs one `i`.
            if (!_gateRefusalChecked
                && ReReadInventory("coin pickup refused for weight — checking the estimate"))
                _gateRefusalChecked = true;
            // We're at the weight cap, so no `get`/pickup echo will fire to re-run
            // the event-driven auto-deposit check. Re-evaluate it here: if wealth
            // already exceeds the deposit threshold this arms the bank reroute to
            // shed weight — otherwise a full character loops forever, never
            // depositing (report paradigm-20260730-125716).
            CheckAutoDeposit();
            return;
        }

        _gateRefusalChecked = false;
        _gate?.NoteGetSent();
        _log?.Info(LogCategory, swapDone > 0
            ? $"collect currency={currency} get={totalPickup} (free={freePickup} + {swapDone} via drop-smaller-for-larger)"
            : $"collect currency={currency} get={totalPickup}");
        Send($"get {totalPickup} {_naming.WireNoun(currency)}");
        NoteGetSent(currency);
        SpendCollectLimit(slot, totalPickup);
        _inFlightCoinDelta[slot] += totalPickup;
        _inFlightCoinDeltaSetAt[slot] = now;
    }

    // Make room for an item pickup (Cash "Drop coin to make room for Auto-sell
    // items"): drop held coin worth `weight` units, cheapest denomination first and
    // none dearer than the setting's coin. All or nothing: when the coin it may drop
    // can't free that much, nothing goes down and the caller leaves the item. True
    // once the drops are on the wire, ahead of the caller's `get`.
    public bool TryDropCoinForWeight(long weight, string itemName)
    {
        CashSettings settings = _readSettings();
        if (!settings.DropCoinForSellItems || weight <= 0) return false;

        SweepStaleInFlight();
        CurrencyHoldings c = _getSnapshot().Currency;
        long[] held = { c.Copper, c.Silver, c.Gold, c.Platinum, c.Runic };
        int dearest = (int)settings.DropCoinForSellItemsUpTo;
        long available = 0;
        for (int k = 0; k <= dearest; k++)
        {
            held[k] = Math.Max(0, held[k] + _inFlightCoinDelta[k]);
            available += held[k];
        }

        // Three coins to the weight unit.
        long need = weight * 3;
        if (available < need)
        {
            _log?.Info(LogCategory,
                $"no room made for item={itemName}: {need} coins must go, {available} held up to {settings.DropCoinForSellItemsUpTo}");
            return false;
        }

        DateTime now = DateTime.UtcNow;
        for (int k = 0; k <= dearest && need > 0; k++)
        {
            long drop = Math.Min(need, held[k]);
            if (drop <= 0) continue;
            _log?.Info(LogCategory, $"drop {drop} {SlotCurrencyNames[k]} to make room for item={itemName}");
            Send($"drop {drop} {_naming.WireNoun(SlotCurrencyNames[k])}");
            _inFlightCoinDelta[k] -= drop;
            _inFlightCoinDeltaSetAt[k] = now;
            need -= drop;
        }
        return true;
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
    // second word being a cash word (IsCashWord). Singular form "a gold piece"
    // is also tolerated (count = 1).
    //
    // The count+denomination shape alone can't separate a stacked item whose name
    // *starts* with a denomination word ("2 gold key") from a coin pile ("2 gold
    // crowns"). The item-table tiebreaker settles it (shared with
    // GroundItemTracker.IsCashEntry, the same fix): an entry that resolves to a
    // real Items.json record is never cash. Currency isn't in the item table, so a
    // true coin pile won't resolve. Unwired, the heuristic stands alone.
    private bool TryParseCashEntry(string raw, out string? currency, out int count)
    {
        currency = null;
        count = 0;
        string[] words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return false;

        // Authoritative item-table override — a known item is never cash.
        if (_isKnownItem is not null && _isKnownItem(raw)) return false;

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
        _autoDepositRetryNotBefore = _autoDepositClock().AddMilliseconds(AutoDepositRetryCooldownMs);
        _log?.Debug(LogCategory,
            $"auto-deposit re-armed after aborted reroute (retry held {AutoDepositRetryCooldownMs / 1000}s)");
    }

    // Whether a deposit is due right now: a gate over its threshold with a bank or
    // stash room picked. The sell detour asks before walking back, so a sale that
    // crossed the gate goes to the bank from the shop.
    public bool IsAutoDepositDue()
    {
        CashSettings settings = _readSettings();
        if (string.IsNullOrEmpty(settings.BankRoomKey)) return false;
        CurrencyHoldings held = _getSnapshot().Currency;
        return (settings.AutoDepositIfWealthExceeds > 0 && held.TotalCopperValue > settings.AutoDepositIfWealthExceeds)
            || (settings.AutoDepositIfCoinsExceed > 0 && held.TotalCoinCount > settings.AutoDepositIfCoinsExceed);
    }

    // A deposit started outside the gate's own firing (handed over by a detour):
    // latch the single-fire guard as a firing would, so the gate doesn't start a
    // second one on the next inventory line.
    public void NoteAutoDepositStarted()
    {
        _autoDepositFiredThisCrossing = true;
        _autoDepositRetryNotBefore = default;
    }

    // The master switch (true = off): off, no bank / stash trip is asked for.
    // Coin collection itself rides the Auto Get Cash probe, which reads it too.
    public Func<bool>? MasterSwitchOff { get; set; }

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
        // the `i`-seeded balance, buy / sell conversions included.
        CurrencyHoldings held = _getSnapshot().Currency;
        long wealthValue = held.TotalCopperValue;
        long coinCount = held.TotalCoinCount;

        bool wealthGate = wealthThreshold > 0 && wealthValue > wealthThreshold;
        bool coinGate = coinThreshold > 0 && coinCount > coinThreshold;

        // OR logic: either gate firing triggers the deposit. The single-fire
        // guard re-arms only once BOTH gates fall back below their thresholds,
        // so a deposit that clears wealth but not coin count doesn't re-fire.
        if (wealthGate || coinGate)
        {
            // Over threshold but already fired this crossing: won't re-fire until a
            // completed deposit drops wealth below the gate or the reroute aborts
            // and re-arms. Logged so a "why isn't it depositing?" capture shows the
            // latch — not the gate — is what's holding it (report 20260730-125716).
            if (_autoDepositFiredThisCrossing)
            {
                _log?.Debug(LogCategory,
                    $"auto-deposit held: already fired this crossing, awaiting a completed deposit "
                    + $"or reroute abort (wealth={wealthValue} gate={wealthThreshold} coins={coinCount})");
                return;
            }
            // A recent reroute aborted without depositing; hold off re-firing
            // until the cooldown elapses so an unreachable bank can't thrash the
            // movement engine on every inventory line.
            if (_autoDepositClock() < _autoDepositRetryNotBefore)
            {
                _log?.Debug(LogCategory,
                    $"auto-deposit held: retry cooldown after an aborted reroute "
                    + $"(wealth={wealthValue} gate={wealthThreshold})");
                return;
            }

            // Before the latch, so the trip is still owed: the first inventory
            // change after the master switch is back on fires it.
            if (MasterSwitchOff?.Invoke() == true) return;

            _autoDepositFiredThisCrossing = true;
            _log?.Info(LogCategory,
                $"auto-deposit triggered wealth={wealthValue} (gate={wealthThreshold}) " +
                $"coins={coinCount} (gate={coinThreshold})");
            AutoDepositRequested?.Invoke(wealthValue);
        }
        else
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
        _dropRefusedSub.Dispose();
        _trainedSub.Dispose();
        _trainedNextSub.Dispose();
        _hiddenSub.Dispose();
        _noticeSub.Dispose();
        _killDropSub.Dispose();
        if (_resurveyReleaseTimer is not null)
        {
            _resurveyReleaseTimer.Stop();
            _resurveyReleaseTimer.Tick -= OnResurveyReleaseElapsed;
            _resurveyReleaseTimer = null;
        }
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}
