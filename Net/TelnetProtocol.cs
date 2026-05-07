namespace FujinTerm.Net;

// Constants and helpers for the Telnet wire protocol (RFC 854 + friends).
//
// Telnet runs over a normal TCP byte stream but reserves byte 255 (IAC,
// "Interpret As Command") as an escape: anything following it is a control
// command — option negotiation, sub-negotiation, etc. — not display data.
internal static class TelnetProtocol
{
    // --- Core command bytes ----------------------------------------------
    public const byte IAC  = 255; // Interpret-As-Command escape byte.
    public const byte DONT = 254; // "Don't do option X."   (peer demands)
    public const byte DO   = 253; // "Please do option X."  (peer requests)
    public const byte WONT = 252; // "I won't do option X." (peer reply)
    public const byte WILL = 251; // "I will do option X."  (peer offers)
    public const byte SB   = 250; // Begin sub-negotiation block.
    public const byte SE   = 240; // End sub-negotiation block.

    // --- Options we care about ------------------------------------------
    public const byte OptBinary    = 0;   // Raw 8-bit channel (no NVT translation).
    public const byte OptEcho      = 1;   // Server echoes user input.
    public const byte OptSupGa     = 3;   // Suppress Go-Ahead — assume full-duplex.
    public const byte OptTermType  = 24;  // Send terminal type string (e.g. "ansi-bbs").
    public const byte OptNaws      = 31;  // Negotiate About Window Size.

    // --- TERMINAL-TYPE sub-negotiation sub-codes ------------------------
    public const byte SubTermIs    = 0;   // "My terminal IS ..." (we send this).
    public const byte SubTermSend  = 1;   // "SEND me your terminal type." (server asks).

    // Translates a peer command into the matching positive reply.
    // Negotiation rule: if peer says WILL, you reply DO; if DO, reply WILL,
    // and so on — keeping the offerer/asker direction.
    public static byte Ack(byte cmd) => cmd switch
    {
        DO   => WILL,
        DONT => WONT,
        WILL => DO,
        WONT => DONT,
        _ => 0,
    };

    // Translates a peer command into the matching refusal.
    public static byte Nak(byte cmd) => cmd switch
    {
        DO   => WONT,
        DONT => WILL,
        WILL => DONT,
        WONT => DO,
        _ => 0,
    };
}
