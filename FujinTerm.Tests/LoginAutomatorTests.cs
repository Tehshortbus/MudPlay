using System.Collections.Concurrent;
using System.Text;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class LoginAutomatorTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static (LoginAutomator a, ConcurrentQueue<string> sent) Build(
        params AutomationStep[] steps)
    {
        ConcurrentQueue<string> sent = new();
        LoginAutomator a = new(
            steps,
            (text, _) => { sent.Enqueue(text); return Task.CompletedTask; });
        return (a, sent);
    }

    [Fact]
    public void NoSteps_FiresLoggedInImmediately()
    {
        (LoginAutomator a, _) = Build();
        bool done = false;
        a.LoggedIntoGame += () => done = true;
        a.Start();
        Assert.True(done);
    }

    [Fact]
    public async Task LiteralPattern_MatchesAndSends()
    {
        AutomationStep s = new(
            "Login:", MenuStepMatchType.Literal,
            () => Task.FromResult<string?>("alice\r"), 30);
        (LoginAutomator a, var sent) = Build(s);
        a.Start();
        a.Feed(Ascii("Welcome\r\nLogin:"));

        // Async send hop — let the continuation drain.
        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("alice\r", sentVal);
    }

    [Fact]
    public async Task LiteralPattern_IsCaseInsensitive()
    {
        AutomationStep s = new(
            "PASSWORD:", MenuStepMatchType.Literal,
            () => Task.FromResult<string?>("hunter2\r"), 30);
        (LoginAutomator a, var sent) = Build(s);
        a.Start();
        a.Feed(Ascii("password:"));

        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("hunter2\r", sentVal);
    }

    [Fact]
    public async Task CsiSequences_StrippedBeforeMatching()
    {
        AutomationStep s = new(
            "Main Menu:", MenuStepMatchType.Literal,
            () => Task.FromResult<string?>("G\r"), 30);
        (LoginAutomator a, var sent) = Build(s);
        a.Start();
        // ANSI-coloured prompt — the CSI escape must not block the match.
        a.Feed(Ascii("\x1b[1;33mMain Menu:\x1b[0m "));

        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("G\r", sentVal);
    }

    [Fact]
    public async Task WildcardPattern_StarMatchesAnyRun()
    {
        AutomationStep s = new(
            "Press*continue", MenuStepMatchType.Wildcard,
            () => Task.FromResult<string?>("\r"), 30);
        (LoginAutomator a, var sent) = Build(s);
        a.Start();
        a.Feed(Ascii("[Press any key to continue]"));

        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("\r", sentVal);
    }

    [Fact]
    public async Task RegexPattern_Captures()
    {
        AutomationStep s = new(
            @"Enter\s+choice\s*:", MenuStepMatchType.Regex,
            () => Task.FromResult<string?>("3\r"), 30);
        (LoginAutomator a, var sent) = Build(s);
        a.Start();
        a.Feed(Ascii("\r\nEnter choice : "));

        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("3\r", sentVal);
    }

    [Fact]
    public async Task MultipleSteps_RunInOrder_WithinOneFeed()
    {
        AutomationStep s1 = new(
            "Login:", MenuStepMatchType.Literal,
            () => Task.FromResult<string?>("alice\r"), 30);
        AutomationStep s2 = new(
            "Password:", MenuStepMatchType.Literal,
            () => Task.FromResult<string?>("hunter2\r"), 30);
        AutomationStep s3 = new(
            "Menu:", MenuStepMatchType.Literal,
            () => Task.FromResult<string?>("G\r"), 30);

        (LoginAutomator a, var sent) = Build(s1, s2, s3);
        bool done = false;
        a.LoggedIntoGame += () => done = true;
        a.Start();

        // All three patterns arrive in one buffer.
        a.Feed(Ascii("Login: alice\r\nPassword: ********\r\nMain Menu:\r\n"));

        await Task.Delay(60);
        Assert.Equal(3, sent.Count);
        Assert.True(sent.TryDequeue(out string? first));  Assert.Equal("alice\r",   first);
        Assert.True(sent.TryDequeue(out string? second)); Assert.Equal("hunter2\r", second);
        Assert.True(sent.TryDequeue(out string? third));  Assert.Equal("G\r",       third);
        Assert.True(done);
    }

    [Fact]
    public async Task StepTimeout_FiresAborted()
    {
        AutomationStep s = new(
            "NeverArrives", MenuStepMatchType.Literal,
            () => Task.FromResult<string?>("x"), 1);
        (LoginAutomator a, _) = Build(s);
        string? abortReason = null;
        a.Aborted += r => abortReason = r;
        a.Start();
        a.Feed(Ascii("Some unrelated banner text\r\n"));

        // Step timeout is 1s; allow some slack.
        await Task.Delay(1500);
        Assert.NotNull(abortReason);
        Assert.Contains("timed out", abortReason);
    }

    [Fact]
    public async Task MissingPassword_AbortsStep()
    {
        AutomationStep s = new(
            "Password:", MenuStepMatchType.Literal,
            () => Task.FromResult<string?>(null), 30);
        (LoginAutomator a, _) = Build(s);
        string? abortReason = null;
        a.Aborted += r => abortReason = r;
        a.Start();
        a.Feed(Ascii("Password:"));

        await Task.Delay(20);
        Assert.NotNull(abortReason);
    }

    [Fact]
    public async Task BuildSteps_NullCredentials_ReturnsNull()
    {
        await Task.CompletedTask;
        Models.Settings.BbsProfile bbs = new() { Name = "BBS", Host = "example.com" };
        EncryptedFileCredentialStore store = new(
            Path.Combine(Path.GetTempPath(), $"FT-key-{Guid.NewGuid():N}"),
            Path.Combine(Path.GetTempPath(), $"FT-cred-{Guid.NewGuid():N}"));
        IReadOnlyList<AutomationStep>? steps = LoginAutomator.BuildSteps(bbs, null, store);
        Assert.Null(steps);
    }

    [Fact]
    public async Task BuildSteps_BlankUsername_ReturnsNull()
    {
        await Task.CompletedTask;
        Models.Settings.BbsProfile bbs = new() { Name = "BBS", Host = "example.com" };
        BbsCredentials creds = new() { Username = "  " };
        EncryptedFileCredentialStore store = new(
            Path.Combine(Path.GetTempPath(), $"FT-key-{Guid.NewGuid():N}"),
            Path.Combine(Path.GetTempPath(), $"FT-cred-{Guid.NewGuid():N}"));
        IReadOnlyList<AutomationStep>? steps = LoginAutomator.BuildSteps(bbs, creds, store);
        Assert.Null(steps);
    }

    [Fact]
    public async Task BuildSteps_WithUsernameAndMenuSteps_ReturnsLoginPasswordPlusMenuSteps()
    {
        string scratchDir = Path.Combine(Path.GetTempPath(), $"FT-cred-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDir);
        try
        {
            EncryptedFileCredentialStore store = new(
                Path.Combine(scratchDir, ".key"),
                Path.Combine(scratchDir, "creds.dat"));
            await store.SetAsync("bbs:foo:char:password", "hunter2");

            Models.Settings.BbsProfile bbs = new()
            {
                Name = "Foo",
                Host = "foo.example.com",
                LoginPromptPattern = "Login:",
                PasswordPromptPattern = "Password:",
            };
            BbsCredentials creds = new()
            {
                Username = "alice",
                PasswordCredentialId = "bbs:foo:char:password",
                MenuNavSteps =
                {
                    new MenuStep { WaitForPattern = "Main Menu:", Send = "G\\r", TimeoutSeconds = 20 },
                    new MenuStep { WaitForPattern = "Enter Realm:", Send = "\\r", TimeoutSeconds = 10 },
                },
            };

            IReadOnlyList<AutomationStep>? steps = LoginAutomator.BuildSteps(bbs, creds, store);
            Assert.NotNull(steps);
            Assert.Equal(4, steps!.Count);
            Assert.Equal("Login:", steps[0].WaitForPattern);
            Assert.Equal("Password:", steps[1].WaitForPattern);
            Assert.Equal("Main Menu:", steps[2].WaitForPattern);
            Assert.Equal("Enter Realm:", steps[3].WaitForPattern);

            string? loginSend = await steps[0].ResolveSend();
            Assert.Equal("alice\r", loginSend);

            string? passwordSend = await steps[1].ResolveSend();
            Assert.Equal("hunter2\r", passwordSend);

            string? menu1Send = await steps[2].ResolveSend();
            Assert.Equal("G\r", menu1Send);
        }
        finally
        {
            try { Directory.Delete(scratchDir, recursive: true); } catch { }
        }
    }
}
