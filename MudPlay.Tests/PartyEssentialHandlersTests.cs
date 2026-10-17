using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

public sealed class PartyEssentialHandlersTests
{
    // The router each engine reads, which a test's chat lines go through.
    private readonly Dictionary<RemoteCommandManager, MessageRouter> _routers = new();

    // What the remote-command engine put on the wire, through the sender the client binds.
    private readonly List<byte[]> _replies = new();

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // Build a self-contained engine + handlers + the state they read from.
    // Tests send chat lines through the router (ChatLines.Hear) and read the
    // replies from _replies. PartyEssentialHandlers' own wire-sender is
    // captured separately so we can assert on the @party-relay path.
    private (RemoteCommandManager engine, PartyEssentialHandlers handlers, PlayerState player, PartyState party, PlayerDatabase players, List<byte[]> partyRelay) Setup(
        MudPlay.Game.Map.Room? currentRoom = null,
        IReadOnlyList<MudPlay.Game.Combat.RoomEntity>? roomEntities = null,
        MovementStatus? movement = null,
        Func<string?>? readDraggedBy = null,
        MessageFlags ailments = MessageFlags.None,
        Func<MudPlay.Game.Map.Room?>? readCurrentRoom = null,
        Func<string, Action<MudPlay.Game.Map.RoomKey>, Action, bool>? requestPositionRefix = null,
        bool fleeing = false,
        bool holdingWait = false,
        Func<DateTime>? now = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new(new ProfileService(), () => null);
        PlayerState player = new();
        RemoteCommandManager engine = new(chat, party, players);
        _routers[engine] = router;
        engine.SetWireSender(_replies.Add);
        PartyEssentialHandlers handlers = new(engine, player, party,
            readCurrentRoom: readCurrentRoom ?? (() => currentRoom),
            readRoomEntities: () => roomEntities,
            readMovement: () => movement ?? default,
            readDraggedBy: readDraggedBy,
            readAilments: () => ailments,
            readFleeing: () => fleeing,
            readHoldingWait: () => holdingWait,
            now: now);
        if (requestPositionRefix is not null) handlers.SetPositionRefix(requestPositionRefix);
        List<byte[]> relayCapture = new();
        handlers.SetWireSender(relayCapture.Add);
        return (engine, handlers, player, party, players, relayCapture);
    }

    private static ChatLine Telepath(string sender, string msg) =>
        new(ChatChannel.TelepathIncoming, sender, msg);

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

    private string LastReply() =>
        Encoding.Latin1.GetString(_replies[^1]);

    // ===== Registration shape =====

    private static readonly string[] Commands =
    {
        "@version", "@health", "@status", "@where", "@who", "@path", "@party",
        "@wait", "@ok", "@waiting", "@lives", "@invite", "@join",
    };

    [Fact]
    public void Dispose_UnregistersAll()
    {
        var (engine, handlers, _, _, _, _) = Setup();
        handlers.Dispose();
        Assert.All(Commands, c => Assert.False(engine.UnregisterHandler(c), c));   // already gone
    }

    // ===== Query handlers =====

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
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@version"));

        string reply = LastReply();
        Assert.Contains($"{{MudPlay {Services.AppInfo.Version}}}", reply);
        // Sanity: the version actually loaded (not the "unknown" fallback).
        Assert.NotEqual("unknown", Services.AppInfo.Version);
    }

    [Fact]
    public void Health_NoPromptData_RepliesUnknown()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@health"));

        Assert.Contains("HP unknown", LastReply());
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

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@health"));

        // Full wire: /<given> {<payload>}\r. The reply body is
        // brace-wrapped at SendReply per the remote-command convention.
        Assert.Equal("/Friend {HP=100/150,MA=75/85, Resting}\r", LastReply());
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

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@health"));
        Assert.Equal("/Friend {HP=100/150,MA=75/85}\r", LastReply());
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

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@health"));
        Assert.Equal("/Friend {HP=100/150,MA=75/85, Meditating}\r", LastReply());
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

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@health"));
        Assert.Equal("/Friend {HP=500/500,KAI=150/150}\r", LastReply());
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

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@health"));
        Assert.Equal("/Friend {HP=200/200}\r", LastReply());
    }

    [Fact]
    public void Status_Idle_EmitsStateRoomAndAilments()
    {
        var (engine, _, player, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Meditating;
        player.HasPromptData = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@status"));
        // Not moving + meditating → "meditating"; room unknown (no tracker fix);
        // no ETA clause (not walking); ailment clause. No boat/steps ETA present.
        Assert.Equal("/Friend {meditating; location unknown; no ailments}\r", LastReply());
    }

    [Fact]
    public void Status_WhenWalking_ReportsEngineRoomAndStepsLeft()
    {
        MudPlay.Game.Map.Room room = new()
        {
            Key = new MudPlay.Game.Map.RoomKey(5, 1000),
            Name = "Market Road",
            Exits = new Dictionary<MudPlay.Game.Map.Direction, MudPlay.Game.Map.RoomExit>(),
        };
        MovementStatus mv = new(MovementKind.Walking, "5/1141", CurrentStep: 1, TotalSteps: 4);
        var (engine, _, player, _, players, _) = Setup(currentRoom: room, movement: mv);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Standing;
        player.HasPromptData = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@status"));
        string reply = LastReply();
        Assert.Contains("walking to 5/1141", reply);
        Assert.Contains("Market Road (map 5, room 1000)", reply);
        // 4 total - 1 sent = 3 steps left (the ETA proxy for land walks).
        Assert.Contains("3 steps left", reply);
    }

    [Fact]
    public void Status_WhenWalkPaused_ReportsPausedNotWalking()
    {
        // A walk paused for a rest / hold / wait isn't moving — @status must say "paused",
        // not "walking", so a party member asking knows the leader has stopped.
        MovementStatus mv = new(MovementKind.Walking, "5/1141", CurrentStep: 1, TotalSteps: 4, Paused: true);
        var (engine, _, player, _, players, _) = Setup(movement: mv);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Standing;
        player.HasPromptData = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@status"));
        string reply = LastReply();
        Assert.Contains("paused en route to 5/1141", reply);
        Assert.DoesNotContain("walking to", reply);
    }

    [Fact]
    public void Status_WhenPausedForRest_ReportsRestReasonNotBarePaused()
    {
        // A pause has a WHY — resting for HP, meditating for mana, held, a party wait,
        // or the user's own manual pause — and each means something different to the
        // party member asking. When PauseReason carries the specific word, the phrase
        // uses it ("resting (low HP) on loop 'X'") rather than a generic "paused", and
        // the condition sub-state isn't echoed a second time.
        MovementStatus mv = new(MovementKind.Loop, "Newhaven Sewers", CurrentStep: 2, TotalSteps: 6,
            Paused: true, PauseReason: "resting (low HP)");
        var (engine, _, player, _, players, _) = Setup(movement: mv);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Resting;
        player.HasPromptData = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@status"));
        string reply = LastReply();
        Assert.Contains("resting (low HP) on loop 'Newhaven Sewers'", reply);
        Assert.DoesNotContain("running loop", reply);
        // The move phrase already says "resting (low HP)"; the bare "resting" condition
        // must not be appended on top of it.
        Assert.DoesNotContain(", resting", reply);
    }

    [Fact]
    public void Status_WhenSailing_ReportsDestinationBoatRoomAndCountdown()
    {
        // Aboard a sea-captain passage: the state reads "sailing to <port>" (the
        // same boat serves every destination, so the port is the signal), the room
        // clause reports the boat transit room the tracker holds, and the ETA counts
        // down to the wall-clock arrival rather than reporting step distance.
        MudPlay.Game.Map.Room boat = new()
        {
            Key = new MudPlay.Game.Map.RoomKey(2, 500),
            Name = "The Ship's Hold",
            Exits = new Dictionary<MudPlay.Game.Map.Direction, MudPlay.Game.Map.RoomExit>(),
        };
        MovementStatus mv = new(MovementKind.Walking, "7/200", CurrentStep: 0, TotalSteps: 3,
            Sailing: true, SailingEta: DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30),
            SailingPlace: "tal'kiran");
        var (engine, _, player, _, players, _) = Setup(currentRoom: boat, movement: mv);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Standing;
        player.HasPromptData = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@status"));
        string reply = LastReply();
        Assert.Contains("sailing to tal'kiran", reply);
        Assert.Contains("The Ship's Hold (map 2, room 500)", reply);
        Assert.Contains("ETA ~", reply);
        Assert.DoesNotContain("walking to", reply);
        Assert.DoesNotContain("steps left", reply);
    }

    [Fact]
    public void Path_WhenIdle_WithLastLoop_NamesTheLoopForRecovery()
    {
        // A dead / stopped player isn't moving, but @path still points a party
        // member at the loop they were running so they can help resume it.
        MovementStatus mv = new(MovementKind.None, null, 0, 0,
            LastPathKind: MovementKind.Loop, LastPathName: "Orc Cave 1");
        var (engine, _, player, _, players, _) = Setup(movement: mv);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        player.HasPromptData = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@path"));
        string reply = LastReply();
        Assert.Contains("not moving", reply);
        Assert.Contains("last ran loop 'Orc Cave 1'", reply);
    }

    [Fact]
    public void Path_WhenIdle_WithLastAutoLair_NamesTheLairForRecovery()
    {
        MovementStatus mv = new(MovementKind.None, null, 0, 0,
            LastPathKind: MovementKind.Lair, LastPathName: "Dragon Lair");
        var (engine, _, player, _, players, _) = Setup(movement: mv);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        player.HasPromptData = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@path"));
        Assert.Contains("last ran auto-lair 'Dragon Lair'", LastReply());
    }

    [Fact]
    public void Path_WhenIdle_NoPathRunThisSession_JustNotMoving()
    {
        MovementStatus mv = new(MovementKind.None, null, 0, 0);   // LastPathKind None
        var (engine, _, player, _, players, _) = Setup(movement: mv);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        player.HasPromptData = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@path"));
        string reply = LastReply();
        Assert.Contains("not moving", reply);
        Assert.DoesNotContain("last ran", reply);
    }

    [Fact]
    public void Status_WhenFighting_ReportsFightingSubState()
    {
        var (engine, _, player, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Standing;
        player.InCombat = true;
        player.HasPromptData = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@status"));
        Assert.Contains("fighting", LastReply());
    }

    [Fact]
    public void Status_WhenFleeing_OutranksFighting()
    {
        // Fleeing is the most urgent condition — reported even when InCombat is
        // also true (a flee is a combat action).
        var (engine, _, player, _, players, _) = Setup(fleeing: true);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Standing;
        player.InCombat = true;
        player.HasPromptData = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@status"));
        string reply = LastReply();
        Assert.Contains("fleeing", reply);
        Assert.DoesNotContain("fighting", reply);
    }

    [Fact]
    public void Status_ListsActiveCurableAilments_InTableOrder()
    {
        var (engine, _, player, _, players, _) = Setup(
            ailments: MessageFlags.Blinded | MessageFlags.Poisoned);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Standing;
        player.HasPromptData = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@status"));
        // Table order is poison, blind, confused, diseased.
        Assert.Contains("ailments: poisoned, blind", LastReply());
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

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@status"));
        Assert.Contains("no ailments", LastReply());
    }

    [Fact]
    public void PartyTelepath_NoParty_RepliesNoActiveParty()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@party"));

        Assert.Contains("no active party", LastReply());
    }

    [Fact]
    public void PartyTelepath_WhenFollowing_RepliesImFollowing()
    {
        var (engine, _, _, party, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        party.Members.Add(new PartyMember { Name = "MudPlay Wuz", IsLeader = true });
        party.Members.Add(new PartyMember { Name = "Raijin", IsSelf = true });
        party.LeaderName = "MudPlay Wuz";
        party.IsInParty = true;
        party.SelfIsLeader = false;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@party"));

        // Given name only — family is stripped (the @-command layer
        // never addresses players by family name).
        Assert.Contains("{I'm following MudPlay}", LastReply());
    }

    [Fact]
    public void PartyTelepath_WhenLeading_RepliesImLeadingWithFollowerGivenNames()
    {
        var (engine, _, _, party, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        party.Members.Add(new PartyMember { Name = "MudPlay", IsLeader = true, IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Raijin Wuz" });
        party.Members.Add(new PartyMember { Name = "Helper" });
        party.LeaderName = "MudPlay";
        party.IsInParty = true;
        party.SelfIsLeader = true;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@party"));

        // Family-stripped given names, comma-separated, roster order,
        // excluding self + leader.
        Assert.Contains("{I'm leading: Raijin, Helper}", LastReply());
    }

    [Fact]
    public void Party_FromTelepath_ReturnsStatusReplyEvenWithArgs()
    {
        // Telepath/Gangpath always status — ignore args so a back-
        // channel @party rest can't trigger the destructive verb path.
        // Sender is a party member (engine party-whitelist gate).
        var (engine, _, _, party, _, relay) = Setup();
        party.Members.Add(new PartyMember { Name = "MudPlay", IsLeader = true, IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Helper" });
        party.LeaderName = "MudPlay";
        party.IsInParty = true;
        party.SelfIsLeader = true;

        ChatLines.Hear(_routers[engine], Telepath("Helper", "@party rest"));

        Assert.Contains("{I'm leading: Helper}", LastReply());
        // No sub-command dispatched.
        Assert.Empty(relay);
    }

    [Fact]
    public void Party_FromSay_NoArgs_ReturnsStatusReply()
    {
        // A party member says "@party" in the room with no args: the status reply
        // comes back. Leader leads the party, so we're following them.
        var (engine, _, _, party, _, _) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        party.LeaderName = "Leader Smith";

        ChatLine say = new(ChatChannel.Local, "Leader", "@party");
        ChatLines.Hear(_routers[engine], say);

        Assert.Equal(">Leader {I'm following Leader}\r", LastReply());
    }

    [Fact]
    public void Where_WithoutRoom_RepliesLocationUnknown()
    {
        // No RoomTracker snapshot (Unknown / Lost, or no game data) → the
        // accessor returns null and @where degrades to a plain message.
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@where"));

        Assert.Contains("Location unknown", LastReply());
    }

    [Fact]
    public void Where_RepliesRoomNameExitsAndKey()
    {
        MudPlay.Game.Map.Room room = new()
        {
            Key = new MudPlay.Game.Map.RoomKey(5, 1141),
            Name = "Town Square",
            Exits = new Dictionary<MudPlay.Game.Map.Direction, MudPlay.Game.Map.RoomExit>
            {
                [MudPlay.Game.Map.Direction.N] =
                    new(new MudPlay.Game.Map.RoomKey(5, 1140), MudPlay.Game.Map.RoomExitHint.None, null),
                [MudPlay.Game.Map.Direction.E] =
                    new(new MudPlay.Game.Map.RoomKey(5, 1142), MudPlay.Game.Map.RoomExitHint.None, null),
            },
        };
        var (engine, _, _, _, players, _) = Setup(room);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@where"));

        string reply = LastReply();
        Assert.Contains("Town Square", reply);
        Assert.Contains("map 5", reply);
        Assert.Contains("room 1141", reply);
        Assert.Contains("north", reply);
        Assert.Contains("east", reply);
    }

    [Fact]
    public void Where_UnknownRoom_OnParadigm_FiresRefixAndRepliesResolvedRoom()
    {
        // Position unknown, but Paradigm's `rm` answers authoritatively. @where
        // fires the re-fix; when it resolves, the tracker re-anchors and the
        // reply carries the real room instead of a bare "Location unknown".
        MudPlay.Game.Map.Room resolvedRoom = new()
        {
            Key = new MudPlay.Game.Map.RoomKey(1, 2155),
            Name = "Training Grounds",
            Exits = new Dictionary<MudPlay.Game.Map.Direction, MudPlay.Game.Map.RoomExit>
            {
                [MudPlay.Game.Map.Direction.S] =
                    new(new MudPlay.Game.Map.RoomKey(1, 2156), MudPlay.Game.Map.RoomExitHint.None, null),
            },
        };
        MudPlay.Game.Map.Room? live = null;   // unknown until the re-fix answers
        string? refixReason = null;
        var (engine, _, _, _, players, _) = Setup(
            readCurrentRoom: () => live,
            requestPositionRefix: (reason, onResolved, _) =>
            {
                refixReason = reason;
                live = resolvedRoom;             // `rm` answered, tracker re-anchored
                onResolved(resolvedRoom.Key);
                return true;
            });
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@where"));

        string reply = LastReply();
        Assert.Contains("Training Grounds", reply);
        Assert.Contains("map 1", reply);
        Assert.Contains("room 2155", reply);
        Assert.Equal("@where from Friend", refixReason);
    }

    [Fact]
    public void Where_UnknownRoom_RefixResolvesOutsideGraph_RepliesAuthoritativeKey()
    {
        // `rm` answered but the room isn't in our map data — reply the raw
        // (map, room) the game handed us rather than "Location unknown".
        var (engine, _, _, _, players, _) = Setup(
            readCurrentRoom: () => null,
            requestPositionRefix: (_, onResolved, _) =>
            {
                onResolved(new MudPlay.Game.Map.RoomKey(1, 2155));
                return true;
            });
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@where"));

        string reply = LastReply();
        Assert.Contains("map 1", reply);
        Assert.Contains("room 2155", reply);
        Assert.Contains("not in map data", reply);
    }

    [Fact]
    public void Where_UnknownRoom_RefixFails_RepliesLocationUnknown()
    {
        // The re-fix started but timed out / missed the graph — degrade to the
        // plain reply.
        var (engine, _, _, _, players, _) = Setup(
            requestPositionRefix: (_, _, onFailed) => { onFailed(); return true; });
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@where"));

        Assert.Contains("Location unknown", LastReply());
    }

    [Fact]
    public void Where_UnknownRoom_RefixUnavailable_RepliesLocationUnknown()
    {
        // Stock realm (no `rm`): the seam returns false, so @where degrades
        // immediately without waiting on a re-fix that will never come.
        var (engine, _, _, _, players, _) = Setup(
            requestPositionRefix: (_, _, _) => false);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@where"));

        Assert.Contains("Location unknown", LastReply());
    }

    [Fact]
    public void Who_WithNoOccupants_RepliesNoOne()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@who"));

        Assert.Contains("no one", LastReply());
    }

    [Fact]
    public void Who_RepliesResolvedOccupantNames()
    {
        var entities = new List<MudPlay.Game.Combat.RoomEntity>
        {
            new("Raijin", "Raijin", MudPlay.Game.Combat.EntityKind.Player, null),
            new("Susanoo", "Susanoo", MudPlay.Game.Combat.EntityKind.Player, null),
            new("nasty giant rat", "giant rat", MudPlay.Game.Combat.EntityKind.Monster, 42),
        };
        var (engine, _, _, _, players, _) = Setup(roomEntities: entities);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@who"));

        string reply = LastReply();
        Assert.Contains("Raijin", reply);
        Assert.Contains("Susanoo", reply);
        Assert.Contains("giant rat", reply);
        // Resolved (canonical) name, not the flavour-prefixed raw form.
        Assert.DoesNotContain("nasty", reply);
    }

    [Fact]
    public void Path_WhenLooping_RepliesLoopNameRoomAndStep()
    {
        MudPlay.Game.Map.Room room = new()
        {
            Key = new MudPlay.Game.Map.RoomKey(9, 1012),
            Name = "Sewer Tunnel",
            Exits = new Dictionary<MudPlay.Game.Map.Direction, MudPlay.Game.Map.RoomExit>(),
        };
        MovementStatus mv = new(MovementKind.Loop, "Sewer farm", CurrentStep: 2, TotalSteps: 6);
        var (engine, _, _, _, players, _) = Setup(currentRoom: room, movement: mv);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@path"));

        string reply = LastReply();
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
        ChatLines.Hear(_routers[engine], Telepath("Friend", "@path"));

        string reply = LastReply();
        Assert.Contains("walking to 5/1141", reply);
        Assert.Contains("step 1/4", reply);
    }

    // ===== @party <sub> whitelist (Say-channel sub-command dispatch) ====
    // Dispatch only happens in Local (say) channel — telepath/gangpath
    // always return the status reply (covered in the Party_FromTelepath_*
    // tests above).

    private static ChatLine Say(string sender, string msg) =>
        new(ChatChannel.Local, sender, msg);

    [Fact]
    public void PartyRest_FromPartyMember_SendsRestToWire()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        ChatLines.Hear(_routers[engine], Say("Leader", "@party rest"));

        Assert.Equal("rest\r", Encoding.Latin1.GetString(relay[0]));
    }

    // While the relayed line is going out the handler names whose it is, which is
    // how the wire tells a member's `@party break` from an engine's; before and
    // after the send it names nobody.
    [Fact]
    public void PartyRelay_NamesTheMember_OnlyWhileItsLineIsBeingSent()
    {
        var (engine, handlers, _, party, _, _) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        List<string?> duringSend = new();
        handlers.SetWireSender(_ => duringSend.Add(handlers.RelayingFor));

        Assert.Null(handlers.RelayingFor);
        ChatLines.Hear(_routers[engine], Say("Leader", "@party break"));

        Assert.Equal(new string?[] { "Leader" }, duringSend);
        Assert.Null(handlers.RelayingFor);
    }

    // With the master switch off no remote command is followed, so a `@party break`
    // never reaches the wire and there is nothing for it to hold. Nor does one from
    // someone who is not in the party.
    [Fact]
    public void PartyBreak_WithTheMasterSwitchOff_OrFromOutsideTheParty_SendsNothing()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);

        ChatLines.Hear(_routers[engine], Say("Stranger", "@party break"));
        Assert.Empty(relay);

        engine.BlockedByMasterSwitch = _ => true;
        ChatLines.Hear(_routers[engine], Say("Leader", "@party break"));
        Assert.Empty(relay);
    }

    [Fact]
    public void PartyMeditate_MapsToShortForm()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        ChatLines.Hear(_routers[engine], Say("Leader", "@party meditate"));

        Assert.Equal("medi\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyGo_ForwardsDirectionToken()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        ChatLines.Hear(_routers[engine], Say("Leader", "@party go n"));

        Assert.Equal("n\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyGo_TextExit_KeepsGoVerb()
    {
        // Report paradigm-20260811-122253: "@party go hole" wrongly sent bare "hole".
        // A non-cardinal target is a text exit — the bare token isn't a command, so
        // the "go" verb must survive (drop it only for real cardinal directions).
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        ChatLines.Hear(_routers[engine], Say("Leader", "@party go hole"));

        Assert.Equal("go hole\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyGo_LongDirectionWord_DropsGoVerb()
    {
        // A long-form direction word ("north") is still a cardinal — drop the "go".
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        ChatLines.Hear(_routers[engine], Say("Leader", "@party go north"));

        Assert.Equal("north\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyStat_PartyI_PartyPar_AllRelay()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        ChatLines.Hear(_routers[engine], Say("Leader", "@party stat"));
        ChatLines.Hear(_routers[engine], Say("Leader", "@party i"));
        ChatLines.Hear(_routers[engine], Say("Leader", "@party par"));

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
        ChatLines.Hear(_routers[engine], Say("Leader", "@party use chime"));

        Assert.Single(relay);
        Assert.Equal("use chime\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyRingChime_RelaysVerbatim()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        ChatLines.Hear(_routers[engine], Say("Leader", "@party ring chime"));

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
        ChatLines.Hear(_routers[engine], Say("Leader", "@party .hi"));

        Assert.Single(relay);
        Assert.Equal(".hi\r", Encoding.Latin1.GetString(relay[0]));
    }

    // A multi-word arbitrary command survives whole (single-space collapsed).
    [Fact]
    public void PartyMultiWordCommand_RelaysVerbatim()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        ChatLines.Hear(_routers[engine], Say("Leader", "@party cast heal raijin"));

        Assert.Single(relay);
        Assert.Equal("cast heal raijin\r", Encoding.Latin1.GetString(relay[0]));
    }

    // Verbatim stops at plain ASCII: a character outside it (here the CP437 é and ═
    // as the screen decodes them) goes out as `?`, as in every reply, and the
    // relayed-directive event is told the command as sent.
    [Fact]
    public void PartyRelay_TextOutsideAscii_GoesOutAsQuestionMarks()
    {
        var (engine, handlers, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        List<string> relayed = new();
        handlers.PartyDirectiveRelayed += (_, command) => relayed.Add(command);

        ChatLines.Hear(_routers[engine], Say("Leader", "@party say café ══"));

        Assert.Equal("say caf? ??\r", Encoding.Latin1.GetString(Assert.Single(relay)));
        Assert.Equal(new[] { "say caf? ??" }, relayed);
    }

    [Fact]
    public void PartyRelay_PlainAsciiCommand_GoesOutByteForByte()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        const string command = "say Hi, \"there\" ~ {x} [1+2=3] 50% @home!";

        ChatLines.Hear(_routers[engine], Say("Leader", "@party " + command));

        Assert.Equal(Encoding.ASCII.GetBytes(command + "\r"), Assert.Single(relay));
    }

    // Plain `suicide` relays like any other passthrough — only the `set suicide`
    // phrase and reroll are refused (both blocked at engine level before this
    // handler runs, verified below and in RemoteCommandManagerTests).
    [Fact]
    public void PartyPlainSuicide_RelaysVerbatim()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        ChatLines.Hear(_routers[engine], Say("Leader", "@party suicide"));

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
        ChatLines.Hear(_routers[engine], Say("Leader", "@party set suicide hunter2"));

        Assert.Empty(relay);
    }

    [Fact]
    public void PartyReroll_NotRelayed()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        ChatLines.Hear(_routers[engine], Say("Leader", "@party reroll"));

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
        ChatLines.Hear(_routers[engine], Say("Stranger", "@party rest"));

        Assert.Empty(relay);
    }

    // ===== @wait / @ok wait-set =====

    [Fact]
    public void Wait_AddsToWaitingMembers()
    {
        var (engine, handlers, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));

        Assert.Contains("Follower", handlers.WaitingMembers);
    }

    [Fact]
    public void Ok_RemovesFromWaitingMembers()
    {
        var (engine, handlers, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");
        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));
        Assert.Contains("Follower", handlers.WaitingMembers);

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@ok"));
        Assert.DoesNotContain("Follower", handlers.WaitingMembers);
    }

    // @waiting — the leader re-collected us after a hold left us behind and is
    // holding for our @ok (report paradigm-20260926-195517).
    [Fact]
    public void Waiting_FromOurLeader_NothingHoldingUs_SendsOk()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Boss", isLeader: true);
        party.LeaderName = "Boss";

        ChatLines.Hear(_routers[engine], Telepath("Boss", "@waiting"));

        Assert.Equal("/Boss @ok\r", Encoding.Latin1.GetString(relay[^1]));
    }

    [Fact]
    public void Waiting_StillHeld_SendsNothingYet()
    {
        var (engine, _, _, party, _, relay) = Setup(holdingWait: true);
        SeedPartyMember(party, "Boss", isLeader: true);
        party.LeaderName = "Boss";

        ChatLines.Hear(_routers[engine], Telepath("Boss", "@waiting"));

        Assert.Empty(relay);
    }

    [Fact]
    public void Waiting_FromSomeoneNotOurLeader_IsIgnored()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Boss", isLeader: true);
        SeedPartyMember(party, "Other");
        party.LeaderName = "Boss";

        ChatLines.Hear(_routers[engine], Telepath("Other", "@waiting"));

        Assert.Empty(relay);
    }

    [Fact]
    public void DistrustedOk_DoesNotRelease_UntilTheWindowClears()
    {
        var (engine, handlers, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");
        handlers.NotePause("Follower", ignoreOk: true);

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@ok"));
        Assert.True(handlers.IsPaused);

        handlers.ClearAllWaits();   // the wait window ran out
        Assert.False(handlers.IsPaused);
        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));
        ChatLines.Hear(_routers[engine], Telepath("Follower", "@ok"));
        Assert.False(handlers.IsPaused);   // trusted again next time
    }

    [Fact]
    public void ReleaseWait_DropsOnlyThatMember()
    {
        var (engine, handlers, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");
        SeedPartyMember(party, "Other");
        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait (too heavy to move)"));
        ChatLines.Hear(_routers[engine], Telepath("Other", "@wait"));

        handlers.ReleaseWait("Follower");

        Assert.DoesNotContain("Follower", handlers.WaitingMembers);
        Assert.True(handlers.IsPaused);   // Other still holds it
    }

    [Fact]
    public void OkedWithin_TracksTheLastOk()
    {
        DateTime now = new(2026, 9, 26, 19, 54, 30, DateTimeKind.Utc);
        var (engine, handlers, _, party, _, _) = Setup(now: () => now);
        SeedPartyMember(party, "Follower");

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@ok"));
        now = now.AddSeconds(1);
        Assert.True(handlers.OkedWithin("Follower", TimeSpan.FromSeconds(5)));
        now = now.AddSeconds(10);
        Assert.False(handlers.OkedWithin("Follower", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Wait_FromNonPartyMember_IsIgnored()
    {
        // @wait is party-whitelist-gated by the engine; the wait-set
        // should never see a non-party sender.
        var (engine, handlers, _, _, _, _) = Setup();
        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@wait"));

        Assert.Empty(handlers.WaitingMembers);
    }

    [Fact]
    public void WaitingMembers_IsCaseInsensitive()
    {
        var (engine, handlers, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");
        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));
        // The @ok comes back with different casing — same player.
        ChatLines.Hear(_routers[engine], Telepath("follower", "@ok"));

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

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));

        Assert.True(row.IsWaiting);
    }

    [Fact]
    public void Ok_ClearsMatchingMembersIsWaiting()
    {
        var (engine, _, _, party, _, _) = Setup();
        SeedPartyMember(party, "Follower");
        PartyMember row = party.Members[0];
        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));
        Assert.True(row.IsWaiting);

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@ok"));
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

        ChatLines.Hear(_routers[engine], Telepath("Follower", "@wait"));

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

        ChatLines.Hear(_routers[engine], Telepath("Stranger", "@wait"));

        Assert.False(row.IsWaiting);
    }

    // ===== @lives =====

    [Fact]
    public void Lives_NoLivesProvider_RepliesUnknown()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.LivesProvider = null;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@lives"));

        Assert.Contains("lives unknown", LastReply());
    }

    [Fact]
    public void Lives_ProviderReturnsNull_RepliesUnknown()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.LivesProvider = () => null;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@lives"));

        Assert.Contains("lives unknown", LastReply());
    }

    [Fact]
    public void Lives_ProviderReturnsValue_RepliesWithCount()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.LivesProvider = () => 4;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@lives"));

        Assert.Contains("4 lives remaining", LastReply());
    }

    [Fact]
    public void Lives_SingularGrammar_WhenOneLeft()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.LivesProvider = () => 1;

        ChatLines.Hear(_routers[engine], Telepath("Friend", "@lives"));

        Assert.Contains("1 life remaining", LastReply());
    }

    // ===== @invite =====

    [Fact]
    public void Invite_WhenSolo_SendsInviteWithSenderGivenName()
    {
        var (engine, _, _, _, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@invite"));

        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void Invite_WhenFollower_DeniesWithLeaderName()
    {
        var (engine, _, _, party, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        party.Members.Add(new PartyMember { Name = "MudPlay Wuz", IsLeader = true });
        party.Members.Add(new PartyMember { Name = "Helper", IsSelf = true });
        party.LeaderName = "MudPlay Wuz";
        party.IsInParty = true;
        party.SelfIsLeader = false;

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@invite"));

        Assert.Contains("{I'm following MudPlay; denied.}", LastReply());
        Assert.Empty(relay);
    }

    [Fact]
    public void Invite_WhenFollower_AndWarnDenialOff_StaysSilent()
    {
        var (engine, _, _, party, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        party.Members.Add(new PartyMember { Name = "MudPlay", IsLeader = true });
        party.Members.Add(new PartyMember { Name = "Helper", IsSelf = true });
        party.LeaderName = "MudPlay";
        party.IsInParty = true;
        party.SelfIsLeader = false;
        engine.WarnOnDenial = false;

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@invite"));

        Assert.Empty(_replies);
        Assert.Empty(relay);
    }

    [Fact]
    public void Invite_WhenPartyFull_RepliesWithFollowerRoster()
    {
        var (engine, _, _, party, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        // 6 members: self + 5 followers — that's the MajorMUD cap.
        party.Members.Add(new PartyMember { Name = "MudPlay", IsLeader = true, IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Helper" });
        party.Members.Add(new PartyMember { Name = "Buddy" });
        party.Members.Add(new PartyMember { Name = "Lou Last" });
        party.Members.Add(new PartyMember { Name = "Sam" });
        party.Members.Add(new PartyMember { Name = "Pal" });
        party.LeaderName = "MudPlay";
        party.IsInParty = true;
        party.SelfIsLeader = true;

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@invite"));

        // Comma-joined given names of the 5 followers (skipping self).
        Assert.Contains("{My party is full: Helper, Buddy, Lou, Sam, Pal are following me.}", LastReply());
        Assert.Empty(relay);
    }

    [Fact]
    public void Invite_WhenLeaderWithRoom_SendsInvite()
    {
        var (engine, _, _, party, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        party.Members.Add(new PartyMember { Name = "MudPlay", IsLeader = true, IsSelf = true });
        party.Members.Add(new PartyMember { Name = "Helper" });
        party.LeaderName = "MudPlay";
        party.IsInParty = true;
        party.SelfIsLeader = true;

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@invite"));

        Assert.Equal("invite Raijin\r", Encoding.Latin1.GetString(relay[0]));
    }

    // ===== @join =====

    [Fact]
    public void Join_WhenSolo_SendsJoinWithSenderGivenName()
    {
        var (engine, _, _, _, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@join"));

        Assert.Equal("join Raijin\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void Join_WhenFollower_DeniesWithLeaderName()
    {
        var (engine, _, _, party, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        party.Members.Add(new PartyMember { Name = "MudPlay", IsLeader = true });
        party.Members.Add(new PartyMember { Name = "Helper", IsSelf = true });
        party.LeaderName = "MudPlay";
        party.IsInParty = true;
        party.SelfIsLeader = false;

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@join"));

        Assert.Contains("{I'm following MudPlay; denied.}", LastReply());
        Assert.Empty(relay);
    }

    [Fact]
    public void Join_FromBelievedLeader_RejoinsInsteadOfDenying()
    {
        // A leader never re-invites a current follower — so an @join from the very
        // leader we still believe we're following proves our "following" state is
        // stale (our train-stats trip broke the party server-side with no
        // leave-line reaching us). Honour it: send join, don't deny (report
        // stock-20260801-002423).
        var (engine, _, _, party, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        party.Members.Add(new PartyMember { Name = "Raijin", IsLeader = true });
        party.LeaderName = "Raijin";
        party.IsInParty = true;
        party.SelfIsLeader = false;

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@join"));

        Assert.Equal("join Raijin\r", Encoding.Latin1.GetString(relay[0]));
    }

    // The join an @join has us send is announced, so the game's answer to it can
    // be read by whoever watches for one (ComebackRequester).
    [Fact]
    public void Join_AnnouncesTheJoinItSent()
    {
        var (engine, handlers, _, _, players, relay) = Setup();
        SeedPlayer(players, "Boss", PlayerRemoteControls.RequestInvite);
        List<string> announced = new();
        handlers.JoinSent = announced.Add;

        ChatLines.Hear(_routers[engine], Telepath("Boss", "@join"));

        Assert.Equal("join Boss\r", Encoding.Latin1.GetString(Assert.Single(relay)));
        Assert.Equal(new[] { "Boss" }, announced);
    }

    // Our own client joined that leader a moment ago by itself: one join is enough.
    [Fact]
    public void Join_WhenAJoinToThatLeaderJustWentOut_SendsNoSecond()
    {
        var (engine, handlers, _, _, players, relay) = Setup();
        SeedPlayer(players, "Boss", PlayerRemoteControls.RequestInvite);
        handlers.JoinAlreadySent = leader => leader == "Boss";
        List<string> announced = new();
        handlers.JoinSent = announced.Add;

        ChatLines.Hear(_routers[engine], Telepath("Boss", "@join"));

        Assert.Empty(relay);
        Assert.Empty(announced);
        Assert.Empty(_replies);
    }

    // ===== @invite / @join while mortally wounded =====

    // Drive the character mortally wounded — negative HP with real prompt
    // data behind it, so PlayerState.IsMortallyWounded reads true.
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

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@join"));

        string reply = LastReply();
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

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@invite"));

        string reply = LastReply();
        Assert.Contains("Can't invite", reply);
        Assert.Contains("mortally wounded", reply);
        Assert.Empty(relay);
    }

    [Fact]
    public void Join_WhileMortallyWounded_AndBeingDragged_NamesTheDragger()
    {
        var (engine, _, player, _, players, relay) =
            Setup(readDraggedBy: () => "MudPlay");
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        DropPlayer(player);

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@join"));

        Assert.Contains("being dragged by MudPlay", LastReply());
        Assert.Empty(relay);
    }

    [Fact]
    public void Join_WhileMortallyWounded_IsSilentWhenWarnDenialOff()
    {
        // A refusal like any other: the Warn setting silences it (user,
        // 2026-10-10), and the join is still not sent.
        var (engine, _, player, _, players, relay) = Setup();
        SeedPlayer(players, "Raijin", PlayerRemoteControls.RequestInvite);
        DropPlayer(player);
        engine.WarnOnDenial = false;

        ChatLines.Hear(_routers[engine], Telepath("Raijin", "@join"));

        Assert.Empty(_replies);
        Assert.Empty(relay);
    }

}
