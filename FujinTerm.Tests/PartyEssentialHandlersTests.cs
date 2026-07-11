using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Remote;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using Xunit;

namespace FujinTerm.Tests;

public sealed class PartyEssentialHandlersTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Build a self-contained engine + handlers + the state they read from.
    /// Tests dispatch via <see cref="RemoteCommandManager.DispatchForTests"/>
    /// and inspect <see cref="RemoteCommandManager.LastSentForTests"/> for
    /// replies. <see cref="PartyEssentialHandlers"/>' own wire-sender is
    /// captured separately so we can assert on the @party-relay path.
    /// </summary>
    private static (RemoteCommandManager engine, PartyEssentialHandlers handlers, PlayerState player, PartyState party, PlayerDatabase players, List<byte[]> partyRelay) Setup(
        FujinTerm.Game.Map.Room? currentRoom = null,
        IReadOnlyList<FujinTerm.Game.Combat.RoomEntity>? roomEntities = null,
        MovementStatus? movement = null,
        Func<string?>? readDraggedBy = null,
        MessageFlags ailments = MessageFlags.None)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        PlayerState player = new();
        RemoteCommandManager engine = new(chat, party, players);
        PartyEssentialHandlers handlers = new(engine, player, party,
            readCurrentRoom: () => currentRoom,
            readRoomEntities: () => roomEntities,
            readMovement: () => movement ?? default,
            readDraggedBy: readDraggedBy,
            readAilments: () => ailments);
        List<byte[]> relayCapture = new();
        handlers.SetWireSender(relayCapture.Add);
        return (engine, handlers, player, party, players, relayCapture);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static void SeedPartyMember(PartyState p, string name, bool isLeader = false)
    {
        p.Members.Add(new PartyMember { Name = name, IsLeader = isLeader });
        p.IsInParty = true;
    }

    private static string LastReply(RemoteCommandManager e) =>
        Encoding.Latin1.GetString(e.LastSentForTests[^1]);

    // ===== Registration shape =====

    [Fact]
    public void Ctor_RegistersAllCommands()
    {
        var (engine, _, _, _, _, _) = Setup();
        // 12 commands: @version @health @status @where @who @path @party
        // @wait @ok @lives @invite @join
        Assert.Equal(12, engine.HandlerCount);
    }

    [Fact]
    public void Dispose_UnregistersAll()
    {
        var (engine, handlers, _, _, _, _) = Setup();
        handlers.Dispose();
        Assert.Equal(0, engine.HandlerCount);
    }

    // ===== Query handlers =====

    [Fact]
    public void Version_RepliesWithDisplayName()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryVersion);
        engine.DispatchForTests(Telepath("Friend", "@version"));

        Assert.Contains("FujinTerm", LastReply(engine));
    }

    [Fact]
    public void Version_RepliesWithNameAndVersion_MatchingOtherClientShape()
    {
        // Matches the format other clients use (e.g. MegaMUD replies
        // "{MegaMud 1.03u}"): "<name> <version>" inside the braces.
        // Version comes from AppInfo.Version which reads the
        // compiled assembly's InformationalVersion — so a csproj
        // <Version> bump propagates automatically.
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryVersion);
        engine.DispatchForTests(Telepath("Friend", "@version"));

        string reply = LastReply(engine);
        Assert.Contains($"{{FujinTerm {Services.AppInfo.Version}}}", reply);
        // Sanity: the version actually loaded (not the "unknown" fallback).
        Assert.NotEqual("unknown", Services.AppInfo.Version);
    }

    [Fact]
    public void Health_NoPromptData_RepliesUnknown()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.DispatchForTests(Telepath("Friend", "@health"));

        Assert.Contains("HP unknown", LastReply(engine));
    }

    [Fact]
    public void Health_WhenResting_AppendsRestingSuffix()
    {
        var (engine, _, player, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Hp = 100; player.MaxHp = 150;
        player.Ma = 75;  player.MaxMa = 85;
        player.ManaType = ManaType.Mana;
        player.Position = PlayerPosition.Resting;
        player.HasPromptData = true;

        engine.DispatchForTests(Telepath("Friend", "@health"));

        // Full wire: /<given> {<payload>}\r. The reply body is
        // brace-wrapped at SendReply per the remote-command convention.
        Assert.Equal("/Friend {HP=100/150,MA=75/85, Resting}\r", LastReply(engine));
    }

    [Fact]
    public void Health_WhenStanding_OmitsPositionSuffix()
    {
        // Standing is the idle default — adding "(Standing)" gives the
        // recipient no usable signal, so the payload skips the suffix
        // entirely. Recipient reads "HP=…, MA=…" and infers idle.
        var (engine, _, player, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Hp = 100; player.MaxHp = 150;
        player.Ma = 75;  player.MaxMa = 85;
        player.ManaType = ManaType.Mana;
        player.Position = PlayerPosition.Standing;
        player.HasPromptData = true;

        engine.DispatchForTests(Telepath("Friend", "@health"));
        Assert.Equal("/Friend {HP=100/150,MA=75/85}\r", LastReply(engine));
    }

    [Fact]
    public void Health_WhenMeditating_AppendsMeditatingSuffix()
    {
        var (engine, _, player, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Hp = 100; player.MaxHp = 150;
        player.Ma = 75;  player.MaxMa = 85;
        player.ManaType = ManaType.Mana;
        player.Position = PlayerPosition.Meditating;
        player.HasPromptData = true;

        engine.DispatchForTests(Telepath("Friend", "@health"));
        Assert.Equal("/Friend {HP=100/150,MA=75/85, Meditating}\r", LastReply(engine));
    }

    [Fact]
    public void Health_KaiClass_UsesKaiLabel()
    {
        var (engine, _, player, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Hp = 500; player.MaxHp = 500;
        player.Ma = 150; player.MaxMa = 150;
        player.ManaType = ManaType.Kai;
        player.HasPromptData = true;

        engine.DispatchForTests(Telepath("Friend", "@health"));
        Assert.Equal("/Friend {HP=500/500,KAI=150/150}\r", LastReply(engine));
    }

    [Fact]
    public void Health_NoMana_OmitsManaSegment()
    {
        // Warrior / no-mana class — ManaType.None. The MA / KAI segment
        // simply isn't emitted; the bare reply is HP-only.
        var (engine, _, player, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Hp = 200; player.MaxHp = 200;
        player.ManaType = ManaType.None;
        player.HasPromptData = true;

        engine.DispatchForTests(Telepath("Friend", "@health"));
        Assert.Equal("/Friend {HP=200/200}\r", LastReply(engine));
    }

    [Fact]
    public void Status_EmitsPositionNavAndAilments()
    {
        var (engine, _, player, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Meditating;
        player.HasPromptData = true;

        engine.DispatchForTests(Telepath("Friend", "@status"));
        // Position; nav-engine phrase; ailment clause — all three present.
        Assert.Equal("/Friend {Meditating; not moving; no ailments}\r", LastReply(engine));
    }

    [Fact]
    public void Status_WhenWalking_ReportsMovementEngine()
    {
        MovementStatus mv = new(MovementKind.Walking, "5/1141", CurrentStep: 0, TotalSteps: 4);
        var (engine, _, player, _, players, _) = Setup(movement: mv);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Standing;
        player.HasPromptData = true;

        engine.DispatchForTests(Telepath("Friend", "@status"));
        Assert.Contains("walking to 5/1141", LastReply(engine));
    }

    [Fact]
    public void Status_ListsActiveCurableAilments_InTableOrder()
    {
        var (engine, _, player, _, players, _) = Setup(
            ailments: MessageFlags.Blinded | MessageFlags.Poisoned);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Standing;
        player.HasPromptData = true;

        engine.DispatchForTests(Telepath("Friend", "@status"));
        // Table order is poison, blind, confused, diseased.
        Assert.Contains("ailments: poisoned, blind", LastReply(engine));
    }

    [Fact]
    public void Status_HeldIsNotReportedAsAnAilment()
    {
        // Held is excluded from the @status clause — its chip rides @ok, so the
        // pull-based reconcile must not see it and clear it independently.
        var (engine, _, player, _, players, _) = Setup(
            ailments: MessageFlags.MovementPrevented);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Standing;
        player.HasPromptData = true;

        engine.DispatchForTests(Telepath("Friend", "@status"));
        Assert.Contains("no ailments", LastReply(engine));
    }

    [Fact]
    public void PartyTelepath_NoParty_RepliesNoActiveParty()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.DispatchForTests(Telepath("Friend", "@party"));

        Assert.Contains("no active party", LastReply(engine));
    }

    [Fact]
    public void PartyTelepath_WhenFollowing_RepliesImFollowing()
    {
        var (engine, _, _, party, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        party.Members.Add(new PartyMember { Name = "Fujin Wuz", IsLeader = true });
        party.Members.Add(new PartyMember { Name = "Raijin", IsSelf = true });
        party.LeaderName = "Fujin Wuz";
        party.IsInParty = true;
        party.SelfIsLeader = false;

        engine.DispatchForTests(Telepath("Friend", "@party"));

        // Given name only — family is stripped (the @-command layer
        // never addresses players by family name).
        Assert.Contains("{I'm following Fujin}", LastReply(engine));
    }

    [Fact]
    public void PartyTelepath_WhenLeading_RepliesImLeadingWithFollowerGivenNames()
    {
        var (engine, _, _, party, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        party.Members.Add(new PartyMember { Name = "Fujin", IsLeader = true, IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin Wuz" });
        party.Members.Add(new PartyMember { Name = "Helper" });
        party.LeaderName = "Fujin";
        party.IsInParty = true;
        party.SelfIsLeader = true;

        engine.DispatchForTests(Telepath("Friend", "@party"));

        // Family-stripped given names, comma-separated, roster order,
        // excluding self + leader.
        Assert.Contains("{I'm leading: Raijin, Helper}", LastReply(engine));
    }

    [Fact]
    public void Party_FromTelepath_ReturnsStatusReplyEvenWithArgs()
    {
        // Telepath/Gangpath always status — ignore args so a back-
        // channel @party rest can't trigger the destructive verb path.
        // Sender is a party member (engine party-whitelist gate).
        var (engine, _, _, party, _, relay) = Setup();
        party.Members.Add(new PartyMember { Name = "Fujin", IsLeader = true, IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Helper" });
        party.LeaderName = "Fujin";
        party.IsInParty = true;
        party.SelfIsLeader = true;

        engine.DispatchForTests(Telepath("Helper", "@party rest"));

        Assert.Contains("{I'm leading: Helper}", LastReply(engine));
        // No sub-command dispatched.
        Assert.Empty(relay);
    }

    [Fact]
    public void Party_FromSay_NoArgs_ReturnsStatusReply()
    {
        // Solo case — sender is themselves in our (non-existent) party
        // only by virtue of the engine's party-whitelist gate accepting
        // ALL senders when IsInParty=false... actually no: the engine
        // denies when not in a party. So this scenario requires the
        // sender to be in the party. Reflect the realistic case: party
        // member says "@party" in the room with no args.
        var (engine, _, _, party, _, _) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);

        ChatLogEntry say = new(Now, ChatChannel.Local, "Leader",
            "@party", "Leader says: @party");
        engine.DispatchForTests(say);

        // We're "leading" Leader (the only member) — leader path with
        // no followers other than self.
        string reply = LastReply(engine);
        Assert.True(
            reply.Contains("I'm leading") || reply.Contains("I'm following") || reply.Contains("no active party"),
            $"Unexpected reply: {reply}");
    }

    [Fact]
    public void Party_FromSay_WithArgs_DispatchesWhenPartyMember()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);

        ChatLogEntry say = new(Now, ChatChannel.Local, "Leader",
            "@party rest", "Leader says: @party rest");
        engine.DispatchForTests(say);

        Assert.Equal("rest\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void Where_WithoutRoom_RepliesLocationUnknown()
    {
        // No RoomTracker snapshot (Unknown / Lost, or no game data) → the
        // accessor returns null and @where degrades to a plain message.
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        engine.DispatchForTests(Telepath("Friend", "@where"));

        Assert.Contains("Location unknown", LastReply(engine));
    }

    [Fact]
    public void Where_RepliesRoomNameExitsAndKey()
    {
        FujinTerm.Game.Map.Room room = new()
        {
            Key = new FujinTerm.Game.Map.RoomKey(5, 1141),
            Name = "Town Square",
            Exits = new Dictionary<FujinTerm.Game.Map.Direction, FujinTerm.Game.Map.RoomExit>
            {
                [FujinTerm.Game.Map.Direction.N] =
                    new(new FujinTerm.Game.Map.RoomKey(5, 1140), FujinTerm.Game.Map.RoomExitHint.None, null),
                [FujinTerm.Game.Map.Direction.E] =
                    new(new FujinTerm.Game.Map.RoomKey(5, 1142), FujinTerm.Game.Map.RoomExitHint.None, null),
            },
        };
        var (engine, _, _, _, players, _) = Setup(room);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        engine.DispatchForTests(Telepath("Friend", "@where"));

        string reply = LastReply(engine);
        Assert.Contains("Town Square", reply);
        Assert.Contains("map 5", reply);
        Assert.Contains("room 1141", reply);
        Assert.Contains("north", reply);
        Assert.Contains("east", reply);
    }

    [Fact]
    public void Who_WithNoOccupants_RepliesNoOne()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        engine.DispatchForTests(Telepath("Friend", "@who"));

        Assert.Contains("no one", LastReply(engine));
    }

    [Fact]
    public void Who_RepliesResolvedOccupantNames()
    {
        var entities = new List<FujinTerm.Game.Combat.RoomEntity>
        {
            new("Raijin", "Raijin", FujinTerm.Game.Combat.EntityKind.Player, null),
            new("Susanoo", "Susanoo", FujinTerm.Game.Combat.EntityKind.Player, null),
            new("nasty giant rat", "giant rat", FujinTerm.Game.Combat.EntityKind.Monster, 42),
        };
        var (engine, _, _, _, players, _) = Setup(roomEntities: entities);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        engine.DispatchForTests(Telepath("Friend", "@who"));

        string reply = LastReply(engine);
        Assert.Contains("Raijin", reply);
        Assert.Contains("Susanoo", reply);
        Assert.Contains("giant rat", reply);
        // Resolved (canonical) name, not the flavour-prefixed raw form.
        Assert.DoesNotContain("nasty", reply);
    }

    [Fact]
    public void Path_WhenIdle_RepliesNotMoving()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        engine.DispatchForTests(Telepath("Friend", "@path"));

        Assert.Contains("not moving", LastReply(engine));
    }

    [Fact]
    public void Path_WhenLooping_RepliesLoopNameRoomAndStep()
    {
        FujinTerm.Game.Map.Room room = new()
        {
            Key = new FujinTerm.Game.Map.RoomKey(9, 1012),
            Name = "Sewer Tunnel",
            Exits = new Dictionary<FujinTerm.Game.Map.Direction, FujinTerm.Game.Map.RoomExit>(),
        };
        MovementStatus mv = new(MovementKind.Loop, "Sewer farm", CurrentStep: 2, TotalSteps: 6);
        var (engine, _, _, _, players, _) = Setup(currentRoom: room, movement: mv);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        engine.DispatchForTests(Telepath("Friend", "@path"));

        string reply = LastReply(engine);
        Assert.Contains("Sewer farm", reply);
        Assert.Contains("Sewer Tunnel", reply);
        Assert.Contains("map 9", reply);
        Assert.Contains("room 1012", reply);
        // 0-based CurrentStep 2 reports 1-based as step 3.
        Assert.Contains("step 3/6", reply);
    }

    [Fact]
    public void Path_WhenWalking_RepliesDestination()
    {
        MovementStatus mv = new(MovementKind.Walking, "5/1141", CurrentStep: 0, TotalSteps: 4);
        var (engine, _, _, _, players, _) = Setup(movement: mv);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        engine.DispatchForTests(Telepath("Friend", "@path"));

        string reply = LastReply(engine);
        Assert.Contains("walking to 5/1141", reply);
        Assert.Contains("step 1/4", reply);
    }

    // ===== @party <sub> whitelist (Say-channel sub-command dispatch) ====
    // Dispatch only happens in Local (say) channel — telepath/gangpath
    // always return the status reply (covered in the Party_FromTelepath_*
    // tests above).

    private static ChatLogEntry Say(string sender, string msg) =>
        new(Now, ChatChannel.Local, sender, msg, $"{sender} says: {msg}");

    [Fact]
    public void PartyRest_FromPartyMember_SendsRestToWire()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Say("Leader", "@party rest"));

        Assert.Equal("rest\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyMeditate_MapsToShortForm()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Say("Leader", "@party meditate"));

        Assert.Equal("medi\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyGo_ForwardsDirectionToken()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Say("Leader", "@party go n"));

        Assert.Equal("n\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyStat_PartyI_PartyPar_AllRelay()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Say("Leader", "@party stat"));
        engine.DispatchForTests(Say("Leader", "@party i"));
        engine.DispatchForTests(Say("Leader", "@party par"));

        Assert.Equal(3, relay.Count);
        Assert.Equal("stat\r", Encoding.Latin1.GetString(relay[0]));
        Assert.Equal("i\r",    Encoding.Latin1.GetString(relay[1]));
        Assert.Equal("par\r",  Encoding.Latin1.GetString(relay[2]));
    }

    // @party <command> is a party-membership-gated passthrough (the party
    // analogue of @do): anything outside the two token rewrites relays verbatim.
    // A follower who never executes the leader's `use chime` never teleports, so
    // the whole CMD-teleport reform choreography hinged on this passthrough.
    [Fact]
    public void PartyUseChime_RelaysVerbatim()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Say("Leader", "@party use chime"));

        Assert.Single(relay);
        Assert.Equal("use chime\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyRingChime_RelaysVerbatim()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Say("Leader", "@party ring chime"));

        Assert.Single(relay);
        Assert.Equal("ring chime\r", Encoding.Latin1.GetString(relay[0]));
    }

    // A say-precursor'd command (`.hi` = say "hi") relays verbatim too — the
    // reported case where @party stat worked (whitelisted) but @party .hi didn't.
    [Fact]
    public void PartySayPrecursor_RelaysVerbatim()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Say("Leader", "@party .hi"));

        Assert.Single(relay);
        Assert.Equal(".hi\r", Encoding.Latin1.GetString(relay[0]));
    }

    // A multi-word arbitrary command survives whole (single-space collapsed).
    [Fact]
    public void PartyMultiWordCommand_RelaysVerbatim()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Say("Leader", "@party cast heal raijin"));

        Assert.Single(relay);
        Assert.Equal("cast heal raijin\r", Encoding.Latin1.GetString(relay[0]));
    }

    // Plain `suicide` relays like any other passthrough — only the `set suicide`
    // phrase and reroll are refused (both blocked at engine level before this
    // handler runs, verified below and in RemoteCommandManagerTests).
    [Fact]
    public void PartyPlainSuicide_RelaysVerbatim()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Say("Leader", "@party suicide"));

        Assert.Single(relay);
        Assert.Equal("suicide\r", Encoding.Latin1.GetString(relay[0]));
    }

    // The two engine-level hard-blocks never reach the wire relay: `set suicide`
    // arms unattended auto-suicide and reroll wipes the build.
    [Fact]
    public void PartySetSuicide_NotRelayed()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Say("Leader", "@party set suicide hunter2"));

        Assert.Empty(relay);
    }

    [Fact]
    public void PartyReroll_NotRelayed()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Say("Leader", "@party reroll"));

        Assert.Empty(relay);
    }

    [Fact]
    public void PartyFromNonMember_NotDispatched()
    {
        // Non-party say-@party still gets a status reply (engine
        // accepts QueryHealthStatus from players with that permission)
        // but the sub-command dispatch is gated on IsActivePartyMember,
        // so the wire-relay stays empty. We seed the stranger with
        // QueryHealthStatus so the engine doesn't refuse first.
        var (engine, _, _, _, players, relay) = Setup();
        SeedPlayer(players, "Stranger", PlayerRemoteControls.QueryHealthStatus);
        engine.DispatchForTests(Say("Stranger", "@party rest"));

        Assert.Empty(relay);
    }

    // ===== @wait / @ok wait-set =====

    [Fact]
    public void Wait_AddsToWaitingMembers()
    {
        var (engine, handlers, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");

        engine.DispatchForTests(Telepath("Follower", "@wait"));

        Assert.Contains("Follower", handlers.WaitingMembers);
    }

    [Fact]
    public void Ok_RemovesFromWaitingMembers()
    {
        var (engine, handlers, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");
        engine.DispatchForTests(Telepath("Follower", "@wait"));
        Assert.Contains("Follower", handlers.WaitingMembers);

        engine.DispatchForTests(Telepath("Follower", "@ok"));
        Assert.DoesNotContain("Follower", handlers.WaitingMembers);
    }

    [Fact]
    public void Wait_FromNonPartyMember_IsIgnored()
    {
        // @wait is party-whitelist-gated by the engine; the wait-set
        // should never see a non-party sender.
        var (engine, handlers, _, _, _, _) = Setup();
        engine.DispatchForTests(Telepath("Stranger", "@wait"));

        Assert.Empty(handlers.WaitingMembers);
    }

    [Fact]
    public void WaitingMembers_IsCaseInsensitive()
    {
        var (engine, handlers, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");
        engine.DispatchForTests(Telepath("Follower", "@wait"));
        // The @ok comes back with different casing — same player.
        engine.DispatchForTests(Telepath("follower", "@ok"));

        Assert.Empty(handlers.WaitingMembers);
    }

    // ===== Per-member IsWaiting flag (drives PartyWindow's WAIT chip) =====

    [Fact]
    public void Wait_FlipsMatchingMembersIsWaiting()
    {
        var (engine, _, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");
        PartyMember row = party.Members[0];
        Assert.False(row.IsWaiting);

        engine.DispatchForTests(Telepath("Follower", "@wait"));

        Assert.True(row.IsWaiting);
    }

    [Fact]
    public void Ok_ClearsMatchingMembersIsWaiting()
    {
        var (engine, _, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");
        PartyMember row = party.Members[0];
        engine.DispatchForTests(Telepath("Follower", "@wait"));
        Assert.True(row.IsWaiting);

        engine.DispatchForTests(Telepath("Follower", "@ok"));
        Assert.False(row.IsWaiting);
    }

    [Fact]
    public void Wait_MatchesByGivenName_AcrossGivenAndFamilyForms()
    {
        // par may have surfaced the member as "Given Family" while the
        // telepath arrives with just "Given" — engine matches on the
        // given-name prefix.
        var (engine, _, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower Lastname");
        PartyMember row = party.Members[0];

        engine.DispatchForTests(Telepath("Follower", "@wait"));

        Assert.True(row.IsWaiting);
    }

    [Fact]
    public void Wait_FromNonPartyMember_DoesNotFlipAnyRow()
    {
        // Engine drops @wait from non-party senders (party-whitelist gate);
        // and since the sender isn't in the roster, no row should change.
        var (engine, _, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");
        PartyMember row = party.Members[0];

        engine.DispatchForTests(Telepath("Stranger", "@wait"));

        Assert.False(row.IsWaiting);
    }

    // ===== @lives =====

    [Fact]
    public void Lives_NoLivesProvider_RepliesUnknown()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.LivesProvider = null;

        engine.DispatchForTests(Telepath("Friend", "@lives"));

        Assert.Contains("lives unknown", LastReply(engine));
    }

    [Fact]
    public void Lives_ProviderReturnsNull_RepliesUnknown()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.LivesProvider = () => null;

        engine.DispatchForTests(Telepath("Friend", "@lives"));

        Assert.Contains("lives unknown", LastReply(engine));
    }

    [Fact]
    public void Lives_ProviderReturnsValue_RepliesWithCount()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.LivesProvider = () => 4;

        engine.DispatchForTests(Telepath("Friend", "@lives"));

        Assert.Contains("4 lives remaining", LastReply(engine));
    }

    [Fact]
    public void Lives_SingularGrammar_WhenOneLeft()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.LivesProvider = () => 1;

        engine.DispatchForTests(Telepath("Friend", "@lives"));

        Assert.Contains("1 life remaining", LastReply(engine));
    }

    // ===== @invite =====

    [Fact]
    public void Invite_WhenSolo_SendsInviteWithSenderGivenName()
    {
        var (engine, _, _, _, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);

        engine.DispatchForTests(Telepath("Raijin", "@invite"));

        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void Invite_WhenFollower_DeniesWithLeaderName()
    {
        var (engine, _, _, party, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        party.Members.Add(new PartyMember { Name = "Fujin Wuz", IsLeader = true });
        party.Members.Add(new PartyMember { Name = "Helper", IsSelf = true });
        party.LeaderName = "Fujin Wuz";
        party.IsInParty = true;
        party.SelfIsLeader = false;

        engine.DispatchForTests(Telepath("Raijin", "@invite"));

        Assert.Contains("{I'm following Fujin; denied.}", LastReply(engine));
        Assert.Empty(relay);
    }

    [Fact]
    public void Invite_WhenFollower_AndWarnDenialOff_StaysSilent()
    {
        var (engine, _, _, party, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        party.Members.Add(new PartyMember { Name = "Fujin", IsLeader = true });
        party.Members.Add(new PartyMember { Name = "Helper", IsSelf = true });
        party.LeaderName = "Fujin";
        party.IsInParty = true;
        party.SelfIsLeader = false;
        engine.WarnOnDenial = false;

        engine.DispatchForTests(Telepath("Raijin", "@invite"));

        Assert.Empty(engine.LastSentForTests);
        Assert.Empty(relay);
    }

    [Fact]
    public void Invite_WhenPartyFull_RepliesWithFollowerRoster()
    {
        var (engine, _, _, party, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        // 6 members: self + 5 followers — that's the MajorMUD cap.
        party.Members.Add(new PartyMember { Name = "Fujin", IsLeader = true, IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Helper" });
        party.Members.Add(new PartyMember { Name = "Buddy" });
        party.Members.Add(new PartyMember { Name = "Lou Last" });
        party.Members.Add(new PartyMember { Name = "Sam" });
        party.Members.Add(new PartyMember { Name = "Pal" });
        party.LeaderName = "Fujin";
        party.IsInParty = true;
        party.SelfIsLeader = true;

        engine.DispatchForTests(Telepath("Raijin", "@invite"));

        // Comma-joined given names of the 5 followers (skipping self).
        Assert.Contains("{My Party is full, Helper, Buddy, Lou, Sam, Pal are following me.}", LastReply(engine));
        Assert.Empty(relay);
    }

    [Fact]
    public void Invite_WhenLeaderWithRoom_SendsInvite()
    {
        var (engine, _, _, party, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        party.Members.Add(new PartyMember { Name = "Fujin", IsLeader = true, IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Helper" });
        party.LeaderName = "Fujin";
        party.IsInParty = true;
        party.SelfIsLeader = true;

        engine.DispatchForTests(Telepath("Raijin", "@invite"));

        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(relay[0]));
    }

    // ===== @join =====

    [Fact]
    public void Join_WhenSolo_SendsJoinWithSenderGivenName()
    {
        var (engine, _, _, _, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);

        engine.DispatchForTests(Telepath("Raijin", "@join"));

        Assert.Equal("join Raijin\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void Join_WhenFollower_DeniesWithLeaderName()
    {
        var (engine, _, _, party, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        party.Members.Add(new PartyMember { Name = "Fujin", IsLeader = true });
        party.Members.Add(new PartyMember { Name = "Helper", IsSelf = true });
        party.LeaderName = "Fujin";
        party.IsInParty = true;
        party.SelfIsLeader = false;

        engine.DispatchForTests(Telepath("Raijin", "@join"));

        Assert.Contains("{I'm following Fujin; denied.}", LastReply(engine));
        Assert.Empty(relay);
    }

    // ===== @invite / @join while mortally wounded =====

    /// <summary>
    /// Drive the character mortally wounded — negative HP with real prompt
    /// data behind it, so PlayerState.IsMortallyWounded reads true.
    /// </summary>
    private static void DropPlayer(PlayerState p)
    {
        p.MaxHp = 150;
        p.Hp = -4;
        p.HasPromptData = true;
    }

    [Fact]
    public void Join_WhileMortallyWounded_RepliesCantJoin_AndDoesNotSend()
    {
        var (engine, _, player, _, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        DropPlayer(player);

        engine.DispatchForTests(Telepath("Raijin", "@join"));

        string reply = LastReply(engine);
        Assert.Contains("Can't join", reply);
        Assert.Contains("mortally wounded", reply);
        Assert.Contains("nobody is dragging me", reply);
        Assert.Empty(relay);
    }

    [Fact]
    public void Invite_WhileMortallyWounded_RepliesCantInvite_AndDoesNotSend()
    {
        var (engine, _, player, _, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        DropPlayer(player);

        engine.DispatchForTests(Telepath("Raijin", "@invite"));

        string reply = LastReply(engine);
        Assert.Contains("Can't invite", reply);
        Assert.Contains("mortally wounded", reply);
        Assert.Empty(relay);
    }

    [Fact]
    public void Join_WhileMortallyWounded_AndBeingDragged_NamesTheDragger()
    {
        var (engine, _, player, _, players, relay) =
            Setup(readDraggedBy: () => "Fujin");
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        DropPlayer(player);

        engine.DispatchForTests(Telepath("Raijin", "@join"));

        Assert.Contains("being dragged by Fujin", LastReply(engine));
        Assert.Empty(relay);
    }

    [Fact]
    public void Join_WhileMortallyWounded_RepliesEvenWhenWarnDenialOff()
    {
        // The mortally-wounded refusal is party-coordination info (like the
        // full-party @invite reply), not a security-sensitive denial — it
        // fires regardless of WarnOnDenial so a helper always learns why.
        var (engine, _, player, _, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        DropPlayer(player);
        engine.WarnOnDenial = false;

        engine.DispatchForTests(Telepath("Raijin", "@join"));

        Assert.Contains("Can't join", LastReply(engine));
        Assert.Empty(relay);
    }
}
