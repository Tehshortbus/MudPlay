namespace MudPlay.Services.GitHub;

// A GitHub call that failed, worded for the player. Unauthorized: the sign-in it
// used is no good any more and is let go. Status: the API's HTTP answer, when it gave one.
public sealed class GitHubException : Exception
{
    public GitHubException(string message, bool unauthorized = false, int? status = null) : base(message)
    {
        Unauthorized = unauthorized;
        Status = status;
    }

    public bool Unauthorized { get; }
    public int? Status { get; }
}
