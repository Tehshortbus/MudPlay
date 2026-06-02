using System.Text;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game;

/// <summary>
/// Sends the configured <see cref="GameCommands.EntryCommand"/>
/// (default <c>E</c>) when the MajorMUD main-menu screen is recognised
/// at the tail of the automated BBS-login sequence.
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
/// the entry command + clears the latch. Mid-session navigation to
/// menu (user types X to exit realm) doesn't re-arm — only a fresh
/// login automation completion does.
/// </para>
/// </remarks>
public sealed class MainMenuEntryAutomation : IDisposable
{
    private readonly MessageRouter _router;
    private readonly GameCommands _commands;
    private readonly LogService? _log;
    private readonly IDisposable _patternSub;
    private Action<byte[]>? _wireSender;
    private DateTime _armedUntilUtc = DateTime.MinValue;
    private bool _disposed;

    /// <summary>
    /// How long the latch stays armed after <see cref="Arm"/>. Default
    /// 15 s — long enough for the BBS to actually paint the main menu
    /// after login completes, short enough that an in-game chat line
    /// arriving minutes later can't trip the entry-command send.
    /// </summary>
    public TimeSpan ArmWindow { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Test seam — override the clock so unit tests don't have to sleep.</summary>
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    /// <summary>Test seam — most recent bytes the engine asked to write to the wire.</summary>
    internal List<byte[]> LastSentForTests { get; } = new();

    /// <summary>True when armed AND inside the window.</summary>
    public bool IsArmed => NowProvider() < _armedUntilUtc;

    public MainMenuEntryAutomation(MessageRouter router, GameCommands commands, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(commands);
        _router = router;
        _commands = commands;
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
    /// Open the arm window. Called by MainWindowViewModel right after
    /// <see cref="Services.LoginAutomator.LoggedIntoGame"/> fires — the
    /// only point in the session where auto-entering the realm is
    /// authorised. The window closes on first menu-match OR when
    /// <see cref="ArmWindow"/> elapses, whichever first.
    /// </summary>
    public void Arm()
    {
        _armedUntilUtc = NowProvider() + ArmWindow;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _patternSub.Dispose();
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

        byte[] bytes = Encoding.Latin1.GetBytes(command + "\r");
        LastSentForTests.Add(bytes);
        _wireSender(bytes);
        _log?.Log(LogSeverity.Info, "MainMenuEntry",
            $"Auto-entered realm with '{command}' after login automation completed.");
    }
}
