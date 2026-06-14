using System.Text.Json;
using FujinTerm.Models.Profile;
using Xunit;

namespace FujinTerm.Tests;

public sealed class GeneralSettingsTests
{
    [Fact]
    public void Defaults_SafeBootState()
    {
        GeneralSettings dto = new();

        Assert.Equal(InitialTask.DoNothing, dto.DefaultTask);
        Assert.Null(dto.DefaultLoopName);
        Assert.Null(dto.DefaultAutoLairName);
        Assert.False(dto.AutoConnect);
        Assert.False(dto.BackupOnSave);
        AssertDefaultColumn(dto.AutoMode);

        // Emergency-hangup carve-out + every re-enable-on-reconnect flag
        // default OFF — reviving an auto-action or dropping the line on a
        // disabled engine is always opt-in, never a surprise.
        Assert.False(dto.AllowHangupInAllOffMode);
        Assert.False(dto.ReEnableAutoCombatOnReconnect);
        Assert.False(dto.ReEnableAutoNukeOnReconnect);
        Assert.False(dto.ReEnableAutoHealRestOnReconnect);
        Assert.False(dto.ReEnableAutoBlessOnReconnect);
        Assert.False(dto.ReEnableAutoLightOnReconnect);
        Assert.False(dto.ReEnableAutoGetItemsOnReconnect);
        Assert.False(dto.ReEnableAutoGetCashOnReconnect);
        Assert.False(dto.ReEnableAutoSneakOnReconnect);
        Assert.False(dto.ReEnableAutoHideOnReconnect);
        Assert.False(dto.ReEnableAutoSearchOnReconnect);
    }

    [Fact]
    public void RoundTripJson_PreservesEveryField()
    {
        GeneralSettings original = new()
        {
            DefaultTask = InitialTask.BeginLoop,
            DefaultLoopName = "Sewer farm",
            DefaultAutoLairName = "City lairs",
            AutoConnect = true,
            BackupOnSave = true,
            AutoMode   = AllToggles(false),
            AllowHangupInAllOffMode = true,
            ReEnableAutoCombatOnReconnect   = true,
            ReEnableAutoNukeOnReconnect     = true,
            ReEnableAutoHealRestOnReconnect = true,
            ReEnableAutoBlessOnReconnect    = true,
            ReEnableAutoLightOnReconnect    = true,
            ReEnableAutoGetItemsOnReconnect = true,
            ReEnableAutoGetCashOnReconnect  = true,
            ReEnableAutoSneakOnReconnect    = true,
            ReEnableAutoHideOnReconnect     = true,
            ReEnableAutoSearchOnReconnect   = true,
        };

        string json = JsonSerializer.Serialize(original);
        GeneralSettings? round = JsonSerializer.Deserialize<GeneralSettings>(json);

        Assert.NotNull(round);
        Assert.Equal(original.DefaultTask,         round!.DefaultTask);
        Assert.Equal(original.DefaultLoopName,     round.DefaultLoopName);
        Assert.Equal(original.DefaultAutoLairName, round.DefaultAutoLairName);
        Assert.Equal(original.AutoConnect,         round.AutoConnect);
        Assert.Equal(original.BackupOnSave,        round.BackupOnSave);
        AssertColumnsEqual(original.AutoMode,   round.AutoMode);
        Assert.True(round.AllowHangupInAllOffMode);
        Assert.True(round.ReEnableAutoCombatOnReconnect);
        Assert.True(round.ReEnableAutoNukeOnReconnect);
        Assert.True(round.ReEnableAutoHealRestOnReconnect);
        Assert.True(round.ReEnableAutoBlessOnReconnect);
        Assert.True(round.ReEnableAutoLightOnReconnect);
        Assert.True(round.ReEnableAutoGetItemsOnReconnect);
        Assert.True(round.ReEnableAutoGetCashOnReconnect);
        Assert.True(round.ReEnableAutoSneakOnReconnect);
        Assert.True(round.ReEnableAutoHideOnReconnect);
        Assert.True(round.ReEnableAutoSearchOnReconnect);
    }

    [Fact]
    public void PartialJson_FillsMissingFieldsWithDefaults()
    {
        // User hand-edits the profile and removes some fields — we must not
        // throw, and missing fields should fall back to type defaults.
        const string partial = """
            {
              "DefaultTask": 2,
              "AutoConnect": true
            }
            """;

        GeneralSettings? dto = JsonSerializer.Deserialize<GeneralSettings>(partial);

        Assert.NotNull(dto);
        Assert.Equal(InitialTask.BeginAutoLair, dto!.DefaultTask);
        Assert.True(dto.AutoConnect);
        Assert.False(dto.BackupOnSave);
        AssertDefaultColumn(dto.AutoMode);
    }

    [Fact]
    public void LegacyProfilesWithManualMode_DontThrow()
    {
        // Profiles written before the ManualMode field was removed will
        // still carry the key in JSON. System.Text.Json drops unknown
        // properties by default, so deserialisation must succeed and
        // the resulting AutoMode must hold the persisted value.
        const string legacy = """
            {
              "DefaultTask": 0,
              "AutoConnect": false,
              "BackupOnSave": false,
              "ManualMode": {
                "AutoCombat": false, "AutoNuke": false, "AutoHealRest": false,
                "AutoBless": false, "AutoLight": false, "AutoGetItems": false,
                "AutoGetCash": false, "AutoSneak": true, "AutoHide": true,
                "AutoSearch": true
              },
              "AutoMode": {
                "AutoCombat": true, "AutoNuke": false, "AutoHealRest": true,
                "AutoBless": false, "AutoLight": false, "AutoGetItems": true,
                "AutoGetCash": true, "AutoSneak": false, "AutoHide": false,
                "AutoSearch": false
              }
            }
            """;

        GeneralSettings? dto = JsonSerializer.Deserialize<GeneralSettings>(legacy);

        Assert.NotNull(dto);
        Assert.True(dto!.AutoMode.AutoCombat);
        Assert.True(dto.AutoMode.AutoHealRest);
        Assert.False(dto.AutoMode.AutoNuke);
    }

    private static AutoActionDefaults AllToggles(bool value) => new()
    {
        AutoCombat = value, AutoNuke = value, AutoHealRest = value,
        AutoBless = value, AutoLight = value, AutoGetItems = value,
        AutoGetCash = value, AutoSneak = value, AutoHide = value,
        AutoSearch = value,
    };

    private static void AssertDefaultColumn(AutoActionDefaults d)
    {
        // Combat / Nuke / Heal-Rest / Bless / Light / Get-Items / Get-Cash
        // default on; Sneak / Hide / Search default off (they're class-skill
        // dependent — a non-stealth class boot-engaging Auto-Sneak is noise).
        Assert.True(d.AutoCombat);
        Assert.True(d.AutoNuke);
        Assert.True(d.AutoHealRest);
        Assert.True(d.AutoBless);
        Assert.True(d.AutoLight);
        Assert.True(d.AutoGetItems);
        Assert.True(d.AutoGetCash);
        Assert.False(d.AutoSneak);
        Assert.False(d.AutoHide);
        Assert.False(d.AutoSearch);
    }

    private static void AssertColumnsEqual(AutoActionDefaults a, AutoActionDefaults b)
    {
        Assert.Equal(a.AutoCombat,   b.AutoCombat);
        Assert.Equal(a.AutoNuke,     b.AutoNuke);
        Assert.Equal(a.AutoHealRest, b.AutoHealRest);
        Assert.Equal(a.AutoBless,    b.AutoBless);
        Assert.Equal(a.AutoLight,    b.AutoLight);
        Assert.Equal(a.AutoGetItems, b.AutoGetItems);
        Assert.Equal(a.AutoGetCash,  b.AutoGetCash);
        Assert.Equal(a.AutoSneak,    b.AutoSneak);
        Assert.Equal(a.AutoHide,     b.AutoHide);
        Assert.Equal(a.AutoSearch,   b.AutoSearch);
    }
}
