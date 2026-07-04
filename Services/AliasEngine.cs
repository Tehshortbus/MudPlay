using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

// In-memory cache of the active character's Alias entries + the runtime
// expansion path.
//
// Match shape: first-word, case-insensitive, literal only — no regex.
// TryExpand is called from MainWindowViewModel.SendUserText before the text is
// encoded to bytes, so both the terminal canvas and the Conversation input
// field get aliases for free.
//
// Substitution: positional placeholders only — {0} for the whole rest of the
// typed line after the alias name; {1}, {2}, … for whitespace-split tokens.
// Deliberately not the same namespace as TriggerEngine.Variables — the two
// engines are isolated.
//
// Validation: NameConflictReason rejects names that would collide with a
// MajorMUD chat-channel command at edit time so a user can't accidentally
// hijack their own chat. The alias dialog surfaces the conflict inline; no
// runtime check is needed.
public sealed class AliasEngine
{
    private readonly ProfileService? _profile;

    // The loaded character's aliases — empty when no profile is active.
    public ObservableCollection<Alias> Aliases { get; } = new();

    public AliasEngine(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        profile.ProfileSaving += SnapshotForSave;
        if (profile.Current is { } current) LoadFrom(current);
    }

    // Parameterless ctor for tests / in-memory scenarios — no profile persistence.
    public AliasEngine() { }

    // ----- CRUD ----------------------------------------------------------

    // Insert a new alias and persist.
    public void Add(Alias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        Aliases.Add(alias);
        _profile?.Save();
    }

    // Replace an existing alias by reference. Persists. false when the original isn't in the list.
    public bool Replace(Alias original, Alias updated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        int index = Aliases.IndexOf(original);
        if (index < 0) return false;
        Aliases[index] = updated;
        _profile?.Save();
        return true;
    }

    // Remove an alias by reference. Persists. false if not found.
    public bool Remove(Alias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        bool removed = Aliases.Remove(alias);
        if (removed) _profile?.Save();
        return removed;
    }

    // True when an enabled alias with the supplied name already exists.
    // excluding lets the edit dialog skip the alias being edited so it doesn't
    // flag its own name as a duplicate of itself.
    public bool IsDuplicate(string name, Alias? excluding = null)
    {
        foreach (Alias a in Aliases)
        {
            if (ReferenceEquals(a, excluding)) continue;
            if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ----- Expansion -----------------------------------------------------

    // If typed's first word matches an enabled alias, return true + a list of
    // CR-terminated steps to send in place of the typed text. Falls through to
    // false for non-matches, disabled aliases, or empty input — callers send the
    // original text. steps is the expanded command steps (split on ^M / ;,
    // trailing whitespace trimmed per step); empty when the return is false.
    public bool TryExpand(string typed, out IReadOnlyList<string> steps)
    {
        steps = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(typed)) return false;

        // First whitespace-delimited token = the alias name to look up.
        // Preserve everything past that token for {0} / {1..N} substitution.
        ReadOnlySpan<char> input = typed.AsSpan();
        int firstWordEnd = 0;
        while (firstWordEnd < input.Length && !char.IsWhiteSpace(input[firstWordEnd]))
            firstWordEnd++;
        if (firstWordEnd == 0) return false;

        string firstWord = input[..firstWordEnd].ToString();
        Alias? hit = FindEnabled(firstWord);
        if (hit is null) return false;

        // Rest of the line, stripped of the single delimiter whitespace
        // run between the alias name and the args (so `kk  goblin` yields
        // args = "goblin", not " goblin").
        int restStart = firstWordEnd;
        while (restStart < input.Length && char.IsWhiteSpace(input[restStart]))
            restStart++;
        string rest = restStart < input.Length ? input[restStart..].ToString() : string.Empty;

        string substituted = Substitute(hit.Expansion, rest);
        steps = Split(substituted);
        return true;
    }

    private Alias? FindEnabled(string firstWord)
    {
        foreach (Alias a in Aliases)
        {
            if (!a.Enabled) continue;
            if (string.Equals(a.Name, firstWord, StringComparison.OrdinalIgnoreCase)) return a;
        }
        return null;
    }

    // Substitute {0} (whole rest) and {N} (whitespace-split positionals from
    // rest) inside expansion. Missing positionals substitute as the empty
    // string — the per-step trim handles dangling whitespace.
    internal static string Substitute(string expansion, string rest)
    {
        if (string.IsNullOrEmpty(expansion)) return string.Empty;

        // Positional tokens are computed lazily — only when the
        // expansion references at least one {N>=1} placeholder.
        string[]? positional = null;

        StringBuilder sb = new(expansion.Length + rest.Length);
        int i = 0;
        while (i < expansion.Length)
        {
            char c = expansion[i];
            if (c == '{' && TryReadIndex(expansion, i, out int idx, out int next))
            {
                if (idx == 0)
                {
                    sb.Append(rest);
                }
                else
                {
                    positional ??= rest.Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries);
                    if (idx - 1 < positional.Length)
                        sb.Append(positional[idx - 1]);
                    // else: missing — substitute empty string.
                }
                i = next;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    // Look ahead from start (pointing at {) for a well-formed {N} placeholder
    // where N is a non-negative integer. Returns false when the brace doesn't
    // open a valid index — caller treats the brace as a literal character.
    private static bool TryReadIndex(string s, int start, out int index, out int nextIndex)
    {
        index = -1;
        nextIndex = start + 1;
        int end = s.IndexOf('}', start + 1);
        if (end <= start + 1) return false;
        string candidate = s.Substring(start + 1, end - start - 1);
        if (!int.TryParse(candidate, System.Globalization.NumberStyles.None,
                           System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            return false;
        if (parsed < 0) return false;
        index = parsed;
        nextIndex = end + 1;
        return true;
    }

    // Split the substituted expansion on ^M / ; the same way
    // MacroStore.SplitCommandSteps does — keeps the multi-step convention
    // uniform across macros, triggers, aliases, and the login automator.
    // Trailing whitespace per step is trimmed so an absent placeholder doesn't
    // dangle.
    private static IReadOnlyList<string> Split(string substituted)
        => MacroStore.SplitCommandSteps(substituted);

    // ----- Name validation (chat-channel collision) ---------------------

    // MajorMUD chat-channel commands whose namespace aliases must not invade.
    // An alias named any of these (case-insensitive) — or starting with one of
    // ForbiddenFirstChars — would silently hijack the user's chat input.
    private static readonly HashSet<string> _forbiddenExact =
        BuildForbiddenSet();

    // First-character prefix rules: a name starting with any of these is rejected.
    private static readonly char[] ForbiddenFirstChars = { '.', '"', '/' };

    private static HashSet<string> BuildForbiddenSet()
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);

        // Word-channel partial forms — start length per channel matches
        // the shortest typeable command MajorMUD recognises:
        //   gossip   from 3 chars  (gos)
        //   auction  from 3 chars  (auc)
        //   broadcast from 2 chars (br)
        AddPrefixes(set, "gossip",    minLength: 3);
        AddPrefixes(set, "auction",   minLength: 3);
        AddPrefixes(set, "broadcast", minLength: 2);

        // Gangpath is special — not a prefix family of "gangpath" itself.
        // Per user direction, valid command forms are:
        //   broadg / broadga / broadgan / broadgang   (broadcast-prefixed gang variants)
        //   bg / gb                                   (two-char shortcuts)
        foreach (string s in new[] { "broadg", "broadga", "broadgan", "broadgang", "bg", "gb" })
            set.Add(s);

        return set;
    }

    private static void AddPrefixes(HashSet<string> set, string word, int minLength)
    {
        for (int len = minLength; len <= word.Length; len++)
            set.Add(word[..len]);
    }

    // Returns a human-readable conflict reason when name would collide with a
    // chat-channel command; null when the name is free of channel collisions.
    // Caller still needs to check non-empty / no-whitespace / no-duplicate
    // separately.
    public static string? NameConflictReason(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        char first = name[0];
        if (first == '.')  return "Names starting with '.' would intercept the say channel.";
        if (first == '"')  return "Names starting with '\"' would intercept the yell channel.";
        if (first == '/')  return "Names starting with '/' would intercept the telepath channel.";

        if (_forbiddenExact.Contains(name))
        {
            string channel = ClassifyChannel(name);
            return $"'{name}' would intercept the {channel} channel — pick a different name.";
        }
        return null;
    }

    private static string ClassifyChannel(string name)
    {
        string n = name.ToLowerInvariant();
        if ("gossip".StartsWith(n, StringComparison.Ordinal))    return "gossip";
        if ("auction".StartsWith(n, StringComparison.Ordinal))   return "auction";
        if ("broadcast".StartsWith(n, StringComparison.Ordinal)) return "broadcast";
        // Anything else in the exact set is a gangpath form.
        return "gangpath";
    }

    // ----- Profile sync --------------------------------------------------

    private void LoadFrom(CharacterProfile profile)
    {
        Aliases.Clear();
        if (profile.Aliases is null) return;
        foreach (Alias a in profile.Aliases) Aliases.Add(a);
    }

    private void Clear() => Aliases.Clear();

    // Snapshot the live list onto the profile DTO right before save.
    private void SnapshotForSave(CharacterProfile profile)
    {
        profile.Aliases = Aliases.Count == 0 ? null : Aliases.ToList();
    }
}
