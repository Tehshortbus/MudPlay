using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// ProfileService.NormalizeForLoad rebuilds the BBS credential
// lookup case-insensitively, so a profile that keyed credentials under
// "Playpen" resolves for a "playpen" BBS (BBS names are case-insensitive
// folder names).
public sealed class ProfileServiceTests
{
    [Theory]
    // report stock-20260828-104653: a copied profile keeps the old name; heal it.
    [InlineData("Fujin", "Raijin WuzHere", "Raijin WuzHere")]  // copied profile → heal to full live name
    [InlineData("Raijin", "Raijin WuzHere", "Raijin WuzHere")] // given matches, family missing → still heal (store family)
    [InlineData("Raijin WuzHere", "Raijin WuzHere", null)]     // identical → no heal, no Save churn
    [InlineData("raijin wuzhere", "Raijin WuzHere", null)]     // case-only difference → no heal
    [InlineData("Fujin", "", null)]                            // blank stat name (pre-stat) → nothing to heal from
    [InlineData("Fujin", null, null)]
    [InlineData(null, "Raijin WuzHere", "Raijin WuzHere")]     // no stored name yet → adopt the live name
    public void HealedCharacterName_HealsOnlyOnRealChange(string? current, string? stat, string? expected)
        => Assert.Equal(expected, ProfileService.HealedCharacterName(current, stat));

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

    [Fact]
    public void NavLairMode_DefaultsUniform_AndRoundTripsByName()
    {
        Assert.Equal(LairDisplayMode.Uniform, new CharacterProfile().NavLairMode);

        var profile = new CharacterProfile { NavLairMode = LairDisplayMode.HeatCount };
        string json = JsonSerializer.Serialize(profile, JsonStore.Options);

        // Persisted by member name (JsonStringEnumConverter), so reordering the
        // enum can never remap a saved profile's mode to the wrong value.
        Assert.Contains("\"HeatCount\"", json);

        CharacterProfile back = JsonSerializer.Deserialize<CharacterProfile>(json, JsonStore.Options)!;
        Assert.Equal(LairDisplayMode.HeatCount, back.NavLairMode);
    }

    [Fact]
    public void NavSpellMode_ByTeleport_IsKeptOutOfTheKeyOlderClientsRead()
    {
        // What an older client does with a mode name it doesn't know: the whole
        // profile fails to load. So the by-teleport mode can't go in NavSpellMode.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CharacterProfile>(
            "{\"NavSpellMode\":\"SomeLaterMode\"}", JsonStore.Options));

        var profile = new CharacterProfile();
        Assert.True(SpellDisplayModes.Write(profile, SpellDisplayMode.ByTeleport));
        Assert.False(SpellDisplayModes.Write(profile, SpellDisplayMode.ByTeleport));
        string json = JsonSerializer.Serialize(profile, JsonStore.Options);
        Assert.Contains("\"NavSpellMode\": \"Mono\"", json);
        Assert.Contains("\"NavSpellOverlay\": \"ByTeleport\"", json);

        CharacterProfile back = JsonSerializer.Deserialize<CharacterProfile>(json, JsonStore.Options)!;
        Assert.Equal(SpellDisplayMode.ByTeleport, SpellDisplayModes.Read(back));

        // Back on a mode every client knows, the second key is cleared: left behind
        // it would outrank the first.
        Assert.True(SpellDisplayModes.Write(back, SpellDisplayMode.Off));
        Assert.Null(back.NavSpellOverlay);
        Assert.Equal(SpellDisplayMode.Off, SpellDisplayModes.Read(back));
    }

    [Fact]
    public void NavSpellMode_ReadFallsBackToTheOldKey()
    {
        // A profile from before the second key, and one a later client left a mode
        // in that this client doesn't know.
        Assert.Equal(SpellDisplayMode.Mono,
            SpellDisplayModes.Read(JsonSerializer.Deserialize<CharacterProfile>("{}", JsonStore.Options)!));
        Assert.Equal(SpellDisplayMode.ByName, SpellDisplayModes.Read(JsonSerializer.Deserialize<CharacterProfile>(
            "{\"NavSpellMode\":\"ByName\"}", JsonStore.Options)!));
        Assert.Equal(SpellDisplayMode.ByName, SpellDisplayModes.Read(JsonSerializer.Deserialize<CharacterProfile>(
            "{\"NavSpellMode\":\"ByName\",\"NavSpellOverlay\":\"SomeLaterMode\"}", JsonStore.Options)!));
    }

    [Fact]
    public void NavSpellOverlay_HandEdited_IsReadWhenItNamesAModeAndIgnoredOtherwise()
    {
        static SpellDisplayMode Read(string overlay) => SpellDisplayModes.Read(
            new CharacterProfile { NavSpellMode = SpellDisplayMode.ByName, NavSpellOverlay = overlay });

        // A mode's name wins over the old key, whichever mode it is.
        Assert.Equal(SpellDisplayMode.Off, Read("Off"));
        Assert.Equal(SpellDisplayMode.ByTeleport, Read("ByTeleport"));
        // A number that is a mode's value is that mode; one that isn't, a different
        // casing, and an empty string all leave the old key standing.
        Assert.Equal(SpellDisplayMode.ByTeleport, Read(((int)SpellDisplayMode.ByTeleport).ToString()));
        Assert.Equal(SpellDisplayMode.ByName, Read("99"));
        Assert.Equal(SpellDisplayMode.ByName, Read("byteleport"));
        Assert.Equal(SpellDisplayMode.ByName, Read(""));
    }

    [Fact]
    public void NavLoopLinesMode_DefaultsToSteps_AndRoundTripsByName()
    {
        // An older profile has no value stored and must come back drawing the loop
        // the way it always was: line plus numbered steps.
        Assert.Equal(LoopLinesMode.Steps, new CharacterProfile().NavLoopLinesMode);
        Assert.Equal(LoopLinesMode.Steps,
            JsonSerializer.Deserialize<CharacterProfile>("{}", JsonStore.Options)!.NavLoopLinesMode);

        var profile = new CharacterProfile { NavLoopLinesMode = LoopLinesMode.NoSteps };
        string json = JsonSerializer.Serialize(profile, JsonStore.Options);
        Assert.Contains("\"NoSteps\"", json);

        CharacterProfile back = JsonSerializer.Deserialize<CharacterProfile>(json, JsonStore.Options)!;
        Assert.Equal(LoopLinesMode.NoSteps, back.NavLoopLinesMode);
    }

    // The listing reads a BBS's profiles folder without checking for it first, because a BBS
    // another client renames or removes mid-listing looks exactly like this one: a folder
    // under BBS/ whose profiles folder isn't there. It used to throw out of the listing.
    [Fact]
    public void ListAll_ABbsWithNoProfilesFolder_IsSkipped_AndTheRestAreListed()
    {
        string id = Path.GetRandomFileName();
        string empty = "listall-test-empty-" + id;
        string full = "listall-test-full-" + id;
        try
        {
            Directory.CreateDirectory(AppPaths.BbsFolder(empty));
            Directory.CreateDirectory(AppPaths.ProfileFolder(full, "Tester"));
            File.WriteAllText(AppPaths.CharacterProfileFile(full, "Tester"), "{}");

            List<ProfileRef> listed = new ProfileService().ListAll().ToList();

            Assert.Contains(new ProfileRef(full, "Tester"), listed);
            Assert.DoesNotContain(listed, r => r.Bbs == empty);
        }
        finally
        {
            foreach (string bbs in new[] { empty, full })
                if (Directory.Exists(AppPaths.BbsFolder(bbs))) Directory.Delete(AppPaths.BbsFolder(bbs), recursive: true);
        }
    }
}
