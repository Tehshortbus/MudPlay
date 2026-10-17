using System.Text.Json;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Coverage for the persistable shape of TalkSettings. The
// Settings.Talk section VM itself does I/O through the global
// Services.AppServices singleton — exercising it cleanly
// from a unit test would require swapping the singleton, which is out
// of scope. The engine-knob push (ApplyToServices) is covered
// in RemoteCommandManagerTests; this file pins the DTO
// schema + defaults.
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

    // The broadcast channel is the one channel that starts switched off: it is
    // public, so it is listened to only once the user unticks its box. A profile
    // saved before the setting existed has no such field and reads as switched off;
    // an unticked box is kept through a save.
    [Fact]
    public void BroadcastChannel_IsDisallowedByDefault_AndForAProfileFromBeforeTheSetting()
    {
        Assert.True(new TalkSettings().DisallowRemoteFromBroadcast);

        TalkSettings? older = JsonSerializer.Deserialize<TalkSettings>(
            """{ "DisallowRemoteFromTelepaths": false, "DisallowRemoteFromGangpaths": true, "DisallowRemoteFromLocal": false }""");
        Assert.True(older!.DisallowRemoteFromBroadcast);
        Assert.True(older.DisallowRemoteFromGangpaths);

        TalkSettings? opened = JsonSerializer.Deserialize<TalkSettings>(
            JsonSerializer.Serialize(new TalkSettings { DisallowRemoteFromBroadcast = false }));
        Assert.False(opened!.DisallowRemoteFromBroadcast);
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
