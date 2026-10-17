using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media.Imaging;
using MudPlay.Game.Emotes;

namespace MudPlay.Services;

// One user-defined emote as persisted in the manifest. Exactly one of Emoji / Image is
// set: Emoji is a Unicode string; Image is a file name (not a path) under
// AppPaths.EmotesDir. Identity is Shortcode (the bare ":name:" token, no colons).
public sealed class UserEmote
{
    public string Shortcode { get; set; } = "";
    public string? Emoji { get; set; }
    public string? Image { get; set; }
    public string? DisplayName { get; set; }
}

// The persisted emote library: the user's own emotes plus the names of built-in emotes
// they've hidden ("removed"), so a user can fully customise the default set.
public sealed class EmoteSet
{
    public List<UserEmote> Emotes { get; set; } = new();
    public List<string> HiddenDefaults { get; set; } = new();
}

// A staged emote the editor is about to commit: a Unicode emoji, or an image sourced
// from ImageSourcePath (an existing file under EmotesDir, or a new external/temp file
// the commit copies + scales in). Shortcode may be empty for an image imported without a
// definition — the editor flags those for the user to name (SuggestedShortcode seeds the
// box). Id is a stable handle so the editor can re-find a staged draft it's editing.
public sealed class EmoteDraft
{
    public string Id { get; } = System.Guid.NewGuid().ToString("N");
    public string Shortcode { get; set; } = "";
    public string? Emoji { get; set; }
    public string? ImageSourcePath { get; set; }
    public string? SuggestedShortcode { get; set; }
    public string? DisplayName { get; set; }
}

// The user's shareable emote library, one per BBS (shared across the characters on
// it, under its Emotes folder; the shared folder stands in while no BBS is active). Layers on the built-in catalog and publishes
// the merged scanner to EmoteRuntime. The editor stages changes and Commit()s them all
// at once (on Settings Apply / OK); nothing here mutates until then, except Export which
// reads the committed set. Import extracts a package's images to a temp dir and returns
// drafts for the editor to stage.
public sealed partial class EmoteStore
{
    private string _dir;
    private string _manifestPath;
    // Where the library was one folder for every BBS; a BBS with none takes a copy.
    private readonly string _sharedDir;
    private readonly SharedFileStamp _stamp = new();
    private readonly LogService? _log;
    private EmoteSet _set = new();

    public EmoteStore(string? dir = null, LogService? log = null)
    {
        _sharedDir = dir ?? AppPaths.EmotesDir;
        _dir = _sharedDir;
        _manifestPath = Path.Combine(_dir, "emotes.json");
        _log = log;
        Directory.CreateDirectory(_dir);
        _set = LoadFromDisk();
        Republish();
    }

    // Switch to a BBS's own library (its Emotes folder), or back to the shared one
    // when no BBS is active. The emotes are a BBS's: its players and their
    // shortcodes. A BBS with no library yet takes a copy of the shared one, which is
    // what every BBS used before.
    public void OnBbsChanged(string? bbsName)
    {
        string dir = string.IsNullOrWhiteSpace(bbsName) ? _sharedDir : AppPaths.BbsEmotesDir(bbsName);
        if (PathsEqual(dir, _dir)) return;
        if (!Directory.Exists(dir)) CopyLibrary(_sharedDir, dir);
        Directory.CreateDirectory(dir);
        _dir = dir;
        _manifestPath = Path.Combine(dir, "emotes.json");
        _set = LoadFromDisk();
        Republish();
    }

    // Re-read the library when another client on the BBS has saved it. Called on
    // the heartbeat. True when it was re-read.
    public bool TakeInOutsideChanges()
    {
        if (!_stamp.ChangedOutside(_manifestPath)) return false;
        _set = LoadFromDisk();
        Republish();
        return true;
    }

    private void CopyLibrary(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        try
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        }
        catch (System.Exception ex) when (ex is IOException or System.UnauthorizedAccessException)
        {
            // Another client copied it first, or a file was in use: what did copy stands.
            _log?.Warn("Emotes", $"copy of the emote library to '{to}' was incomplete: {ex.Message}");
        }
    }

    public string Dir => _dir;
    public IReadOnlyList<UserEmote> Emotes => _set.Emotes;
    public IReadOnlyList<string> HiddenDefaults => _set.HiddenDefaults;

    // A fresh copy of the committed set for the editor to stage against.
    public EmoteSet Snapshot() => new()
    {
        Emotes = _set.Emotes.Select(e => new UserEmote
            { Shortcode = e.Shortcode, Emoji = e.Emoji, Image = e.Image, DisplayName = e.DisplayName }).ToList(),
        HiddenDefaults = _set.HiddenDefaults.ToList(),
    };

    [GeneratedRegex(@"^[A-Za-z0-9_+\-]+$")]
    private static partial Regex ShortcodeShape();

    // Strip surrounding colons / whitespace and validate the shape (the same token class
    // the scanner matches). Returns null when the name can't be a shortcode.
    public static string? NormalizeShortcode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim().Trim(':').Trim();
        return s.Length > 0 && ShortcodeShape().IsMatch(s) ? s : null;
    }

    // Commit the staged library: copy in any new/temp images, write the manifest, prune
    // orphaned image files, and republish the live scanner. This is the ONLY path that
    // changes what's on disk or in the conversation window.
    public void Commit(IReadOnlyList<EmoteDraft> drafts, IReadOnlyList<string> hiddenDefaults)
    {
        List<UserEmote> emotes = new();
        HashSet<string> keptImages = new(System.StringComparer.OrdinalIgnoreCase);

        foreach (EmoteDraft d in drafts)
        {
            if (NormalizeShortcode(d.Shortcode) is not { } name) continue;

            if (!string.IsNullOrEmpty(d.ImageSourcePath))
            {
                try
                {
                    string file = CopyScaledImage(d.ImageSourcePath, name);
                    ConversationImageInvalidate?.Invoke(Path.Combine(_dir, file));
                    emotes.Add(new UserEmote { Shortcode = name, Image = file, DisplayName = d.DisplayName });
                    keptImages.Add(file);
                }
                catch (System.Exception ex) { _log?.Warn("Emotes", $"copy of '{d.ImageSourcePath}' failed: {ex.Message}"); }
            }
            else if (!string.IsNullOrEmpty(d.Emoji))
            {
                emotes.Add(new UserEmote { Shortcode = name, Emoji = d.Emoji, DisplayName = d.DisplayName });
            }
        }

        _set = new EmoteSet
        {
            Emotes = emotes,
            HiddenDefaults = hiddenDefaults
                .Select(NormalizeShortcode).Where(n => n is not null).Select(n => n!).Distinct().ToList(),
        };
        Save();
        PruneOrphanImages(keptImages);
        Republish();
    }

    // Write the committed set as a shareable package (a zip of the manifest + images).
    public bool Export(string destZipPath)
    {
        try
        {
            if (File.Exists(destZipPath)) File.Delete(destZipPath);
            using ZipArchive zip = ZipFile.Open(destZipPath, ZipArchiveMode.Create);
            if (File.Exists(_manifestPath)) zip.CreateEntryFromFile(_manifestPath, "emotes.json");
            foreach (string? img in _set.Emotes.Select(e => e.Image).Where(i => !string.IsNullOrEmpty(i)).Distinct())
            {
                string path = Path.Combine(_dir, img!);
                if (File.Exists(path)) zip.CreateEntryFromFile(path, img!);
            }
            return true;
        }
        catch (System.Exception ex) { _log?.Warn("Emotes", $"export to '{destZipPath}' failed: {ex.Message}"); return false; }
    }

    private static readonly string[] ImageExts = { ".png", ".gif", ".jpg", ".jpeg", ".webp" };

    // Extract a package (or plain zip of images) into tempDir and return drafts. A zip
    // with an emotes.json defines shortcodes; a zip without one (or with extra images)
    // yields UNDEFINED drafts (empty shortcode, filename suggested) the editor flags red.
    // Returns null only when the file can't be read as a zip.
    public IReadOnlyList<EmoteDraft>? ReadPackageDrafts(string srcZipPath, string tempDir)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(srcZipPath);
            Directory.CreateDirectory(tempDir);
            List<EmoteDraft> drafts = new();
            HashSet<string> namedImages = new(System.StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> written = new(System.StringComparer.OrdinalIgnoreCase);

            if (zip.GetEntry("emotes.json") is { } manifest)
            {
                EmoteSet? incoming;
                using (Stream s = manifest.Open())
                    incoming = System.Text.Json.JsonSerializer.Deserialize<EmoteSet>(s);
                foreach (UserEmote e in incoming?.Emotes ?? new())
                {
                    if (NormalizeShortcode(e.Shortcode) is not { } name) continue;
                    if (!string.IsNullOrEmpty(e.Image) && zip.GetEntry(e.Image) is { } imgEntry)
                    {
                        string fileName = Path.GetFileName(e.Image);
                        if (TempFileFor(tempDir, fileName, imgEntry.FullName, written) is not { } temp) continue;
                        imgEntry.ExtractToFile(temp, overwrite: true);
                        namedImages.Add(fileName);
                        drafts.Add(new EmoteDraft { Shortcode = name, ImageSourcePath = temp, DisplayName = e.DisplayName });
                    }
                    else if (!string.IsNullOrEmpty(e.Emoji))
                        drafts.Add(new EmoteDraft { Shortcode = name, Emoji = e.Emoji, DisplayName = e.DisplayName });
                }
            }

            // Any image not claimed by the manifest → an undefined draft to name.
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || namedImages.Contains(entry.Name)) continue;
                if (!ImageExts.Contains(Path.GetExtension(entry.Name).ToLowerInvariant())) continue;
                if (TempFileFor(tempDir, entry.Name, entry.FullName, written) is not { } temp) continue;
                entry.ExtractToFile(temp, overwrite: true);
                drafts.Add(new EmoteDraft { ImageSourcePath = temp, SuggestedShortcode = SuggestName(entry.Name) });
            }
            return drafts;
        }
        catch (System.Exception ex) { _log?.Warn("Emotes", $"read of package '{srcZipPath}' failed: {ex.Message}"); return null; }
    }

    // Where a package's image of this file name is unpacked in tempDir, or null when
    // it can't go there: a manifest is another player's text, so an image is only ever
    // written as one file directly in tempDir. A name an earlier image from another
    // entry already took is refused too, so one image never silently replaces another;
    // written maps each file name taken to the entry it was unpacked from.
    private string? TempFileFor(string tempDir, string fileName, string entryName, Dictionary<string, string> written)
    {
        if (string.IsNullOrEmpty(fileName) || PathGuard.Child(tempDir, fileName) is not { } path)
        {
            _log?.Warn("Emotes", $"skipped package image {PathGuard.Show(fileName)}: not a file name that can be unpacked");
            return null;
        }
        if (!written.TryAdd(fileName, entryName) && written[fileName] != entryName)
        {
            _log?.Warn("Emotes", $"skipped package image {PathGuard.Show(fileName)}: another image in the package already has that file name");
            return null;
        }
        return path;
    }

    // Copy every image in folderPath into tempDir and return UNDEFINED drafts (the
    // editor flags them red so the user names each). Filenames seed the shortcode box.
    public IReadOnlyList<EmoteDraft> ReadFolderDrafts(string folderPath, string tempDir)
    {
        List<EmoteDraft> drafts = new();
        try
        {
            Directory.CreateDirectory(tempDir);
            foreach (string file in Directory.EnumerateFiles(folderPath))
            {
                if (!ImageExts.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                string temp = Path.Combine(tempDir, Path.GetFileName(file));
                File.Copy(file, temp, overwrite: true);
                drafts.Add(new EmoteDraft { ImageSourcePath = temp, SuggestedShortcode = SuggestName(Path.GetFileName(file)) });
            }
        }
        catch (System.Exception ex) { _log?.Warn("Emotes", $"read of folder '{folderPath}' failed: {ex.Message}"); }
        return drafts;
    }

    private static string? SuggestName(string fileName)
    {
        string bare = Path.GetFileNameWithoutExtension(fileName);
        string cleaned = new string(bare.Where(c => char.IsLetterOrDigit(c) || c is '_' or '+' or '-').ToArray());
        return NormalizeShortcode(cleaned);
    }

    private const int MaxEmotePx = 128;

    // Copy an image into EmotesDir as baseName, downscaled to <= MaxEmotePx (preserving
    // aspect) via Avalonia's decoder. Falls back to a raw copy when decoding isn't
    // available (bytes that aren't a decodable image). Returns the stored file name.
    private string CopyScaledImage(string src, string baseName)
    {
        try
        {
            using Bitmap bmp = new(src);
            int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
            string destPng = Path.Combine(_dir, baseName + ".png");
            if (w <= MaxEmotePx && h <= MaxEmotePx
                && string.Equals(Path.GetExtension(src), ".png", System.StringComparison.OrdinalIgnoreCase))
            {
                if (!PathsEqual(src, destPng)) File.Copy(src, destPng, overwrite: true);
                return baseName + ".png";
            }
            double scale = System.Math.Min(1.0, (double)MaxEmotePx / System.Math.Max(w, h));
            PixelSize target = new(System.Math.Max(1, (int)System.Math.Round(w * scale)),
                                   System.Math.Max(1, (int)System.Math.Round(h * scale)));
            using Bitmap scaled = bmp.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
            scaled.Save(destPng);
            return baseName + ".png";
        }
        catch
        {
            string ext = Path.GetExtension(src);
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            string file = baseName + ext.ToLowerInvariant();
            string dest = Path.Combine(_dir, file);
            if (!PathsEqual(src, dest)) File.Copy(src, dest, overwrite: true);
            return file;
        }
    }

    // Bridge to ConversationMessageInlines.EmoteImages.Invalidate — set by AppServices so
    // a replaced image drops its cached bitmap.
    public System.Action<string>? ConversationImageInvalidate { get; set; }

    private void Republish()
    {
        EmoteCatalog catalog = EmoteCatalog.WithUser(_set.Emotes.Select(ToEmote), _set.HiddenDefaults);
        EmoteRuntime.Publish(new EmoteScanner(catalog));
    }

    private Emote ToEmote(UserEmote e)
    {
        return !string.IsNullOrEmpty(e.Image)
            ? new Emote(e.Shortcode, EmoteKind.Image, Path.Combine(_dir, e.Image))
            : new Emote(e.Shortcode, EmoteKind.Unicode, e.Emoji ?? "");
    }

    // Delete image files under EmotesDir no committed emote references any more.
    private void PruneOrphanImages(HashSet<string> keep)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(_dir))
            {
                string name = Path.GetFileName(path);
                if (name.Equals("emotes.json", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".txt", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (!keep.Contains(name)) { try { File.Delete(path); } catch { } }
            }
        }
        catch { /* best effort */ }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), System.StringComparison.OrdinalIgnoreCase);

    private EmoteSet LoadFromDisk()
    {
        _stamp.Mark(_manifestPath);
        try { return JsonStore.Load<EmoteSet>(_manifestPath) ?? new EmoteSet(); }
        catch (System.Exception ex) { _log?.Warn("Emotes", $"failed to load '{_manifestPath}': {ex.Message}"); return new EmoteSet(); }
    }

    private void Save()
    {
        try { JsonStore.Save(_manifestPath, _set); _stamp.Mark(_manifestPath); }
        catch (System.Exception ex) { _log?.Warn("Emotes", $"failed to save '{_manifestPath}': {ex.Message}"); }
    }
}
