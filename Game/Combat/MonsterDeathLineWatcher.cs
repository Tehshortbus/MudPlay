using MudPlay.Game.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game.Combat;

// Reads a monster's death from its own death text, for the set's monsters whose text
// is known (RealmEventLines: the Messages table's death rows). Our own kills are read
// from our exp line (MonsterDeathWatcher), and the game prints a kill as death text,
// then "You gain N experience.", then the prompt (GAME_MECHANICS "Kill detection and
// monster-kill message order"). So a death text with no exp line before the prompt is
// a kill that paid us nothing: a party member's, or anyone's, while we weren't
// attacking that monster. Such a kill is that monster's death all the same (user,
// 2026-11-14), and is raised as DiedToAnother. A death text our exp line follows is
// our kill, which MonsterDeathWatcher already counts, so it raises nothing here.
public sealed class MonsterDeathLineWatcher : IDisposable
{
    public const string LogCategory = "MonsterDeath";

    private readonly MessageRouter _router;
    private readonly Func<RealmEventLines> _lines;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly IDisposable _expSub;
    private MonsterDeathLine? _pending;
    private bool _disposed;

    public event Action<MonsterDeathLine>? DiedToAnother;

    // The last kill read here, for the bug report.
    public MonsterDeathLine? Last { get; private set; }

    public MonsterDeathLineWatcher(MessageRouter router, Func<RealmEventLines> lines,
        Func<DateTimeOffset>? now = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(lines);
        _router = router;
        _lines = lines;
        _now = now ?? (() => DateTimeOffset.Now);
        _log = log;
        _router.LineDispatched += OnLine;
        _expSub = router.Subscribe(KnownPatterns.UserGainExperience, OnExp);
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        // The prompt closes the kill's output: no exp line came, so it wasn't ours.
        if (line.IsPromptLine)
        {
            Settle();
            return;
        }
        if (!_lines().IsDeath(line.Text, out string? monster)) return;
        // A second death before the prompt: the first had its chance at an exp line.
        Settle();
        if (monster is null)
        {
            _log?.Debug(LogCategory, $"death text '{line.Text.Trim()}' is several monsters' — names no one");
            return;
        }
        _pending = new MonsterDeathLine(monster, line.Text.Trim(), _now());
    }

    private void OnExp(MatchResult _)
    {
        if (_pending is not { } death) return;
        _pending = null;
        _log?.Debug(LogCategory, $"'{death.Monster}' died to our attack (its death text, then our exp line)");
    }

    private void Settle()
    {
        if (_pending is not { } death) return;
        _pending = null;
        Last = death;
        _log?.Info(LogCategory, $"'{death.Monster}' died with no exp line of ours: another's kill, read from '{death.Line}'");
        DiedToAnother?.Invoke(death);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLine;
        _expSub.Dispose();
    }
}
