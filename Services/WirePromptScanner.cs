using System.Text;
using System.Text.RegularExpressions;
using FujinTerm.Game;

namespace FujinTerm.Services;

/// <summary>
/// Streaming scanner that watches the post-IAC byte stream from the live
/// Telnet connection for MajorMUD status-line prompts and fires one
/// <see cref="PromptObserved"/> event per match. Bypasses
/// <see cref="Terminal.LineExtractor"/> for prompt parsing because the
/// server rewrites the statline in place (CR + erase-line + new content
/// on the same row) — by the time the cell grid finally emits a "line",
/// only the last statline survives and any intermediate HP / MA / position
/// changes are lost. Scanning the wire stream catches every update as it
/// lands.
/// </summary>
/// <remarks>
/// <para>
/// Stateful: CSI escapes (<c>ESC '[' params final-byte</c>) are stripped
/// inline as bytes arrive, so a sequence like
/// <c>[HP=27\x1b[0;37m/MA=31\x1b[0;37m]:</c> still matches the regex.
/// A small carryover buffer (~1 KB cap) preserves partial matches
/// across chunk boundaries.
/// </para>
/// <para>
/// Unanchored on purpose: the server chains multiple statlines back-to-back
/// on the same row (<c>[HP=27/MA=31]:[HP=28/MA=34]:[HP=29/MA=34]:</c>),
/// so the original <c>^</c>-anchored line regex would only catch the first.
/// </para>
/// </remarks>
public sealed class WirePromptScanner
{
    private const int BufferCap = 1024;

    /// <summary>
    /// Stripped-text carryover. Bytes flow through the inline ANSI state
    /// machine into here; the regex runs against this string.
    /// </summary>
    private readonly StringBuilder _buffer = new(BufferCap);

    private StripState _state;

    /// <summary>
    /// The active status-line pattern. Defaults to the permissive
    /// class-default shape; <see cref="InstallRegex"/> swaps in a regex
    /// built from the user's custom statline so the parser matches whatever
    /// the editor authored. Reference assignment is atomic, so a swap from
    /// the UI thread while <see cref="Append"/> reads it off the Telnet pump
    /// is safe — at worst one append still uses the previous pattern.
    /// </summary>
    private Regex _statusLine = StatlinePromptRegexBuilder.Default;

    /// <summary>Fired once per matched status line, in the order observed on the wire.</summary>
    public event Action<PromptObservation>? PromptObserved;

    /// <summary>
    /// Fired (at most once per <see cref="Append"/>) when a default-shaped
    /// statline appears that the active pattern did <b>not</b> match — i.e. the
    /// live prompt isn't the statline the editor authored. Drives the logon
    /// reconciler to resend <c>set statline</c>. Structurally unreachable while
    /// the active pattern IS the default, so default-statline users never see
    /// it (and never trigger a resend).
    /// </summary>
    public event Action? PromptShapeUnmatched;

    /// <summary>
    /// Swap in the status-line pattern for the active profile's statline —
    /// built by <see cref="StatlinePromptRegexBuilder"/> from the editor
    /// command string. Installed on profile load / mutation so the scanner
    /// reads exactly the shape the BBS was told to print.
    /// </summary>
    public void InstallRegex(Regex statusLine)
    {
        ArgumentNullException.ThrowIfNull(statusLine);
        _statusLine = statusLine;
    }

    /// <summary>Restore the permissive class-default pattern (on profile close).</summary>
    public void ResetRegexToDefault() => _statusLine = StatlinePromptRegexBuilder.Default;

    /// <summary>
    /// Append <paramref name="data"/> from the live Telnet stream. Strips
    /// CSI escapes inline, runs the status-line regex, and fires
    /// <see cref="PromptObserved"/> for each match.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        foreach (byte b in data)
        {
            switch (_state)
            {
                case StripState.Normal:
                    if (b == 0x1B) { _state = StripState.EscSeen; }
                    else if (b >= 0x20 && b < 0x7F)
                    {
                        // Printable ASCII. The statline regex only cares about
                        // these. Drop CR / LF / other controls — between two
                        // statlines they're just separators, never inside one.
                        _buffer.Append((char)b);
                    }
                    break;

                case StripState.EscSeen:
                    _state = b == (byte)'[' ? StripState.Csi : StripState.Normal;
                    break;

                case StripState.Csi:
                    // CSI final bytes are 0x40-0x7E. Parameter / intermediate
                    // bytes (0x20-0x3F) keep us in Csi.
                    if (b >= 0x40 && b <= 0x7E) _state = StripState.Normal;
                    break;
            }
        }

        // Run the regex over the carryover. Successive matches on the same
        // buffer are cheap because the StringBuilder→string conversion happens
        // once and the regex is compiled.
        string text = _buffer.ToString();
        int lastEnd = 0;
        bool activeMatched = false;
        foreach (Match m in _statusLine.Matches(text))
        {
            if (!int.TryParse(m.Groups["hp"].Value, out int hp)) continue;
            activeMatched = true;

            string typeRaw = m.Groups["type"].Value;
            ManaType manaType = typeRaw switch
            {
                "MA"  => ManaType.Mana,
                "KAI" => ManaType.Kai,
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

            PromptObserved?.Invoke(new PromptObservation(hp, manaType, mana, position));
            lastEnd = m.Index + m.Length;
        }

        // Mismatch detection for the logon reconciler: if the active pattern
        // matched nothing here but a default-shaped statline IS present, the
        // server is printing a statline our editor-built pattern doesn't
        // recognise (typically: editor holds a custom statline but the game is
        // still on the class default). Signal it once so the reconciler can
        // resend `set statline`. Skipped when the active pattern already IS the
        // default — default-statline users can't drift, so they never resend.
        if (!activeMatched && !ReferenceEquals(_statusLine, StatlinePromptRegexBuilder.Default))
        {
            bool defaultMatched = false;
            foreach (Match d in StatlinePromptRegexBuilder.Default.Matches(text))
            {
                defaultMatched = true;
                int end = d.Index + d.Length;
                if (end > lastEnd) lastEnd = end;
            }
            if (defaultMatched) PromptShapeUnmatched?.Invoke();
        }

        // Drop everything up to the last match — the tail (anything after the
        // last match's end) might be the start of a partial statline that
        // completes in the next Append, so keep it.
        if (lastEnd > 0)
        {
            _buffer.Remove(0, lastEnd);
        }

        // Hard cap so a long quiet stretch of non-statline text doesn't pin
        // memory. Drops oldest first; statlines are short so we never lose a
        // legitimate in-flight partial match here.
        if (_buffer.Length > BufferCap)
        {
            _buffer.Remove(0, _buffer.Length - BufferCap);
        }
    }

    /// <summary>Reset the scanner — drops carryover and any in-flight CSI escape.</summary>
    public void Reset()
    {
        _buffer.Clear();
        _state = StripState.Normal;
    }

    private enum StripState : byte { Normal, EscSeen, Csi }
}

/// <summary>One observed prompt — payload of <see cref="WirePromptScanner.PromptObserved"/>.</summary>
public readonly record struct PromptObservation(
    int Hp,
    ManaType ManaType,
    int Mana,
    PlayerPosition Position);
