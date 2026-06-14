using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Coverage for the persistable shape of <see cref="TalkSettings"/>. The
/// Settings.Talk section VM itself does I/O through the global
/// <see cref="Services.AppServices"/> singleton — exercising it cleanly
/// from a unit test would require swapping the singleton, which is out
/// of scope. The engine-knob push (<c>ApplyToServices</c>) is covered
/// in <see cref="RemoteCommandManagerTests"/>; this file pins the DTO
/// schema + defaults.
/// </summary>
public sealed class TalkSectionViewModelTests
{
    [Fact]
    public void TalkSettings_RoundTripsThroughJson()
    {
        TalkSettings src = new()
        {
            DisallowAllRemoteCommands       = true,
            DisallowPartyCommands           = true,
            DisallowRemoteFromTelepaths     = true,
            DisallowRemoteFromGangpaths     = true,
            DisallowRemoteFromLocal         = true,
            WarnOnInvalidRemoteCommand      = false,
            RemoteCommandFailureMessage     = "nope",
        };

        string json = JsonSerializer.Serialize(src);
        TalkSettings? back = JsonSerializer.Deserialize<TalkSettings>(json);

        Assert.NotNull(back);
        Assert.True(back!.DisallowAllRemoteCommands);
        Assert.True(back.DisallowPartyCommands);
        Assert.True(back.DisallowRemoteFromTelepaths);
        Assert.True(back.DisallowRemoteFromGangpaths);
        Assert.True(back.DisallowRemoteFromLocal);
        Assert.False(back.WarnOnInvalidRemoteCommand);
        Assert.Equal("nope", back.RemoteCommandFailureMessage);
    }

    [Fact]
    public void TalkSettings_Defaults_AreSafeAndPermissive()
    {
        // A freshly-loaded profile with no Talk entry falls through to
        // these values for the engine. Defaults must be:
        //   * permissive — no master / channel disable on by default
        //     (otherwise nothing works out of the box),
        //   * informative — WarnOnDenial true so legitimate senders see
        //     why their command didn't work,
        //   * pre-set failure message — matches the MegaMUD-flavor
        //     curly-brace meta-line convention.
        TalkSettings dto = new();

        Assert.False(dto.DisallowAllRemoteCommands);
        Assert.False(dto.DisallowPartyCommands);
        Assert.False(dto.DisallowRemoteFromTelepaths);
        Assert.False(dto.DisallowRemoteFromGangpaths);
        Assert.False(dto.DisallowRemoteFromLocal);
        Assert.True(dto.WarnOnInvalidRemoteCommand);
        // Bare text — RemoteCommandManager wraps every reply in { } at
        // send time, so the configured failure message is the unbraced
        // form to avoid double-bracing on the wire.
        Assert.Equal("command invalid or not allowed", dto.RemoteCommandFailureMessage);
    }
}
