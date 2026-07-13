using System.Text.RegularExpressions;
using FujinTerm.Game;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Inventory;

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

    // Stock None/Light encumbrance boundary (percent).
    private const int StockNoneCeiling = 16;

    private const int MaxCaptureLines = 50;

    private readonly LogService? _log;
    private readonly object _lock = new();

    // Resolves a game item display-name ("a torch") to its carry weight (MDB
    // Encum), or null when unknown. Lets item get / drop / buy / sell move the
    // live encumbrance estimate the same way coin transactions already do.
    // Null in tests that don't exercise item weight.
    private readonly Func<string, int?>? _itemWeight;

    // Resolves a worn item's display-name to its true slot string ("Torso"), or
    // null when unknown. The "You are now wearing X." line names no slot, so this
    // supplies the real placement instead of a generic "Worn" that would misfile
    // the piece in "Snapshot Current". Null in tests / when no game data is loaded
    // — the handler falls back to the generic slot.
    private readonly Func<string, string?>? _slotResolver;

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
    // Carried-but-unworn item names. Re-based by each full 'i' dump and patched
    // incrementally between dumps: get / buy add, drop / sell remove, and
    // equip / remove move a name between this list and the worn set. Name
    // matching is best-effort (case-insensitive) and self-corrects on the next
    // 'i'. Death-recovery reads it as a "last-known" deathpile snapshot; the
    // worn half is deduped against it at capture time.
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

    // Wrap-merge: the MUD wraps long lines (~78 cols), so a multi-currency
    // "You deposit 1 platinum piece, 93 gold crowns, ... copper farthin" +
    // "gs." splits across two emitted rows. Hold a non-'.'-terminated
    // transaction-start line and prepend it to the next row for one retry.
    private string _pendingMergeLine = "";

    public InventoryManager(
        LogService? log = null,
        Func<string, int?>? itemWeightResolver = null,
        Func<string, string?>? slotResolver = null)
    {
        _log = log;
        _itemWeight = itemWeightResolver;
        _slotResolver = slotResolver;
    }

    // Fired (outside the lock) whenever the snapshot changes.
    public event Action? Changed;

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
            // The newly-held weapon leaves the pack for the hand; the displaced one
            // returns to it.
            RemoveCarried(name);
            foreach (string old in displaced)
                PatchCarried(list => { list.Add(old); return true; });
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
            PatchEquipped(list =>
            {
                list.Add(new EquippedItem(name, slot));
                return true;
            });
            RemoveCarried(name);
            return;
        }

        Match removed = RemovedItemRegex().Match(line);
        if (removed.Success)
        {
            string name = removed.Groups[1].Value.TrimEnd();
            PatchEquipped(list =>
                list.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) > 0);
            // A removed piece returns to the pack (unworn) until re-equipped.
            PatchCarried(list => { list.Add(name); return true; });
            return;
        }

        if (NoWeaponReadiedRegex().IsMatch(line))
        {
            // Weapon removal is unnamed ("You now have no weapon readied."),
            // unlike the armor path which names its item. Read the outgoing
            // weapon's name from the worn set as it leaves so it can return to
            // the pack (unworn) just like removed armor.
            var unreadied = new List<string>();
            PatchEquipped(list =>
            {
                foreach (EquippedItem e in list)
                    if (e.Slot == "Weapon Hand") unreadied.Add(e.Name);
                return list.RemoveAll(e => e.Slot == "Weapon Hand") > 0;
            });
            foreach (string name in unreadied)
                PatchCarried(list => { list.Add(name); return true; });
            return;
        }

        Match pickedUp = PickedUpCurrencyRegex().Match(line);
        if (pickedUp.Success)
        {
            if (int.TryParse(pickedUp.Groups[1].Value, out int amount))
            {
                lock (_lock) AdjustCurrency(pickedUp.Groups[2].Value, amount);
                Changed?.Invoke();
            }
            return;
        }

        Match dropped = DroppedCurrencyRegex().Match(line);
        if (dropped.Success)
        {
            if (int.TryParse(dropped.Groups[1].Value, out int amount))
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
            if (int.TryParse(hidden.Groups[1].Value, out int amount))
            {
                lock (_lock) AdjustCurrency(hidden.Groups[2].Value, -amount);
                Changed?.Invoke();
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
            return;
        }

        Match withdraw = WithdrawCurrencyRegex().Match(line);
        if (withdraw.Success)
        {
            long amount = ParsePriceToCopper(withdraw.Groups[1].Value);
            lock (_lock) ApplyTransaction(amount);
            Changed?.Invoke();
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
            string boughtName = bought.Groups[1].Value.TrimEnd();
            PatchCarried(list => { list.Add(boughtName); return true; });
            AdjustItemWeight(boughtName, +1);

            string priceTail = bought.Groups[2].Value;
            long priceCopper = ParsePriceToCopper(priceTail);
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
            // The sold item leaves the pack.
            string soldName = sold.Groups[1].Value.TrimEnd();
            RemoveCarried(soldName);
            AdjustItemWeight(soldName, -1);

            long price = ParsePriceToCopper(sold.Groups[2].Value);
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
            ApplyGiveTransfer(gaveAway.Groups[1].Value.TrimEnd(), -1);
            return;
        }

        // Receive: "Bob just gave you a torch." — the item (or coins) enters our
        // pack, like a get. Lets a hand-off from a party member (or another of
        // our characters) show up without waiting for the next full 'i' dump.
        Match received = ReceivedItemRegex().Match(line);
        if (received.Success)
        {
            ApplyGiveTransfer(received.Groups[2].Value.TrimEnd(), +1);
            return;
        }

        // Failed give: "You don't have a torch to give." — no state change (we
        // never held it), logged so a give-driven flow can see the attempt bounced.
        if (GiveFailedRegex().IsMatch(line))
        {
            _log?.Debug(LogCategory, "give bounced: item not held");
            return;
        }

        // Get: "You took rusty dagger." — the item enters the pack unworn. The
        // currency pickup ("You picked up N ...", no trailing period) is a
        // different verb and is matched (and returned) above, so only item lines
        // reach here.
        Match gotItem = TookItemRegex().Match(line);
        if (gotItem.Success)
        {
            string name = gotItem.Groups[1].Value.TrimEnd();
            PatchCarried(list => { list.Add(name); return true; });
            AdjustItemWeight(name, +1);
            return;
        }

        // Drop: "You dropped torch." — the item leaves the pack. Currency drops
        // carry a numeric count and are matched above.
        Match droppedItem = DropItemRegex().Match(line);
        if (droppedItem.Success)
        {
            string name = droppedItem.Groups[1].Value.TrimEnd();
            RemoveCarried(name);
            AdjustItemWeight(name, -1);
        }
    }

    // ----- full parse --------------------------------------------------

    private void ParseFullInventory()
    {
        string? wealthLine = null;
        string? encumbranceLine = null;
        string? keysLine = null;
        var currencyTokens = new List<string>();

        foreach (string raw in _captureBuffer)
        {
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith("Wealth:", StringComparison.Ordinal))
                wealthLine = trimmed;
            else if (trimmed.StartsWith("Encumbrance:", StringComparison.Ordinal))
                encumbranceLine = trimmed;
            else if (trimmed.StartsWith("You have the following keys:", StringComparison.Ordinal))
                keysLine = trimmed;
        }

        // Reconstruct the word-wrapped items text and pull currency tokens out
        // of it. Item tokens are ignored in this slice — only coins matter.
        string itemsText = string.Join(" ", CollectItemLines());
        const string prefix = "You are carrying ";
        if (itemsText.StartsWith(prefix, StringComparison.Ordinal))
            itemsText = itemsText[prefix.Length..];
        else if (itemsText.StartsWith("You are carrying nothing", StringComparison.Ordinal))
            itemsText = string.Empty;

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
            _carried = carried;
            _readiedLight = readiedLight;
            _keys = keys;
            _loaded = true;
            _lastUpdated = DateTimeOffset.Now;
        }

        _log?.Debug(LogCategory,
            $"parsed: wealth={wealthCopper} copper, enc={curWeight}/{maxWeight} {category} [{percentage}%], worn={equipped.Count}, carried={carried.Count}, keys={keys.Count}"
            + (readiedLight is { } rl ? $", lit={rl.Name} (Readied/{rl.Readied})" : ""));
        Changed?.Invoke();
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
        if (changed) Changed?.Invoke();
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
        if (changed) Changed?.Invoke();
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

        if (sign > 0) PatchCarried(list => { list.Add(name); return true; });
        else RemoveCarried(name);
        AdjustItemWeight(name, sign);
    }

    // Drop the first carried entry matching name (case-insensitive). Only the
    // first, so selling one of two identical items leaves the other in the pack.
    private void RemoveCarried(string name)
    {
        PatchCarried(list =>
        {
            int idx = list.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            list.RemoveAt(idx);
            return true;
        });
    }

    // The capture buffer holds the "You are carrying ..." rows plus the Keys /
    // Wealth / Encumbrance trailers. Item rows are everything that isn't one of
    // those trailers; we join them to undo the ~80-col word wrap before
    // splitting on ", ".
    private IEnumerable<string> CollectItemLines()
    {
        foreach (string raw in _captureBuffer)
        {
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith("Wealth:", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("Encumbrance:", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("You have no keys", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("You have the following keys", StringComparison.Ordinal)) continue;
            yield return trimmed;
        }
    }

    // ----- currency math (all under _lock) -----------------------------

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

    // Move the live encumbrance estimate as a single item enters (+1) or leaves
    // (-1) the pack, reading its carry weight (MDB Encum) from the active game
    // data. A null resolver, an unresolved name, or a zero-weight item is a
    // no-op — and the next full 'i' dump re-bases the reading regardless, so a
    // stale estimate self-corrects. Gated on a loaded baseline like the carried
    // list: adjusting from a 0/0 reading would be meaningless.
    private void AdjustItemWeight(string itemName, int sign)
    {
        if (_itemWeight is null) return;
        if (_itemWeight(itemName) is not int weight || weight == 0) return;
        bool changed = false;
        lock (_lock)
        {
            if (_loaded)
            {
                _curWeight = (int)Math.Max(0, _curWeight + (long)sign * weight);
                RecomputeEncumbrancePercent();
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
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

    // Stock boundaries (None ≤ 16%).
    private static EncumbranceLevel DeriveCategory(int percentage)
    {
        if (percentage <= StockNoneCeiling) return EncumbranceLevel.None;
        if (percentage <= 33) return EncumbranceLevel.Light;
        if (percentage <= 66) return EncumbranceLevel.Medium;
        return EncumbranceLevel.Heavy;
    }

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

    [GeneratedRegex(@"^You picked up (\d+) (\w+ coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)\.?$")]
    private static partial Regex PickedUpCurrencyRegex();

    [GeneratedRegex(@"^You dropped (\d+) (\w+ coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)\.?$")]
    private static partial Regex DroppedCurrencyRegex();

    [GeneratedRegex(@"^You hid (\d+) (\w+ coins?|platinum pieces?|gold crowns?|silver nobles?|copper farthings?)\.?$")]
    private static partial Regex HidCurrencyRegex();

    [GeneratedRegex(@"^You deposit (\d.+)\.$")]
    private static partial Regex DepositCurrencyRegex();

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

    [GeneratedRegex(@"^You now have no weapon readied\.$")]
    private static partial Regex NoWeaponReadiedRegex();

    // Item get / drop (single item). MajorMUD phrases the item get as "You took
    // X." and the item drop as "You dropped X." (the drop regex also tolerates
    // the present-tense "You drop X."). The currency forms use different verbs
    // ("You picked up N ..." with no trailing period / "You dropped N ...") and
    // carry a numeric count; they're matched and returned earlier, so a coin
    // line never reaches these.
    [GeneratedRegex(@"^You took (.+?)\.$")]
    private static partial Regex TookItemRegex();

    [GeneratedRegex(@"^You drop(?:ped)? (.+?)\.$")]
    private static partial Regex DropItemRegex();

    // Item / coin hand-off between characters. Give-away names the recipient
    // ("... to Bob."); receive names the giver ("Bob just gave you ..."). The
    // captured name (item or a currency token) is routed through the currency
    // guard in ApplyGiveTransfer. The greedy item groups let a multi-word name
    // ("a rusty dagger") round-trip; the recipient / giver token isn't used.
    [GeneratedRegex(@"^You just gave (.+) to (.+)\.$")]
    private static partial Regex GaveItemAwayRegex();

    [GeneratedRegex(@"^(.+?) just gave you (.+)\.$")]
    private static partial Regex ReceivedItemRegex();

    [GeneratedRegex(@"^You don't have (.+) to give\.$")]
    private static partial Regex GiveFailedRegex();
}
