using System.Collections.ObjectModel;
using System.IO;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

/// <summary>
/// In-memory cache of the Messages/Responses catalogue for the
/// <see cref="GameDataCache.ActiveSet"/>. Records are paired with the
/// active game-data set on disk at
/// <c>Data/game data/{set}/messages.json</c>, falling back to the
/// universal wcc-derived seed at <c>Data/Global/Messages.seed.json</c>
/// (bootstrapped from the bundled <c>Defaults/</c> copy on first
/// launch).
/// </summary>
/// <remarks>
/// Wiring: <see cref="AppServices"/> subscribes the store to
/// <see cref="GameDataCache.ActiveSetChanged"/> — on every set switch
/// the file at <see cref="AppPaths.MessagesFile"/> is reloaded
/// (missing file ⇒ falls through to the seed). The Game Data Browser →
/// Messages tab binds the live <see cref="Messages"/> collection.
/// </remarks>
public sealed class MessageStore
{
    private readonly LogService? _log;

    /// <summary>Live mirror of the active set's message records. Bound by the Messages tab.</summary>
    public ObservableCollection<MessageRecord> Messages { get; } = new();

    /// <summary>Set name currently sourcing <see cref="Messages"/>, or <c>null</c> when none is active.</summary>
    public string? ActiveSet { get; private set; }

    public MessageStore() { }

    /// <summary>
    /// Production ctor — wire the log sink so parse failures surface
    /// in the LogPane instead of silently leaving the catalogue empty.
    /// </summary>
    public MessageStore(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>
    /// Switch the catalogue to <paramref name="setName"/>'s on-disk
    /// file. Pass <c>null</c> to clear (no set active). Load priority:
    /// <list type="number">
    ///   <item>Per-set file <see cref="AppPaths.MessagesFile"/>
    ///     (<c>Data/game data/{set}/messages.json</c>) — the canonical
    ///     persisted state once a user has edited.</item>
    ///   <item>Universal seed <see cref="AppPaths.DefaultMessagesSeedFile"/>
    ///     (<c>Data/Global/Messages.seed.json</c>) — applies to every
    ///     set; the message text is universal across MajorMUD realms.
    ///     Bootstrapped from the bundled <c>Defaults/</c> copy on
    ///     first launch via <see cref="AppPaths.EnsureGlobalSeedsBootstrapped"/>.</item>
    /// </list>
    /// The seed itself is never written.
    /// </summary>
    public void Load(string? setName)
    {
        Messages.Clear();
        ActiveSet = setName;
        if (string.IsNullOrWhiteSpace(setName)) return;

        if (TryLoadInto(AppPaths.MessagesFile(setName))) return;
        TryLoadInto(AppPaths.DefaultMessagesSeedFile);
    }

    /// <summary>
    /// Read a JSON list from <paramref name="path"/> and append every
    /// record to <see cref="Messages"/>. Returns <c>true</c> iff the
    /// file existed AND parsed cleanly. A corrupt file returns
    /// <c>false</c> + leaves <see cref="Messages"/> in whatever state
    /// the partial parse left it (callers reset via the upstream
    /// Clear() before invoking).
    /// </summary>
    private bool TryLoadInto(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            List<MessageRecord>? loaded = JsonStore.Load<List<MessageRecord>>(path);
            if (loaded is null) return false;
            foreach (MessageRecord m in loaded) Messages.Add(m);
            return true;
        }
        catch (Exception ex)
        {
            // Corrupt file ⇒ leave empty, but DO surface — a silent
            // swallow is what hid the missing JsonStringEnumConverter
            // for the entire seed-loading work. Log loud so future
            // schema drift fails visibly.
            _log?.Log(LogSeverity.Warn, "Messages",
                $"Failed to load '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Persist <see cref="Messages"/> to <see cref="ActiveSet"/>'s file.</summary>
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(ActiveSet)) return;
        JsonStore.Save(AppPaths.MessagesFile(ActiveSet), Messages);
    }

    /// <summary>Replace the catalogue with <paramref name="records"/> and persist.</summary>
    public void Replace(IEnumerable<MessageRecord> records)
    {
        Messages.Clear();
        foreach (MessageRecord m in records) Messages.Add(m);
        Save();
    }
}
