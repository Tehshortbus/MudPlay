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
//
// Each report sent is remembered (its gist and issue) until its issue is closed;
// then its gist is deleted, so the player's account doesn't keep every report
// forever. Checked at launch and once a day after.
// Read and changed on the UI thread.
public sealed class GitHubBugReporter
{
    public const string LogCategory = "GitHub";
    // An issue body's limit is 65,536 characters; the description is cut well short
    // of it, the gist holds the rest.
    private const int MaxDescriptionChars = 60_000;
    private const int MaxTitleChars = 80;
    private static readonly TimeSpan CleanUpEvery = TimeSpan.FromDays(1);

    private readonly GitHubClient _client;
    private readonly string _file;
    private readonly string _uploadsFile;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, Action, IDisposable> _arm;
    private GitHubSignIn? _signIn;
    private List<Upload> _uploads;
    private IDisposable? _cleanUpTick;

    public readonly record struct Submitted(int Number, string IssueUrl);

    // A report sent: Login is the account its gist is on, the only one that can delete it.
    private sealed record Upload(string ReportId, string GistId, string GistUrl, int Issue, string? Login);

    // file: where the sign-in is kept between launches; uploadsFile: the reports whose
    // gists wait for their issues to close. arm stands in for the dispatcher's timer.
    public GitHubBugReporter(GitHubClient client, string file, string uploadsFile, LogService? log = null,
        Func<DateTimeOffset>? now = null, Func<TimeSpan, Action, IDisposable>? arm = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadsFile);
        _client = client;
        _file = file;
        _uploadsFile = uploadsFile;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _arm = arm ?? DispatcherTicks.Once;
        _signIn = Load();
        _uploads = LoadUploads();
    }

    public event Action? SignInChanged;

    public bool IsSignedIn => _signIn is not null;
    public string? Login => _signIn?.Login;
    public bool UsesOwnToken => _signIn?.OwnToken == true;

    // The issues whose reports' gists are still up, waiting for them to close.
    public IReadOnlyList<int> IssuesAwaitingClose => _uploads.Select(u => u.Issue).ToList();

    // Named seam: the clean-up running now, or the last one, finished.
    public Task CleanUpRun { get; private set; } = Task.CompletedTask;

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
            GitHubClient.Gist gist = await _client.CreateGistAsync(signIn.AccessToken, reportId + ".md", report,
                $"MudPlay bug report {reportId}", ct);
            GitHubClient.Issue issue;
            try
            {
                issue = await _client.CreateIssueAsync(signIn.AccessToken,
                    IssueTitle(description), IssueBody(reportId, description, gist.Url), ct);
            }
            catch (Exception ex) when (ex is GitHubException or HttpRequestException or OperationCanceledException)
            {
                // Each retry uploads the report again: a gist no issue links is taken
                // back, or the player's account fills with copies.
                await RemoveGistAsync(signIn.AccessToken, gist);
                throw;
            }
            _log?.Info(LogCategory, $"Opened issue #{issue.Number} for bug report {reportId} ({issue.Url}; report at {gist.Url}).");
            _uploads.Add(new Upload(reportId, gist.Id, gist.Url, issue.Number, signIn.Login));
            SaveUploads();
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

    // Not cancelled with the submit: the window closing mid-issue still takes the gist back.
    private async Task RemoveGistAsync(string token, GitHubClient.Gist gist)
    {
        try
        {
            await _client.DeleteGistAsync(token, gist.Id, CancellationToken.None);
            _log?.Info(LogCategory, $"Removed the gist {gist.Url}: no issue links it.");
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException or OperationCanceledException)
        {
            _log?.Warn(LogCategory, $"Couldn't remove the gist {gist.Url}, which no issue links: {ex.Message}");
        }
    }

    // ----- clean-up -----------------------------------------------------

    // Now, then every day while the client runs. Does nothing without a sign-in or a
    // report waiting, so a player who never used Github submit never reaches GitHub.
    public void StartCleanUps()
    {
        if (_cleanUpTick is not null) return;
        CleanUpTick();
    }

    private void CleanUpTick()
    {
        _cleanUpTick = _arm(CleanUpEvery, CleanUpTick);
        if (CleanUpRun.IsCompleted) CleanUpRun = CleanUpAsync();
    }

    // Only the signed-in account's reports: another account's gists wait until it signs
    // in again. Stops at the first failure and tries again on the next round.
    private async Task CleanUpAsync()
    {
        if (_signIn is not { } signIn || !_uploads.Any(u => IsTheirs(u, signIn))) return;
        try
        {
            if (!await ResumeAsync(CancellationToken.None) || _signIn is not { } current) return;
            foreach (Upload upload in _uploads.Where(u => IsTheirs(u, current)).ToList())
            {
                if (!await _client.IssueIsClosedAsync(current.AccessToken, upload.Issue, CancellationToken.None)) continue;
                try
                {
                    await _client.DeleteGistAsync(current.AccessToken, upload.GistId, CancellationToken.None);
                    _log?.Info(LogCategory, $"Removed the gist of bug report {upload.ReportId} ({upload.GistUrl}): issue #{upload.Issue} is closed.");
                }
                catch (GitHubException ex) when (ex.Status == 404)
                {
                    _log?.Info(LogCategory, $"The gist of bug report {upload.ReportId} was already gone; issue #{upload.Issue} is closed.");
                }
                _uploads.Remove(upload);
                SaveUploads();
            }
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException or OperationCanceledException)
        {
            _log?.Warn(LogCategory, $"Couldn't clean up the gists of closed bug reports; tries again tomorrow: {ex.Message}");
            if (ex is GitHubException { Unauthorized: true }) Forget(ex.Message);
        }
    }

    private static bool IsTheirs(Upload upload, GitHubSignIn signIn) =>
        string.Equals(upload.Login, signIn.Login, StringComparison.OrdinalIgnoreCase);

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

    private List<Upload> LoadUploads()
    {
        if (!File.Exists(_uploadsFile)) return new();
        try { return JsonSerializer.Deserialize<List<Upload>>(File.ReadAllText(_uploadsFile)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log?.Warn(LogCategory, $"Couldn't read the bug reports sent to GitHub, so their gists aren't cleaned up: {ex.Message}");
            return new();
        }
    }

    private void SaveUploads()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_uploadsFile)!);
            File.WriteAllText(_uploadsFile, JsonSerializer.Serialize(_uploads));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warn(LogCategory, $"Couldn't save the bug reports sent to GitHub: {ex.Message}");
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
