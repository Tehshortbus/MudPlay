using System.IO;
using System.Linq;
using MudPlay.Game.Combat;

namespace MudPlay.Services;

// The per-set, editable vocabulary of monster flavor adjectives ("large", "nasty", …)
// the game prepends to a monster's base name. RoomEntityClassifier strips a leading word
// in this set to resolve a prefixed display name ("large giant rat") to its canonical
// monster generically — so no per-monster prefix data has to be hand-maintained.
//
// The vocabulary is game-TYPE-specific: a different door game uses different adjectives.
// So it lives WITH the game data — one file per set at game data/{set}/flavor-prefixes.json,
// travelling with the realm the same way monster-messages.json does. Absent that file the
// built-in MonsterFlavorPrefixes.DefaultPrefixes (the 17 stock MajorMUD adjectives) apply;
// the Game Data Browser's Flavor Prefixes section lets the user add / remove words for a
// custom realm, writing the FULL current list to the per-set file. There is no delta seed
// — the tiny word list is stored whole.
//
// Wiring: AppServices subscribes Load to GameDataCache.ActiveSetChanged, so every set
// switch swaps the vocabulary in real time. Edits with no active set stay in memory only
// (Save no-ops, matching MonsterMessageStore).
public sealed class FlavorPrefixStore
{
    private readonly LogService? _log;
    private readonly HashSet<string> _prefixes = new(StringComparer.OrdinalIgnoreCase);

    // Set name currently sourcing the vocabulary, or null when none is active.
    public string? ActiveSet { get; private set; }

    // Raised after any load / add / remove / reset so the editor re-renders its list.
    public event Action? Changed;

    public FlavorPrefixStore(LogService? log = null)
    {
        _log = log;
        SeedDefaults();
    }

    // The active vocabulary, sorted for stable editor display.
    public IReadOnlyList<string> Prefixes =>
        _prefixes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    // True when word is a known flavor adjective (case-insensitive). Hot path — called
    // once per room-occupant token as the classifier walks an "Also here:" list.
    public bool IsPrefix(string word) => _prefixes.Contains(word);

    // Switch the vocabulary to setName's on-disk file, falling back to the built-in
    // defaults when the set has no file yet (or no set is active).
    public void Load(string? setName)
    {
        ActiveSet = setName;
        _prefixes.Clear();
        List<string>? loaded = string.IsNullOrWhiteSpace(setName)
            ? null
            : TryLoad(AppPaths.FlavorPrefixesFile(setName));
        if (loaded is not null)
        {
            foreach (string? p in loaded)
                if (p is not null) AddNormalized(p);   // a hand edit's null is no prefix
        }
        else
            SeedDefaults();
        Changed?.Invoke();
    }

    // Add word (trimmed, deduped case-insensitively). Returns true when it was new;
    // persists + notifies only on a real change.
    public bool Add(string word)
    {
        if (word is null) return false;
        string w = word.Trim();
        if (w.Length == 0 || !_prefixes.Add(w)) return false;
        Save();
        Changed?.Invoke();
        return true;
    }

    // Remove word. Returns true when it was present; persists + notifies on a real change.
    public bool Remove(string word)
    {
        if (word is null || !_prefixes.Remove(word.Trim())) return false;
        Save();
        Changed?.Invoke();
        return true;
    }

    // Restore the built-in defaults, replacing any customization for this set.
    public void ResetToDefaults()
    {
        _prefixes.Clear();
        SeedDefaults();
        Save();
        Changed?.Invoke();
    }

    private void SeedDefaults()
    {
        foreach (string p in MonsterFlavorPrefixes.DefaultPrefixes) _prefixes.Add(p);
    }

    private void AddNormalized(string word)
    {
        string w = word.Trim();
        if (w.Length > 0) _prefixes.Add(w);
    }

    // Parsed list iff the file existed AND parsed cleanly; null for missing/corrupt so
    // Load falls back to the built-in defaults.
    private List<string>? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonStore.Load<List<string>>(path);
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, "FlavorPrefixes", $"Failed to load '{path}': {ex.Message}");
            return null;
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(ActiveSet)) return;
        JsonStore.Save(AppPaths.FlavorPrefixesFile(ActiveSet),
            _prefixes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
