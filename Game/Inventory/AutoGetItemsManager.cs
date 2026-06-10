using System.Text;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Inventory;

/// <summary>
/// Phase 9 PR 9.L — auto-get items engine. Parses the room
/// "You notice &lt;list&gt; here." survey line, resolves each entry
/// against game data, and sends <c>get &lt;item name&gt;</c> for any
/// item flagged <see cref="Models.GameData.ItemOverlay.AutoCollect"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is no bulk "get all" verb in MajorMUD — each item is
/// collected individually by name. Every entry the survey line names
/// is run through the injected resolver, which maps the loose room
/// wording back to an item <c>Number</c> and reads its per-character
/// <c>AutoCollect</c> override. Non-items (cash entries, scenery) and
/// items not flagged for collection are skipped.
/// </para>
/// <para>
/// <b>Collect-after-combat</b>: when
/// <see cref="Models.Profile.ItemLootSettings.CollectAfterCombatFinished"/>
/// is set and the room still holds engageable hostiles, the gets are
/// queued and flushed on <see cref="OnRoomObserved"/> once combat
/// clears (no engageable hostiles remain). When the toggle is off, or
/// no hostiles are present, the gets fire immediately. A room change
/// (<see cref="OnRoomChanged"/>) discards any un-flushed queue — those
/// items belong to a room we've left.
/// </para>
/// <para>
/// Master switch: <see cref="Models.Profile.AutoActionDefaults.AutoGetItems"/>
/// (shared with the Settings → General toggle and the toolbar Toggle
/// command).
/// </para>
/// <para>
/// <b>Deferred to follow-ups</b>: needs-fulfillment (grabbing a torch
/// to satisfy a LightSource need), encumbrance gating, batching, an
/// Acquisition movement gate (v1 mirrors <c>CashManager</c> and does
/// not pause the walker), and party provisioning.
/// </para>
/// </remarks>
public sealed class AutoGetItemsManager : IDisposable
{
    /// <summary>LogService category — <c>[AutoGet]</c> rows per
    /// collected / deferred item.</summary>
    public const string LogCategory = "AutoGet";

    /// <summary>One resolved room entry: the canonical name to send to
    /// the game and whether the user flagged it for auto-collection.</summary>
    public sealed record ResolvedItem(string Name, bool AutoCollect);

    private readonly Func<string, ResolvedItem?> _resolve;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _collectAfterCombatFinished;
    private readonly Func<bool> _hasEngageableHostiles;
    private readonly LogService? _log;
    private readonly IDisposable _noticeSub;

    private Terminal.LineExtractor? _lines;
    private string? _noticeBuffer;            // multi-line continuation

    // Items deferred until the room's combat finishes. Cleared on flush
    // and on room change. Holds canonical names (already resolved).
    private readonly List<string> _deferred = new();

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public AutoGetItemsManager(
        MessageRouter router,
        Func<string, ResolvedItem?> resolve,
        Func<bool> isEnabled,
        Func<bool> collectAfterCombatFinished,
        Func<bool> hasEngageableHostiles,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(collectAfterCombatFinished);
        ArgumentNullException.ThrowIfNull(hasEngageableHostiles);
        _resolve = resolve;
        _isEnabled = isEnabled;
        _collectAfterCombatFinished = collectAfterCombatFinished;
        _hasEngageableHostiles = hasEngageableHostiles;
        _log = log;

        _noticeSub = router.Subscribe(KnownPatterns.YouNoticeRoom, OnYouNoticeRoom);
    }

    /// <summary>Bind the wire sender — the gate-wrapped engine pipeline
    /// from <c>MainWindowViewModel</c>.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Bind the per-session <see cref="Terminal.LineExtractor"/> so the
    /// manager can stitch a wrapped "You notice" survey back together.
    /// </summary>
    public void AttachLineExtractor(Terminal.LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    /// <summary>
    /// Called on each room-entity observation (wired to
    /// <c>RoomEntityClassifier.EntitiesObserved</c> after the combat
    /// tracker so the hostile check is current). Flushes the deferred
    /// queue once no engageable hostiles remain — the "combat finished
    /// for this room" signal.
    /// </summary>
    public void OnRoomObserved()
    {
        if (_deferred.Count == 0) return;
        if (_hasEngageableHostiles()) return;   // still fighting — keep waiting
        FlushDeferred();
    }

    /// <summary>
    /// Called on actual room change. Discards any un-flushed deferred
    /// gets — the items belonged to the room we just left.
    /// </summary>
    public void OnRoomChanged()
    {
        if (_deferred.Count == 0) return;
        _log?.Debug(LogCategory, $"room changed — dropping {_deferred.Count} deferred get(s)");
        _deferred.Clear();
    }

    // ----- notice parsing ----------------------------------------------

    /// <summary>Single-line "You notice &lt;list&gt; here." — the
    /// pattern subscription path. Multi-line wraps stitch through
    /// <see cref="OnLine"/> and feed the same dispatch.</summary>
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

    /// <summary>
    /// Split the survey list into entries, resolve each against game
    /// data, and collect (or defer) those flagged for auto-collection.
    /// The deferred queue is rebuilt per survey — a fresh "You notice"
    /// supersedes the prior room snapshot.
    /// </summary>
    private void DispatchList(string list)
    {
        if (!_isEnabled()) return;

        bool deferMode = _collectAfterCombatFinished() && _hasEngageableHostiles();
        if (deferMode) _deferred.Clear();   // rebuild for this survey

        foreach (string entry in SplitEntries(list))
        {
            ResolvedItem? item = _resolve(entry);
            if (item is null) continue;          // not an item / not in game data
            if (!item.AutoCollect) continue;     // user didn't flag it

            if (deferMode)
            {
                _deferred.Add(item.Name);
                _log?.Info(LogCategory, $"deferred (combat) item={item.Name}");
            }
            else
            {
                _log?.Info(LogCategory, $"collect item={item.Name}");
                Send($"get {item.Name}");
            }
        }
    }

    private void FlushDeferred()
    {
        foreach (string name in _deferred)
        {
            _log?.Info(LogCategory, $"collect (post-combat) item={name}");
            Send($"get {name}");
        }
        _deferred.Clear();
    }

    /// <summary>
    /// Split "a, b and c" survey wording into individual entries —
    /// commas separate all but the final pair, which uses " and ".
    /// </summary>
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
