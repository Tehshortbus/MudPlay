using MudPlay.Services;

namespace MudPlay.Game.Remote;

// `!@<command> [args]` typed by the user: the remote command runs on this client as
// though it had just come in, and nothing goes to the game. Each reply the handler
// would have sent back is printed to the terminal in brackets instead. Typing is
// quicker than the menus for some things (user, 2026-11-02).
//
// It runs as the local API does (RemoteCommandManager.TryInvokeLocal): the user owns
// the client, so no party-permission check applies, while the remote-command off
// switch, the master switch and the hard blocks still hold.
public sealed class SelfRemoteCommand
{
    public const string Prefix = "!@";

    // The sender a handler sees and logs.
    public const string SenderName = "(typed !@)";

    private readonly RemoteCommandManager _engine;
    private readonly Action<string> _print;
    private readonly LogService? _log;

    // print: one terminal line, written as given.
    public SelfRemoteCommand(RemoteCommandManager engine, Action<string> print, LogService? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _print = print ?? throw new ArgumentNullException(nameof(print));
        _log = log;
    }

    // True when the line was a `!@` command and has been run here: the caller sends
    // nothing for it.
    public bool TryHandleTypedLine(string? line)
    {
        if (line is null) return false;
        string text = line.Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        string[] words = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words[0] == "@")
        {
            _print("[Type a remote command after !@, e.g. !@health]");
            return true;
        }
        string command = words[0].ToLowerInvariant();
        _log?.Info("RemoteCmd", $"typed {Prefix}{command[1..]}: run on this client");
        RemoteCommandManager.LocalInvokeResult result =
            _engine.TryInvokeLocal(command, words[1..], reply => _print($"[{reply}]"), SenderName);
        string? refused = result switch
        {
            RemoteCommandManager.LocalInvokeResult.UnknownCommand => $"{command} is not a remote command",
            RemoteCommandManager.LocalInvokeResult.Disabled => "remote commands are switched off",
            RemoteCommandManager.LocalInvokeResult.HardBlocked => $"{command} never runs as a remote command",
            RemoteCommandManager.LocalInvokeResult.PathChannelOnly => $"{command} only comes from another player by telepath or gangpath",
            RemoteCommandManager.LocalInvokeResult.MasterSwitchOff => $"{command} cannot run: the master switch (Auto-All) is off",
            _ => null,
        };
        if (refused is not null) _print($"[{refused}]");
        return true;
    }
}
