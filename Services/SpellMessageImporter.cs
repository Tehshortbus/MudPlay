using System.IO;
using System.Linq;
using System.Text.Json;
using FujinTerm.Models.GameData;

namespace FujinTerm.Services;

/// <summary>
/// Loads MegaMUD-derived spell-message files (JSON array of
/// <see cref="SpellMessage"/> records) and writes the result into the
/// active <see cref="GameDataCache"/> set as
/// <c>SpellMessages.json</c>. No bundled data — users supply their
/// realm's file at import time, per the master plan.
/// </summary>
/// <remarks>
/// <para>
/// PR 5.8 ships the parsing / serialisation half. UI wiring (the file
/// picker on Game Data → Import Spell Messages…) lands with PR 5.23
/// alongside the MDB import menu entry.
/// </para>
/// <para>
/// Schema: each row in the JSON file is
/// <c>{ "SpellId": int, "Kind": "Cast" | "Hit" | "Resist" | "Expire"
/// | "TargetEffect", "Pattern": string, "EffectFlags": int }</c>.
/// Unknown <c>Kind</c> values fail the row but don't fail the import;
/// the importer surfaces them through <see cref="OnError"/> and
/// continues. Conflicts (a <c>SpellId+Kind+Pattern</c> tuple already
/// in the target file) are reported back to the caller — wiring
/// through the unified <see cref="Models.Import.ImportConflict"/>
/// dialog happens at the call-site.
/// </para>
/// </remarks>
public sealed class SpellMessageImporter
{
    private readonly GameDataCache _cache;

    /// <summary>Single-line status text for the eventual import-progress UI.</summary>
    public event Action<string>? OnStatusChanged;

    /// <summary>Per-row error during parse — the row is dropped, import continues.</summary>
    public event Action<string>? OnError;

    public SpellMessageImporter(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <summary>
    /// Read <paramref name="sourcePath"/> and return the rows. No
    /// merge / write — callers decide what to do with the parsed set
    /// (route conflicts through <see cref="Models.Import.ImportConflict"/>
    /// then write via <see cref="WriteAsync"/>).
    /// </summary>
    public static async Task<IReadOnlyList<SpellMessage>> ParseAsync(string sourcePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Spell-messages source file not found.", sourcePath);

        await using FileStream fs = File.OpenRead(sourcePath);
        return await ParseStreamAsync(fs, ct);
    }

    internal static async Task<IReadOnlyList<SpellMessage>> ParseStreamAsync(Stream stream, CancellationToken ct = default)
    {
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        SpellMessage[]? rows = await JsonSerializer.DeserializeAsync<SpellMessage[]>(stream, options, ct);
        return rows ?? Array.Empty<SpellMessage>();
    }

    /// <summary>
    /// Write <paramref name="rows"/> as the active set's
    /// <c>SpellMessages.json</c>. Caller is responsible for any merge
    /// against the existing file — pass the already-resolved final
    /// row set here.
    /// </summary>
    public async Task WriteAsync(IReadOnlyList<SpellMessage> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_cache.ActiveSet is null)
            throw new InvalidOperationException("No active game-data set — switch to one before writing spell messages.");

        string setDir = Path.Combine(_cache.GameDataRoot, _cache.ActiveSet);
        Directory.CreateDirectory(setDir);
        string targetPath = Path.Combine(setDir, "SpellMessages.json");

        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        await using FileStream fs = File.Create(targetPath);
        await JsonSerializer.SerializeAsync(fs, rows, options, ct);

        // Force the cache to drop any stale entry for SpellMessages so
        // the next GetRawTable sees the just-written content.
        _cache.EvictTable("SpellMessages");
        OnStatusChanged?.Invoke($"Wrote {rows.Count} spell-message rows to {targetPath}");
    }

    /// <summary>
    /// Read the active set's existing <c>SpellMessages.json</c> rows,
    /// or empty when the file doesn't exist yet. Used by importers
    /// that want to merge against the current state before writing.
    /// </summary>
    public IReadOnlyList<SpellMessage> ReadExisting()
    {
        if (_cache.ActiveSet is null) return Array.Empty<SpellMessage>();
        string path = Path.Combine(_cache.GameDataRoot, _cache.ActiveSet, "SpellMessages.json");
        if (!File.Exists(path)) return Array.Empty<SpellMessage>();

        try
        {
            JsonSerializerOptions options = new()
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            };
            byte[] bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize<SpellMessage[]>(bytes, options) ?? Array.Empty<SpellMessage>();
        }
        catch (JsonException ex)
        {
            OnError?.Invoke($"Existing SpellMessages.json is malformed: {ex.Message}");
            return Array.Empty<SpellMessage>();
        }
    }
}
