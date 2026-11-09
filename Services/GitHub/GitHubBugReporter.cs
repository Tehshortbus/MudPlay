using System.Text.Json;

namespace MudPlay.Services.GitHub;

// Sends a bug report to GitHub from the Bug report window: the full report as a
// secret gist on the player's account (a report runs to a megabyte; an issue body
// holds 65,536 characters), and an issue on the MudPlay repository carrying their
// description and the gist's link.
//
// The sign-in is the MudPlay Reporter app's, approved on github.com, or a token of
// the player's own. It is kept in the app folder so later reports go straight up,
// readable only by the player's account where the system allows it. Never logged.
// Read and changed on the UI thread.
public sealed class GitHubBugReporter
{
    public const string LogCategory = "GitHub";
    // An issue body's limit is 65,536 characters; the description is cut well short
    // of it, the gist holds the rest.
    private const int MaxDescriptionChars = 60_000;
    private const int MaxTitleChars = 80;

    private readonly GitHubClient _client;
    private readonly string _file;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;
    private GitHubSignIn? _signIn;

    public readonly record struct Submitted(int Number, string IssueUrl);

    // file: where the sign-in is kept between launches.
    public GitHubBugReporter(GitHubClient client, string file, LogService? log = null, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        _client = client;
        _file = file;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _signIn = Load();
    }

    public event Action? SignInChanged;

    public bool IsSignedIn => _signIn is not null;
    public string? Login => _signIn?.Login;
    public bool UsesOwnToken => _signIn?.OwnToken == true;

    // ----- sign-in ------------------------------------------------------

    // A usable sign-in without asking the player: the saved one, renewed once it has
    // lapsed. False when they must sign in.
    public async Task<bool> ResumeAsync(CancellationToken ct)
    {
        if (_signIn is not { } signIn) return false;
        DateTimeOffset now = _now();
        if (signIn.IsCurrent(now)) return true;
        if (!signIn.CanRenew(now))
        {
            Forget("the GitHub sign-in expired");
            return false;
        }
        try
        {
            Set(await _client.RefreshAsync(signIn, ct));
            _log?.Info(LogCategory, "Renewed the GitHub sign-in.");
            return true;
        }
        catch (GitHubException ex) when (ex.Unauthorized)
        {
            Forget($"renewing it failed: {ex.Message}");
            return false;
        }
    }

    public Task<GitHubClient.DeviceCode> StartSignInAsync(CancellationToken ct) => _client.StartDeviceSignInAsync(ct);

    public async Task FinishSignInAsync(GitHubClient.DeviceCode code, CancellationToken ct)
    {
        GitHubSignIn signIn = await _client.FinishDeviceSignInAsync(code, ct);
        string login = await _client.LoginAsync(signIn.AccessToken, ct);
        Set(signIn with { Login = login });
        _log?.Info(LogCategory, $"Signed in to GitHub as {login}.");
    }

    // A token the player made on GitHub themselves; checked before it is kept.
    public async Task UseOwnTokenAsync(string token, CancellationToken ct)
    {
        string trimmed = token.Trim();
        if (trimmed.Length == 0) throw new GitHubException("Paste a token first.");
        string login = await _client.LoginAsync(trimmed, ct);
        Set(new GitHubSignIn(trimmed, null, null, null, login, OwnToken: true));
        _log?.Info(LogCategory, $"Using the player's own GitHub token, for {login}.");
    }

    public void SignOut() => Forget("signed out");

    // ----- the report ---------------------------------------------------

    // Needs a sign-in (ResumeAsync or FinishSignInAsync first). A sign-in GitHub turns
    // down is let go, so the next try asks for a fresh one.
    public async Task<Submitted> SubmitAsync(string reportId, string report, string description, CancellationToken ct)
    {
        if (_signIn is not { } signIn) throw new GitHubException("Not signed in to GitHub.");
        try
        {
            string gist = await _client.CreateGistAsync(signIn.AccessToken, reportId + ".md", report,
                $"MudPlay bug report {reportId}", ct);
            GitHubClient.Issue issue = await _client.CreateIssueAsync(signIn.AccessToken,
                IssueTitle(description), IssueBody(reportId, description, gist), ct);
            _log?.Info(LogCategory, $"Opened issue #{issue.Number} for bug report {reportId} ({issue.Url}; report at {gist}).");
            return new Submitted(issue.Number, issue.Url);
        }
        catch (GitHubException ex)
        {
            _log?.Warn(LogCategory, $"Bug report {reportId} wasn't sent: {ex.Message}");
            if (ex.Unauthorized) Forget(ex.Message);
            throw;
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn(LogCategory, $"Bug report {reportId} wasn't sent: couldn't reach GitHub: {ex.Message}");
            throw;
        }
    }

    public static string IssueTitle(string description)
    {
        string first = description.Trim().Split('\n', 2)[0].Trim();
        if (first.Length > MaxTitleChars) first = first[..(MaxTitleChars - 1)].TrimEnd() + "…";
        return $"Bug report: {first}";
    }

    public static string IssueBody(string reportId, string description, string gistUrl)
    {
        string text = description.Trim();
        if (text.Length > MaxDescriptionChars) text = text[..MaxDescriptionChars] + "…";
        return $"{text}\n\n---\n**Full report:** {gistUrl}\n**Report:** `{reportId}` · MudPlay {AppInfo.Version}\n";
    }

    // ----- keeping it ---------------------------------------------------

    private void Set(GitHubSignIn signIn)
    {
        _signIn = signIn;
        Save(signIn);
        SignInChanged?.Invoke();
    }

    private void Forget(string why)
    {
        if (_signIn is null) return;
        _signIn = null;
        try { File.Delete(_file); }
        catch (IOException ex) { _log?.Warn(LogCategory, $"Couldn't remove the saved GitHub sign-in: {ex.Message}"); }
        _log?.Info(LogCategory, $"Let go of the GitHub sign-in: {why}.");
        SignInChanged?.Invoke();
    }

    private GitHubSignIn? Load()
    {
        if (!File.Exists(_file)) return null;
        try { return JsonSerializer.Deserialize<GitHubSignIn>(File.ReadAllText(_file)); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log?.Warn(LogCategory, $"Couldn't read the saved GitHub sign-in, so it is ignored: {ex.Message}");
            return null;
        }
    }

    private void Save(GitHubSignIn signIn)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(signIn));
            // A token is as good as the account it opens: other users on the machine
            // mustn't read it.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warn(LogCategory, $"Couldn't save the GitHub sign-in; it lasts until MudPlay closes: {ex.Message}");
        }
    }
}
