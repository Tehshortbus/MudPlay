using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;
using MudPlay.Services.GitHub;

namespace MudPlay.ViewModels;

// Prompts the user to describe the problem for a bug report. The client
// state is captured at click time (before this dialog opens); this dialog
// only collects the "what went wrong" prose. Write report returns the trimmed
// description for the caller to write to the Desktop; Cancel / X returns null.
//
// Github submit sends the report from here instead: signed in (the MudPlay Reporter
// app's sign-in, or a token of the player's own), it uploads the report as a secret
// gist and opens an issue linking it, then closes with null, since nothing is left
// to write. Only when GitHub fails is the report written to the Desktop, so it
// isn't lost; the window stays open with the reason.
public sealed partial class BugReportDialogViewModel : ObservableObject, IDialogViewModel<string>
{
    private readonly GitHubBugReporter? _github;
    private readonly Func<string, (string ReportId, string Content)>? _render;
    private readonly Func<string, Task<string?>>? _writeToDesktop;
    private CancellationTokenSource? _work;
    private string? _signInPage;

    public event Action<string?>? CloseRequested;

    // github, render and writeToDesktop come together: without them Github submit
    // isn't offered. render gives the report's id (its file name without .md) and
    // text for a description; writeToDesktop writes it and returns the path, or null.
    public BugReportDialogViewModel(
        GitHubBugReporter? github = null,
        Func<string, (string ReportId, string Content)>? render = null,
        Func<string, Task<string?>>? writeToDesktop = null)
    {
        _github = github;
        _render = render;
        _writeToDesktop = writeToDesktop;
        if (_github is not null) _github.SignInChanged += OnSignInChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _description = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit), nameof(CanChangeSignIn))]
    private bool _busy;

    // What the GitHub submit is doing, or why it stopped.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = string.Empty;

    // The code to enter on github.com while a sign-in waits for it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInCode))]
    private string _signInCode = string.Empty;

    [ObservableProperty]
    private bool _showOwnToken;

    [ObservableProperty]
    private string _ownToken = string.Empty;

    // Submit is enabled only once the user has typed something.
    public bool CanSubmit => !string.IsNullOrWhiteSpace(Description) && !Busy;
    public bool CanChangeSignIn => !Busy;
    public bool OffersGitHub => _github is not null && _render is not null;
    public bool HasStatus => Status.Length > 0;
    public bool HasSignInCode => SignInCode.Length > 0;
    public bool IsSignedIn => _github?.IsSignedIn == true;

    public string SignInText => _github switch
    {
        { IsSignedIn: true, UsesOwnToken: true } g => $"GitHub: your own token ({g.Login})",
        { IsSignedIn: true } g => $"GitHub: signed in as {g.Login}",
        _ => "GitHub: not signed in",
    };

    private void OnSignInChanged()
    {
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(SignInText));
    }

    [RelayCommand]
    private void Submit()
    {
        if (!CanSubmit) return;
        Close(Description.Trim());
    }

    [RelayCommand]
    private void Cancel() => Close(null);

    // The window closed by its X: a sign-in poll or an upload under way stops.
    public void Abandon()
    {
        _work?.Cancel();
        if (_github is not null) _github.SignInChanged -= OnSignInChanged;
    }

    private void Close(string? result)
    {
        Abandon();
        CloseRequested?.Invoke(result);
    }

    [RelayCommand]
    private async Task GitHubSubmitAsync()
    {
        if (!CanSubmit || _github is null || _render is null) return;
        string description = Description.Trim();
        using CancellationTokenSource work = new();
        _work = work;
        Busy = true;
        try
        {
            if (!await _github.ResumeAsync(work.Token)) await SignInAsync(work.Token);
            Status = "Uploading the report to GitHub…";
            (string reportId, string content) = _render(description);
            GitHubBugReporter.Submitted sent = await _github.SubmitAsync(reportId, content, description, work.Token);
            ShellLaunch.OpenUrl(sent.IssueUrl);
            Close(null);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            // The window was closed or cancelled: nothing more to say or write.
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException or OperationCanceledException)
        {
            // HttpClient's own timeout surfaces as a cancellation we didn't ask for.
            string reason = ex is GitHubException ? ex.Message
                : ex is OperationCanceledException ? "GitHub didn't answer in time." : $"Couldn't reach GitHub: {ex.Message}";
            string? path = _writeToDesktop is null ? null : await _writeToDesktop(description);
            Status = path is null ? reason : $"{reason} The report was written to {path} instead.";
        }
        finally
        {
            SignInCode = string.Empty;
            _signInPage = null;
            if (ReferenceEquals(_work, work)) _work = null;
            Busy = false;
        }
    }

    private async Task SignInAsync(CancellationToken ct)
    {
        Status = "Asking GitHub for a sign-in code…";
        GitHubClient.DeviceCode code = await _github!.StartSignInAsync(ct);
        SignInCode = code.UserCode;
        _signInPage = code.VerificationUri;
        Status = $"Enter this code on {code.VerificationUri} (opened in your browser), then approve MudPlay Reporter.";
        ShellLaunch.OpenUrl(code.VerificationUri);
        await _github.FinishSignInAsync(code, ct);
        SignInCode = string.Empty;
    }

    [RelayCommand]
    private void OpenSignInPage()
    {
        if (_signInPage is { } page) ShellLaunch.OpenUrl(page);
    }

    [RelayCommand]
    private void SignOut()
    {
        if (Busy) return;
        _github?.SignOut();
        Status = "Signed out of GitHub.";
    }

    [RelayCommand]
    private void ToggleOwnToken() => ShowOwnToken = !ShowOwnToken;

    [RelayCommand]
    private async Task UseOwnTokenAsync()
    {
        if (_github is null || Busy) return;
        using CancellationTokenSource work = new();
        _work = work;
        Busy = true;
        try
        {
            Status = "Checking the token with GitHub…";
            await _github.UseOwnTokenAsync(OwnToken, work.Token);
            OwnToken = string.Empty;
            ShowOwnToken = false;
            Status = $"Using your own token, for {_github.Login}.";
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            // The window was closed.
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException or OperationCanceledException)
        {
            Status = ex is GitHubException { Unauthorized: true } ? "GitHub didn't accept that token."
                : ex is GitHubException ? ex.Message : $"Couldn't reach GitHub: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_work, work)) _work = null;
            Busy = false;
        }
    }
}
