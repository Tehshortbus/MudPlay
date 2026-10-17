using System.Text.RegularExpressions;
using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Inventory;

// Tracks the player's currency and carry-weight by watching the terminal
// line stream: a full 'i' dump re-bases the snapshot, and incremental coin
// pickups / drops / bank deposits / withdrawals patch it between dumps.
// Publishes an immutable InventorySnapshot the cash engine reads instead of
// tracking coin lines itself. It also models the numeric encumbrance reading
// (current / max / percentage / bracket).
//
// Currency ratios are MajorMUD-faithful in copper farthings: 1 silver = 10,
// 1 gold = 100, 1 platinum = 10000, 1 runic = 1000000, so wealth +
// encumbrance math stays in step with the game.
//
// Coin weight follows the game's rule: 3 coins of any denomination = 1
// encumbrance unit, integer-truncated. Picking up 1–2 coins when the prior
// total was a multiple of 3 moves weight by 0, matching the stat readout.
//
// Encumbrance bracket: between full parses the derived bracket uses Stock
// boundaries (None ≤ 16%). A full 'i' parse always overrides the derived
// bracket with the game's literal word, so any drift self-corrects on the
// next dump.
//
// Single-writer: this manager keeps its own snapshot and never writes
// PlayerState.Encumbrance — that field's sole writer is EncumbranceParser.
// The two observe the same line independently.
public sealed partial class InventoryManager : IDisposable
{
    // LogService category — [Inventory] rows per parse / patch.
    public const string LogCategory = "Inventory";

    private const int MaxCaptureLines = 50;

    private readonly LogService? _log;
    private readonly object _lock = new();

    // Resolves a game item display-name ("a torch") to its carry weight (MDB
    // Encum), or null when unknown. Lets item get / drop / buy / sell move the
    // live encumbrance estimate the same way coin transactions already do.
    // Null leaves item weight out of the estimate.
    private readonly Func<string, int?>? _itemWeight;

    // Resolves a worn item's display-name to its true slot string ("Torso"), or
    // null when unknown. The "You are now wearing X." line names no slot, so this
    // supplies the real placement instead of a generic "Worn" that would misfile
    // the piece in "Snapshot Current". Null when no game data is loaded
    // — the handler falls back to the generic slot.
    private readonly Func<string, string?>? _slotResolver;

    // True when a text is an item's record name exactly as the game data spells
    // it. Gates the "<Player> gives you <item>." hand-over, whose shape NPC give
    // flavour text shares. Null when no game data is loaded: that
    // hand-over form then goes unread and the next 'i' picks the item up.
    private readonly Func<string, bool>? _isItemRecordName;

    // True when a named item is a key. The game lists keys on a ring of their own
    // in the 'i' dump, apart from the pack, so a key handed over lands there. Null
    // when no game data is loaded: only a key already on the ring is
    // then known to be one.
    private readonly Func<string, bool>? _isKey;
    // The named item is kept when its uses run out (the data's Retain After Uses).
    private readonly Func<string, bool>? _staysWhenSpent;
    // The light Stock's `You lit the <item>.` named since the last full read. Such a
    // light is not listed apart (only a full read does that): its copy is still in
    // the pack list, and this is the only record of which entry is burning.
    private string? _litSinceRead;

    // The Items table's ItemType for a key.
    public const int KeyItemType = 7;

    private LineExtractor? _lines;
    private bool _disposed;

    // ----- snapshot state (guarded by _lock) ---------------------------
    private int _copper, _silver, _gold, _platinum, _runic;
    private long _wealthCopper;
    // Copper value of an auto-deposit already applied optimistically at `dep`
    // dispatch (NoteAutoDeposit), still awaiting its server "You deposit …" echo.
    // The echo reconciles against this instead of subtracting a second time, so
    // the coin isn't double-counted. Re-based to 0 by every full 'i' dump.
    private long _pendingDepositCopper;
    private int _curWeight, _maxWeight, _percentage;
    private EncumbranceLevel _category = EncumbranceLevel.Unknown;
    private bool _loaded;
    private DateTimeOffset _lastUpdated = DateTimeOffset.MinValue;
    private IReadOnlyList<EquippedItem> _equipped = Array.Empty<EquippedItem>();
    // The list index + paired family ("Finger" / "Wrist") of the item a preceding
    // "You have removed X." line just vacated, so the NEXT "You are now wearing Y."
    // of the same family can slot Y into that exact position rather than appending —
    // the game's `eq <ring>` into a full pair prints remove-then-wear, and appending
    // the new ring at the end inverts the snapshot's finger order vs the game's, which
    // made the paired-slot gear composer mis-read which finger held which and emit a
    // redundant `rem` on the next swap. Null when there's nothing to correlate.
    private (int Index, string Family)? _lastRemovedPaired;
    // Carried-but-unworn item names. Re-based by each full 'i' dump and patched
    // incrementally between dumps: get / buy add, drop / sell remove, and
    // equip / remove move a name between this list and the worn set. Name
    // matching is best-effort (case-insensitive) and self-corrects on the next
    // 'i'. Death recovery records it as the "inventory lost" half of a deathpile
    // (DeathLootCapture); a piece moves out of it as it is worn, so the worn half
    // never shares a copy with it.
    private IReadOnlyList<string> _carried = Array.Empty<string>();
    // The lit light source, if the last dump listed one as "… (Readied/N)".
    // Rebased by each full 'i': a dump with no readied light clears it.
    private ReadiedLight? _readiedLight;
    // Key-ring contents from the dump's "You have the following keys: …" trailer.
    // The game tracks keys as a separate carry list, so they never appear in the
    // "You are carrying …" items sentence — parsed here from their own line and
    // rebased by each full 'i' ("You have no keys." clears it).
    private IReadOnlyList<string> _keys = Array.Empty<string>();

    // ----- full-'i' capture FSM (single-threaded — OnLine only) --------
    private bool _capturing;
    private readonly List<string> _captureBuffer = new();

    // True while a full-'i' dump is being consumed (between "You are carrying …" and
    // the terminating "Encumbrance:" line). The unrecognized-line watcher consults this
    // to exclude the dump's word-wrapped continuation rows — bare item / key fragments
    // that carry no anchor of their own and would otherwise stage as unrecognized. The
    // watcher runs one handler ahead of this parser on each line, so it reads the flag
    // as of the PREVIOUS line: the anchor line is matched by its own prefix, every
    // continuation sees the flag already set, and the closing "Encumbrance:" is routed
    // separately — so nothing after the dump is over-suppressed. Read on the UI thread.
    public bool IsCapturing => _capturing;

    // Wrap-merge: the MUD wraps long lines (~78 cols), so a multi-currency
    // "You deposit 1 platinum piece, 93 gold crowns, ... copper farthin" +
    // "gs." splits across two emitted rows. Hold a non-'.'-terminated
    // transaction-start line and prepend it to the next row for one retry.
    private string _pendingMergeLine = "";

    public InventoryManager(
        LogService? log = null,
        Func<string, int?>? itemWeightResolver = null,
        Func<string, string?>? slotResolver = null,
        Func<string, bool>? isItemRecordName = null,
        Func<string, bool>? isKey = null,
        Func<string, bool>? staysWhenSpent = null)
    {
        _isKey = isKey;
        _staysWhenSpent = staysWhenSpent;
        _log = log;
        _itemWeight = itemWeightResolver;
        _slotResolver = slotResolver;
        _isItemRecordName = isItemRecordName;
    }

    // Fired (outside the lock) whenever the snapshot changes.
    public event Action? Changed;

    // Fired only after an authoritative full `i` dump replaces the snapshot.
    // Consumers that must verify a transaction should not treat incremental
    // get/drop patches as equivalent to a fresh server inventory.
    public event Action? FullInventoryParsed;

    // Fired with the deposited copper value each time a `You deposit …` echo is
    // parsed — manual or automated `dep` alike, since both produce the same
    // server confirmation. Feeds the transaction-history ledger. (The purse-math
    // reconciliation against an optimistic auto-deposit happens separately; this
    // announces the full confirmed amount for the ledger.)
    public event Action<long>? BankDeposited;

    // Fired with the withdrawn copper value each time a `You withdrew …` echo is
    // parsed. The mirror of BankDeposited, and the other half of what keeps a
    // cached bank balance honest between `bank` queries: a balance only ever moves
    // by a deposit or a withdrawal, and both announce themselves.
    public event Action<long>? BankWithdrew;

    // Fired with an item's display-name each time a `You hid <item>.` echo names a
    // non-currency item — a stash-room / manual item hide. Coin-shaped hides are
    // excluded here (CashManager's coin path logs those), so the ledger doesn't
    // double-record a `You hid N <coin>.` line.
    public event Action<string>? ItemHidden;

    // Fired (singular name + count) each time the player's own `You sold …` /
    // `You dropped …` confirmation is parsed — the counted Paradigm form and the
    // one-at-a-time Stock form both resolve to (name, N). Lets a consumer react to
    // exactly what left the pack (e.g. the Chest Offload tab reconciling its list
    // against confirmed sales/drops rather than optimistically or by re-diffing).
    // ItemSold also carries the sale's copper value off the same line.
    public event Action<string, int, long>? ItemSold;
    // The buying counterpart: singular name, count and the total price in copper off
    // the player's own `You bought …` / `You just bought …` line.
    public event Action<string, int, long>? ItemBought;
    public event Action<string, int>? ItemDropped;

    // Fired (singular name + count) for each `You took <item>.` — an item entering
    // the pack off the floor, manual or automated get alike. Coin pickups use a
    // different verb and never raise it.
    public event Action<string, int>? ItemTaken;

    // Another player handed us an item: (item name, giver). Items only — a coin
    // hand-off isn't raised. Lets death recovery treat gear a party member recovered
    // for us and gave back as our deathpile coming home.
    public event Action<string, string>? ItemReceived;

    // The game confirmed an item hand-over of ours: (item name, copies, recipient
    // as the line names them). Items only. It is the one proof a `give` landed, so
    // what a party member was handed is remembered from it and not from the send.
    public event Action<string, int, string>? ItemGivenAway;

    // The game confirmed a coin hand-over of ours: (recipient as the line names
    // them, what the coins it counted are worth in copper). The count is the
    // game's, which on Stock is what the recipient had room to keep.
    public event Action<string, long>? CoinsGivenAway;

    // The game answered a coin give of ours with its line for a target that isn't
    // a player here, or is hidden from us. It names nobody.
    public event Action? CoinGiveMisaimed;

    // The game answered a coin give of ours with its line for more of a coin than
    // we hold. It names nobody, and nothing moved.
    public event Action? CoinGiveLackedCoins;

    // The game refused a give of ours. Carries the player the line names, or null
    // for the refusal that names nobody. A give waiting to be confirmed stops
    // waiting on it rather than on a timer.
    public event Action<string?>? GiveRefused;

    // True for another character's hand-over line this parser reads, item or
    // coins. It reads the wire directly and registers no router pattern, so the
    // unrecognized-line watcher asks here rather than restate the shapes.
    public bool IsReceivedHandOverLine(string line)
        => ReceivedItemRegex().IsMatch(line)
           || TryMatchHandedItem(line, out _, out _, out _)
           || (ReceivedCoinsRegex().Match(line) is { Success: true } coins
               && HandOverCoinNoun(coins.Groups[2].Value) is not null)
           || (HandedCoinsRegex().Match(line) is { Success: true } handed
               && CoinNounSuffixRegex().IsMatch(handed.Groups[2].Value));

    // True after at least one successful full 'i' parse.
    public bool IsLoaded
    {
        get { lock (_lock) return _loaded; }
    }

    // Immutable point-in-time copy of the currency + encumbrance state.
    public InventorySnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return new InventorySnapshot(
                    new CurrencyHoldings(_copper, _silver, _gold, _platinum, _runic, _wealthCopper),
                    new EncumbranceReading(_curWeight, _maxWeight, _percentage, _category),
                    _equipped,
                    _carried,
                    _lastUpdated,
                    _readiedLight,
                    _keys);
            }
        }
    }

    // Bind the per-session LineExtractor so the manager can watch the line
    // stream. Idempotent for the same instance; swapping extractors detaches
    // the old one first.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Mark the snapshot stale (e.g. after death / disconnect) without clearing
    // the data. Drops any half-captured wrap-merge fragment so a mid-transaction
    // cut doesn't poison the next session's first line.
    public void MarkStale()
    {
        lock (_lock) _loaded = false;
        _pendingMergeLine = "";
    }

    // Persist the carry-weight reading for the profile. Returns null when nothing
    // has ever been observed (no `i` parsed, no prior restore) so the save hook
    // won't overwrite a good persisted value with an empty one. Only the
    // encumbrance numbers travel — coins / items / worn gear re-base from the
    // next session's connect-`i`.
    public Models.Profile.LastKnownEncumbrance? SnapshotEncumbrance()
    {
        lock (_lock)
        {
            if (_maxWeight <= 0) return null;
            return new Models.Profile.LastKnownEncumbrance
            {
                CurrentWeight = _curWeight,
                MaxWeight = _maxWeight,
                Percentage = _percentage,
                Category = _category,
            };
        }
    }

    // Seed the carry-weight reading from a persisted snapshot on profile load.
    // Encumbrance can't change while out of the realm, so the last-known value is
    // accurate the moment the next session reconnects. Deliberately leaves
    // _loaded FALSE: this is a stale estimate for the cost models / calibrator /
    // Workshop to read, not a fresh dump — the connect-`i` still authoritatively
    // re-bases the full snapshot. A null clears back to unobserved (profile
    // close / a character with no persisted reading).
    public void HydrateEncumbrance(Models.Profile.LastKnownEncumbrance? snapshot)
    {
        lock (_lock)
        {
            if (snapshot is { MaxWeight: > 0 })
            {
                _curWeight = snapshot.CurrentWeight;
                _maxWeight = snapshot.MaxWeight;
                _percentage = snapshot.Percentage;
                _category = snapshot.Category;
                _lastUpdated = DateTimeOffset.Now;
                _log?.Info(LogCategory,
                    $"restored last-known encumbrance {_category} {_curWeight}/{_maxWeight} [{_percentage}%] from profile");
            }
            else
            {
                _curWeight = _maxWeight = _percentage = 0;
                _category = EncumbranceLevel.Unknown;
            }
        }
        Changed?.Invoke();
    }

    // Apply an auto-deposit the moment its `dep <value>` is dispatched at the
    // bank. The amount is known exactly — it was computed from the fresh
    // pre-deposit `i` — so the purse is decremented now rather than when the
    // server's "You deposit …" echo lands. The reroute's return-leg router reads
    // on-hand wealth synchronously right after the `dep`, and a stale
    // (pre-deposit) figure would route it through a toll it can no longer afford.
    // The echo, when it arrives, reconciles against _pendingDepositCopper so the
    // coin isn't subtracted twice. A non-positive value is a no-op.
    public void NoteAutoDeposit(long copperValue)
    {
        if (copperValue <= 0) return;
        lock (_lock)
        {
            _pendingDepositCopper += copperValue;
            ApplyTransaction(-copperValue);
        }
        Changed?.Invoke();
    }

    // ----- line processing ---------------------------------------------

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        string text = line.Text.TrimEnd();

        if (_capturing)
        {
            _captureBuffer.Add(text);

            if (text.TrimStart().StartsWith("Encumbrance:", StringComparison.Ordinal))
            {
                ParseFullInventory();
                _capturing = false;
                _captureBuffer.Clear();
                return;
            }

            if (_captureBuffer.Count >= MaxCaptureLines)
            {
                _log?.Debug(LogCategory, "capture aborted: no Encumbrance line within buffer limit");
                _capturing = false;
                _captureBuffer.Clear();
            }
            return;
        }

        if (text.StartsWith("You are carrying ", StringComparison.Ordinal)
            || text == "You are carrying nothing.")
        {
            _captureBuffer.Clear();
            _captureBuffer.Add(text);
            _capturing = true;
            return;
        }

        // Incremental: apply a pending wrap-merge first, then detect a fresh
        // wrapped transaction-start to stash for the next row.
        string toProcess = text;
        if (_pendingMergeLine.Length > 0)
        {
            toProcess = _pendingMergeLine + text;
            _pendingMergeLine = "";
        }

        ProcessIncremental(toProcess);

        if (LooksLikeWrappedTransactionStart(text))
            _pendingMergeLine = text;
    }

    private static bool LooksLikeWrappedTransactionStart(string line)
    {
        if (line.EndsWith('.')) return false;
        return line.StartsWith("You deposit ", StringComparison.Ordinal)
            || line.StartsWith("You withdrew ", StringComparison.Ordinal)
            || line.StartsWith("you withdrew ", StringComparison.Ordinal)
            || line.StartsWith("You bought ", StringComparison.Ordinal)
            || line.StartsWith("You just bought ", StringComparison.Ordinal)
            || line.StartsWith("You sold ", StringComparison.Ordinal)
            || line.StartsWith("You just sold ", StringComparison.Ordinal);
    }

    private void ProcessIncremental(string line)
    {
        // Equip / remove lines patch the worn set between full 'i' dumps, so the
        // Character Workshop updates live instead of only on the next inventory
        // pull. The game prints a single line for a weapon swap ("You are now
        // holding X.") but two for an armor swap into an occupied slot ("You have
        // removed <old>." then "You are now wearing <new>."), so a weapon equip
        // must vacate the hand itself while armor relies on the explicit removal.
        Match held = HeldWeaponRegex().Match(line);
        if (held.Success)
        {
            string name = held.Groups[1].Value.TrimEnd();
            // A weapon swap prints only this one line, but the displaced weapon is
            // still in the worn set — read its name off the vacated hand as it
            // leaves so it returns to the pack (like the unnamed-removal path),
            // instead of vanishing until the next full 'i' dump. Skip an item that
            // matches the incoming name (a re-confirm of the same weapon).
            var displaced = new List<string>();
            AsOneChange(() =>
            {
                PatchEquipped(list =>
                {
                    foreach (EquippedItem e in list)
                        if (e.Slot == "Weapon Hand"
                            && !string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                            displaced.Add(e.Name);
                    list.RemoveAll(e => e.Slot == "Weapon Hand");
                    list.Add(new EquippedItem(name, "Weapon Hand"));
                    return true;
                });
                // The newly-held weapon leaves the pack for the hand; the displaced
                // one returns to it.
                RemoveCarried(name);
                foreach (string old in displaced)
                    AddCarried(old);
            });
            return;
        }

        Match worn = WornItemRegex().Match(line);
        if (worn.Success)
        {
            string name = worn.Groups[1].Value.TrimEnd();
            // The wear line names no slot; resolve the item's true slot from game
            // data so "Snapshot Current" files it correctly (e.g. a re-worn vest
            // returns to "Torso", not a generic bucket). Fall back to a generic
            // "Worn" when unresolved — the next full 'i' dump restores the exact
            // 21-slot placement, and AC / ability bonuses sum regardless of slot.
            string slot = _slotResolver?.Invoke(name) ?? "Worn";
            (int Index, string Family)? filled = _lastRemovedPaired;
            _lastRemovedPaired = null;   // consumed — correlate only the immediately-following wear
            AsOneChange(() =>
            {
                PatchEquipped(list =>
                {
                    // A finger/wrist item worn right after removing a same-family
                    // slot-mate took THAT physical slot (the game's `eq <ring>`
                    // remove-then-wear pair) — insert it at the freed index so the
                    // snapshot keeps the game's slot order. Only when the family still
                    // has a surviving slot-mate (so we're filling a pair, not seeding
                    // one). Otherwise append, unchanged.
                    if (filled is { } f && PairedFamily(slot) is { } fam && fam == f.Family
                        && f.Index <= list.Count
                        && list.Exists(e => PairedFamily(e.Slot) == fam))
                        list.Insert(f.Index, new EquippedItem(name, slot));
                    else
                        list.Add(new EquippedItem(name, slot));
                    return true;
                });
                RemoveCarried(name);
            });
            return;
        }

        Match lit = LitLightRegex().Match(line);
        if (lit.Success)
        {
            _litSinceRead = lit.Groups[1].Value.TrimEnd();
            return;
        }

        // Stock's line for taking off the lit light. It has to be read ahead of the
        // plain removal, whose pattern would take "torch and extinguished it" for
        // the item's name.
        Match putOut = RemovedLitLightRegex().Match(line);
        if (putOut.Success)
        {
            string name = putOut.Groups[1].Value.TrimEnd();
            _lastRemovedPaired = null;
            AsOneChange(() => UnreadyLight(name));
            return;
        }

        Match removed = RemovedItemRegex().Match(line);
        if (removed.Success)
        {
            string name = removed.Groups[1].Value.TrimEnd();
            _lastRemovedPaired = null;
            AsOneChange(() =>
            {
                bool wasWorn = false;
                PatchEquipped(list =>
                {
                    int idx = list.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                    // Remember a vacated finger/wrist slot for the wear line that follows.
                    if (idx >= 0 && PairedFamily(list[idx].Slot) is { } fam)
                        _lastRemovedPaired = (idx, fam);
                    wasWorn = list.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
                    return wasWorn;
                });
                // Nothing worn by that name, and it is the light listed as lit: the
                // light is what came off. Adding a pack entry beside the lit one
                // would count the one light twice.
                if (!wasWorn && IsLitLight(name)) UnreadyLight(name);
                // A removed piece returns to the pack (unworn) until re-equipped.
                else AddCarried(name);
            });
            return;
        }

        if (NoWeaponReadiedRegex().IsMatch(line))
        {
            // Weapon removal is unnamed ("You now have no weapon readied."),
            // unlike the armor path which names its item. Read the outgoing
            // weapon's name from the worn set as it leaves so it can return to
            // the pack (unworn) just like removed armor.
            var unreadied = new List<string>();
            AsOneChange(() =>
            {
                PatchEquipped(list =>
                {
                    foreach (EquippedItem e in list)
                        if (e.Slot == "Weapon Hand") unreadied.Add(e.Name);
                    return list.RemoveAll(e => e.Slot == "Weapon Hand") > 0;
                });
                foreach (string name in unreadied)
                    AddCarried(name);
            });
            return;
        }

        Match pickedUp = PickedUpCurrencyRegex().Match(line);
        if (pickedUp.Success)
        {
            if (TryCoinCount(pickedUp.Groups[1].Value, out int amount))
            {
                lock (_lock) AdjustCurrency(pickedUp.Groups[2].Value, amount);
                Changed?.Invoke();
            }
            return;
        }

        Match dropped = DroppedCurrencyRegex().Match(line);
        if (dropped.Success)
        {
            if (TryCoinCount(dropped.Groups[1].Value, out int amount))
            {
                lock (_lock) AdjustCurrency(dropped.Groups[2].Value, -amount);
                Changed?.Invoke();
            }
            return;
        }

        // Stash: "You hid 219 copper farthings." — the coins leave the
        // purse exactly like a drop, so decrement holdings. Without this the
        // snapshot stays stale after a stash, and the next stash computes its
        // `hide` amounts from pre-stash holdings — sending amounts the
        // character no longer has ("You don't have N copper farthings to hide!").
        Match hidden = HidCurrencyRegex().Match(line);
        if (hidden.Success)
        {
            if (TryCoinCount(hidden.Groups[1].Value, out int amount))
            {
                lock (_lock) AdjustCurrency(hidden.Groups[2].Value, -amount);
                Changed?.Invoke();
            }
            return;
        }

        // Item stash: "You hid <item>." (Stock) or Paradigm's counted batch form
        // "You hid <N> <item>." — same verb as the coin line above, which already
        // returned for the coin nouns it knows. A board's own coin wording ("a gold
        // piece") still reaches here, so skip text shaped like a coin amount;
        // CashManager's coin path records those. An item merely named after a coin
        // (iron crown, sack of coins) has no amount in front and is an item, as on
        // the drop path.
        //
        // A stashed item leaves the pack exactly like a drop, so decrement the
        // carried list + weight estimate — otherwise the reading stays inflated
        // until the next full `i`, and the collect gates over-estimate weight and
        // wrongly skip (report paradigm-20260812-201631: 35 stashed orc-heads left
        // the estimate reading Heavy while the character was actually Medium, so
        // the cash "skip if Heavy" gate skipped a collect). Paradigm names the item
        // SINGULAR with a leading count ("You hid 35 orc-head."), so strip the
        // count and apply it N times.
        Match itemHidden = HidItemRegex().Match(line);
        if (itemHidden.Success)
        {
            string hiddenItem = itemHidden.Groups[1].Value.TrimEnd();
            if (hiddenItem.Length > 0 && !CoinAmountRegex().IsMatch(hiddenItem))
            {
                (int count, string name) = CountedCommand.SplitLeadingCount(hiddenItem);
                RemoveHeld(name, count);
                AdjustItemWeight(name, -count);
                _log?.Debug(LogCategory, $"hid item={name} count={count} — carried/weight decremented");
                ItemHidden?.Invoke(hiddenItem);
            }
            return;
        }

        // Training: "You hand over 350 copper farthings to train to the next level!"
        // / "You hand over 1 gold crown and you receive training to attain level 3."
        // The fee leaves the purse. Missed, the purse kept the coins the trainer took,
        // the weight estimate sat at the cap and every coin pickup and stash after it
        // was refused until an `i` (report paradigm-20261001-085357). The line gives
        // the fee, not which coins went, so it comes off by value; CashManager re-reads
        // the inventory afterwards to settle the mix.
        Match trained = TrainFeeRegex().Match(line);
        if (trained.Success)
        {
            long fee = ParsePriceToCopper(trained.Groups[1].Value);
            if (fee > 0)
            {
                lock (_lock) ApplyTransaction(-fee);
                Changed?.Invoke();
                _log?.Debug(LogCategory, $"training fee {fee} copper taken off the purse");
            }
            return;
        }

        Match deposit = DepositCurrencyRegex().Match(line);
        if (deposit.Success)
        {
            long amount = ParsePriceToCopper(deposit.Groups[1].Value);
            lock (_lock)
            {
                // An auto-deposit already decremented the purse optimistically at
                // dispatch (NoteAutoDeposit); this echo just confirms it. Consume
                // the pending amount rather than subtracting again. A manual
                // deposit (no pending) subtracts here exactly as before.
                long alreadyApplied = Math.Min(_pendingDepositCopper, amount);
                _pendingDepositCopper -= alreadyApplied;
                long residual = amount - alreadyApplied;
                if (residual > 0) ApplyTransaction(-residual);
            }
            Changed?.Invoke();
            if (amount > 0) BankDeposited?.Invoke(amount);
            return;
        }

        Match withdraw = WithdrawCurrencyRegex().Match(line);
        if (withdraw.Success)
        {
            long amount = ParsePriceToCopper(withdraw.Groups[1].Value);
            lock (_lock) ApplyTransaction(amount);
            Changed?.Invoke();
            if (amount > 0) BankWithdrew?.Invoke(amount);
            return;
        }

        // Buy: "You bought lantern for 396 copper farthings." (Stock) /
        // "You just bought broadsword for 26 gold crowns, 1 silver noble,
        // 16 copper farthings." (ParaMUD multi-currency). When the player
        // holds at least the exact denominations of the price, the game
        // pays per-coin without breaking change — subtract those coins and
        // keep the rest of the mix intact (ApplyBuyDeduction). Otherwise the
        // game breaks a larger coin to make change, re-consolidating the
        // whole purse — model that with the greedy ApplyTransaction path.
        Match bought = BoughtRegex().Match(line);
        if (bought.Success)
        {
            // The bought item enters the pack unworn (MajorMUD never auto-wields
            // a purchase), so it lands in the carried list until the player wears
            // or sells it.
            // Paradigm batches a buy into one counted line ("You bought 5 orc-head
            // for …") with the singular name and a single total price; strip the
            // count and apply the item side N times (the price tail below is the
            // total and needs no split).
            (int boughtCount, string boughtName) =
                CountedCommand.SplitLeadingCount(bought.Groups[1].Value.TrimEnd());
            // A purchase is a definitive "item entered the pack" signal, but
            // PatchCarried is gated on a loaded baseline (the first full `i`).
            // During character creation that `i` hasn't run — it only fires on
            // realm entry — so without seeding a baseline here the buy is dropped
            // and "Equip all" sees an empty pack (the reported bug). Treat the
            // first purchase as the baseline.
            EnsureLoadedBaseline();
            AddHeld(boughtName, boughtCount);
            AdjustItemWeight(boughtName, +boughtCount);

            string priceTail = bought.Groups[2].Value;
            long priceCopper = ParsePriceToCopper(priceTail);
            ItemBought?.Invoke(boughtName, boughtCount, priceCopper);
            if (priceCopper > 0)
            {
                (int Copper, int Silver, int Gold, int Platinum, int Runic) paid =
                    ParsePriceBreakdown(priceTail);
                lock (_lock)
                {
                    bool exactDenoms = _copper >= paid.Copper
                        && _silver >= paid.Silver
                        && _gold >= paid.Gold
                        && _platinum >= paid.Platinum
                        && _runic >= paid.Runic;
                    if (exactDenoms) ApplyBuyDeduction(paid);
                    else ApplyTransaction(-priceCopper);
                }
                Changed?.Invoke();
            }
            return;
        }

        // Sell: "You sold lantern for 101 copper farthings." — the game hands
        // back consolidated change, so the greedy re-decompose models it.
        Match sold = SoldRegex().Match(line);
        if (sold.Success)
        {
            // The sold item leaves the pack. Paradigm batches a sell into one
            // counted line ("You sold 5 orc-head for …"); strip the count.
            (int soldCount, string soldName) =
                CountedCommand.SplitLeadingCount(sold.Groups[1].Value.TrimEnd());
            RemoveHeld(soldName, soldCount);
            AdjustItemWeight(soldName, -soldCount);
            long price = ParsePriceToCopper(sold.Groups[2].Value);
            ItemSold?.Invoke(soldName, soldCount, price);

            if (price > 0)
            {
                lock (_lock) ApplyTransaction(price);
                Changed?.Invoke();
            }
            return;
        }

        // Give away: "You just gave a torch to Bob." — the item (or coins) leaves
        // us, exactly like a drop, so it must leave the pack / purse to keep the
        // Character Workshop inventory and the possession checks (path-item gate,
        // etc.) honest. The recipient is irrelevant to our own snapshot.
        Match gaveAway = GaveItemAwayRegex().Match(line);
        if (gaveAway.Success)
        {
            string gaveItem = gaveAway.Groups[1].Value.TrimEnd();
            ApplyGiveTransfer(gaveItem, -1);
            if (!CurrencyTokenRegex().IsMatch(gaveItem))
                ItemGivenAway?.Invoke(gaveItem, 1, gaveAway.Groups[2].Value.Trim());
            return;
        }

        // Receive: "Bob just gave you a torch." — the item (or coins) enters our
        // pack, like a get. Lets a hand-off from a party member (or another of
        // our characters) show up without waiting for the next full 'i' dump.
        Match received = ReceivedItemRegex().Match(line);
        if (received.Success)
        {
            string item = received.Groups[2].Value.TrimEnd();
            ApplyGiveTransfer(item, +1);
            if (!CurrencyTokenRegex().IsMatch(item))
                ItemReceived?.Invoke(item, received.Groups[1].Value.Trim());
            return;
        }

        // Receive, second wording: "Bob gives you magical quartz rod." / "Bob gives
        // you 2 black star key." — the item as its record names it, under a count
        // when there is more than one.
        if (TryMatchHandedItem(line, out string handedItem, out int handedCount, out string handedBy))
        {
            _log?.Debug(LogCategory, $"handed {handedCount} '{handedItem}' by {handedBy}");
            AddHeld(handedItem, handedCount);
            AdjustItemWeight(handedItem, +handedCount);
            for (int i = 0; i < handedCount; i++)
                ItemReceived?.Invoke(handedItem, handedBy);
            return;
        }

        // Give away, same wording: "You give 2 darkwood ring to Bob."
        Match handedAway = HandedAwayRegex().Match(line);
        if (handedAway.Success
            && TryReadHandedName(handedAway.Groups[1].Value, out string awayItem, out int awayCount))
        {
            RemoveHeld(awayItem, awayCount);
            AdjustItemWeight(awayItem, -awayCount);
            ItemGivenAway?.Invoke(awayItem, awayCount, handedAway.Groups[2].Value);
            return;
        }

        // Coins handed over. The engine words these apart from an item, with no
        // "just" and no full stop: "You gave Bob 30 gold" / "Bob gave you 30 gold"
        // for `give`, and the full coin noun ("30 gold crowns") for `share`.
        Match coinsAway = GaveCoinsAwayRegex().Match(line);
        if (coinsAway.Success
            && TryApplyCoinHandOver(coinsAway.Groups[2].Value, coinsAway.Groups[3].Value, -1, coinsAway.Groups[1].Value))
            return;

        Match coinsIn = ReceivedCoinsRegex().Match(line);
        if (coinsIn.Success
            && TryApplyCoinHandOver(coinsIn.Groups[1].Value, coinsIn.Groups[2].Value, +1))
            return;

        // The same in the second wording, which always prints the coin's full noun:
        // "Bob gives you 30 platinum pieces" / "You give 2 runic coins to Bob".
        Match handedCoins = HandedCoinsRegex().Match(line);
        if (handedCoins.Success && CoinNounSuffixRegex().IsMatch(handedCoins.Groups[2].Value)
            && TryApplyCoinHandOver(handedCoins.Groups[1].Value, handedCoins.Groups[2].Value, +1))
            return;

        Match handedCoinsAway = HandedCoinsAwayRegex().Match(line);
        if (handedCoinsAway.Success && CoinNounSuffixRegex().IsMatch(handedCoinsAway.Groups[2].Value)
            && TryApplyCoinHandOver(handedCoinsAway.Groups[1].Value, handedCoinsAway.Groups[2].Value, -1,
                handedCoinsAway.Groups[3].Value))
            return;

        // Failed give: "You don't have a torch to give." — no state change (we
        // never held it), logged so a give-driven flow can see the attempt bounced.
        if (GiveFailedRegex().IsMatch(line))
        {
            _log?.Debug(LogCategory, "give bounced: item not held");
            return;
        }

        // The Stock engine's answer to a coin give at something that isn't a player,
        // or at a player hidden from us (GAME_MECHANICS "Giving items and coins to
        // another player"). Nothing moved, and the line names nobody.
        if (line == CoinGiveMisaimedLine)
        {
            _log?.Debug(LogCategory, "coin give refused: nobody of that name here to give to");
            CoinGiveMisaimed?.Invoke();
            return;
        }

        // A coin give of more of a coin than we hold: the purse on record was wrong
        // about that coin. Nothing moved.
        if (CoinGiveLackedCoinsRegex().IsMatch(line))
        {
            _log?.Debug(LogCategory, $"coin give refused: {line}");
            CoinGiveLackedCoins?.Invoke();
            return;
        }

        // A give the other player's settings or pack turned down, or an item that
        // can't be given: the Stock engine's three refusals. Nothing moved.
        Match refusedBy = GiveRefusedByRegex().Match(line);
        if (refusedBy.Success || line == GiveNotAllowedLine)
        {
            _log?.Debug(LogCategory, $"give refused: {line}");
            GiveRefused?.Invoke(refusedBy.Success ? refusedBy.Groups[1].Value : null);
            return;
        }

        // Get: "You took rusty dagger." — the item enters the pack unworn. The
        // currency pickup ("You picked up N ...", no trailing period) is a
        // different verb and is matched (and returned) above, so only item lines
        // reach here.
        Match gotItem = TookItemRegex().Match(line);
        if (gotItem.Success)
        {
            // Paradigm batches a get into one counted line ("You took 5 orc-head.")
            // with the singular name; strip the count and apply it N times.
            (int count, string name) = CountedCommand.SplitLeadingCount(gotItem.Groups[1].Value.TrimEnd());
            AddHeld(name, count);
            AdjustItemWeight(name, +count);
            ItemTaken?.Invoke(name, count);
            return;
        }

        // Drop: "You dropped torch." (or Paradigm's counted "You dropped 5 orc-head.")
        // — the item leaves the pack. Currency drops carry a coin noun and are
        // matched above.
        Match droppedItem = DropItemRegex().Match(line);
        if (droppedItem.Success)
        {
            (int count, string name) = CountedCommand.SplitLeadingCount(droppedItem.Groups[1].Value.TrimEnd());
            RemoveHeld(name, count);
            AdjustItemWeight(name, -count);
            ItemDropped?.Invoke(name, count);
        }
    }

    // ----- full parse --------------------------------------------------

    private void ParseFullInventory()
    {
        string? wealthLine = null;
        string? encumbranceLine = null;
        var currencyTokens = new List<string>();

        // The capture buffer is the raw ~80-col word-wrapped dump, in order:
        //   You are carrying <items>.
        //   You have the following keys: <keys>.   (or "You have no keys.")
        //   Wealth: <...>
        //   Encumbrance: <...>
        // Any of these sentences can wrap across several rows. Classify each row
        // by the last section marker seen, so a wrapped continuation stays with
        // its own section instead of bleeding into the next — a wrapped keys line
        // otherwise stranded its trailing "key" out of the last key name and into
        // the carried-items text.
        var itemRows = new List<string>();
        var keyRows = new List<string>();
        var section = InventorySection.None;
        foreach (string raw in _captureBuffer)
        {
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith("You are carrying", StringComparison.Ordinal))
            { section = InventorySection.Items; itemRows.Add(trimmed); }
            else if (trimmed.StartsWith("You have the following keys:", StringComparison.Ordinal))
            { section = InventorySection.Keys; keyRows.Add(trimmed); }
            else if (trimmed.StartsWith("You have no keys", StringComparison.Ordinal))
                section = InventorySection.Keys;   // no key names to collect
            else if (trimmed.StartsWith("Wealth:", StringComparison.Ordinal))
            { section = InventorySection.Wealth; wealthLine = trimmed; }
            else if (trimmed.StartsWith("Encumbrance:", StringComparison.Ordinal))
            { section = InventorySection.Encumbrance; encumbranceLine = trimmed; }
            else
            {
                switch (section)
                {
                    case InventorySection.Items: itemRows.Add(trimmed); break;
                    case InventorySection.Keys: keyRows.Add(trimmed); break;
                    case InventorySection.Wealth: wealthLine = Join(wealthLine, trimmed); break;
                    case InventorySection.Encumbrance: encumbranceLine = Join(encumbranceLine, trimmed); break;
                }
            }
        }

        string? keysLine = keyRows.Count > 0 ? string.Join(" ", keyRows) : null;

        // Reconstruct the word-wrapped items text and sort each token: a coin, the
        // lit light, a worn piece, or a carried item.
        string itemsText = string.Join(" ", itemRows);
        const string prefix = "You are carrying ";
        // Check the "nothing" form first: it also starts with the prefix, so
        // stripping the prefix would leave the literal "nothing" to be split into a
        // phantom carried item (which then leaks into @have, @drop-all, etc.).
        if (itemsText.StartsWith("You are carrying nothing", StringComparison.Ordinal))
            itemsText = string.Empty;
        else if (itemsText.StartsWith(prefix, StringComparison.Ordinal))
            itemsText = itemsText[prefix.Length..];

        // The dump's items sentence ends with a period ("... 5 copper
        // farthings."). Drop it so the final ", "-split token is the bare
        // currency entry the anchored CurrencyTokenRegex expects.
        itemsText = itemsText.TrimEnd('.', ' ');

        int copper = 0, silver = 0, gold = 0, platinum = 0, runic = 0;
        var equipped = new List<EquippedItem>();
        var carried = new List<string>();
        ReadiedLight? readiedLight = null;
        if (itemsText.Length > 0)
        {
            foreach (string token in itemsText.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryParseCurrency(token, out int count, out string denom))
                {
                    switch (denom)
                    {
                        case "runic": runic = count; break;
                        case "platinum": platinum = count; break;
                        case "gold": gold = count; break;
                        case "silver": silver = count; break;
                        case "copper": copper = count; break;
                    }
                    currencyTokens.Add(token);
                    continue;
                }

                // A lit light prints inline with a "(Readied/N)" suffix instead
                // of a slot label; capture the countdown and don't file it as a
                // plain carried item.
                Match lit = ReadiedLightRegex().Match(token);
                if (lit.Success)
                {
                    string name = lit.Groups[1].Value.TrimEnd();
                    if (name.Length > 0 && int.TryParse(lit.Groups[2].Value, out int readied))
                        readiedLight = new ReadiedLight(name, readied);
                    continue;
                }

                // Worn items carry a trailing "(<Slot>)" suffix; carried-but-
                // unworn items don't. Harvest the worn ones for equipment-bonus
                // aggregation in the Character Workshop; the unworn ones feed
                // death-recovery's "inventory lost" capture.
                Match eq = EquippedSlotRegex().Match(token);
                if (eq.Success)
                {
                    string name = eq.Groups[1].Value.TrimEnd();
                    string slot = NormalizeSlot(eq.Groups[2].Value);
                    if (name.Length > 0)
                        equipped.Add(new EquippedItem(name, slot));
                }
                else if (token.Length > 0)
                {
                    carried.Add(token);
                }
            }
        }

        long wealthCopper = ComputeWealth(copper, silver, gold, platinum, runic);
        if (wealthLine is not null)
        {
            Match wm = WealthRegex().Match(wealthLine);
            if (wm.Success && long.TryParse(wm.Groups[1].Value, out long w))
                wealthCopper = w;   // authoritative — game's own consolidated total
        }

        int curWeight = 0, maxWeight = 0, percentage = 0;
        EncumbranceLevel category = EncumbranceLevel.Unknown;
        if (encumbranceLine is not null)
        {
            Match em = EncumbranceRegex().Match(encumbranceLine);
            if (em.Success)
            {
                int.TryParse(em.Groups[1].Value, out curWeight);
                int.TryParse(em.Groups[2].Value, out maxWeight);
                int.TryParse(em.Groups[4].Value, out percentage);
                category = EncumbranceParser.ParseLevel(encumbranceLine);
            }
        }

        // "You have the following keys:  black star key, brass key." → individual
        // names. Absent line (or "You have no keys.") leaves the list empty.
        var keys = new List<string>();
        if (keysLine is not null)
        {
            const string keyPrefix = "You have the following keys:";
            string keyBody = keysLine[keyPrefix.Length..].Trim().TrimEnd('.', ' ');
            foreach (string name in keyBody.Split(", ",
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (name.Length > 0) keys.Add(name);
        }

        lock (_lock)
        {
            _copper = copper;
            _silver = silver;
            _gold = gold;
            _platinum = platinum;
            _runic = runic;
            _wealthCopper = wealthCopper;
            // The dump is the authoritative purse — any optimistic auto-deposit
            // still awaiting its echo is now baked into this total, so drop the
            // pending reconciliation to avoid suppressing a later real deposit.
            _pendingDepositCopper = 0;
            _curWeight = curWeight;
            _maxWeight = maxWeight;
            _percentage = percentage;
            _category = category;
            _equipped = equipped;
            _carried = NormalizeStacks(carried);
            _readiedLight = readiedLight;
            _litSinceRead = null;
            _keys = keys;
            _loaded = true;
            _lastUpdated = DateTimeOffset.Now;
        }

        _log?.Debug(LogCategory,
            $"parsed: wealth={wealthCopper} copper, enc={curWeight}/{maxWeight} {category} [{percentage}%], worn={equipped.Count}, carried={carried.Count}, keys={keys.Count}"
            + (readiedLight is { } rl ? $", lit={rl.Name} (Readied/{rl.Readied})" : ""));
        Changed?.Invoke();
        FullInventoryParsed?.Invoke();
    }

    // The paired-slot family a worn-slot label belongs to — "Finger" or "Wrist" —
    // or null for any non-paired slot. Matches both the bare "Finger"/"Wrist" the
    // 'i' dump prints and the "Finger1"/"Wrist2" form a resolver may hand back.
    private static string? PairedFamily(string? slot)
    {
        if (string.IsNullOrEmpty(slot)) return null;
        if (slot.StartsWith("Finger", StringComparison.OrdinalIgnoreCase)) return "Finger";
        if (slot.StartsWith("Wrist", StringComparison.OrdinalIgnoreCase))  return "Wrist";
        return null;
    }

    // One game line can move a piece between the pack and the worn set (a wear, a
    // wield, a remove) or between the pack and the lit light. Both sides are
    // patched before anyone is told, and Changed is raised once: a listener called
    // in between would see the piece in both places and count it twice, or in
    // neither and take it for lost. Lines are read on one thread, so the two
    // fields need no lock.
    private int _movesUnderWay;
    private bool _changedDuringMove;

    private void AsOneChange(Action patch)
    {
        _movesUnderWay++;
        try
        {
            patch();
        }
        finally
        {
            if (--_movesUnderWay == 0 && _changedDuringMove)
            {
                _changedDuringMove = false;
                Changed?.Invoke();
            }
        }
    }

    // What a patch of the pack or the worn set raises in place of Changed itself.
    private void NotifyChanged()
    {
        if (_movesUnderWay > 0) _changedDuringMove = true;
        else Changed?.Invoke();
    }

    // Apply an in-place edit to the worn set, publishing only if it changed.
    // Gated on a loaded baseline: patching an empty set would imply the
    // character wears nothing but the piece just equipped — misleading until
    // the first full 'i' establishes the real loadout.
    private void PatchEquipped(Func<List<EquippedItem>, bool> mutate)
    {
        bool changed = false;
        lock (_lock)
        {
            if (_loaded)
            {
                var list = new List<EquippedItem>(_equipped);
                if (mutate(list))
                {
                    _equipped = list;
                    changed = true;
                }
            }
        }
        if (changed) NotifyChanged();
    }

    // Establish the loaded baseline from an acquisition when no full 'i' has run
    // yet. PatchCarried/PatchEquipped are no-ops until _loaded so a stray mid-game
    // patch can't imply a one-item pack — but character creation reaches the
    // starting-gear store before any 'i' (that only fires on realm entry), so
    // creation-time purchases would be dropped. A purchase is unambiguous, and
    // the creation pack legitimately holds only what was just bought, so the
    // first buy seeds the baseline. Self-correcting: the realm-entry 'i' later
    // replaces the carried list wholesale (ParseFullInventory), so even a wrong
    // guess is overwritten.
    private void EnsureLoadedBaseline()
    {
        bool seeded = false;
        lock (_lock)
        {
            if (!_loaded)
            {
                _loaded = true;
                seeded = true;
            }
        }
        if (seeded)
            _log?.Info(LogCategory,
                "Seeded inventory baseline from a purchase (no prior full inventory) — creation-time buys now track.");
    }

    // Carried-list counterpart to PatchEquipped: same loaded-baseline gate (an
    // add before the first 'i' would imply the pack holds only that one item),
    // same publish-only-if-changed contract.
    private void PatchCarried(Func<List<string>, bool> mutate)
    {
        bool changed = false;
        lock (_lock)
        {
            if (_loaded)
            {
                var list = new List<string>(_carried);
                if (mutate(list))
                {
                    _carried = list;
                    changed = true;
                }
            }
        }
        if (changed) NotifyChanged();
    }

    // A give / receive of coins ("... 30 gold crowns ...") adjusts currency, not
    // the pack; anything else is an item entering (+1) or leaving (-1) it. The
    // currency guard stops a coin hand-off from being filed as a phantom carried
    // item and keeps the purse in step, mirroring how drop / pickup split coins
    // from items elsewhere in this class.
    private void ApplyGiveTransfer(string name, int sign)
    {
        Match coin = CurrencyTokenRegex().Match(name);
        if (coin.Success && int.TryParse(coin.Groups[1].Value, out int count))
        {
            lock (_lock) AdjustCurrency(coin.Groups[2].Value, sign * count);
            Changed?.Invoke();
            return;
        }

        if (sign > 0) AddHeld(name, 1);
        else RemoveHeld(name, 1);
        AdjustItemWeight(name, sign);
    }

    // An item that comes to us (handed over, picked up, bought): a key goes on the
    // key ring, anything else in the pack.
    private void AddHeld(string name, int count)
    {
        if (IsKey(name)) PatchKeyRing(name, +count);
        else AddCarried(name, count);
    }

    // An item that leaves us (handed over, dropped, hidden, sold). A key nothing
    // told us was one is in the pack, so the pack gives up its copies first and
    // the ring the rest. Without the ring a key that had left stayed "held", and a
    // door it opens stayed passable to the route planner.
    private void RemoveHeld(string name, int count)
    {
        int fromPack;
        lock (_lock)
        {
            int idx = FindCarriedIndex(_carried, name);
            int inPack = idx < 0 ? 0 : CountedCommand.SplitLeadingCount(_carried[idx]).Count;
            fromPack = Math.Min(count, inPack);
        }
        AsOneChange(() =>
        {
            RemoveCarried(name, fromPack);
            int rest = count - fromPack;
            // The lit light is listed apart from the pack, so a copy that left with
            // no pack entry to take it from was the lit one. With both a spare and
            // the lit one held, which of them the game took can't be told (Stock
            // takes the lower slot): the spare is taken off first, and the total
            // is right either way until the next read says which is lit.
            if (rest > 0 && IsLitLight(name))
            {
                ClearLitLight();
                rest--;
            }
            PatchKeyRing(name, -rest);
        });
    }

    private bool IsLitLight(string name)
    {
        lock (_lock)
            return _readiedLight is { } lit && string.Equals(lit.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearLitLight()
    {
        lock (_lock)
        {
            if (_readiedLight is null) return;
            _readiedLight = null;
        }
        NotifyChanged();
    }

    // The lit light was taken off and put out: it is an ordinary pack copy again.
    // A light lit since the last full read was never listed apart, so its pack
    // entry is still there and nothing is added for it.
    private void UnreadyLight(string name)
    {
        bool listedApart = IsLitLight(name);
        ClearLitLight();
        if (listedApart) AddCarried(name);
        if (string.Equals(_litSinceRead, name, StringComparison.OrdinalIgnoreCase)) _litSinceRead = null;
    }

    // The lit light burned out (KnownPatterns.LightBurnedOut; the line doesn't name
    // the item in every wording, so the light is whichever is known to be lit). It
    // is no longer lit, and unless its record is Retain After Uses it has left the
    // pack: Stock's rule (GAME_MECHANICS "Light sources"), taken for Paradigm too,
    // whose rule isn't recorded. Left as it was, the light stayed "lit" until the
    // next full read, and a death in between recorded a light that no longer
    // existed: a Stock pile then waits for it for good.
    //   - listed apart by the last full read: it comes off the lit slot, and goes
    //     back into the pack only if it is kept when spent;
    //   - lit since (Stock's `You lit the <item>.`): its copy is still in the pack
    //     list, and is taken out unless it is kept when spent.
    // A light lit since the last read on a realm that words the lighting some other
    // way can't be told from the rest of the pack, and waits for the next read.
    public void NoteLitLightBurnedOut()
    {
        string? litSince = _litSinceRead;
        _litSinceRead = null;
        string? listedApart;
        lock (_lock) listedApart = _readiedLight?.Name;

        if (litSince is not null && !string.Equals(litSince, listedApart, StringComparison.OrdinalIgnoreCase))
        {
            bool kept = _staysWhenSpent?.Invoke(litSince) == true;
            if (!kept)
                AsOneChange(() =>
                {
                    RemoveCarried(litSince);
                    AdjustItemWeight(litSince, -1);
                });
            _log?.Info(LogCategory, $"The lit {litSince} burned out — "
                + (kept ? "kept in the pack, spent." : "taken out of the pack."));
            return;
        }
        if (listedApart is null)
        {
            _log?.Info(LogCategory, "A light burned out, but none is known to be lit — the pack is left as it is until the next inventory read.");
            return;
        }
        bool stays = _staysWhenSpent?.Invoke(listedApart) == true;
        AsOneChange(() =>
        {
            ClearLitLight();
            if (stays) AddCarried(listedApart);
            else AdjustItemWeight(listedApart, -1);
        });
        _log?.Info(LogCategory, $"The lit {listedApart} burned out — "
            + (stays ? "no longer lit, kept in the pack, spent." : "no longer lit, and gone from what is held."));
    }

    private bool IsKey(string name)
    {
        if (_isKey?.Invoke(name) == true) return true;
        lock (_lock) return FindCarriedIndex(_keys, name) >= 0;
    }

    // Add (or, negative, take) copies of a key on the ring, stacked the way the 'i'
    // dump lists them. Gated on a loaded baseline, as PatchCarried is.
    private void PatchKeyRing(string name, int delta)
    {
        if (delta == 0) return;
        bool changed = false;
        lock (_lock)
        {
            if (_loaded)
            {
                var ring = new List<string>(_keys);
                int idx = FindCarriedIndex(ring, name);
                int have = idx < 0 ? 0 : CountedCommand.SplitLeadingCount(ring[idx]).Count;
                int now = Math.Max(0, have + delta);
                if (now != have)
                {
                    if (now == 0) ring.RemoveAt(idx);
                    else if (idx < 0) ring.Add(FormatStack(name, now));
                    else ring[idx] = FormatStack(name, now);
                    _keys = ring;
                    changed = true;
                }
            }
        }
        if (changed) NotifyChanged();
    }

    // "<Player> gives you <item>." is a player's hand-over only when the item is a
    // record name word for word. An NPC's keyword give prints the same shape as
    // flavour text ("Dhelvanen gives you a green potion."), which names the item
    // its own way and is no proof the give landed, so it must not add a row.
    private bool TryMatchHandedItem(string line, out string item, out int count, out string giver)
    {
        item = giver = string.Empty;
        count = 0;
        Match m = HandedItemRegex().Match(line);
        if (!m.Success || !TryReadHandedName(m.Groups[2].Value, out item, out count)) return false;
        giver = m.Groups[1].Value;
        return true;
    }

    // The item a hand-over in the second wording names: a record name, alone or
    // under a leading count ("2 black star key"). A record name that itself opens
    // with a number is taken whole.
    private bool TryReadHandedName(string text, out string name, out int count)
    {
        name = string.Empty;
        count = 0;
        if (_isItemRecordName is null) return false;
        (count, name) = CountedCommand.SplitLeadingCount(text);
        if (_isItemRecordName(name)) return true;
        if (!_isItemRecordName(text)) return false;
        (count, name) = (1, text);
        return true;
    }

    // givenTo: the recipient a hand-over of ours names; null for coins handed to us.
    private bool TryApplyCoinHandOver(string countText, string coin, int sign, string? givenTo = null)
    {
        if (HandOverCoinNoun(coin) is not { } noun || !int.TryParse(countText, out int count))
            return false;
        lock (_lock) AdjustCurrency(noun, sign * count);
        Changed?.Invoke();
        // The noun's first word is the metal CurrencyHoldings.ToCopper keys on.
        if (givenTo is not null)
        {
            // A coin noun whose first word is none of the four metals is the fifth
            // coin under the name its board gave it, and is worth what a runic is:
            // the rule the bare-word wording already goes by.
            int space = noun.IndexOf(' ');
            string metal = (space > 0 ? noun[..space] : noun).ToLowerInvariant();
            if (metal is not ("copper" or "silver" or "gold" or "platinum")) metal = "runic";
            CoinsGivenAway?.Invoke(givenTo, CurrencyHoldings.ToCopper(metal, count));
        }
        return true;
    }

    // The coin a coin hand-over names, as the noun AdjustCurrency keys on. `share`
    // prints the full noun; `give` prints the bare metal, and for the fifth coin a
    // name each board sets itself, so any other single word is that coin.
    private static string? HandOverCoinNoun(string coin)
    {
        if (CoinNounSuffixRegex().IsMatch(coin)) return coin;
        if (coin.Contains(' ')) return null;
        return coin.ToLowerInvariant() switch
        {
            "copper" => "copper farthing",
            "silver" => "silver noble",
            "gold" => "gold crown",
            "platinum" => "platinum piece",
            _ => "runic coin",
        };
    }

    // Index of the carried entry whose SINGULAR name equals `name`, ignoring any
    // leading stack count ("43 black diamond" matches "black diamond"), or -1. So a
    // get/drop of a single item finds and updates the stacked row instead of missing it.
    private static int FindCarriedIndex(IReadOnlyList<string> list, string name)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(CountedCommand.SplitLeadingCount(list[i]).Name, name,
                    StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // Render a stack the way the game does in the `i` dump: a bare singular name for
    // one, a count-prefixed singular for more ("black diamond" / "44 black diamond").
    private static string FormatStack(string name, int count)
        => count <= 1 ? name : $"{count} {name}";

    // Collapse identical carried items into one "N name" stack, first-seen order. The
    // `i` dump lists some items as repeated bare tokens ("torch, torch, torch") and
    // others pre-stacked ("43 black diamond"); folding both to a single entry per item
    // gives the add/remove patches one row to update and stops @inv / @have / the
    // Character Info list from showing duplicate rows as auto-gets land.
    private static List<string> NormalizeStacks(IReadOnlyList<string> items)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in items)
        {
            (int n, string name) = CountedCommand.SplitLeadingCount(token);
            if (!counts.ContainsKey(name)) order.Add(name);
            counts[name] = counts.GetValueOrDefault(name) + n;
        }
        var result = new List<string>(order.Count);
        foreach (string name in order) result.Add(FormatStack(name, counts[name]));
        return result;
    }

    // Add `count` copies of `name` to the pack, STACKING onto an existing entry (bare
    // or count-prefixed) rather than appending duplicate rows. Auto-get / buy / receive
    // used to `list.Add` a fresh bare row per item, so a pack that dumped "43 black
    // diamond" grew a separate "black diamond" row per pickup — corrupting @inv / @have
    // and the Character Info list. Gated on a loaded baseline via PatchCarried.
    private void AddCarried(string name, int count = 1)
    {
        if (count <= 0) return;
        PatchCarried(list =>
        {
            int idx = FindCarriedIndex(list, name);
            if (idx < 0) { list.Add(FormatStack(name, count)); return true; }
            int have = CountedCommand.SplitLeadingCount(list[idx]).Count;
            list[idx] = FormatStack(name, have + count);
            return true;
        });
    }

    // Remove `count` copies of `name` from the pack: decrement a stacked entry
    // ("43 black diamond" → "42 black diamond", removing the row at zero) as readily as
    // a bare one. Matches by singular name so a drop/sell of one finds the stack — the
    // old exact-string match never saw a count-prefixed entry and silently no-op'd.
    private void RemoveCarried(string name, int count = 1)
    {
        if (count <= 0) return;
        PatchCarried(list =>
        {
            int idx = FindCarriedIndex(list, name);
            if (idx < 0) return false;
            int left = CountedCommand.SplitLeadingCount(list[idx]).Count - count;
            if (left <= 0) list.RemoveAt(idx);
            else list[idx] = FormatStack(name, left);
            return true;
        });
    }

    // Section a row in the word-wrapped inventory dump belongs to, so a wrapped
    // continuation row folds back into its own sentence instead of the next.
    private enum InventorySection { None, Items, Keys, Wealth, Encumbrance }

    private static string Join(string? head, string tail) =>
        string.IsNullOrEmpty(head) ? tail : head + " " + tail;

    // ----- currency math (all under _lock) -----------------------------

    // Coin count from a currency line's count group: a digit, or the article "a"
    // (a single coin). Used by the pickup / drop / stash currency patches.
    private static bool TryCoinCount(string countGroup, out int amount)
    {
        if (countGroup.Equals("a", StringComparison.OrdinalIgnoreCase)) { amount = 1; return true; }
        return int.TryParse(countGroup, out amount);
    }

    private void AdjustCurrency(string coinType, int amount)
    {
        long oldCoins = TotalCoins();

        // Key on the denomination's noun (the trailing word), not its leading
        // word — every noun is unique, so "coin" identifies runic even when a
        // board renames the runic word (e.g. "quatloos coin").
        string normalized = coinType.TrimEnd('s');
        if (normalized.EndsWith("coin", StringComparison.OrdinalIgnoreCase))
            _runic = Math.Max(0, _runic + amount);
        else if (normalized.StartsWith("platinum piece", StringComparison.OrdinalIgnoreCase))
            _platinum = Math.Max(0, _platinum + amount);
        else if (normalized.StartsWith("gold crown", StringComparison.OrdinalIgnoreCase))
            _gold = Math.Max(0, _gold + amount);
        else if (normalized.StartsWith("silver noble", StringComparison.OrdinalIgnoreCase))
            _silver = Math.Max(0, _silver + amount);
        else if (normalized.StartsWith("copper farthing", StringComparison.OrdinalIgnoreCase))
            _copper = Math.Max(0, _copper + amount);

        _wealthCopper = ComputeWealth(_copper, _silver, _gold, _platinum, _runic);
        ApplyWeightDelta(oldCoins);
    }

    private void ApplyTransaction(long copperDelta)
    {
        long oldCoins = TotalCoins();

        long wealth = Math.Max(0, _wealthCopper + copperDelta);
        _wealthCopper = wealth;

        _runic = (int)Math.Min(int.MaxValue, wealth / 1_000_000L); wealth %= 1_000_000L;
        _platinum = (int)Math.Min(int.MaxValue, wealth / 10_000L); wealth %= 10_000L;
        _gold = (int)Math.Min(int.MaxValue, wealth / 100L); wealth %= 100L;
        _silver = (int)Math.Min(int.MaxValue, wealth / 10L); wealth %= 10L;
        _copper = (int)Math.Min(int.MaxValue, wealth);

        ApplyWeightDelta(oldCoins);
    }

    // Buy-time per-coin deduction: the player held the exact denominations of
    // the price, so the game pays without breaking change. Subtract those
    // coins, leave the rest of the mix as-is, and recompute wealth + weight.
    // The caller verifies sufficiency per denomination before calling.
    private void ApplyBuyDeduction((int Copper, int Silver, int Gold, int Platinum, int Runic) paid)
    {
        long oldCoins = TotalCoins();

        _copper = Math.Max(0, _copper - paid.Copper);
        _silver = Math.Max(0, _silver - paid.Silver);
        _gold = Math.Max(0, _gold - paid.Gold);
        _platinum = Math.Max(0, _platinum - paid.Platinum);
        _runic = Math.Max(0, _runic - paid.Runic);

        _wealthCopper = ComputeWealth(_copper, _silver, _gold, _platinum, _runic);
        ApplyWeightDelta(oldCoins);
    }

    private long TotalCoins() => (long)_copper + _silver + _gold + _platinum + _runic;

    private void ApplyWeightDelta(long oldCoins)
    {
        long weightDelta = TotalCoins() / 3 - oldCoins / 3;
        _curWeight = (int)Math.Max(0, _curWeight + weightDelta);
        RecomputeEncumbrancePercent();
    }

    // Re-derive percentage + bracket from the current / max weight. Shared by
    // the coin-weight and item-weight adjustments.
    private void RecomputeEncumbrancePercent()
    {
        _percentage = _maxWeight > 0 ? (int)((long)_curWeight * 100 / _maxWeight) : 0;
        _category = DeriveCategory(_percentage);
    }

    // Move the live encumbrance estimate as `delta` copies of an item enter (+n)
    // or leave (-n) the pack, reading its carry weight (MDB Encum) from the active
    // game data. Single get/drop pass ±1; a Paradigm counted stash passes -N. A
    // null resolver, an unresolved name, or a zero-weight item is a no-op — and the
    // next full 'i' dump re-bases the reading regardless, so a stale estimate
    // self-corrects. Gated on a loaded baseline like the carried list: adjusting
    // from a 0/0 reading would be meaningless.
    private void AdjustItemWeight(string itemName, int delta)
    {
        if (_itemWeight is null) return;
        if (_itemWeight(itemName) is not int weight || weight == 0) return;
        bool changed = false;
        lock (_lock)
        {
            if (_loaded)
            {
                _curWeight = (int)Math.Max(0, _curWeight + (long)delta * weight);
                RecomputeEncumbrancePercent();
                changed = true;
            }
        }
        if (changed) NotifyChanged();
    }

    private static long ComputeWealth(int copper, int silver, int gold, int platinum, int runic)
    {
        try
        {
            return checked(
                copper +
                silver * 10L +
                gold * 100L +
                platinum * 10_000L +
                runic * 1_000_000L);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static EncumbranceLevel DeriveCategory(int percentage)
        => EncumbranceCategory.ForPercent(percentage);

    // The game labels a two-handed weapon "(Two handed)" in your own inventory
    // but "(Weapon Hand)" in player listings; fold both to one slot so callers
    // see a single weapon slot regardless of where the line came from.
    private static string NormalizeSlot(string slot) =>
        slot.Equals("Two handed", StringComparison.Ordinal) ? "Weapon Hand" : slot;

    // True when token is exactly one currency entry (e.g. "30 platinum
    // pieces"). The match must span the whole token so an item name that merely
    // contains a coin word doesn't read as currency.
    private static bool TryParseCurrency(string token, out int count, out string denom)
    {
        count = 0;
        denom = string.Empty;
        Match m = CurrencyTokenRegex().Match(token);
        if (!m.Success || m.Index != 0 || m.Length != token.Length) return false;
        if (!int.TryParse(m.Groups[1].Value, out count)) return false;
        // Match on the denomination's unique noun so a board-renamed runic word
        // still resolves to the canonical "runic" key.
        denom = m.Groups[2].Value.TrimEnd('s') switch
        {
            string s when s.EndsWith("coin", StringComparison.Ordinal) => "runic",
            string s when s.EndsWith("piece", StringComparison.Ordinal) => "platinum",
            string s when s.EndsWith("crown", StringComparison.Ordinal) => "gold",
            string s when s.EndsWith("noble", StringComparison.Ordinal) => "silver",
            _ => "copper",
        };
        return true;
    }

    // Sum a price tail ("26 gold crowns, 1 silver noble, 16 copper farthings"
    // or "nothing") into copper farthings using the standard ratios.
    private static long ParsePriceToCopper(string priceTail)
    {
        if (string.IsNullOrEmpty(priceTail)) return 0;
        long total = 0;
        foreach (Match m in PriceSegmentRegex().Matches(priceTail))
        {
            if (!long.TryParse(m.Groups[1].Value, out long count)) continue;
            string denom = m.Groups[2].Value.TrimEnd('s');
            total += denom switch
            {
                var d when d.EndsWith("coin", StringComparison.Ordinal) => count * 1_000_000L,
                var d when d.EndsWith("piece", StringComparison.Ordinal) => count * 10_000L,
                var d when d.EndsWith("crown", StringComparison.Ordinal) => count * 100L,
                var d when d.EndsWith("noble", StringComparison.Ordinal) => count * 10L,
                var d when d.EndsWith("farthing", StringComparison.Ordinal) => count,
                _ => 0,
            };
        }
        return total;
    }

    // Same price-tail input as ParsePriceToCopper, but returns the
    // per-denomination coin counts so the buy handler can tell an
    // exact-denomination payment (no change broken) from a forced-break one.
    private static (int Copper, int Silver, int Gold, int Platinum, int Runic) ParsePriceBreakdown(string priceTail)
    {
        int c = 0, s = 0, g = 0, p = 0, r = 0;
        if (string.IsNullOrEmpty(priceTail)) return (c, s, g, p, r);
        foreach (Match m in PriceSegmentRegex().Matches(priceTail))
        {
            if (!int.TryParse(m.Groups[1].Value, out int count)) continue;
            switch (m.Groups[2].Value.TrimEnd('s'))
            {
                case var d when d.EndsWith("coin", StringComparison.Ordinal): r += count; break;
                case var d when d.EndsWith("piece", StringComparison.Ordinal): p += count; break;
                case var d when d.EndsWith("crown", StringComparison.Ordinal): g += count; break;
                case var d when d.EndsWith("noble", StringComparison.Ordinal): s += count; break;
                case var d when d.EndsWith("farthing", StringComparison.Ordinal): c += count; break;
            }
        }
        return (c, s, g, p, r);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }

    // ----- regexes -----------------------------------------------------

    [GeneratedRegex(@"^(\d+) (\w+ coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)$")]
    private static partial Regex CurrencyTokenRegex();

    [GeneratedRegex(@"^Wealth:\s+(\d+)\s+copper farthings?$")]
    private static partial Regex WealthRegex();

    [GeneratedRegex(@"^Encumbrance:\s+(\d+)/(\d+)\s+-\s+(\w+)\s+\[(\d+)%\]$")]
    private static partial Regex EncumbranceRegex();

    // Coin pickup / drop / stash lines. The count group is `\d+` OR the article `a`
    // (a single coin — "You picked up a gold crown."), and the verb tense is optional
    // ("pick up" present as well as "picked up" past), matching the forms CashManager
    // recognises. Without both, single-coin and present-tense loots slipped past the
    // snapshot and @wealth / @enc stayed stale until the next full `i`.
    [GeneratedRegex(@"^You pick(?:ed)? up (\d+|a) (\w+ coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)\.?$")]
    private static partial Regex PickedUpCurrencyRegex();

    [GeneratedRegex(@"^You drop(?:ped)? (\d+|a) (\w+ coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)\.?$")]
    private static partial Regex DroppedCurrencyRegex();

    [GeneratedRegex(@"^You hid (\d+|a) (\w+ coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)\.?$")]
    private static partial Regex HidCurrencyRegex();

    [GeneratedRegex(@"^You hid (.+)\.$")]
    private static partial Regex HidItemRegex();

    // Trailing coin noun set (mirrors the currency regexes' denomination nouns) —
    // tells a coin hand-over from an item's.
    [GeneratedRegex(@"\b(?:farthing|noble|crown|piece|coin)s?$")]
    private static partial Regex CoinNounSuffixRegex();

    // A coin amount as the hide echo words it: a count or `a`, then a metal and a
    // coin noun, or one word and "coin(s)" for a board's runic tier. Tells "You hid
    // a gold piece." from a genuine item hide. The count is what makes it coin: an
    // item is echoed by its bare name, and Paradigm's counted item echo ("3 iron
    // crown") names no coin metal.
    [GeneratedRegex(@"^(?:\d+|a) (?:(?:copper|silver|gold|platinum) (?:farthing|noble|crown|piece)s?|\w+ coins?)$")]
    private static partial Regex CoinAmountRegex();

    [GeneratedRegex(@"^You deposit (\d.+)\.$")]
    private static partial Regex DepositCurrencyRegex();

    [GeneratedRegex(@"^You hand over (.+?) (?:and you receive training to attain level \d+|to train to the next level)")]
    private static partial Regex TrainFeeRegex();

    [GeneratedRegex(@"^[Yy]ou withdrew (\d.+)\.$")]
    private static partial Regex WithdrawCurrencyRegex();

    // "just " is ParaMUD; Stock omits it. Item-name + price groups captured;
    // only the price tail (group 2) drives currency math here.
    [GeneratedRegex(@"^You (?:just )?bought (.+) for (.+)\.$")]
    private static partial Regex BoughtRegex();

    [GeneratedRegex(@"^You (?:just )?sold (.+) for (.+)\.$")]
    private static partial Regex SoldRegex();

    [GeneratedRegex(@"(\d+)\s+(\w+ coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)")]
    private static partial Regex PriceSegmentRegex();

    // Worn-item suffix. 21-slot model plus the weapon/off-hand labels; the two
    // weapon spellings ("Weapon Hand" / "Two handed") both land here and are
    // folded by NormalizeSlot.
    [GeneratedRegex(@"^(.*?)\s+\((Head|Ears|Eyes|Face|Neck|Back|Torso|Arms|Wrist|Hands|Finger|Waist|Legs|Feet|Worn|Off-Hand|Weapon Hand|Two handed)\)$")]
    private static partial Regex EquippedSlotRegex();

    // A lit light prints inline as "lantern (Readied/239)" — the count is the
    // remaining-charge counter (item's UseCount / 10 at full), not a slot.
    [GeneratedRegex(@"^(.*?)\s+\(Readied/(\d+)\)$")]
    private static partial Regex ReadiedLightRegex();

    // Incremental equip / remove lines (single item, between full 'i' dumps).
    [GeneratedRegex(@"^You are now holding (.+)\.$")]
    private static partial Regex HeldWeaponRegex();

    [GeneratedRegex(@"^You are now wearing (.+)\.$")]
    private static partial Regex WornItemRegex();

    [GeneratedRegex(@"^You have removed (.+)\.$")]
    private static partial Regex RemovedItemRegex();

    [GeneratedRegex(@"^You have removed (.+) and extinguished it\.$")]
    private static partial Regex RemovedLitLightRegex();

    // Stock's line for lighting a light (GAME_MECHANICS "Light sources").
    [GeneratedRegex(@"^You lit the (.+)\.$")]
    private static partial Regex LitLightRegex();

    [GeneratedRegex(@"^You now have no weapon readied\.$")]
    private static partial Regex NoWeaponReadiedRegex();

    // Item get / drop (single item). MajorMUD phrases the item get as "You took
    // X." and the item drop as "You dropped X." (the drop regex also tolerates
    // the present-tense "You drop X."). The currency forms use different verbs
    // ("You picked up N ..." with no trailing period / "You dropped N ...") and
    // carry a numeric count; they're matched and returned earlier, so a coin
    // line never reaches these. The engine also reports damage as "You took N
    // damage." — not a pickup.
    [GeneratedRegex(@"^You took (?!\d+ damage\.$)(.+?)\.$")]
    private static partial Regex TookItemRegex();

    [GeneratedRegex(@"^You drop(?:ped)? (.+?)\.$")]
    private static partial Regex DropItemRegex();

    // Item / coin hand-off between characters. Give-away names the recipient
    // ("... to Bob."); receive names the giver ("Bob just gave you ..."). The
    // captured name (item or a currency token) is routed through the currency
    // guard in ApplyGiveTransfer. The greedy item groups let a multi-word name
    // ("a rusty dagger") round-trip. The recipient and the giver are passed on
    // with the item (ItemGivenAway, ItemReceived) and change nothing in the pack.
    [GeneratedRegex(@"^You just gave (.+) to (.+)\.$")]
    private static partial Regex GaveItemAwayRegex();

    [GeneratedRegex(@"^(.+?) just gave you (.+)\.$")]
    private static partial Regex ReceivedItemRegex();

    // The second hand-over wording, Paradigm's: first name, the item's record name
    // (under a count above one), a full stop. TryReadHandedName checks the item.
    [GeneratedRegex(@"^(\S+) gives you (.+)\.$")]
    private static partial Regex HandedItemRegex();

    [GeneratedRegex(@"^You give (.+) to (\S+)\.$")]
    private static partial Regex HandedAwayRegex();

    // Coins in that wording carry no full stop, which keeps them apart from items.
    [GeneratedRegex(@"^\S+ gives you (\d+) ([^.]+)$")]
    private static partial Regex HandedCoinsRegex();

    [GeneratedRegex(@"^You give (\d+) ([^.]+) to ([^\s.]+)$")]
    private static partial Regex HandedCoinsAwayRegex();

    // A coin hand-over: count, then the coin. Giver and recipient are one word.
    [GeneratedRegex(@"^You gave (\S+) (\d+) (.+?)\.?$")]
    private static partial Regex GaveCoinsAwayRegex();

    [GeneratedRegex(@"^\S+ gave you (\d+) (.+?)\.?$")]
    private static partial Regex ReceivedCoinsRegex();

    [GeneratedRegex(@"^You don't have (.+) to give\.$")]
    private static partial Regex GiveFailedRegex();

    // "<Player> refuses your offer." (they have receiving switched off) and
    // "<Player> cannot accept your offer." (no room for it), as the Stock engine
    // prints them (GAME_MECHANICS "Giving items and coins to another player").
    [GeneratedRegex(@"^(.+) (?:refuses|cannot accept) your offer\.$")]
    private static partial Regex GiveRefusedByRegex();

    // "You do not have that much copper!" (and silver, gold, platinum), and for the
    // fifth coin "You do not have that many <coin>!", as the Stock engine prints
    // them (GAME_MECHANICS "Giving items and coins to another player").
    [GeneratedRegex(@"^You do not have that (?:much (?:copper|silver|gold|platinum)|many .+)!$")]
    private static partial Regex CoinGiveLackedCoinsRegex();

    private const string GiveNotAllowedLine = "You may not give that item away!";
    private const string CoinGiveMisaimedLine = "Why would you want to give to that?";
}
