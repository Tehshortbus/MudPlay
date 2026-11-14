using MudPlay.Game.GameData;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Combat;

// A monster appearing or changing form in the room ("A moss zombie rises from the
// ground!", one of the set's monster entrances in RealmEventLines) puts a monster on
// no roster: the room isn't listed again. As for a mid-fight summon
// (MonsterSummonWatcher), the roster is marked as not whole and the room re-displayed,
// which the combat engine asks for only while it runs.
public sealed class MonsterEntranceWatcher : IDisposable
{
    private const string LogCategory = "Summon";

    private readonly MessageRouter _router;
    private readonly RoomEntityClassifier _classifier;
    private readonly Func<RealmEventLines> _lines;
    private readonly Func<string, bool> _requestRoomRefresh;
    private readonly LogService? _log;
    private bool _disposed;

    public MonsterEntranceWatcher(MessageRouter router, RoomEntityClassifier classifier,
        Func<RealmEventLines> lines, Func<string, bool> requestRoomRefresh, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(requestRoomRefresh);
        _router = router;
        _classifier = classifier;
        _lines = lines;
        _requestRoomRefresh = requestRoomRefresh;
        _log = log;
        _router.LineDispatched += OnLine;
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine || !_lines().IsEntrance(line.Text)) return;
        string text = line.Text.Trim();
        _classifier.NoteUnlistedArrival($"a monster entrance: '{text}'");
        bool sent = _requestRoomRefresh($"monster entrance '{text}'");
        _log?.Log(LogSeverity.Info, LogCategory, sent
            ? $"'{text}' — re-displaying the room so the monster joins the roster"
            : $"'{text}' — room re-display not sent (combat engine off, dark room or one just sent)");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLine;
    }
}
