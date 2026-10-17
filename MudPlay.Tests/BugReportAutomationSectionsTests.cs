using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The user's triggers, aliases and macros in a report (a definition holding an
// account detail is left out whole), and the death history.
public sealed class BugReportAutomationSectionsTests
{
    private static readonly BugReportScrub.Scrubber Scrubber = BugReportScrub.Build(
        [new(BugReportScrub.LoginNameLabel, "loginzed"), new(BugReportScrub.LoginPasswordLabel, "Pa55w0rd!x")], []);

    private static readonly BugReportChatCensor Censor =
        StandInCensor.Of("flibber");

    [Fact]
    public void UserAutomation_ListsEveryDefinition_AndOmitsOneThatHoldsAnAccountDetail()
    {
        string text = BugReportAutomationSections.UserAutomation(
            [
                new Trigger("heal up", true, default, default, "You are bleeding", "cast heal"),
                new Trigger("login helper", true, default, default, "User-ID", "LOGINZED"),
                new Trigger("rude", false, default, default, "gossips: hi", "gos you flibber"),
            ],
            [
                new Alias("gh", true, "goto home"),
                new Alias("pw", true, "say pa55w0rd!x"),
            ],
            [new Macro("F5", Ctrl: true, Shift: false, Alt: false, "par", Enabled: true)],
            Scrubber, Censor);

        Assert.Contains("**Triggers** (3)", text);
        Assert.Contains("`You are bleeding` → `cast heal`", text);
        Assert.Contains("rude [off]", text);
        Assert.Contains("`gos you [censored]`", text);
        Assert.Contains("- gh → `goto home`", text);
        Assert.Contains("- Ctrl+F5 → `par`", text);
        // Two definitions held an account detail: gone whole, name and all.
        Assert.Equal(2, text.Split(BugReportScrub.OmittedDefinition).Length - 1);
        Assert.DoesNotContain("login helper", text);
        Assert.DoesNotContain("loginzed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pa55w0rd", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserAutomation_NothingDefined_SaysNone()
    {
        string text = BugReportAutomationSections.UserAutomation([], [], [], Scrubber, Censor);
        Assert.Equal(3, text.Split("_(none)_").Length - 1);
    }

    [Fact]
    public void DeathHistory_ListsOldestFirstWithStatus()
    {
        List<DeathRecord> records =
        [
            new() { RecordNumber = 1, At = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), LivesRemaining = 8, Status = DeathRecoveryStatus.Recovered },
            new() { RecordNumber = 2, At = new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero), LivesRemaining = 7,
                    RoomName = "A dark cave", UnrecoveredItems = ["a ring"], GameData = "alpha" },
        ];

        string text = BugReportAutomationSections.DeathHistory(records);

        Assert.Contains("The last 2 of 2 recorded.", text);
        Assert.True(text.IndexOf("- #1 ", StringComparison.Ordinal) < text.IndexOf("- #2 ", StringComparison.Ordinal));
        Assert.Contains("8 lives left — Recovered; game data not recorded (taken as the active one's)", text);
        Assert.Contains("(A dark cave), 7 lives left — Active; still missing 1 item(s); recorded on game data 'alpha'", text);
    }

    [Fact]
    public void DeathHistory_None_SaysSo() =>
        Assert.Contains("no deaths recorded", BugReportAutomationSections.DeathHistory([]));
}
