using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Health;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A `break` the user sent holds the combat engine's attack on the monster it was
// fighting (user, 2026-10-10: "a manual break should hold attacking that target
// until the user types something to attack it, but if that target dies, it should
// clear and engage the next target if there is one"). These pin what sets the hold,
// that no attack path fires under it, and each way it ends.
public sealed class CombatManagerBreakHoldTests
{
    private const string Rat = "giant rat";
    private const string Thief = "kobold thief";
    private const string RatBites = "The giant rat bites you for 5 damage!";

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public MovementCoordinator Coordinator { get; }
        public PlayerState State { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatStateTracker Tracker { get; }
        public MonsterDeathWatcher Deaths { get; }
        public CastCoordinator Cast { get; }
        public CombatManager Combat { get; }
        public OutboundBreakObserver UserCommands { get; }

        public CombatSettings Settings { get; } = new()
        {
            NormalAttackCommand = "a",
            TargetOrder = TargetOrder.Normal,
        };
        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();
        public HashSet<string> AttackSpells { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AutoCombatEnabled { get; set; } = true;
        public bool Sneaking { get; set; }
        public bool Dark { get; set; }

        // What the combat engine and the cast coordinator put on the wire, and what
        // the combat tracker's watchdog did.
        public List<byte[]> Sent { get; } = new();
        public List<byte[]> TrackerSent { get; } = new();
        public List<string> Notices { get; } = new();
        public List<Action> Posted { get; } = new();

        // The one clock every component here reads; nothing waits on real time.
        // Tick() moves it a round, each line fed in lands a millisecond after the
        // one before (the game's lines never share an instant), and anything
        // longer the test moves it by itself.
        public ManualClock Clock { get; } = new();
        public DateTimeOffset Now => Clock.Now;
        private static readonly TimeSpan LineGap = TimeSpan.FromMilliseconds(1);

        public Harness(bool wireCaster = true)
        {
            DefaultPatterns.Seed(Router);
            Coordinator = new MovementCoordinator(Log);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log, now: () => Clock.Now);
            // Same construction order as AppServices: the tracker hears a room
            // observation, and the death watcher an exp line, ahead of the engine.
            Tracker = new CombatStateTracker(
                Router, Coordinator, Classifier, Monsters, State,
                () => AutoCombatEnabled,
                resolveOverlay: n => Overlays.TryGetValue(n, out MonsterOverlay? o) ? o : new MonsterOverlay(),
                log: Log,
                clock: () => Clock.Now);
            Tracker.SetWireSender(b => TrackerSent.Add(b));
            Deaths = new MonsterDeathWatcher(Router, Log, now: () => Clock.Now);
            Deaths.MonsterDied += evt =>
            {
                if (evt.RoomSpellRoster is null) Combat!.NoteUnattributedDeath();
            };
            Cast = new CastCoordinator(Router, Log, now: () => Clock.Now);
            Cast.SetWireSender(b => Sent.Add(b));
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: n => Overlays.TryGetValue(n, out MonsterOverlay? o) ? o : new MonsterOverlay(),
                party: Party,
                readSettings: () => Settings,
                isEnabled: () => AutoCombatEnabled,
                readOwnGivenName: () => "MudPlay",
                post: a => Posted.Add(a),
                log: Log,
                now: () => Clock.Now);
            Combat.SetWireSender(b => Sent.Add(b));
            Combat.RoomSpellKill += Deaths.NoteRoomSpellKill;
            Combat.SetDarkRoomProbe(() => Dark);
            Combat.SetBackstabHooks(() => Sneaking, _ => false);
            Combat.SetCombatSpellPredicate(c => AttackSpells.Contains(c));
            Combat.SetSwitchDispatchScheduler((_, cb) => cb());
            if (wireCaster) Combat.SetCombatSpellCaster(Cast, () => (100, 100));
            Combat.UserBreakHoldNotice += Notices.Add;
            Tracker.SetCombatHeldOnPurposeProbe(() => Combat.AttackHeldByUserBreak);
            UserCommands = new OutboundBreakObserver(
                c => AttackSpells.Contains(c), Combat.NoteUserBreak, Combat.NoteUserAttack);
        }

        public void AddMonster(int number, string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                Links: new[] { new GameDataLink("Monsters", number) }));

        public void Feed(string line)
        {
            Clock.Advance(LineGap);
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(), Clock.Now, IsPromptLine: false));
            DrainPosted();
        }

        // A line printed in one colour, as the game prints a combat line: 6 is the
        // cyan of a swing that missed or was dodged, 1 the red of one armour turned.
        public void FeedInColour(string line, int colour)
        {
            CellAttributes[] attributes = Enumerable.Repeat(
                CellAttributes.Default.WithForeground(TerminalColor.Indexed(colour)), line.Length).ToArray();
            Clock.Advance(LineGap);
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, attributes, Clock.Now, IsPromptLine: false));
            DrainPosted();
        }

        public void Advance(TimeSpan by) => Clock.Advance(by);

        // A command of the user's own as MainWindowViewModel.SendUserInput hands it
        // to the break observer: at send, ahead of anything the game says back.
        public void UserSends(string line)
        {
            UserCommands.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"));
            DrainPosted();
        }

        // A command a party member's `@do` put on the wire, handed over the same
        // way with that member's name (DoHandler.SendingFor).
        public void PartyMemberDoes(string member, string line)
        {
            UserCommands.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"), $"{member}'s @do");
            DrainPosted();
        }

        // The same for a command a member's `@party <command>` relayed
        // (PartyEssentialHandlers.RelayingFor).
        public void PartyMemberRelays(string member, string line)
        {
            UserCommands.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"), $"{member}'s @party");
            DrainPosted();
        }

        // The game's answer to a `break` typed in a fight: the echo, then the Off.
        public void GameAnswersBreak()
        {
            Clock.Advance(LineGap);
            DateTimeOffset at = Clock.Now;
            Router.Dispatch(new LineExtractor.EmittedLine(
                "[HP=100/MA=50]:", Array.Empty<CellAttributes>(), at, IsPromptLine: true));
            Router.Dispatch(new LineExtractor.EmittedLine(
                "break", Array.Empty<CellAttributes>(), at, IsPromptLine: false));
            Feed("*Combat Off*");
        }

        public void UserBreaks()
        {
            UserSends("break");
            GameAnswersBreak();
        }

        // One combat round on: the fake clock moves five seconds, then the round
        // heartbeat runs as AppServices orders it.
        public void Tick()
        {
            Clock.Advance(TimeSpan.FromSeconds(5));
            Cast.OnCombatTick();
            Combat.OnCombatTick();
            DrainPosted();
        }

        public void Quiet(TimeSpan forThisLong)
        {
            Clock.Advance(forThisLong);
            Combat.OnHeartbeat();
            Tracker.OnCombatTick();
            DrainPosted();
        }

        public void DrainPosted()
        {
            while (Posted.Count > 0)
            {
                List<Action> due = new(Posted);
                Posted.Clear();
                foreach (Action a in due) a();
            }
        }

        public IReadOnlyList<string> AllSent
            => Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public string LastSent => Sent.Count == 0 ? string.Empty : AllSent[^1];

        public bool CombatGateHeld => Coordinator.AssertedGates.Contains(MovementCoordinator.CombatGate);

        public void Dispose()
        {
            Combat.Dispose();
            Deaths.Dispose();
            Cast.Dispose();
            Tracker.Dispose();
            Classifier.Dispose();
        }
    }

    // A weapon fight with one rat, engaged and answered.
    // The monster the user's break holds the attack on, as the bug report tells it.
    private static string? HeldOn(Harness h) =>
        System.Text.RegularExpressions.Regex.Match(h.Combat.UserBreakHoldSummary, "^on '([^']*)'")
            is { Success: true } m ? m.Groups[1].Value : null;

    private static Harness FightingARat()
    {
        Harness h = new();
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}.");
        Assert.Equal($"a {Rat}", h.LastSent);
        h.Feed("*Combat Engaged*");
        return h;
    }

    // ----- what sets it ------------------------------------------------

    [Theory]
    [InlineData("break")]
    [InlineData("brea")]
    [InlineData("bre")]
    [InlineData("BREAK")]
    public void TypedBreak_HoldsTheAttackOnTheMonsterBeingFought(string word)
    {
        using Harness h = FightingARat();

        h.UserSends(word);

        Assert.Equal(Rat, HeldOn(h));
        Assert.True(h.Combat.AttackHeldByUserBreak);
        Assert.Null(h.Combat.CurrentTarget);
        Assert.Contains(Rat, Assert.Single(h.Notices));
        Assert.Contains($"on '{Rat}'", h.Combat.UserBreakHoldSummary);
    }

    // `br` is the channel broadcast, `breaks` is nothing, and neither is a break.
    [Theory]
    [InlineData("br")]
    [InlineData("br hello all")]
    [InlineData("breaks")]
    [InlineData("b")]
    public void WordsThatAreNotBreak_HoldNothing(string line)
    {
        using Harness h = FightingARat();

        h.UserSends(line);

        Assert.Null(HeldOn(h));
        Assert.Equal(Rat, h.Combat.CurrentTarget);
    }

    [Fact]
    public void TypedBreak_WithNoAttackOnRecord_HoldsNothing()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);

        h.UserSends("break");

        Assert.Null(HeldOn(h));
        Assert.Empty(h.Notices);
    }

    // With the engine off (Auto-Combat off, the master switch) there is nothing to
    // hold back, but the hold is kept for the monster the user was fighting by hand
    // and is in force when the engine comes back on. No terminal line for it: every
    // hand-fought break would print one.
    [Fact]
    public void TypedBreak_WithTheEngineOff_StillTracks_AndHoldsWhenItComesBackOn()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.AutoCombatEnabled = false;
        h.Feed($"Also here: {Rat}.");
        Assert.Empty(h.Sent);

        h.UserSends("a rat");
        h.UserSends("break");

        Assert.Equal(Rat, HeldOn(h));
        Assert.False(h.Combat.AttackHeldByUserBreak);
        Assert.Empty(h.Notices);

        h.AutoCombatEnabled = true;
        h.Feed($"Also here: {Rat}.");
        h.Feed(RatBites);
        h.Tick();

        Assert.Empty(h.Sent);
        Assert.Equal(Rat, HeldOn(h));
        Assert.True(h.Combat.AttackHeldByUserBreak);
    }

    // A party member's `@do break` holds the attack exactly as a typed one does
    // (user, 2026-10-10, asked which breaks sent on someone's behalf should: "only
    // the @do break"), and the log and the terminal say who asked.
    [Fact]
    public void PartyMembersDoBreak_HoldsTheAttack_AndSaysWhoAsked()
    {
        using Harness h = FightingARat();
        int sent = h.Sent.Count;

        h.PartyMemberDoes("Leader", "break");
        h.GameAnswersBreak();
        h.Feed(RatBites);
        h.Tick();

        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent, h.Sent.Count);
        Assert.Contains("Leader's @do break", Assert.Single(h.Notices));
        Assert.Contains("asked for by Leader's @do", h.Combat.UserBreakHoldSummary);
        Assert.Contains(h.Log.Snapshot(), e =>
            e.Severity == LogSeverity.Info && e.Message.Contains("Leader's @do 'break'")
            && e.Message.Contains($"attack held on '{Rat}'"));
    }

    // An attack sent the same way lets the hold go, as the user's own would, so the
    // member who set it can lift it.
    [Fact]
    public void PartyMembersDoAttack_EndsTheHold_AndSaysWhoAsked()
    {
        using Harness h = FightingARat();
        h.PartyMemberDoes("Leader", "break");
        h.GameAnswersBreak();

        h.PartyMemberDoes("Leader", "a rat");

        Assert.Null(HeldOn(h));
        Assert.Equal(Rat, h.Combat.CurrentTarget);
        Assert.Contains("'a rat' sent by Leader's @do", h.Combat.UserBreakHoldSummary);
    }

    // A `break` a party member relays with `@party` holds the attack exactly as a
    // `@do break` does (user, 2026-10-10, asked whether it should: "yes"), and the
    // log, the terminal and the bug report say who asked.
    [Fact]
    public void PartyMembersPartyBreak_HoldsTheAttack_AndSaysWhoAsked()
    {
        using Harness h = FightingARat();
        int sent = h.Sent.Count;

        h.PartyMemberRelays("Leader", "break");
        h.GameAnswersBreak();
        h.Feed(RatBites);
        h.Tick();

        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent, h.Sent.Count);
        Assert.Contains("Leader's @party break", Assert.Single(h.Notices));
        Assert.Contains("asked for by Leader's @party", h.Combat.UserBreakHoldSummary);
        Assert.Contains(h.Log.Snapshot(), e =>
            e.Severity == LogSeverity.Info && e.Message.Contains("Leader's @party 'break'")
            && e.Message.Contains($"attack held on '{Rat}'"));
    }

    // An attack relayed the same way lifts the hold, as a `@do` attack does.
    [Fact]
    public void PartyMembersPartyAttack_EndsTheHold_AndSaysWhoAsked()
    {
        using Harness h = FightingARat();
        h.PartyMemberRelays("Leader", "break");
        h.GameAnswersBreak();

        h.PartyMemberRelays("Leader", "a rat");

        Assert.Null(HeldOn(h));
        Assert.Equal(Rat, h.Combat.CurrentTarget);
        Assert.Contains("'a rat' sent by Leader's @party", h.Combat.UserBreakHoldSummary);
    }

    // ----- no attack path fires under it ---------------------------------

    // Before the hold, the first combat line after the break's *Combat Off* sent the
    // attack again, and failing that the round tick did.
    [Fact]
    public void WeaponFight_CombatLineAndRoundTick_SendNoAttack()
    {
        using Harness h = FightingARat();
        int sent = h.Sent.Count;

        h.UserBreaks();
        h.Feed(RatBites);
        h.Tick();
        h.Feed(RatBites);
        h.Tick();

        Assert.Equal(sent, h.Sent.Count);
        Assert.Equal(Rat, HeldOn(h));
    }

    // A break typed ahead of the round: the round's lines arrive before the game
    // has read it, and the hold is already up because it was set as the line went out.
    [Fact]
    public void BreakTypedAhead_TheRoundsLinesBeforeItsAnswer_SendNoAttack()
    {
        using Harness h = FightingARat();
        int sent = h.Sent.Count;

        h.UserSends("break");
        h.Feed(RatBites);
        h.Feed("The giant rat swings at you but misses!");
        h.GameAnswersBreak();
        h.Feed(RatBites);

        Assert.Equal(sent, h.Sent.Count);
    }

    // The round tick alone brings a spell fight back after an unexplained *Combat
    // Off*. Not one the user's break drew.
    [Fact]
    public void SpellFight_RoundTickAndCombatLine_SendNoAttack()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "aslt", MinEnemies = 0 };
        h.AddMonster(1, Rat);
        h.Feed($"Also here: {Rat}.");
        Assert.Equal($"aslt {Rat}", h.LastSent);
        h.Feed("*Combat Engaged*");
        int sent = h.Sent.Count;

        h.UserBreaks();
        h.Tick();
        h.Feed(RatBites);
        h.Tick();

        Assert.Equal(sent, h.Sent.Count);
        Assert.Null(h.Combat.Snapshot().CastingSpellTarget);
        Assert.False(h.Combat.Snapshot().SpellAttackOwed);
    }

    // A room attack broken off: the room is read again with every monster still in
    // it, another walks in, and nothing is cast at any of them.
    [Fact]
    public void RoomAttack_BrokenOff_IsNotCastAgain_AndNoOtherMonsterIsPicked()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "aslt", MinEnemies = 0 };
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}, {Thief}.");
        Assert.Equal("hsto", h.LastSent);
        h.Feed("*Combat Engaged*");
        int sent = h.Sent.Count;

        h.UserBreaks();
        h.Feed($"Also here: {Rat}, {Thief}.");
        h.Classifier.AppendArrivalEntity(h.Classifier.Classify(Thief), "A kobold thief sneaks in from the west.");
        h.Feed("The kobold thief bites you for 4 damage!");
        h.Tick();

        Assert.Equal(sent, h.Sent.Count);
        Assert.Equal(Rat, HeldOn(h));
    }

    // The engine stands down for the room, not for one monster: another that walks
    // in and attacks is not fought back while the held one lives.
    [Fact]
    public void AnotherMonsterAttackingUs_IsNotFoughtBack()
    {
        using Harness h = FightingARat();
        int sent = h.Sent.Count;
        h.UserBreaks();

        h.Classifier.AppendArrivalEntity(h.Classifier.Classify(Thief), "A kobold thief sneaks in from the west.");
        h.Feed("The kobold thief bites you for 4 damage!");
        h.Feed($"Also here: {Rat}, {Thief}.");
        h.Tick();

        Assert.Equal(sent, h.Sent.Count);
    }

    [Fact]
    public void BackstabOpener_IsNotSentAgain()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Sneaking = true;
        h.AddMonster(1, Rat);
        h.Feed($"Also here: {Rat}.");
        Assert.Equal($"bs {Rat}", h.LastSent);
        int sent = h.Sent.Count;

        h.UserBreaks();
        h.Feed($"Also here: {Rat}.");
        h.Feed(RatBites);
        h.Tick();

        Assert.Equal(sent, h.Sent.Count);
    }

    // The 3.161.5 rules under the hold: a heal cast between rounds, or a gear command
    // of the user's, arms a re-attack for the *Combat Off* it draws. With the attack
    // held there is none to bring back.
    [Fact]
    public void BetweenRoundCastAndTypedGear_TheirCombatOffBringsNoAttackBack()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Cast.NotifyExternalCastSent();
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        h.Combat.NoteTypedGearCommand("eq sword");
        h.Feed("*Combat Off*");
        h.Feed(RatBites);

        Assert.Equal(sent, h.Sent.Count);
    }

    // The debuff the combat profile offers the between-round slot is an attack on the
    // monster too.
    [Fact]
    public void BetweenRoundDebuff_IsNotOffered()
    {
        using Harness h = new();
        h.Combat.SetBetweenRoundSlotQuery(() => true);
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.AddMonster(1, Rat);
        h.Feed($"Also here: {Rat}.");
        h.Feed("*Combat Engaged*");
        Assert.Equal("curse", h.Combat.PickInBetweenDebuff()?.Spell);

        h.UserBreaks();

        Assert.Null(h.Combat.PickInBetweenDebuff());
    }

    // A fumble re-fires the last client command. That may be the attack sent ahead of
    // the user's break; a heal is still re-fired.
    [Theory]
    [InlineData("a giant rat", false)]
    [InlineData("aslt giant rat", false)]
    [InlineData("kick giant rat", false)]
    [InlineData("mihe", true)]
    public void Fumble_ReFiresAHeal_ButNotAnAttack(string lastClientCommand, bool replayed)
    {
        using Harness h = FightingARat();
        h.AttackSpells.Add("aslt");
        h.UserBreaks();
        int sent = h.Sent.Count;
        int replays = 0;

        h.Combat.HandleFumble(lastClientCommand, () => replays++);

        Assert.Equal(replayed ? 1 : 0, replays);
        Assert.Equal(sent, h.Sent.Count);
    }

    // ----- how it ends ---------------------------------------------------

    [Theory]
    [InlineData("a giant rat")]
    [InlineData("a rat")]
    [InlineData("a")]
    [InlineData("attack giant rat")]
    [InlineData("au rat")]
    [InlineData("aa rat")]
    [InlineData("bash rat")]
    [InlineData("allout rat")]
    [InlineData("force rat")]
    [InlineData("smash rat")]
    [InlineData("bs rat")]
    [InlineData("backstab rat")]
    [InlineData("kick rat")]
    [InlineData("pu rat")]
    [InlineData("jumpkick rat")]
    [InlineData("aslt rat")]
    public void TheUsersAttack_EndsTheHold_AndTheEngineTakesUpThatMonster(string attack)
    {
        using Harness h = FightingARat();
        h.AttackSpells.Add("aslt");
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.UserSends(attack);

        Assert.Null(HeldOn(h));
        Assert.Equal(Rat, h.Combat.CurrentTarget);
        Assert.Equal(sent, h.Sent.Count);                       // the round is the user's
        Assert.Equal(2, h.Notices.Count);
        Assert.Contains("ended", h.Notices[1]);

        // The fight is on again and goes as any other: the rat dies, and the room is clear.
        h.Feed("*Combat Engaged*");
        h.Feed("You gain 50 experience.");
        h.Feed("*Combat Off*");
        Assert.Null(h.Combat.CurrentTarget);
        Assert.False(h.CombatGateHeld);
    }

    // Lines that look like an attack and are none leave the hold up.
    [Theory]
    [InlineData("bash n")]            // a door
    [InlineData("aa west")]           // the same
    [InlineData("kill giant rat")]    // the dead killblow command
    [InlineData("ki rat")]            // no command at all
    [InlineData("sm rat")]
    [InlineData("i")]
    [InlineData("l rat")]
    [InlineData("mihe")]              // a heal
    public void LinesThatAreNoAttack_LeaveTheHoldUp(string line)
    {
        using Harness h = FightingARat();
        h.AttackSpells.Add("aslt");
        h.UserBreaks();

        h.UserSends(line);

        Assert.Equal(Rat, HeldOn(h));
    }

    [Fact]
    public void TheUsersAttackOnAnotherMonster_EndsTheHold_AndThatOneBecomesTheTarget()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}, {Thief}.");
        Assert.Equal($"a {Rat}", h.LastSent);
        h.Feed("*Combat Engaged*");
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.UserSends("a kobold");

        Assert.Null(HeldOn(h));
        Assert.Equal(Thief, h.Combat.CurrentTarget);
        Assert.Equal(sent, h.Sent.Count);
    }

    // An attack at something the roster can't place still ends the hold. The round
    // is the user's; the next one picks a target as a fresh fight does.
    [Fact]
    public void TheUsersAttackAtNothingWeCanPlace_EndsTheHold_AndTheNextRoundPicksATarget()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.UserSends("a wyvern");

        Assert.Null(HeldOn(h));
        Assert.Equal(sent, h.Sent.Count);

        h.Tick();

        Assert.Equal($"a {Rat}", h.LastSent);
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    // The held monster is gone from the room's roster (someone killed it, or it
    // left): the hold clears and the next monster is engaged at once.
    [Fact]
    public void TheHeldMonsterGoneFromTheRoom_EndsTheHold_AndTheNextIsEngaged()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}, {Thief}.");
        h.Feed("*Combat Engaged*");
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Feed($"Also here: {Thief}.");

        Assert.Null(HeldOn(h));
        Assert.Equal($"a {Thief}", h.LastSent);
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    [Fact]
    public void TheHeldMonsterWalkingOut_EndsTheHold()
    {
        using Harness h = FightingARat();
        h.UserBreaks();

        h.Classifier.RemoveDepartedEntity(Rat);

        Assert.Null(HeldOn(h));
        Assert.False(h.CombatGateHeld);
    }

    // A monster walking in says nothing about who has gone.
    [Fact]
    public void AnArrival_DoesNotEndTheHold()
    {
        using Harness h = FightingARat();
        h.UserBreaks();

        h.Classifier.AppendArrivalEntity(h.Classifier.Classify(Thief), "A kobold thief sneaks in from the west.");

        Assert.Equal(Rat, HeldOn(h));
    }

    // The round beat the break to the game and killed the monster: its exp line
    // comes before any *Combat Off* does. That death is the held monster's, whatever
    // else of its name is in the room, and the next one is engaged.
    [Fact]
    public void KillInTheRoundTheBreakWasTypedIn_EndsTheHold_AndEngagesTheNextOfThatName()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.Feed($"Also here: {Rat}, {Rat}.");
        Assert.Equal($"a {Rat}", h.LastSent);
        h.Feed("*Combat Engaged*");
        int sent = h.Sent.Count;

        h.UserSends("break");
        h.Feed("You gain 50 experience.");
        h.Feed("*Combat Off*");

        Assert.Null(HeldOn(h));
        Assert.Contains("it died", h.Combat.UserBreakHoldSummary);
        Assert.True(h.Classifier.Current is { Entities.Count: 1 });
        Assert.Equal($"a {Rat}", h.LastSent);
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    // An exp line later on, with the fight long stopped, is something else's kill (a
    // damage shield's): which monster died isn't known, so the room is read again
    // and its roster decides.
    [Fact]
    public void ALaterKillUnderTheHold_ReadsTheRoomAgain_AndTheRosterDecides()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}, {Thief}.");
        h.Feed("*Combat Engaged*");
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Feed("You gain 50 experience.");
        h.Feed("*Combat Off*");

        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent + 1, h.Sent.Count);
        Assert.Equal(string.Empty, h.LastSent);                 // a bare Enter

        h.Feed($"Also here: {Rat}.");                           // the thief was the one that died
        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    // The same kill with two of the held monster's name in the room, and the room
    // read again listing one: a death of that name, which ends the hold.
    [Fact]
    public void ALaterKillUnderTheHold_WithOneFewerOfThatNameListed_EndsTheHold()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}, {Rat}, {Thief}.");
        h.Feed("*Combat Engaged*");
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Feed("You gain 50 experience.");
        h.Feed("*Combat Off*");
        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(string.Empty, h.LastSent);                 // the room is asked for

        h.Feed($"Also here: {Rat}, {Thief}.");

        Assert.Null(HeldOn(h));
        Assert.Equal($"a {Rat}", h.LastSent);
        Assert.Equal(sent + 2, h.Sent.Count);
    }

    // ----- any death of that name ends it --------------------------------

    // user, 2026-10-10: "the break hold should end on the death of any monster
    // matching the name of the monster we broke from". The client is shown no more
    // than the name, so one fewer of it listed is that death, and the one still
    // standing is engaged as after any kill.
    [Fact]
    public void OneFewerOfThatNameListed_EndsTheHold_AndTheNextIsEngaged()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.Feed($"Also here: {Rat}, {Rat}.");
        h.Feed("*Combat Engaged*");
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Feed($"Also here: {Rat}.");

        Assert.Null(HeldOn(h));
        Assert.Contains("one of that name died or left", h.Combat.UserBreakHoldSummary);
        Assert.Equal($"a {Rat}", h.LastSent);
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    // Which of two of a name walked out isn't known either, so it ends the hold
    // the same way.
    [Fact]
    public void OneOfThatNameWalkingOut_EndsTheHold()
    {
        using Harness h = new();
        h.AddMonster(1, Rat);
        h.Feed($"Also here: {Rat}, {Rat}.");
        h.Feed("*Combat Engaged*");
        h.UserBreaks();

        h.Classifier.RemoveDepartedEntity(Rat);

        Assert.Null(HeldOn(h));
        Assert.Equal($"a {Rat}", h.LastSent);
    }

    // Another of the name walking in is counted, so that one of the two going is
    // seen for what it is.
    [Fact]
    public void AnotherOfThatNameWalkingIn_ThenOneGoing_EndsTheHold()
    {
        using Harness h = FightingARat();
        h.UserBreaks();

        h.Classifier.AppendArrivalEntity(h.Classifier.Classify(Rat), "A giant rat scurries in from the west.");
        Assert.Equal(Rat, HeldOn(h));
        Assert.Contains("2 of that name listed", h.Combat.UserBreakHoldSummary);

        h.Feed($"Also here: {Rat}.");

        Assert.Null(HeldOn(h));
    }

    // ----- a round with no attack from it --------------------------------

    private const string ThiefBites = "The kobold thief bites you for 4 damage!";

    private static Harness FightingARatBesideAThief()
    {
        Harness h = new();
        h.AddMonster(1, Rat);
        h.AddMonster(2, Thief);
        h.Feed($"Also here: {Rat}, {Thief}.");
        Assert.Equal($"a {Rat}", h.LastSent);
        h.Feed("*Combat Engaged*");
        return h;
    }

    // user, 2026-10-10: "hostile monsters attack every round, so if we go 1 round and
    // we dont see that monster attacking anyone that we do know is in the room,
    // assume its dead". That is how a death the client cannot see is known; where it
    // can see, the room's listing decides. The round is the round clock's: at a tick
    // with no attack line from it for a round and a second, the room is read. Read
    // without it, the hold ends and the next target is picked.
    [Fact]
    public void ARoundWithNoAttackFromIt_ReadsTheRoom_AndItsAbsenceEndsTheHold()
    {
        using Harness h = FightingARatBesideAThief();
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Feed(RatBites);
        h.Feed(ThiefBites);
        h.Tick();                                   // the tick straight after its line
        Assert.Equal(sent, h.Sent.Count);

        h.Feed(ThiefBites);                         // only the thief, this round
        h.Tick();
        Assert.Equal(sent + 1, h.Sent.Count);
        Assert.Equal(string.Empty, h.LastSent);     // a bare Enter: the room is read first
        Assert.Equal(Rat, HeldOn(h));

        h.Feed($"Also here: {Thief}.");

        Assert.Null(HeldOn(h));
        Assert.Contains("no longer in the room", h.Combat.UserBreakHoldSummary);
        Assert.Equal($"a {Thief}", h.LastSent);
        Assert.Equal(sent + 2, h.Sent.Count);
    }

    // The look decides. A monster the room still lists is alive, however quiet: the
    // hold stands and nothing is sent at it.
    [Fact]
    public void SilentForARound_ButStillListed_TheHoldStands()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        h.Tick();
        h.Tick();
        Assert.Equal(string.Empty, h.LastSent);
        int sent = h.Sent.Count;

        h.Feed($"Also here: {Rat}.");
        h.Tick();

        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent, h.Sent.Count);
        Assert.True(h.CombatGateHeld);
    }

    // A neutral the user hit by hand and then broke from never attacks. Two silent
    // rounds make the client look once; the room lists it, so it is left alone, and
    // its silence asks for no other look for thirty seconds.
    [Fact]
    public void PassiveNeutralBrokenFrom_IsLookedAtOnce_AndNeverAttacked()
    {
        const string Guard = "village guard";
        using Harness h = new();
        h.AddMonster(3, Guard);
        h.Overlays[3] = new MonsterOverlay { Relationship = MonsterRelationship.Neutral };
        h.Feed($"Also here: {Guard}.");
        Assert.Empty(h.Sent);
        h.Combat.NoteAttackCommandObserved("a", Guard);     // the round override's reading of the line
        h.UserSends($"a {Guard}");
        h.Feed("*Combat Engaged*");

        h.UserBreaks();
        h.Tick();
        h.Tick();
        Assert.Equal(new[] { string.Empty }, h.AllSent);    // one bare Enter

        h.Feed($"Also here: {Guard}.");
        for (int round = 0; round < 5; round++) h.Tick();   // to 35 s after the break

        Assert.Equal(new[] { string.Empty }, h.AllSent);
        Assert.Equal(Guard, HeldOn(h));
        Assert.Null(h.Combat.CurrentTarget);
    }

    // The break takes a hand-engaged neutral back out of the engine's hands, so
    // whatever ends the hold, it isn't fought again without a new attack from the
    // user. Here another of its name dies.
    [Fact]
    public void NeutralBrokenFrom_IsNotFoughtAgain_WhenTheHoldEndsAnotherWay()
    {
        const string Guard = "village guard";
        using Harness h = new();
        h.AddMonster(3, Guard);
        h.Overlays[3] = new MonsterOverlay { Relationship = MonsterRelationship.Neutral };
        h.Feed($"Also here: {Guard}, {Guard}.");
        h.Combat.NoteAttackCommandObserved("a", Guard);
        h.UserSends($"a {Guard}");
        h.Feed("*Combat Engaged*");
        h.UserBreaks();

        h.Feed($"Also here: {Guard}.");
        h.Tick();
        h.Feed($"Also here: {Guard}.");
        h.Tick();

        Assert.Null(HeldOn(h));
        Assert.Null(h.Combat.CurrentTarget);
        Assert.Empty(h.Sent);

        // A new attack from the user puts it back in the engine's hands.
        h.Combat.NoteAttackCommandObserved("a", Guard);
        h.UserSends($"a {Guard}");
        Assert.Equal(Guard, h.Combat.CurrentTarget);
    }

    // Every kind of attack line from it keeps the hold: a hit or a miss, on us or on
    // someone else in the room. A hit is read by the damage ledger's reader and a
    // miss on us by the router's patterns; a miss on someone else is read by its
    // colour, so the wording of those three lines is only an example.
    [Theory]
    [InlineData("The giant rat bites you for 5 damage!", -1)]
    [InlineData("The giant rat lunges at you, but misses!", -1)]
    [InlineData("The giant rat lunges at you, but you dodge out of the way!", -1)]
    [InlineData("The giant rat bites Bob for 8 damage!", -1)]
    [InlineData("The giant rat lunges at Bob, but misses!", 6)]
    [InlineData("The giant rat lunges at Bob, but Bob dodges!", 6)]
    [InlineData("The giant rat claws Bob, but the blow glances off!", 1)]
    [InlineData("Bob just dodged an attack from the giant rat.", 7)]
    public void AnAttackLineFromItEachRound_KeepsTheHold(string line, int colour)
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;

        for (int round = 0; round < 4; round++)
        {
            if (colour < 0) h.Feed(line);
            else h.FeedInColour(line, colour);
            h.Tick();
        }

        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent, h.Sent.Count);
    }

    // Lines that name it and are not its attack say nothing of it attacking:
    // someone else hitting it or missing it, a spell searing it, something done to
    // it that prints in a miss's colour (knocked flat), another monster's attack.
    [Fact]
    public void LinesThatAreNotItsAttack_DoNotCountAsItAttacking()
    {
        using Harness h = FightingARatBesideAThief();
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Feed("Bob hits the giant rat for 30 damage!");
        h.Feed(ThiefBites);
        h.Tick();
        h.FeedInColour("Bob swings at the giant rat, but misses!", 6);
        h.Feed("The giant rat is seared by the flames for 12 damage!");
        h.FeedInColour("The giant rat is knocked flat!", 6);
        h.Feed(ThiefBites);
        h.Tick();

        Assert.Equal(sent + 1, h.Sent.Count);
        Assert.Equal(string.Empty, h.LastSent);
    }

    // The round runs from the break when that is later than its last attack line.
    [Fact]
    public void TheRoundIsCountedFromTheBreak_WhenThatIsTheLater()
    {
        using Harness h = FightingARat();
        h.Feed(RatBites);
        h.Advance(TimeSpan.FromSeconds(4));         // the break is typed late in the round
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Tick();                                   // five seconds after the break, nine after the bite
        Assert.Equal(sent, h.Sent.Count);
        Assert.Equal(Rat, HeldOn(h));

        h.Tick();
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    // Its own attack line arriving after its silence asked for the room, before the
    // room has been read: it stands, and the hold with it.
    [Fact]
    public void ItsAttackAfterTheRoomWasAskedFor_KeepsTheHold()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        h.Tick();
        h.Tick();
        int sent = h.Sent.Count;

        h.Feed(RatBites);
        h.Feed($"Also here: {Rat}.");
        h.Tick();

        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent, h.Sent.Count);
    }

    // The reading asked for never comes: it is not waited for past the next round,
    // and with nothing to see by, the silence decides. The monster is dropped from
    // the roster as a death is, and the next engaged.
    [Fact]
    public void SilentForARound_AndTheRoomReadNeverComes_ItIsTakenForDeadAtTheNextRound()
    {
        using Harness h = FightingARatBesideAThief();
        h.UserBreaks();
        h.Tick();
        h.Tick();
        Assert.Equal(Rat, HeldOn(h));

        h.Tick();

        Assert.Null(HeldOn(h));
        Assert.Contains("taken for dead", h.Combat.UserBreakHoldSummary);
        Assert.True(h.Classifier.Current is { Entities.Count: 1 });
        Assert.Equal($"a {Thief}", h.LastSent);
    }

    // The room it shows lists nobody: the monster is gone with everyone else. An
    // empty room prints no "Also here:", so the display's end is the answer.
    [Fact]
    public void SilentForARound_AndTheRoomShowsNobody_EndsTheHold_AndLetsTheGateGo()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        Assert.True(h.CombatGateHeld);
        h.Tick();
        h.Tick();

        h.Combat.NoteRoomDisplayed();

        Assert.Null(HeldOn(h));
        Assert.Contains("lists nobody", h.Combat.UserBreakHoldSummary);
        Assert.False(h.CombatGateHeld);
    }

    // A room display nobody asked for under the hold answers nothing.
    [Fact]
    public void RoomDisplayNotAskedFor_DoesNotEmptyTheRoster()
    {
        using Harness h = FightingARat();
        h.UserBreaks();

        h.Combat.NoteRoomDisplayed();

        Assert.Equal(Rat, HeldOn(h));
        Assert.True(h.Classifier.Current is { Entities.Count: 1 });
    }

    // In the dark a look lists nobody whoever is there, so none is sent and the
    // silence alone decides: the hold ends, and the monster comes off the roster as
    // a death does.
    [Fact]
    public void SilentForARound_InADarkRoom_IsTakenForDeadWithoutReadingTheRoom()
    {
        using Harness h = FightingARatBesideAThief();
        h.UserBreaks();
        h.Dark = true;
        int sent = h.Sent.Count;

        h.Tick();
        h.Tick();

        Assert.Null(HeldOn(h));
        Assert.Contains("taken for dead", h.Combat.UserBreakHoldSummary);
        Assert.DoesNotContain(string.Empty, h.AllSent.Skip(sent));
        Assert.True(h.Classifier.Current is { Entities.Count: 1 });
        Assert.Equal($"a {Thief}", h.LastSent);
    }

    // With the engine off nothing automatic is sent, and nothing waits on the
    // answer: the hold is left as it is. Back on, the silence is judged by a look.
    [Fact]
    public void SilentWithTheEngineOff_TheHoldStands_AndIsJudgedOnceTheEngineIsBack()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;
        h.AutoCombatEnabled = false;

        h.Tick();
        h.Tick();
        h.Tick();

        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent, h.Sent.Count);

        h.AutoCombatEnabled = true;
        h.Tick();

        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent + 1, h.Sent.Count);
        Assert.Equal(string.Empty, h.LastSent);
    }

    // A monster that stays silent and listed isn't looked at every round: after a
    // look has shown it, its silence asks for the next one no sooner than thirty
    // seconds on. When that one falls inside another re-display's cooldown (here a
    // later kill's, two seconds before), the heartbeat sends it once the cooldown
    // lets it out, and only once.
    [Fact]
    public void SilentAndStillListed_IsLookedAtAgainOnlyAfterThirtySeconds()
    {
        using Harness h = FightingARatBesideAThief();
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Tick();
        h.Tick();                                   // 10 s: the first look
        Assert.Equal(sent + 1, h.Sent.Count);
        h.Feed($"Also here: {Rat}, {Thief}.");
        Assert.Contains("silent but listed at the last look", h.Combat.UserBreakHoldSummary);

        for (int round = 0; round < 5; round++)     // 15 s to 35 s: silent, and no look
        {
            h.Tick();
            h.Combat.OnHeartbeat();
        }
        Assert.Equal(sent + 1, h.Sent.Count);
        Assert.Equal(Rat, HeldOn(h));

        h.Advance(TimeSpan.FromSeconds(3));         // 38 s: a kill, and the room read again
        h.Feed("You gain 50 experience.");
        h.Feed("*Combat Off*");
        Assert.Equal(sent + 2, h.Sent.Count);
        h.Feed($"Also here: {Rat}.");               // the thief was the one that died
        Assert.Equal(Rat, HeldOn(h));

        h.Advance(TimeSpan.FromSeconds(2));         // 40 s: thirty seconds after the first reading
        h.Cast.OnCombatTick();
        h.Combat.OnCombatTick();
        h.Combat.OnHeartbeat();
        Assert.Equal(sent + 2, h.Sent.Count);       // asked for, held back by the kill's re-display
        Assert.Contains("the room is being read", h.Combat.UserBreakHoldSummary);

        h.Advance(TimeSpan.FromSeconds(1));         // 41 s: three seconds after it
        h.Combat.OnHeartbeat();

        Assert.Equal(sent + 3, h.Sent.Count);
        Assert.Equal(string.Empty, h.LastSent);
        h.Combat.OnHeartbeat();
        Assert.Equal(sent + 3, h.Sent.Count);       // asked once
        Assert.Equal(Rat, HeldOn(h));
    }

    // An ordinary room display in between is judged by the count as always: the
    // back-off holds back only the looks its silence asks for.
    [Fact]
    public void SilentAndStillListed_AnOrdinaryDisplayWithoutIt_StillEndsTheHold()
    {
        using Harness h = FightingARatBesideAThief();
        h.UserBreaks();
        h.Tick();
        h.Tick();
        h.Feed($"Also here: {Rat}, {Thief}.");      // silent but listed
        h.Tick();

        h.Feed($"Also here: {Thief}.");

        Assert.Null(HeldOn(h));
        Assert.Equal($"a {Thief}", h.LastSent);
    }

    // The combat tracker's idle watchdog takes six quiet seconds under a held gate
    // for an empty room, re-displays it and force-clears the gate, which lets a walk
    // step out. Under the hold the quiet is asked for, and it stands back.
    [Fact]
    public void IdleWatchdog_StandsBackUnderTheHold()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        Assert.True(h.CombatGateHeld);

        h.Quiet(TimeSpan.FromSeconds(7));
        h.Feed($"Also here: {Rat}.");
        h.Quiet(TimeSpan.FromSeconds(7));

        Assert.Empty(h.TrackerSent);
        Assert.True(h.CombatGateHeld);
        Assert.Equal(Rat, HeldOn(h));
    }

    [Fact]
    public void LeavingTheRoom_EndsTheHold_AndTheNewRoomIsFought()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;

        // The next room's roster is read before the move is confirmed, and it holds
        // a monster of the same name: the hold stands the engine down for it.
        h.Feed($"Also here: {Rat}, {Thief}.");
        Assert.Equal(sent, h.Sent.Count);

        h.Combat.NoteRoomLeft(lastMoveSentAt: h.Now);
        h.DrainPosted();

        Assert.Null(HeldOn(h));
        Assert.Contains("left the room", h.Combat.UserBreakHoldSummary);
        Assert.Equal($"a {Rat}", h.LastSent);
        Assert.Equal(sent + 1, h.Sent.Count);
    }

    // The tracker's room changing with no move of ours since the break is the
    // tracker correcting where it thinks we stand (a re-localisation in a grid of
    // like-named rooms): the monster is where it was, the hold stands and nothing
    // is sent. Never having moved this session, or having last moved before the
    // break, are the same case.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoomChangedWithNoMoveSinceTheBreak_IsACorrection_AndTheHoldStands(bool movedBeforeTheBreak)
    {
        using Harness h = FightingARat();
        DateTimeOffset? lastMove = movedBeforeTheBreak ? h.Now.AddSeconds(-20) : null;
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.Combat.NoteRoomLeft(lastMove);
        h.DrainPosted();

        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent, h.Sent.Count);

        // The classifier wiping its roster for that change is no sighting of the
        // room either; the next display of it lists the monster and nothing is sent.
        h.Classifier.NoteRoomChanged();
        h.Feed($"Also here: {Rat}.");

        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent, h.Sent.Count);
    }

    // ----- a flee still moves us ------------------------------------------

    private sealed class WalkUnderWay : IRecoverableEngine
    {
        public string Name => "walk";
        public RoomKey? JourneyOrigin => null;
        public List<Direction> Moves { get; } = new();
        public Direction? PeekNextPlannedDirection() => null;
        public IReadOnlyList<Direction> PeekPlannedDirections(int count) => Array.Empty<Direction>();
        public void SendBacktrackMove(Direction direction) => Moves.Add(direction);
        public void PauseForRecovery(string reason) { }
        public void ResumeAfterRecovery(RoomKey recoveredAnchor) { }
        public void AbortFromRecoveryFailure(string detail) { }
    }

    // The health engine wired to the fight as AppServices wires it: in combat by the
    // tracker's flag, a hostile by the tracker's roster, and a walk under way to
    // retreat along. Its `break` and its move go out on its own wire, which is not
    // the user's.
    private static (HealthManager Health, WalkUnderWay Walk, List<string> Sent) HealthEngineFor(Harness h)
    {
        WalkUnderWay walk = new();
        List<string> sent = new();
        HealthManager health = new(h.State, h.Coordinator,
            readSettings: () => new HealthSettings(),
            isEnabled: () => true,
            readHangupCommand: () => string.Empty,
            getActiveMovementEngine: () => walk,
            getLastSentDirection: () => Direction.N,
            readCombatSettings: () => new CombatSettings
            {
                RunDirection = RunDirection.Backward, RunDistance = 1, BreakBeforeFleeing = true,
            },
            readGeneralSettings: null,
            hasEngageableHostiles: () => h.Tracker.HasEngageableHostiles,
            log: h.Log,
            hasHostileInRoom: () => h.Tracker.HasHostileMonster,
            findReversePath: (_, _) => null,
            post: a => a());
        health.SetWireSender(b => sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        health.SetScheduler((_, _) => { });
        return (health, walk, sent);
    }

    // user, 2026-10-10: under the hold the character "shouldnt move until we enter a
    // flee state or it dies". The walk waits on the Combat gate; a flee's move is the
    // health engine's own and is not held by it. Leaving the room then ends the hold.
    [Fact]
    public void LowHpRun_StillMovesUs_AndLeavingTheRoomEndsTheHold()
    {
        using Harness h = FightingARat();
        (HealthManager health, WalkUnderWay walk, List<string> healthSent) = HealthEngineFor(h);
        using (health)
        {
            h.UserBreaks();
            Assert.True(h.CombatGateHeld);
            h.Feed(RatBites);

            h.State.MaxHp = 200;
            h.State.HasPromptData = true;
            h.State.Hp = 30;

            Assert.Equal(Direction.S, Assert.Single(walk.Moves));
            Assert.Contains("break", healthSent);
            Assert.Equal(Rat, HeldOn(h));    // the flee's own break is no user's attack or break

            h.Combat.NoteRoomLeft(lastMoveSentAt: h.Now);

            Assert.Null(HeldOn(h));
            Assert.Contains("left the room", h.Combat.UserBreakHoldSummary);
        }
    }

    [Fact]
    public void RunFromAFleeMonster_StillMovesUs()
    {
        using Harness h = FightingARat();
        (HealthManager health, WalkUnderWay walk, _) = HealthEngineFor(h);
        using (health)
        {
            h.UserBreaks();

            FleeOutcome outcome = health.FleeFromMonster("a Flee monster is here", () => true);

            Assert.Equal(FleeOutcome.Started, outcome);
            Assert.Equal(Direction.S, Assert.Single(walk.Moves));
        }
    }

    [Fact]
    public void RunFromAPlayer_StillMovesUs()
    {
        using Harness h = FightingARat();
        (HealthManager health, WalkUnderWay walk, _) = HealthEngineFor(h);
        using (health)
        {
            h.UserBreaks();

            bool ran = health.FleeFromPlayer("a player attacked", rooms: 1, stayAway: TimeSpan.Zero);

            Assert.True(ran);
            Assert.Equal(Direction.S, Assert.Single(walk.Moves));
        }
    }

    [Fact]
    public void Disconnect_ProfileLoad_AndResetStates_EndTheHold()
    {
        using Harness h = FightingARat();

        h.UserBreaks();
        h.Combat.OnDisconnected();
        Assert.Null(HeldOn(h));

        h.Feed($"Also here: {Rat}.");
        h.Feed("*Combat Engaged*");
        h.UserBreaks();
        Assert.Equal(Rat, HeldOn(h));
        h.Combat.ClearUserBreakHold("another profile was loaded");
        Assert.Null(HeldOn(h));
        Assert.Contains("another profile was loaded", h.Combat.UserBreakHoldSummary);
    }

    // The Auto-Combat toggle is not an attack on that monster: off and on again, the
    // hold is still there. The master switch works through the same toggle.
    [Fact]
    public void AutoCombatOffAndOnAgain_LeavesTheHoldUp()
    {
        using Harness h = FightingARat();
        h.UserBreaks();
        int sent = h.Sent.Count;

        h.AutoCombatEnabled = false;
        h.Feed($"Also here: {Rat}.");
        h.AutoCombatEnabled = true;
        h.Feed($"Also here: {Rat}.");
        h.Feed(RatBites);
        h.Tick();

        Assert.Equal(Rat, HeldOn(h));
        Assert.Equal(sent, h.Sent.Count);
    }

    // A second break under the hold changes nothing and says nothing.
    [Fact]
    public void BreakTypedAgain_KeepsTheSameHold()
    {
        using Harness h = FightingARat();
        h.UserBreaks();

        h.UserSends("break");

        Assert.Equal(Rat, HeldOn(h));
        Assert.Single(h.Notices);
    }
}
