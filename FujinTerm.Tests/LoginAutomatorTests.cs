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
    public async Task LiteralPattern_AutoAppendsCarriageReturn()
    {
        AutomationStep s = new("Main Menu:", "G");
        (LoginAutomator a, var sent) = Build(steps: s);
        a.Start();
        a.Feed(Ascii("Main Menu: "));

        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("G\r", sentVal);
    }

    [Fact]
    public async Task EmptySend_StillSendsCarriageReturn()
    {
        AutomationStep s = new("Press any key", "");
        (LoginAutomator a, var sent) = Build(steps: s);
        a.Start();
        a.Feed(Ascii("[Press any key to continue]"));

        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("\r", sentVal);
    }

    [Fact]
    public async Task UsernamePlaceholder_Substitutes()
    {
        AutomationStep s = new("Login:", "{username}");
        (LoginAutomator a, var sent) = Build(username: "alice", steps: s);
        a.Start();
        a.Feed(Ascii("Login:"));

        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("alice\r", sentVal);
    }

    [Fact]
    public async Task UseridPlaceholder_AlsoSubstitutes()
    {
        AutomationStep s = new("Login:", "{userid}");
        (LoginAutomator a, var sent) = Build(username: "alice", steps: s);
        a.Start();
        a.Feed(Ascii("Login:"));

        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("alice\r", sentVal);
    }

    [Fact]
    public async Task PasswordPlaceholder_Substitutes()
    {
        AutomationStep s = new("Password:", "{password}");
        (LoginAutomator a, var sent) = Build(
            resolvePassword: () => Task.FromResult<string?>("hunter2"), steps: s);
        a.Start();
        a.Feed(Ascii("Password:"));

        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("hunter2\r", sentVal);
    }

    [Fact]
    public async Task Placeholder_IsCaseInsensitive()
    {
        AutomationStep s = new("Login:", "{USERNAME}");
        (LoginAutomator a, var sent) = Build(username: "alice", steps: s);
        a.Start();
        a.Feed(Ascii("Login:"));

        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("alice\r", sentVal);
    }

    [Fact]
    public async Task CsiSequences_StrippedBeforeMatching()
    {
        AutomationStep s = new("Main Menu:", "G");
        (LoginAutomator a, var sent) = Build(steps: s);
        a.Start();
        a.Feed(Ascii("\x1b[1;33mMain Menu:\x1b[0m "));

        await Task.Delay(20);
        Assert.True(sent.TryDequeue(out string? sentVal));
        Assert.Equal("G\r", sentVal);
    }

    [Fact]
    public async Task FullLoginFlow_RunsInOrder()
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

        await Task.Delay(60);
        Assert.Equal(3, sent.Count);
        Assert.True(sent.TryDequeue(out string? first));  Assert.Equal("alice\r",   first);
        Assert.True(sent.TryDequeue(out string? second)); Assert.Equal("hunter2\r", second);
        Assert.True(sent.TryDequeue(out string? third));  Assert.Equal("G\r",       third);
        Assert.True(done);
    }

    [Fact]
    public async Task UsernameLock_RefusesSecondSendAfterAcceptance()
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

        await Task.Delay(60);
        Assert.True(sent.TryDequeue(out string? first));   Assert.Equal("alice\r",  first);
        Assert.True(sent.TryDequeue(out string? second));  Assert.Equal("secret\r", second);
        Assert.False(sent.TryDequeue(out _));
        Assert.NotNull(abortReason);
        Assert.Contains("username already accepted", abortReason);
    }

    [Fact]
    public async Task PasswordLock_RefusesSecondSendAfterAcceptance()
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

        await Task.Delay(60);
        Assert.True(sent.TryDequeue(out string? first));   Assert.Equal("hunter2\r", first);
        Assert.True(sent.TryDequeue(out string? second));  Assert.Equal("\r",        second);
        Assert.False(sent.TryDequeue(out _));
        Assert.NotNull(abortReason);
        Assert.Contains("password already accepted", abortReason);
    }

    [Fact]
    public async Task UsernamePlaceholder_NoUsernameConfigured_Aborts()
    {
        AutomationStep s = new("Login:", "{username}");
        (LoginAutomator a, _) = Build(username: null, steps: s);
        string? abortReason = null;
        a.Aborted += r => abortReason = r;
        a.Start();
        a.Feed(Ascii("Login:"));

        await Task.Delay(20);
        Assert.NotNull(abortReason);
        Assert.Contains("{username}", abortReason);
    }

    [Fact]
    public async Task PasswordPlaceholder_NoResolverConfigured_Aborts()
    {
        AutomationStep s = new("Password:", "{password}");
        (LoginAutomator a, _) = Build(resolvePassword: null, steps: s);
        string? abortReason = null;
        a.Aborted += r => abortReason = r;
        a.Start();
        a.Feed(Ascii("Password:"));

        await Task.Delay(20);
        Assert.NotNull(abortReason);
        Assert.Contains("{password}", abortReason);
    }

    [Fact]
    public async Task PasswordPlaceholder_ResolverReturnsNull_Aborts()
    {
        AutomationStep s = new("Password:", "{password}");
        (LoginAutomator a, _) = Build(
            resolvePassword: () => Task.FromResult<string?>(null), steps: s);
        string? abortReason = null;
        a.Aborted += r => abortReason = r;
        a.Start();
        a.Feed(Ascii("Password:"));

        await Task.Delay(20);
        Assert.NotNull(abortReason);
        Assert.Contains("not found", abortReason);
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
    public async Task TryBuild_FullFlow_ResolvesPasswordFromInlineCiphertext()
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

            await Task.Delay(60);
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
