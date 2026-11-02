using System.IO;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// The triggers every character of a realm or of a BBS shares (TriggerLevel.Realm,
// TriggerLevel.Bbs), in a file the other clients on this machine read and write
// too. Each client holds a copy of the list:
//   - another client's write is taken in when the file's write time or size moves
//     (TakeIn, polled on the heartbeat), so a trigger edited on one character works
//     at once on the other (user, 2026-10-30);
//   - a change of its own is made to what is on disk, under the hold every client's
//     write takes (SharedFileWrite.Hold), so neither client's edit is lost to the
//     other's older copy. A change that can't have the hold isn't made, and says so.
public sealed class SharedTriggerFile
{
    private static readonly (DateTime Time, long Size) NoFile = (DateTime.MinValue, -1);

    private readonly TriggerLevel _level;
    private readonly LogService? _log;
    // The file's write time and size as this client last read or wrote it.
    private (DateTime Time, long Size) _seen = NoFile;
    // The last read found the file damaged: it isn't written over, so what is in it
    // can still be put right by hand.
    private bool _unreadable;

    public SharedTriggerFile(TriggerLevel level, LogService? log = null)
    {
        _level = level;
        _log = log;
    }

    // The file in use; null with no realm or BBS to share with.
    public string? Path { get; private set; }

    public IReadOnlyList<Trigger> Triggers { get; private set; } = Array.Empty<Trigger>();

    private string Which => _level == TriggerLevel.Realm ? "realm" : "BBS";
    private string Whose => Which + "'s";

    public void Open(string? path)
    {
        Path = path;
        _seen = NoFile;
        Triggers = Array.Empty<Trigger>();
        if (path is null) return;
        Take(path);
        _log?.Info(TriggerEngine.LogSource, $"The {Whose} triggers: {Triggers.Count}.");
    }

    // Takes in the file when another client wrote it since this client last read or
    // wrote it. True when it did.
    public bool TakeIn()
    {
        if (Path is null || Stamp(Path) == _seen) return false;
        Take(Path);
        _log?.Info(TriggerEngine.LogSource, $"The {Whose} triggers changed on disk (another client): {Triggers.Count} now.");
        return true;
    }

    // Makes edit to the list as it is on disk, writes it and keeps the result. False
    // when it couldn't be written (another client's hold not let go in time, the
    // disk), with nothing changed.
    public bool Change(Action<List<Trigger>> edit)
    {
        if (Path is null)
        {
            _log?.Warn(TriggerEngine.LogSource, $"No {Which} is loaded to keep the trigger with, so the change wasn't made.");
            return false;
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using IDisposable hold = SharedFileWrite.Hold(Path, out _);
            List<Trigger> list = Stamp(Path) == _seen ? Triggers.ToList() : Read(Path);
            if (_unreadable)
            {
                _log?.Warn(TriggerEngine.LogSource, $"The {Whose} triggers file '{Path}' can't be read, so it isn't written over and the change wasn't made.");
                return false;
            }
            edit(list);
            JsonStore.Save(Path, list);
            _seen = Stamp(Path);
            Triggers = Leveled(list);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warn(TriggerEngine.LogSource, $"The {Whose} triggers couldn't be saved, so the change wasn't made: {ex.Message}");
            return false;
        }
    }

    private void Take(string path)
    {
        // Stamped before the read: a write landing between the two is still seen as new.
        _seen = Stamp(path);
        Triggers = Read(path);
    }

    private IReadOnlyList<Trigger> Leveled(IEnumerable<Trigger> list) => list.Select(t => t with { Level = _level }).ToArray();

    private List<Trigger> Read(string path)
    {
        _unreadable = false;
        if (!File.Exists(path)) return new List<Trigger>();
        try
        {
            List<Trigger> read = JsonStore.Load<List<Trigger>>(path) ?? new List<Trigger>();
            return Leveled(read.OfType<Trigger>().Select(t => t.WithBlanksForMissing())).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _unreadable = true;
            _log?.Warn(TriggerEngine.LogSource, $"The {Whose} triggers can't be read from '{path}': {ex.Message}");
            return new List<Trigger>();
        }
    }

    private static (DateTime Time, long Size) Stamp(string path)
    {
        FileInfo file = new(path);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : NoFile;
    }
}
