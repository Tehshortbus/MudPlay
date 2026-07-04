using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Inventory;
using FujinTerm.Game.Map;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Terminal;

namespace FujinTerm.Game.Recovery;

// Death observation aggregator. Composes DeathLineWatcher.PlayerDied and the
// per-character CharacterProfile.DeathHistory into a live observable shape that
// the Workshop DEATH section binds to.
//
// Surfaces the loaded profile's Records (the deathpile grid), the persisted
// AutoRecover / AutoEquip toggles, and the recovery actions. The recovery state
// machine drives each record's Active → Partial → Recovered transition off
// observed room re-entry and "You pick up …" confirmations. Current lives are
// surfaced separately on the Character Info tab (from PlayerStats.Lives), not
// here.
//
// The @comeback remote command is a separate party-pickup flow (stranded-
// follower → leader) owned by PartyComebackManager, not this aggregator — it has
// nothing to do with death recovery.
public sealed partial class DeathRecoveryManager : ObservableObject, IDisposable
{
    // LogService category — appears as [DeathRecovery] rows per observation +
    // comeback request.
    public const string LogCategory = "DeathRecovery";

    private readonly DeathLineWatcher _deathWatcher;
    private readonly ProfileService _profile;
    private readonly RoomTracker _roomTracker;
    private readonly LogService? _log;
    private AutoWalkManager? _walker;
    private Action<byte[]>? _wireSender;
    private LineExtractor? _lines;
    private Func<InventorySnapshot>? _inventorySnapshot;
    private bool _disposed;

    // In-progress recovery context — one deathpile at a time (you stand in one
    // room). _activeRecovery is the record we're recovering; _remaining holds
    // the pile item names not yet seen picked up; _recoveryTotal is the pile
    // size when recovery began. _pendingRecoverNow is a record the user pressed
    // "Recover Now" on while away — it forces a grab on arrival even when
    // Auto-Recover is off.
    private DeathRecord? _activeRecovery;
    private DeathRecord? _pendingRecoverNow;
    private readonly HashSet<string> _remaining = new(StringComparer.OrdinalIgnoreCase);
    private int _recoveryTotal;

    public DeathRecoveryManager(
        DeathLineWatcher deathWatcher,
        ProfileService profile,
        RoomTracker roomTracker,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(deathWatcher);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(roomTracker);
        _deathWatcher = deathWatcher;
        _profile = profile;
        _roomTracker = roomTracker;
        _log = log;

        _deathWatcher.PlayerDied += OnPlayerDied;
        // Re-entering a room that holds one of our deathpiles drives the
        // Active → Partial → Recovered transitions (and the auto-grab).
        _roomTracker.StateChanged += OnRoomChanged;
    }

    private void OnPlayerDied(PlayerDiedEvent evt)
    {
        _log?.Info(LogCategory, $"player slain by={evt.Killer}");
        // The death record is written separately by
        // DeathDetector → RoomTracker.NoteDeath → profile.DeathHistory.
        // Nudge the grid so the new record surfaces once that write lands.
        OnPropertyChanged(nameof(Records));
    }

    // Wire the walker used by WalkToDeathRoom / RecoverNow. Set post-construction
    // because the AutoWalkManager is built after this manager in AppServices.
    public void AttachWalker(AutoWalkManager walker) => _walker = walker;

    // Bind the gate-wrapped wire sender so auto-recover can send get / wear /
    // hold commands. Bound by MainWindowViewModel on connect; unbound, the grab +
    // re-equip are no-ops (status transitions still follow observed pickups).
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the per-session LineExtractor so the manager can watch for
    // "You pick up ..." confirmations that drive the Partial → Recovered
    // transition (and the per-item re-equip). Bound by MainWindowViewModel on
    // connect.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Bind the live inventory snapshot provider so SimulateDeath captures a
    // realistic deathpile. Real deaths capture via RoomTracker.NoteDeath; this is
    // only for the test button.
    public void AttachInventorySnapshot(Func<InventorySnapshot> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _inventorySnapshot = provider;
    }

    // The loaded profile's death history (oldest → newest). Empty when no profile
    // is loaded or the lucky character has never died. The DEATH grid sorts this
    // newest-first for display.
    public IReadOnlyList<DeathRecord> Records =>
        _profile.Current?.DeathHistory is { } list ? list : Array.Empty<DeathRecord>();

    // Auto-grab a deathpile's lost items (ignoring per-item auto-get policy) when
    // re-entering the death room. Persisted per-character. The grab itself is
    // inert until inventory tracking records lost items; the preference is stored
    // now.
    public bool AutoRecover
    {
        get => _profile.Current?.DeathAutoRecover ?? false;
        set
        {
            if (_profile.Current is not { } p || p.DeathAutoRecover == value) return;
            p.DeathAutoRecover = value;
            _profile.Save();
            OnPropertyChanged();
        }
    }

    // Re-equip items that were worn at death after recovering them. Persisted
    // per-character; inert until inventory tracking records what was equipped at
    // death.
    public bool AutoEquip
    {
        get => _profile.Current?.DeathAutoEquip ?? false;
        set
        {
            if (_profile.Current is not { } p || p.DeathAutoEquip == value) return;
            p.DeathAutoEquip = value;
            _profile.Save();
            OnPropertyChanged();
        }
    }

    // Manually flag a record as fully recovered (user pressed "Mark Recovered").
    // Sets Recovered, persists, and notifies binders.
    public void MarkRecovered(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Status == DeathRecoveryStatus.Recovered) return;
        record.Status = DeathRecoveryStatus.Recovered;
        record.RecoveryMessage = "Marked recovered by user.";
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    // Remove a single record from the history.
    public void ClearSelected(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_profile.Current?.DeathHistory is not { } list || !list.Remove(record)) return;
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    // Remove every record whose status is Recovered.
    public void ClearAllRecovered()
    {
        if (_profile.Current?.DeathHistory is not { } list) return;
        if (list.RemoveAll(r => r.Status == DeathRecoveryStatus.Recovered) == 0) return;
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    // Walk to the room a death occurred in. Returns false when no walker is
    // attached or the record has no recorded room.
    public bool WalkToDeathRoom(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_walker is null || record.Room is not { } r) return false;
        return _walker.WalkTo(new RoomKey(r.Map, r.Room));
    }

    // Demand signal to recover a deathpile. If we're already standing in the
    // death room, grab every recorded pile item in place (and re-equip the worn
    // ones when Auto-Equip is on); otherwise start walking there and grab on
    // arrival. The grab is forced regardless of the Auto-Recover toggle — the
    // user asked for it explicitly.
    public bool RecoverNow(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Room is not { } target) return false;

        Room? here = _roomTracker.State.CurrentRoom;
        bool inRoom = here is not null && here.Key.Map == target.Map && here.Key.Room == target.Room;
        string where = record.RoomName ?? record.RoomKeyText;

        if (inRoom)
        {
            _log?.Info(LogCategory, $"recover-now: in death room {where} — grabbing pile");
            BeginRecovery(record, autoGrab: true);
            return true;
        }

        // Walking back: arm a one-shot force-grab so arrival recovers even
        // when Auto-Recover is off.
        _pendingRecoverNow = record;
        bool walking = WalkToDeathRoom(record);
        if (walking)
            _log?.Info(LogCategory, $"recover-now: walking to {where} then recovering");
        else
            _pendingRecoverNow = null;
        return walking;
    }

    // ----- recovery state machine -------------------------------------

    // Fires on every room transition. On entering (Confirmed) a room that holds
    // an un-recovered deathpile, begin recovery; on leaving the pile we were
    // recovering, drop the in-progress tracker (the record stays Partial until we
    // return and finish).
    private void OnRoomChanged(RoomTransition t)
    {
        Room? room = t.NewRoom;

        if (_activeRecovery is { Room: { } ar }
            && (room is null || ar.Map != room.Key.Map || ar.Room != room.Key.Room))
        {
            _activeRecovery = null;
            _remaining.Clear();
        }

        if (room is null || t.NewConfidence != RoomConfidence.Confirmed) return;

        DeathRecord? rec = FindRecoverableAt(room.Key);
        if (rec is null || ReferenceEquals(_activeRecovery, rec)) return;

        bool force = ReferenceEquals(_pendingRecoverNow, rec);
        if (force) _pendingRecoverNow = null;
        BeginRecovery(rec, autoGrab: AutoRecover || force);
    }

    // Newest un-recovered record whose death room matches key.
    private DeathRecord? FindRecoverableAt(RoomKey key)
    {
        if (_profile.Current?.DeathHistory is not { } list) return null;
        DeathRecord? best = null;
        foreach (DeathRecord r in list)
        {
            if (r.Status == DeathRecoveryStatus.Recovered) continue;
            if (r.Room is not { } room || room.Map != key.Map || room.Room != key.Room) continue;
            if (best is null || r.RecordNumber > best.RecordNumber) best = r;
        }
        return best;
    }

    // Start (or restart) recovering a deathpile: mark the record Partial, arm the
    // per-item tracker, and — when autoGrab — send a get for every pile item.
    // Active → Partial → Recovered then follows observed "You pick up ..." lines.
    // A known-empty pile (nothing was lost) jumps straight to Recovered.
    private void BeginRecovery(DeathRecord record, bool autoGrab)
    {
        _activeRecovery = record;
        _remaining.Clear();
        AddNames(_remaining, record.EquippedAtDeath);
        AddNames(_remaining, record.LostItems);
        _recoveryTotal = _remaining.Count;

        bool known = record.EquippedAtDeath is not null || record.LostItems is not null;
        if (known && _recoveryTotal == 0)
        {
            FinalizeRecovered(record, "Nothing was lost at death.");
            return;
        }

        if (record.Status == DeathRecoveryStatus.Active)
            SetStatus(record, DeathRecoveryStatus.Partial, "Returned to the death room — recovering.");

        if (!autoGrab) return;
        foreach (string name in _remaining)
        {
            _log?.Info(LogCategory, $"auto-recover: get {name}");
            Send($"get {name}");
        }
    }

    // Watch for "You pick up <item>." confirmations while a recovery is in
    // progress. Each matched pile item is cleared from the remaining set (and
    // re-equipped when Auto-Equip is on and it was worn at death); the record
    // flips to Recovered once the set empties.
    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine || _activeRecovery is null || _remaining.Count == 0) return;

        Match m = PickUpRegex().Match(line.Text);
        if (!m.Success) return;
        string picked = m.Groups[1].Value;

        // The pickup wording may carry an article ("You pick up a torch.")
        // while the recorded name is bare ("torch"), so match by containment —
        // but bounded to whole words so a recorded "war" doesn't get marked
        // recovered when we pick up a "warhammer".
        List<string>? hits = null;
        foreach (string name in _remaining)
        {
            if (ContainsWord(picked, name))
                (hits ??= new()).Add(name);
        }
        if (hits is null) return;

        DeathRecord record = _activeRecovery;
        foreach (string name in hits)
        {
            _remaining.Remove(name);
            ReequipIfWorn(record, name);
        }

        if (_remaining.Count == 0)
            FinalizeRecovered(record, $"Recovered {_recoveryTotal} item(s).");
    }

    // Whole-word containment (case-insensitive): true when needle appears in
    // haystack not flanked by word characters on either side. Lookarounds rather
    // than \b so a needle that starts / ends with a non-word char (e.g. a
    // "+2"-suffixed item) still matches. Prevents a recorded "war" matching a
    // picked-up "warhammer" that a plain substring check would.
    private static bool ContainsWord(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return false;
        return Regex.IsMatch(haystack, $@"(?<!\w){Regex.Escape(needle)}(?!\w)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private void ReequipIfWorn(DeathRecord record, string name)
    {
        if (!AutoEquip || record.EquippedAtDeath is not { } worn) return;
        DeathItem? item = worn.FirstOrDefault(i =>
            string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        string verb = item.IsHeld ? "hold" : "wear";
        _log?.Info(LogCategory, $"auto-equip: {verb} {name}");
        Send($"{verb} {name}");
    }

    private void FinalizeRecovered(DeathRecord record, string message)
    {
        _activeRecovery = null;
        _remaining.Clear();
        SetStatus(record, DeathRecoveryStatus.Recovered, message);
    }

    private void SetStatus(DeathRecord record, DeathRecoveryStatus status, string message)
    {
        if (record.Status == status) return;
        record.Status = status;
        record.RecoveryMessage = message;
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    private static void AddNames(HashSet<string> set, List<DeathItem>? items)
    {
        if (items is null) return;
        foreach (DeathItem i in items)
            if (!string.IsNullOrWhiteSpace(i.Name)) set.Add(i.Name);
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    // Test seam — feed a plain inbound line to the pickup parser.
    internal void FeedTestLine(string text, DateTimeOffset? when = null)
        => OnLine(new LineExtractor.EmittedLine(text, [], when ?? DateTimeOffset.UtcNow, false));

    // Append a synthetic death record (the "Simulate Death" button) so the DEATH
    // grid + recovery flow can be exercised without dying in game. Decrements the
    // displayed lives by one, floored at zero.
    public void SimulateDeath()
    {
        if (_profile.Current is not { } p) return;
        p.DeathHistory ??= new List<DeathRecord>();
        Room? here = _roomTracker.State.CurrentRoom;
        // Continue the declining-lives series from the most recent record.
        int prevLives = p.DeathHistory.Count > 0 ? p.DeathHistory[^1].LivesRemaining : 0;
        var record = new DeathRecord(
            DateTimeOffset.UtcNow,
            here is null ? null : new RoomRef(here.Key.Map, here.Key.Room),
            Math.Max(0, prevLives - 1),
            "Simulated death (test).")
        {
            RecordNumber = p.DeathHistory.Count + 1,
            RoomName = here?.Name,
            Status = DeathRecoveryStatus.Active,
        };
        if (_inventorySnapshot is { } provider)
        {
            (List<DeathItem> equipped, List<DeathItem> lost) =
                DeathLootCapture.FromSnapshot(provider());
            record.EquippedAtDeath = equipped;
            record.LostItems = lost;
        }
        p.DeathHistory.Add(record);
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deathWatcher.PlayerDied -= OnPlayerDied;
        _roomTracker.StateChanged -= OnRoomChanged;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }

    // "You pick up <item>." — the get-confirmation MajorMUD prints when an
    // item leaves the ground and enters inventory. A realm that phrases this
    // differently simply leaves the record Partial (the user can Mark
    // Recovered); no false transition occurs.
    [GeneratedRegex(@"^You pick up (.+?)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex PickUpRegex();
}
