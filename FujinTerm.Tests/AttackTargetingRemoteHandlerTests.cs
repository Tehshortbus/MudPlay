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
/// <see cref="AttackTargetingRemoteHandler"/> reads / writes
/// <see cref="CombatSettings.TargetPriority"/> (<c>@atkprio</c>) and
/// <see cref="CombatSettings.AttackTiming"/> (<c>@atkorder</c>) on the loaded
/// profile in response to the two numbered-option remote commands. Pins the
/// bare-query reply, each numeric set, the name-requiring options, and the
/// "command failed" syntax-error path.
/// </summary>
public sealed class AttackTargetingRemoteHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Setup : IDisposable
    {
        public RemoteCommandManager Engine { get; }
        public AttackTargetingRemoteHandler Handler { get; }
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
            Handler = new AttackTargetingRemoteHandler(Engine, Profile);
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

    private static CombatSettings ReadCombat(CharacterProfile p)
    {
        if (p.Settings is null || !p.Settings.TryGetValue("Combat", out JsonElement json))
            return new CombatSettings();
        return JsonSerializer.Deserialize<CombatSettings>(json.GetRawText()) ?? new CombatSettings();
    }

    private static string LastReply(Setup s) =>
        Encoding.Latin1.GetString(s.Engine.LastSentForTests[^1]);

    // ----- @atkprio -------------------------------------------------------

    [Fact]
    public void Prio_BareCommand_RepliesCurrentAndOptions()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkprio"));

        string reply = LastReply(s);
        Assert.Contains("@atkprio", reply);
        Assert.Contains("1 Default", reply);          // default selection
        Assert.Contains("Options:", reply);
        Assert.Contains("2=", reply);
        Assert.Contains("3=", reply);
    }

    [Fact]
    public void Prio_One_SetsDefault()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkprio 1"));

        CombatSettings dto = ReadCombat(s.Profile.Current!);
        Assert.Equal(TargetPriority.Default, dto.TargetPriority);
        Assert.Null(dto.TargetPriorityMemberName);
    }

    [Fact]
    public void Prio_Two_SetsFollowLeader()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkprio 2"));

        CombatSettings dto = ReadCombat(s.Profile.Current!);
        Assert.Equal(TargetPriority.FollowLeader, dto.TargetPriority);
        Assert.Null(dto.TargetPriorityMemberName);
    }

    [Fact]
    public void Prio_ThreeWithName_SetsFollowMemberAndStoresName()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkprio 3 Bob"));

        CombatSettings dto = ReadCombat(s.Profile.Current!);
        Assert.Equal(TargetPriority.FollowMember, dto.TargetPriority);
        Assert.Equal("Bob", dto.TargetPriorityMemberName);

        string reply = LastReply(s);
        Assert.Contains("Bob", reply);
    }

    [Fact]
    public void Prio_ThreeWithoutName_Fails()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkprio 3"));

        string reply = LastReply(s);
        Assert.Contains("command failed", reply);
    }

    [Fact]
    public void Prio_TwoWithExtraToken_Fails()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkprio 2 Bob"));

        string reply = LastReply(s);
        Assert.Contains("command failed", reply);
    }

    [Fact]
    public void Prio_NonNumeric_Fails()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkprio leader"));

        string reply = LastReply(s);
        Assert.Contains("command failed", reply);
    }

    [Fact]
    public void Prio_UnknownNumber_Fails()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkprio 9"));

        string reply = LastReply(s);
        Assert.Contains("command failed", reply);
    }

    [Fact]
    public void Prio_BareQuery_ReflectsStoredMemberName()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkprio 3 Bob"));
        s.Engine.DispatchForTests(Telepath("Tank", "@atkprio"));

        string reply = LastReply(s);
        Assert.Contains("3 Attack what player attacks", reply);
        Assert.Contains("(Bob)", reply);
    }

    // ----- @atkorder ------------------------------------------------------

    [Fact]
    public void Order_BareCommand_RepliesCurrentAndOptions()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkorder"));

        string reply = LastReply(s);
        Assert.Contains("@atkorder", reply);
        Assert.Contains("1 Default", reply);
        Assert.Contains("Options:", reply);
        Assert.Contains("4=", reply);
    }

    [Fact]
    public void Order_Two_SetsAttackLastParty()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkorder 2"));

        CombatSettings dto = ReadCombat(s.Profile.Current!);
        Assert.Equal(AttackTiming.AttackLastParty, dto.AttackTiming);
        Assert.Null(dto.AttackAfterPlayerName);
    }

    [Fact]
    public void Order_Three_SetsAttackLastRoom()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkorder 3"));

        CombatSettings dto = ReadCombat(s.Profile.Current!);
        Assert.Equal(AttackTiming.AttackLastRoom, dto.AttackTiming);
    }

    [Fact]
    public void Order_FourWithName_SetsAttackAfterAndStoresName()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkorder 4 Alice"));

        CombatSettings dto = ReadCombat(s.Profile.Current!);
        Assert.Equal(AttackTiming.AttackAfter, dto.AttackTiming);
        Assert.Equal("Alice", dto.AttackAfterPlayerName);
    }

    [Fact]
    public void Order_FourWithoutName_Fails()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkorder 4"));

        string reply = LastReply(s);
        Assert.Contains("command failed", reply);
    }

    [Fact]
    public void Order_One_ClearsName()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkorder 4 Alice"));
        s.Engine.DispatchForTests(Telepath("Tank", "@atkorder 1"));

        CombatSettings dto = ReadCombat(s.Profile.Current!);
        Assert.Equal(AttackTiming.Default, dto.AttackTiming);
        Assert.Null(dto.AttackAfterPlayerName);
    }

    [Fact]
    public void Order_NonNumeric_Fails()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Tank", PlayerRemoteControls.AlterSettings);

        s.Engine.DispatchForTests(Telepath("Tank", "@atkorder last"));

        string reply = LastReply(s);
        Assert.Contains("command failed", reply);
    }

    // ----- Permission gating ---------------------------------------------

    [Fact]
    public void Prio_WithoutAlterSettings_DoesNotWrite()
    {
        using Setup s = new();
        SeedPlayer(s.Players, "Rando", PlayerRemoteControls.QueryHealthStatus);

        s.Engine.DispatchForTests(Telepath("Rando", "@atkprio 2"));

        CombatSettings dto = ReadCombat(s.Profile.Current!);
        Assert.Equal(TargetPriority.Default, dto.TargetPriority);
    }
}
