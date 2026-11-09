using System.Net;
using System.Text;
using System.Text.Json;
using MudPlay.Services.GitHub;
using Xunit;

namespace MudPlay.Tests;

// The Bug report window's Github submit: the MudPlay Reporter sign-in (device flow,
// kept between launches and renewed), a token of the player's own, and the report
// sent as a secret gist with an issue that links it. GitHub is a scripted server.
public sealed class GitHubBugReporterTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-github-" + Path.GetRandomFileName());
    private readonly FakeGitHub _server = new();
    private DateTimeOffset _now = T0;
    private readonly List<TimeSpan> _waits = new();

    private string File => Path.Combine(_dir, "github.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private GitHubBugReporter Reporter() =>
        new(new GitHubClient(_server, delay: (wait, _) => { _waits.Add(wait); return Task.CompletedTask; }, now: () => _now),
            File, now: () => _now);

    // ----- the scripted server ------------------------------------------

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly record struct Request(string Method, string Path, string Body, string? Auth);

        private readonly Queue<(string Path, HttpStatusCode Status, string Json)> _answers = new();
        public List<Request> Requests { get; } = new();

        public void Answer(string path, string json, HttpStatusCode status = HttpStatusCode.OK) =>
            _answers.Enqueue((path, status, json));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Request(request.Method.Method, request.RequestUri!.AbsolutePath, body,
                request.Headers.Authorization?.Parameter));
            (string path, HttpStatusCode status, string json) = _answers.Dequeue();
            Assert.Equal(path, request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private void AnswerDeviceSignIn(string token = "ghu_one")
    {
        _server.Answer("/login/device/code",
            """{"device_code":"dev","user_code":"WDJB-MJHT","verification_uri":"https://github.com/login/device","interval":5,"expires_in":900}""");
        _server.Answer("/login/oauth/access_token", """{"error":"authorization_pending"}""");
        _server.Answer("/login/oauth/access_token", """{"error":"slow_down","interval":10}""");
        _server.Answer("/login/oauth/access_token",
            $$"""{"access_token":"{{token}}","expires_in":28800,"refresh_token":"ghr_one","refresh_token_expires_in":15897600}""");
        _server.Answer("/user", """{"login":"thunder"}""");
    }

    private static async Task SignIn(GitHubBugReporter reporter)
    {
        GitHubClient.DeviceCode code = await reporter.StartSignInAsync(CancellationToken.None);
        Assert.Equal("WDJB-MJHT", code.UserCode);
        await reporter.FinishSignInAsync(code, CancellationToken.None);
    }

    // ----- sign-in ------------------------------------------------------

    [Fact]
    public async Task ADeviceSignIn_WaitsOutThePolls_AndIsKeptForTheNextLaunch()
    {
        GitHubBugReporter reporter = Reporter();
        Assert.False(await reporter.ResumeAsync(CancellationToken.None));
        AnswerDeviceSignIn();

        await SignIn(reporter);

        Assert.True(reporter.IsSignedIn);
        Assert.Equal("thunder", reporter.Login);
        Assert.Contains($"client_id={GitHubClient.ClientId}", _server.Requests[0].Body);
        Assert.Equal("ghu_one", _server.Requests[^1].Auth);
        Assert.Equal(new[] { 5, 5, 10 }, _waits.Select(w => (int)w.TotalSeconds));

        GitHubBugReporter nextLaunch = Reporter();
        Assert.True(await nextLaunch.ResumeAsync(CancellationToken.None));
        Assert.Equal("thunder", nextLaunch.Login);
    }

    [Fact]
    public async Task ALapsedSignIn_IsRenewedWithoutASecret_AndOnePastRenewingIsLetGo()
    {
        GitHubBugReporter reporter = Reporter();
        AnswerDeviceSignIn();
        await SignIn(reporter);

        _now = T0.AddHours(9);
        _server.Answer("/login/oauth/access_token",
            """{"access_token":"ghu_two","expires_in":28800,"refresh_token":"ghr_two","refresh_token_expires_in":15897600}""");
        Assert.True(await reporter.ResumeAsync(CancellationToken.None));
        string renew = _server.Requests[^1].Body;
        Assert.Contains("grant_type=refresh_token", renew);
        Assert.Contains("refresh_token=ghr_one", renew);
        Assert.DoesNotContain("client_secret", renew);
        Assert.Equal("thunder", reporter.Login);

        _now = T0.AddDays(200);
        Assert.False(await reporter.ResumeAsync(CancellationToken.None));
        Assert.False(reporter.IsSignedIn);
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public async Task AnOwnToken_IsCheckedBeforeItIsKept_AndNeverExpires()
    {
        GitHubBugReporter reporter = Reporter();
        _server.Answer("/user", """{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<GitHubException>(() => reporter.UseOwnTokenAsync("ghp_bad", CancellationToken.None));
        Assert.False(reporter.IsSignedIn);

        _server.Answer("/user", """{"login":"raijin"}""");
        await reporter.UseOwnTokenAsync("  ghp_good  ", CancellationToken.None);
        Assert.True(reporter.UsesOwnToken);
        Assert.Equal("ghp_good", _server.Requests[^1].Auth);

        _now = T0.AddDays(400);
        Assert.True(await Reporter().ResumeAsync(CancellationToken.None));
    }

    // ----- the report ---------------------------------------------------

    [Fact]
    public async Task TheReport_GoesUpAsASecretGist_AndTheIssueLinksIt()
    {
        GitHubBugReporter reporter = Reporter();
        AnswerDeviceSignIn();
        await SignIn(reporter);
        _server.Answer("/gists", """{"html_url":"https://gist.github.com/thunder/abc"}""", HttpStatusCode.Created);
        _server.Answer($"/repos/{GitHubClient.Repository}/issues",
            """{"number":1300,"html_url":"https://github.com/Tehshortbus/MudPlay/issues/1300"}""", HttpStatusCode.Created);

        GitHubBugReporter.Submitted sent = await reporter.SubmitAsync("anomaly-20261109-120000", "# report\nlots",
            "Walked into the house door\nand stopped", CancellationToken.None);

        Assert.Equal(1300, sent.Number);
        JsonElement gist = JsonDocument.Parse(_server.Requests[^2].Body).RootElement;
        Assert.False(gist.GetProperty("public").GetBoolean());
        Assert.Equal("# report\nlots", gist.GetProperty("files").GetProperty("anomaly-20261109-120000.md")
            .GetProperty("content").GetString());
        JsonElement issue = JsonDocument.Parse(_server.Requests[^1].Body).RootElement;
        Assert.Equal("Bug report: Walked into the house door", issue.GetProperty("title").GetString());
        string body = issue.GetProperty("body").GetString()!;
        Assert.StartsWith("Walked into the house door\nand stopped", body);
        Assert.Contains("https://gist.github.com/thunder/abc", body);
        Assert.Contains("`anomaly-20261109-120000`", body);
    }

    [Fact]
    public async Task ASignInGitHubTurnsDown_IsLetGo()
    {
        GitHubBugReporter reporter = Reporter();
        AnswerDeviceSignIn();
        await SignIn(reporter);
        _server.Answer("/gists", """{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);

        GitHubException ex = await Assert.ThrowsAsync<GitHubException>(
            () => reporter.SubmitAsync("id", "report", "text", CancellationToken.None));
        Assert.True(ex.Unauthorized);
        Assert.False(reporter.IsSignedIn);
        Assert.False(System.IO.File.Exists(File));
    }
}
