namespace MudPlay.Services.GitHub;

// A GitHub sign-in: the MudPlay Reporter app's (renewed with RefreshToken until
// RefreshExpiresAt), or a token of the player's own (OwnToken), which never expires
// on our side. A null ExpiresAt is a token with no expiry.
public sealed record GitHubSignIn(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RefreshExpiresAt,
    string? Login,
    bool OwnToken)
{
    // A minute short of the expiry, so a token doesn't lapse between the check and
    // the call.
    public bool IsCurrent(DateTimeOffset now) => ExpiresAt is not { } expires || now < expires - TimeSpan.FromMinutes(1);

    public bool CanRenew(DateTimeOffset now) =>
        RefreshToken is not null && (RefreshExpiresAt is not { } until || now < until);
}
