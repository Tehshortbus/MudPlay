using System;
using System.Collections.Generic;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Train;

// How a spell trip ended. Aborted = someone else took the walker (a Stop, a user
// walk-to, a death); the owner then leaves its engine stopped.
public readonly record struct ShopSpellResult(IReadOnlyList<string> Learned, bool Aborted);

// The shop leg of a train trip: walk to each shop that sells a scroll the
// character can now learn, buy it, and read it.
//
// Shape is the funding errand's: the owner has already stopped the running engine,
// so this drives the walker directly and reports back. Nothing is trusted past the
// stop in front of it — after every shop the trip is re-planned from where it
// stands with the purse it actually holds, so a scroll that was out of stock or a
// toll paid on the way just yields a shorter plan.
//
// It opens with `sp`: the plan is only as good as the spellbook it's checked
// against, and the list is otherwise read only when the player happens to ask.
//
// Every wait is on a timer as well as on its reply. A shop that answers `buy` or
// `read` with a line we don't know must cost a few seconds, not strand the run.
//
// Walker, wire and spellbook are reached through delegates so the FSM is testable
// without a map or a line stream.
public sealed class ShopSpellErrand
{
    private const string LogCategory = "AutoTrain";

    public TimeSpan SpellListWindow { get; set; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan ShopListWindow { get; set; } = TimeSpan.FromSeconds(4);
    public TimeSpan BuyWindow { get; set; } = TimeSpan.FromSeconds(4);
    public TimeSpan ReadWindow { get; set; } = TimeSpan.FromSeconds(4);

    private enum Phase { Idle, ReadingSpellList, Walking, Listing, Buying, Reading }

    private readonly Func<RoomKey?> _currentRoom;
    private readonly Func<RoomKey, RoomKey, int, IReadOnlyCollection<RoomKey>, IReadOnlyCollection<int>, ShopSpellPlan> _plan;
    private readonly Func<int, IReadOnlyList<ShopSpellPurchase>> _carriedScrolls;
    private readonly Func<int, int> _carriedCount;
    private readonly Func<int, bool> _isObtained;
    private readonly Func<RoomKey, bool> _walkTo;
    private readonly Action<string> _send;
    private readonly Action<TimeSpan, Action> _armTimer;
    private readonly Action<long>? _reserve;
    private readonly Game.Light.ErrandLightStop? _lightStop;
    private readonly LogService? _log;

    private Phase _phase = Phase.Idle;
    private int _level;
    private RoomKey _returnTo;
    private ShopSpellStop? _stop;
    // Scrolls still to deal with where we stand; Buy is false for one already carried.
    private readonly Queue<(ShopSpellPurchase Scroll, bool Buy)> _queue = new();
    private readonly HashSet<RoomKey> _visited = new();
    // Spells this trip has stopped trying for: a read that went unanswered, or a
    // scroll the game says we already know. Without it the re-plan would walk to the
    // next shop selling the same scroll and read it again.
    private readonly HashSet<int> _gaveUp = new();
    private readonly List<string> _learned = new();
    // Bumped whenever the thing a timer was armed for is over, so a stale timer is a no-op.
    private int _step;

    // Set while this errand is itself starting a walk: WalkTo raises Stopped for the
    // walk it supersedes, and that churn is ours, not a user abort.
    private bool _drivingWalker;

    public event Action<ShopSpellResult>? Finished;

    public bool IsBusy => _phase != Phase.Idle;

    // Where the trip stands, for the bug report.
    public string Describe() => _phase == Phase.Idle
        ? "idle"
        : $"{_phase}" + (_stop is { } stop ? $" — {Describe(stop)}" : string.Empty)
          + (_queue.Count > 0 ? $", on {_queue.Peek().Scroll.ItemName}" : string.Empty);

    public ShopSpellErrand(
        Func<RoomKey?> currentRoom,
        // (from, returnTo, level, rooms already visited, spells given up on) → the trip from here.
        Func<RoomKey, RoomKey, int, IReadOnlyCollection<RoomKey>, IReadOnlyCollection<int>, ShopSpellPlan> plan,
        // level → scrolls in the pack for spells still unlearned and wanted.
        Func<int, IReadOnlyList<ShopSpellPurchase>> carriedScrolls,
        Func<int, int> carriedCount,
        Func<int, bool> isObtained,
        Func<RoomKey, bool> walkTo,
        Action<string> send,
        Action<TimeSpan, Action> armTimer,
        LogService? log = null,
        // Holds the scroll money back from the walker's toll decisions (0 releases it).
        Action<long>? reserve = null,
        // Auto-Light's stop for a leg that runs dark (and the walk back after it):
        // unwired, the legs walk as they are.
        Game.Light.ErrandLightStop? lightStop = null)
    {
        _lightStop = lightStop;
        _currentRoom = currentRoom ?? throw new ArgumentNullException(nameof(currentRoom));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _carriedScrolls = carriedScrolls ?? throw new ArgumentNullException(nameof(carriedScrolls));
        _carriedCount = carriedCount ?? throw new ArgumentNullException(nameof(carriedCount));
        _isObtained = isObtained ?? throw new ArgumentNullException(nameof(isObtained));
        _walkTo = walkTo ?? throw new ArgumentNullException(nameof(walkTo));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _armTimer = armTimer ?? throw new ArgumentNullException(nameof(armTimer));
        _log = log;
        _reserve = reserve;
    }

    // A scroll for a spell this level can learn is already in the pack: a trip has
    // something to do even with nothing left to buy.
    public bool HasCarriedScrolls(int level) => _carriedScrolls(level).Count > 0;

    // Start the trip for a character now at `level`. False when there is nothing to
    // go for — no shop to visit and no scroll in the pack — and nothing was sent.
    public bool Begin(int level, RoomKey returnTo)
    {
        if (IsBusy || _currentRoom() is not { } here) return false;
        _visited.Clear();
        _gaveUp.Clear();
        if (_plan(here, returnTo, level, _visited, _gaveUp).Stops.Count == 0 && _carriedScrolls(level).Count == 0)
            return false;

        _level = level;
        _returnTo = returnTo;
        _lightStop?.BeginRun();
        _learned.Clear();
        _queue.Clear();
        _stop = null;
        _phase = Phase.ReadingSpellList;
        _send("sp");
        Arm(SpellListWindow, ReadCarriedScrolls);
        return true;
    }

    // Abandon the trip without reporting — the owner is tearing its run down.
    public void Cancel(string reason)
    {
        if (!IsBusy) return;
        _log?.Info(LogCategory, $"Spell trip cancelled — {reason}.");
        Reset();
    }

    // A scroll bought on an earlier trip and never learned (the read went
    // unanswered, or the level wasn't there yet) needs no shop.
    private void ReadCarriedScrolls()
    {
        foreach (ShopSpellPurchase scroll in _carriedScrolls(_level)) _queue.Enqueue((scroll, false));
        Pump();
    }

    private void NextStop()
    {
        if (_currentRoom() is not { } here)
        {
            Finish("current room unknown");
            return;
        }

        ShopSpellPlan plan = _plan(here, _returnTo, _level, _visited, _gaveUp);
        if (plan.Unaffordable.Count > 0)
            _log?.Info(LogCategory, $"Not buying (can't afford): {string.Join(", ", plan.Unaffordable)}.");
        if (plan.Stops.Count == 0)
        {
            Finish(null);
            return;
        }

        _stop = plan.Stops[0];
        _reserve?.Invoke(plan.CostCopper);
        if (here.Equals(_stop.Room))
        {
            ArriveAtShop();
            return;
        }

        _drivingWalker = true;
        bool started;
        try { started = _lightStop?.Walk(_stop.Room, _walkTo, LightStopTakenOver, ahead: [_returnTo]) ?? _walkTo(_stop.Room); }
        finally { _drivingWalker = false; }
        if (!started)
        {
            _log?.Info(LogCategory, $"No path to {Describe(_stop)} — skipping it.");
            _visited.Add(_stop.Room);
            NextStop();
            return;
        }

        _phase = Phase.Walking;
        _step++;
        _log?.Info(LogCategory, $"Walking to {Describe(_stop)} for {Names(_stop.Purchases)}.");
    }

    private void LightStopTakenOver(string detail)
    {
        if (_phase == Phase.Walking) Finish($"walk stopped ({detail})", aborted: true);
    }

    // Wired to the walker's event stream by the owner. The light stop, while it has
    // a leg's walk, takes the events of its own walk to the shop.
    public void OnWalkEvent(WalkEvent e)
    {
        if (_lightStop?.OnWalkEvent(e) == true) return;
        if (_phase != Phase.Walking || _stop is not { } stop) return;
        bool ours = e.Destination is null || e.Destination == stop.Room;

        if (e.Kind == WalkEventKind.Stopped)
        {
            if (_drivingWalker || !ours) return;
            Finish($"walk stopped ({e.Detail})", aborted: true);
        }
        else if (e.Kind == WalkEventKind.Finished && _currentRoom() is { } here && here.Equals(stop.Room))
        {
            ArriveAtShop();
        }
        else if (ours && e.Kind is WalkEventKind.Failed or WalkEventKind.Finished)
        {
            _log?.Info(LogCategory, $"Couldn't reach {Describe(stop)} — skipping it.");
            _visited.Add(stop.Room);
            NextStop();
        }
    }

    // The live stock decides what's worth a `buy`: most scrolls restock on a small
    // chance, so a shop that sells one is often out of it.
    private void ArriveAtShop()
    {
        _phase = Phase.Listing;
        _send("list");
        Arm(ShopListWindow, () =>
        {
            if (_phase != Phase.Listing || _stop is not { } stop) return;
            _log?.Info(LogCategory, $"No stock list from {Describe(stop)} — trying the buys anyway.");
            foreach (ShopSpellPurchase scroll in stop.Purchases) _queue.Enqueue((scroll, true));
            Pump();
        });
    }

    // A shop's `list` readout landed.
    public void OnShopListed(IReadOnlyList<ShopListParser.StockRow> stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (_phase != Phase.Listing || _stop is not { } stop) return;

        // The shop's own verdict comes first (GAME_MECHANICS "`list` — live shop stock readout"):
        // "(You can't use)" is a scroll this character can never learn, "(Too
        // powerful)" one it isn't yet the level for. Either would be coin spent on a
        // scroll the read then refuses.
        List<string> outOfStock = new(), cantUse = new(), tooPowerful = new();
        foreach (ShopSpellPurchase scroll in stop.Purchases)
        {
            ShopListParser.StockRow? row = RowFor(stock, scroll.ItemName);
            if (row is { CantUse: true })
            {
                _gaveUp.Add(scroll.SpellNumber);
                cantUse.Add(scroll.ItemName);
            }
            else if (row is { TooPowerful: true })
            {
                _gaveUp.Add(scroll.SpellNumber);
                tooPowerful.Add(scroll.ItemName);
            }
            else if (row is { Quantity: > 0 }) _queue.Enqueue((scroll, true));
            else outOfStock.Add(scroll.ItemName);
        }
        if (outOfStock.Count > 0)
            _log?.Info(LogCategory, $"Out of stock at {Describe(stop)}: {string.Join(", ", outOfStock)}.");
        if (cantUse.Count > 0)
            _log?.Info(LogCategory, $"{Describe(stop)} marks these \"You can't use\" — not buying: {string.Join(", ", cantUse)}.");
        if (tooPowerful.Count > 0)
            _log?.Info(LogCategory, $"{Describe(stop)} marks these \"Too powerful\" for this level — not buying: {string.Join(", ", tooPowerful)}.");
        Pump();
    }

    // Deal with the scroll at the head of the queue: buy it if it isn't in the pack
    // yet, then read it. One command out, then wait for its reply or its window.
    private void Pump()
    {
        _step++;
        while (_queue.Count > 0)
        {
            (ShopSpellPurchase scroll, bool buy) = _queue.Peek();
            if (_isObtained(scroll.SpellNumber) || _gaveUp.Contains(scroll.SpellNumber))
            {
                _queue.Dequeue();
                continue;
            }

            // Queued as already carried, and it no longer is (read under another
            // spell's entry of the same scroll): nothing to read.
            if (!buy && _carriedCount(scroll.ItemNumber) <= 0)
            {
                _queue.Dequeue();
                continue;
            }

            if (buy && _carriedCount(scroll.ItemNumber) <= 0)
            {
                _phase = Phase.Buying;
                _send($"buy {scroll.ItemName}");
                Arm(BuyWindow, () =>
                {
                    if (_phase != Phase.Buying) return;
                    Skip($"No sale on {scroll.ItemName} — moving on.");
                });
                return;
            }

            _phase = Phase.Reading;
            _send($"read {scroll.ItemName}");
            Arm(ReadWindow, () =>
            {
                if (_phase != Phase.Reading) return;
                _gaveUp.Add(scroll.SpellNumber);
                Skip($"Read {scroll.ItemName} but {scroll.SpellName} wasn't learned — the scroll stays in the pack.");
            });
            return;
        }

        if (_stop is { } done) _visited.Add(done.Room);
        NextStop();
    }

    // The pack changed: the scroll we asked for is in it now.
    public void OnInventoryChanged()
    {
        if (_phase != Phase.Buying || _queue.Count == 0) return;
        if (_carriedCount(_queue.Peek().Scroll.ItemNumber) > 0) Pump();
    }

    // "You cannot afford <item>."
    public void OnBuyRefused(string itemName)
    {
        if (_phase != Phase.Buying || _queue.Count == 0) return;
        if (!NameMatches(itemName, _queue.Peek().Scroll.ItemName)) return;
        Skip($"Can't afford {_queue.Peek().Scroll.ItemName} — moving on.");
    }

    // The spellbook changed: the spell we were reading for is in it now.
    public void OnSpellbookChanged()
    {
        if (_phase != Phase.Reading || _queue.Count == 0) return;
        ShopSpellPurchase scroll = _queue.Peek().Scroll;
        if (!_isObtained(scroll.SpellNumber)) return;
        _learned.Add(scroll.SpellName);
        _log?.Info(LogCategory, $"Learned {scroll.SpellName} from {scroll.ItemName}.");
        _queue.Dequeue();
        Pump();
    }

    // The game says the scroll being read is one we already know. Returns that
    // spell's name so the owner can put it in the spellbook, or null when no read
    // of ours is out.
    public string? OnScrollAlreadyKnown()
    {
        if (_phase != Phase.Reading || _queue.Count == 0) return null;
        ShopSpellPurchase scroll = _queue.Peek().Scroll;
        _gaveUp.Add(scroll.SpellNumber);
        Skip($"Already know {scroll.SpellName} — {scroll.ItemName} wasn't needed.");
        return scroll.SpellName;
    }

    private void Skip(string why)
    {
        _log?.Info(LogCategory, why);
        _queue.Dequeue();
        Pump();
    }

    private void Finish(string? problem, bool aborted = false)
    {
        IReadOnlyList<string> learned = _learned.ToArray();
        _log?.Info(LogCategory, (problem is null ? "Spell trip done" : $"Spell trip ended ({problem})")
            + (learned.Count > 0 ? $" — learned {string.Join(", ", learned)}." : " — nothing learned."));
        Reset();
        Finished?.Invoke(new(learned, aborted));
    }

    private void Reset()
    {
        _lightStop?.Cancel();
        _phase = Phase.Idle;
        _step++;
        _queue.Clear();
        _stop = null;
        _reserve?.Invoke(0);
    }

    private void Arm(TimeSpan window, Action onElapsed)
    {
        int step = ++_step;
        _armTimer(window, () =>
        {
            if (_step == step) onElapsed();
        });
    }

    // The shop's row for a scroll; one that is in stock is preferred over a sold-out
    // row of the same name.
    private static ShopListParser.StockRow? RowFor(IReadOnlyList<ShopListParser.StockRow> stock, string itemName)
    {
        ShopListParser.StockRow? found = null;
        foreach (ShopListParser.StockRow row in stock)
        {
            if (!NameMatches(row.Name, itemName)) continue;
            if (row.Quantity > 0) return row;
            found ??= row;
        }
        return found;
    }

    // The list's item column is a fixed width, so a long name can arrive cut short.
    private const int TruncatedNameFloor = 16;

    private static bool NameMatches(string seen, string itemName)
    {
        string a = seen.Trim(), b = itemName.Trim();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return a.Length >= TruncatedNameFloor && b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(ShopSpellStop stop) => $"{stop.ShopName} ({stop.Room.Map}/{stop.Room.Room})";

    private static string Names(IReadOnlyList<ShopSpellPurchase> scrolls)
    {
        List<string> names = new(scrolls.Count);
        foreach (ShopSpellPurchase s in scrolls) names.Add(s.SpellName);
        return string.Join(", ", names);
    }
}
