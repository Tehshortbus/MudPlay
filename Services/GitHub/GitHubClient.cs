using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MudPlay.Services.GitHub;

// The GitHub calls a bug report needs: the device sign-in of the MudPlay Reporter
// app, renewing its sign-in, the signed-in login, a secret gist and an issue.
//
// The device flow runs on the app's public client ID alone: a desktop app can't
// keep a secret, and GitHub renews a device-flow sign-in without one. The player
// approves on github.com, so their password never reaches the client.
public sealed class GitHubClient : IDisposable
{
    // The MudPlay Reporter GitHub App (owned by Tehshortbus), installed on the repo.
    public const string ClientId = "Iv23linwzjxJZh2Jf2d7";
    public const string Repository = "Tehshortbus/MudPlay";

    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string ApiBase = "https://api.github.com";
    // GitHub's answer to a poll asking for more time between polls.
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;

    public readonly record struct DeviceCode(
        string Device, string UserCode, string VerificationUri, TimeSpan Interval, DateTimeOffset ExpiresAt);

    public readonly record struct Issue(int Number, string Url);

    // Named seam: handler, delay and now stand in for the network, the wait between
    // polls and the clock. The client passes none of them.
    public GitHubClient(
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? now = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(60);
        // GitHub refuses requests with no User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"MudPlay/{AppInfo.Version}");
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public void Dispose() => _http.Dispose();

    // ----- sign-in ------------------------------------------------------

    public async Task<DeviceCode> StartDeviceSignInAsync(CancellationToken ct)
    {
        JsonElement json = await PostFormAsync(DeviceCodeUrl, new() { ["client_id"] = ClientId }, ct)
            .ConfigureAwait(false);
        if (Text(json, "error") is { } error)
            throw new GitHubException($"GitHub refused the sign-in: {Text(json, "error_description") ?? error}");
        return new DeviceCode(
            Text(json, "device_code") ?? throw Malformed("device_code"),
            Text(json, "user_code") ?? throw Malformed("user_code"),
            Text(json, "verification_uri") ?? "https://github.com/login/device",
            TimeSpan.FromSeconds(Number(json, "interval") ?? 5),
            _now() + TimeSpan.FromSeconds(Number(json, "expires_in") ?? 900));
    }

    // Polls until the player approves the code on github.com, declines it, or it
    // expires.
    public async Task<GitHubSignIn> FinishDeviceSignInAsync(DeviceCode code, CancellationToken ct)
    {
        TimeSpan interval = code.Interval;
        while (true)
        {
            if (_now() >= code.ExpiresAt) throw new GitHubException("The sign-in code expired before it was entered on GitHub.");
            await _delay(interval, ct).ConfigureAwait(false);
            JsonElement json = await PostFormAsync(TokenUrl, new()
            {
                ["client_id"] = ClientId,
                ["device_code"] = code.Device,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, ct).ConfigureAwait(false);

            switch (Text(json, "error"))
            {
                case null:
                    return SignInFrom(json);
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval = Number(json, "interval") is { } asked ? TimeSpan.FromSeconds(asked) : interval + SlowDownStep;
                    continue;
                case "expired_token":
                    throw new GitHubException("The sign-in code expired before it was entered on GitHub.");
                case "access_denied":
                    throw new GitHubException("The sign-in was declined on GitHub.");
                case { } other:
                    throw new GitHubException($"GitHub refused the sign-in: {Text(json, "error_description") ?? other}");
            }
        }
    }

    // A sign-in's access token lasts 8 hours; its refresh token renews it for 6 months.
    public async Task<GitHubSignIn> RefreshAsync(GitHubSignIn signIn, CancellationToken ct)
    {
        if (signIn.RefreshToken is not { } refresh) throw new GitHubException("The GitHub sign-in can't be renewed.");
        JsonElement json = await PostFormAsync(TokenUrl, new()
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
        }, ct).ConfigureAwait(false);
        if (Text(json, "error") is { } error)
            throw new GitHubException($"GitHub didn't renew the sign-in: {Text(json, "error_description") ?? error}", unauthorized: true);
        return SignInFrom(json) with { Login = signIn.Login };
    }

    private GitHubSignIn SignInFrom(JsonElement json)
    {
        DateTimeOffset now = _now();
        return new GitHubSignIn(
            Text(json, "access_token") ?? throw Malformed("access_token"),
            Text(json, "refresh_token"),
            Number(json, "expires_in") is { } expires ? now + TimeSpan.FromSeconds(expires) : null,
            Number(json, "refresh_token_expires_in") is { } refreshExpires ? now + TimeSpan.FromSeconds(refreshExpires) : null,
            Login: null,
            OwnToken: false);
    }

    // ----- the API ------------------------------------------------------

    public async Task<string> LoginAsync(string token, CancellationToken ct)
    {
        JsonElement json = await SendApiAsync("reading your GitHub account", HttpMethod.Get, "/user", token, body: null, ct).ConfigureAwait(false);
        return Text(json, "login") ?? throw Malformed("login");
    }

    // A secret gist: unlisted, read by anyone holding its link.
    public async Task<string> CreateGistAsync(string token, string fileName, string content, string description, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["description"] = description,
            ["public"] = false,
            ["files"] = new Dictionary<string, object> { [fileName] = new Dictionary<string, string> { ["content"] = content } },
        };
        JsonElement json = await SendApiAsync("uploading the report as a gist", HttpMethod.Post, "/gists", token, body, ct).ConfigureAwait(false);
        return Text(json, "html_url") ?? throw Malformed("html_url");
    }

    public async Task<Issue> CreateIssueAsync(string token, string title, string body, CancellationToken ct)
    {
        var payload = new Dictionary<string, string> { ["title"] = title, ["body"] = body };
        JsonElement json = await SendApiAsync("opening the issue", HttpMethod.Post, $"/repos/{Repository}/issues", token, payload, ct)
            .ConfigureAwait(false);
        return new Issue((int)(Number(json, "number") ?? throw Malformed("number")), Text(json, "html_url") ?? throw Malformed("html_url"));
    }

    // ----- plumbing -----------------------------------------------------

    private async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        // Without it the sign-in endpoints answer form-encoded.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new GitHubException($"GitHub answered {(int)response.StatusCode} to the sign-in.");
        return Parse(text);
    }

    // step names the call in the player's words, so a refusal says which one failed.
    private async Task<JsonElement> SendApiAsync(string step, HttpMethod method, string path, string token, object? body, CancellationToken ct)
    {
        using HttpRequestMessage request = new(method, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = JsonContent.Create(body);
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return Parse(text);

        string? message = TryParse(text) is { } error ? Text(error, "message") : null;
        string reason = $"GitHub refused {step}: {(int)response.StatusCode}{(message is null ? "" : $" ({message})")}";
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new GitHubException($"{reason}. The GitHub sign-in is no longer valid.", unauthorized: true),
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound => new GitHubException(
                $"{reason}. The sign-in isn't allowed to do this: sign out and sign in again, or use a token of your own with the public_repo and gist scopes."),
            _ => new GitHubException(reason + "."),
        };
    }

    private static JsonElement Parse(string text) =>
        TryParse(text) ?? throw new GitHubException("GitHub's answer wasn't readable.");

    private static JsonElement? TryParse(string text)
    {
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static long? Number(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : null;

    private static GitHubException Malformed(string field) => new($"GitHub's answer had no {field}.");
}
