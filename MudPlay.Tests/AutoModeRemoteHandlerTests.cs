using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// AutoModeRemoteHandler reads / writes
// AutoActionDefaults on the loaded profile in response
// to @auto-* remote commands.
public sealed class AutoModeRemoteHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Setup : IDisposable
    {
        public RemoteCommandManager Engine { get; }
        // The router the engine's chat lines arrive through.
        public MessageRouter Router { get; }
        // What the engine put on the wire, through the sender the client binds.
        public List<byte[]> Sent { get; } = new();
        public AutoModeRemoteHandler Handler { get; }
        public ProfileService Profile { get; }
        public PlayerDatabase Players { get; }

        public Setup()
        {
            MessageRouter router = new();
            Router = router;
            DefaultPatterns.Seed(router);
            ChatRouter chat = new(router);
            PartyState party = new();
            Players = new PlayerDatabase(new ProfileService(), () => null);
            Engine = new RemoteCommandManager(chat, party, Players);
            Engine.SetWireSender(Sent.Add);
            Profile = TestProfiles.Service();
            Profile.LoadDefaultProfile();
            AutoModeController controller = new(Profile);
            Handler = new AutoModeRemoteHandler(Engine, Profile, controller);
        }

        public void Dispose() => Handler.Dispose();
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

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
        s.Profile.Current!.Settings = new();
        AutoActionDefaults pre = ReadMode(s.Profile.Current!);
        Assert.True(pre.AutoCombat);    // default true

        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-combat off"));
        Assert.False(ReadMode(s.Profile.Current!).AutoCombat);

        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-combat on"));
        Assert.True(ReadMode(s.Profile.Current!).AutoCombat);
    }

    [Fact]
    public void AutoCombat_NoArg_TogglesAndRepliesNewState()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        // Default true → no-arg toggles to off and reports the new state.
        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-combat"));

        Assert.False(ReadMode(s.Profile.Current!).AutoCombat);
        string reply = Encoding.Latin1.GetString(s.Sent[^1]);
        Assert.Contains("@auto-combat", reply);
        Assert.Contains("off", reply);    // toggled from default on
    }

    [Fact]
    public void AutoRest_AndAutoHeal_AreSeparateSwitches()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-rest off"));
        Assert.False(ReadMode(s.Profile.Current!).AutoRest);
        Assert.True(ReadMode(s.Profile.Current!).AutoHeal);

        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-heal off"));
        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-rest on"));
        Assert.True(ReadMode(s.Profile.Current!).AutoRest);
        Assert.False(ReadMode(s.Profile.Current!).AutoHeal);
    }

    [Fact]
    public void AutoAll_NoArg_TogglesKillThenRestore()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        // Defaults have engines on → first no-arg toggle kills everything.
        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-all"));
        AutoActionDefaults killed = ReadMode(s.Profile.Current!);
        Assert.False(killed.AutoCombat);
        Assert.False(killed.AutoGetCash);

        // Second toggle restores the snapshot.
        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-all"));
        AutoActionDefaults restored = ReadMode(s.Profile.Current!);
        Assert.True(restored.AutoCombat);
        Assert.True(restored.AutoGetCash);
    }

    [Fact]
    public void AutoAll_Off_ClearsEveryFlag()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);
        // Start with defaults; flip everything off via @auto-all off.
        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-all off"));

        AutoActionDefaults mode = ReadMode(s.Profile.Current!);
        Assert.False(mode.AutoCombat);
        Assert.False(mode.AutoHeal);
        Assert.False(mode.AutoRest);
        Assert.False(mode.AutoGetCash);
        Assert.False(mode.AutoSneak);
        Assert.False(mode.AutoHide);
    }

    [Fact]
    public void AutoAll_On_RestoresEveryFlag()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);
        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-all off"));
        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-all on"));

        AutoActionDefaults mode = ReadMode(s.Profile.Current!);
        Assert.True(mode.AutoCombat);
        Assert.True(mode.AutoHeal);
        Assert.True(mode.AutoRest);
        Assert.True(mode.AutoGetCash);
    }

    [Fact]
    public void Garbage_RepliesWithQuestionMark()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-combat maybe"));

        string reply = Encoding.Latin1.GetString(s.Sent[^1]);
        Assert.Contains("?", reply);
    }

    [Fact]
    public void Sneak_OffArg_TogglesAutoSneak()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-sneak off"));
        Assert.False(ReadMode(s.Profile.Current!).AutoSneak);
    }

    [Fact]
    public void Settings_RepliesEachEngineState()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        ChatLines.Hear(s.Router, Telepath("Tank", "@settings"));

        string reply = Encoding.Latin1.GetString(s.Sent[^1]);
        Assert.Contains("Auto-Combat: On", reply);     // default on
        Assert.Contains("Auto-Light: Off", reply);      // default off
        Assert.Contains("Auto-Heal: On", reply);
        Assert.Contains("Auto-Rest: On", reply);
    }

    [Fact]
    public void Settings_ReflectsFlippedFlag()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        ChatLines.Hear(s.Router, Telepath("Tank", "@auto-combat off"));
        ChatLines.Hear(s.Router, Telepath("Tank", "@settings"));

        string reply = Encoding.Latin1.GetString(s.Sent[^1]);
        Assert.Contains("Auto-Combat: Off", reply);
    }
}
