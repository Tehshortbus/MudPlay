using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;

namespace FujinTerm.Services;

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

    // Parameterless ctor for tests / in-memory scenarios — no profile persistence.
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

    // Find an enabled macro whose chord matches the supplied key combo. Used
    // by the runtime dispatch path when a keystroke fires on TerminalControl
    // / ConversationWindow's input.
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
    // server — on either ^M (literal caret-M) or ;. Same multi-step
    // convention every other place in the app uses (login automator,
    // triggers, aliases). Empty / whitespace-only fragments are dropped so a
    // trailing separator doesn't fire an empty line.
    public static IReadOnlyList<string> SplitCommandSteps(string? command)
    {
        if (string.IsNullOrEmpty(command)) return Array.Empty<string>();
        // Split on `^M` (literal) OR `;`. ^M is two characters so we
        // pre-substitute it with `;` before the single-char split.
        string normalized = command.Replace("^M", ";", StringComparison.Ordinal);
        string[] parts = normalized.Split(';');
        List<string> steps = new(parts.Length);
        foreach (string p in parts)
        {
            string trimmed = p.Trim();
            if (trimmed.Length > 0) steps.Add(trimmed);
        }
        return steps;
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
