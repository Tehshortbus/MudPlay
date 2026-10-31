namespace MudPlay.Services;

// A connection that ended without anyone asking for it: the server closed it, or
// the socket died under us. The read loop ending is the only sign of it, so unless
// the client says so in the terminal the screen keeps the last prompt (often a
// round box) and looks like a client waiting on the game (report
// paradigm-20261022-074654). Kept for the bug report's "Last link drop" line.
public sealed record ConnectionLoss(
    DateTimeOffset At,
    string Host,
    int Port,
    bool WentSilent,
    TimeSpan? SilentFor,
    string? EndedBecause)
{
    // What became of the redial: armed, or why not. Set once the reconnect
    // decision has been made, after the loss itself is announced.
    public string Reconnect { get; init; } = "not decided";

    // A drop counts as the server going silent when keepalive was on for the
    // connection and nothing arrived for the idle window plus a 5 s grace, so a
    // server that answered just before the window closed still reads as a lost
    // carrier. A connection that never read a byte went silent from the start.
    public static bool Silent(int idleSeconds, DateTimeOffset lastRead, DateTimeOffset now)
    {
        if (idleSeconds <= 0) return false;
        if (lastRead == DateTimeOffset.MinValue) return true;
        return (now - lastRead).TotalSeconds >= idleSeconds + 5;
    }

    // Whether something that gave up on the game had in fact lost the link: nothing
    // was read from the server after the last command sent before it gave up. The
    // game answers every command, so a failure the game caused came after its
    // answer, and one with the answer still owed came of a link already dead (the
    // wifi going minutes before the keepalive counts the drop).
    public static bool LeftUnanswered(DateTimeOffset lastRead, DateTimeOffset sentBeforeGivingUp)
        => lastRead < sentBeforeGivingUp;

    public static ConnectionLoss Of(string host, int port, int idleSeconds,
        DateTimeOffset lastRead, string? endedBecause, DateTimeOffset now)
        => new(now, host, port, Silent(idleSeconds, lastRead, now),
            lastRead == DateTimeOffset.MinValue ? null : now - lastRead, endedBecause);

    // The terminal line. The silence is only worth stating when it is long enough
    // to explain why the screen had stood still.
    public string TerminalLine
        => $"[CONNECTION LOST: {Host} {Port}"
         + (SilentFor is { TotalSeconds: >= 5 } quiet ? $" - NOTHING FROM THE SERVER FOR {Seconds(quiet)}" : string.Empty)
         + "]";

    // The Settings → BBS + Display tick box that would have redialed this drop,
    // named as the tab labels it.
    public string Trigger => WentSilent ? "Server stops responding" : "Carrier is lost mid-session";

    // Said in the terminal when the matching tick box is off, so the drop isn't
    // left looking like a client still waiting for the game.
    public string NotReconnectingLine
        => $"[NOT RECONNECTING: \"{Trigger.ToUpperInvariant()}\" IS OFF UNDER SETTINGS > BBS + DISPLAY. PRESS CONNECT TO DIAL BACK IN.]";

    public string LogLine
        => $"Connection to {Host}:{Port} lost — "
         + (WentSilent ? "no response" : "carrier lost")
         + (SilentFor is { } quiet ? $"; nothing from the server for {Seconds(quiet)} before it" : "; nothing was ever read")
         + $"; the read ended: {EndedBecause ?? "(not recorded)"}";

    public string Summary(DateTimeOffset now)
        => $"{At.ToLocalTime():HH:mm:ss} ({BugReportHoldsAndWaits.Age(now - At)} ago), "
         + (WentSilent ? "no response" : "carrier lost")
         + (SilentFor is { } quiet ? $"; nothing from the server for {Seconds(quiet)} before it" : string.Empty)
         + $"; the read ended: {EndedBecause ?? "(not recorded)"}; reconnect: {Reconnect}";

    private static string Seconds(TimeSpan span) => $"{(int)span.TotalSeconds}s";
}
