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
    private static (RemoteCommandManager engine, PartyEssentialHandlers handlers, PlayerState player, PartyState party, PlayerDatabase players, List<byte[]> partyRelay) Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        PlayerState player = new();
        RemoteCommandManager engine = new(chat, party, players);
        PartyEssentialHandlers handlers = new(engine, player, party);
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
    public void Ctor_RegistersAllEightCommands()
    {
        var (engine, _, _, _, _, _) = Setup();
        // 8 commands: @version @health @status @par @where @party @wait @ok
        Assert.Equal(8, engine.HandlerCount);
    }

    [Fact]
    public void Dispose_UnregistersAllEight()
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
    public void Status_EmitsCurrentPosition()
    {
        var (engine, _, player, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        player.Position = PlayerPosition.Meditating;
        player.HasPromptData = true;

        engine.DispatchForTests(Telepath("Friend", "@status"));
        Assert.Contains("Meditating", LastReply(engine));
    }

    [Fact]
    public void Par_NoParty_RepliesNoPartyActive()
    {
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        engine.DispatchForTests(Telepath("Friend", "@par"));

        Assert.Contains("No party", LastReply(engine));
    }

    [Fact]
    public void Par_WithMembers_EmitsRosterSummary()
    {
        var (engine, _, _, party, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryHealthStatus);
        party.Members.Add(new PartyMember
        {
            Name = "Forged", IsLeader = true,
            HpPercent = 100, MpPercent = 100, Position = PlayerPosition.Standing,
        });
        party.Members.Add(new PartyMember
        {
            Name = "Helper", IsLeader = false,
            HpPercent = 94, MpPercent = 80, Position = PlayerPosition.Resting,
        });
        party.IsInParty = true;

        engine.DispatchForTests(Telepath("Friend", "@par"));

        string reply = LastReply(engine);
        Assert.Contains("Party (2)", reply);
        Assert.Contains("*Forged", reply);   // leader marker
        Assert.Contains("Helper",   reply);
        Assert.Contains("94%",      reply);
        Assert.Contains("(Resting)", reply);
    }

    [Fact]
    public void Where_RepliesPlaceholder()
    {
        // Phase 7 RoomTracker fills this in. PR 6.3 just acknowledges.
        var (engine, _, _, _, players, _) = Setup();
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryLocation);
        engine.DispatchForTests(Telepath("Friend", "@where"));

        Assert.Contains("Location unknown", LastReply(engine));
    }

    // ===== @party <sub> whitelist =====

    [Fact]
    public void PartyRest_FromPartyMember_SendsRestToWire()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Telepath("Leader", "@party rest"));

        Assert.Equal("rest\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyMeditate_MapsToShortForm()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Telepath("Leader", "@party meditate"));

        Assert.Equal("medi\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyGo_ForwardsDirectionToken()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Telepath("Leader", "@party go n"));

        Assert.Equal("n\r", Encoding.Latin1.GetString(relay[0]));
    }

    [Fact]
    public void PartyStat_PartyI_PartyPar_AllRelay()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Telepath("Leader", "@party stat"));
        engine.DispatchForTests(Telepath("Leader", "@party i"));
        engine.DispatchForTests(Telepath("Leader", "@party par"));

        Assert.Equal(3, relay.Count);
        Assert.Equal("stat\r", Encoding.Latin1.GetString(relay[0]));
        Assert.Equal("i\r",    Encoding.Latin1.GetString(relay[1]));
        Assert.Equal("par\r",  Encoding.Latin1.GetString(relay[2]));
    }

    [Fact]
    public void PartyUnknownSub_IsIgnored()
    {
        var (engine, _, _, party, _, relay) = Setup();
        SeedPartyMember(party, "Leader", isLeader: true);
        engine.DispatchForTests(Telepath("Leader", "@party doSomethingWeird"));

        Assert.Empty(relay);
    }

    [Fact]
    public void PartyFromNonMember_NotRelayed()
    {
        // The party-whitelist gate in the engine already denies this, but
        // belt-and-braces: the handler shouldn't reach the wire either.
        var (engine, _, _, _, _, relay) = Setup();
        engine.DispatchForTests(Telepath("Stranger", "@party rest"));

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
}
