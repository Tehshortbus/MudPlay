using System.IO;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The once-per-install move of the logon steps from the characters that each kept a
// copy (profiles saved through 3.206.x) onto their board (issue #1219). The boards
// and characters are written under the scratch data root, the characters as an old
// release wrote them, and deleted in Dispose.
public sealed class LogonStepMigrationTests : IDisposable
{
    private readonly string _tag = Path.GetRandomFileName().Replace(".", "");
    private readonly List<string> _boards = [];
    private readonly LogService _log = new();

    public LogonStepMigrationTests() => ScratchRoot.Require();

    public void Dispose()
    {
        foreach (string board in _boards) new BbsProfileStore().Delete(board);
    }

    private string Board(BbsProfile? withSteps = null)
    {
        string name = $"!mudplay-test-logonmove-{_boards.Count}-{_tag}";
        _boards.Add(name);
        BbsProfile board = withSteps ?? new BbsProfile();
        board.Name = name;
        new BbsProfileStore().Save(board);
        return name;
    }

    // A character as an old release saved it: its login on board carrying its own
    // logon steps (the last one's response sent), and autoSetup when the client
    // filled them in.
    private ProfileRef Character(string board, string name, string lastSend, string? autoSetup = null, int minutesAgo = 0)
    {
        string path = AppPaths.CharacterProfileFile(board, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string auto = autoSetup is null ? "null" : $"\"{autoSetup}\"";
        File.WriteAllText(path, $$"""
            {
              "Name": "{{name}}",
              "BbsCredentials": {
                "{{board}}": {
                  "EncryptedUsername": "enc-{{name}}",
                  "MenuNavSteps": [
                    { "WaitForPattern": "Otherwise type \"new\":", "Send": "{username}" },
                    { "WaitForPattern": "Enter your password:", "Send": "{password}" },
                    { "WaitForPattern": "Main System Menu (TOP)", "Send": "{{lastSend}}" }
                  ],
                  "AutoLogonSetup": {{auto}}
                }
              }
            }
            """);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
        return new ProfileRef(board, name);
    }

    private static string? LastSend(string board) => new BbsProfileStore().Get(board)!.LogonSteps?[^1].Send;

    [Fact]
    public void ACharactersSteps_BecomeItsBoards_AndTheCharacterDropsThemOnItsNextSave()
    {
        string board = Board();
        ProfileRef alpha = Character(board, "Alpha", "g");

        IReadOnlyList<string> moved = LogonStepMigration.MoveToBoards(new BbsProfileStore(), [alpha], _log);

        Assert.Equal([board], moved);
        Assert.Equal("g", LastSend(board));
        Assert.Null(new BbsProfileStore().Get(board)!.AutoLogonSetup);

        ProfileService profiles = TestProfiles.Service();
        profiles.Load(board, "Alpha");
        Assert.Equal("enc-Alpha", profiles.Current!.BbsCredentials![board].EncryptedUsername);
        profiles.Save();
        Assert.DoesNotContain("MenuNavSteps", File.ReadAllText(AppPaths.CharacterProfileFile(board, "Alpha")));
    }

    // When the characters disagree: steps the player set beat ones the client filled
    // in, then the steps most of them had, then the most recently saved character's.
    [Fact]
    public void CharactersThatDisagree_TheBoardTakesThePlayersMostSharedThenNewestSteps()
    {
        string setBeatsFilled = Board();
        ProfileRef[] a =
        [
            Character(setBeatsFilled, "A1", "m", autoSetup: "Stock", minutesAgo: 0),
            Character(setBeatsFilled, "A2", "x", minutesAgo: 30),
        ];
        string mostShared = Board();
        ProfileRef[] b =
        [
            Character(mostShared, "B1", "x", minutesAgo: 0),
            Character(mostShared, "B2", "y", minutesAgo: 10),
            Character(mostShared, "B3", "y", minutesAgo: 20),
        ];
        string newest = Board();
        ProfileRef[] c =
        [
            Character(newest, "C1", "x", minutesAgo: 20),
            Character(newest, "C2", "y", minutesAgo: 5),
        ];

        LogonStepMigration.MoveToBoards(new BbsProfileStore(), [.. a, .. b, .. c], _log);

        Assert.Equal("x", LastSend(setBeatsFilled));
        Assert.Equal("y", LastSend(mostShared));
        Assert.Equal("y", LastSend(newest));
        Assert.Contains(_log.Snapshot(), e => e.Message.Contains("B1 had other logon steps", StringComparison.Ordinal));
    }

    // Only the client's own fill-in on file: the board keeps it as the client's, so
    // the setup each realm's data calls for still takes its place at connect.
    [Fact]
    public void OnlyStepsTheClientFilledIn_StayTheClients()
    {
        string board = Board();
        LogonStepMigration.MoveToBoards(new BbsProfileStore(), [Character(board, "Alpha", "m", autoSetup: "Stock")], _log);

        Assert.Equal("Stock", new BbsProfileStore().Get(board)!.AutoLogonSetup);
    }

    [Fact]
    public void ABoardWithStepsAlready_KeepsThem()
    {
        string board = Board(new BbsProfile { LogonSteps = [new() { WaitForPattern = "Menu:", Send = "kept" }] });

        Assert.Empty(LogonStepMigration.MoveToBoards(new BbsProfileStore(), [Character(board, "Alpha", "g")], _log));
        Assert.Equal("kept", LastSend(board));
    }
}
