using System.Text;
using System.Text.Json;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// Cluster 5d — <see cref="AutoModeRemoteHandler"/> reads / writes
/// <see cref="AutoActionDefaults"/> on the loaded profile in response
/// to <c>@auto-*</c> remote commands.
/// </summary>
public sealed class AutoModeRemoteHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Setup : IDisposable
    {
        public RemoteCommandManager Engine { get; }
        public AutoModeRemoteHandler Handler { get; }
        public ProfileService Profile { get; }
        public PlayerDatabase Players { get; }

        public Setup()
        {
            MessageRouter router = new();
            DefaultPatterns.Seed(router);
            ChatRouter chat = new(router);
            PartyState party = new();
            Players = new PlayerDatabase();
            Engine = new RemoteCommandManager(chat, party, Players);
            Profile = new ProfileService();
            Profile.LoadBlank();
            AutoModeController controller = new(Profile);
            Handler = new AutoModeRemoteHandler(Engine, Profile, controller);
        }

        public void Dispose() => Handler.Dispose();
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static AutoActionDefaults ReadMode(CharacterProfile p)
    {
        if (p.Settings is null || !p.Settings.TryGetValue("General", out JsonElement json))
            return new AutoActionDefaults();
        GeneralSettings g = JsonSerializer.Deserialize<GeneralSettings>(json.GetRawText())!;
        return g.AutoMode;
    }

    [Fact]
    public void AutoCombat_OnArg_TogglesFlag()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);
        // Force default off to make the off→on flip observable.
        s.Profile.Current!.Settings = new();
        AutoActionDefaults pre = ReadMode(s.Profile.Current!);
        Assert.True(pre.AutoCombat);    // default true

        s.Engine.DispatchForTests(Telepath("Tank", "@auto-combat off"));
        Assert.False(ReadMode(s.Profile.Current!).AutoCombat);

        s.Engine.DispatchForTests(Telepath("Tank", "@auto-combat on"));
        Assert.True(ReadMode(s.Profile.Current!).AutoCombat);
    }

    [Fact]
    public void AutoCombat_NoArg_TogglesAndRepliesNewState()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        // Default true → no-arg toggles to off and reports the new state.
        s.Engine.DispatchForTests(Telepath("Tank", "@auto-combat"));

        Assert.False(ReadMode(s.Profile.Current!).AutoCombat);
        string reply = Encoding.Latin1.GetString(s.Engine.LastSentForTests[^1]);
        Assert.Contains("@auto-combat", reply);
        Assert.Contains("off", reply);    // toggled from default on
    }

    [Fact]
    public void AutoRest_AliasesAutoHealRest()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@auto-rest off"));
        Assert.False(ReadMode(s.Profile.Current!).AutoHealRest);

        s.Engine.DispatchForTests(Telepath("Tank", "@auto-heal on"));
        Assert.True(ReadMode(s.Profile.Current!).AutoHealRest);
    }

    [Fact]
    public void AutoAll_NoArg_TogglesKillThenRestore()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        // Defaults have engines on → first no-arg toggle kills everything.
        s.Engine.DispatchForTests(Telepath("Tank", "@auto-all"));
        AutoActionDefaults killed = ReadMode(s.Profile.Current!);
        Assert.False(killed.AutoCombat);
        Assert.False(killed.AutoGetCash);

        // Second toggle restores the snapshot.
        s.Engine.DispatchForTests(Telepath("Tank", "@auto-all"));
        AutoActionDefaults restored = ReadMode(s.Profile.Current!);
        Assert.True(restored.AutoCombat);
        Assert.True(restored.AutoGetCash);
    }

    [Fact]
    public void AutoAll_On_FlipsEveryFlag()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);
        // Start with defaults; flip everything off via @auto-all off.
        s.Engine.DispatchForTests(Telepath("Tank", "@auto-all off"));

        AutoActionDefaults mode = ReadMode(s.Profile.Current!);
        Assert.False(mode.AutoCombat);
        Assert.False(mode.AutoHealRest);
        Assert.False(mode.AutoGetCash);
        Assert.False(mode.AutoSneak);
        Assert.False(mode.AutoHide);
    }

    [Fact]
    public void AutoAll_On_RestoresEveryFlag()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);
        s.Engine.DispatchForTests(Telepath("Tank", "@auto-all off"));
        s.Engine.DispatchForTests(Telepath("Tank", "@auto-all on"));

        AutoActionDefaults mode = ReadMode(s.Profile.Current!);
        Assert.True(mode.AutoCombat);
        Assert.True(mode.AutoHealRest);
        Assert.True(mode.AutoGetCash);
    }

    [Fact]
    public void Garbage_RepliesWithQuestionMark()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@auto-combat maybe"));

        string reply = Encoding.Latin1.GetString(s.Engine.LastSentForTests[^1]);
        Assert.Contains("?", reply);
    }

    [Fact]
    public void Sneak_OffArg_TogglesAutoSneak()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@auto-sneak off"));
        Assert.False(ReadMode(s.Profile.Current!).AutoSneak);
    }

    [Fact]
    public void Settings_RepliesEachEngineState()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@settings"));

        string reply = Encoding.Latin1.GetString(s.Engine.LastSentForTests[^1]);
        Assert.Contains("Auto-Combat: On", reply);     // default on
        Assert.Contains("Auto-Sneak: Off", reply);      // default off
        // @auto-rest aliases @auto-heal — must not double-report.
        Assert.DoesNotContain("Auto-Rest", reply);
        Assert.Contains("Auto-Heal: On", reply);
    }

    [Fact]
    public void Settings_ReflectsFlippedFlag()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@auto-combat off"));
        s.Engine.DispatchForTests(Telepath("Tank", "@settings"));

        string reply = Encoding.Latin1.GetString(s.Engine.LastSentForTests[^1]);
        Assert.Contains("Auto-Combat: Off", reply);
    }
}
