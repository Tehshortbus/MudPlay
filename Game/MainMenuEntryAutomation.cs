using System.Text;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// Sends the configured <see cref="GameCommands.EntryCommand"/>
/// (default <c>E</c>) when the MajorMUD main-menu screen is recognised
/// at the tail of the automated BBS-login sequence, then follows it
/// up with a four-step refresh sequence (CR → <c>stat</c> → <c>exp</c>
/// → <c>i</c>) so subscribers like <see cref="StatParser"/> /
/// (future) <see cref="Inventory.InventoryManager"/> seed
/// <see cref="PlayerStats"/> + inventory state right at the moment
/// we enter the realm.
/// </summary>
/// <remarks>
/// <para>
/// Security model: the entry command must NEVER auto-fire on a chat
/// line that happens to look like the main menu (a malicious player
/// could gossip or telepath <c>[E] . Enter the Realm</c> to trick a
/// naive client into auto-entering when the player wanted to stay
/// out-of-realm). To prevent that, the engine is latched closed by
/// default and is only briefly armed when the
/// <see cref="LoginAutomator"/> reports its final step completed.
/// The arm window has a short TTL (<see cref="ArmWindow"/>, default
/// 15 s): if the menu doesn't appear in time, the latch closes and
/// in-game lines can't trip it later.
/// </para>
/// <para>
/// On a typical connect: TelnetClient connects → LoginAutomator walks
/// the menu-nav sequence → final step's <c>LoggedIntoGame</c> event
/// fires → main-window VM calls <see cref="Arm"/> → the next
/// MainMenuEnterRealm pattern match (the actual menu screen) sends
/// the entry command + closes the latch + queues the startup
/// refresh. Mid-session navigation to menu (user types X to exit
/// realm) doesn't re-arm — only a fresh login automation completion
/// does.
/// </para>
/// <para>
/// Hangup suppression: if a <see cref="HangupSignal.SignalHangup"/>
/// fired before the current connect (intentional <c>@hangup</c> or a
/// future hang-up-if-naked / hang-up-if-low-HP automation),
/// <see cref="Arm"/> consumes the suppression flag and refuses to
/// open the latch — user reads what's on the screen and types their
/// entry manually so the auto-refresh doesn't spam the wire while
/// they're in a dangerous spot.
/// </para>
/// <para>
/// Startup sequence cadence: the four lines (<c>\r</c>, <c>stat\r</c>,
/// <c>exp\r</c>, <c>i\r</c>) are sent with <see cref="StartupStep"/>
/// gaps (default 400 ms) so the BBS renders each response cleanly
/// before the next command lands. A bare CR comes first because the
/// MOTD between Enter and the in-game prompt can pause on a "press
/// any key" pagination — the CR flushes past it. <c>stat</c> /
/// <c>exp</c> are parsed by <see cref="StatParser"/>; <c>i</c> is
/// sent today and parsed by the Phase 9 inventory work when that
/// lands.
/// </para>
/// </remarks>
public sealed class MainMenuEntryAutomation : IDisposable
{
    private readonly MessageRouter _router;
    private readonly GameCommands _commands;
    private readonly HangupSignal _hangup;
    private readonly LogService? _log;
    private readonly IDisposable _patternSub;
    private Action<byte[]>? _wireSender;
    private DateTime _armedUntilUtc = DateTime.MinValue;
    private Avalonia.Threading.DispatcherTimer? _startupTimer;
    private int _startupIndex;
    private bool _disposed;

    /// <summary>
    /// Startup sequence sent after <see cref="GameCommands.EntryCommand"/>.
    /// First entry is a bare CR (the empty string + the trailing \r the
    /// wire-sender adds) so any "press any key" pagination on the MOTD
    /// gets flushed. The remaining three populate
    /// <see cref="PlayerStats"/> (stat / exp) and emit inventory text
    /// the Phase 9 InventoryManager parser will consume once it ships.
    /// Read-only so tests + future settings UI can introspect the
    /// canonical sequence without rebuilding it.
    /// </summary>
    public static readonly IReadOnlyList<string> StartupSequence =
        new[] { string.Empty, "stat", "exp", "i" };

    /// <summary>
    /// How long the latch stays armed after <see cref="Arm"/>. Default
    /// 15 s — long enough for the BBS to actually paint the main menu
    /// after login completes, short enough that an in-game chat line
    /// arriving minutes later can't trip the entry-command send.
    /// </summary>
    public TimeSpan ArmWindow { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gap between successive startup-sequence sends after the entry
    /// command lands. Default 400 ms — long enough for a small
    /// MajorMUD screen (the stat block, exp readout, or inventory
    /// list) to scroll without the next command queueing into the
    /// middle of the previous response, short enough that the four
    /// commands complete within ~1.6 s of entering the realm.
    /// </summary>
    public TimeSpan StartupStep { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Test seam — override the clock so unit tests don't have to sleep.</summary>
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    /// <summary>Test seam — most recent bytes the engine asked to write to the wire.</summary>
    internal List<byte[]> LastSentForTests { get; } = new();

    /// <summary>True when armed AND inside the window.</summary>
    public bool IsArmed => NowProvider() < _armedUntilUtc;

    public MainMenuEntryAutomation(
        MessageRouter router,
        GameCommands commands,
        HangupSignal hangup,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(hangup);
        _router = router;
        _commands = commands;
        _hangup = hangup;
        _log = log;
        _patternSub = _router.Subscribe(KnownPatterns.MainMenuEnterRealm, OnMainMenuLine);
    }

    /// <summary>
    /// Bind the wire-sender. MainWindowViewModel supplies
    /// <c>SendUserInput</c> alongside the other auto-send services.
    /// Without it the engine still observes pattern matches but
    /// produces no wire output.
    /// </summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    /// <summary>
    /// Open the arm window — UNLESS a hangup signal is pending, in
    /// which case consume the suppression flag and refuse to arm so
    /// the user manually re-enters. Called by MainWindowViewModel
    /// right after <see cref="Services.LoginAutomator.LoggedIntoGame"/>
    /// fires (the only point in the session where auto-entering the
    /// realm is authorised). The window closes on first menu-match OR
    /// when <see cref="ArmWindow"/> elapses, whichever first.
    /// </summary>
    public void Arm()
    {
        if (_hangup.ConsumeSuppressEntry())
        {
            _log?.Log(LogSeverity.Info, "MainMenuEntry",
                "Skipping auto-entry — prior hangup intent; user types entry command manually.");
            return;
        }
        _armedUntilUtc = NowProvider() + ArmWindow;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _patternSub.Dispose();
        StopStartupSequence();
    }

    /// <summary>
    /// Test seam — runs one pass of the startup-sequence timer
    /// without a real tick. Mirrors production: the timer only fires
    /// after <see cref="StartStartupSequence"/> sets it up (which
    /// only happens after a successful entry-command send), so a
    /// suppressed-entry / never-armed test path produces no startup
    /// commands.
    /// </summary>
    internal void TickStartupSequenceForTests()
    {
        if (_startupTimer is null) return;
        SendNextStartupCommand();
    }

    private void OnMainMenuLine(MatchResult _)
    {
        // Tight latch check: must be armed AND inside the window.
        // Once the entry command is sent we close the latch so a
        // subsequent in-game line that happens to look like the menu
        // (gossip, telepath, room description) can't re-fire.
        if (NowProvider() >= _armedUntilUtc) return;
        _armedUntilUtc = DateTime.MinValue;

        string command = _commands.EntryCommand;
        if (string.IsNullOrEmpty(command)) return;
        if (_wireSender is null) return;

        SendWire(command);
        _log?.Log(LogSeverity.Info, "MainMenuEntry",
            $"Auto-entered realm with '{command}' after login automation completed.");
        StartStartupSequence();
    }

    private void SendWire(string text)
    {
        // Empty string + \r = bare CR (flushes MOTD pagination). Other
        // strings get the same \r suffix every other engine uses.
        byte[] bytes = Encoding.Latin1.GetBytes(text + "\r");
        LastSentForTests.Add(bytes);
        _wireSender?.Invoke(bytes);
    }

    // ----- Post-entry startup sequence ----------------------------------

    private void StartStartupSequence()
    {
        StopStartupSequence();
        _startupIndex = 0;
        _startupTimer = new Avalonia.Threading.DispatcherTimer(
            interval: StartupStep,
            priority: Avalonia.Threading.DispatcherPriority.Background,
            callback: (_, _) => SendNextStartupCommand());
        _startupTimer.Start();
    }

    private void StopStartupSequence()
    {
        _startupTimer?.Stop();
        _startupTimer = null;
    }

    private void SendNextStartupCommand()
    {
        if (_startupIndex >= StartupSequence.Count)
        {
            StopStartupSequence();
            return;
        }
        string next = StartupSequence[_startupIndex];
        _startupIndex++;
        SendWire(next);
        _log?.Log(LogSeverity.Debug, "MainMenuEntry",
            next.Length == 0
                ? "Sent post-entry CR (MOTD flush)."
                : $"Sent post-entry refresh: {next}.");
    }
}
