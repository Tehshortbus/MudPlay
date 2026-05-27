using System.Text.Json;

namespace FujinTerm.Services;

/// <summary>
/// Shared System.Text.Json plumbing used by every settings/profile service so
/// the on-disk JSON looks identical no matter who wrote it: indented for
/// human edits, comment-tolerant on read, missing files return null instead
/// of throwing.
/// </summary>
internal static class JsonStore
{
    /// <summary>
    /// Serializer options shared by all stores. Indented for readable diffs;
    /// comments tolerated on load so users can annotate hand-edited files.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Read and deserialize <typeparamref name="T"/> from <paramref name="path"/>.
    /// Returns <c>null</c> if the file does not exist. Throws with the file
    /// path in the message if the JSON is malformed — corrupt configuration is
    /// loud, not silent.
    /// </summary>
    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;

        string json = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Failed to parse {typeof(T).Name} from '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Serialize <paramref name="value"/> to JSON and write it to <paramref name="path"/>,
    /// creating any missing parent directories. Write is atomic via temp-file + rename
    /// so a crash mid-write can't leave a half-written file on disk.
    /// </summary>
    public static void Save<T>(string path, T value) where T : class
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Atomic write: serialize to a sibling .tmp file, then rename over the
        // target. File.Move with overwrite is atomic on every supported OS.
        string tmp = path + ".tmp";
        string json = JsonSerializer.Serialize(value, Options);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
