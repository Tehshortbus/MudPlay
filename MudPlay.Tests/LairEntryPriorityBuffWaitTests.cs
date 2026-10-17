using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A loop set to "Wait for priority buff spells" stands one step short of a lair while
// the buffs ticked "Priority buff" that are due are cast, holds every other buff back,
// and enters once none is left due. The hold and the cast scheduler are wired to each
// other here as the app wires them, so what is "due" is the scheduler's own answer.
public sealed class LairEntryPriorityBuffWaitTests
{
    // The hold stands outside a lair waiting, as the bug report's pending list
    // tells it.
    private static bool Waiting(LairEntryDebuffHold hold) =>
        hold.DescribePending()?.StartsWith("waiting", StringComparison.Ordinal) == true;

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public CastCoordinator Cast { get; }
        public CastingDirector Director { get; }
        public MovementCoordinator Coordinator { get; } = new();
        public LairEntryDebuffHold Hold { get; }
        public List<string> CastsSent { get; } = new();
        public List<string> ItemsUsed { get; } = new();
        // Item-cast buffs whose item can't be used: due for ever, never sent.
        public HashSet<string> UnusableItems { get; } = new();
        public BuffSettings Buffs { get; } = new();
        public SpellsSettings Spells { get; } = new();
        public HealthSettings Health { get; } = new();
        public Dictionary<string, int> ManaCosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AutoHeal { get; set; } = true;
        public bool AutoBless { get; set; } = true;
        public bool SneakKept { get; set; }
        // The debuff slot's "min mana per cast", as mana; null = none set.
        public int? DebuffMinMana { get; set; }
        // The game calls mana full here, under the maximum the client believes in.
        public bool ManaIsFull { get; set; }
        // The room a fight now is in, as the combat engine judges it; null = not known.
        public RoomKey? FightRoom { get; set; }
        // The cast codes that are mana-regen buffs.
        public HashSet<string> ManaRegenBuffs { get; } = new(StringComparer.OrdinalIgnoreCase);
        // A buff's own duration; 600 seconds where none is given.
        public Dictionary<string, long> BuffDurations { get; } = new(StringComparer.OrdinalIgnoreCase);
        // False once the loop is stopped (or otherwise no longer about to enter).
        public bool LoopStillAtTheDoor { get; set; } = true;
        // The combat profile's debuff, offered once the loop is in the lair.
        public string? DebuffOwed { get; set; }
        public DateTime Now { get; set; } = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        public List<Action> Scheduled { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(_ => { });
            Cast.CastSent += CastsSent.Add;
            Director = new CastingDirector(State, Cast,
                readSpells: () => Spells,
                readHealth: () => Health,
                isEnabled: () => AutoHeal,
                log: Log,
                now: () => Now);
            Director.SetAutoBlessGate(() => AutoBless);
            Director.SetManaCostLookup(code => ManaCosts.TryGetValue(code, out int cost) ? cost : null);
            Director.SetPartyBuffSource(() => Buffs);
            Director.SetStealthMaintenanceDeferGate(() => SneakKept);
            Director.SetBuffDurationSources(
                code => (string.Empty, BuffDurations.TryGetValue(code, out long seconds) ? seconds : 600), _ => null);
            Director.SetItemCastSource(_ => 600, token =>
            {
                if (UnusableItems.Contains(token)) return false;
                ItemsUsed.Add(token);
                return true;
            });
            Director.SetCombatDebuffSource(
                () => DebuffOwed is { } spell ? (spell, null) : null,
                () => DebuffOwed = null);

            Hold = new LairEntryDebuffHold(Coordinator,
                slotUsed: () => Director.BetweenRoundSlotUsed,
                castDue: Director.HasCastDue,
                scheduleAfter: (_, action) => Scheduled.Add(action),
                now: () => new DateTimeOffset(Now),
                log: Log,
                buffsDue: Director.BuffsDue,
                stillWanted: () => LoopStillAtTheDoor,
                healOrCureDue: Director.HealOrCureDue,
                debuffMana: () => DebuffMinMana is { } min ? ("dbuf", min, State.Ma, State.MaxMa, false) : null,
                inCombat: () => State.InCombat,
                manaIsFull: () => ManaIsFull,
                fightRoom: () => FightRoom);
            Director.SetLairEntryBuffHold(Hold);
            Director.SetManaRegenBuffProbe(ManaRegenBuffs.Contains);

            // Healthy and full of mana, so only what a test sets up is due. The buffs
            // are added after this, so nothing is cast until a test runs a pass.
            State.MaxHp = 200;
            State.Hp = 200;
            State.MaxMa = 100;
            State.Ma = 100;
            State.HasPromptData = true;
        }

        public BuffSlot AddBuff(string spell, bool priority, bool duringCombat = false)
        {
            BuffSlot slot = new()
            {
                Spell = spell, CastOnSelf = true, PriorityBuff = priority, BlessDuringCombat = duringCombat,
            };
            Buffs.Slots.Add(slot);
            return slot;
        }

        public bool HeldForMana => Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffManaGate);

        // One round at a door: the scheduler's pass, the hold's look, the round
        // turning, the hold's look.
        public string? Round(Action<string>? onCast = null)
        {
            string? cast = Director.Evaluate();
            if (cast is not null) onCast?.Invoke(cast);
            Poll();
            NextRound();
            Poll();
            return cast;
        }

        // The scheduler's passes that wait for HP to settle after a hit, as the app
        // runs them: queued, and run by the test when their time has come.
        public List<Action> SettledPasses { get; } = new();

        public void HoldPassesUntilHpSettles() =>
            Director.SetSettledPassScheduler((_, run) => SettledPasses.Add(run));

        public void RunSettledPasses()
        {
            Action[] due = SettledPasses.ToArray();
            SettledPasses.Clear();
            foreach (Action run in due) run();
        }

        public bool Held => Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffGate);

        public bool AskToEnter() => Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs);

        // Let the hold's timer look again.
        public void Poll()
        {
            Action[] due = Scheduled.ToArray();
            Scheduled.Clear();
            foreach (Action a in due) a();
        }

        // The game's round turns over: the between-round cast is free again.
        public void NextRound(int seconds = 5)
        {
            Now = Now.AddSeconds(seconds);
            Cast.OnCombatTick();
            Director.NotifyRoundComplete();
        }

        // The game refuses the cast just sent: the scheduler takes its timer back, so
        // the buff is due again next round.
        public void GameFizzlesTheCast(string spell) =>
            Router.Dispatch(new LineExtractor.EmittedLine(
                $"You attempt to cast {spell}, but fail.", Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));

        public IEnumerable<string> InfoLog(string source) =>
            Log.Snapshot().Where(e => e.Severity == LogSeverity.Info && e.Source == source).Select(e => e.Message);

        public void Dispose()
        {
            Director.Dispose();
            Cast.Dispose();
        }
    }

    [Fact]
    public void PriorityBuffDue_IsWaitedForAndCast_ThenTheLoopEnters_AndTheDebuffGetsTheCast()
    {
        using Harness h = new();
        h.AddBuff("star", priority: true);
        h.AddBuff("plai", priority: false);

        Assert.False(h.AskToEnter());                       // star is due: stand outside
        Assert.True(h.Held);

        Assert.Equal("star", h.Director.Evaluate());        // the wait lets it through
        h.Poll();
        Assert.True(h.Held);                                // the cast is spent this round

        h.NextRound();
        h.Poll();

        Assert.False(h.Held);                               // nothing priority left due: go in
        Assert.True(h.AskToEnter());
        Assert.False(h.Director.BetweenRoundSlotUsed);      // and the cast is free for the debuff
        h.DebuffOwed = "dbuf";
        Assert.Equal("dbuf", h.Director.Evaluate());        // which goes out ahead of the held plai
        Assert.Equal(new[] { "star", "dbuf" }, h.CastsSent);
    }

    [Fact]
    public void TwoPriorityBuffsDue_AreCastOverTwoRounds_ThenTheLoopEnters()
    {
        using Harness h = new();
        h.AddBuff("sta1", priority: true);
        h.AddBuff("sta2", priority: true);

        Assert.False(h.AskToEnter());
        Assert.Equal("sta1", h.Director.Evaluate());
        Assert.Null(h.Director.Evaluate());                 // one a round
        h.Poll();

        h.NextRound();
        h.Poll();
        Assert.True(h.Held);                                // the second is still due
        Assert.Equal("sta2", h.Director.Evaluate());
        h.Poll();

        h.NextRound();
        h.Poll();

        Assert.False(h.Held);
        Assert.Equal(new[] { "sta1", "sta2" }, h.CastsSent);
    }

    [Fact]
    public void OrdinaryBuffDue_IsNotCastDuringTheWait_AndGoesOutAfterTheFight()
    {
        using Harness h = new();
        h.AddBuff("plai", priority: false);
        h.AddBuff("star", priority: true);

        Assert.False(h.AskToEnter());
        Assert.Equal("star", h.Director.Evaluate());
        h.Poll();
        h.NextRound();
        Assert.Null(h.Director.Evaluate());                 // plai is due and the cast is free, yet held
        h.Poll();
        Assert.False(h.Held);
        Assert.Null(h.Director.Evaluate());                 // held for the step in as well
        Assert.Equal(new[] { "star" }, h.CastsSent);

        // The step landed and the fight is over: the loop's next step is no lair.
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));
        Assert.Equal("plai", h.Director.Evaluate());
    }

    [Fact]
    public void NoPriorityBuffDue_EntersAsSoonAsTheCastIsFree_WithTheOthersHeld()
    {
        using Harness h = new();
        h.AddBuff("plai", priority: false);

        // The cast is free and only an ordinary buff is due: straight in, as Block spells.
        Assert.True(h.AskToEnter());
        Assert.False(h.Held);
        Assert.Null(h.Director.Evaluate());
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void NoPriorityBuffDue_CastSpentOnArrival_WaitsOutThatRoundOnly()
    {
        using Harness h = new();
        h.AddBuff("plai", priority: false);
        h.Director.MarkBetweenRoundSlotUsed();              // something took this round's cast

        Assert.False(h.AskToEnter());
        Assert.True(h.Held);

        h.NextRound();
        Assert.Null(h.Director.Evaluate());                 // plai doesn't take the freed cast
        h.Poll();

        Assert.False(h.Held);
        Assert.Empty(h.CastsSent);
    }

    // Too little mana for the spell itself, with mana above the buff's own floor: it
    // is skipped. It isn't waited for, and it doesn't stand in the way of the priority
    // buffs behind it in cast order, which used to wait 18 seconds for nothing.
    [Fact]
    public void PriorityBuffThatCannotBePaidFor_FirstInCastOrder_IsSkipped_TheOnesBehindItAreCast_ThenEntry()
    {
        using Harness h = new();
        h.AddBuff("cost", priority: true);
        h.AddBuff("sta1", priority: true);
        h.AddBuff("sta2", priority: true);
        h.ManaCosts["cost"] = 500;

        Assert.False(h.AskToEnter());
        Assert.Equal(new[] { "sta1", "sta2" }, h.Director.BuffsDue(priority: true));   // at the door, cost isn't pending
        Assert.Equal("sta1", h.Director.Evaluate());
        h.Poll();
        h.NextRound(); h.Poll();
        Assert.Equal("sta2", h.Director.Evaluate());
        h.Poll();
        h.NextRound(); h.Poll();

        Assert.False(h.Held);                               // 10 s in: no 18 s spent on cost
        Assert.Equal(new[] { "sta1", "sta2" }, h.CastsSent);
        Assert.Equal("no priority buff is due and the between-round cast is free — entering the lair",
            h.InfoLog("Loop").Last());
    }

    [Fact]
    public void OnlyPriorityBuffDueCannotBePaidFor_NothingIsWaitedFor()
    {
        using Harness h = new();
        h.AddBuff("cost", priority: true);
        h.ManaCosts["cost"] = 500;

        Assert.True(h.AskToEnter());
    }

    // Outside a lair wait nothing has changed: a buff that can't be paid for at the
    // head of the list is waited for, and the buffs behind it wait with it, for both
    // kinds of buff. Then it goes first.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OutsideALairWait_ABuffThatCannotBePaidFor_IsWaitedFor_AndTheOnesBehindItToo(bool priority)
    {
        using Harness h = new();
        h.AddBuff("cost", priority);
        h.AddBuff("chea", priority);
        h.ManaCosts["cost"] = 90;
        h.ManaCosts["chea"] = 5;
        h.State.Ma = 80;                                    // over the floor, under cost's cost

        for (int round = 0; round < 3; round++)
        {
            Assert.Null(h.Director.Evaluate());
            h.NextRound();
        }
        Assert.True(h.Director.HasCastDue());               // and it counts as due

        h.State.Ma = 95;                                    // the pass on the mana change casts it
        h.NextRound();
        Assert.Equal("chea", h.Director.Evaluate());
        Assert.Equal(new[] { "cost", "chea" }, h.CastsSent);
    }

    // Why the skip stays at the door: cheap, short buffs skipped past a costly one
    // spend the mana it is waiting for, and it is never cast. Ten minutes of upkeep at
    // two mana a round: the costly buff goes out as soon as it can be paid for, first.
    [Fact]
    public void OutsideALairWait_CheapUpkeepDoesNotStarveACostlyBuffAtTheHeadOfTheList()
    {
        using Harness h = new();
        h.AddBuff("cost", priority: false);
        h.ManaCosts["cost"] = 60;
        for (int i = 1; i <= 3; i++)
        {
            h.AddBuff($"chp{i}", priority: false);
            h.BuffDurations[$"chp{i}"] = 30;
            h.ManaCosts[$"chp{i}"] = 6;
        }
        int mana = 55, counted = 0;
        h.AutoBless = false;
        h.State.Ma = mana;                                  // over the 50% floor, under cost's cost
        h.AutoBless = true;

        for (int round = 0; round < 120; round++)
        {
            h.Director.Evaluate();
            for (; counted < h.CastsSent.Count; counted++) mana -= h.CastsSent[counted] == "cost" ? 60 : 6;
            h.NextRound();
            mana = Math.Min(100, mana + 2);
            h.AutoBless = false;                            // the mana tick itself runs no pass here
            h.State.Ma = mana;
            h.AutoBless = true;
        }

        Assert.Equal("cost", h.CastsSent[0]);               // waited for, then first
        Assert.Contains("chp1", h.CastsSent);               // and the cheap ones get theirs after it
    }

    // At a door a reroll staged over its floor but under its cost stays staged (it
    // isn't dropped) and the buffs behind it go out meanwhile.
    [Fact]
    public void AtTheDoor_AStagedRerollThatCannotBePaidFor_StaysStaged_AndTheNextDueBuffGoesOut()
    {
        using Harness h = new();
        h.AddBuff("flux", priority: false);
        h.AddBuff("plai", priority: false);
        Assert.Equal("flux", h.Director.Evaluate());
        h.NextRound();
        h.ManaCosts["flux"] = 500;
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));   // plai is due: stand outside
        h.Director.RequestManaRegenReroll("flux");          // rolled low: staged; plai goes out in its place

        Assert.Equal(new[] { "flux", "plai" }, h.CastsSent);

        h.ManaCosts["flux"] = 50;
        h.NextRound();
        Assert.Equal("flux", h.Director.Evaluate());
    }

    // Held to keep a sneak: due, never sent. The same 18 seconds end the wait.
    [Fact]
    public void PriorityBuffHeldToKeepASneak_EndsTheWaitAfterEighteenSeconds()
    {
        using Harness h = new() { SneakKept = true };
        h.AddBuff("star", priority: true);

        Assert.False(h.AskToEnter());
        Assert.Null(h.Director.Evaluate());
        h.NextRound(seconds: 20);
        h.Poll();

        Assert.False(h.Held);
        Assert.Empty(h.CastsSent);
    }

    // Every due priority buff is waited for, however many: five take five rounds (six
    // here, the cast being spent on arrival), well past where Wait for spells stops.
    [Fact]
    public void FivePriorityBuffsDue_AreAllCast_ThenTheLoopEnters()
    {
        using Harness h = new();
        for (int i = 1; i <= 5; i++) h.AddBuff($"sta{i}", priority: true);
        h.AddBuff("plai", priority: false);
        h.Director.MarkBetweenRoundSlotUsed();              // spent before we got here

        Assert.False(h.AskToEnter());
        for (int i = 1; i <= 5; i++)
        {
            h.NextRound(); h.Poll();
            Assert.True(h.Held);
            Assert.Equal($"sta{i}", h.Director.Evaluate());
            h.Poll();
        }
        h.NextRound();
        Assert.Null(h.Director.Evaluate());                 // none left due; plai stays held
        h.Poll();

        Assert.False(h.Held);                               // 30 seconds after arriving
        Assert.Equal(new[] { "sta1", "sta2", "sta3", "sta4", "sta5" }, h.CastsSent);
        Assert.Equal("no priority buff is due and the between-round cast is free — entering the lair",
            h.InfoLog("Loop").Last());
    }

    // One that is due and can't go out among them (an item-cast buff whose item can't
    // be used): the ones that can go out do, and the wait ends 18 seconds after the
    // last of those, not 18 seconds after it began.
    [Fact]
    public void OneUncastableAmongThem_TheOthersGoOut_ThenEntryEighteenSecondsAfterTheLast()
    {
        using Harness h = new();
        h.AddBuff("sta1", priority: true);
        h.AddBuff("sta2", priority: true);
        h.AddBuff("#spent wand", priority: true);
        h.UnusableItems.Add("#spent wand");

        Assert.False(h.AskToEnter());
        Assert.Equal("sta1", h.Director.Evaluate());        // at 0 s
        h.Poll();
        h.NextRound(); h.Poll();
        Assert.Equal("sta2", h.Director.Evaluate());        // at 5 s: the last to go out
        h.Poll();
        h.NextRound();
        Assert.Null(h.Director.Evaluate());                 // the wand is due and can't be used
        h.Poll();
        h.NextRound(seconds: 12);                           // 22 s in, 17 s since sta2
        Assert.Null(h.Director.Evaluate());
        h.Poll();
        Assert.True(h.Held);

        h.NextRound(seconds: 2);                            // 19 s since sta2
        h.Poll();

        Assert.False(h.Held);
        Assert.Equal(new[] { "sta1", "sta2" }, h.CastsSent);
        Assert.Contains(h.InfoLog("Loop"),
            m => m.StartsWith("no priority buff went out for 18 seconds (priority buffs still due: #spent wand)"));
    }

    // The game refuses the buff every time, so it is due again every round and each
    // send restarts the 18 seconds. Its third send is the last it gets outside this
    // lair, however often the step in is asked for again.
    [Fact]
    public void PriorityBuffRefusedEveryRound_IsGivenUpOnAtItsThirdSend_AndNotSentAgainOnAReAsk()
    {
        using Harness h = new();
        h.AddBuff("star", priority: true);

        Assert.False(h.AskToEnter());
        for (int send = 1; send <= LairEntryDebuffHold.MaxSendsOfOnePriorityBuff; send++)
        {
            Assert.True(h.Held);
            Assert.Equal("star", h.Director.Evaluate());
            h.GameFizzlesTheCast("star");
            h.Poll();
            h.NextRound(); h.Poll();
        }

        Assert.False(h.Held);                               // a fourth send is not waited for
        Assert.Null(h.Director.Evaluate());                 // nor made: buffs are held for the step in
        Assert.Contains(h.InfoLog("Loop"), m => m.StartsWith(
            "priority buff star went out 3 times outside this lair, the most one may"));

        for (int reAsk = 1; reAsk <= 3; reAsk++)
        {
            h.NextRound(seconds: 6);                        // the step never landed
            Assert.True(h.AskToEnter());                    // star is due, but no longer waited for
            Assert.Null(h.Director.Evaluate());
        }
        Assert.Equal(new[] { "star", "star", "star" }, h.CastsSent);
    }

    // A priority roll spell set to reroll without end goes out every round. It gets
    // its three sends, then the next priority buff gets its turn, and the staged
    // reroll is cast after the fight like any held buff.
    [Fact]
    public void PriorityRollSpellRerollingWithoutEnd_IsGivenUpOn_AndTheNextPriorityBuffIsStillCast()
    {
        using Harness h = new();
        h.AddBuff("flux", priority: true);
        h.AddBuff("sta2", priority: true);

        Assert.False(h.AskToEnter());
        Assert.Equal("flux", h.Director.Evaluate());
        h.Poll();
        h.NextRound(); h.Director.RequestManaRegenReroll("flux"); h.Poll();   // rolled low: the second send
        h.NextRound(); h.Director.RequestManaRegenReroll("flux"); h.Poll();   // and the third
        Assert.Equal(new[] { "flux", "flux", "flux" }, h.CastsSent);

        h.NextRound(); h.Director.RequestManaRegenReroll("flux");             // low again: sta2 goes instead
        h.Poll();
        Assert.Equal("sta2", h.CastsSent[^1]);
        h.NextRound(); h.Poll();

        Assert.False(h.Held);
        Assert.Equal(4, h.CastsSent.Count);
        Assert.Equal("no priority buff is due (held back after 3 sends: flux) and the between-round cast is free "
            + "— entering the lair", h.InfoLog("Loop").Last());

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));            // in, fought, moved on
        h.NextRound(seconds: 6);
        Assert.Equal("flux", h.Director.Evaluate());                          // the reroll it still owed
    }

    // Stop pressed with five priority buffs still to go: the wait ends at its next
    // look instead of running on for 25 seconds with the gate up and the others held.
    [Fact]
    public void LoopStoppedMidWait_TheGateAndTheHeldBuffsAreReleasedAtOnce()
    {
        using Harness h = new();
        for (int i = 1; i <= 5; i++) h.AddBuff($"sta{i}", priority: true);
        h.AddBuff("plai", priority: false);
        Assert.False(h.AskToEnter());
        Assert.Equal("sta1", h.Director.Evaluate());
        h.Poll();

        h.LoopStillAtTheDoor = false;               // Stop: the loop never asks again
        h.NextRound();
        h.Poll();

        Assert.False(h.Held);
        h.NextRound(); h.Director.Evaluate();
        h.NextRound(); h.Director.Evaluate();
        h.NextRound(); h.Director.Evaluate();
        h.NextRound(); h.Director.Evaluate();
        h.NextRound();
        Assert.Equal("plai", h.Director.Evaluate());   // ordinary buffing is back to normal
    }

    // The same five buffs under Wait for spells: its three rounds are what they were.
    [Fact]
    public void WaitForSpells_StillStopsAtThreeRounds_WithFivePriorityBuffsDue()
    {
        using Harness h = new();
        for (int i = 1; i <= 5; i++) h.AddBuff($"sta{i}", priority: true);

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        for (int i = 1; i <= LairEntryDebuffHold.MaxRoundsWaited; i++)
        {
            Assert.Equal($"sta{i}", h.Director.Evaluate());
            h.Poll();
            h.NextRound(); h.Poll();
        }

        Assert.False(h.Held);
        Assert.Equal(new[] { "sta1", "sta2", "sta3" }, h.CastsSent);
        Assert.Contains(h.InfoLog("Loop"),
            m => m.StartsWith("waited the limit of 3 rounds of casts (buffs still due: sta4, sta5)"));
    }

    // What the scheduler isn't casting right now isn't "due", so it isn't waited for.
    [Fact]
    public void PriorityBuffTheSchedulerIsNotCasting_IsNotWaitedFor()
    {
        using Harness belowFloor = new();
        belowFloor.AddBuff("star", priority: true);
        belowFloor.State.Ma = 40;                           // under the buff's own 50% floor
        Assert.True(belowFloor.AskToEnter());

        using Harness blessOff = new() { AutoBless = false };
        blessOff.AddBuff("star", priority: true);
        Assert.True(blessOff.AskToEnter());

        using Harness everythingOff = new() { AutoBless = false, AutoHeal = false };
        everythingOff.AddBuff("star", priority: true);
        Assert.True(everythingOff.AskToEnter());

        using Harness whenFull = new();
        whenFull.Buffs.Slots.Add(new BuffSlot
            { Spell = "star", CastOnSelf = true, PriorityBuff = true, OnlyWhenHpFull = true });
        whenFull.State.Hp = 100;                            // not topped off: its condition isn't met
        Assert.True(whenFull.AskToEnter());
    }

    // Heals and cures are as in Wait for spells: never held, and a due one is waited for.
    [Fact]
    public void DueHeal_IsCastAndWaitedFor_WhileOrdinaryBuffsStayHeld()
    {
        using Harness h = new();
        h.Spells.MajorHealSpell = "heal";
        h.AutoHeal = false;                                 // take the wound without a pass healing it
        h.State.Hp = 70;                                    // 35%: under the 40% major-heal trigger
        h.AutoHeal = true;
        h.AddBuff("plai", priority: false);

        Assert.False(h.AskToEnter());                       // the heal is due: stand outside
        Assert.Equal("heal", h.Director.Evaluate());
        h.Poll();
        h.State.Hp = 200;
        h.NextRound();
        h.Poll();

        Assert.False(h.Held);
        Assert.Equal(new[] { "heal" }, h.CastsSent);
    }

    // A heal that is due round after round (the wound doesn't close) with no priority
    // buff going out: the 18 seconds end the wait, and the log names the heal it ran
    // out on rather than "none".
    [Fact]
    public void HealDueEveryRound_EndsTheWaitAfterEighteenSeconds_AndTheLogNamesTheHeal()
    {
        using Harness h = new();
        h.Spells.MajorHealSpell = "heal";
        h.AutoHeal = false;
        h.State.Hp = 70;
        h.AutoHeal = true;

        Assert.False(h.AskToEnter());
        for (int round = 0; round < 10 && h.Held; round++)
        {
            h.Director.Evaluate();                          // the heal goes out; HP stays low
            h.Poll();
            h.NextRound();
            h.Poll();
        }

        Assert.False(h.Held);
        Assert.Contains(h.InfoLog("Loop"), m => m.StartsWith(
            "no priority buff went out for 18 seconds (a heal or cure still due: heal)"));
    }

    // The priority buff goes out and leaves mana under the debuff's own minimum. The
    // loop stays for the mana, with every buff held (the ordinary one, and a priority
    // one that comes due meanwhile), then walks in and the debuff gets the cast.
    [Fact]
    public void ManaUnderTheDebuffsMinimumAfterThePriorityBuffs_HoldsForMana_ThenEntersAndDebuffs()
    {
        using Harness h = new() { DebuffMinMana = 40 };
        h.AddBuff("star", priority: true);
        h.AddBuff("plai", priority: false);

        Assert.False(h.AskToEnter());
        Assert.Equal("star", h.Director.Evaluate());
        h.State.Ma = 25;                                    // what the buff cost
        h.Poll();
        h.NextRound(); h.Poll();

        Assert.True(h.Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffManaGate));
        Assert.False(h.Held);
        Assert.Contains("waiting outside the lair for mana: 25 of the 40 the debuff dbuf needs "
            + "(its min mana per cast) — buffs held back meanwhile, bar the ones that bring mana back; a monster in this room is fought", h.InfoLog("Loop"));

        h.AddBuff("late", priority: true);                  // comes due while it stands there
        h.NextRound(seconds: 30);
        Assert.Null(h.Director.Evaluate());                 // held, like plai
        h.Poll();
        Assert.True(h.Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffManaGate));

        h.State.Ma = 45;                                    // regenerated (or rested) back over it
        h.Poll();

        Assert.False(h.Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffManaGate));
        Assert.Equal("mana is back at 45, and the debuff dbuf needs 40 — entering the lair", h.InfoLog("Loop").Last());
        h.DebuffOwed = "dbuf";
        Assert.Equal("dbuf", h.Director.Evaluate());
        Assert.Equal(new[] { "star", "dbuf" }, h.CastsSent);
    }

    // A monster comes out to the doorstep while the loop stands there for mana. The
    // debuff is for the lair: in this fight nothing is held, and a shield cast "during
    // combat" that comes due goes out. The wait is still there when the fight is over.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void FightAtTheDoorstepDuringAManaWait_HoldsNoBuff_AndTheWaitGoesOnAfterIt(LairEntryDebuffMode mode)
    {
        using Harness h = new() { DebuffMinMana = 60 };
        h.AutoBless = false;                                // keep the mana change from casting anything
        h.State.Ma = 30;
        h.AutoBless = true;
        Assert.False(h.Hold.ReadyToEnter(mode, new RoomKey(1, 100)));
        Assert.True(h.HeldForMana);
        h.AddBuff("shld", priority: false, duringCombat: true).BlessIfAboveMa = 0;   // comes due as the fight starts

        h.State.InCombat = true;                            // the scheduler's pass on the fight starting

        Assert.Equal(new[] { "shld" }, h.CastsSent);        // not held: we fight with what we have
        h.NextRound(seconds: 30);
        h.Poll();
        Assert.True(Waiting(h.Hold));                      // no step is decided on mid-fight

        h.State.InCombat = false;
        h.Poll();
        Assert.True(h.HeldForMana);                         // the fight is over; mana is still short
        h.State.Ma = 65;
        h.Poll();
        Assert.False(Waiting(h.Hold));
    }

    // A long fight in the lair where the debuff never comes (the boss can't be
    // debuffed): a "during combat" buff that comes due is not held for the fight. It
    // goes out by the fight's second round at the latest, whatever the way of waiting.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void LongFightInTheLairWithNoDebuff_ADuringCombatBuffGoesOutByTheThirdRound(LairEntryDebuffMode mode)
    {
        using Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(mode, new RoomKey(1, 100)));
        h.NextRound(seconds: 2);                            // the step lands
        h.State.InCombat = true;                            // engaged; the engine sent no signal (it put a debuff off)
        h.AddBuff("shl2", priority: false, duringCombat: true);   // ran out, or was knocked off
        List<string?> rounds = new();
        for (int round = 0; round < 24; round++)            // two minutes of fight
        {
            h.NextRound();
            rounds.Add(h.Director.Evaluate());
        }

        Assert.Null(rounds[0]);                             // two rounds' casts are kept for the debuff
        Assert.Null(rounds[1]);
        Assert.Equal("shl2", rounds[2]);
        Assert.Contains("buffs released: the fight is two rounds old with no debuff sent", h.InfoLog("Loop"));
    }

    // A debuff the engine put off (a backstab went first, or a heal took the cast)
    // sends no signal. It is offered again a round later, and must get that cast ahead
    // of a during-combat buff: at six seconds, where the hold used to end, and up to
    // two rounds in.
    [Theory]
    [InlineData(5.5, "dbuf")]
    [InlineData(6.5, "dbuf")]
    [InlineData(10.5, "dbuf")]
    [InlineData(11.5, "shld")]
    public void PutOffDebuff_GetsItsCastAheadOfADuringCombatBuff_ForTwoRounds(double secondsIntoTheFight, string cast)
    {
        using Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        h.NextRound(seconds: 1);
        h.State.InCombat = true;
        h.Director.MarkBetweenRoundSlotUsed();              // the round's cast went elsewhere: the debuff is put off
        h.AddBuff("shld", priority: false, duringCombat: true);
        h.DebuffOwed = "dbuf";                              // offered again from the next round

        h.Now = h.Now.AddSeconds(secondsIntoTheFight);
        h.Cast.OnCombatTick();
        h.Director.NotifyRoundComplete();

        Assert.Equal(cast, h.Director.Evaluate());
    }

    // The combat engine opens the lair's fight with no debuff to cast: the buff goes
    // out on the very next pass.
    [Fact]
    public void EngineOpensTheLairsFightWithNoDebuffToCast_ADuringCombatBuffGoesOutAtOnce()
    {
        RoomKey lair = new(1, 100);
        using Harness h = new() { FightRoom = new RoomKey(1, 99) };
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, lair));
        h.AddBuff("shl2", priority: false, duringCombat: true);
        h.NextRound(seconds: 2);
        h.FightRoom = lair;                                 // the step is in flight into the lair
        h.State.InCombat = true;
        Assert.Empty(h.CastsSent);

        h.Hold.NoteNoDebuffThisFight();

        Assert.Equal("shl2", h.Director.Evaluate());
    }

    // The reviewer's sequence: the go-ahead with a buff held, then the engine engages
    // something at the door with no debuff to cast. The buff used to go out on the
    // next pass and spend the cast the lair's debuff needed. It stays held.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void EngineSignalAtTheDoorAfterTheGoAhead_TheHeldBuffStaysHeld_AndTheCastStaysFree(LairEntryDebuffMode mode)
    {
        RoomKey lair = new(1, 100);
        using Harness h = new() { FightRoom = new RoomKey(1, 99) };
        Assert.True(h.Hold.ReadyToEnter(mode, lair));       // the go-ahead
        h.AddBuff("plai", priority: false);                 // due, and held for the step in

        h.Hold.NoteNoDebuffThisFight();                     // an engage at the door, no debuff to cast

        Assert.Null(h.Director.Evaluate());
        Assert.False(h.Director.BetweenRoundSlotUsed);
        Assert.Empty(h.CastsSent);

        using Harness unknown = new();                      // where the fight is isn't known: the same
        Assert.True(unknown.Hold.ReadyToEnter(mode, lair));
        unknown.AddBuff("plai", priority: false);
        unknown.Hold.NoteNoDebuffThisFight();
        Assert.Null(unknown.Director.Evaluate());
    }

    // Standing for mana, a mana-bringing buff that is always due again (shorter than
    // its recast lead, or refused) is not cast round after round: three sends, like
    // any buff a wait lets through, then it is held with the rest.
    [Fact]
    public void StandingForMana_AManaBuffThatIsAlwaysDue_IsGivenUpOnAfterThreeSends()
    {
        using Harness h = new() { DebuffMinMana = 60 };
        h.AutoBless = false;
        h.State.Ma = 30;
        h.AutoBless = true;
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        h.AddBuff("regn", priority: false).BlessIfAboveMa = 0;
        h.ManaRegenBuffs.Add("regn");
        h.BuffDurations["regn"] = 10;                       // under its recast lead: due again at once
        h.Health.RestIfBelowMa = 0;

        for (int round = 0; round < 8; round++) h.Round();

        Assert.True(h.HeldForMana);
        Assert.Equal(new[] { "regn", "regn", "regn" }, h.CastsSent);
        Assert.Contains(h.InfoLog("Loop"), m => m.StartsWith("buff regn went out 3 times outside this lair, the most one may"));
    }

    // A fight where the loop stands waiting is said in the log, once.
    [Fact]
    public void FightAtTheDoor_IsLoggedOnce()
    {
        using Harness h = new();
        h.AddBuff("star", priority: true);
        Assert.False(h.AskToEnter());

        h.State.InCombat = true;                            // the scheduler's pass on the fight starting
        for (int look = 0; look < 5; look++) { h.Poll(); h.Director.Evaluate(); }
        h.State.InCombat = false;
        h.Poll();

        Assert.Single(h.InfoLog("Loop"), m => m == "a fight at the door: buffs are free, the wait resumes after");
        Assert.True(Waiting(h.Hold));
    }

    // An item-cast buff ticked priority is a priority buff to the scheduler, so it is
    // used during the wait like a cast one.
    [Fact]
    public void ItemCastPriorityBuff_IsUsedDuringTheWait()
    {
        using Harness h = new();
        h.AddBuff("#silver wand", priority: true);

        Assert.False(h.AskToEnter());
        Assert.Equal("#silver wand", h.Director.Evaluate());
        Assert.Equal(new[] { "#silver wand" }, h.ItemsUsed);
        h.Poll();
        h.NextRound();
        h.Poll();

        Assert.False(h.Held);
    }

    [Fact]
    public void TheLog_SaysWhatIsWaitedFor_WhatIsHeld_EachCast_AndWhyTheWaitEnded()
    {
        using Harness h = new();
        h.AddBuff("plai", priority: false);
        h.AddBuff("star", priority: true);

        Assert.False(h.AskToEnter());
        h.Director.Evaluate();
        h.Poll();
        h.NextRound();
        h.Poll();

        Assert.Equal(new[]
        {
            "waiting outside the lair until the debuff can be cast — casting priority buffs first "
                + "(due: star); other buffs held back (due: plai)",
            "priority buff star cast while waiting outside the lair",
            "no priority buff is due and the between-round cast is free — entering the lair",
        }, h.InfoLog("Loop"));
    }

    // The same buffs under the two older modes, to pin that they are untouched: Wait
    // for spells casts both kinds, Block spells casts neither.
    [Fact]
    public void TheOtherModes_TreatPriorityAndOrdinaryBuffsAlike()
    {
        using Harness wait = new();
        wait.AddBuff("plai", priority: false);
        wait.AddBuff("star", priority: true);
        Assert.False(wait.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        Assert.Equal("star", wait.Director.Evaluate());
        wait.Poll(); wait.NextRound(); wait.Poll();
        Assert.True(wait.Held);
        Assert.Equal("plai", wait.Director.Evaluate());

        using Harness block = new();
        block.AddBuff("plai", priority: false);
        block.AddBuff("star", priority: true);
        Assert.True(block.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));
        Assert.Null(block.Director.Evaluate());
    }

    // ----- When the held buffs are let through again ----------------

    // In the lair the buffs are held for the debuff, through the fight if need be.
    // The moment it is sent they are let through: the next free cast is a buff's, by
    // the buff's own "during combat" setting, whatever the way of waiting.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void DebuffSentInTheLair_AnOrdinaryBuffGoesOutAtTheNextFreeCast(LairEntryDebuffMode mode)
    {
        using Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(mode, new RoomKey(1, 100)));   // nothing due at the door: straight in
        h.AddBuff("plai", priority: false, duringCombat: true);       // comes due as the fight starts
        h.State.InCombat = true;
        h.NextRound(seconds: 4);
        Assert.Null(h.Director.Evaluate());                 // four seconds into the fight: held for the debuff

        h.DebuffOwed = "dbuf";
        Assert.Equal("dbuf", h.Director.Evaluate());
        Assert.Contains("debuff dbuf sent: buffs released", h.InfoLog("Loop"));
        Assert.False(h.Hold.BlockingBuffs);

        h.NextRound();
        Assert.Equal("plai", h.Director.Evaluate());        // the next free cast, mid-fight
        Assert.Equal(new[] { "dbuf", "plai" }, h.CastsSent);
    }

    // The combat engine sends its pre-attack debuff itself on a fresh engage, not
    // through a pass of the scheduler; the scheduler is told, and so is the hold.
    [Fact]
    public void PreAttackDebuffSentByTheCombatEngine_ReleasesTheBuffsToo()
    {
        using Harness h = new();
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        h.AddBuff("plai", priority: false, duringCombat: true);
        h.State.InCombat = true;

        h.Director.NotePreAttackDebuffSent("dbuf");

        Assert.Contains("debuff dbuf sent: buffs released", h.InfoLog("Loop"));
        Assert.Null(h.Director.Evaluate());                 // the debuff took this round's cast
        h.NextRound();
        Assert.Equal("plai", h.Director.Evaluate());
    }

    // A lair with nothing to debuff (the debuff's conditions aren't met, or it is
    // refused): no debuff goes out, and the buffs are let through as the fight ends.
    // The hold is back at the next lair's doorstep.
    [Fact]
    public void NoDebuffGoesOutInTheLair_TheBuffsAreReleasedAsTheFightEnds_AndHeldAgainAtTheNextDoorstep()
    {
        using Harness h = new();
        h.AddBuff("plai", priority: false);                 // out of combat only
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));
        h.State.InCombat = true;
        h.NextRound(seconds: 4);
        Assert.Null(h.Director.Evaluate());

        h.State.InCombat = false;                           // the kill: the scheduler's pass, and the hold is off

        Assert.Equal(new[] { "plai" }, h.CastsSent);
        Assert.Contains("buffs released: the fight ended with no debuff sent", h.InfoLog("Loop"));

        h.AddBuff("late", priority: false);                 // due as the loop comes to the next lair
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 101)));
        h.NextRound();
        Assert.Null(h.Director.Evaluate());                 // held again: plai had its cast, none is owed
        h.Poll();
        Assert.False(h.Held);
        Assert.Equal(new[] { "plai" }, h.CastsSent);
    }

    // Six lairs side by side, each a one-round kill whose last hit and kill arrive
    // together, so the scheduler's pass at the kill waits for HP to settle and the
    // loop is at the next doorstep first. Buffs that only cast out of combat used to
    // be held at every one and never went out. Each doorstep now lets one go before
    // it holds them again.
    [Theory]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void SixLairsSideBySide_TheHeldBuffsStillGoOut_OneAtEachDoorstep(LairEntryDebuffMode mode)
    {
        using Harness h = new();
        h.HoldPassesUntilHpSettles();
        h.AddBuff("pla1", priority: false);
        h.AddBuff("pla2", priority: false);
        List<int> castsAfterEachLair = new();

        for (int lair = 1; lair <= 6; lair++)
        {
            RoomKey door = new(1, 100 + lair);
            bool straightIn = h.Hold.ReadyToEnter(mode, door);  // asked the moment the last fight ended
            h.Now = h.Now.AddMilliseconds(450);
            h.RunSettledPasses();                               // the scheduler's pass, after the ask
            if (!straightIn)
            {
                h.Poll();
                h.NextRound(); h.Poll();
                Assert.True(h.Hold.ReadyToEnter(mode, door));
            }
            h.State.Hp = 200;
            castsAfterEachLair.Add(h.CastsSent.Count);

            h.Now = h.Now.AddSeconds(2);                        // the step lands on the lair's monster
            h.State.InCombat = true;
            h.NextRound(seconds: 4);
            h.State.Hp = 180;                                   // its hit ...
            h.State.InCombat = false;                           // ... and its death, in one burst
        }

        Assert.Equal(new[] { "pla1", "pla2" }, h.CastsSent);
        // None at the first door (nothing had been held yet), one at each of the next
        // two, and no more waiting once both are up.
        Assert.Equal(new[] { 0, 1, 2, 2, 2, 2 }, castsAfterEachLair);
    }

    // Two lairs in a row and a priority buff that is always due again (its duration
    // under its recast lead; one the game refuses and a roll spell rerolling do the
    // same). The second door used to give the owed cast a fresh eight seconds at every
    // send, with the give-up switched off inside it: the loop stood there for good.
    // Every door is now what the first is: three sends, given up on, in.
    [Theory]
    [InlineData(true, LairEntryDebuffMode.WaitForPriorityBuffs)]
    [InlineData(false, LairEntryDebuffMode.WaitForPriorityBuffs)]
    [InlineData(false, LairEntryDebuffMode.BlockSpells)]
    public void PriorityBuffAlwaysDue_LairsInARow_EveryDoorIsBounded(bool ordinaryDue, LairEntryDebuffMode mode)
    {
        using Harness h = new();
        h.AddBuff("star", priority: true);
        h.BuffDurations["star"] = 10;                       // always due again
        if (ordinaryDue) h.AddBuff("plai", priority: false);

        for (int lair = 1; lair <= 3; lair++)
        {
            RoomKey door = new(1, 100 + lair);
            int sentBefore = h.CastsSent.Count;
            DateTime atTheDoor = h.Now;
            bool ready = h.Hold.ReadyToEnter(mode, door);
            for (int round = 0; !ready && h.Held && round < 120; round++) h.Round();

            Assert.False(h.Held);
            // Three sends of one priority buff at a door, a round each: fifteen seconds,
            // and one round more when the loop arrives on a cast the last fight spent.
            // Block spells holds the buff and is in within that one round.
            Assert.True(h.Now - atTheDoor <= TimeSpan.FromSeconds(20), $"lair {lair}: {(h.Now - atTheDoor).TotalSeconds}s");
            Assert.True(h.CastsSent.Count - sentBefore <= LairEntryDebuffHold.MaxSendsOfOnePriorityBuff);

            Assert.True(h.Hold.ReadyToEnter(mode, door));
            h.NextRound(seconds: 2);
            h.State.InCombat = true;
            h.Director.NotePreAttackDebuffSent("dbuf");
            h.NextRound(seconds: 4);
            h.Director.MarkBetweenRoundSlotUsed();          // a one-round fight; the cast spent in it
            h.State.InCombat = false;
        }

        // Said when a buff is given up on, once a door: not at every send after it.
        Assert.True(h.InfoLog("Loop").Count(m => m.StartsWith("priority buff star went out")) <= 3);
    }

    // Lairs with one ordinary room between them, crossed in 200 ms: under the 400 ms
    // the scheduler's pass waits for HP to settle after a kill whose last hit comes
    // with it. The ask for the step into that room used to forget what the lair had
    // held, and the buffs that only cast out of combat never went out.
    [Theory]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void LairOrdinaryRoomLair_TheHeldBuffsStillGoOut(LairEntryDebuffMode mode)
    {
        using Harness h = new();
        h.HoldPassesUntilHpSettles();
        h.AddBuff("pla1", priority: false);
        h.AddBuff("pla2", priority: false);

        for (int lair = 1; lair <= 6; lair++)
        {
            RoomKey door = new(1, 100 + lair);
            bool straightIn = h.Hold.ReadyToEnter(mode, door);
            for (int look = 0; !straightIn && h.Held && look < 60; look++)
            {
                h.Now = h.Now.AddMilliseconds(500);
                h.RunSettledPasses();
                h.Director.Evaluate();
                h.Poll();
                if (look % 10 == 9) h.NextRound(seconds: 0);
            }
            Assert.False(h.Held);
            Assert.True(h.Hold.ReadyToEnter(mode, door));

            h.Now = h.Now.AddSeconds(2);                        // the step lands
            h.State.InCombat = true;
            h.Director.NotePreAttackDebuffSent("dbuf");
            h.NextRound(seconds: 4);
            h.NextRound();
            h.State.Hp = 180;                                   // the last round's hit ...
            h.State.InCombat = false;                           // ... and the kill, in one burst
            h.State.Hp = 200;
            Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));   // the step into the room between
            h.Now = h.Now.AddMilliseconds(200);                 // it lands; the next step is a lair again
        }

        Assert.Contains("pla1", h.CastsSent);
        Assert.Contains("pla2", h.CastsSent);
    }

    // An ordinary item buff whose item can't be used is due for ever and never sent.
    // The second door gives it its eight seconds; the doors after that give it none.
    [Theory]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void OwedCastThatCanNeverBeCast_CostsItsEightSecondsOnce_NotAtEveryDoor(LairEntryDebuffMode mode)
    {
        using Harness h = new();
        h.AddBuff("#wand", priority: false);
        h.UnusableItems.Add("#wand");
        List<double> stoodAtEachDoor = new();

        for (int lair = 1; lair <= 5; lair++)
        {
            RoomKey door = new(1, 100 + lair);
            DateTime atTheDoor = h.Now;
            bool ready = h.Hold.ReadyToEnter(mode, door);
            for (int look = 0; !ready && h.Held && look < 200; look++)
            {
                h.Director.Evaluate();
                h.Poll();
                h.Now = h.Now.AddSeconds(1);
                h.Poll();
            }
            stoodAtEachDoor.Add((h.Now - atTheDoor).TotalSeconds);
            Assert.True(h.Hold.ReadyToEnter(mode, door));

            h.Now = h.Now.AddSeconds(2);
            h.State.InCombat = true;
            h.Director.NotePreAttackDebuffSent("dbuf");
            h.NextRound(seconds: 4);
            h.NextRound();
            h.State.InCombat = false;
            h.NextRound(seconds: 1);
        }

        Assert.Equal(new double[] { 0, 8, 0, 0, 0 }, stoodAtEachDoor);
    }

    // Mana that will never reach the debuff's minimum: the game calls it full under
    // the maximum the client believes in. The wait had no way out; it now says so
    // once and goes in.
    [Fact]
    public void ManaFullUnderTheDebuffsMinimum_IsNotStoodForAnHour()
    {
        using Harness h = new() { DebuffMinMana = 95 };
        h.State.Ma = 94;
        h.ManaIsFull = true;                                // "Meditation will not help at this time."

        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells, new RoomKey(1, 100)));

        Assert.Contains("the debuff dbuf asks for 95 mana (its min mana per cast) and mana is full at 94: it will "
            + "come back no higher, so the lair is entered without waiting for it", h.InfoLog("Loop"));
    }

    // Standing for mana, the buffs that bring mana back still go out: here a
    // mana-regen buff that comes due. Every other buff waits.
    [Theory]
    [InlineData(LairEntryDebuffMode.WaitForSpells)]
    [InlineData(LairEntryDebuffMode.BlockSpells)]
    [InlineData(LairEntryDebuffMode.WaitForPriorityBuffs)]
    public void StandingForMana_TheBuffsThatBringManaBackStillGoOut(LairEntryDebuffMode mode)
    {
        using Harness h = new() { DebuffMinMana = 60 };
        h.AutoBless = false;
        h.State.Ma = 30;
        h.AutoBless = true;
        Assert.False(h.Hold.ReadyToEnter(mode, new RoomKey(1, 100)));
        Assert.True(h.HeldForMana);
        h.AddBuff("plai", priority: false).BlessIfAboveMa = 0;
        h.AddBuff("regn", priority: true).BlessIfAboveMa = 0;
        h.ManaRegenBuffs.Add("regn");
        h.Health.RestIfBelowMa = 0;

        Assert.Equal("regn", h.Director.Evaluate());        // the one that brings mana back
        h.NextRound();
        Assert.Null(h.Director.Evaluate());                 // plai waits
        h.Poll();
        Assert.True(h.HeldForMana);
        Assert.Equal(new[] { "regn" }, h.CastsSent);
    }

    // Asking what is due must change nothing. A reroll staged while Block spells holds
    // every buff can't be paid for at the moment a bug report is captured (or a log
    // line is built); the hold was keeping it for after the fight, and it is still cast.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DescribingAHold_OrAskingWhatIsDue_DropsNoStagedReroll(bool asked)
    {
        using Harness h = new();
        h.AddBuff("flux", priority: false);
        Assert.Equal("flux", h.Director.Evaluate());        // up, its timer running
        h.NextRound();
        h.Director.MarkBetweenRoundSlotUsed();
        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));
        h.Director.RequestManaRegenReroll("flux");          // rolled low: a reroll is staged, and held
        h.State.Ma = 40;                                    // under the buff's 50% floor for now

        if (asked)
        {
            h.Hold.Describe();
            h.Director.BuffsDue(priority: false);
            h.Director.HasCastDue();
        }

        h.State.Ma = 100;                                   // mana back before the hold ends
        h.NextRound(); h.Poll();
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));
        Assert.Equal("flux", h.Director.Evaluate());
    }

    [Fact]
    public void BuffsDue_ListsEachKindInCastOrder_WhetherOrNotTheHoldKeepsItBack()
    {
        using Harness h = new();
        h.AddBuff("pla1", priority: false);
        h.AddBuff("star", priority: true);
        h.AddBuff("pla2", priority: false);

        Assert.False(h.AskToEnter());                       // the ordinary ones are held now

        Assert.Equal(new[] { "star" }, h.Director.BuffsDue(priority: true));
        Assert.Equal(new[] { "pla1", "pla2" }, h.Director.BuffsDue(priority: false));
        Assert.True(h.Director.HasCastDue());

        h.Director.Evaluate();                              // star goes out
        Assert.Empty(h.Director.BuffsDue(priority: true));
        Assert.False(h.Director.HasCastDue());              // the held ones don't count as due
    }
}
