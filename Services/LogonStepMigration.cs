using System.IO;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// Releases through 3.206.4 kept a copy of the logon steps on every character
// (BbsCredentials, under its legacy names). Now the board holds the steps its
// characters share (BbsProfile.LogonSteps, issue #1219) and a character can keep
// its own (BbsCredentials.LogonSteps): one account's characters can need different
// answers at the board's menus, a character slot or a realm.
//
// MoveToBoards gives each board, once, the steps its characters had: the ones the
// player set over ones the client filled in, then the ones most of its characters
// had, then the most recently saved character's. KeepCharactersOwn then gives every
// character whose old copy differs from its board's steps that copy as its own, so
// it logs on as it did before. 3.206.5 ran only the first, and dropped each old copy
// the next time its character was saved: a character saved since lost it.
public static class LogonStepMigration
{
    private sealed record Copy(string Character, List<MenuStep> Steps, string? AutoSetup, DateTime Saved)
    {
        public string Key => StepsKey(Steps);
        public bool SetByPlayer => !(BuiltInBbses.Named(AutoSetup)?.HasSteps(Steps) ?? false);
    }

    private static string StepsKey(IEnumerable<MenuStep> steps) =>
        string.Join("\n", steps.Select(s => s.WaitForPattern + "\t" + s.Send));

    // Returns the boards given steps.
    public static IReadOnlyList<string> MoveToBoards(BbsProfileStore boards, IEnumerable<ProfileRef> characters, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(boards);
        ArgumentNullException.ThrowIfNull(characters);
        Dictionary<string, List<Copy>> byBoard = new(StringComparer.OrdinalIgnoreCase);
        foreach ((ProfileRef character, string path, CharacterProfile profile) in Readable(characters, log))
        {
            if (profile.BbsCredentials is not { } logins) continue;
            DateTime saved = File.GetLastWriteTimeUtc(path);
            foreach ((string board, BbsCredentials login) in logins)
            {
                if (login.LegacyLogonSteps is not { Count: > 0 } steps) continue;
                if (!byBoard.TryGetValue(board, out List<Copy>? copies)) byBoard[board] = copies = [];
                copies.Add(new Copy(character.Name, steps, login.LegacyAutoLogonSetup, saved));
            }
        }

        List<string> moved = [];
        foreach (string name in boards.ListNames())
        {
            if (!byBoard.TryGetValue(name, out List<Copy>? copies) || boards.Get(name) is not { } board
                || board.LogonSteps is not null)
                continue;
            List<Copy> candidates = copies.Any(c => c.SetByPlayer) ? copies.Where(c => c.SetByPlayer).ToList() : copies;
            Copy chosen = candidates
                .GroupBy(c => c.Key)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Max(c => c.Saved))
                .First()
                .MaxBy(c => c.Saved)!;
            board.LogonSteps = chosen.Steps;
            board.AutoLogonSetup = chosen.SetByPlayer ? null : chosen.AutoSetup;
            boards.Save(board);
            moved.Add(name);
            log?.Info("BBS", $"'{name}': the logon steps are the board's now, the default for every character on it; "
                + $"it takes {chosen.Character}'s ({chosen.Steps.Count} step(s)).");
        }
        return moved;
    }

    // Returns the characters given their own steps, as "character on board".
    public static IReadOnlyList<string> KeepCharactersOwn(BbsProfileStore boards, IEnumerable<ProfileRef> characters, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(boards);
        ArgumentNullException.ThrowIfNull(characters);
        List<string> kept = [];
        foreach ((ProfileRef character, string path, CharacterProfile profile) in Readable(characters, log))
        {
            if (profile.BbsCredentials is not { } logins) continue;
            bool changed = false;
            foreach ((string name, BbsCredentials login) in logins)
            {
                if (login.LegacyLogonSteps is not { Count: > 0 } steps || login.LogonSteps is not null
                    || boards.Peek(name) is not { } board)
                    continue;
                if (board.LogonSteps is { } shared && StepsKey(shared) == StepsKey(steps)) continue;
                login.LogonSteps = steps;
                changed = true;
                kept.Add($"{character.Name} on {name}");
                log?.Info("BBS", $"'{character.Name}' keeps its own logon steps on '{name}' ({steps.Count} step(s)), "
                    + "which differ from the board's.");
            }
            if (changed) JsonStore.Save(path, profile);
        }
        return kept;
    }

    private static IEnumerable<(ProfileRef Character, string Path, CharacterProfile Profile)> Readable(
        IEnumerable<ProfileRef> characters, LogService? log)
    {
        foreach (ProfileRef character in characters)
        {
            string path = AppPaths.CharacterProfileFile(character.Bbs, character.Name);
            CharacterProfile? profile;
            try
            {
                profile = JsonStore.Load<CharacterProfile>(path);
            }
            catch (InvalidDataException ex)
            {
                log?.Warn("BBS", $"Logon steps: character '{character.Name}' on '{character.Bbs}' can't be read, so its steps stay where they are ({ex.Message}).");
                continue;
            }
            if (profile is not null) yield return (character, path, profile);
        }
    }
}
