namespace MudPlay.Services.GitHub;

// A GitHub call that failed, worded for the player. Unauthorized: the sign-in it
// used is no good any more and is let go.
public sealed class GitHubException : Exception
{
    public GitHubException(string message, bool unauthorized = false) : base(message) => Unauthorized = unauthorized;

    public bool Unauthorized { get; }
}
