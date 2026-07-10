using FujinTerm.Game.Cash;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.Inventory;

// Tracks the items lying on the current room's floor, parsed from the
// "You notice <list> here." survey line. Cash entries are filtered out (coin is
// the CashManager's domain), so the snapshot is item-only — the single source
// both @what (read the list back) and @get-all (send get per item) consume.
//
// The snapshot is per-room. A fresh survey rebuilds it wholesale (a new "You
// notice" supersedes the prior list), and a room change clears it via
// OnRoomChanged — the loot belonged to the room we left, and an empty room emits
// no survey line, so without the clear a stale list would linger. Item wording
// is preserved verbatim ("a rusty dagger"), so get can match on the noun phrase
// after the caller strips the article.
//
// There is no bulk "get all" verb in MajorMUD — @get-all walks this list and
// sends one get per entry. Cash recognition is stricter than the CashManager's
// collect rule (see IsCashEntry): a denomination word used as a material
// adjective ("a silver ring") must stay an item, so only a counted coin or the
// canonical "a <denom> piece" singular is filtered.
public sealed class GroundItemTracker : IDisposable
{
    // The four stable single-word denominations; the fifth (runic) is
    // recognised via _naming because a board can rename the runic word.
    private static readonly HashSet<string> StableDenominations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "copper", "silver", "gold", "platinum",
        };

    private readonly IDisposable _noticeSub;
    private readonly List<string> _items = new();
    private readonly CurrencyNaming _naming;

    private Terminal.LineExtractor? _lines;
    private string? _noticeBuffer;            // multi-line continuation
    private bool _disposed;

    public GroundItemTracker(MessageRouter router, CurrencyNaming naming)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(naming);
        _naming = naming;
        _noticeSub = router.Subscribe(KnownPatterns.YouNoticeRoom, OnYouNoticeRoom);
    }

    // Item names on the room floor from the latest survey, cash excluded,
    // wording preserved for a get match. Empty until a "You notice" line lands;
    // cleared on room change.
    public IReadOnlyList<string> Items => _items;

    // Bind the per-session LineExtractor so the tracker can stitch a wrapped
    // "You notice" survey back together — same shape as AutoGetItemsManager /
    // the CashManager.
    public void AttachLineExtractor(Terminal.LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Discard the snapshot on an actual room change — the floor loot belonged to
    // the room we just left.
    public void OnRoomChanged()
    {
        _noticeBuffer = null;
        _items.Clear();
    }

    // ----- notice parsing ----------------------------------------------

    // Single-line "You notice <list> here." — the pattern subscription path.
    // Multi-line wraps stitch through OnLine and feed the same rebuild.
    private void OnYouNoticeRoom(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        RebuildFrom(m.Groups[0]);
    }

    // Multi-line stitch mirrors CashManager / AutoGetItemsManager — a wrapped
    // survey arrives as two emitted lines, so we buffer from the "You notice "
    // row until a row ends with '.'. Single-line surveys are skipped here (the
    // pattern subscription handles them) to avoid double-processing.
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
        RebuildFrom(body);
    }

    // Rebuild the snapshot from a survey list — split into entries, drop cash,
    // keep item wording verbatim.
    private void RebuildFrom(string list)
    {
        _items.Clear();
        foreach (string entry in SplitEntries(list))
        {
            if (IsCashEntry(entry)) continue;
            _items.Add(entry);
        }
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

    // Cash entries in the survey are either a counted coin ("5 gold crowns",
    // "50 gold sovereigns") or the stock singular ("a gold piece"). We must
    // NOT swallow items whose material adjective happens to be a denomination
    // word ("a silver ring", "a copper key"), so the two forms are gated
    // differently: a bare count + denomination is unambiguously cash (ground
    // items always carry an article, never a leading count); the singular "a
    // <denom> ..." only counts as cash when it ends in the canonical coin noun
    // "piece(s)", leaving material-adjective items intact.
    private bool IsCashEntry(string entry)
    {
        string[] words = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return false;

        if (int.TryParse(words[0], out _))
            return IsCashWord(words[1]);

        if ((string.Equals(words[0], "a", StringComparison.OrdinalIgnoreCase)
             || string.Equals(words[0], "an", StringComparison.OrdinalIgnoreCase))
            && IsCashWord(words[1]))
        {
            return string.Equals(words[^1], "piece", StringComparison.OrdinalIgnoreCase)
                || string.Equals(words[^1], "pieces", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    // True when word names any cash denomination — one of the stable four or
    // the active board's runic word (stock "runic" included).
    private bool IsCashWord(string word) =>
        StableDenominations.Contains(word) || _naming.IsRunic(word);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _noticeSub.Dispose();
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}
