using System.IO;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// Moves the logon steps, once, from the characters that used to carry a copy each
// (BbsCredentials, releases through 3.206.x) onto the board they log on to
// (BbsProfile.LogonSteps), so every character on a board shares them (issue #1219).
// When a board's characters had different steps, the board takes the ones the
// player set over ones the client filled in, then the ones most of its characters
// had, then the most recently saved character's; the others are named in the log.
// A board that already has steps keeps them. The characters' files are left as
// they are: the old copy is dropped the next time each one is saved.
public static class LogonStepMigration
{
    private sealed record Copy(string Character, List<MenuStep> Steps, string? AutoSetup, DateTime Saved)
    {
        public string Key => string.Join("\n", Steps.Select(s => s.WaitForPattern + "\t" + s.Send));
        public bool SetByPlayer => !(BuiltInBbses.Named(AutoSetup)?.HasSteps(Steps) ?? false);
    }

    // Returns the boards given steps.
    public static IReadOnlyList<string> MoveToBoards(BbsProfileStore boards, IEnumerable<ProfileRef> characters, LogService? log)
    {
        ArgumentNullException.ThrowIfNull(boards);
        ArgumentNullException.ThrowIfNull(characters);
        Dictionary<string, List<Copy>> byBoard = new(StringComparer.OrdinalIgnoreCase);
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
            if (profile?.BbsCredentials is not { } logins) continue;
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

            List<string> differing = copies.Where(c => c.Key != chosen.Key).Select(c => c.Character)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToList();
            log?.Info("BBS", $"'{name}': the logon steps are the board's now, shared by every character on it; "
                + $"it takes {chosen.Character}'s ({chosen.Steps.Count} step(s)).");
            if (differing.Count > 0)
                log?.Warn("BBS", $"'{name}': {string.Join(", ", differing)} had other logon steps and now log on with "
                    + $"{chosen.Character}'s; change them on Settings → BBS + Display if that's wrong.");
        }
        return moved;
    }
}
