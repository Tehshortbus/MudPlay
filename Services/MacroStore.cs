using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// In-memory store of the loaded character's Macro entries. Owns the merge /
// save path against CharacterProfile.Macros + exposes lookup + conflict-check
// helpers used by the edit dialog and the runtime dispatch engine.
public sealed class MacroStore
{
    private readonly ProfileService? _profile;

    // The loaded character's macros — empty when no profile is active.
    public ObservableCollection<Macro> Macros { get; } = new();

    public MacroStore(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        profile.ProfileSaving += SnapshotForSave;
        if (profile.Current is { } current) LoadFrom(current);
    }

    // In-memory store — no profile persistence.
    public MacroStore() { }

    // Insert a new macro and persist. No duplicate-key check here — call FindMatch first.
    public void Add(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        Macros.Add(macro);
        _profile?.Save();
    }

    // Replace an existing macro identified by reference. Persists. No-op if
    // the original isn't in the list.
    public bool Replace(Macro original, Macro updated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        int index = Macros.IndexOf(original);
        if (index < 0) return false;
        Macros[index] = updated;
        _profile?.Save();
        return true;
    }

    // Remove a macro by reference. Persists. false if not found.
    public bool Remove(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        bool removed = Macros.Remove(macro);
        if (removed) _profile?.Save();
        return removed;
    }

    // Find an enabled macro whose chord matches the supplied key combo.
    public Macro? FindMatch(string key, bool ctrl, bool shift, bool alt)
    {
        foreach (Macro m in Macros)
            if (m.Matches(key, ctrl, shift, alt)) return m;
        return null;
    }

    // True when another macro already binds the supplied chord. Optionally
    // excludes a single macro from the check (so editing an existing macro
    // without changing its chord doesn't flag itself as a duplicate of
    // itself).
    public bool IsDuplicate(string key, bool ctrl, bool shift, bool alt, Macro? excluding = null)
    {
        foreach (Macro m in Macros)
        {
            if (ReferenceEquals(m, excluding)) continue;
            if (m.Ctrl  != ctrl)  continue;
            if (m.Shift != shift) continue;
            if (m.Alt   != alt)   continue;
            if (string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Split a macro's Command into the individual lines it should send to the
    // server — on ^M (literal caret-M), ;, OR a real newline. Same multi-step
    // convention every other place in the app uses (login automator,
    // triggers, aliases). A newline counts because a multi-line field (the
    // trigger Response box accepts Return) should send each line as its own
    // command, each terminated with a CR — not one blob missing the Enters
    // between them. Empty / whitespace-only fragments are dropped so a trailing
    // separator doesn't fire an empty line.
    public static IReadOnlyList<string> SplitCommandSteps(string? command)
        => Steps(command, typed: false);

    private static IReadOnlyList<string> Steps(string? command, bool typed)
    {
        if (string.IsNullOrEmpty(command)) return Array.Empty<string>();
        string[] parts = Fragments(command, typed);
        List<string> steps = new(parts.Length);
        foreach (string p in parts)
        {
            string trimmed = p.Trim();
            if (trimmed.Length > 0) steps.Add(trimmed);
        }
        return steps;
    }

    // SplitCommandSteps for a loop waypoint's command, where a blank fragment BETWEEN
    // two separators is a deliberate bare Enter (`pull book;^M` re-shows the room) and
    // comes back as an empty string. A single trailing separator still sends nothing.
    public static IReadOnlyList<string> SplitCommandStepsKeepingEnters(string? command)
    {
        if (string.IsNullOrEmpty(command)) return Array.Empty<string>();
        string[] parts = Fragments(command, typed: false);
        List<string> steps = new(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            string trimmed = parts[i].Trim();
            if (trimmed.Length > 0 || i < parts.Length - 1) steps.Add(trimmed);
        }
        return steps;
    }

    // A newline, ^M (two chars, either case) and `;` each end a command, except a `;`
    // that starts a word: at the start of a line or after a space, with a character
    // right behind it. That one belongs to the game, whose own commands can begin with
    // it (`;o`, `/name @do ;o`), so it is sent as typed. `n;s`, `n; s` and `n ; s`
    // still split; `n; ;o` sends `n` and then `;o`. The `;` in `open chest^M;look`
    // follows a break, not a space, so it stays a separator.
    //
    // In a line the player typed, `;;` right before a character also keeps one `;`
    // for the game: `;;time` sends `;time`, `n;;time` sends `n` then `;time`. Stored
    // command lists don't read it that way, since a doubled separator there has
    // always been an empty step (a bare Enter in a loop waypoint).
    private static string[] Fragments(string command, bool typed)
    {
        string[] lines = command
            .Replace("^M", HardBreak, StringComparison.OrdinalIgnoreCase)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        List<string> parts = new(lines.Length);
        foreach (string line in lines)
        {
            int start = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c != ';' && c != HardBreak[0]) continue;
                if (c == ';' && typed && IsDoubledBeforeText(line, i))
                {
                    // The first `;` ends the command before it; the second opens the next.
                    parts.Add(line[start..i]);
                    start = ++i;
                    continue;
                }
                if (c == ';' && StartsAWord(line, i)) continue;
                parts.Add(line[start..i]);
                start = i + 1;
            }
            parts.Add(line[start..]);
        }
        return parts.ToArray();
    }

    // Stands in for ^M while a command is split: a break that is neither a `;` nor a
    // line start, and that no command can contain.
    private const string HardBreak = "\u0001";

    private static bool StartsAWord(string line, int semicolon)
    {
        bool opensWord = semicolon == 0 || char.IsWhiteSpace(line[semicolon - 1]);
        return opensWord && IsText(line, semicolon + 1);
    }

    private static bool IsDoubledBeforeText(string line, int semicolon) =>
        semicolon + 1 < line.Length && line[semicolon + 1] == ';' && IsText(line, semicolon + 2);

    private static bool IsText(string line, int index) =>
        index < line.Length && line[index] is not (';' or '\u0001') && !char.IsWhiteSpace(line[index]);

    // Split a line the PLAYER just typed (terminal / conversation input) into
    // the commands it should send. Lets a player rapid-fire several commands
    // from one line — "sea n;sea n;n" — using the same ';' / '^M' convention as
    // macros. A line with no separator is returned verbatim (untrimmed) as a
    // single element, so ordinary input — including a blank line, whose lone CR
    // still matters at a prompt — is sent exactly as before. This is the live-
    // input wrapper around SplitCommandSteps; engines never route through it.
    public static IReadOnlyList<string> SplitTypedInput(string text)
    {
        if (text.IndexOf(';') < 0 && !text.Contains("^M", StringComparison.OrdinalIgnoreCase))
            return new[] { text };
        IReadOnlyList<string> steps = Steps(text, typed: true);
        return steps.Count > 0 ? steps : new[] { text };
    }

    // Default numpad macros every new profile gets so the user can walk
    // around immediately — the conventional numpad → compass-direction layout
    // (8 = north, 2 = south, 0 = up, decimal = down, corners = diagonals).
    public static IReadOnlyList<Macro> DefaultMacros() => new[]
    {
        new Macro(Key: "NumPad8", Ctrl: false, Shift: false, Alt: false, Command: "n",  Enabled: true),
        new Macro(Key: "NumPad2", Ctrl: false, Shift: false, Alt: false, Command: "s",  Enabled: true),
        new Macro(Key: "NumPad4", Ctrl: false, Shift: false, Alt: false, Command: "w",  Enabled: true),
        new Macro(Key: "NumPad6", Ctrl: false, Shift: false, Alt: false, Command: "e",  Enabled: true),
        new Macro(Key: "NumPad9", Ctrl: false, Shift: false, Alt: false, Command: "ne", Enabled: true),
        new Macro(Key: "NumPad7", Ctrl: false, Shift: false, Alt: false, Command: "nw", Enabled: true),
        new Macro(Key: "NumPad3", Ctrl: false, Shift: false, Alt: false, Command: "se", Enabled: true),
        new Macro(Key: "NumPad1", Ctrl: false, Shift: false, Alt: false, Command: "sw", Enabled: true),
        new Macro(Key: "NumPad0", Ctrl: false, Shift: false, Alt: false, Command: "u",  Enabled: true),
        new Macro(Key: "Decimal", Ctrl: false, Shift: false, Alt: false, Command: "d",  Enabled: true),
    };

    // ----- Profile sync ---------------------------------------------------

    private void LoadFrom(CharacterProfile profile)
    {
        Macros.Clear();
        if (profile.Macros is { Count: > 0 } stored)
        {
            foreach (Macro m in stored) Macros.Add(m);
        }
        else
        {
            // Fresh / never-saved profile: seed numpad directional macros so
            // the user can move the moment they connect.
            foreach (Macro m in DefaultMacros()) Macros.Add(m);
        }
    }

    private void Clear() => Macros.Clear();

    // Snapshot the live list onto the profile DTO right before save.
    private void SnapshotForSave(CharacterProfile profile)
    {
        profile.Macros = Macros.Count == 0 ? null : Macros.ToList();
    }
}
