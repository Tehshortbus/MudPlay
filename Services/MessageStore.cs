using System.Collections.ObjectModel;
using System.IO;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

/// <summary>
/// In-memory cache of the Messages/Responses catalogue for the
/// <see cref="GameDataCache.ActiveSet"/>. Records are paired with the
/// active game-data set on disk — initially imported from a MegaMUD
/// <c>messages.md</c> file and saved to
/// <c>Data/Global/Messages/{set-name}.json</c> so each realm's
/// catalogue ships alongside the realm's MDB tables.
/// </summary>
/// <remarks>
/// Wiring: <see cref="AppServices"/> subscribes the store to
/// <see cref="GameDataCache.ActiveSetChanged"/> — on every set switch
/// the file at <see cref="AppPaths.MessagesFile"/> is reloaded
/// (missing file ⇒ empty catalogue). The Game Data Browser →
/// Messages tab binds the live <see cref="Messages"/> collection.
/// </remarks>
public sealed class MessageStore
{
    /// <summary>Live mirror of the active set's message records. Bound by the Messages tab.</summary>
    public ObservableCollection<MessageRecord> Messages { get; } = new();

    /// <summary>Set name currently sourcing <see cref="Messages"/>, or <c>null</c> when none is active.</summary>
    public string? ActiveSet { get; private set; }

    /// <summary>
    /// Switch the catalogue to <paramref name="setName"/>'s on-disk
    /// file. Pass <c>null</c> to clear (no set active). Missing /
    /// unparseable user file collapses to the app-shipped seed at
    /// <see cref="AppPaths.DefaultMessagesFile"/> (when one exists for
    /// this set), then to an empty catalogue. The seed itself is
    /// never written — first user edit causes a fresh user file to be
    /// created via <see cref="Save"/>.
    /// </summary>
    public void Load(string? setName)
    {
        Messages.Clear();
        ActiveSet = setName;
        if (string.IsNullOrWhiteSpace(setName)) return;

        // 1. User file wins when present (the canonical persisted state).
        string userPath = AppPaths.MessagesFile(setName);
        if (TryLoadInto(userPath)) return;

        // 2. Fall back to the app-shipped seed for this set, if any.
        string seedPath = AppPaths.DefaultMessagesFile(setName);
        TryLoadInto(seedPath);
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
        catch
        {
            // Corrupt file ⇒ leave empty; user can re-import or
            // hand-edit. We don't surface here because the Browser
            // status bar already shows the row count.
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
