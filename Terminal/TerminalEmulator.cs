using System.Collections.Generic;
using System.Text;

namespace FujinTerm.Terminal;

/// <summary>
/// A small ANSI / VT100-ish terminal emulator. Bytes received from the
/// network are fed in via <see cref="Feed"/>; the emulator interprets the
/// stream — printable characters, C0 controls, and CSI/ESC sequences —
/// and updates a <see cref="TerminalScreen"/> accordingly.
///
/// Sequences that require a reply back to the host (Device Status Report,
/// Device Attributes) are emitted via <see cref="ResponseReady"/>; the
/// shell hooks that to the Telnet client. Any state change to the screen
/// fires <see cref="ScreenUpdated"/> after the input buffer is drained,
/// so the UI can repaint at most once per chunk.
/// </summary>
public sealed class TerminalEmulator
{
    public TerminalScreen Screen { get; }

    /// <summary>Bytes the host expects in reply (DSR, DA responses).</summary>
    public event Action<byte[]>? ResponseReady;

    /// <summary>Fires once per Feed() call after all bytes are processed.</summary>
    public event Action? ScreenUpdated;

    // Current SGR state (foreground/background/flags) used for new writes.
    private CellAttributes _attr = CellAttributes.Default;

    // ESC 7 / DECSC saves these; ESC 8 / DECRC restores them.
    private int _savedCx, _savedCy;
    private CellAttributes _savedAttr = CellAttributes.Default;

    // VT-style "delayed wrap": writing the last column on a line does NOT
    // immediately move the cursor; the next printable character is what
    // wraps. This matches xterm and BBS expectations.
    private bool _wrapPending;

    // DECSTBM scroll region (inclusive, 0-based). Defaults span the whole screen.
    private int _scrollTop;
    private int _scrollBottom;

    // DECOM (?6) origin mode: when on, CUP coords are relative to scroll region
    // and the cursor cannot move outside it.
    private bool _originMode;

    // DECAWM (?7) auto-wrap mode: when off, writing past the right margin
    // overwrites the last column instead of advancing to the next row.
    private bool _autoWrap = true;

    // ----- CSI / ESC parser state ---------------------------------------
    private State _state = State.Ground;
    // Numeric parameter list of the current CSI sequence ("1;31" → [1, 31]).
    private readonly List<int> _params = new(8);
    private int _curParam;
    private bool _hasCurParam;
    // True when the current sequence began with "CSI ?" (DEC private mode).
    private bool _privateMode;

    public TerminalEmulator(int cols, int rows)
    {
        Screen = new TerminalScreen(cols, rows);
        _scrollBottom = rows - 1;
    }

    /// <summary>Resize the underlying screen and reset the scroll region.</summary>
    public void Resize(int cols, int rows)
    {
        Screen.Resize(cols, rows);
        _scrollTop = 0;
        _scrollBottom = rows - 1;
    }

    /// <summary>Process a chunk of incoming bytes from the host.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes) FeedByte(b);
        Screen.Bump();
        ScreenUpdated?.Invoke();
    }

    // The four states the parser can be in. Most bytes are processed in
    // Ground; Esc/Csi handle escape sequences; OscString swallows OSC
    // strings (we don't act on any of them); Charset eats the SCS designator
    // byte after ESC ( / ) / * / +.
    private enum State { Ground, Esc, Csi, OscString, Charset }

    private void FeedByte(byte b)
    {
        switch (_state)
        {
            case State.Ground:
                ProcessGround(b);
                break;
            case State.Esc:
                ProcessEsc(b);
                break;
            case State.Csi:
                ProcessCsi(b);
                break;
            case State.OscString:
                // OSC ends with BEL (0x07) or ST (ESC \). We just consume.
                if (b == 0x07) _state = State.Ground;
                else if (b == 0x1B) _state = State.Esc;
                break;
            case State.Charset:
                // Only one designator byte follows the SCS introducer; drop it.
                _state = State.Ground;
                break;
        }
    }

    /// <summary>Handle a byte while we're not inside any escape sequence.</summary>
    private void ProcessGround(byte b)
    {
        // C0 controls first — these are common enough that the switch cost
        // is well worth it.
        switch (b)
        {
            case 0x07: return;            // BEL — we don't beep visually.
            case 0x08:                     // BS  — backspace one column (no erase).
                if (Screen.CursorX > 0) Screen.CursorX--;
                _wrapPending = false;
                return;
            case 0x09:                     // HT  — advance to the next 8-col tab stop.
                int next = (Screen.CursorX / 8 + 1) * 8;
                Screen.CursorX = Math.Min(next, Screen.Cols - 1);
                _wrapPending = false;
                return;
            case 0x0A:                     // LF
            case 0x0B:                     // VT  — treated as LF.
            case 0x0C:                     // FF  — treated as LF.
                LineFeed();
                _wrapPending = false;
                return;
            case 0x0D:                     // CR  — column 0, no row change.
                Screen.CursorX = 0;
                _wrapPending = false;
                return;
            case 0x1B:                     // ESC — start an escape sequence.
                BeginEsc();
                return;
            case 0x00:
                return;                    // NUL — ignore (used as filler by some BBSes).
        }

        // A printable byte: if we deferred wrapping from the previous cell,
        // commit that now before placing this glyph.
        if (_wrapPending)
        {
            if (_autoWrap)
            {
                Screen.CursorX = 0;
                LineFeed();
            }
            _wrapPending = false;
        }

        // Translate raw 8-bit byte through CP437 to a Unicode glyph and
        // place it under the cursor with the current attributes.
        char ch = Cp437.ToUnicode(b);
        Screen.Put(Screen.CursorX, Screen.CursorY, new Cell(ch, _attr));

        // Advance, or flag that the next char needs to wrap.
        if (Screen.CursorX + 1 >= Screen.Cols)
            _wrapPending = true;
        else
            Screen.CursorX++;
    }

    /// <summary>Index-y down a row, scrolling the region if at the bottom.</summary>
    private void LineFeed()
    {
        if (Screen.CursorY == _scrollBottom)
        {
            // ScrollUp captures the top row into Scrollback before
            // overwriting it.
            Screen.ScrollUp(_scrollTop, _scrollBottom, 1, _attr);
        }
        else if (Screen.CursorY + 1 < Screen.Rows)
        {
            // Capture the row we're leaving behind so the Backscroll window
            // is a true chronological transcript — without this, only rows
            // that scroll off the top of the screen ever land in history,
            // and a partial screen of output the user just read wouldn't
            // show up there. LF is the canonical "this line is done"
            // signal in the protocol; capture matches it 1:1.
            Screen.Scrollback.Append(Screen.Row(Screen.CursorY));
            Screen.CursorY++;
        }
    }

    private void BeginEsc()
    {
        _state = State.Esc;
        _params.Clear();
        _curParam = 0;
        _hasCurParam = false;
        _privateMode = false;
    }

    /// <summary>Handle a byte right after an ESC.</summary>
    private void ProcessEsc(byte b)
    {
        switch (b)
        {
            case (byte)'[':                    // Start of a CSI sequence.
                _state = State.Csi;
                return;
            case (byte)']':                    // OSC string (we just discard).
                _state = State.OscString;
                return;
            case (byte)'c':                    // RIS — full terminal reset.
                ResetTerminal();
                _state = State.Ground;
                return;
            case (byte)'7':                    // DECSC — save cursor + attrs.
                SaveCursor();
                _state = State.Ground;
                return;
            case (byte)'8':                    // DECRC — restore cursor + attrs.
                RestoreCursor();
                _state = State.Ground;
                return;
            case (byte)'D':                    // IND — index down (LF, no CR).
                LineFeed();
                _state = State.Ground;
                return;
            case (byte)'M':                    // RI  — reverse index (cursor up, scroll down at top).
                if (Screen.CursorY == _scrollTop)
                    Screen.ScrollDown(_scrollTop, _scrollBottom, 1, _attr);
                else if (Screen.CursorY > 0)
                    Screen.CursorY--;
                _state = State.Ground;
                return;
            case (byte)'E':                    // NEL — newline (CR + LF).
                Screen.CursorX = 0;
                LineFeed();
                _state = State.Ground;
                return;
            case (byte)'(':
            case (byte)')':
            case (byte)'*':
            case (byte)'+':
                // SCS: select character set into G0/G1/G2/G3. Consume one
                // designator byte and discard (we render through CP437).
                _state = State.Charset;
                return;
        }
        // Unknown ESC X — drop the sequence and resume normal parsing.
        _state = State.Ground;
    }

    /// <summary>Handle a byte while inside a CSI sequence (after ESC [).</summary>
    private void ProcessCsi(byte b)
    {
        // A leading '?' marks a DEC private-mode sequence (e.g. ?25h).
        if (b == (byte)'?' && _params.Count == 0 && !_hasCurParam)
        {
            _privateMode = true;
            return;
        }
        // Numeric digit: accumulate into the current parameter.
        if (b >= 0x30 && b <= 0x39)
        {
            _curParam = _curParam * 10 + (b - 0x30);
            _hasCurParam = true;
            return;
        }
        // Parameter separator.
        if (b == (byte)';')
        {
            _params.Add(_hasCurParam ? _curParam : 0);
            _curParam = 0;
            _hasCurParam = false;
            return;
        }
        // Final byte (uppercase or lowercase letter range): execute.
        if (b >= 0x40 && b <= 0x7E)
        {
            if (_hasCurParam) _params.Add(_curParam);
            DispatchCsi((char)b);
            _state = State.Ground;
            return;
        }
        // Intermediates ('!' to '/') and other bytes — stay in CSI and ignore.
    }

    /// <summary>Read parameter <paramref name="index"/>, treating 0/missing as default.</summary>
    private int Param(int index, int defaultValue) =>
        index < _params.Count && _params[index] != 0 ? _params[index] : defaultValue;

    /// <summary>Read parameter <paramref name="index"/> verbatim (0 stays 0).</summary>
    private int ParamRaw(int index, int defaultValue) =>
        index < _params.Count ? _params[index] : defaultValue;

    /// <summary>Run the side-effect of a completed CSI sequence.</summary>
    private void DispatchCsi(char final)
    {
        switch (final)
        {
            // Cursor movement: A=up, B=down, C=right, D=left, E=next line, F=prev line.
            case 'A': MoveRel(0, -Param(0, 1)); break;
            case 'B': MoveRel(0, +Param(0, 1)); break;
            case 'C': MoveRel(+Param(0, 1), 0); break;
            case 'D': MoveRel(-Param(0, 1), 0); break;
            case 'E':
                Screen.CursorX = 0;
                MoveRel(0, +Param(0, 1));
                break;
            case 'F':
                Screen.CursorX = 0;
                MoveRel(0, -Param(0, 1));
                break;
            // Absolute column (CHA) / absolute row (VPA).
            case 'G':
                Screen.CursorX = Math.Clamp(Param(0, 1) - 1, 0, Screen.Cols - 1);
                _wrapPending = false;
                break;
            case 'd':
                Screen.CursorY = Math.Clamp(Param(0, 1) - 1, 0, Screen.Rows - 1);
                _wrapPending = false;
                break;
            // Cursor position (CUP / HVP). Coordinates are 1-based on the wire.
            case 'H':
            case 'f':
                {
                    int row = Param(0, 1) - 1;
                    int col = Param(1, 1) - 1;
                    if (_originMode)
                    {
                        // Origin mode: row is relative to the scroll region.
                        row = Math.Clamp(_scrollTop + row, _scrollTop, _scrollBottom);
                    }
                    else
                    {
                        row = Math.Clamp(row, 0, Screen.Rows - 1);
                    }
                    Screen.CursorY = row;
                    Screen.CursorX = Math.Clamp(col, 0, Screen.Cols - 1);
                    _wrapPending = false;
                }
                break;
            case 'J': EraseInDisplay(ParamRaw(0, 0)); break;
            case 'K': EraseInLine(ParamRaw(0, 0)); break;
            case 'S': Screen.ScrollUp(_scrollTop, _scrollBottom, Param(0, 1), _attr); break;
            case 'T': Screen.ScrollDown(_scrollTop, _scrollBottom, Param(0, 1), _attr); break;
            // Insert / delete blank lines at cursor (within scroll region).
            case 'L':
                if (Screen.CursorY >= _scrollTop && Screen.CursorY <= _scrollBottom)
                    Screen.ScrollDown(Screen.CursorY, _scrollBottom, Param(0, 1), _attr);
                break;
            case 'M':
                if (Screen.CursorY >= _scrollTop && Screen.CursorY <= _scrollBottom)
                    Screen.ScrollUp(Screen.CursorY, _scrollBottom, Param(0, 1), _attr);
                break;
            // DECSTBM — set top/bottom margins of the scroll region.
            case 'r':
                {
                    int top = Param(0, 1) - 1;
                    int bottom = ParamRaw(1, Screen.Rows) - 1;
                    if (bottom <= 0) bottom = Screen.Rows - 1;
                    top = Math.Clamp(top, 0, Screen.Rows - 1);
                    bottom = Math.Clamp(bottom, top, Screen.Rows - 1);
                    if (bottom > top)
                    {
                        _scrollTop = top;
                        _scrollBottom = bottom;
                        // DECSTBM: cursor moves to home (or origin home in DECOM).
                        Screen.CursorY = _originMode ? _scrollTop : 0;
                        Screen.CursorX = 0;
                        _wrapPending = false;
                    }
                }
                break;
            case 'P': DeleteChars(Param(0, 1)); break;          // DCH
            case '@': InsertChars(Param(0, 1)); break;          // ICH
            case 'X': EraseChars(Param(0, 1)); break;           // ECH
            case 'm': ApplySgr(); break;                        // SGR — colors / styles
            case 's': SaveCursor(); break;                      // SCO save cursor
            case 'u': RestoreCursor(); break;                   // SCO restore cursor
            case 'h': SetMode(true); break;                     // DEC private mode set
            case 'l': SetMode(false); break;                    // DEC private mode reset
            case 'n': DeviceStatusReport(); break;              // DSR
            case 'c': SendDeviceAttributes(); break;            // DA
        }
    }

    /// <summary>Move the cursor by a (dx, dy) offset, clamped to the screen.</summary>
    private void MoveRel(int dx, int dy)
    {
        Screen.CursorX = Math.Clamp(Screen.CursorX + dx, 0, Screen.Cols - 1);
        Screen.CursorY = Math.Clamp(Screen.CursorY + dy, 0, Screen.Rows - 1);
        _wrapPending = false;
    }

    /// <summary>CSI J — erase parts of the display.</summary>
    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:  // From cursor to end of screen.
                Screen.ClearRow(Screen.CursorY, Screen.CursorX, Screen.Cols - 1, _attr);
                if (Screen.CursorY + 1 <= Screen.Rows - 1)
                    Screen.ClearRowsInclusive(Screen.CursorY + 1, Screen.Rows - 1, _attr);
                break;
            case 1:  // From start of screen to cursor.
                if (Screen.CursorY > 0)
                    Screen.ClearRowsInclusive(0, Screen.CursorY - 1, _attr);
                Screen.ClearRow(Screen.CursorY, 0, Screen.CursorX, _attr);
                break;
            case 2:
            case 3:  // Whole screen (3 also clears scrollback in real terms).
                Screen.ClearAll(_attr);
                // ANSI.SYS / SyncTERM behavior: ED 2 also homes the cursor.
                // BBS games (e.g. MajorMUD) rely on this — they emit \x1b[2J
                // and immediately start drawing absolute-positioned art with
                // no follow-up cursor-home sequence.
                Screen.CursorX = 0;
                Screen.CursorY = _originMode ? _scrollTop : 0;
                _wrapPending = false;
                break;
        }
    }

    /// <summary>CSI K — erase parts of the current line.</summary>
    private void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0: Screen.ClearRow(Screen.CursorY, Screen.CursorX, Screen.Cols - 1, _attr); break; // to end
            case 1: Screen.ClearRow(Screen.CursorY, 0, Screen.CursorX, _attr); break;               // from start
            case 2: Screen.ClearRow(Screen.CursorY, 0, Screen.Cols - 1, _attr); break;              // whole line
        }
    }

    /// <summary>CSI P — delete <paramref name="n"/> chars at cursor, shifting the rest left.</summary>
    private void DeleteChars(int n)
    {
        n = Math.Clamp(n, 1, Screen.Cols - Screen.CursorX);
        int y = Screen.CursorY;
        for (int x = Screen.CursorX; x < Screen.Cols; x++)
        {
            int src = x + n;
            var c = src < Screen.Cols ? Screen[src, y] : new Cell(' ', _attr);
            Screen.Put(x, y, c);
        }
    }

    /// <summary>CSI @ — insert <paramref name="n"/> blank cells at cursor, shifting right.</summary>
    private void InsertChars(int n)
    {
        n = Math.Clamp(n, 1, Screen.Cols - Screen.CursorX);
        int y = Screen.CursorY;
        for (int x = Screen.Cols - 1; x >= Screen.CursorX + n; x--)
            Screen.Put(x, y, Screen[x - n, y]);
        for (int x = Screen.CursorX; x < Screen.CursorX + n; x++)
            Screen.Put(x, y, new Cell(' ', _attr));
    }

    /// <summary>CSI X — erase <paramref name="n"/> cells in place (no shift).</summary>
    private void EraseChars(int n)
    {
        n = Math.Clamp(n, 1, Screen.Cols - Screen.CursorX);
        for (int x = Screen.CursorX; x < Screen.CursorX + n; x++)
            Screen.Put(x, Screen.CursorY, new Cell(' ', _attr));
    }

    /// <summary>
    /// CSI m — Select Graphic Rendition. Walks the parameter list and
    /// accumulates style/color changes onto <see cref="_attr"/>.
    /// </summary>
    private void ApplySgr()
    {
        if (_params.Count == 0) { _attr = CellAttributes.Default; return; }
        for (int i = 0; i < _params.Count; i++)
        {
            int p = _params[i];
            switch (p)
            {
                case 0: _attr = CellAttributes.Default; break;                   // reset
                case 1: _attr = _attr.WithFlag(CellFlags.Bold, true); break;     // bold / bright
                case 2: _attr = _attr.WithFlag(CellFlags.Bold, false); break;    // dim — treat as not-bold
                case 4: _attr = _attr.WithFlag(CellFlags.Underline, true); break;
                case 5:
                case 6: _attr = _attr.WithFlag(CellFlags.Blink, true); break;    // slow / rapid blink
                case 7: _attr = _attr.WithFlag(CellFlags.Reverse, true); break;
                case 8: _attr = _attr.WithFlag(CellFlags.Concealed, true); break;
                case 21:
                case 22: _attr = _attr.WithFlag(CellFlags.Bold, false); break;   // clear bold/dim
                case 24: _attr = _attr.WithFlag(CellFlags.Underline, false); break;
                case 25: _attr = _attr.WithFlag(CellFlags.Blink, false); break;
                case 27: _attr = _attr.WithFlag(CellFlags.Reverse, false); break;
                case 28: _attr = _attr.WithFlag(CellFlags.Concealed, false); break;
                // Standard 8 foreground colors.
                case >= 30 and <= 37:
                    _attr = _attr.WithForeground(TerminalColor.Indexed(p - 30));
                    break;
                // 38;5;N (256-color) or 38;2;R;G;B (truecolor).
                case 38:
                    if (TryReadExtendedColor(ref i, out var fg)) _attr = _attr.WithForeground(fg);
                    break;
                case 39: _attr = _attr.WithForeground(TerminalColor.Default); break;
                // Standard 8 background colors.
                case >= 40 and <= 47:
                    _attr = _attr.WithBackground(TerminalColor.Indexed(p - 40));
                    break;
                case 48:
                    if (TryReadExtendedColor(ref i, out var bg)) _attr = _attr.WithBackground(bg);
                    break;
                case 49: _attr = _attr.WithBackground(TerminalColor.Default); break;
                // Bright foreground / background variants.
                case >= 90 and <= 97:
                    _attr = _attr.WithForeground(TerminalColor.Indexed(p - 90 + 8));
                    break;
                case >= 100 and <= 107:
                    _attr = _attr.WithBackground(TerminalColor.Indexed(p - 100 + 8));
                    break;
            }
        }
    }

    /// <summary>
    /// Decode the tail of a 38;... or 48;... SGR sequence. Returns true on
    /// success and advances <paramref name="i"/> past the consumed sub-args.
    /// </summary>
    private bool TryReadExtendedColor(ref int i, out TerminalColor color)
    {
        color = TerminalColor.Default;
        if (i + 1 >= _params.Count) return false;
        int kind = _params[i + 1];
        if (kind == 5)        // 256-color: ;5;<n>
        {
            if (i + 2 >= _params.Count) { i = _params.Count; return false; }
            int idx = _params[i + 2] & 0xFF;
            color = TerminalColor.Indexed(idx);
            i += 2;
            return true;
        }
        if (kind == 2)        // truecolor: ;2;<r>;<g>;<b>
        {
            if (i + 4 >= _params.Count) { i = _params.Count; return false; }
            byte r = (byte)(_params[i + 2] & 0xFF);
            byte g = (byte)(_params[i + 3] & 0xFF);
            byte b = (byte)(_params[i + 4] & 0xFF);
            color = TerminalColor.Rgb(r, g, b);
            i += 4;
            return true;
        }
        return false;
    }

    private void SaveCursor()
    {
        _savedCx = Screen.CursorX;
        _savedCy = Screen.CursorY;
        _savedAttr = _attr;
    }

    private void RestoreCursor()
    {
        Screen.CursorX = _savedCx;
        Screen.CursorY = _savedCy;
        _attr = _savedAttr;
        _wrapPending = false;
    }

    /// <summary>CSI ? Pn h/l — DEC private mode set/reset.</summary>
    private void SetMode(bool on)
    {
        if (!_privateMode) return;
        foreach (var p in _params)
        {
            switch (p)
            {
                case 6:  // DECOM — origin mode.
                    _originMode = on;
                    Screen.CursorY = on ? _scrollTop : 0;
                    Screen.CursorX = 0;
                    _wrapPending = false;
                    break;
                case 7:  // DECAWM — auto-wrap.
                    _autoWrap = on;
                    _wrapPending = false;
                    break;
                case 25: // DECTCEM — cursor visibility.
                    Screen.CursorVisible = on;
                    break;
            }
        }
    }

    /// <summary>CSI Pn n — Device Status Report.</summary>
    private void DeviceStatusReport()
    {
        if (_params.Count == 0) return;
        switch (_params[0])
        {
            case 5:  // "Are you OK?" → reply "yes".
                ResponseReady?.Invoke(Encoding.ASCII.GetBytes("\x1B[0n"));
                break;
            case 6:  // "Where's the cursor?" → reply with row;col.
                var msg = $"\x1B[{Screen.CursorY + 1};{Screen.CursorX + 1}R";
                ResponseReady?.Invoke(Encoding.ASCII.GetBytes(msg));
                break;
        }
    }

    /// <summary>CSI c — identify ourselves as a VT101.</summary>
    private void SendDeviceAttributes()
    {
        ResponseReady?.Invoke(Encoding.ASCII.GetBytes("\x1B[?1;0c"));
    }

    /// <summary>ESC c — full reset to power-on state.</summary>
    private void ResetTerminal()
    {
        _attr = CellAttributes.Default;
        Screen.CursorX = 0;
        Screen.CursorY = 0;
        Screen.CursorVisible = true;
        Screen.ClearAll(_attr);
        _wrapPending = false;
        _scrollTop = 0;
        _scrollBottom = Screen.Rows - 1;
        _originMode = false;
        _autoWrap = true;
    }
}
