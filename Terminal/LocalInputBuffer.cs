using System.Text;

namespace FujinTerm.Terminal;

/// <summary>
/// Client-side line buffer for the terminal control. Replaces
/// character-mode (every keystroke straight to the wire) with
/// line-mode: printable keystrokes accumulate locally and only flush
/// to the server when the user presses Enter.
/// </summary>
/// <remarks>
/// <para>
/// Two reasons the line-mode model wins for FujinTerm:
/// </para>
/// <list type="number">
///   <item>Engine auto-sends (par poll, AutoParty invite, @health
///         round-trip, @wait/@ok, etc.) can fire at any time. In
///         character-mode they interleave into the user's half-typed
///         input on the wire — the server's line buffer becomes
///         <c>"abc" + "par\r"</c> and submits as the garbage command
///         <c>"abcpar"</c>. Line-mode keeps the user's bytes off the
///         wire until they hit Enter; auto-sends sail through as
///         complete commands.</item>
///   <item>MajorMUD pauses server-side game output while it's waiting
///         on a user's in-progress statline. Buffering locally means
///         the server never sees the in-progress input, so game flow
///         keeps streaming while the user composes.</item>
/// </list>
/// <para>
/// Special keys (arrows, F-keys, Ctrl+letter, Escape, Tab) still pass
/// straight through to the wire because they're meaningful to the
/// server immediately (login prompts, menu navigation) and aren't part
/// of a "line" anyone is composing.
/// </para>
/// <para>
/// <see cref="CharacterMode"/> suspends the line-mode model for the
/// duration of MajorMUD's full-screen forms (character creation /
/// <c>train stats</c>), which want character-at-a-time input with
/// server echo. While it's set the TerminalControl bypasses the buffer
/// entirely — every keystroke goes straight to the wire and the
/// local-echo overlay is suppressed so the server's own form rendering
/// is the only thing on screen.
/// </para>
/// </remarks>
public sealed class LocalInputBuffer
{
    /// <summary>
    /// MajorMUD's wire-level line cap. Keystrokes past this point are
    /// silently dropped — the buffer reaches steady state at 254 chars
    /// and waits for Enter. Surfaced as a property so the
    /// TerminalControl overlay can paint a "buffer full" indicator at
    /// the boundary.
    /// </summary>
    public const int MaxLength = 254;

    private readonly StringBuilder _buf = new(MaxLength);
    private bool _characterMode;

    /// <summary>
    /// When true, line-mode is suspended: callers send each keystroke
    /// straight to the wire and skip the local-echo overlay. Flipped on
    /// entry to / exit from MajorMUD's full-screen forms (trainer stats,
    /// character creation) via <see cref="FujinTerm.Game.TrainerMenuTracker"/>.
    /// Setting it to true also drops any in-progress buffered text so a
    /// stale overlay can't linger behind the server's form. Re-raising
    /// <see cref="Changed"/> lets the control repaint immediately.
    /// </summary>
    public bool CharacterMode
    {
        get => _characterMode;
        set
        {
            if (_characterMode == value) return;
            _characterMode = value;
            if (value) _buf.Length = 0;
            Changed?.Invoke();
        }
    }

    /// <summary>Current buffered text (the chars between the last Enter and the user's caret).</summary>
    public string Text => _buf.ToString();

    /// <summary>Length of <see cref="Text"/> — equivalent to <c>Text.Length</c> without the allocation.</summary>
    public int Length => _buf.Length;

    /// <summary>True when the buffer is at <see cref="MaxLength"/> — further appends are dropped.</summary>
    public bool IsFull => _buf.Length >= MaxLength;

    /// <summary>Fires after every state-changing op (Append / Backspace / Flush / Clear) so the terminal can repaint.</summary>
    public event Action? Changed;

    /// <summary>
    /// Append <paramref name="text"/> to the buffer, clamped to the
    /// remaining capacity. Returns the number of chars actually
    /// appended so callers can detect drop-on-overflow. Empty / null
    /// input is a no-op (returns 0, no event fire).
    /// </summary>
    public int Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int remaining = MaxLength - _buf.Length;
        if (remaining <= 0) return 0;
        int take = Math.Min(remaining, text.Length);
        _buf.Append(text, 0, take);
        Changed?.Invoke();
        return take;
    }

    /// <summary>
    /// Remove the last character from the buffer. No-op + no event
    /// when already empty (caller can fall through to sending a real
    /// backspace byte to the server in that case if they want).
    /// </summary>
    /// <returns>True when a char was removed; false when buffer was empty.</returns>
    public bool Backspace()
    {
        if (_buf.Length == 0) return false;
        _buf.Length--;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Replace the whole buffer with <paramref name="text"/> (clamped to
    /// <see cref="MaxLength"/>), then repaint. Used by command-history
    /// recall: Up / Down swap the live line for a previously-sent command
    /// wholesale rather than character by character. Always fires
    /// <see cref="Changed"/> so recalling an empty line still erases the
    /// previously-shown text from the overlay.
    /// </summary>
    public void Set(string? text)
    {
        _buf.Length = 0;
        if (!string.IsNullOrEmpty(text))
            _buf.Append(text, 0, Math.Min(MaxLength, text.Length));
        Changed?.Invoke();
    }

    /// <summary>
    /// Drop all buffered chars without producing wire bytes. Used by
    /// connection swaps + tests; the Enter path uses
    /// <see cref="FlushBytes"/> instead.
    /// </summary>
    public void Clear()
    {
        if (_buf.Length == 0) return;
        _buf.Length = 0;
        Changed?.Invoke();
    }

    /// <summary>
    /// Encode the buffered text as Latin-1 + a trailing CR (the MUD
    /// wire convention), clear the buffer, and return the bytes.
    /// Always returns a CR-only byte (<c>0x0D</c>) when the buffer was
    /// empty — pressing Enter on a blank line is itself a meaningful
    /// command at most MUD prompts (refresh / "no-op move on").
    /// </summary>
    public byte[] FlushBytes()
    {
        if (_buf.Length == 0)
        {
            // Empty Enter — still send the CR.
            return new byte[] { 0x0D };
        }
        // Latin-1 = same single-byte-per-char encoding the rest of
        // the codebase uses for BBS output.
        byte[] payload = new byte[_buf.Length + 1];
        for (int i = 0; i < _buf.Length; i++)
        {
            // Drop down to byte. Latin-1 covers BBS-relevant code
            // points (CP437 was the assumed MUD-era charset); chars
            // above U+00FF wouldn't have a clean BBS rendering anyway
            // and get clamped to the low byte.
            payload[i] = (byte)(_buf[i] & 0xFF);
        }
        payload[^1] = 0x0D;
        _buf.Length = 0;
        Changed?.Invoke();
        return payload;
    }
}
