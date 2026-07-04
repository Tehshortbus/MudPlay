using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static FujinTerm.Net.TelnetProtocol;

namespace FujinTerm.Net;

// A minimal Telnet client tailored for connecting to BBSes.
//
// Responsibilities:
//   • Open a TCP connection to host:port.
//   • Continuously read bytes; strip out Telnet IAC commands; surface the
//     remaining "display data" bytes via the DataReceived event.
//   • Auto-negotiate a small set of options (BINARY, ECHO, SUP-GA,
//     TERM-TYPE, NAWS) the way a typical terminal client would.
//   • Forward user keystrokes back to the server (with IAC byte escaping).
public sealed class TelnetClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    // Per-option negotiation state. A byte slot per option is enough because
    // options are 0–255 and we only remember the *last* settled command.
    //   _localOpt[opt]  == DO / DONT  → peer's instruction about *us*.
    //   _remoteOpt[opt] == WILL / WONT → peer's claim about *itself*.
    private readonly byte[] _localOpt = new byte[256];
    private readonly byte[] _remoteOpt = new byte[256];

    // Sub-negotiation accumulator (used when we see IAC SB ... IAC SE).
    private InterpretState _state;
    private int _sbLen;
    private readonly byte[] _sbBuf = new byte[256];

    // Terminal type string sent in response to TERM-TYPE SEND.
    public string TerminalType { get; set; } = "ansi-bbs";

    // Window size advertised through NAWS (columns).
    public int Cols { get; set; } = 80;

    // Window size advertised through NAWS (rows).
    public int Rows { get; set; } = 25;

    // Seconds of socket idle before the OS starts probing the connection with
    // TCP keepalive packets. 0 disables keepalive — the OS default (~2 hours
    // on Linux) is too long for a BBS. Set before ConnectAsync; applied to the
    // live socket once the TCP connection is established. Hardcoded interval /
    // retry-count give a ~30 s tail (3 probes 10 s apart) before the socket
    // dies.
    public int NoResponseTimeoutSeconds { get; set; }

    // Wall-clock timestamp of the most recent read that returned bytes.
    // DateTimeOffset.MinValue if no data has arrived yet this session. The
    // connection-lifecycle code in MainWindowViewModel reads this on
    // Disconnected to distinguish a server-side carrier drop from a keepalive
    // timeout — long quiet stretch + dead socket = no response.
    public DateTimeOffset LastDataReceived { get; private set; } = DateTimeOffset.MinValue;


    public bool IsConnected => _tcp.Connected && _stream is not null;

    // Raised on the read-loop thread when display bytes arrive.
    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action? Connected;
    public event Action? Disconnected;
    // Diagnostic log line (negotiation traffic, errors).
    public event Action<string>? Log;

    // Open the TCP socket and start the background read loop.
    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        ApplyKeepAlive();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        Connected?.Invoke();
    }

    // Configure TCP-level keepalive on the connected socket, gated on
    // NoResponseTimeoutSeconds. Cross-platform via .NET's
    // SocketOptionName.TcpKeepAlive* (mapped to TCP_KEEPIDLE / INTVL / CNT on
    // Linux, the equivalents on Windows + macOS). Best-effort: any platform
    // that doesn't support a given option throws, which we swallow —
    // keepalive is a defense, not a correctness requirement.
    private void ApplyKeepAlive()
    {
        int idle = NoResponseTimeoutSeconds;
        if (idle <= 0) return;

        Socket sock = _tcp.Client;
        try { sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
        try { sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, idle); } catch { }
        try { sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10); } catch { }
        try { sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3); } catch { }
    }

    // Send raw bytes to the server. If the payload contains any IAC (255)
    // bytes they are doubled — the protocol's escape rule — so the server
    // reads them as data instead of command escapes.
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_stream is null) return;
        if (data.Span.IndexOf(IAC) < 0)
        {
            // Fast path: no escaping needed.
            await _stream.WriteAsync(data, ct);
            return;
        }
        // Slow path: copy through, doubling every IAC.
        var buf = new byte[data.Length * 2];
        int o = 0;
        foreach (var b in data.Span)
        {
            buf[o++] = b;
            if (b == IAC) buf[o++] = IAC;
        }
        await _stream.WriteAsync(buf.AsMemory(0, o), ct);
    }

    // Convenience: send a string as Latin-1 (8-bit "ANSI") bytes.
    public Task SendTextAsync(string text, CancellationToken ct = default)
        => SendAsync(Encoding.Latin1.GetBytes(text), ct);

    // Cancel the read loop, close the socket.
    public async Task DisconnectAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _tcp.Close(); } catch { }
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts?.Dispose();
        _tcp.Dispose();
    }

    // Tell the server about a (new) window size via NAWS. Only sent if NAWS
    // has actually been negotiated — otherwise the server isn't expecting it.
    public async Task SendWindowSizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        Cols = cols;
        Rows = rows;
        if (_localOpt[OptNaws] != DO || _stream is null) return;

        // NAWS payload: 16-bit big-endian width then height. IACs in the
        // payload (e.g. width == 255) must be escaped per protocol rules.
        var raw = new byte[]
        {
            (byte)((cols >> 8) & 0xff), (byte)(cols & 0xff),
            (byte)((rows >> 8) & 0xff), (byte)(rows & 0xff),
        };
        var buf = new byte[16];
        int p = 0;
        buf[p++] = IAC; buf[p++] = SB; buf[p++] = OptNaws;
        foreach (var r in raw)
        {
            buf[p++] = r;
            if (r == IAC) buf[p++] = IAC;
        }
        buf[p++] = IAC; buf[p++] = SE;
        await _stream.WriteAsync(buf.AsMemory(0, p), ct);
    }

    // ----- Read loop -----------------------------------------------------

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var inBuf = new byte[16 * 1024];
        var outBuf = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                int n = await _stream.ReadAsync(inBuf.AsMemory(0, inBuf.Length), ct);
                if (n == 0) break; // Server closed the connection.

                LastDataReceived = DateTimeOffset.UtcNow;

                // Filter out telnet command bytes; outBuf gets only display data.
                int outLen = Interpret(inBuf.AsSpan(0, n), outBuf);
                if (outLen > 0)
                {
                    DataReceived?.Invoke(new ReadOnlyMemory<byte>(outBuf, 0, outLen));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    // ----- IAC state machine --------------------------------------------

    // Tracks where we are in the IAC parser. Each incoming byte transitions
    // between these states.
    private enum InterpretState { Normal, GotIac, GotCmd, InSb, GotIacInSb }

    private byte _pendingCmd;

    // Walk the input bytes through the Telnet command-stripping state machine,
    // copying display bytes to output and dispatching DO/DONT/WILL/WONT and
    // SB/SE blocks as they're found. Returns the number of display bytes
    // written.
    private int Interpret(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int outLen = 0;
        foreach (byte b in input)
        {
            switch (_state)
            {
                case InterpretState.Normal:
                    if (b == IAC) _state = InterpretState.GotIac;
                    else output[outLen++] = b;
                    break;

                case InterpretState.GotIac:
                    // IAC IAC = literal 0xFF byte in the data stream.
                    if (b == IAC) { output[outLen++] = IAC; _state = InterpretState.Normal; }
                    else if (b == SB) { _sbLen = 0; _state = InterpretState.InSb; }
                    else if (b is DO or DONT or WILL or WONT) { _pendingCmd = b; _state = InterpretState.GotCmd; }
                    else { _state = InterpretState.Normal; } // ignore unknown IAC <X>
                    break;

                case InterpretState.GotCmd:
                    HandleNegotiation(_pendingCmd, b);
                    _state = InterpretState.Normal;
                    break;

                case InterpretState.InSb:
                    // Bytes inside SB ... SE accumulate into _sbBuf until an IAC SE.
                    if (b == IAC) _state = InterpretState.GotIacInSb;
                    else if (_sbLen < _sbBuf.Length) _sbBuf[_sbLen++] = b;
                    break;

                case InterpretState.GotIacInSb:
                    if (b == IAC) { if (_sbLen < _sbBuf.Length) _sbBuf[_sbLen++] = IAC; _state = InterpretState.InSb; }
                    else if (b == SE) { HandleSubNegotiation(); _state = InterpretState.Normal; }
                    else { _state = InterpretState.Normal; } // malformed — drop it
                    break;
            }
        }
        return outLen;
    }

    // React to a DO/DONT/WILL/WONT request. We say yes only to the small
    // option set we actually implement; everything else is politely refused.
    private void HandleNegotiation(byte cmd, byte opt)
    {
        Log?.Invoke($"RX: {CmdName(cmd)} {OptName(opt)}");

        // DO/DONT target *our* behavior; WILL/WONT target the server's.
        bool isLocal = cmd is DO or DONT;
        var table = isLocal ? _localOpt : _remoteOpt;

        // Avoid loops: if the peer is just confirming the current state,
        // don't reply again.
        if (table[opt] == cmd) return;

        // Whitelist: which options will we agree to?
        bool supported = opt switch
        {
            OptBinary or OptTermType or OptSupGa or OptNaws => true,
            // ECHO: we don't echo locally, but it's fine if the *server* echoes.
            OptEcho => !isLocal,
            _ => false,
        };

        if (supported)
        {
            table[opt] = cmd;
            SendCommand(Ack(cmd), opt);
            // Right after agreeing to NAWS, send the initial size.
            if (cmd == DO && opt == OptNaws)
                _ = SendWindowSizeAsync(Cols, Rows);
        }
        else
        {
            // Only refuse active offers; don't reply to a refusal of a
            // refusal (would loop).
            if (cmd is DO or WILL)
                SendCommand(Nak(cmd), opt);
        }
    }

    // Handle a completed IAC SB ... IAC SE block. Today only TERM-TYPE SEND is
    // interesting — we reply with the configured terminal name.
    private void HandleSubNegotiation()
    {
        if (_sbLen < 1) return;
        byte opt = _sbBuf[0];
        if (opt == OptTermType && _sbLen >= 2 && _sbBuf[1] == SubTermSend)
        {
            var name = Encoding.ASCII.GetBytes(TerminalType);
            // Format: IAC SB TERM-TYPE IS <name bytes> IAC SE
            var buf = new byte[6 + name.Length];
            int p = 0;
            buf[p++] = IAC; buf[p++] = SB; buf[p++] = OptTermType; buf[p++] = SubTermIs;
            Array.Copy(name, 0, buf, p, name.Length);
            p += name.Length;
            buf[p++] = IAC; buf[p++] = SE;
            _ = _stream!.WriteAsync(buf.AsMemory(0, p)).AsTask();
            Log?.Invoke($"TX: TermType IS {TerminalType}");
        }
    }

    // Send a single 3-byte IAC <cmd> <opt> negotiation packet.
    private void SendCommand(byte cmd, byte opt)
    {
        if (_stream is null) return;
        var buf = new byte[3] { IAC, cmd, opt };
        _ = _stream.WriteAsync(buf).AsTask();
        Log?.Invoke($"TX: {CmdName(cmd)} {OptName(opt)}");
    }

    // Pretty-printers used only for the Log event.
    private static string CmdName(byte b) => b switch
    {
        DO => "DO", DONT => "DONT", WILL => "WILL", WONT => "WONT",
        SB => "SB", SE => "SE", IAC => "IAC",
        _ => $"0x{b:X2}",
    };

    private static string OptName(byte b) => b switch
    {
        OptBinary => "BINARY",
        OptEcho => "ECHO",
        OptSupGa => "SUP-GA",
        OptTermType => "TERM-TYPE",
        OptNaws => "NAWS",
        _ => $"opt{b}",
    };
}
