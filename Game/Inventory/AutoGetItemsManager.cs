using System.Text;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Inventory;

// Auto-get items engine. Parses the room "You notice <list> here." survey
// line, resolves each entry against game data, and sends get <item name> for
// any item flagged ItemOverlay.AutoCollect.
//
// There is no bulk "get all" verb in MajorMUD — each item is collected
// individually by name. Every entry the survey line names is run through the
// injected resolver, which maps the loose room wording back to an item Number
// and reads its per-character AutoCollect override. Non-items (cash entries,
// scenery) and items not flagged for collection are skipped.
//
// Collect-after-combat: when CashSettings.CollectAfterCombatFinished is set
// (the shared Cash + Items timing toggle) and the room still holds engageable
// hostiles, the gets are queued and flushed on OnRoomObserved once combat
// clears (no engageable hostiles remain). When the toggle is off, or no
// hostiles are present, the gets fire immediately. A room change (OnRoomChanged)
// discards any un-flushed queue — those items belong to a room we've left.
//
// Movement gate: collecting (or deferring) items asserts the shared
// AcquisitionGate so the walker holds until get-clear — deferred items hold the
// gate before CombatStateTracker clears the Combat gate, defeating the
// synchronous walker-resume race; immediate gets hold it through a settle
// window. Bound via SetAcquisitionGate (optional — unbound, the engine doesn't
// gate movement).
//
// Master switch: AutoActionDefaults.AutoGetItems (shared with the Settings →
// General toggle and the toolbar Toggle command).
//
// Not yet handled: needs-fulfillment (grabbing a torch to satisfy a LightSource
// need), encumbrance gating, batching, and party provisioning.
public sealed class AutoGetItemsManager : IDisposable
{
    // LogService category — [AutoGet] rows per collected / deferred item.
    public const string LogCategory = "AutoGet";

    // One resolved room entry: the item's canonical Number (for the held-count
    // lookup), the name to send to the game, whether the user flagged it for
    // auto-collection, whether it is marked CannotBeTaken (a hard never-pick-up
    // flag that wins over AutoCollect — the engine never sends get for it), and
    // the MaxToGet carry cap (int.MaxValue = unbounded).
    public sealed record ResolvedItem(int Number, string Name, bool AutoCollect, bool CannotBeTaken, int MaxToGet);

    private readonly Func<string, ResolvedItem?> _resolve;
    private readonly Func<int, int> _heldCount;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _collectAfterCombatFinished;
    private readonly Func<bool> _hasEngageableHostiles;
    private readonly Func<bool> _isPeekSuppressed;
    private readonly LogService? _log;
    private readonly IDisposable _noticeSub;

    private Terminal.LineExtractor? _lines;
    private string? _noticeBuffer;            // multi-line continuation

    // Items deferred until the room's combat finishes. Cleared on flush
    // and on room change. Holds canonical names (already resolved).
    private readonly List<string> _deferred = new();

    private Action<byte[]>? _wireSender;
    private AcquisitionGate? _gate;
    private bool _disposed;

    public AutoGetItemsManager(
        MessageRouter router,
        Func<string, ResolvedItem?> resolve,
        Func<bool> isEnabled,
        Func<bool> collectAfterCombatFinished,
        Func<bool> hasEngageableHostiles,
        Func<bool>? isPeekSuppressed = null,
        Func<int, int>? heldCount = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(collectAfterCombatFinished);
        ArgumentNullException.ThrowIfNull(hasEngageableHostiles);
        _resolve = resolve;
        // Null when unbound (tests without a cap case) → nothing is ever held,
        // so a MaxToGet cap can still fire off the first pickup. Live wiring
        // supplies the carried+worn+key-ring count.
        _heldCount = heldCount ?? (static _ => 0);
        _isEnabled = isEnabled;
        _collectAfterCombatFinished = collectAfterCombatFinished;
        _hasEngageableHostiles = hasEngageableHostiles;
        // Null when unbound (tests) → never a peek. A `look <dir>` peek renders a
        // full "You notice" survey for the adjacent room; gate the get path on it
        // so we don't send get commands (and trigger the get→inventory→equip
        // chain) against a room the player never entered.
        _isPeekSuppressed = isPeekSuppressed ?? (static () => false);
        _log = log;

        _noticeSub = router.Subscribe(KnownPatterns.YouNoticeRoom, OnYouNoticeRoom);
    }

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the shared AcquisitionGate so collecting (or deferring) items holds
    // the walker until get-clear. Optional — when unbound the engine doesn't
    // gate movement.
    public void SetAcquisitionGate(AcquisitionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
    }

    // Bind the per-session LineExtractor so the manager can stitch a wrapped
    // "You notice" survey back together.
    public void AttachLineExtractor(Terminal.LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Called on each room-entity observation (wired after the combat tracker so
    // the hostile check is current). Flushes the deferred queue once no
    // engageable hostiles remain — the "combat finished for this room" signal.
    public void OnRoomObserved()
    {
        if (_deferred.Count == 0) return;
        if (_hasEngageableHostiles()) return;   // still fighting — keep waiting
        FlushDeferred();
    }

    // Called on actual room change. Discards any un-flushed deferred gets — the
    // items belonged to the room we just left.
    public void OnRoomChanged()
    {
        if (_deferred.Count == 0) return;
        _log?.Debug(LogCategory, $"room changed — dropping {_deferred.Count} deferred get(s)");
        _deferred.Clear();
        _gate?.NoteDeferredCleared();
    }

    // ----- notice parsing ----------------------------------------------

    // Single-line "You notice <list> here." — the pattern subscription path.
    // Multi-line wraps stitch through OnLine and feed the same dispatch.
    private void OnYouNoticeRoom(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        DispatchList(m.Groups[0]);
    }

    // Multi-line stitch mirrors CashManager.OnLine — a wrapped survey
    // ("You notice ...\n... here.") arrives as two emitted lines, so we
    // buffer from the "You notice " row until a row ends with '.'.
    // Duplicated rather than shared because the two engines own
    // independent buffers and CashManager is already shipped; factoring
    // a common stitcher would mean touching that engine for no behaviour
    // change. Single-line surveys are skipped here (the pattern
    // subscription handles them) to avoid double-processing.
    private void OnLine(Terminal.LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        string text = line.Text.TrimEnd();
        if (text.Length == 0) return;

        if (_noticeBuffer is not null)
        {
            _noticeBuffer = _noticeBuffer + " " + text;
            if (text.EndsWith('.'))
            {
                string complete = _noticeBuffer;
                _noticeBuffer = null;
                ProcessMultiLine(complete);
            }
            return;
        }

        if (text.StartsWith("You notice ", StringComparison.Ordinal)
            && !text.EndsWith('.'))
        {
            _noticeBuffer = text;
        }
    }

    private void ProcessMultiLine(string completeLine)
    {
        const string prefix = "You notice ";
        if (!completeLine.StartsWith(prefix, StringComparison.Ordinal)) return;
        string body = completeLine[prefix.Length..].TrimEnd();
        const string suffix = " here.";
        if (body.EndsWith(suffix, StringComparison.Ordinal))
            body = body[..^suffix.Length];
        else if (body.EndsWith('.'))
            body = body[..^1];
        DispatchList(body);
    }

    // Split the survey list into entries, resolve each against game data, and
    // collect (or defer) those flagged for auto-collection. The deferred queue
    // is rebuilt per survey — a fresh "You notice" supersedes the prior room
    // snapshot.
    private void DispatchList(string list)
    {
        if (!_isEnabled()) return;
        // A look-direction peek renders a full "You notice" survey for the
        // adjacent room. Getting items from a room we never entered wastes
        // commands and (via the resulting inventory change) can fire auto-equip;
        // skip while the peek window is armed.
        if (_isPeekSuppressed())
        {
            _log?.Debug(LogCategory, "skipped you-notice survey (look-direction peek)");
            return;
        }

        bool deferMode = _collectAfterCombatFinished() && _hasEngageableHostiles();
        if (deferMode) _deferred.Clear();   // rebuild for this survey

        // Copies decided in this survey, by item Number — so a MaxToGet cap
        // holds even when a single "You notice" lists the same item twice
        // (the held-count snapshot doesn't move until the get echoes back).
        Dictionary<int, int>? decidedThisPass = null;

        foreach (string entry in SplitEntries(list))
        {
            ResolvedItem? item = _resolve(entry);
            if (item is null) continue;          // not an item / not in game data
            if (item.CannotBeTaken)              // hard never-pick-up flag wins over AutoCollect
            {
                _log?.Debug(LogCategory, $"skipped item={item.Name} (cannot be taken)");
                continue;
            }
            if (!item.AutoCollect) continue;     // user didn't flag it

            // MaxToGet carry cap. Count what we already hold (carried + worn +
            // key ring — key-type items land in the ring, not the pack) plus
            // anything already decided in this same survey; stop at the cap.
            if (item.MaxToGet != int.MaxValue)
            {
                decidedThisPass ??= new Dictionary<int, int>();
                int have = _heldCount(item.Number) + decidedThisPass.GetValueOrDefault(item.Number);
                if (have >= item.MaxToGet)
                {
                    _log?.Debug(LogCategory,
                        $"skipped item={item.Name} (have {have} >= max {item.MaxToGet})");
                    continue;
                }
                decidedThisPass[item.Number] = decidedThisPass.GetValueOrDefault(item.Number) + 1;
            }

            if (deferMode)
            {
                _deferred.Add(item.Name);
                _log?.Info(LogCategory, $"deferred (combat) item={item.Name}");
            }
            else
            {
                _log?.Info(LogCategory, $"collect item={item.Name}");
                _gate?.NoteGetSent();
                Send($"get {item.Name}");
            }
        }

        // Hold the walker while the queued gets wait for combat to finish.
        // Asserted now, before CombatStateTracker clears the Combat gate on
        // the same EntitiesObserved pass, so the walker can't slip out
        // between fight-clear and the loot flush.
        if (deferMode) _gate?.NoteDeferredPending(_deferred.Count);
    }

    private void FlushDeferred()
    {
        foreach (string name in _deferred)
        {
            _log?.Info(LogCategory, $"collect (post-combat) item={name}");
            _gate?.NoteGetSent();
            Send($"get {name}");
        }
        _deferred.Clear();
        _gate?.NoteDeferredCleared();
    }

    // Split "a, b and c" survey wording into individual entries — commas
    // separate all but the final pair, which uses " and ".
    private static IEnumerable<string> SplitEntries(string list)
    {
        foreach (string comma in list.Split(',', StringSplitOptions.TrimEntries
                                              | StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string piece in comma.Split(" and ",
                         StringSplitOptions.TrimEntries
                         | StringSplitOptions.RemoveEmptyEntries))
            {
                if (piece.Length > 0) yield return piece;
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
        _noticeSub.Dispose();
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}
