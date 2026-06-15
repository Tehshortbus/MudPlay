using System.Collections.Generic;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// <see cref="ProfileService.NormalizeForLoad"/> rebuilds the BBS credential
/// lookup case-insensitively, so a profile that keyed credentials under
/// "Playpen" resolves for a "playpen" BBS (BBS names are case-insensitive
/// folder names).
/// </summary>
public sealed class ProfileServiceTests
{
    [Fact]
    public void NormalizeForLoad_BbsCredentials_ResolveCaseInsensitively()
    {
        var profile = new CharacterProfile
        {
            // Default (case-sensitive) dictionary keyed with capital P — the
            // shape a deserialized profile arrives in.
            BbsCredentials = new Dictionary<string, BbsCredentials>
            {
                ["Playpen"] = new BbsCredentials { EncryptedUsername = "enc" },
            },
        };

        // Pre-condition: the mismatched-case lookup misses before normalization.
        Assert.False(profile.BbsCredentials.TryGetValue("playpen", out _));

        ProfileService.NormalizeForLoad(profile);

        Assert.True(profile.BbsCredentials!.TryGetValue("playpen", out BbsCredentials? cred));
        Assert.Equal("enc", cred!.EncryptedUsername);
    }

    [Fact]
    public void NormalizeForLoad_NullCredentials_IsNoOp()
    {
        var profile = new CharacterProfile();
        ProfileService.NormalizeForLoad(profile);
        Assert.Null(profile.BbsCredentials);
    }
}
