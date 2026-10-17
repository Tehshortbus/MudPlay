using Avalonia.Threading;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// Over max encumbrance: every move is refused with "You are too heavy to move".
// It usually comes from a debuff that cuts carrying capacity mid-fight (weakness,
// frail's Encum%). It isn't a hold: freedom / cure paralysis do nothing, and the
// leader can't see it.
//
// Two ways in. A capacity debuff landing reads `i` at once, so we know before the
// first refused move; the refusal line itself is the fallback for anything else
// that put us over. While over, our own walk / loop / auto-lair holds (TooHeavyGate)
// and a follower tells the leader — @wait (too heavy to move).
//
// It ends when a fresh `i` shows the weight back under the max: the debuff wore off
// (its wear-off line prompts a read) or the player shed weight. A read every
// RecheckInterval covers a wear-off line we missed.
public sealed class TooHeavyWaitSignal : IDisposable
{
    public const string AsserterName = "TooHeavyMovementGate";
    private const string LogCategory = "Encumbrance";
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(15);

    private readonly InventoryManager _inventory;
    private readonly PartyRestSync _restSync;
    private readonly MovementCoordinator _coordinator;
    private readonly LogService? _log;
    private readonly IDisposable _sub;
    private readonly WireSender _wire = new();
    private DispatcherTimer? _timer;
    private bool _tooHeavy;
    // The snapshot's reading predates the debuff until a fresh `i` lands, so a
    // Changed before then (a coin pickup) must not read as "back under max".
    private bool _awaitingFreshRead;
    // A capacity debuff landed and the `i` that tells us whether it put us over
    // hasn't come back yet.
    private bool _checkingAfterDebuff;
    private bool _disposed;

    public bool IsTooHeavy => _tooHeavy;

    public TooHeavyWaitSignal(
        MessageRouter router, InventoryManager inventory, PartyRestSync restSync,
        MovementCoordinator coordinator, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(restSync);
        ArgumentNullException.ThrowIfNull(coordinator);
        _inventory = inventory;
        _restSync = restSync;
        _coordinator = coordinator;
        _log = log;
        _sub = router.Subscribe(KnownPatterns.MovementFailedHeavy, _ => OnTooHeavy());
        _inventory.Changed += OnInventoryChanged;
        _inventory.FullInventoryParsed += OnFullInventoryParsed;
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // A spell that cuts our carrying capacity just landed. Whether it put us over
    // the max only shows in `i`, so read it; OnFullInventoryParsed decides.
    public void NoteCapacityDebuffApplied()
    {
        if (_tooHeavy || _checkingAfterDebuff) return;
        _checkingAfterDebuff = true;
        _log?.Info(LogCategory, "A debuff cut our carrying capacity — reading `i` to see if we can still move.");
        Recheck();
    }

    // The debuff wore off: the max is back, so don't wait for the next timed read.
    public void NoteCapacityDebuffEnded()
    {
        if (!_tooHeavy) return;
        _awaitingFreshRead = true;
        Recheck();
    }

    private void OnTooHeavy() => EnterTooHeavy("the game refused a move", reread: true);

    // reread: the reading in hand predates whatever put us over, so ask for a new one.
    private void EnterTooHeavy(string how, bool reread)
    {
        if (!_tooHeavy)
        {
            _tooHeavy = true;
            _coordinator.AssertGate(MovementCoordinator.TooHeavyGate, AsserterName, "over max encumbrance");
            _log?.Info(LogCategory, $"Too heavy to move ({how}) — holding until we're back under our max encumbrance.");
        }
        // Resend: the leader may already be waiting on us for another reason, and
        // this one is worth naming.
        _restSync.RequestWait(WaitReason.TooHeavy, resend: true);
        if (reread)
        {
            _awaitingFreshRead = true;
            Recheck();
        }
        StartTimer();
    }

    private void OnFullInventoryParsed()
    {
        _awaitingFreshRead = false;
        if (_checkingAfterDebuff)
        {
            _checkingAfterDebuff = false;
            EncumbranceReading enc = _inventory.Snapshot.Encumbrance;
            if (!_tooHeavy && enc.MaxWeight > 0 && enc.CurrentWeight > enc.MaxWeight)
            {
                EnterTooHeavy($"{enc.CurrentWeight}/{enc.MaxWeight} after the debuff", reread: false);
                return;
            }
        }
        OnInventoryChanged();
    }

    private void OnInventoryChanged()
    {
        if (!_tooHeavy || _awaitingFreshRead) return;
        EncumbranceReading enc = _inventory.Snapshot.Encumbrance;
        if (enc.MaxWeight <= 0 || enc.CurrentWeight > enc.MaxWeight) return;
        _tooHeavy = false;
        StopTimer();
        _coordinator.ClearGate(MovementCoordinator.TooHeavyGate, AsserterName, "back under max encumbrance");
        _log?.Info(LogCategory, $"Back under max encumbrance ({enc.CurrentWeight}/{enc.MaxWeight}) — moving again.");
        _restSync.RequestOk(WaitReason.TooHeavy);
    }

    // The master switch (true = off): off, the `i` reads are not sent. The state
    // stays as it is and the timed read picks it up once the switch is back on,
    // or the user's own `i` settles it sooner.
    public Func<bool>? MasterSwitchOff { get; set; }

    private void Recheck()
    {
        if (MasterSwitchOff?.Invoke() == true) return;
        _wire.Send("i");
    }

    private void StartTimer()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer(RecheckInterval, DispatcherPriority.Background, (_, _) =>
        {
            if (_tooHeavy) Recheck();
            else StopTimer();
        });
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sub.Dispose();
        _inventory.Changed -= OnInventoryChanged;
        _inventory.FullInventoryParsed -= OnFullInventoryParsed;
        StopTimer();
        if (_tooHeavy)
            _coordinator.ClearGate(MovementCoordinator.TooHeavyGate, AsserterName, "disposed");
    }
}
