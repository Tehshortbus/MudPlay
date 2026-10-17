using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Game;

namespace MudPlay.Services;

// Streaming scanner that watches the post-IAC byte stream from the live Telnet
// connection for MajorMUD status-line prompts and fires one PromptObserved event
// per match. Bypasses Terminal.LineExtractor for prompt parsing because the
// server rewrites the statline in place (CR + erase-line + new content on the
// same row) — by the time the cell grid finally emits a "line", only the last
// statline survives and any intermediate HP / MA / position changes are lost.
// Scanning the wire stream catches every update as it lands.
//
// Stateful: CSI escapes (ESC '[' params final-byte) are stripped inline as bytes
// arrive, so a sequence like [HP=27\x1b[0;37m/MA=31\x1b[0;37m]: still matches the
// regex. A small carryover buffer (~1 KB cap) preserves partial matches across
// chunk boundaries.
//
// The regex remains unanchored because the server chains multiple statlines
// back-to-back on the same row
// ([HP=27/MA=31]:[HP=28/MA=34]:[HP=29/MA=34]:). Each candidate is nevertheless
// boundary-validated: it must start at a real wire row/control boundary or
// directly after a previously accepted statline. This rejects another player's
// prompt quoted inside chat ("Bob gossips: [HP=671/KAI=40]:w") without losing
// the chained rewrites this scanner exists to preserve.
public sealed partial class WirePromptScanner
{
    private const int BufferCap = 1024;

    // Stripped-text carryover. Bytes flow through the inline ANSI state machine
    // into here; the regex runs against this string.
    private readonly StringBuilder _buffer = new(BufferCap);

    // Offsets in _buffer immediately after a wire row/control boundary. Kept
    // separately instead of inserting sentinel characters into _buffer because
    // custom statlines may use %n: their regex intentionally spans CR/LF after the
    // scanner strips those controls. Offset 0 is a valid boundary for a fresh or
    // fully-consumed buffer.
    private readonly List<int> _promptBoundaries = new() { 0 };

    private StripState _state;

    // The active status-line pattern. Defaults to the permissive class-default
    // shape; InstallRegex swaps in a regex built from the user's custom statline
    // so the parser matches whatever the editor authored. Reference assignment is
    // atomic, so a swap from the UI thread while Append reads it off the Telnet
    // pump is safe — at worst one append still uses the previous pattern.
    private Regex _statusLine = StatlinePromptRegexBuilder.Default;

    // Fired once per matched status line, in the order observed on the wire.
    public event Action<PromptObservation>? PromptObserved;

    // Fired (at most once per Append) when statline-shaped text sits where the
    // prompt goes but the active pattern matched nothing in that read — the live
    // prompt isn't the statline Settings -> Statline describes. Carries the
    // offending prompt text. Fires for the default pattern too: a server whose
    // class default (or a statline set by hand in-game) isn't one of the stock
    // shapes leaves HP unreadable exactly like a drifted custom statline does.
    // StatlineReconciler decides what a run of these means.
    public event Action<string>? PromptShapeUnmatched;

    // The Stock engine's own prompt once the character is out of the game. The
    // engine prints it after every way out of the realm, and unlike the "character
    // has been saved" text before it a board can't reword it (GAME_MECHANICS "Realm
    // exit / logoff sequence").
    private const string RealmLeftPrompt = "[MAJORMUD]:";

    // Fired once for each RealmLeftPrompt that stands where a prompt goes: at the
    // start of a wire row, and not inside text the active statline pattern took for
    // its own (a custom statline may carry the same words).
    public event Action? RealmLeftPromptObserved;

    // Buffer offset the next search for RealmLeftPrompt starts from, so one prompt
    // left sitting in the carryover is reported once.
    private int _realmLeftScanFrom;

    // Buffer offset just past the last statline-shaped text reported unmatched.
    // Unmatched text isn't consumed (it may be the head of a prompt the active
    // pattern completes on the next read), so this keeps it from re-firing.
    private int _unmatchedScanFrom;

    // Whether the active pattern matched a prompt since the last command we sent, and
    // the buffer offset of the last cursor-line tail NoteCommandSent reported (-1 = none),
    // so one prompt is reported once however many commands go out before the next.
    private bool _matchedSinceSend;
    private int _tailReportedFrom = -1;

    // True while Append is handing the prompts it matched to PromptObserved. A
    // handler that sends commands from there (the prompt that ends a rest starts a
    // gear swap) reaches NoteCommandSent before the matched text is trimmed off the
    // buffer, and the second command of the burst reported the prompt that had just
    // matched as one the statline can't read (report paradigm-20261010-145330).
    private bool _dispatchingMatches;

    // Longest prompt text quoted in a report — enough to recognise it, short enough
    // for a one-line notice.
    private const int MaxReportedPrompt = 80;

    // The pool an unlabelled %m reads as. A custom statline may put any label (or
    // none) in front of %m; the character's stat screen says whether its pool is mana
    // or kai (AppServices keeps this current). Mana until a stat screen says otherwise.
    public ManaType UnlabeledManaType { get; set; } = ManaType.Mana;

    // Swap in the status-line pattern for the active profile's statline — built
    // by StatlinePromptRegexBuilder from the editor command string. Installed on
    // profile load / mutation so the scanner reads exactly the shape the BBS was
    // told to print.
    public void InstallRegex(Regex statusLine)
    {
        ArgumentNullException.ThrowIfNull(statusLine);
        _statusLine = statusLine;
    }

    // Restore the permissive class-default pattern (on profile close).
    public void ResetRegexToDefault() => _statusLine = StatlinePromptRegexBuilder.Default;

    // Whether a terminal row starts with the prompt the active pattern reads, and
    // what follows it on the row. For code that has a screen row rather than the
    // wire: it asks the pattern Settings -> Statline built, never a shape of its own.
    public bool TrySplitPromptRow(string? row, out string rest) =>
        StatlinePromptRegexBuilder.TrySplitPromptRow(_statusLine, row, out rest);

    // Append data from the live Telnet stream. Strips CSI escapes inline, runs
    // the status-line regex, and fires PromptObserved for each match.
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        foreach (byte b in data)
        {
            switch (_state)
            {
                case StripState.Normal:
                    if (b == 0x1B) { _state = StripState.EscSeen; }
                    else if (b is (byte)'\r' or (byte)'\n')
                    {
                        MarkPromptBoundary();
                    }
                    else if (b >= 0x20 && b < 0x7F)
                    {
                        // Printable ASCII. The statline regex only cares about
                        // these. CR / LF and row-rewrite CSI controls are recorded
                        // as boundary offsets above/below; other controls are dropped.
                        _buffer.Append((char)b);
                    }
                    break;

                case StripState.EscSeen:
                    _state = b == (byte)'[' ? StripState.Csi : StripState.Normal;
                    break;

                case StripState.Csi:
                    // CSI final bytes are 0x40-0x7E. Parameter / intermediate
                    // bytes (0x20-0x3F) keep us in Csi.
                    if (b >= 0x40 && b <= 0x7E)
                    {
                        // MajorMUD commonly starts each rewritten row with
                        // ESC[79D ESC[K rather than a literal CR. Record cursor-left /
                        // cursor-position / erase-line finals as row boundaries, but
                        // never SGR 'm' — colour changes occur inside a statline.
                        if (b is (byte)'D' or (byte)'G' or (byte)'H' or (byte)'f' or (byte)'K')
                            MarkPromptBoundary();
                        _state = StripState.Normal;
                    }
                    break;
            }
        }

        // Run the regex over the carryover. Successive matches on the same
        // buffer are cheap because the StringBuilder→string conversion happens
        // once and the regex is compiled.
        string text = _buffer.ToString();
        int lastEnd = 0;
        int previousAcceptedEnd = -1;
        bool activeMatched = false;
        List<(int Start, int End)>? statlineSpans = null;
        foreach (Match m in _statusLine.Matches(text))
        {
            if (!IsPromptBoundary(text, m.Index, previousAcceptedEnd)) continue;
            // The prompt IS the editor's statline even when it carries no HP
            // (a custom template without %h) — that's no mismatch to correct,
            // just nothing to observe.
            activeMatched = true;
            (statlineSpans ??= new()).Add((m.Index, m.Index + m.Length));
            if (!int.TryParse(m.Groups["hp"].Value, out int hp)) continue;

            string typeRaw = m.Groups["type"].Value;
            ManaType manaType = typeRaw switch
            {
                "MA"  => ManaType.Mana,
                "KAI" => ManaType.Kai,
                _ when m.Groups["mana"].Success => UnlabeledManaType,
                _      => ManaType.None,
            };

            int mana = 0;
            if (manaType != ManaType.None && int.TryParse(m.Groups["mana"].Value, out int parsedMana))
            {
                mana = parsedMana;
            }

            string posRaw = m.Groups["statea"].Success ? m.Groups["statea"].Value
                          : m.Groups["stateb"].Success ? m.Groups["stateb"].Value
                          : string.Empty;
            PlayerPosition position = posRaw switch
            {
                "Resting"    => PlayerPosition.Resting,
                "Meditating" => PlayerPosition.Meditating,
                _            => PlayerPosition.Standing,
            };

            _dispatchingMatches = true;
            try { PromptObserved?.Invoke(new PromptObservation(hp, manaType, mana, position)); }
            finally { _dispatchingMatches = false; }
            lastEnd = m.Index + m.Length;
            previousAcceptedEnd = lastEnd;
        }

        if (activeMatched) _matchedSinceSend = true;

        ScanForRealmLeftPrompt(text, statlineSpans);

        // Mismatch detection: the active pattern matched nothing here, yet
        // statline-shaped text sits where the prompt goes — the start of a
        // wire row, which is where the server parks the prompt before our
        // command's echo or leaves the cursor after output. The server is
        // printing a statline our editor-built pattern doesn't recognise
        // (editor holds a custom statline but the game is on the class default,
        // or the game prints a shape the Default pattern doesn't cover). Signal
        // it once per read; the reconciler counts consecutive signals, so one
        // stray bracketed server line can't act on its own.
        if (!activeMatched)
        {
            string? unmatched = null;
            // Text chained straight onto the last reported prompt is a prompt
            // position too, same as the chained-rewrite rule for matches.
            int previousShapeEnd = _unmatchedScanFrom > 0 ? _unmatchedScanFrom : -1;
            foreach (Match c in StatlineShape().Matches(text, Math.Min(_unmatchedScanFrom, text.Length)))
            {
                if (!IsPromptBoundary(text, c.Index, previousShapeEnd)) continue;
                // Every statline carries at least a number; bracketed menu
                // prompts like "[MAJORMUD]:" don't.
                if (!c.ValueSpan.ContainsAnyInRange('0', '9')) continue;
                unmatched = c.Value;
                previousShapeEnd = c.Index + c.Length;
            }
            if (unmatched is not null)
            {
                _unmatchedScanFrom = previousShapeEnd;
                PromptShapeUnmatched?.Invoke(unmatched);
            }
        }

        // Drop everything up to the last match — the tail (anything after the
        // last match's end) might be the start of a partial statline that
        // completes in the next Append, so keep it.
        if (lastEnd > 0)
        {
            RemovePrefix(lastEnd, establishStartBoundary: true);
        }

        // Hard cap so a long quiet stretch of non-statline text doesn't pin
        // memory. Drops oldest first; statlines are short so we never lose a
        // legitimate in-flight partial match here.
        if (_buffer.Length > BufferCap)
        {
            RemovePrefix(_buffer.Length - BufferCap, establishStartBoundary: false);
        }
    }

    private void ScanForRealmLeftPrompt(string text, List<(int Start, int End)>? statlineSpans)
    {
        int from = Math.Min(_realmLeftScanFrom, text.Length);
        int at;
        while ((at = text.IndexOf(RealmLeftPrompt, from, StringComparison.Ordinal)) >= 0)
        {
            from = at + RealmLeftPrompt.Length;
            bool insideStatline = false;
            if (statlineSpans is not null)
                foreach ((int start, int end) in statlineSpans)
                    if (at >= start && at < end) { insideStatline = true; break; }
            if (insideStatline || !IsPromptBoundary(text, at, previousAcceptedEnd: -1)) continue;
            RealmLeftPromptObserved?.Invoke();
        }
        // Keep the tail that could still be the head of the prompt split across reads.
        _realmLeftScanFrom = Math.Max(from, text.Length - (RealmLeftPrompt.Length - 1));
    }

    private void MarkPromptBoundary()
    {
        int offset = _buffer.Length;
        if (_promptBoundaries.Count == 0 || _promptBoundaries[^1] != offset)
            _promptBoundaries.Add(offset);
    }

    // A candidate is valid when only spaces separate it from the nearest real
    // wire boundary, or from the end of the previously accepted prompt (the
    // chained-statline case). Any printable prefix — especially "X gossips: " —
    // makes it ordinary text rather than our status line.
    private bool IsPromptBoundary(string text, int candidateStart, int previousAcceptedEnd)
    {
        if (previousAcceptedEnd >= 0
            && OnlySpaces(text, previousAcceptedEnd, candidateStart))
            return true;

        for (int i = _promptBoundaries.Count - 1; i >= 0; i--)
        {
            int boundary = _promptBoundaries[i];
            if (boundary > candidateStart) continue;
            return OnlySpaces(text, boundary, candidateStart);
        }
        return false;
    }

    private static bool OnlySpaces(string text, int start, int end)
    {
        if (start < 0 || end < start) return false;
        for (int i = start; i < end; i++)
            if (text[i] != ' ') return false;
        return true;
    }

    private void RemovePrefix(int count, bool establishStartBoundary)
    {
        if (count <= 0) return;
        _buffer.Remove(0, count);
        _unmatchedScanFrom = Math.Max(0, _unmatchedScanFrom - count);
        _realmLeftScanFrom = Math.Max(0, _realmLeftScanFrom - count);
        _tailReportedFrom = _tailReportedFrom >= count ? _tailReportedFrom - count : -1;

        int write = 0;
        for (int read = 0; read < _promptBoundaries.Count; read++)
        {
            int shifted = _promptBoundaries[read] - count;
            if (shifted < 0) continue;
            if (write > 0 && _promptBoundaries[write - 1] == shifted) continue;
            _promptBoundaries[write++] = shifted;
        }
        if (write < _promptBoundaries.Count)
            _promptBoundaries.RemoveRange(write, _promptBoundaries.Count - write);

        if (establishStartBoundary
            && (_promptBoundaries.Count == 0 || _promptBoundaries[0] != 0))
            _promptBoundaries.Insert(0, 0);
    }

    // A command just went out. Whatever the server left on the cursor's row is its
    // prompt — the one place a prompt is certain to be, whatever it looks like (a
    // statline set to plain text has no brackets or numbers for the shape check to
    // find). If no prompt has matched the active pattern since the last command and
    // that row holds text, report it as unmatched. Must run on the thread that feeds
    // Append.
    public void NoteCommandSent()
    {
        // Sent in answer to a prompt that matched: the cursor's row holds that prompt.
        if (_dispatchingMatches) return;
        if (_matchedSinceSend)
        {
            _matchedSinceSend = false;
            return;
        }
        int start = _promptBoundaries.Count > 0 ? _promptBoundaries[^1] : 0;
        if (start > _buffer.Length || start == _tailReportedFrom) return;
        // The shape check already reported this row's prompt when it arrived.
        if (_unmatchedScanFrom > start) return;
        string tail = _buffer.ToString(start, _buffer.Length - start).Trim();
        if (tail.Length == 0) return;
        _tailReportedFrom = start;
        PromptShapeUnmatched?.Invoke(tail.Length > MaxReportedPrompt ? tail[..MaxReportedPrompt] : tail);
    }

    // Drops carryover and any in-flight CSI escape. The client calls it when the
    // link drops, so a row the old connection left half-sent starts nothing.
    public void Reset()
    {
        _buffer.Clear();
        _promptBoundaries.Clear();
        _promptBoundaries.Add(0);
        _unmatchedScanFrom = 0;
        _realmLeftScanFrom = 0;
        _matchedSinceSend = false;
        _tailReportedFrom = -1;
        _state = StripState.Normal;
    }

    // Statline-shaped text: one or more bracketed groups closed by a colon —
    // "[HP=145/MA=46]:", "[HP=145/145][MA=46/46]:", "[HP=12 MA=3]:". Loose on
    // purpose: it only has to recognise that a prompt arrived, not read it.
    [GeneratedRegex(@"\[[^\[\]]{1,40}\](?:\s?\[[^\[\]]{1,40}\]){0,4}\s?:", RegexOptions.CultureInvariant)]
    private static partial Regex StatlineShape();

    private enum StripState : byte { Normal, EscSeen, Csi }
}

// One observed prompt — payload of WirePromptScanner.PromptObserved.
public readonly record struct PromptObservation(
    int Hp,
    ManaType ManaType,
    int Mana,
    PlayerPosition Position);
