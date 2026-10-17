using System.Collections.Concurrent;
using System.Text;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class LoginAutomatorTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    // The send and password delegates here finish at once, so a matched step is
    // sent, and the next one tried, inside the Feed that matched it: each test reads
    // the outcome on the next line, with no wait on the clock.
    private static (LoginAutomator a, ConcurrentQueue<string> sent) Build(
        string? username = null,
        Func<Task<string?>>? resolvePassword = null,
        params AutomationStep[] steps)
    {
        ConcurrentQueue<string> sent = new();
        LoginAutomator a = new(
            steps,
            username,
            resolvePassword,
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
    public void LiteralPattern_AutoAppendsCarriageReturn()
    {
        AutomationStep s = new("Main Menu:", "G");
        (LoginAutomator a, var sent) = Build(steps: s);
        a.Start();
        a.Feed(Ascii("Main Menu: "));

        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("G\r", sentVal);
    }

    // A bulletin pager mid-login gets Enter and the step keeps waiting — once per
    // prompt, and the step still matches when its own text arrives.
    [Fact]
    public void PagerPrompt_SendsEnter_AndKeepsWaitingOnTheStep()
    {
        AutomationStep s = new("Main Menu:", "G");
        (LoginAutomator a, var sent) = Build(steps: s);
        a.Start();

        a.Feed(Ascii("News...\r\n(N)onstop, (Q)uit, or (C)ontinue? "));
        Assert.Equal(new[] { "\r" }, sent.ToArray());

        a.Feed(Ascii("more news\r\n"));   // no second prompt → no second Enter
        Assert.Single(sent);

        a.Feed(Ascii("Main Menu: "));
        Assert.Equal(new[] { "\r", "G\r" }, sent.ToArray());
    }

    // A step authored for the pager text answers it itself; no extra Enter.
    [Fact]
    public void PagerPrompt_AnAuthoredStepForItWins()
    {
        AutomationStep s = new("(C)ontinue?", "N");
        (LoginAutomator a, var sent) = Build(steps: s);
        a.Start();
        a.Feed(Ascii("(N)onstop, (Q)uit, or (C)ontinue? "));
        Assert.Equal(new[] { "N\r" }, sent.ToArray());
    }

    [Fact]
    public void StepAdvanced_FiresOncePerMatchedStep()
    {
        // Each matched+sent step raises StepAdvanced (the signal that keeps the
        // main-menu auto-entry window fresh while a login is progressing).
        AutomationStep s1 = new("Login:", "alice");
        AutomationStep s2 = new("Password:", "pw");
        (LoginAutomator a, _) = Build(steps: new[] { s1, s2 });
        int advances = 0;
        a.StepAdvanced += () => advances++;
        a.Start();

        a.Feed(Ascii("Login:"));
        Assert.Equal(1, advances);

        a.Feed(Ascii("Password:"));
        Assert.Equal(2, advances);
    }

    [Fact]
    public void EmptySend_StillSendsCarriageReturn()
    {
        AutomationStep s = new("Press any key", "");
        (LoginAutomator a, var sent) = Build(steps: s);
        a.Start();
        a.Feed(Ascii("[Press any key to continue]"));

        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("\r", sentVal);
    }

    [Theory]
    [InlineData("{user}")]
    [InlineData("{userid}")]
    [InlineData("{username}")]
    public void UsernameAliases_AllSubstitute(string token)
    {
        AutomationStep s = new("Login:", token);
        (LoginAutomator a, var sent) = Build(username: "alice", steps: s);
        a.Start();
        a.Feed(Ascii("Login:"));

        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("alice\r", sentVal);
    }

    [Theory]
    [InlineData("{pass}")]
    [InlineData("{pswd}")]
    [InlineData("{passwd}")]
    [InlineData("{password}")]
    public void PasswordAliases_AllSubstitute(string token)
    {
        AutomationStep s = new("Password:", token);
        (LoginAutomator a, var sent) = Build(
            resolvePassword: () => Task.FromResult<string?>("hunter2"), steps: s);
        a.Start();
        a.Feed(Ascii("Password:"));

        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("hunter2\r", sentVal);
    }

    [Fact]
    public void Placeholder_IsCaseInsensitive()
    {
        AutomationStep s = new("Login:", "{USERNAME}");
        (LoginAutomator a, var sent) = Build(username: "alice", steps: s);
        a.Start();
        a.Feed(Ascii("Login:"));

        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("alice\r", sentVal);
    }

    [Fact]
    public void CsiSequences_StrippedBeforeMatching()
    {
        AutomationStep s = new("Main Menu:", "G");
        (LoginAutomator a, var sent) = Build(steps: s);
        a.Start();
        a.Feed(Ascii("\x1b[1;33mMain Menu:\x1b[0m "));

        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("G\r", sentVal);
    }

    [Fact]
    public void FullLoginFlow_RunsInOrder()
    {
        AutomationStep s1 = new("Login:", "{username}");
        AutomationStep s2 = new("Password:", "{password}");
        AutomationStep s3 = new("Main Menu:", "G");

        (LoginAutomator a, var sent) = Build(
            username: "alice",
            resolvePassword: () => Task.FromResult<string?>("hunter2"),
            steps: new[] { s1, s2, s3 });
        bool done = false;
        a.LoggedIntoGame += () => done = true;
        a.Start();
        a.Feed(Ascii("Login: alice\r\nPassword: ********\r\nMain Menu:\r\n"));

        Assert.Equal(3, sent.Count);
        Assert.True(sent.TryDequeue(out string? first));  Assert.Equal("alice\r",   first);
        Assert.True(sent.TryDequeue(out string? second)); Assert.Equal("hunter2\r", second);
        Assert.True(sent.TryDequeue(out string? third));  Assert.Equal("G\r",       third);
        Assert.True(done);
    }

    [Fact]
    public void UsernameLock_RefusesSecondSendAfterAcceptance()
    {
        AutomationStep s1 = new("Login:", "{username}");
        AutomationStep s2 = new("Password:", "secret");
        AutomationStep s3 = new("Trick:", "{username}");

        (LoginAutomator a, var sent) = Build(
            username: "alice",
            steps: new[] { s1, s2, s3 });
        string? abortReason = null;
        a.Aborted += r => abortReason = r;
        a.Start();
        a.Feed(Ascii("Login:Password:Trick:"));

        Assert.True(sent.TryDequeue(out string? first));   Assert.Equal("alice\r",  first);
        Assert.True(sent.TryDequeue(out string? second));  Assert.Equal("secret\r", second);
        Assert.False(sent.TryDequeue(out _));
        Assert.NotNull(abortReason);
        Assert.Contains("username already accepted", abortReason);
    }

    [Fact]
    public void PasswordLock_RefusesSecondSendAfterAcceptance()
    {
        AutomationStep s1 = new("Password:", "{password}");
        AutomationStep s2 = new("Welcome:", "");
        AutomationStep s3 = new("Repeat:", "{password}");

        (LoginAutomator a, var sent) = Build(
            resolvePassword: () => Task.FromResult<string?>("hunter2"),
            steps: new[] { s1, s2, s3 });
        string? abortReason = null;
        a.Aborted += r => abortReason = r;
        a.Start();
        a.Feed(Ascii("Password:Welcome:Repeat:"));

        Assert.True(sent.TryDequeue(out string? first));   Assert.Equal("hunter2\r", first);
        Assert.True(sent.TryDequeue(out string? second));  Assert.Equal("\r",        second);
        Assert.False(sent.TryDequeue(out _));
        Assert.NotNull(abortReason);
        Assert.Contains("password already accepted", abortReason);
    }

    [Fact]
    public void UsernamePlaceholder_NoUsernameSaved_SkipsTheStep_WithoutAborting()
    {
        AutomationStep s = new("Login:", "{username}");
        (LoginAutomator a, ConcurrentQueue<string> sent) = Build(username: null, steps: s);
        string? abortReason = null, skipReason = null;
        a.Aborted += r => abortReason = r;
        a.StepSkipped += r => skipReason = r;
        a.Start();
        a.Feed(Ascii("Login:"));

        Assert.Null(abortReason);
        Assert.Contains("no username", skipReason);
        Assert.False(sent.TryDequeue(out _));
    }

    [Fact]
    public void PasswordPlaceholder_NoPasswordSaved_SkipsTheStep_WithoutAborting()
    {
        AutomationStep s = new("Password:", "{password}");
        (LoginAutomator a, ConcurrentQueue<string> sent) = Build(resolvePassword: null, steps: s);
        string? abortReason = null, skipReason = null;
        a.Aborted += r => abortReason = r;
        a.StepSkipped += r => skipReason = r;
        a.Start();
        a.Feed(Ascii("Password:"));

        Assert.Null(abortReason);
        Assert.Contains("no password", skipReason);
        Assert.False(sent.TryDequeue(out _));
    }

    [Fact]
    public void PasswordPlaceholder_SavedPasswordUnreadable_SkipsTheStep_WithoutAborting()
    {
        AutomationStep s = new("Password:", "{password}");
        (LoginAutomator a, ConcurrentQueue<string> sent) = Build(
            resolvePassword: () => Task.FromResult<string?>(null), steps: s);
        string? abortReason = null, skipReason = null;
        a.Aborted += r => abortReason = r;
        a.StepSkipped += r => skipReason = r;
        a.Start();
        a.Feed(Ascii("Password:"));

        Assert.Null(abortReason);
        Assert.Contains("can't be read", skipReason);
        Assert.False(sent.TryDequeue(out _));
    }

    [Fact]
    public void NoCredentialsSaved_TheMenuStepsAfterTheLoginStillRun()
    {
        (LoginAutomator a, ConcurrentQueue<string> sent) = Build(
            username: null, resolvePassword: null,
            steps: [new("Login:", "{username}"), new("Password:", "{password}"), new("Menu:", "E")]);
        bool loggedIn = false;
        a.LoggedIntoGame += () => loggedIn = true;
        a.Start();
        a.Feed(Ascii("Login:"));
        a.Feed(Ascii("Password:"));
        Assert.False(sent.TryDequeue(out _));

        a.Feed(Ascii("Menu:"));
        Assert.True(sent.TryDequeue(out string? menu));
        Assert.Equal("E\r", menu);
        Assert.True(loggedIn);
    }

    private static PasswordProtector NewProtector()
        => new(Path.Combine(Path.GetTempPath(), $"FT-key-{Guid.NewGuid():N}"));

    [Fact]
    public void TryBuild_NullCredentials_ReturnsNull()
    {
        LoginAutomator? a = LoginAutomator.TryBuild(
            null, NewProtector(), (_, _) => Task.CompletedTask);
        Assert.Null(a);
    }

    [Fact]
    public void TryBuild_NoMenuNavSteps_ReturnsNull()
    {
        BbsCredentials creds = new() { EncryptedUsername = NewProtector().Protect("alice") };
        LoginAutomator? a = LoginAutomator.TryBuild(
            creds, NewProtector(), (_, _) => Task.CompletedTask);
        Assert.Null(a);
    }

    [Fact]
    public void TryBuild_FullFlow_ResolvesPasswordFromInlineCiphertext()
    {
        string scratchDir = Path.Combine(Path.GetTempPath(), $"FT-cred-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDir);
        try
        {
            PasswordProtector protector = new(Path.Combine(scratchDir, ".key"));
            string encrypted = protector.Protect("hunter2");

            BbsCredentials creds = new()
            {
                EncryptedUsername = protector.Protect("alice"),
                EncryptedPassword = encrypted,
                MenuNavSteps =
                {
                    new MenuStep { WaitForPattern = "Login:",    Send = "{username}" },
                    new MenuStep { WaitForPattern = "Password:", Send = "{password}" },
                    new MenuStep { WaitForPattern = "Main Menu:", Send = "G" },
                },
            };

            ConcurrentQueue<string> sent = new();
            LoginAutomator? a = LoginAutomator.TryBuild(
                creds, protector,
                (text, _) => { sent.Enqueue(text); return Task.CompletedTask; });
            Assert.NotNull(a);
            bool done = false;
            a!.LoggedIntoGame += () => done = true;
            a.Start();
            a.Feed(Ascii("Login:Password:Main Menu:"));

            Assert.Equal(3, sent.Count);
            Assert.True(sent.TryDequeue(out string? first));  Assert.Equal("alice\r",   first);
            Assert.True(sent.TryDequeue(out string? second)); Assert.Equal("hunter2\r", second);
            Assert.True(sent.TryDequeue(out string? third));  Assert.Equal("G\r",       third);
            Assert.True(done);
        }
        finally
        {
            try { Directory.Delete(scratchDir, recursive: true); } catch { }
        }
    }
}
