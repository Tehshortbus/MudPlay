using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// PR 9.A (spell extension) — <see cref="CombatManager"/> combat-spell
/// round economy: the chooser-driven cast path that suppresses weapon
/// swings, the per-round heartbeat re-cast, and the opt-in guard that keeps
/// the weapon engine unchanged until <see cref="CombatManager.SetCombatSpellCaster"/>
/// is wired.
/// </summary>
public sealed class CombatManagerSpellsTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatManager Combat { get; }
        public CastCoordinator Cast { get; }
        public List<byte[]> Sent { get; } = new();
        public CombatSettings Settings { get; set; } = new()
        {
            NormalAttackCommand = "a",
            TargetOrder = TargetOrder.Normal,
        };

        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();

        // Spell.Number → Short cast-code, feeding the per-monster override
        // resolver. An unmapped number resolves to null (unknown → fall back).
        public Dictionary<int, string> SpellShorts { get; } = new();
        // Resolved spells by cast-code, for the debuff-slot guard (energy / targeting).
        public Dictionary<string, KnownSpell> SpellsByCode { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool AutoCombatEnabled { get; set; } = true;
        public bool AutoNukeEnabled { get; set; } = true;
        // The player a room attack would hit and mustn't (see SetRoomAttackHold).
        public string? RoomAttackHeldBy { get; set; }
        public int Ma { get; set; } = 100;
        public int MaxMa { get; set; } = 100;
        public bool Sneaking { get; set; }
        public HashSet<int> SeeHidden { get; } = new();

        // Fake clock for CombatManager.SetClock — Tick() advances it by a full
        // round each call, matching TickEngine.CombatTickInterval (5s), so
        // AlternationAdvanceMinGap sees genuine round-to-round elapsed time
        // instead of the zero real elapsed time a synchronous test call has.
        private DateTimeOffset _clock = DateTimeOffset.UtcNow;

        // Models RoundDamageTracker's timer-driven RoundCount: a genuine round
        // boundary (Tick / TickFast) advances it; a premature same-round tick
        // (TickSameRound) does not. Drives the attack-spell MaxCasts tally.
        private int _roundCount;

        // When deferPost is set, the cascade switch-dispatch scheduler queues actions
        // into Posted instead of running them inline, so a test can interleave server
        // lines between a deferred switch-dispatch being scheduled and it actually
        // running — mirroring production, where the real-time delay lets the kill's
        // exp / *Combat Off* packet land + drop the target before the switch fires.
        // DrainPosted() runs the queue (the delay window elapsing).
        public List<Action> Posted { get; } = new();
        private readonly bool _deferPost;
        public void DrainPosted()
        {
            List<Action> due = new(Posted);
            Posted.Clear();
            foreach (Action a in due) a();
        }

        // The death watcher, wired as AppServices wires it: built ahead of the combat
        // manager, a room spell's kill raised through it from the combat manager, and
        // every other death routed to NoteUnattributedDeath. Only with
        // wireDeathWatcher; other tests call NoteUnattributedDeath themselves.
        public MonsterDeathWatcher? Deaths { get; }
        public List<MonsterDeathEvent> DeathEvents { get; } = new();

        public Harness(bool wireCaster = true, bool deferPost = false, bool wireDeathWatcher = false)
        {
            _deferPost = deferPost;
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            if (wireDeathWatcher)
            {
                Deaths = new MonsterDeathWatcher(Router, Log);
                Deaths.MonsterDied += evt =>
                {
                    DeathEvents.Add(evt);
                    if (evt.RoomSpellRoster is null) Combat!.NoteUnattributedDeath();
                };
            }
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(b => Sent.Add(b));
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: n => Overlays.TryGetValue(n, out MonsterOverlay? o)
                                     ? o : new MonsterOverlay(),
                party: Party,
                readSettings: () => Settings,
                isEnabled: () => AutoCombatEnabled,
                readOwnGivenName: () => "MudPlay",
                post: a => { if (_deferPost) Posted.Add(a); else a(); },
                log: Log,
                resolveSpellByCode: c => SpellsByCode.TryGetValue(c, out KnownSpell s) ? s : null);
            Combat.SetWireSender(b => Sent.Add(b));
            if (Deaths is not null) Combat.RoomSpellKill += Deaths.NoteRoomSpellKill;
            Combat.SetClock(() => _clock);
            // Production counts MaxCasts off RoundDamageTracker.RoundCount; mirror it.
            Combat.ReadRoundCount = () => _roundCount;
            Combat.SetBackstabHooks(() => Sneaking, n => SeeHidden.Contains(n));
            Combat.SetAutoNukeGate(() => AutoNukeEnabled);
            Combat.SetRoomAttackHold(() => RoomAttackHeldBy);
            Combat.SetSpellShortResolver(
                n => SpellShorts.TryGetValue(n, out string? s) ? s : null);
            // Store the settle callback instead of running a real timer so a test
            // controls when the window elapses (FireSettle). Only arms on arrival
            // observations, so the existing Also-Here tests are unaffected.
            Combat.SetArrivalSettleScheduler((_, cb) => PendingSettle = cb);
            // The cascade switch-dispatch delay (production path). Runs inline unless a
            // test opts into deferPost, in which case it queues into Posted so the test
            // controls when the delay window elapses (DrainPosted) — modelling the kill
            // packet landing in the gap between schedule and dispatch.
            Combat.SetSwitchDispatchScheduler((_, cb) => { if (_deferPost) Posted.Add(cb); else cb(); });
            if (wireCaster)
                Combat.SetCombatSpellCaster(Cast, () => (Ma, MaxMa));
        }

        // The pending arrival-settle callback (see CombatManager). FireSettle runs
        // it, simulating the debounce window elapsing with no room re-display.
        public Action? PendingSettle { get; private set; }
        public void FireSettle() => PendingSettle?.Invoke();

        // Simulate a mid-room monster arrival ("A giant rat strides in …") — appends
        // to the classifier's Current with Source=Arrival, the path RoomEntryWatcher
        // drives in production.
        public void Arrive(string name)
            => Classifier.AppendArrivalEntity(
                Classifier.Classify(name),
                rawWireLine: $"A {name} strides in from the west.");

        // A player walking into the room mid-fight.
        public void ArrivePlayer(string given)
            => Classifier.AppendArrivalEntity(
                new RoomEntity(given, given, EntityKind.Player, MonsterNumber: null),
                rawWireLine: $"{given} walks into the room from the west.");

        public void SetOverlay(int monsterNumber, MonsterAttackPriority? priority = null,
                               MonsterRelationship? relationship = null)
            => Overlays[monsterNumber] = new MonsterOverlay
            {
                Priority = priority,
                Relationship = relationship,
            };

        public void AddMonster(int number, string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                Links: new[] { new GameDataLink("Monsters", number) }));

        // A monster the classifier recognises by name (so it's EntityKind.Monster)
        // but which carries no Monsters-table link, so its number never resolves —
        // the real-world case where the server colours a variant hostile whose
        // flavored name isn't in game data. Mirrors ResolveMonsterNumber returning
        // null; the room-nuke still hits it, so it must count toward MinEnemies.
        public void AddUnnumberedMonster(string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"U-{name}",
                Name: name,
                Links: Array.Empty<GameDataLink>()));

        // Moves the fake clock without the round-boundary side effects Tick()/
        // TickSameRound()/TickFast() carry (they also bump _roundCount and drive
        // Cast/Combat's tick) — for tests exercising ConfirmedAttackCastCount's own
        // real-time grouping window directly.
        public void AdvanceClock(TimeSpan by) => _clock += by;

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        // A line in the dark cyan the game prints a monster's miss in.
        public void FeedMiss(string line)
        {
            CellAttributes cyan = CellAttributes.Default with { Foreground = TerminalColor.Indexed(6) };
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Enumerable.Repeat(cyan, line.Length).ToArray(),
                DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        // A command as the server echoes it: the prompt row, then its trailing
        // text as a second line carrying the same timestamp.
        public void FeedEchoed(string command)
        {
            DateTimeOffset at = DateTimeOffset.UtcNow;
            Router.Dispatch(new LineExtractor.EmittedLine(
                "[HP=724/MA=343]:", Array.Empty<CellAttributes>(), at, IsPromptLine: true));
            Router.Dispatch(new LineExtractor.EmittedLine(
                command, Array.Empty<CellAttributes>(), at, IsPromptLine: false));
        }

        // A between-round cast's *Combat Off* that closes the open attack-spell
        // round. In production RoundDamageTracker (CombatStatus tieBreak 100)
        // CloseCurrent's that round — RoundCount++ — BEFORE CombatManager's resume
        // reads the count. RoundDamageTracker isn't wired here (the int _roundCount
        // is its stand-in), so bump first, then dispatch the Off — mirroring that
        // ordering so the resume tallies the interrupted spell toward MaxCasts.
        public void FeedOffClosingRound()
        {
            _roundCount++;
            Feed("*Combat Off*");
        }

        /// <summary>One combat round. Mirrors the AppServices tick-subscription
        /// order: the coordinator clears its cooldown first, then the combat
        /// heartbeat re-decides. (CastingDirector sits between them in production but
        /// isn't under test here.) Advances the fake clock a full round first, so
        /// AlternationAdvanceMinGap doesn't reject this as a same-round re-fire.</summary>
        public void Tick()
        {
            _clock += TimeSpan.FromSeconds(5);
            _roundCount++;   // a genuine round boundary closed (mirrors RoundDamage)
            Cast.OnCombatTick();
            Combat.OnCombatTick();
        }

        // An extra combat tick landing WITHIN the same ~5s round — the mob's
        // counter-swing line trips a second CombatTickElapsed a beat after our own
        // hit. Advances the fake clock only 1s (under AttackTallyMinGap) so the
        // MaxCasts tally gate should reject it as not a real round boundary.
        public void TickSameRound()
        {
            _clock += TimeSpan.FromSeconds(1);
            Cast.OnCombatTick();
            Combat.OnCombatTick();
        }

        // A genuine round-1 boundary tick that lands FAST — under AttackTallyMinGap
        // after the engage (the server delivered round 1's damage line ~3s out). A
        // solo fight must still tally this as round 1, not reject it as premature.
        public void TickFast()
        {
            _clock += TimeSpan.FromSeconds(3);
            _roundCount++;   // a genuine (fast) round boundary — must tally, not reject
            Cast.OnCombatTick();
            Combat.OnCombatTick();
        }

        public string LastSent => Sent.Count == 0
            ? string.Empty
            : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public IEnumerable<string> AllSent =>
            Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        public void Dispose()
        {
            Combat.Dispose();
            Deaths?.Dispose();
            Cast.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- opt-in guard ------------------------------------------------

    [Fact]
    public void CasterUnwired_MultiAttackConfigured_StillSwingsWeapon()
    {
        using Harness h = new(wireCaster: false);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void CasterWired_NoSpellsConfigured_StillSwingsWeapon()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    // ----- spell suppresses the weapon swing ---------------------------

    [Fact]
    public void MultiAttackQualifies_CastsSpell_NoWeaponSwing()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // A multi-attack (room-wide) spell is cast BARE — never "blast <mob>".
        Assert.Equal("blast", h.LastSent);
        Assert.DoesNotContain("a giant rat", h.AllSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    [Fact]
    public void MultiAttackBelowMinEnemies_FallsToWeapon()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    // Report paradigm-20260811-063728: a wrapped 5-mob "Also here:" where one
    // occupant's flavored name didn't resolve to a Monsters number was counted as
    // 4 by the heartbeat's engageable count, held below MinEnemies=5, so the room
    // spell never took over from the single-target cascade. The room-nuke hits
    // every monster regardless of whether we resolved its number, so an unknown-
    // number monster must count toward MinEnemies — matching the initial dispatch's
    // fail-open candidate build.
    [Fact]
    public void Heartbeat_CountsUnknownNumberMonster_TowardMinEnemies_RoomsBare()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");        // resolvable
        h.AddUnnumberedMonster("dark stalker");   // Monster kind, number never resolves

        // One resolvable mob → below MinEnemies=2 → single-target attack spell.
        h.Feed("Also here: giant rat.");
        Assert.Equal("nuke giant rat", h.LastSent);

        // A second mob arrives whose number we can't resolve. The "already engaged"
        // guard blocks a re-dispatch here (giant rat still current), so the switch
        // is the heartbeat's job — and its count must include the unknown mob.
        h.Feed("Also here: giant rat, dark stalker.");
        h.Tick();

        // Count reached MinEnemies → room spell, cast BARE (never "blast <mob>").
        Assert.Equal("blast", h.LastSent);
    }

    // The fumble re-fire must not repeat an attack spell the game has engaged: a
    // cast of the announced spell is this engine's own attack, with its target or
    // bare (a room spell). Anything else isn't, and neither is the spell before its
    // own *Combat Engaged* has come.
    [Fact]
    public void AttackAlreadyEngaged_KnowsTheAnnouncedAttackSpell()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        Assert.Equal("nuke giant rat", h.LastSent);

        Assert.False(h.Combat.AttackAlreadyEngaged("nuke giant rat"));   // announced, not answered yet

        h.Feed("*Combat Engaged*");

        Assert.True(h.Combat.AttackAlreadyEngaged("nuke giant rat"));
        Assert.True(h.Combat.AttackAlreadyEngaged("NUKE giant rat"));
        Assert.True(h.Combat.AttackAlreadyEngaged("nuke"));
        Assert.False(h.Combat.AttackAlreadyEngaged("nuker giant rat"));  // another word
        Assert.False(h.Combat.AttackAlreadyEngaged("mihe"));
        Assert.False(h.Combat.AttackAlreadyEngaged(null));

        // The decision as a whole: the cast is not replayed, a heal is.
        int replays = 0;
        h.Combat.HandleFumble("nuke giant rat", () => replays++);
        Assert.Equal(0, replays);
        h.Combat.HandleFumble("mihe", () => replays++);
        Assert.Equal(1, replays);

        h.Feed("*Combat Off*");
        Assert.False(h.Combat.AttackAlreadyEngaged("nuke giant rat"));
    }

    // Report paradigm-20260923-091205: engaged one mob single-target, then a 2nd mob
    // ARRIVES mid-fight (count crosses MinEnemies=2). The "already engaged" guard used to
    // return without re-deciding, so the room kept getting single-target pecks until the
    // next damage tick. The arrival must announce the room-attack switch itself.
    [Fact]
    public void MidFightArrival_CrossesMinEnemies_SwitchesToRoomAttack()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "dark stalker");

        // One mob → single-target attack spell (spell mode).
        h.Feed("Also here: giant rat.");
        Assert.Equal("nuke giant rat", h.LastSent);

        // Second mob arrives → count 2 ≥ MinEnemies → switch to the bare room attack now,
        // without waiting for a damage tick.
        h.Arrive("dark stalker");
        Assert.Equal("blast", h.LastSent);
    }

    // Report paradigm-20260923-103938: a room-attack spell (hsto) is a persistent room
    // channel — once cast while engaged it hits every monster each round, survivors and
    // roamers alike, so it isn't recast (a re-send costs nothing but adds nothing).
    // After the primary target dies
    // and the room re-displays with survivors still ≥ MinEnemies, the round must re-anchor
    // WITHOUT re-issuing the room spell.
    [Fact]
    public void RoomChannel_SurvivesKill_DoesNotRecastWhileConditionsHold()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "dark stalker");
        h.AddMonster(3, "muckworm");

        // Three mobs → the room attack casts ONCE; the primary target is the first mob.
        h.Feed("Also here: giant rat, dark stalker, muckworm.");
        Assert.Equal("blast", h.LastSent);
        Assert.Single(h.AllSent, s => s == "blast");

        // The primary dies under the room spell (its exp line, no *Combat Off*); the room
        // re-displays with two survivors (still ≥ MinEnemies). The channel is still
        // hitting them — the re-pick must re-anchor, not re-cast.
        h.Feed("You gain 10 experience.");
        h.Feed("Also here: dark stalker, muckworm.");
        Assert.Single(h.AllSent, s => s == "blast");   // still exactly one blast — no recast

        // A mob roams in — the channel already covers it, so still no recast.
        h.Arrive("giant rat");
        h.Feed("Also here: dark stalker, muckworm, giant rat.");
        Assert.Single(h.AllSent, s => s == "blast");
    }

    // Report paradigm-20260924-123009: a between-round cast (a mid-fight buff) BREAKS the
    // running room spell — its *Combat Off* is the channel ending. After a kill had
    // dropped the target, the interrupt's resume found the channel still recorded,
    // re-anchored "without recast" onto a survivor nothing was hitting, and never
    // attacked. The resume must re-dispatch the room attack instead. (Driven straight
    // through ResumeEngage: in production the re-engage lands after a timer-driven
    // arrival settle the harness doesn't run.)
    [Fact]
    public void RoomChannel_EndedByInterrupt_ResumeReDispatchesTheRoomAttack()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "dark stalker");
        h.AddMonster(3, "muckworm");

        h.Feed("Also here: giant rat, dark stalker, muckworm.");
        Assert.Single(h.AllSent, s => s == "blast");

        // A kill dropped the target while the channel was on record; then the buff's
        // *Combat Off* interrupted and the engine resumes.
        h.Feed("You gain 10 experience.");
        Assert.Null(h.Combat.CurrentTarget);
        typeof(CombatManager)
            .GetMethod("ResumeEngage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(h.Combat, new object[] { h.Classifier.Current! });

        Assert.Equal(2, h.AllSent.Count(s => s == "blast"));   // re-cast — the channel was broken
    }

    // A room attack keeps its round queued after it kills everything; that round prints
    // "Your spell has no effect in this room!" and queues no other, with no *Combat Off*.
    // Monsters seen after it must draw a fresh cast, not a re-anchor onto the dead channel.
    [Fact]
    public void RoomChannel_EndedByNoEffectInRoom_RecastsForTheNextMonsters()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "dark stalker");
        h.AddMonster(3, "muckworm");

        h.Feed("Also here: giant rat, dark stalker, muckworm.");
        Assert.Single(h.AllSent, s => s == "blast");

        h.Feed("You gain 10 experience.");
        h.Feed("Your spell has no effect in this room!");
        h.Feed("Also here: dark stalker, muckworm.");

        Assert.Equal(2, h.AllSent.Count(s => s == "blast"));
    }

    // The channel ends the moment a cast condition fails. Kill down below MinEnemies and
    // the room attack is over — the round falls through to the normal single-target chain.
    [Fact]
    public void RoomChannel_EndsWhenCountFallsBelowMinEnemies_SwitchesToSingle()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "dark stalker");

        h.Feed("Also here: giant rat, dark stalker.");
        Assert.Equal("blast", h.LastSent);

        // Down to one survivor (< MinEnemies) → channel over → single-target attack.
        h.Feed("You gain 10 experience.");
        h.Feed("Also here: dark stalker.");
        Assert.Equal("nuke dark stalker", h.LastSent);
    }

    // ----- room attacks held for a player outside the party ------------

    // On a realm with PvP on, a room spell hits every player in the room who isn't in
    // our party. With one here, the room qualifies by count but the round goes to the
    // single-target spell.
    [Fact]
    public void RoomAttackHeld_RoomQualifies_CastsTheSingleTargetSpellInstead()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "dark stalker");
        h.RoomAttackHeldBy = "Bob";

        h.Feed("Also here: giant rat, dark stalker.");

        Assert.Equal("nuke giant rat", h.LastSent);
        Assert.DoesNotContain("blast", h.AllSent);
    }

    // A room spell slotted as the normal attack is a room attack all the same.
    [Fact]
    public void RoomAttackHeld_RoomScopedSpellInTheNormalSlot_FallsToTheWeapon()
    {
        using Harness h = new();
        h.SpellsByCode["esto"] = new KnownSpell(566, "esto", "eldritch storm", 1, 1, 1, 12,
            new SpellFormulaInput { EnergyCost = 1000 });
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "esto", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");
        h.RoomAttackHeldBy = "Bob";

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
        Assert.DoesNotContain("esto", h.AllSent);
    }

    // A room attack repeats by itself, so a player walking in on one has to be met with
    // `break`; the *Combat Off* that answers it re-dispatches the round single-target.
    [Fact]
    public void RoomAttackRunning_PlayerWalksIn_BreaksThenGoesSingleTarget()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "dark stalker");

        h.Feed("Also here: giant rat, dark stalker.");
        Assert.Equal("blast", h.LastSent);

        h.RoomAttackHeldBy = "Bob";
        h.ArrivePlayer("Bob");
        Assert.Equal("break", h.LastSent);

        // A second look at the room before the game answers doesn't break twice.
        h.Feed("Also here: giant rat, dark stalker, Bob.");
        Assert.Single(h.AllSent, sent => sent == "break");

        h.FeedOffClosingRound();
        Assert.Equal("nuke giant rat", h.LastSent);
        Assert.Single(h.AllSent, sent => sent == "blast");
    }

    // Nobody to spare: a room attack already running is left alone.
    [Fact]
    public void RoomAttackRunning_PartyMemberWalksIn_KeepsRunning()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "dark stalker");

        h.Feed("Also here: giant rat, dark stalker.");
        h.ArrivePlayer("Bob");

        Assert.Equal("blast", h.LastSent);
        Assert.DoesNotContain("break", h.AllSent);
    }

    // Simultaneous-arrival settle (report paradigm-20260811-063728 + a live report):
    // three monsters stride in on one wire flush, then the room re-displays. Engaging
    // the first arrival single-target used to strand the room below its multi-attack
    // threshold. The burst must HOLD until the room re-display drives one decision on
    // the whole group, so a qualifying room nukes on its first action.
    [Fact]
    public void SimultaneousArrivalBurst_RoomsOnRedisplay_NoSingleTargetFirst()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 3 };
        h.AddMonster(1, "giant rat");

        // Burst of arrivals — the first engage is held, nothing goes out yet.
        h.Arrive("giant rat");
        h.Arrive("giant rat");
        h.Arrive("giant rat");
        Assert.Equal(string.Empty, h.LastSent);

        // Authoritative room re-display of the whole group → rooms bare, first action.
        h.Feed("Also here: giant rat, giant rat, giant rat.");
        Assert.Equal("blast", h.LastSent);
        Assert.DoesNotContain("nuke giant rat", h.AllSent);
    }

    // The other side of the settle: a LONE spawn with no room re-display still engages,
    // just a beat later when the window elapses. Below the multi-attack threshold, so
    // it's the single-target cascade — the burst hold must not strand a solo arrival.
    [Fact]
    public void LoneArrival_EngagesSingleTarget_WhenSettleElapses()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 3 };
        h.AddMonster(1, "giant rat");

        h.Arrive("giant rat");
        Assert.Equal(string.Empty, h.LastSent);   // held during the window

        h.FireSettle();                            // window elapsed, no re-display
        Assert.Equal("nuke giant rat", h.LastSent);
    }

    // Report paradigm-20260927-095328: the loop sent `u`, and before the new room
    // displayed a rat "crept into the room from the above" — into the room we were
    // leaving. Engaging it sent `bs large giant rat` into the next room. While our
    // move is in flight the settle holds; the new room's display decides.
    [Fact]
    public void ArrivalSettle_OurMoveInFlight_HoldsForTheNewRoom()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        bool moving = true;
        h.Combat.SetMoveInFlightProbe(() => moving);

        h.Arrive("giant rat");
        h.FireSettle();

        Assert.Equal(string.Empty, h.LastSent);   // nothing swung at the room we're leaving

        moving = false;                            // landed: the new room shows nobody
        h.Classifier.NoteRoomChanged();
        h.Combat.NoteMoveRefused();                // a stray refusal after landing does nothing
        Assert.Equal(string.Empty, h.LastSent);
    }

    [Fact]
    public void ArrivalSettle_OurMoveRefused_EngagesTheRoomWeStayedIn()
    {
        // The move bounced, so we never left: the held arrival is in our room.
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        bool moving = true;
        h.Combat.SetMoveInFlightProbe(() => moving);

        h.Arrive("giant rat");
        h.FireSettle();
        Assert.Equal(string.Empty, h.LastSent);

        moving = false;
        h.Combat.NoteMoveRefused();

        Assert.Equal("a giant rat", h.LastSent);
    }

    // The arrival settle's defer on a kill, as a kill reaches the client now: an exp
    // line in the window, with no target of ours for it to drop, so no death is
    // stamped. The settle used to engage the accumulated roster and send `nebo` at
    // the just-killed muckworm; it waits for the room's re-display instead.
    [Fact]
    public void ArrivalSettle_ExpGainInsideWindow_DefersToResync_NoCorpseCast()
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nebo", MinEnemies = 1 };
        h.AddMonster(1, "muckworm");

        h.Arrive("muckworm");                       // arms the settle hold (no target yet)
        Assert.Equal(string.Empty, h.LastSent);

        h.Feed("You gain 10 experience.");          // a kill pays us inside the window
        int sentBeforeSettle = h.Sent.Count;

        h.FireSettle();

        Assert.DoesNotContain("nebo muckworm", h.AllSent);   // never corpse-cast
        Assert.Equal(sentBeforeSettle + 1, h.Sent.Count);    // exactly the CR resync went out
        Assert.Equal(string.Empty, h.LastSent);              // = the bare CR (trimmed to empty)
        Assert.Contains("exp line", h.Combat.LastArrivalSettleDecision);
    }

    // The same exp line where no re-display can be asked for (a dark room): the
    // settle has disarmed and nothing would engage the arrival until it swung. It
    // waits one more window instead, then engages.
    [Fact]
    public void ArrivalSettle_ExpGainInsideWindow_NoReDisplayPossible_EngagesAfterOneMoreWindow()
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nebo", MinEnemies = 1 };
        h.AddMonster(1, "muckworm");
        h.Combat.SetDarkRoomProbe(() => true);

        h.Arrive("muckworm");
        h.Feed("You gain 10 experience.");
        h.FireSettle();
        Assert.DoesNotContain("nebo muckworm", h.AllSent);

        h.FireSettle();

        Assert.Equal("nebo muckworm", h.LastSent);
    }

    // An exp line from before the hold armed is no kill of the window's: the settle
    // engages the arrival as usual.
    [Fact]
    public void ArrivalSettle_ExpGainBeforeWindow_EngagesTheArrival()
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nebo", MinEnemies = 1 };
        h.AddMonster(1, "muckworm");

        h.Feed("You gain 10 experience.");
        h.Arrive("muckworm");
        h.FireSettle();

        Assert.Equal("nebo muckworm", h.LastSent);
    }

    // ----- a swing while our move is in flight (report paradigm-20261010-231540) -----

    // A loop that only attacks in lairs walks through a room with one brute zombie in
    // it and sends its step into the lair next door. From then combat reads as on (the
    // lair is what it is judged against) though the character still stands outside.
    // deferPost: the game's lines come in between a dispatch being scheduled and run.
    private static Harness AtTheLairDoorstepWithTheStepSent(out Action land)
    {
        Harness h = new(deferPost: true);
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "aslt" };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "isto", MinEnemies = 2, MaxCastsPerRoom = 1 };
        h.AddMonster(2739, "brute zombie");
        bool moving = false;
        DateTimeOffset stepSentAt = DateTimeOffset.UtcNow;
        h.Combat.SetMoveInFlightProbe(() => moving, () => stepSentAt);
        land = () => moving = false;

        h.Feed("*Combat Off*");                     // the last lair's fight ended
        h.AutoCombatEnabled = false;                // not a lair: combat suppressed here
        h.Feed("Also here: brute zombie.");
        h.DrainPosted();
        Assert.Empty(h.Sent);

        h.Combat.NotePreMove();
        h.Combat.NoteMoveSent();
        moving = true;
        h.AutoCombatEnabled = true;
        return h;
    }

    // The capture, line for line: the zombie swings as we leave, three more shamble in
    // before the lair displays, and the first one follows us in. Nothing is sent from
    // the room being left, and the lair opens as any lair does: debuff, then room attack.
    // Whichever room the three shambled into, the lair's own display decides.
    [Fact]
    public void SwingWhileOurMoveIsInFlight_IsNotAnswered_AndTheLairOpensWithItsDebuff()
    {
        using Harness h = AtTheLairDoorstepWithTheStepSent(out Action land);

        h.Feed("The brute zombie swings at you, but you dodge out of the way!");
        h.Feed("The brute zombie swings at you with its arm!");
        h.Feed("The brute zombie swings at you with its arm!");
        h.Feed("The brute zombie swings at you with its arm!");
        h.DrainPosted();
        Assert.Empty(h.Sent);
        Assert.Null(h.Combat.CurrentTarget);

        h.Arrive("brute zombie");
        h.Arrive("brute zombie");
        h.Arrive("brute zombie");
        h.Feed("Also here: brute zombie, brute zombie, brute zombie.");
        land();
        h.Arrive("brute zombie");
        h.DrainPosted();

        Assert.Equal(new[] { "isto", "hsto" }, h.AllSent);
    }

    // The same swing with the step refused: we never left, so it is answered.
    [Fact]
    public void SwingWhileOurMoveIsInFlight_MoveRefused_IsAnsweredInTheRoomWeStayedIn()
    {
        using Harness h = AtTheLairDoorstepWithTheStepSent(out Action land);

        h.Feed("The brute zombie swings at you with its arm!");
        h.DrainPosted();
        Assert.Empty(h.Sent);

        land();
        h.Combat.NoteMoveRefused();
        h.DrainPosted();

        Assert.Equal(new[] { "aslt brute zombie" }, h.AllSent);
    }

    // With the last fight's *Combat Off* still standing, any hit line wakes the
    // re-attack: a party member's hit on the monster, or the monster's named attack
    // landing on us, which is not worded as a swing.
    [Theory]
    [InlineData("Bob hits the brute zombie for 30 damage!")]
    [InlineData("The brute zombie crushes you with a huge fist for 3 damage!")]
    public void HitLineWhileOurMoveIsInFlight_OpensNoFightInTheRoomBeingLeft(string line)
    {
        using Harness h = AtTheLairDoorstepWithTheStepSent(out _);

        h.Feed(line);
        h.DrainPosted();

        Assert.Empty(h.Sent);
        Assert.Null(h.Combat.CurrentTarget);
    }

    // A Friend that hit us as we left stays behind. The room we land in holds another of
    // its name: that one never attacked, and is left alone until it does.
    [Fact]
    public void SwingWhileOurMoveIsInFlight_AttackerStaysBehind_ItsNamesakeNextDoorIsLeftAlone()
    {
        using Harness h = new();
        h.AddMonster(1, "big gravedigger");
        h.SetOverlay(1, relationship: MonsterRelationship.Friend);
        bool moving = false;
        h.Combat.SetMoveInFlightProbe(() => moving);

        h.Feed("Also here: big gravedigger.");
        moving = true;
        h.Feed("The big gravedigger hits you for 12 damage!");
        Assert.DoesNotContain(h.AllSent, s => s.Contains("gravedigger"));

        h.Feed("Also here: big gravedigger.");      // the room we land in
        moving = false;
        Assert.DoesNotContain(h.AllSent, s => s.Contains("gravedigger"));
        Assert.Null(h.Combat.CurrentTarget);
        Assert.False(h.Combat.IsUserEngagedInstance("big gravedigger"));

        h.Feed("The big gravedigger hits you for 12 damage!");
        Assert.Equal("a big gravedigger", h.LastSent);
    }

    // The room we land in is empty: nothing is left to attack and no target stands.
    [Fact]
    public void SwingWhileOurMoveIsInFlight_AttackerStaysBehind_EmptyRoomLeavesNoTarget()
    {
        using Harness h = AtTheLairDoorstepWithTheStepSent(out Action land);

        h.Feed("The brute zombie swings at you with its arm!");
        h.Classifier.NoteRoomChanged();
        land();
        h.DrainPosted();

        Assert.Empty(h.Sent);
        Assert.Null(h.Combat.CurrentTarget);
    }

    // The step is never answered: no landing, no refusal the client reads. The hold
    // lasts three seconds from the step, then the attacker is answered where we stand,
    // on the hold's own timer (a quiet attacker) or on the next line that comes.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SwingWhileOurMoveIsInFlight_StepNeverLands_IsAnsweredAfterTheBound(bool byTimer)
    {
        using Harness h = AtTheLairDoorstepWithTheStepSent(out _);

        h.Feed("The brute zombie swings at you with its arm!");
        h.AdvanceClock(TimeSpan.FromSeconds(2));
        h.Feed("The brute zombie swings at you with its arm!");
        h.FireSettle();                              // a timer firing early changes nothing
        h.DrainPosted();
        Assert.Empty(h.Sent);

        h.AdvanceClock(TimeSpan.FromSeconds(2));
        if (byTimer) h.FireSettle();
        else h.Feed("The brute zombie swings at you with its arm!");
        h.DrainPosted();

        Assert.Equal(new[] { "aslt brute zombie" }, h.AllSent);
    }

    // Three callers hand the roster we already had back to the engine with no line from
    // the game behind them: a switch re-running the room, a rest that wants the room
    // cleared, a ShadowRest ending. With the step in flight that roster is the room
    // being left, and the fight waits as it does for a swing; a refusal or the bound
    // then opens it where we stand.
    [Theory]
    [InlineData("reemit", true)]
    [InlineData("reemit", false)]
    [InlineData("rest-clear", true)]
    [InlineData("rest-clear", false)]
    [InlineData("shadowrest", true)]
    [InlineData("shadowrest", false)]
    public void OldRosterHandedBackWhileOurMoveIsInFlight_OpensNoFightUntilTheHoldEnds(string caller, bool refused)
    {
        using Harness h = AtTheLairDoorstepWithTheStepSent(out Action land);
        h.Combat.SetRestClearGate(() => caller == "rest-clear");

        switch (caller)
        {
            case "reemit": h.Classifier.ReemitCurrent(); break;
            case "rest-clear": h.Combat.RequestRestClearEngage(); break;
            default: h.Combat.ResumeAfterShadowRest(); break;
        }
        h.DrainPosted();
        Assert.Empty(h.Sent);
        Assert.Null(h.Combat.CurrentTarget);

        if (refused)
        {
            land();
            h.Combat.NoteMoveRefused();
        }
        else
        {
            h.AdvanceClock(TimeSpan.FromSeconds(4));
            h.FireSettle();
        }
        h.DrainPosted();

        Assert.Equal(new[] { "aslt brute zombie" }, h.AllSent);
    }

    // The lair's own display is a room read anew, and is engaged with the step still
    // unconfirmed: the tracker confirms a move only on the exits line that follows.
    [Fact]
    public void RoomReadAnewWhileOurMoveIsInFlight_IsEngagedAtOnce()
    {
        using Harness h = AtTheLairDoorstepWithTheStepSent(out _);
        h.Classifier.ReemitCurrent();

        h.Feed("Also here: brute zombie, brute zombie.");
        h.DrainPosted();

        Assert.Equal(new[] { "isto", "hsto" }, h.AllSent);
    }

    // A Friend is engaged only on the mark its attack earns, so the bound has to hand
    // that mark over, not just stop holding.
    [Fact]
    public void SwingWhileOurMoveIsInFlight_FriendAttacker_StepNeverLands_IsFoughtBackAfterTheBound()
    {
        using Harness h = new();
        h.AddMonster(1, "big gravedigger");
        h.SetOverlay(1, relationship: MonsterRelationship.Friend);
        DateTimeOffset stepSentAt = DateTimeOffset.UtcNow;
        h.Combat.SetMoveInFlightProbe(() => true, () => stepSentAt);

        h.Feed("Also here: big gravedigger.");
        h.Feed("The big gravedigger hits you for 12 damage!");
        h.AdvanceClock(TimeSpan.FromSeconds(2));
        h.FireSettle();
        Assert.DoesNotContain(h.AllSent, s => s.Contains("gravedigger"));

        h.AdvanceClock(TimeSpan.FromSeconds(2));
        h.FireSettle();

        Assert.Equal("a big gravedigger", h.LastSent);
    }

    // The arrival's hold is under the same bound: a monster that came in as a step
    // went out, the step never answered, is engaged once the bound has passed.
    [Fact]
    public void ArrivalSettle_OurMoveInFlight_StepNeverLands_EngagesAfterTheBound()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        DateTimeOffset stepSentAt = DateTimeOffset.UtcNow;
        h.Combat.SetMoveInFlightProbe(() => true, () => stepSentAt);

        h.Arrive("giant rat");
        h.FireSettle();                              // the settle elapses: held for the move
        Assert.Equal(string.Empty, h.LastSent);

        h.AdvanceClock(TimeSpan.FromSeconds(4));
        h.FireSettle();                              // the hold's timer

        Assert.Equal("a giant rat", h.LastSent);
    }

    // A Friend that hit us with the step refused is fought back, as it is with no move.
    [Fact]
    public void SwingWhileOurMoveIsInFlight_FriendAttacker_MoveRefused_IsFoughtBack()
    {
        using Harness h = new();
        h.AddMonster(1, "big gravedigger");
        h.SetOverlay(1, relationship: MonsterRelationship.Friend);
        bool moving = true;
        h.Combat.SetMoveInFlightProbe(() => moving);

        h.Feed("Also here: big gravedigger.");
        h.Feed("The big gravedigger hits you for 12 damage!");
        Assert.DoesNotContain(h.AllSent, s => s.Contains("gravedigger"));

        moving = false;
        h.Combat.NoteMoveRefused();

        Assert.Equal("a big gravedigger", h.LastSent);
    }

    // Post-kill re-engage race (reports 081053, 081654, 103708, 135433). Each realm
    // gives monsters custom death messages we can't map to the flavored target, so
    // the specific-death matcher misses and combat used to re-cast at the corpse on
    // the kill's *Combat Off* ("You don't see X here!"), then idle a round. The exp
    // gain that precedes a kill's Off — with no between-round cast to explain the
    // Off — marks it as our kill, so spell mode drops and no corpse re-cast goes out.
    [Fact]
    public void KillInferredFromExp_DropsSpellTarget_NoCorpseRecast()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("mmis giant rat", h.LastSent);   // engaged, spell mode
        int sentAfterEngage = h.Sent.Count;

        // Wire order of a kill: (custom death line we can't match) → exp → Off.
        h.Feed("You gain 100 experience.");
        h.Feed("*Combat Off*");

        // The corpse is not re-cast at — spell mode was dropped on the inferred kill.
        Assert.Equal(sentAfterEngage, h.Sent.Count);
    }

    // Switch-dispatch corpse-cast on the killing round (reports paradigm-20260815-201731
    // / -202241 Mage lbol→mmis at MA 99-108; -120544 / -120934 Paladin harm→weapon). The
    // combat tick is DAMAGE-LINE driven, so the heartbeat's cascade switch is decided on
    // the killing blow's damage line — ahead of the exp / *Combat Off* that drop the
    // target, which often arrive in a LATER packet. Dispatching the switch synchronously
    // (or via a bare UI-post that can't bridge the packet gap) fired the alternate spell
    // (or the weapon) AT the corpse ("You don't see X here!"). The switch is now delayed a
    // short real-time window; the kill's exp lands + nulls the target in that window, and
    // the re-validated dispatch then skips — no `mmis` at the dead mob. The exp fed here
    // between schedule and drain models that later-packet kill.
    [Fact]
    public void CapSwitchOnKillingRound_ExpDropsTarget_DeferredSwitchSkips_NoCorpseCast()
    {
        using Harness h = new(deferPost: true);
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "lbol", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");            // engage → lbol announced (spell mode)
        Assert.Equal("lbol giant rat", h.LastSent);

        // Round 1 tallies lbol to its cap and DECIDES the cap-switch to mmis — but the
        // dispatch is deferred (queued), not run inline.
        h.Tick();
        Assert.Single(h.Posted);
        Assert.DoesNotContain("mmis giant rat", h.AllSent);

        // The killing blow's exp line lands in the same burst and drops the target.
        h.Feed("You gain 100 experience.");

        // The deferred switch now runs, re-validates against the now-current state,
        // sees the target gone, and skips — the alternate never corpse-casts.
        h.DrainPosted();
        Assert.DoesNotContain("mmis giant rat", h.AllSent);
    }

    // The other side of the delay: with the mob still alive when the window elapses (no
    // kill this burst) the delayed switch re-validates fine and dispatches the alternate —
    // the cascade still advances, a hair later than the old synchronous path but well
    // within the 5 s round, so the cap-preempt (report paradigm-20260814-061340) is
    // preserved. (Screenshot case: a mob that SURVIVES lbol correctly takes mmis.)
    [Fact]
    public void CapSwitch_TargetAlive_DeferredSwitchDispatchesAlternate()
    {
        using Harness h = new(deferPost: true);
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "lbol", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("lbol giant rat", h.LastSent);

        h.Tick();                                   // cap-switch to mmis, deferred
        h.DrainPosted();                            // no kill → runs → dispatches the alternate

        Assert.Equal("mmis giant rat", h.LastSent);
    }

    // Report paradigm-20260905-205200: the AlternateSpellPhysical/AlternatePhysicalSpell/
    // CustomRoundCycle heartbeat branches force a fresh dispatch every round (they can't
    // lean on the server's auto-repeat) and — like the cap-switch above — decide on the
    // round's own DAMAGE line, ahead of that round's "dies." / "You gain N experience."
    // when the swing that just landed was the killing blow. Dispatching synchronously
    // re-announced the phase's action at the corpse every time. Now deferred the same
    // short window and re-validated before actually sending.
    [Fact]
    public void AlternateSpellPhysical_KillLandsInDeferWindow_NoCorpseCast()
    {
        using Harness h = new(deferPost: true);
        h.Settings.ActionOrder = CombatActionOrder.AlternateSpellPhysical;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("harm giant rat", h.LastSent);   // round 0 = spell phase

        // Round 1 flips to physical — the continuation is deferred, not dispatched inline.
        h.Tick();
        Assert.Single(h.Posted);
        Assert.DoesNotContain("a giant rat", h.AllSent);

        // The prior round's own damage turns out to have killed the rat — its exp
        // lands in the same burst / an adjacent packet.
        h.Feed("You gain 100 experience.");

        // Re-validated: target is gone. No corpse-swing.
        h.DrainPosted();
        Assert.DoesNotContain("a giant rat", h.AllSent);
    }

    // The other side of the delay: the mob is still alive when the window elapses, so
    // the deferred continuation re-validates fine and flips phase as normal — a hair
    // later than the old synchronous path, well within the round.
    [Fact]
    public void AlternateSpellPhysical_TargetAlive_DeferredContinuationDispatches()
    {
        using Harness h = new(deferPost: true);
        h.Settings.ActionOrder = CombatActionOrder.AlternateSpellPhysical;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("harm giant rat", h.LastSent);

        h.Tick();
        Assert.Single(h.Posted);

        h.DrainPosted();   // no kill this burst → re-validates fine → flips to physical
        Assert.Equal("a giant rat", h.LastSent);
    }

    // Reports paradigm-20260819-121003 / -142147: after lbol caps and the switch to
    // mmis is DEFERRED, a second tick during the delay window recomputed the switch
    // (the announce is still the stale lbol, so sameSpell=false takes the ungated
    // decision-changed branch) and scheduled it AGAIN — the alternate fired twice.
    // The pending-switch latch must collapse the re-arm so it dispatches exactly once.
    [Fact]
    public void CapSwitch_RetickDuringDeferWindow_DispatchesAlternateOnce()
    {
        using Harness h = new(deferPost: true);
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "lbol", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("lbol giant rat", h.LastSent);

        h.Tick();                       // cap-switch to mmis, deferred (queued)
        Assert.Single(h.Posted);        // one dispatch armed
        h.TickSameRound();              // re-tick while the switch is still pending
        Assert.Single(h.Posted);        // still ONE — the latch blocked the double-schedule

        h.DrainPosted();
        Assert.Equal("mmis giant rat", h.LastSent);
        Assert.Equal(1, h.AllSent.Count(s => s == "mmis giant rat"));   // exactly one mmis
    }

    // Report paradigm-20260819-120938: a solo mob's counter-swing tripped a combat
    // tick WITHIN the engage round (before lbol's round resolved); the MinValue solo
    // anchor let that premature tick tally lbol and — MaxCasts=1 — cap it, so mmis
    // went out before lbol ever fired. The tally now keys off the real round count,
    // so a same-round tick counts nothing and the cap waits for a genuine boundary.
    [Fact]
    public void SoloEngage_PrematureSameRoundTick_DoesNotCapBeforeARoundCloses()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "lbol", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("lbol giant rat", h.LastSent);

        h.TickSameRound();                                   // premature — no round closed yet
        Assert.Equal("lbol giant rat", h.LastSent);          // still lbol; not capped
        Assert.DoesNotContain("mmis giant rat", h.AllSent);  // mmis did NOT fire early

        h.Tick();                                            // real round boundary → tally → cap → mmis
        Assert.Equal("mmis giant rat", h.LastSent);
    }

    // Report paradigm-20260815-202319 ("not re-engaging combat after buffing mid-combat"):
    // a between-round self-buff (armr) fires, then the round's nuke (lbol, MaxCasts=1)
    // KILLS one mob in a multi-mob room. The death→re-observe re-picks the live survivor
    // as BOTH _currentTarget and _castingSpellTarget. Fresh-engage dispatch now bypasses
    // CastCoordinator's recast-interval burst guard (report paradigm-20260916-033047 —
    // GAME_MECHANICS.md's confirmed rule that a between-round buff and a real attack
    // spell are independent slots server-side), so the survivor's re-cast fires
    // immediately at the re-observe instead of waiting on the kill's *Combat Off* to
    // trigger a separate resume path.
    [Fact]
    public void BetweenRoundBuff_KillLeavesSurvivor_ResumesSurvivorImmediately()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "lbol", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "rotworm");
        h.AddMonster(2, "thin leprous outcast");

        h.Feed("Also here: rotworm, thin leprous outcast.");
        Assert.Equal("lbol rotworm", h.LastSent);            // engaged the first mob

        // A between-round survival buff interrupts the round (it drops *Combat Off*).
        h.Cast.NotifyExternalCastSent();
        h.Combat.NoteBetweenRoundCast();
        int sentBeforeReObserve = h.Sent.Count;

        // lbol kills rotworm this round; its exp drops the target. The room re-display
        // then hands us the survivor, which the re-observe re-picks and immediately
        // re-engages — the buff and the attack spell no longer compete for the round.
        h.Feed("You gain 100 experience.");
        h.Feed("Also here: thin leprous outcast.");

        Assert.Equal(sentBeforeReObserve + 1, h.Sent.Count);
        Assert.Equal("lbol thin leprous outcast", h.LastSent);
    }

    // Report paradigm-20260923-210406: two party heals a beat apart (two members hurt in
    // adjacent rounds) each drop *Combat Off* on a weapon build. The first heal's resume
    // re-attacks; the SECOND used to be paced out by ResumePacing (2.5s wall-clock),
    // leaving combat off for a full round — a lost round + the mob's exp — until the next
    // cast fell past the window. Each distinct between-round cast must resume the weapon.
    [Fact]
    public void WeaponResume_SecondBetweenRoundCastWithinPacing_StillReAttacks()
    {
        using Harness h = new();   // no attack spells → weapon mode
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);
        int afterEngage = h.Sent.Count;

        // First party heal → *Combat Off* → weapon resume re-attacks.
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        Assert.True(h.Sent.Count > afterEngage, "first heal should re-attack");
        Assert.Equal("a giant rat", h.LastSent);
        int afterFirstResume = h.Sent.Count;

        // Second party heal a beat later — well within ResumePacing (2.5s), distinct
        // cast stamp — must ALSO re-attack, not sit paced out for the round.
        System.Threading.Thread.Sleep(2);   // distinct _betweenRoundCastAt, still << 2.5s
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        Assert.True(h.Sent.Count > afterFirstResume, "second heal within the pacing window must still re-attack");
        Assert.Equal("a giant rat", h.LastSent);
    }

    // Report paradigm-20260820-063541 ("LBOL cast twice"): a between-round survival
    // cast interrupts an attack spell (lbol, MaxCasts=1) mid-round and drops *Combat
    // Off*. The heartbeat can't tally the interrupted round (OnCombatTick bails while
    // _combatOff), so the spell-resume used to re-announce the STILL-current lbol
    // uncapped — a second lbol before the cascade advanced. RoundDamageTracker now
    // tie-breaks ahead of CombatManager on that Off and closes the round first; the
    // resume tallies lbol's cast toward its cap before re-deciding, so it cascades to
    // the alternate (mmis) instead of firing lbol a second time.
    [Fact]
    public void BetweenRoundInterrupt_TalliesAttackSpellRound_ResumeRespectsMaxCasts()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "lbol", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("lbol giant rat", h.LastSent);          // engage → lbol (spell mode)

        // A between-round survival cast interrupts lbol's round and drops *Combat Off*.
        h.Cast.NotifyExternalCastSent();
        h.Combat.NoteBetweenRoundCast();
        h.FeedOffClosingRound();                             // round closes (tieBreak) → resume tallies lbol

        // lbol hit MaxCasts=1 on the interrupted round → the resume cascades to the
        // alternate, NOT a second lbol.
        Assert.Equal("mmis giant rat", h.LastSent);
        Assert.Equal(1, h.AllSent.Count(s => s == "lbol giant rat"));   // exactly one lbol
    }

    // Report paradigm-20260916-083245 ("firing 2 multi-attacks"): a between-round cast
    // stamps the resume window, THEN the round's attack fires (the pre-attack-debuff-
    // then-attack sequence — debuff before the attack, same round). The attack's OWN
    // *Combat Off* lands in that window and used to re-announce the attack a SECOND time.
    // The spell resume now suppresses itself when the attack already fired this round
    // (last attack sent AFTER the between-round cast), matching the weapon path.
    [Fact]
    public void BetweenRoundThenAttack_CombatOffDoesNotDoubleFire()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Combat.NoteBetweenRoundCast();                 // between-round cast opens the resume window
        h.Feed("Also here: giant rat.");                 // engage → blast fires AFTER the between-round cast
        Assert.Equal("blast", h.LastSent);

        h.Feed("*Combat Off*");                          // the attack's own Off, inside the resume window
        Assert.Equal(1, h.AllSent.Count(s => s == "blast"));   // NOT re-announced a second time
    }

    // MaxCasts must count real rounds, not damage-line ticks. A multi-hit attack spell
    // (each cast lands several damage lines) plus the mob's counter-swing trips the tick
    // 2-3× per ~5s round; without the tally gate a MaxCasts=2 spell hit its cap in a
    // single round and swapped a round early (report paradigm-20260815-130957: "hamm set
    // to 2, swapped after the first cast"). The engage spell must cast for two full rounds
    // before the cascade advances, regardless of the extra intra-round ticks.
    [Fact]
    public void MaxCasts_ExtraTicksWithinRound_CountRoundsNotTicks()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "hamm", MinEnemies = 0, MaxCastsPerRoom = 2 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("hamm giant rat", h.LastSent);   // engage → hamm (cast round 1 pending)

        // Round 1: the real round-boundary tick tallies once; the mob's counter-swing
        // trips a second tick the same round, which the gate must reject. Cap (2) is
        // NOT reached after one round — still hamm.
        h.Tick();
        h.TickSameRound();
        Assert.Equal("hamm giant rat", h.LastSent);

        // Round 2: the second real round tallies the 2nd cast → cap reached → swap to
        // harm. (The extra same-round tick again does nothing.)
        h.Tick();
        h.TickSameRound();
        Assert.Equal("harm giant rat", h.LastSent);
    }

    // Report paradigm-20260815-202241 ("LBOL → MMIS without firing LBOL"): in a MULTI-mob
    // room the OTHER mobs' swing lines trip the damage-driven combat tick within ~100ms of
    // the engage. The tally clock reset to MinValue let that premature tick count the attack
    // spell as a fired round, so a MaxCasts-1 nuke cap-switched to the alternate the SAME
    // round and the normal spell never went out (engageable=2, sinceAttack≈79ms). The tally
    // clock is now anchored at the engage moment, so AttackTallyMinGap rejects the premature
    // tick and the normal spell holds until a genuine round elapses. (Single-mob rooms were
    // never affected — their first tick is a real round.)
    [Fact]
    public void MaxCasts1_MultiMobEngage_PrematureTickDoesNotSwapBeforeFirstCast()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "lbol", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "rotworm");
        h.AddMonster(2, "thin leprous outcast");

        h.Feed("Also here: rotworm, thin leprous outcast.");
        Assert.Equal("lbol rotworm", h.LastSent);          // engage → lbol (round 0, not yet fired)

        // A second mob's swing trips a combat tick a beat after the engage — far short of a
        // real round. The gate must reject it: lbol hasn't fired, so no tally, no swap.
        h.TickSameRound();
        Assert.Equal("lbol rotworm", h.LastSent);          // still lbol — no premature mmis

        // The first genuine round now tallies lbol → MaxCasts=1 reached → swap to the alternate.
        h.Tick();
        Assert.Equal("mmis rotworm", h.LastSent);
    }

    // Report paradigm-20260818-055820: the engine cast lbol (MaxCasts=1) and the server
    // auto-repeated it, but the cap-switch to mmis fired one round late (cap-switch logged
    // at sinceAttack≈9333ms, engageable=1), so lbol fired twice. Cause: the tally clock was
    // anchored at engage to reject a MULTI-mob premature tick, but a SINGLE-mob fight's
    // genuine round-1 tick can land under AttackTallyMinGap (~3s) and got rejected too,
    // slipping the first tally to round 2. The anchor is now multi-mob only.
    [Fact]
    public void MaxCasts1_SingleMobEngage_FastFirstRound_TalliesRound1_NoLateSwap()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "lbol", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "small animated tree");

        h.Feed("Also here: small animated tree.");
        Assert.Equal("lbol small animated tree", h.LastSent);   // engage → lbol (round 0)

        // Round 1 lands fast — under AttackTallyMinGap after the engage. A solo fight has
        // no premature tick to guard against, so it must still tally round 1, reach
        // MaxCasts=1, and switch to mmis so lbol doesn't auto-repeat a second round.
        h.TickFast();
        Assert.Equal("mmis small animated tree", h.LastSent);
    }

    // Report paradigm-20260822-003106 (second instance): a spell that fires more than
    // one damage line per cast (a "You cast X at Y for N damage!" line per projectile)
    // must count as ONE cast toward MaxCasts, not one per line — and a genuinely later,
    // separate cast must still count as a new one, even with zero round-closing ticks
    // in between (RoundDamageTracker's own round-close can bundle real casts together
    // for a fast caster before it ever fires — a live fight measured as a single ~10s
    // "round" contained two full disr casts, silently under-counting MaxCasts). MaxCasts
    // 2 makes both halves of that guarantee observable: if the two projectile lines were
    // miscounted as two casts, the cap would exhaust (and switch) after the FIRST real
    // cast; if a later, separate cast were missed, the cap would never exhaust at all.
    // Report paradigm-20261003-194358: each "You negate <monster>'s cast of …!" was
    // tallied as a cast of our own attack spell, spending its cast cap on rounds in
    // which we never cast.
    [Fact]
    public void NegatedMonsterCast_IsNotACastOfOurs()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "disr", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "turn", MinEnemies = 0 };
        h.AddMonster(1, "fierce wraith");
        h.Combat.ReadRoundCount = () => h.Combat.ConfirmedAttackCastCount;

        h.Feed("Also here: fierce wraith.");
        h.Feed("You negate fierce wraith's cast of necromantic beam!");
        h.Cast.OnCombatTick();
        h.Combat.OnCombatTick();

        Assert.Equal(0, h.Combat.ConfirmedAttackCastCount);
        Assert.Equal("disr fierce wraith", h.LastSent);   // the one allowed cast is still unspent
    }

    // A party buff resisted by a member whose name holds the monster's ("Batu" while
    // fighting a bat) is no cast of the attack spell.
    [Theory]
    [InlineData("You attempt to cast bless at Batu, but the spell is resisted.")]
    [InlineData("You attempt to cast bless at Batu, but it's negated!")]
    public void BuffResistedByAMemberNamedLikeTheMonster_IsNotACastOfOurs(string line)
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "disr", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "turn", MinEnemies = 0 };
        h.AddMonster(1, "bat");
        h.Combat.ReadRoundCount = () => h.Combat.ConfirmedAttackCastCount;

        h.Feed("Also here: bat.");
        Assert.Equal("disr bat", h.LastSent);
        h.Feed(line);
        h.Cast.OnCombatTick();
        h.Combat.OnCombatTick();

        Assert.Equal(0, h.Combat.ConfirmedAttackCastCount);
        Assert.Equal("disr bat", h.LastSent);
    }

    // Our OWN attack spell negated or resisted by its target still spends a cast of
    // its cap (user, 2026-10-17: "Count it", "Count resists too").
    [Theory]
    [InlineData("You attempt to cast disrupt at fierce wraith, but it's negated!")]
    [InlineData("You attempt to cast disrupt at fierce wraith, but the spell is resisted.")]
    public void NegatedOrResistedAttackSpellOfOurs_CountsTowardItsCap(string line)
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "disr", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "turn", MinEnemies = 0 };
        h.AddMonster(1, "fierce wraith");
        h.Combat.ReadRoundCount = () => h.Combat.ConfirmedAttackCastCount;

        h.Feed("Also here: fierce wraith.");
        Assert.Equal("disr fierce wraith", h.LastSent);
        h.Feed(line);
        h.Cast.OnCombatTick();
        h.Combat.OnCombatTick();

        Assert.Equal(1, h.Combat.ConfirmedAttackCastCount);
        Assert.Equal("turn fierce wraith", h.LastSent);   // the cap of one is spent
    }

    [Fact]
    public void MaxCasts2_ConfirmedCastCount_GroupsProjectiles_CountsSeparateCasts_NoRoundCloseNeeded()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "disr", MinEnemies = 0, MaxCastsPerRoom = 2 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "turn", MinEnemies = 0 };
        h.AddMonster(1, "fierce wraith");
        h.Combat.ReadRoundCount = () => h.Combat.ConfirmedAttackCastCount;   // production wiring

        h.Feed("Also here: fierce wraith.");
        Assert.Equal("disr fierce wraith", h.LastSent);

        // The first real cast's own two projectiles, sub-second apart — grouped as ONE.
        h.Feed("You cast disrupt at fierce wraith for 75 damage!");
        h.AdvanceClock(TimeSpan.FromMilliseconds(300));
        h.Feed("You cast disrupt at fierce wraith for 88 damage!");
        h.Cast.OnCombatTick();
        h.Combat.OnCombatTick();
        Assert.Equal(1, h.Combat.ConfirmedAttackCastCount);
        Assert.Equal("disr fierce wraith", h.LastSent);   // 1 of 2 allowed casts spent — no switch yet

        // A genuinely separate real cast, well past the grouping window — but with no
        // round-closing Tick() at all in between. Must still count as cast #2 and cap-switch.
        h.AdvanceClock(TimeSpan.FromSeconds(2));
        h.Feed("You cast disrupt at fierce wraith for 77 damage!");
        h.Cast.OnCombatTick();
        h.Combat.OnCombatTick();
        Assert.Equal(2, h.Combat.ConfirmedAttackCastCount);
        Assert.Equal("turn fierce wraith", h.LastSent);
    }

    // Report paradigm-20260822-063043: one disrupt CAST emits exactly TWO
    // projectile lines. A mob hit/miss can open that round's server burst, causing
    // TickEngine to fire CombatTickElapsed before either projectile arrives and then
    // debounce both projectiles. The grouped confirmation must therefore tally the
    // one cast and arm the disr→turn switch directly, with no post-projectile combat
    // heartbeat; otherwise the server commits a second disrupt before the next tick.
    [Fact]
    public void MaxCasts1_TwoProjectileCast_SwitchesAfterOneCast_WithoutLaterHeartbeat()
    {
        using Harness h = new(deferPost: true);
        h.Settings.NormalAttackSpell = new CombatSpellSlot
        {
            SpellName = "disr",
            MinEnemies = 0,
            MaxCastsPerRoom = 1,
        };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot
        {
            SpellName = "turn",
            MinEnemies = 0,
        };
        h.AddMonster(1, "big wraith");
        h.Combat.ReadRoundCount = () => h.Combat.ConfirmedAttackCastCount;

        h.Feed("Also here: big wraith.");
        Assert.Equal("disr big wraith", h.LastSent);

        // Models the mob's result line firing the damage-driven heartbeat first.
        // No cast has confirmed yet, so it must not spend disr's cap.
        h.Cast.OnCombatTick();
        h.Combat.OnCombatTick();
        Assert.Equal("disr big wraith", h.LastSent);

        // These are two projectiles from ONE disrupt cast, not two casts.
        h.Feed("You cast disrupt at big wraith for 61 damage!");
        h.AdvanceClock(TimeSpan.FromMilliseconds(300));
        h.Feed("You cast disrupt at big wraith for 77 damage!");

        Assert.Equal(1, h.Combat.ConfirmedAttackCastCount);
        Assert.Single(h.Posted);                 // one corpse-safe switch is armed
        Assert.DoesNotContain("turn big wraith", h.AllSent);

        // No Combat.OnCombatTick call after either projectile. The confirmation
        // itself owns the cap transition, and its short safety delay now expires.
        h.DrainPosted();

        Assert.Equal("turn big wraith", h.LastSent);
        Assert.Equal(1, h.AllSent.Count(s => s == "turn big wraith"));
    }

    // The confirmation-driven path above must retain the delayed switch's original
    // purpose: if that one disrupt cast kills, the exp/death packet gets a chance to
    // clear the target and the queued turn must not be sent at its corpse.
    [Fact]
    public void MaxCasts1_TwoProjectileKillingCast_DeferredSwitchStillSkipsCorpse()
    {
        using Harness h = new(deferPost: true);
        h.Settings.NormalAttackSpell = new CombatSpellSlot
        {
            SpellName = "disr",
            MinEnemies = 0,
            MaxCastsPerRoom = 1,
        };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot
        {
            SpellName = "turn",
            MinEnemies = 0,
        };
        h.AddMonster(1, "spectre");
        h.Combat.ReadRoundCount = () => h.Combat.ConfirmedAttackCastCount;

        h.Feed("Also here: spectre.");
        h.Feed("You cast disrupt at spectre for 83 damage!");
        h.AdvanceClock(TimeSpan.FromMilliseconds(300));
        h.Feed("You cast disrupt at spectre for 91 damage!");

        Assert.Equal(1, h.Combat.ConfirmedAttackCastCount);
        Assert.Single(h.Posted);

        h.Feed("You gain 1500 experience.");
        h.DrainPosted();

        Assert.DoesNotContain("turn spectre", h.AllSent);
    }

    // Report paradigm-20260913-040159: an attack spell whose damage line never starts
    // with "You " — many single-target attack spells narrate the hit in third person
    // ("Spiritual power strikes X for N damage!") instead of the physical "You hit X
    // for N damage!" skeleton OnAttackCastConfirmed otherwise expects, and split the
    // announce and the damage attribution across two lines entirely (an incantation
    // line naming no target, then the third-person damage line). Neither line alone
    // satisfies the physical shape, so ConfirmedAttackCastCount froze forever, the
    // round-count tally never advanced, and MaxCastsPerRoom=1 was never perceived as
    // reached — soul auto-repeated every round and god's wrath was never reached.
    [Fact]
    public void MaxCasts1_ThirdPersonCasterMessage_TalliesAndSwitchesToAlternate()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "soul", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "gwra", MinEnemies = 0 };
        h.AddMonster(1, "fat giant squid");
        h.Combat.ReadRoundCount = () => h.Combat.ConfirmedAttackCastCount;
        h.Combat.ResolveAttackSpellMatchers = code => string.Equals(code, "soul", StringComparison.OrdinalIgnoreCase)
            ? Lines("Spiritual power strikes {target} for {damage} damage!")
            : Lines();

        h.Feed("Also here: fat giant squid.");
        Assert.Equal("soul fat giant squid", h.LastSent);

        // The incantation line alone confirms nothing (no target name in it) —
        // matches production, where this line never advances the tally by itself.
        h.Feed("You make a powerful incantation!");
        Assert.Equal(0, h.Combat.ConfirmedAttackCastCount);

        // The third-person damage line must confirm the cast via soul's own
        // caster-message template and immediately cap-switch to god's wrath.
        h.Feed("Spiritual power strikes fat giant squid for 171 damage!");
        Assert.Equal(1, h.Combat.ConfirmedAttackCastCount);
        Assert.Equal("gwra fat giant squid", h.LastSent);
    }

    private static IReadOnlyList<CasterMessageMatcher> Lines(params string[] templates)
        => templates.Select(t => CasterMessageMatcher.TryCreate(t)!).ToList();

    // A spell whose own record holds only the cast emote confirms on the damage
    // wording recorded under the same name (Paradigm dragonfire).
    [Fact]
    public void MaxCasts1_DamageLineOnASameNamedRecord_Counts()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "dfir", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mete", MinEnemies = 0 };
        h.AddMonster(1, "cave bear");
        h.Combat.ReadRoundCount = () => h.Combat.ConfirmedAttackCastCount;
        h.Combat.ResolveAttackSpellMatchers = _ => Lines(
            "A withering blast of dragonfire sears {target} for {damage} damage!",
            "You breath {s} on {s} for {damage} damage!");

        h.Feed("Also here: cave bear.");
        h.Feed("You make a complex circling gesture!");
        Assert.Equal(0, h.Combat.ConfirmedAttackCastCount);
        h.Feed("A withering blast of dragonfire sears cave bear for 90 damage!");

        Assert.Equal(1, h.Combat.ConfirmedAttackCastCount);
        Assert.Equal("mete cave bear", h.LastSent);
    }

    // Report paradigm-20261006-051627: a room spell's landing line names no monster
    // ("… ravages your foe for N damage!"), so it never counted as a cast, slot 1's
    // cap of one was never reached, and the second room spell was never cast. The
    // monsters' own lines open the round and trip the heartbeat first, so the
    // landing line has to carry the switch itself.
    [Fact]
    public void RoomSpellMaxCasts1_LandingLineNamingNoMonster_HandsOverToRoomSpell2()
    {
        using Harness h = new(deferPost: true);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "spir", MinEnemies = 3, MaxCastsPerRoom = 1 };
        h.Settings.MultiAttack2Enabled = true;
        h.Settings.MultiAttack2Spell = new CombatSpellSlot { SpellName = "msto", MaxCastsPerRoom = 99 };
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mete", MinEnemies = 0 };
        h.AddMonster(1, "slimeworm");
        h.Combat.ReadRoundCount = () => h.Combat.ConfirmedAttackCastCount;
        h.Combat.ResolveAttackSpellMatchers = code => code.ToLowerInvariant() switch
        {
            "spir" => Lines("A horde of shrieking spirits ravages {target} for {damage} damage!"),
            "msto" => Lines("A swirling mana storm engulfs {target} for {damage} damage!"),
            _ => Lines(),
        };

        h.Feed("Also here: slimeworm, slimeworm, slimeworm, slimeworm.");
        Assert.Equal("spir", h.LastSent);

        h.Feed("The slimeworm lunges at you!");
        h.Cast.OnCombatTick();
        h.Combat.OnCombatTick();
        h.Feed("You scatter some ashes in a sweeping motion!");
        Assert.Equal(0, h.Combat.ConfirmedAttackCastCount);

        h.Feed("A horde of shrieking spirits ravages your foe for 167 damage!");
        Assert.Equal(1, h.Combat.ConfirmedAttackCastCount);
        h.DrainPosted();
        Assert.Equal("msto", h.LastSent);
        Assert.Single(h.AllSent, s => s == "spir");

        // Slot 2 is counted the same way and stays on: nothing more is sent.
        h.AdvanceClock(TimeSpan.FromSeconds(5));
        h.Feed("You make a complex gesture!");
        h.Feed("A swirling mana storm engulfs your foe for 140 damage!");
        Assert.Equal(2, h.Combat.ConfirmedAttackCastCount);
        h.DrainPosted();
        Assert.Single(h.AllSent, s => s == "msto");
    }

    // A single-target spell still needs its line to name the monster we are on: the
    // same wording aimed elsewhere is not our round.
    [Fact]
    public void SingleTargetSpell_LineNamingAnotherMonster_IsNotCounted()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell    = new CombatSpellSlot { SpellName = "soul", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "gwra", MinEnemies = 0 };
        h.AddMonster(1, "fat giant squid");
        h.Combat.ReadRoundCount = () => h.Combat.ConfirmedAttackCastCount;
        h.Combat.ResolveAttackSpellMatchers = _ => Lines("Spiritual power strikes {target} for {damage} damage!");

        h.Feed("Also here: fat giant squid.");
        h.Feed("Spiritual power strikes whale shark for 171 damage!");

        Assert.Equal(0, h.Combat.ConfirmedAttackCastCount);
        Assert.Equal("soul fat giant squid", h.LastSent);
    }

    // The other side of the gate: a mid-fight between-round cast's *Combat Off* (even
    // with an exp gain sitting nearby, e.g. party share-exp) is NOT a kill — the
    // resume must still re-announce the spell rather than dropping a live target.
    [Fact]
    public void BetweenRoundCastOff_WithNearbyExp_StillResumes_NotTreatedAsKill()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("mmis giant rat", h.LastSent);

        h.Feed("You gain 100 experience.");   // e.g. a partymate's kill
        h.Combat.NoteBetweenRoundCast();       // our own heal broke combat this round
        h.Feed("*Combat Off*");

        // Not a kill (a cast explains the Off) → the live target is re-announced.
        Assert.Equal("mmis giant rat", h.LastSent);
    }

    // A between-round self-heal / buff drops *Combat Off* and the resume
    // path re-engages the SAME still-alive monster — that must read as a
    // continuation, not a new fight, or the phase counter restarts on every
    // interrupt and a round-cycle build heavy on self-heals never reaches its
    // spell phase (the reported "won't re-engage after buffing, confused
    // which attack to use").
    [Fact]
    public void CustomRoundCycle_ResumeAfterInterrupt_DoesNotResetPhase()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.CustomRoundCycle;
        h.Settings.CycleRoundsPhysical = 3;
        h.Settings.CycleRoundsSpell = 0;   // spells till death once reached
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // Round 0 (engage) — physical phase.
        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);

        // Round 1 — still physical, mid-phase.
        h.Tick();

        // A between-round cast (self-heal) interrupts the swing.
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        Assert.Equal("a giant rat", h.LastSent);   // resumed with a weapon swing, still physical

        // Rounds 2–3 — the phase boundary must land on schedule (round 3),
        // exactly as if the interrupt never happened. A phase-counter reset
        // on the resume would still be mid-physical here.
        h.Tick();
        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);
    }

    // The attack spell recasts IMMEDIATELY after the heal/buff that interrupted
    // it — engage, attack, heal-or-buff, attack, heal-or-buff, ... — not after
    // waiting out the round cooldown. An earlier attempt to fix a collision here
    // by respecting the cooldown instead just forced a full extra round of the
    // mob swinging free before the resume landed (a live capture caught it
    // exactly: armr fires, *Combat Off*, one full round of silence — the mob's
    // free swing — then harm finally resumes). CastingDirector's attack-owed gate
    // (CombatManager._spellAttackOwed) is what actually prevents the collision
    // this used to guard against — it stops a SECOND heal/buff from contesting
    // the round, so by the time this resume runs nothing else wants the slot.
    //
    // The buff and its *Combat Off* resume land within the same ~500ms — a live
    // capture (report paradigm-20260811-203111: "broke combat to cast armr, didn't
    // re-attack until after they swung") proved the server's Off comes back faster
    // than MinRecastInterval's 500ms burst guard, so that guard, not the round
    // cooldown, was deferring the resume a whole round. The resume dispatch now
    // passes bypassRecastInterval, so the re-attack goes out on THIS interrupt with
    // no failure at all — no round of silence.
    [Fact]
    public void SpellMode_ResumeAfterInterrupt_RecastsImmediately_NoRoundOfSilence()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("harm giant rat", h.LastSent);
        int sentAtEngage = h.Sent.Count;

        List<(CastFailureReason Reason, string Detail, string? Spell)> failures = new();
        h.Cast.CastFailed += (reason, detail, spell) => failures.Add((reason, detail, spell));

        // A survival cast (heal/buff) just went out, same instant.
        h.Cast.NotifyExternalCastSent();

        // Its *Combat Off* interrupt must resume the SAME target's attack spell
        // right away — no waiting for the next tick, no burst-guard deferral.
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");

        Assert.Equal(sentAtEngage + 1, h.Sent.Count);          // resume landed this round
        Assert.Equal("harm giant rat", h.LastSent);
        Assert.DoesNotContain(failures, f => f.Detail == "cast-blocked");
        Assert.DoesNotContain(failures, f => f.Detail == "recast-interval");
    }

    // Report paradigm-20260813-081016 ("why did it spam turn like that"): the
    // resume above correctly re-announces on the interrupt's *Combat Off* — but
    // casting the resumed spell ITSELF drops *Combat Off* again a moment later
    // (CONFIRMED mechanic, unlike a weapon swing). Without a per-interrupt
    // guard, that self-caused Off satisfies the exact same "within
    // CastInterruptResumeWindow of _betweenRoundCastAt" condition that fired
    // the first resume, so it fires AGAIN — and each of those casts drops its
    // OWN Off too, compounding into dozens of casts inside the 3s window from
    // one legitimate interrupt. A single NoteBetweenRoundCast must resume
    // exactly once, no matter how many further Off lines land before the
    // window expires.
    [Fact]
    public void SpellMode_ResumeAfterInterrupt_FiresOnlyOnce_NotEveryOffInWindow()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "turn", MinEnemies = 0 };
        h.AddMonster(1, "small zombie");

        h.Feed("Also here: small zombie.");
        Assert.Equal("turn small zombie", h.LastSent);

        h.Cast.NotifyExternalCastSent();
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        int sentAfterFirstResume = h.Sent.Count;
        Assert.Equal("turn small zombie", h.LastSent);   // the one legitimate resume

        // The resumed cast's own *Combat Off* lands — same interrupt window,
        // no new NoteBetweenRoundCast (nothing else cast a survival spell).
        // Fed repeatedly to mirror the live burst, which was dozens of lines.
        for (int i = 0; i < 10; i++) h.Feed("*Combat Off*");

        Assert.Equal(sentAfterFirstResume, h.Sent.Count);   // no further re-announces
    }

    // Report paradigm-20260813-131020: manually hand-casting a room/utility spell
    // mid-fight arms the same between-round resume, and a user MASHING casts
    // re-stamps it each keypress — each fresh stamp clears the per-interrupt guard
    // above, so without a manual-only rate limit each would fire its own re-attack
    // (the reported manual-cast → mmis spam). Two DISTINCT manual casts inside the
    // pacing window must resume only once. (The engine's per-round survival casts
    // are never paced — covered by SpellsFirst_RepeatedSelfHealInterrupts…).
    [Fact]
    public void SpellMode_MashedManualCasts_ResumeIsRateLimited()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "turn", MinEnemies = 0 };
        h.AddMonster(1, "small zombie");

        h.Feed("Also here: small zombie.");
        Assert.Equal("turn small zombie", h.LastSent);

        // First manual cast → its *Combat Off* resumes the attack once.
        h.Cast.NotifyExternalCastSent();
        h.Combat.NoteManualBetweenRoundCast();
        h.Feed("*Combat Off*");
        int afterFirst = h.Sent.Count;

        // A SECOND, distinct manual cast lands within the pacing window (instant in
        // a test). Its fresh stamp clears the per-interrupt guard, but the
        // manual-cast rate limit suppresses the re-attack.
        h.Cast.NotifyExternalCastSent();
        h.Combat.NoteManualBetweenRoundCast();
        h.Feed("*Combat Off*");

        Assert.Equal(afterFirst, h.Sent.Count);   // no second re-announce within the window
    }

    // A *Combat Off* nothing of ours explains, in a spell fight (a stun, a typed
    // command): the round tick alone brings the spell back (user, 2026-10-09). It
    // used to take a combat line the client reads, which a monster whose swings it
    // doesn't read never gives.
    private static Harness SpellFightStoppedAfterTheAnnounceWasAnswered(out int announced)
    {
        Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "turn", MinEnemies = 0 };
        h.AddMonster(1, "small zombie");
        h.Feed("Also here: small zombie.");
        Assert.Equal("turn small zombie", h.LastSent);
        h.Feed("*Combat Engaged*");
        announced = h.Sent.Count;
        h.Feed("*Combat Off*");
        Assert.Equal(announced, h.Sent.Count);               // nothing on the Off line itself
        return h;
    }

    [Fact]
    public void SpellMode_UnexplainedCombatOff_RoundTickAloneReAnnouncesTheSpell()
    {
        using Harness h = SpellFightStoppedAfterTheAnnounceWasAnswered(out int announced);

        h.Combat.OnCombatTick();

        Assert.Equal(announced + 1, h.Sent.Count);
        Assert.Equal("turn small zombie", h.LastSent);
    }

    [Fact]
    public void SpellMode_UnexplainedCombatOff_ALineAndTheTickOfOneRound_AreOneReAnnounce()
    {
        // The round's first line wakes OnCombatLine and is the tick as well; later
        // ticks inside the pacing window add nothing. One announce a round is what
        // keeps the casts to what the spell's energy allows.
        using Harness h = SpellFightStoppedAfterTheAnnounceWasAnswered(out int announced);

        h.Feed("The small zombie claws you for 5 damage!");
        h.Combat.OnCombatTick();
        h.Combat.OnCombatTick();

        Assert.Equal(announced + 1, h.Sent.Count);
    }

    [Fact]
    public void SpellMode_UnexplainedCombatOff_TargetGone_TickSendsNothing()
    {
        using Harness h = SpellFightStoppedAfterTheAnnounceWasAnswered(out int announced);

        h.Classifier.NoteRoomChanged();                         // the room emptied; nothing to cast at
        h.Combat.OnCombatTick();

        Assert.Equal(announced, h.Sent.Count);
    }

    [Fact]
    public void SpellMode_UnexplainedCombatOff_AutoCombatOff_TickSendsNothing()
    {
        using Harness h = SpellFightStoppedAfterTheAnnounceWasAnswered(out int announced);
        h.AutoCombatEnabled = false;

        h.Combat.OnCombatTick();

        Assert.Equal(announced, h.Sent.Count);
    }

    [Fact]
    public void SpellMode_FreshAnnounceNotYetAnswered_TickDoesNotAnnounceAgain()
    {
        // The Off the game prints for the announce itself, before its Engaged: the
        // spell is in flight, and a second announce would only break it off.
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "turn", MinEnemies = 0 };
        h.AddMonster(1, "small zombie");
        h.Feed("Also here: small zombie.");
        int announced = h.Sent.Count;

        h.Feed("*Combat Off*");
        h.Combat.OnCombatTick();

        Assert.Equal(announced, h.Sent.Count);
    }

    // Report paradigm-20261009-120757, 12:07:17: the heal's latch was armed, the game
    // refused the heal (which draws no *Combat Off*), and the Off of the attack the
    // engine sent next was taken for the cast's.
    [Fact]
    public void RefusedBetweenRoundCast_DisarmsTheResume_TheNextCombatOffIsNotItsOwn()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);
        int sent = h.Sent.Count;

        h.Cast.NotifyExternalCastSent();
        h.Combat.NoteBetweenRoundCast();
        h.Feed("You have already cast a spell this round!");
        h.Feed("*Combat Off*");

        Assert.Equal(sent, h.Sent.Count);
    }

    // Report paradigm-20260921-074300: with SpellsFirst, once the attack-spell cascade
    // lapsed to the weapon (mana under the per-cast floor), the heartbeat returned early
    // in weapon mode and never re-evaluated — so a mana regen back over the floor went
    // unnoticed until an unrelated room re-observation forced a fresh decision (kept
    // swinging ~6 rounds after mana had recovered). The weapon→spell re-climb fixes it:
    // at a round boundary in weapon mode it re-runs the chooser and switches back to the
    // spell once its resource recovers. (This mob was never cast at, so the per-target
    // weapon latch — which deliberately commits the SAME target to the weapon — is off.)
    [Fact]
    public void SpellsFirst_ManaRecoversInWeaponMode_ReclimbsToTheAttackSpell()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.Settings.SpellManaThresholdMode = ThresholdMode.Absolute;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 0, MinManaPerCast = 50 };
        h.AddMonster(1, "giant rat");

        h.Ma = 30;                              // below the 50 per-cast floor
        h.Feed("Also here: giant rat.");        // engage → harm can't fire → weapon
        Assert.DoesNotContain("harm giant rat", h.AllSent);

        h.Ma = 80;                              // mana regenerates back over the floor
        h.Tick();                               // round boundary → weapon→spell re-climb

        Assert.Equal("harm giant rat", h.LastSent);
    }

    // ----- manual user-attack override (report paradigm-20260814-135715) ------

    [Fact]
    public void ManualPhysicalAttack_SuppressesResume_ThenNextTickResumes()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");        // engage → "harm giant rat"
        int afterEngage = h.Sent.Count;

        // The user takes this round: a hand-typed physical attack (no engine echo claim
        // to match, so it's read as manual).
        h.Combat.NoteAttackCommandObserved("a");

        // An interrupt's *Combat Off* would normally resume our attack — the override
        // must hold it this round.
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        Assert.Equal(afterEngage, h.Sent.Count);      // suppressed — engine sent nothing over the user

        // Next round (tick) clears the override and re-announces the spell that Off
        // stopped: in spell mode nothing else would (report paradigm-20261003-194358).
        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);
        Assert.Equal(afterEngage + 1, h.Sent.Count);

        // A fresh interrupt resumes as usual.
        int afterTick = h.Sent.Count;
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        Assert.True(h.Sent.Count > afterTick);        // resumes — override cleared at the tick
    }

    // The held resume never fires at a corpse: a kill inside the held round ends it.
    [Fact]
    public void ManualPhysicalAttack_HeldResume_DroppedWhenTheTargetDies()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        h.Combat.NoteAttackCommandObserved("a");
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        int held = h.Sent.Count;

        h.Feed("You gain 100 experience.");           // the user's swing killed it
        h.Tick();

        Assert.DoesNotContain("harm giant rat", h.AllSent.Skip(held));
    }

    [Fact]
    public void EngineOwnAttackEcho_IsNotTreatedAsManualOverride()
    {
        using Harness h = new();
        h.Settings.NormalWeapon = "sword";              // weapon build — engine swings, no spell
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");                // engine sends "a giant rat"
        Assert.Equal("a giant rat", h.LastSent);

        // The engine's own swing flows back through the observer as "a"; that echo must
        // be consumed, NOT armed as a user override — otherwise the resume below stalls.
        h.Combat.NoteAttackCommandObserved("a");

        int afterEngage = h.Sent.Count;
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        Assert.True(h.Sent.Count > afterEngage);        // resume still fires — no false override
    }

    [Fact]
    public void ManualCombatCast_Overrides_ButUtilityCastResumes()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 0 };
        h.Combat.SetCombatSpellPredicate(code => string.Equals(code, "nuke", StringComparison.OrdinalIgnoreCase));
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        int afterEngage = h.Sent.Count;

        // A hand-cast COMBAT spell (energy 1–1000) is the user's attack — override holds.
        h.Combat.OnManualCastObserved("nuke");
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        Assert.Equal(afterEngage, h.Sent.Count);        // suppressed

        h.Tick();

        // A hand-cast IN-BETWEEN spell (heal/buff) is NOT an override — it keeps the
        // resume so the engine re-attacks after the heal.
        int afterTick = h.Sent.Count;
        h.Combat.OnManualCastObserved("heal");          // not the combat cast-code
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        Assert.True(h.Sent.Count > afterTick);          // resumed after the utility cast
    }

    [Fact]
    public void ManualCast_NoEffect_DoesNotMarkEngineSpellImmune()
    {
        // report paradigm-20260818-055955: a hand-typed spell that draws "no effect"
        // must not be blamed on the engine's last AUTO cast. The engine's
        // _lastCastAction still points at its own prior spell, so attributing the
        // immunity there wrongly marked the engine's attack spell immune and dropped
        // the cascade straight to melee. The override guard keeps a manual probe from
        // mutating the auto-cascade immune map.
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.SpellsFirst;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 0 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "zap", MinEnemies = 0 };
        h.Combat.SetCombatSpellPredicate(code => string.Equals(code, "dtch", StringComparison.OrdinalIgnoreCase));
        h.AddMonster(1, "earth elemental");

        h.Feed("Also here: earth elemental.");            // engine engages with "harm earth elemental"
        Assert.Equal("harm earth elemental", h.LastSent);

        // The user hand-casts a probe (dtch) at the elemental; the server says it has no
        // effect. The override holds the engine's send THIS round, so the wrong immune
        // mark only surfaces next round — pre-fix, "harm" was marked immune and the engine
        // switched to the alternate "zap" once the override cleared.
        h.Combat.OnManualCastObserved("dtch");
        h.Feed("Your spell has no effect on earth elemental.");
        h.Tick();   // clears the override; engine re-decides

        Assert.DoesNotContain("zap earth elemental", h.AllSent);   // harm NOT marked immune
    }

    // ----- Auto-Nuke auto-engine gate ----------------------------------

    [Fact]
    public void AutoNukeOff_MultiAttackQualifies_FallsToWeapon()
    {
        using Harness h = new();
        h.AutoNukeEnabled = false;            // nukes disabled
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // Multi-target nuke is suppressed; the single-target weapon swing runs.
        Assert.Equal("a giant rat", h.LastSent);
        Assert.DoesNotContain("blast", h.AllSent);
    }

    [Fact]
    public void AutoNukeOff_SingleTargetAttackSpell_StillFires()
    {
        using Harness h = new();
        h.AutoNukeEnabled = false;            // nukes disabled
        // A single-target attack spell is NOT a nuke — it stays available.
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "lightning", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("lightning giant rat", h.LastSent);
    }

    [Fact]
    public void AutoNukeOff_AreaDebuff_NotOffered()
    {
        using Harness h = new();
        h.AutoNukeEnabled = false;            // nukes (incl. debuffs) disabled
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // No combat spell configured beyond the debuff → weapon swing, and the
        // in-between debuff window stays empty.
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Null(h.Combat.PickInBetweenDebuff());
    }

    // ----- per-monster spell overrides (Number → Short resolution) -----

    [Fact]
    public void AttackOverride_CastsOverrideSpell_NotConfiguredNormal()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.SpellShorts[42] = "fireball";
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 42, OverrideAttackCount = 3 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // The Spell.Number override (42) resolves to its Short and replaces the
        // configured normal-attack cast-code for this monster.
        Assert.Equal("fireball giant rat", h.LastSent);
        Assert.DoesNotContain("harm giant rat", h.AllSent);
    }

    // A room debuff in a monster's debuff override is allowed and cast bare.
    [Fact]
    public void PreAttackOverride_RoomDebuff_IsCastBare()
    {
        using Harness h = new();
        h.SpellShorts[71] = "stnk";
        h.SpellsByCode["stnk"] = new KnownSpell(71, "stnk", "stinking cloud", 1, 1, 1, 12,
            new SpellFormulaInput { EnergyCost = 0 });
        h.Overlays[80] = new MonsterOverlay { OverridePreAttackSpellId = 71, OverridePreAttackCount = 1 };
        h.AddMonster(80, "cave bear");

        h.Feed("Also here: cave bear.");

        Assert.Contains("stnk", h.AllSent);
        Assert.DoesNotContain("stnk cave bear", h.AllSent);
    }

    // A room spell in a monster's attack override is cast bare, like a multi-attack —
    // the targeted form is an unknown command to the server.
    [Fact]
    public void AttackOverride_RoomSpell_IsCastBare()
    {
        using Harness h = new();
        h.SpellShorts[566] = "esto";
        h.SpellsByCode["esto"] = new KnownSpell(566, "esto", "eldritch storm", 1, 1, 1, 12,
            new SpellFormulaInput { EnergyCost = 1000 });
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 566 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("esto", h.LastSent);
        Assert.DoesNotContain("esto giant rat", h.AllSent);
    }

    [Fact]
    public void AttackOverride_NullCount_StillActivatesWithUnlimitedCap()
    {
        // report paradigm-20260813-132647: an override spell set with no Max
        // was silently ignored, casting the global attack spell instead — the
        // count is only a per-room cast cap (CastsOk already treats a null cap
        // as unlimited), not a required activation gate.
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.SpellShorts[42] = "fireball";
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 42 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("fireball giant rat", h.LastSent);
        Assert.DoesNotContain("harm giant rat", h.AllSent);
    }

    [Fact]
    public void AttackOverride_UnknownNumber_FallsBackToConfiguredSlot()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        // Resolver has no entry for 99 → override can't resolve → configured slot.
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 99, OverrideAttackCount = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("harm giant rat", h.LastSent);
    }

    [Fact]
    public void PreAttackOverride_FiresBeforeAttack()
    {
        using Harness h = new();
        h.SpellShorts[7] = "curse";
        h.Overlays[1] = new MonsterOverlay { OverridePreAttackSpellId = 7, OverridePreAttackCount = 2 };
        h.AddMonster(1, "giant rat");

        // A per-monster pre-attack override (a single-target debuff) fires BEFORE the
        // attack on engage, keeping its mob ("curse giant rat"), then the combat attack
        // (here the weapon, no attack spell configured) fires immediately behind it in
        // the SAME round — the debuff and the attack are independent slots (report
        // paradigm-20260825-103417).
        h.Feed("Also here: giant rat.");

        Assert.Contains("curse giant rat", h.AllSent);
        Assert.Equal("a giant rat", h.LastSent);
    }

    // An attack spell (energy > 0) in the per-monster DEBUFF override was cast as a
    // between-round debuff and then overwritten by the attack sent right behind it, so
    // it never landed (reports paradigm-20260928-030642 / -031828). It's held to the
    // Combat-tab debuff rules now: blocked, and the engage goes straight to the attack.
    [Fact]
    public void PreAttackOverride_AttackSpell_IsBlocked()
    {
        using Harness h = new();
        h.SpellShorts[1] = "mmis";
        h.SpellsByCode["mmis"] = new KnownSpell(1, "mmis", "magic missile", 1, 1, 1, 8,
            new SpellFormulaInput { EnergyCost = 500 });
        h.Overlays[80] = new MonsterOverlay { OverridePreAttackSpellId = 1, OverridePreAttackCount = 1 };
        h.AddMonster(80, "cave bear");

        h.Feed("Also here: cave bear.");

        Assert.DoesNotContain("mmis cave bear", h.AllSent);
        Assert.Equal("a cave bear", h.LastSent);
    }

    // A cast ends the sneak (the engine's cast command clears the sneaking and hidden
    // flags), so a pre-attack debuff before the backstab spends the surprise. It waits
    // for the round after the backstab.
    [Fact]
    public void PreAttackDebuff_WaitsForTheBackstab()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Sneaking = true;
        h.SpellShorts[7] = "curse";
        h.Overlays[1] = new MonsterOverlay { OverridePreAttackSpellId = 7, OverridePreAttackCount = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("bs giant rat", h.LastSent);
        Assert.DoesNotContain("curse giant rat", h.AllSent);
    }

    [Fact]
    public void AltAttackOverride_CastsOnAltRung_WhenNormalUnconfigured()
    {
        using Harness h = new();
        // No normal attack spell; the alternate rung is supplied by a per-monster
        // override (Spell.Number 55 → its Short cast-code).
        h.SpellShorts[55] = "iceball";
        h.Overlays[1] = new MonsterOverlay { OverrideAltAttackSpellId = 55, OverrideAltAttackCount = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("iceball giant rat", h.LastSent);
    }

    // ----- physical-first: weapon exhausted before spells -------------

    [Fact]
    public void PhysicalFirst_ExhaustsWeaponBeforeFallingToSpell()
    {
        // Physical-first with a caster: the weapon must be GENUINELY exhausted
        // before the spell cascade is reached. On the first alt no-effect the
        // engine force-retries the weapon (not the spell); only once THAT also
        // fails does it cast the attack spell.
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.PhysicalFirst;
        h.Settings.NormalWeapon = "sword";
        h.Settings.AlternateWeapon = "hammer";
        h.Settings.AlternateAttackCommand = "aa";
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "lightning", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");                             // swings weapon (physical-first)
        Assert.Equal("a giant rat", h.LastSent);

        h.Feed("Your weapon has no effect against this monster!");   // normal → swap to alt
        h.Feed("Your weapon has no effect against this monster!");   // alt 1st → force-retry the WEAPON
        Assert.Equal("aa giant rat", h.LastSent);
        Assert.DoesNotContain("lightning giant rat", h.AllSent);     // spell not reached yet

        h.Feed("Your weapon has no effect against this monster!");   // alt 2nd → weapon out → spell
        Assert.Equal("lightning giant rat", h.LastSent);
    }

    [Fact]
    public void ManaStuck_WeaponOut_MovesOnNow_ButStaysRetryableUntilManaRegens()
    {
        // Weapons can't hit and MA is below the spell's cast floor: the mob is
        // un-actionable THIS round (the walker moves on rather than stand getting
        // beaten waiting for a mana tick) — but NOT permanently. Once MA regens
        // above the floor it reads actionable again, so the cast chain is retried.
        using Harness h = new();
        h.Settings.NormalWeapon = "sword";
        h.Settings.AlternateWeapon = "hammer";
        h.Settings.AlternateAttackCommand = "aa";
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "lightning", MinManaPerCast = 50 };
        h.Ma = 10; h.MaxMa = 100;                                    // below the cast floor
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");                            // spell can't fire → swings

        h.Feed("Your weapon has no effect against this monster!");   // normal → alt
        h.Feed("Your weapon has no effect against this monster!");   // alt 1st → retry
        h.Feed("Your weapon has no effect against this monster!");   // alt 2nd → weapon out

        Assert.False(h.Combat.CanEngageMonster(1));                  // can't act now → move on
        h.Ma = 100;                                                 // MA regenerates
        Assert.True(h.Combat.CanEngageMonster(1));                  // castable again → retry
    }

    // ----- engageability weighs per-monster overrides -------------------

    [Fact]
    public void Engage_OverrideSpell_MakesWeaponImmuneMonsterKillable()
    {
        // Both weapons proven ineffective AND no configured attack spell — but a
        // per-monster override spell can land. The pre-engage gate must treat the
        // monster as actionable, else the walker skips a monster the override kills.
        using Harness h = new();
        h.Settings.NormalWeapon = "sword";
        h.Settings.AlternateWeapon = "hammer";
        h.Settings.AlternateAttackCommand = "aa";
        h.SpellShorts[42] = "fireball";
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 42 };
        h.Ma = 100; h.MaxMa = 100;
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        h.Feed("Your weapon has no effect against this monster!");   // normal → alt
        h.Feed("Your weapon has no effect against this monster!");   // alt retry
        h.Feed("Your weapon has no effect against this monster!");   // alt out → weapons exhausted

        Assert.True(h.Combat.CanEngageMonster(1));
    }

    [Fact]
    public void Engage_NoKillMeans_WeaponsOutNoSpell_StaysUnkillable()
    {
        // Negative control: same weapons-out setup with NO override and NO configured
        // attack spell → genuinely unkillable, so the walker still moves on.
        using Harness h = new();
        h.Settings.NormalWeapon = "sword";
        h.Settings.AlternateWeapon = "hammer";
        h.Settings.AlternateAttackCommand = "aa";
        h.Ma = 100; h.MaxMa = 100;
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        h.Feed("Your weapon has no effect against this monster!");
        h.Feed("Your weapon has no effect against this monster!");
        h.Feed("Your weapon has no effect against this monster!");

        Assert.False(h.Combat.CanEngageMonster(1));
    }

    [Fact]
    public void Engage_OverrideSpellManaBlocked_StuckNotUnkillable()
    {
        // Weapons out, only an override spell, MA below the override's own floor:
        // actionable-once-mana-returns (StuckOnMana → not engageable now), but NOT
        // written off — a mana tick makes it engageable again.
        using Harness h = new();
        h.Settings.NormalWeapon = "sword";
        h.Settings.AlternateWeapon = "hammer";
        h.Settings.AlternateAttackCommand = "aa";
        h.SpellShorts[42] = "fireball";
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 42, OverrideAttackMinMana = 50 };
        h.Ma = 10; h.MaxMa = 100;                                    // below the override floor
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        h.Feed("Your weapon has no effect against this monster!");
        h.Feed("Your weapon has no effect against this monster!");
        h.Feed("Your weapon has no effect against this monster!");

        Assert.False(h.Combat.CanEngageMonster(1));                  // stuck on mana → move on for now
        h.Ma = 100;                                                 // MA regenerates
        Assert.True(h.Combat.CanEngageMonster(1));                  // override can land → actionable
    }

    // ----- announce once; the server auto-repeats -----------------------

    [Fact]
    public void Heartbeat_AnnouncesOnce_ServerAutoRepeats()
    {
        // CONFIRMED mechanic: an announced spell attack auto-repeats server-side each
        // round (like a weapon swing). So the client announces it ONCE and the
        // heartbeat sends NOTHING on later rounds while the decision is unchanged —
        // re-sending a spell the server is already repeating is the double/corpse-cast
        // bug this rework removes.
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");      // announce once
        h.Tick();                             // server repeats — no re-send
        h.Tick();

        Assert.Equal(1, h.AllSent.Count(s => s == "blast"));
        Assert.DoesNotContain("a giant rat", h.AllSent);
    }

    [Fact]
    public void Heartbeat_MaxCastsReached_SwitchesToWeapon()
    {
        // MaxCasts is the number of ROUNDS the spell actually fires; only after that
        // does the client re-announce the next cascade action (here the weapon, no
        // attack spells configured). Announcing is round 0 — the spell fires on the
        // NEXT tick — so the announce does NOT count toward the cap; each heartbeat
        // that keeps the same decision counts one fired round. The switch is announced
        // ON the cap-reaching tick, pre-empting the server's extra auto-repeat.
        using Harness h = new();
        h.Settings.MultiAttackSpell =
            new CombatSpellSlot { SpellName = "blast", MinEnemies = 1, MaxCastsPerRoom = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");      // announce — round 0 (fires next tick), not counted
        h.Tick();                             // fired round 1 of 2 — still repeating, no send
        Assert.Equal("blast", h.LastSent);
        h.Tick();                             // fired round 2 of 2 → cap reached → switch to weapon NOW

        Assert.Equal(1, h.AllSent.Count(s => s == "blast"));   // announced once
        Assert.Equal("a giant rat", h.LastSent);                          // switched to weapon
        h.Tick();                             // weapon mode — heartbeat quiet
        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- stop on death; re-engage the survivor as a spell -------------

    [Fact]
    public void SpellKill_SendsNothingAtTheCorpse()
    {
        // The kill ends the engagement (the server stops its repeat; the client
        // clears spell mode). The heartbeat must not re-announce at the dead target —
        // the "You don't see X here!" corpse cast this rework fixes.
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");        // announce harm
        Assert.Equal("harm giant rat", h.LastSent);
        int sentAtKill = h.Sent.Count;

        h.Feed("The giant rat dies.");
        h.Tick();
        h.Tick();

        Assert.Equal(sentAtKill, h.Sent.Count);   // nothing sent after the kill
    }

    [Fact]
    public void AfterKill_NextMonster_ReEngagedWithSpell()
    {
        // A spell fighter that kills a mob re-engages the next one with the SPELL
        // (via the chooser), not a weapon swing.
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "orc");

        h.Feed("Also here: giant rat.");        // announce harm at the rat
        h.Feed("The giant rat dies.");          // kill
        h.Feed("Also here: orc.");              // next monster
        h.Tick();                               // re-announce at the survivor

        Assert.Equal("harm orc", h.LastSent);
        Assert.DoesNotContain("a orc", h.AllSent);
    }

    [Fact]
    public void MaxCasts_SingleTarget_ResetsPerTarget()
    {
        // A single-target attack spell's MaxCasts is PER TARGET: after it caps on one
        // mob, the next mob gets the spell again (not stuck on the weapon).
        using Harness h = new();
        h.Settings.NormalAttackSpell =
            new CombatSpellSlot { SpellName = "harm", MinEnemies = 1, MaxCastsPerRoom = 1 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "orc");

        h.Feed("Also here: giant rat.");        // announce harm at the rat (per-target cap 1)
        Assert.Equal("harm giant rat", h.LastSent);
        h.Feed("The giant rat dies.");          // kill → per-target counters reset
        h.Feed("Also here: orc.");              // fresh engage fires immediately — no tick needed

        Assert.Equal("harm orc", h.LastSent);   // spell again, not weapon
    }

    // ----- report paradigm-20260812-200128: normal/alternate double-fire ------

    [Fact]
    public void NormalKills_MaxCasts1_WithAlternate_NoAlternateAtCorpse()
    {
        // Screenshots 1/2: lbol (MaxCasts=1) kills → the engine must NOT advance the
        // cascade and fire the alternate (mmis) at the just-dead target. A kill wins
        // over the MaxCasts switch. Uses the custom-death-line kill shape (exp + Off).
        using Harness h = new();
        h.Settings.NormalAttackSpell =
            new CombatSpellSlot { SpellName = "lbol", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("lbol giant rat", h.LastSent);
        int sentAtKill = h.Sent.Count;

        h.Feed("You gain 100 experience.");
        h.Feed("*Combat Off*");
        h.Tick();
        h.Tick();

        Assert.Equal(sentAtKill, h.Sent.Count);                 // nothing cast after the kill
        Assert.DoesNotContain("mmis giant rat", h.AllSent);     // no alternate at the corpse
    }

    [Fact]
    public void NormalSpell_MaxCasts1_FiresARoundBeforeCascadingToAlternate()
    {
        // Screenshot 3: lbol (MaxCasts=1) must get its own fired round — it must NOT
        // flip to the alternate the same round it's announced. MaxCasts counts fired
        // rounds, not the announce. The switch then lands on the cap-reaching tick, not
        // one tick later — see NormalSpell_MaxCasts1_SwitchesOnTheCapTick.
        using Harness h = new();
        h.Settings.NormalAttackSpell =
            new CombatSpellSlot { SpellName = "lbol", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");        // announce lbol — round 0, not counted
        Assert.Equal("lbol giant rat", h.LastSent);
        Assert.DoesNotContain("mmis giant rat", h.AllSent);   // not flipped the round it's announced

        h.Tick();                               // lbol's one fired round → cap → cascade to alt
        Assert.Equal("mmis giant rat", h.LastSent);
    }

    [Fact]
    public void NormalSpell_MaxCasts1_SwitchesOnTheCapTick()
    {
        // report paradigm-20260814-061340: MaxCasts=1 fired the spell TWICE server-side.
        // The server auto-repeats the announced spell every round; the client announced
        // the switch to the alternate one tick AFTER the cap was reached, so the server
        // repeated the capped spell one extra round before the switch landed. The switch
        // must be announced on the SAME tick the cap is reached, pre-empting that repeat.
        using Harness h = new();
        h.Settings.NormalAttackSpell =
            new CombatSpellSlot { SpellName = "vamp", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");        // announce vamp — round 0 (fires next tick)
        Assert.Equal("vamp giant rat", h.LastSent);

        h.Tick();                               // vamp's one fired round → cap → switch NOW
        Assert.Equal("mmis giant rat", h.LastSent);
        Assert.Equal(1, h.AllSent.Count(s => s == "vamp giant rat"));   // vamp announced exactly once
    }

    [Fact]
    public void AfterKill_NextMonster_ReOpensWithNormalNotAlternate()
    {
        // Screenshot 4 / bug #3: after the normal caps and the cascade advances to
        // the alternate, a KILL resets the cascade so the NEXT mob reconsiders the
        // normal spell first — it must not inherit the dead mob's advanced cascade.
        using Harness h = new();
        h.Settings.NormalAttackSpell =
            new CombatSpellSlot { SpellName = "lbol", MinEnemies = 0, MaxCastsPerRoom = 1 };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 0 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "orc");

        h.Feed("Also here: giant rat.");        // lbol at the rat
        h.Tick();                               // lbol's fired round → cap → cascade to mmis
        Assert.Equal("mmis giant rat", h.LastSent);

        h.Feed("The giant rat dies.");          // kill → cascade reset
        h.Feed("Also here: orc.");              // next monster — re-opens with the normal
        h.Tick();

        // The orc's FIRST cast is the normal (lbol), proving the cascade reset — it did
        // not inherit the rat's spent cascade. Its own cap-tick then advances to the
        // alternate, but the re-open is what this guards.
        string firstOrcCast = h.AllSent.First(s => s.EndsWith(" orc", StringComparison.Ordinal));
        Assert.Equal("lbol orc", firstOrcCast);
    }

    [Fact]
    public void Heartbeat_ManaDrained_FallsToWeapon()
    {
        using Harness h = new();
        h.Settings.SpellManaThresholdMode = ThresholdMode.Absolute;
        h.Settings.MultiAttackSpell =
            new CombatSpellSlot { SpellName = "blast", MinEnemies = 1, MinManaPerCast = 30 };
        h.AddMonster(1, "giant rat");

        h.Ma = 50;
        h.Feed("Also here: giant rat.");      // cast (50 >= 30)
        Assert.Equal("blast", h.LastSent);

        h.Ma = 20;                            // now below the gate
        h.Tick();                             // mana too low → weapon

        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- in-between debuff bridge ------------------------------------

    [Fact]
    public void AreaDebuff_FiresBeforeAttack_ThenAttackImmediately_SameRound_OncePerRoom()
    {
        using Harness h = new();
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // On engage the area debuff fires BEFORE the attack — cast bare, never
        // "curse <mob>" — and the combat attack fires IMMEDIATELY behind it in the
        // SAME round rather than waiting a whole round for the debuff's *Combat Off*
        // (report paradigm-20260825-103417). The between-round debuff and the combat
        // attack are independent slots. (No director wired here, so the pre-attack
        // pass fires the debuff directly; the switch-dispatch scheduler runs inline.)
        h.Feed("Also here: giant rat.");
        Assert.Contains("curse", h.AllSent);
        Assert.Equal("blast", h.LastSent);

        // The debuff's own *Combat Off* must NOT fire the attack a SECOND time —
        // the immediate dispatch already sent it this round.
        h.Feed("*Combat Off*");
        Assert.Single(h.AllSent, s => s == "blast");

        // Once per room: later rounds keep attacking and never re-fire the debuff.
        h.Tick();
        Assert.Equal("blast", h.LastSent);
        Assert.Single(h.AllSent, s => s == "curse");
    }

    // Report paradigm-20260916-104702: with "AoE debuff first round only" set, once the
    // fight is underway in the room the debuff is abandoned — a round-2+ debuff (its
    // between-round slot lost to a higher-priority buff on entry) is wasted mana. The
    // debuff fires on entry (combat not seen yet), but a rejected/held round-1 attempt is
    // NOT retried once an attack has been swung.
    [Fact]
    public void AreaDebuffFirstRoundOnly_NotRetriedOnceCombatSeen()
    {
        using Harness h = new();
        // Slot spent every round → the pre-attack can't land the debuff on entry, so it's
        // never cast (no once-per-room tag) — the exact situation the user hits when a
        // higher-priority buff owns the between-round slot on room entry.
        h.Combat.SetBetweenRoundSlotQuery(() => true);
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.Settings.AreaDebuffFirstRoundOnly = true;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");        // pre-attack HELD (slot spent); blast attacks → combat seen
        Assert.DoesNotContain("curse", h.AllSent);   // debuff never landed on entry

        // 'First round only' abandons the debuff now that combat's underway.
        Assert.Null(h.Combat.PickInBetweenDebuff());
    }

    [Fact]
    public void AreaDebuffFirstRoundOnly_Off_RetriesAfterCombatSeen()
    {
        // Same shape, flag OFF (default): the debuff keeps retrying until it lands.
        using Harness h = new();
        h.Combat.SetBetweenRoundSlotQuery(() => true);
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.DoesNotContain("curse", h.AllSent);

        Assert.Equal("curse", h.Combat.PickInBetweenDebuff()?.Spell);   // retries — no first-round gate
    }

    // The corpse-cast guard on the immediate post-debuff attack: an AoE debuff that
    // kills the room drops the target in the gap before the deferred attack runs, so
    // the attack must re-validate and skip rather than blast an empty room (report
    // paradigm-20260825-103417). deferPost models the ~200ms spacing: the attack is
    // queued, the kills land, then the window elapses.
    [Fact]
    public void PreAttackDebuff_KillsRoom_DeferredAttackSkips_NoCorpseCast()
    {
        using Harness h = new(deferPost: true);
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "kobold");

        // Engage: the debuff fires directly; the combat attack is scheduled behind it.
        h.Feed("Also here: giant rat, kobold.");
        Assert.Equal("curse", h.LastSent);
        Assert.Single(h.Posted);

        // The AoE debuff wiped the room — two exp gains this round force the roster
        // re-parse that drops the current target before the deferred attack runs.
        h.Feed("You gain 100 experience.");
        h.Feed("You gain 100 experience.");

        // The deferred attack re-validates, sees the target gone, and skips — no
        // "blast" corpse-cast at the cleared room.
        h.DrainPosted();
        Assert.DoesNotContain("blast", h.AllSent);
    }

    [Fact]
    public void AoeMultiKill_AllListedHostilesDead_DropsRosterImmediately()
    {
        // Report -081208: an AoE that wipes the whole room used to leave the combat
        // gate held until the 6s idle-stall watchdog — the empty re-display carries no
        // "Also here:" line, so the classifier fired no observation. When this round's
        // kills account for EVERY listed hostile, drop the roster now (an empty
        // observation) so the gate can release on the CR round-trip instead of 6s.
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 1 };
        h.AddMonster(1, "scorpion crab");
        h.AddMonster(2, "ironshell crab");

        h.Feed("Also here: scorpion crab, ironshell crab.");   // engage; 2 hostiles listed

        int lastMonsterCount = -1;
        h.Classifier.EntitiesObserved += o =>
            lastMonsterCount = o.Entities.Count(e => e.Kind == EntityKind.Monster);

        // The AoE kills both this round → two exp gains == the whole listed roster.
        h.Feed("You gain 100 experience.");
        h.Feed("You gain 100 experience.");

        Assert.Equal(0, lastMonsterCount);   // roster dropped to empty → gate can release now
    }

    [Fact]
    public void AoeMultiKill_SurvivorRemains_DoesNotDropRoster()
    {
        // Two exp gains but THREE listed hostiles → a survivor remains, so the roster
        // is NOT dropped early (the CR re-parse handles it); the gate must not release
        // over a live mob.
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 1 };
        h.AddMonster(1, "scorpion crab");
        h.AddMonster(2, "ironshell crab");
        h.AddMonster(3, "giant crab");

        h.Feed("Also here: scorpion crab, ironshell crab, giant crab.");   // 3 hostiles

        int lastMonsterCount = 99;
        h.Classifier.EntitiesObserved += o =>
            lastMonsterCount = o.Entities.Count(e => e.Kind == EntityKind.Monster);

        h.Feed("You gain 100 experience.");
        h.Feed("You gain 100 experience.");   // 2 kills < 3 listed → survivor

        Assert.NotEqual(0, lastMonsterCount);   // roster NOT wiped to empty (no premature clear)
    }

    // Report paradigm-20261007-131120: a room spell (hsto, two or more) killed one of
    // two. A running room spell draws no *Combat Off*, so the corpse stayed on the
    // roster, the count never fell under the spell's minimum, and the survivor was
    // roomed again instead of getting the single-target attack. One kill under a room
    // spell re-reads the room, and the thinned roster picks the single-target spell.
    [Fact]
    public void RoomSpellKillsOneOfTwo_ReReadsTheRoom_ThenSingleTargetsTheSurvivor()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nebo" };
        h.AddMonster(1, "elder leprechaun");
        h.AddMonster(2, "dark sprite");

        h.Feed("Also here: elder leprechaun, dark sprite.");
        Assert.Equal("hsto", h.LastSent);
        int sentAfterEngage = h.AllSent.Count();

        h.Feed("You gain 12500 experience.");

        Assert.Contains("", h.AllSent.Skip(sentAfterEngage));          // the room is read again

        h.Feed("Also here: dark sprite.");
        Assert.Equal("nebo dark sprite", h.LastSent);
    }

    // The room spell's own damage line, as the caster reads it. With its wording on
    // record the client knows the spell landed this round and counts each kill on
    // its exp line; without it (landingLineRead false) a kill waits for a *Combat
    // Off*, as every kill did before.
    private const string HstoLanding = "A hellish storm of fire and brimstone scorches your foes for 664 damage!";

    private static void KnowTheRoomSpellsLandingLine(Harness h)
        => h.Combat.ResolveAttackSpellMatchers = _ => Lines(
            "A hellish storm of fire and brimstone scorches your foes for {damage} damage!");

    // Four of one monster under a room spell, three of them killed, and the room read
    // again with the survivor attacked by name: where report paradigm-20261010-145330
    // stood when the game printed the *Combat Off* of that attack.
    private static void RoomSpellKillsThreeOfFour_SurvivorAttacked(Harness h, bool landingLineRead)
    {
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "aslt" };
        h.AddMonster(1, "brute zombie");
        if (landingLineRead) KnowTheRoomSpellsLandingLine(h);

        h.Feed("Also here: brute zombie, brute zombie, brute zombie, brute zombie.");
        Assert.Equal("hsto", h.LastSent);
        h.Feed("*Combat Engaged*");

        // The survivor keeps the room spell running, so the kills print no *Combat Off*.
        h.Feed(HstoLanding);
        h.Feed("You gain 16250 experience.");
        h.Feed("You gain 16250 experience.");
        h.Feed("You gain 16250 experience.");

        h.Feed("Also here: brute zombie.");
        Assert.Equal("aslt brute zombie", h.LastSent);
    }

    // Report paradigm-20261010-145330. A new attack sent while another is running
    // prints *Combat Off* ahead of its *Combat Engaged*. Right after the room spell's
    // three exp lines, that Off was read as a kill: the survivor was dropped,
    // three kills were counted against a roster already re-read down to one and the
    // roster emptied, the Combat gate cleared, and the buff cast next had no attack to
    // bring back. The Off answers the echoed attack; nothing died at it. The three
    // kills before it are deaths of their own, counted as they happen when the
    // spell's line is known and left uncounted otherwise.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AttackOnARoomSpellsSurvivor_ItsCombatOffIsNotAKill_AndTheBuffsOffResumesIt(bool landingLineRead)
    {
        using Harness h = new(wireDeathWatcher: true);
        MovementCoordinator coordinator = new(h.Log);
        using CombatStateTracker tracker = new(
            h.Router, coordinator, h.Classifier, h.Monsters, new PlayerState(), () => true, h.Log);
        bool GateHeld() => coordinator.AssertedGates.Contains(MovementCoordinator.CombatGate);
        int emptyRosters = 0;
        h.Classifier.EntitiesObserved += o =>
        {
            if (!o.Entities.Any(e => e.Kind == EntityKind.Monster)) emptyRosters++;
        };

        RoomSpellKillsThreeOfFour_SurvivorAttacked(h, landingLineRead);
        int roomSpellKills = landingLineRead ? 3 : 0;
        Assert.Equal(roomSpellKills, h.DeathEvents.Count);
        Assert.All(h.DeathEvents, d => Assert.Equal("brute zombie", Assert.Single(d.RoomSpellRoster!).Name));
        Assert.True(GateHeld());

        h.FeedEchoed("aslt brute zombie");
        h.Feed("*Combat Off*");
        h.Feed("*Combat Engaged*");

        Assert.Equal(roomSpellKills, h.DeathEvents.Count);       // the Off is no death: no lair-timer kill either
        Assert.Equal("brute zombie", h.Combat.CurrentTarget);    // the survivor is still the target
        Assert.True(h.Classifier.Current is { Entities.Count: 1 });
        Assert.Equal(0, emptyRosters);                           // the room was never read as cleared
        Assert.True(GateHeld());

        // The Buff Watchdog's between-round cast stops the attack; its Off brings it back.
        int sentBeforeBuff = h.Sent.Count;
        h.Cast.NotifyExternalCastSent();
        h.Combat.NoteBetweenRoundCast();
        h.FeedEchoed("blsh");
        h.Feed("*Combat Off*");

        Assert.Equal(sentBeforeBuff + 1, h.Sent.Count);
        Assert.Equal("aslt brute zombie", h.LastSent);
        Assert.Equal(roomSpellKills, h.DeathEvents.Count);
        Assert.Equal(0, emptyRosters);
        Assert.True(GateHeld());

        // Next round the survivor dies: exp, then the kill's own Off with no echo
        // ahead of it. That one is a death, and the room is clear.
        h.Feed("*Combat Engaged*");
        h.Tick();
        h.Feed("You gain 16250 experience.");
        h.Feed("*Combat Off*");

        Assert.Equal(roomSpellKills + 1, h.DeathEvents.Count);
        Assert.Null(h.DeathEvents[^1].RoomSpellRoster);
        Assert.Null(h.Combat.CurrentTarget);
        Assert.False(GateHeld());
    }

    // A typed `break` right after a room spell's kills answers the same way: its Off
    // is the command's, and the monster it was typed at is alive.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TypedBreakAfterARoomSpellsKills_IsNotAKill(bool landingLineRead)
    {
        using Harness h = new(wireDeathWatcher: true);
        RoomSpellKillsThreeOfFour_SurvivorAttacked(h, landingLineRead);
        h.FeedEchoed("aslt brute zombie");
        h.Feed("*Combat Off*");
        h.Feed("*Combat Engaged*");
        int deaths = h.DeathEvents.Count;

        h.FeedEchoed("break");
        h.Feed("*Combat Off*");

        Assert.Equal(deaths, h.DeathEvents.Count);
        Assert.Equal("brute zombie", h.Combat.CurrentTarget);
        Assert.True(h.Classifier.Current is { Entities.Count: 1 });
    }

    // The command's Off need not be the line after its echo: a `break` typed ahead
    // is echoed at once and answered only after the lines of the round it waited for.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TypedAheadBreak_AnsweredAfterTheRoundsLines_IsNotAKill(bool landingLineRead)
    {
        using Harness h = new(wireDeathWatcher: true);
        RoomSpellKillsThreeOfFour_SurvivorAttacked(h, landingLineRead);
        int deaths = h.DeathEvents.Count;

        h.FeedEchoed("break");
        h.Feed("The brute zombie swings at you with its arm!");
        h.Feed("The brute zombie swings at you, but you dodge out of the way!");
        h.Feed("*Combat Off*");

        Assert.Equal(deaths, h.DeathEvents.Count);
        Assert.Equal("brute zombie", h.Combat.CurrentTarget);
        Assert.True(h.Classifier.Current is { Entities.Count: 1 });
    }

    // The roster tally starts again at each read of the room, and still releases a
    // room emptied in two halves of one round: two die, the room is read again with
    // two left, and those two die. The fourth exp line empties the roster and clears
    // the Combat gate, with no wait for the idle watchdog.
    [Fact]
    public void RoomReadAgainBetweenKillsOfOneRound_ReleasesAtTheLastKill()
    {
        using Harness h = new();
        MovementCoordinator coordinator = new(h.Log);
        using CombatStateTracker tracker = new(
            h.Router, coordinator, h.Classifier, h.Monsters, new PlayerState(), () => true, h.Log);
        bool GateHeld() => coordinator.AssertedGates.Contains(MovementCoordinator.CombatGate);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.AddMonster(1, "brute zombie");

        h.Feed("Also here: brute zombie, brute zombie, brute zombie, brute zombie.");
        h.Feed("*Combat Engaged*");
        h.Feed("You gain 16250 experience.");
        h.Feed("You gain 16250 experience.");
        h.Feed("Also here: brute zombie, brute zombie.");
        Assert.True(GateHeld());

        h.Feed("You gain 16250 experience.");
        Assert.True(h.Classifier.Current is { Entities.Count: 2 });   // one of the two still stands
        Assert.True(GateHeld());

        h.Feed("You gain 16250 experience.");
        Assert.True(h.Classifier.Current is { Entities.Count: 0 });
        Assert.False(GateHeld());
    }

    // With a statline the extractor can't split no echo is read. Where the room
    // spell's kills went uncounted (its line not known), the attack's Off keeps its
    // old reading as a death. What it may no longer do is empty the roster: the three
    // kills came before the room was read again, and the one monster that read lists
    // is the one that outlived them.
    [Fact]
    public void UnsplitStatline_KillsUncounted_AttackOffStillReadsAsADeath_ButTheReReadRosterIsKept()
    {
        using Harness h = new(wireDeathWatcher: true);
        MovementCoordinator coordinator = new(h.Log);
        using CombatStateTracker tracker = new(
            h.Router, coordinator, h.Classifier, h.Monsters, new PlayerState(), () => true, h.Log);
        RoomSpellKillsThreeOfFour_SurvivorAttacked(h, landingLineRead: false);

        h.Feed("<724hp 343ma> aslt brute zombie");
        h.Feed("*Combat Off*");
        h.Feed("*Combat Engaged*");

        Assert.Single(h.DeathEvents);
        Assert.True(h.Classifier.Current is { Entities.Count: 1 });
        Assert.Contains(MovementCoordinator.CombatGate, coordinator.AssertedGates);
    }

    // The same unsplit statline with the kills counted on their exp lines: those exp
    // lines are spent, so the attack's Off has nothing to pair with. It is no death,
    // the survivor stays the target, and the buff's Off brings the attack back with
    // no echo read at all.
    [Fact]
    public void UnsplitStatline_KillsCounted_AttackOffIsNoDeath_AndTheBuffsOffResumesIt()
    {
        using Harness h = new(wireDeathWatcher: true);
        RoomSpellKillsThreeOfFour_SurvivorAttacked(h, landingLineRead: true);
        Assert.Equal(3, h.DeathEvents.Count);

        h.Feed("<724hp 343ma> aslt brute zombie");
        h.Feed("*Combat Off*");
        h.Feed("*Combat Engaged*");

        Assert.Equal(3, h.DeathEvents.Count);
        Assert.Equal("brute zombie", h.Combat.CurrentTarget);
        Assert.True(h.Classifier.Current is { Entities.Count: 1 });

        int sentBeforeBuff = h.Sent.Count;
        h.Cast.NotifyExternalCastSent();
        h.Combat.NoteBetweenRoundCast();
        h.Feed("<724hp 343ma> blsh");
        h.Feed("*Combat Off*");

        Assert.Equal(sentBeforeBuff + 1, h.Sent.Count);
        Assert.Equal("aslt brute zombie", h.LastSent);
    }

    // ----- a room spell's kills are deaths on their exp lines -----------------

    // Paradigm's shape for a room emptied by a room spell: a pair per monster, then one
    // *Combat Off*. Four kills are four deaths, each raised at its exp line, and the
    // Off after the last adds none. Until this, the four were one death, on that Off.
    [Fact]
    public void RoomSpellEmptiesTheRoom_OneDeathPerExpLine_AndTheClosingOffAddsNone()
    {
        using Harness h = new(wireDeathWatcher: true);
        MovementCoordinator coordinator = new(h.Log);
        using CombatStateTracker tracker = new(
            h.Router, coordinator, h.Classifier, h.Monsters, new PlayerState(), () => true, h.Log);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.AddMonster(1, "brute zombie");
        KnowTheRoomSpellsLandingLine(h);

        h.Feed("Also here: brute zombie, brute zombie, brute zombie, brute zombie.");
        h.Feed("*Combat Engaged*");
        h.Feed(HstoLanding);
        for (int kill = 1; kill <= 4; kill++)
        {
            h.Feed("The brute zombie keels over like a hewn tree!");
            h.Feed("You gain 16250 experience.");
            Assert.Equal(kill, h.DeathEvents.Count);
        }
        h.Feed("*Combat Off*");

        Assert.Equal(4, h.DeathEvents.Count);
        Assert.All(h.DeathEvents, d =>
        {
            Assert.Equal(16250, d.ExperienceGained);
            Assert.Equal("brute zombie", Assert.Single(d.RoomSpellRoster!).Name);
        });
        Assert.DoesNotContain(MovementCoordinator.CombatGate, coordinator.AssertedGates);
    }

    // A kill that leaves a survivor prints no *Combat Off* on Paradigm, and used to be
    // counted only if some later Off happened to fall within five seconds of it. It is
    // a death when it happens, and so is each kill of the rounds after it.
    [Fact]
    public void RoomSpellKillWithSurvivors_IsADeathAtOnce_WithNoCombatOff()
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.AddMonster(1, "brute zombie");
        h.AddMonster(2, "wandering cadaver");
        KnowTheRoomSpellsLandingLine(h);

        h.Feed("Also here: brute zombie, brute zombie, brute zombie, wandering cadaver.");
        h.Feed("*Combat Engaged*");
        h.Feed(HstoLanding);
        h.Feed("The wandering cadaver falls with a rotten splat.");
        h.Feed("You gain 10250 experience.");

        // One death, and it names nobody: either kind the room listed could be it.
        MonsterDeathEvent first = Assert.Single(h.DeathEvents);
        Assert.Equal(10250, first.ExperienceGained);
        Assert.Equal(new[] { "brute zombie", "wandering cadaver" },
            first.RoomSpellRoster!.Select(k => k.Name).OrderBy(n => n).ToArray());

        // The room is read again, the spell runs on, and next round kills two more.
        h.Feed("Also here: brute zombie, brute zombie, brute zombie.");
        h.Tick();
        h.Feed(HstoLanding);
        h.Feed("You gain 16250 experience.");
        h.Feed("You gain 16250 experience.");

        Assert.Equal(3, h.DeathEvents.Count);
        Assert.Equal("brute zombie", Assert.Single(h.DeathEvents[^1].RoomSpellRoster!).Name);
    }

    // Stock prints a *Combat Off* after every kill, a room spell's included. Each
    // kill is counted once: on its exp line, with the Off after it adding nothing.
    [Fact]
    public void RoomSpellKillsEachFollowedByCombatOff_CountOncePerKill()
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.AddMonster(1, "brute zombie");
        KnowTheRoomSpellsLandingLine(h);

        h.Feed("Also here: brute zombie, brute zombie, brute zombie.");
        h.Feed("*Combat Engaged*");
        h.Feed(HstoLanding);
        h.Feed("You gain 16250 experience.");
        h.Feed("*Combat Off*");
        Assert.Single(h.DeathEvents);
        h.Feed("You gain 16250 experience.");
        h.Feed("*Combat Off*");

        Assert.Equal(2, h.DeathEvents.Count);
    }

    // Never more deaths on exp lines than the room listed: an exp line past that is a
    // monster the roster never held, and is left to the *Combat Off* that follows it,
    // which counts it the old way. Three kills, three deaths, none of them twice.
    [Fact]
    public void MoreExpLinesThanTheRoomListed_TheExtraOneIsLeftToItsCombatOff()
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.AddMonster(1, "brute zombie");
        KnowTheRoomSpellsLandingLine(h);

        h.Feed("Also here: brute zombie, brute zombie.");
        h.Feed("*Combat Engaged*");
        h.Feed(HstoLanding);
        h.Feed("You gain 16250 experience.");
        h.Feed("You gain 16250 experience.");
        Assert.Equal(2, h.DeathEvents.Count);

        h.Feed("You gain 16250 experience.");
        Assert.Equal(2, h.DeathEvents.Count);
        h.Feed("*Combat Off*");

        Assert.Equal(3, h.DeathEvents.Count);
        Assert.NotNull(h.DeathEvents[1].RoomSpellRoster);
        Assert.Null(h.DeathEvents[2].RoomSpellRoster);
    }

    // An exp line in a round our room spell's own line wasn't read in is not taken
    // for that spell's kill. It waits for a *Combat Off*, as before.
    [Fact]
    public void ExpLineWithNoRoomSpellLandingAheadOfIt_IsNotCountedOnTheExpLine()
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.AddMonster(1, "brute zombie");
        KnowTheRoomSpellsLandingLine(h);

        h.Feed("Also here: brute zombie, brute zombie, brute zombie.");
        h.Feed("*Combat Engaged*");
        h.Feed(HstoLanding);          // round 1 lands and kills nothing
        h.Tick();                     // five seconds on: round 2's landing line never shows

        h.Feed("You gain 16250 experience.");
        Assert.Empty(h.DeathEvents);

        h.Feed("*Combat Off*");
        Assert.Null(Assert.Single(h.DeathEvents).RoomSpellRoster);
    }

    // A single-target kill is untouched: no room spell runs, so the exp line raises
    // nothing and the kill's own *Combat Off* is its one death.
    [Fact]
    public void SingleTargetKill_IsStillOneDeathOnItsCombatOff()
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "aslt" };
        h.AddMonster(1, "brute zombie");
        h.Combat.ResolveAttackSpellMatchers = _ => Lines(
            "Pure energy rips through {target}, ravaging them for {damage} damage!");

        h.Feed("Also here: brute zombie.");
        h.Feed("*Combat Engaged*");
        h.Feed("Pure energy rips through brute zombie, ravaging them for 207 damage!");
        h.Feed("You gain 16250 experience.");
        Assert.Empty(h.DeathEvents);
        h.Feed("*Combat Off*");

        Assert.Null(Assert.Single(h.DeathEvents).RoomSpellRoster);
    }

    // The room spell kills whatever is in the room, a neutral we would never attack
    // included, so the count of kills it may claim is every monster the room listed.
    // Held to the ones we would attack, three kills among one hostile and two
    // neutrals were two deaths.
    [Fact]
    public void RoomSpellKillsNeutralsToo_EveryListedMonsterCanBeAKill()
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 1 };
        h.AddMonster(1, "brute zombie");
        h.AddMonster(2, "village guard");
        h.SetOverlay(2, relationship: MonsterRelationship.Neutral);
        KnowTheRoomSpellsLandingLine(h);

        h.Feed("Also here: brute zombie, village guard, village guard.");
        h.Feed("*Combat Engaged*");
        h.Feed(HstoLanding);
        h.Feed("You gain 100 experience.");
        h.Feed("You gain 100 experience.");
        h.Feed("You gain 100 experience.");

        Assert.Equal(3, h.DeathEvents.Count);
        Assert.All(h.DeathEvents, d => Assert.NotNull(d.RoomSpellRoster));
    }

    // With a room spell announced, a line that starts "You " and names the monster
    // the round is anchored to is not the spell landing: a monster's own hit on us
    // reads that way. An exp line behind one is not counted as the spell's kill.
    [Theory]
    [InlineData("You are burned by the brute zombie for 12 damage!")]
    [InlineData("You dodge the brute zombie's swing!")]
    public void LineNamingTheAnchorMonster_IsNotTheRoomSpellLanding(string line)
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.AddMonster(1, "brute zombie");
        KnowTheRoomSpellsLandingLine(h);

        h.Feed("Also here: brute zombie, brute zombie, brute zombie.");
        h.Feed("*Combat Engaged*");
        h.Feed(HstoLanding);          // round 1 lands and kills nothing
        h.Tick();                     // five seconds on: that line is stale

        h.Feed(line);
        h.Feed("You gain 16250 experience.");

        Assert.Empty(h.DeathEvents);
        Assert.Contains("left to its *Combat Off*", h.Combat.RoomSpellKillSummary);
    }

    // An exp line with no roster of the room on record is left to its *Combat Off*,
    // and the bug report says so.
    [Fact]
    public void ExpLineWithNoRosterRead_IsLeftToItsCombatOff_AndSaysWhy()
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.AddMonster(1, "brute zombie");
        KnowTheRoomSpellsLandingLine(h);

        h.Feed("Also here: brute zombie, brute zombie.");
        h.Feed("*Combat Engaged*");
        h.Feed(HstoLanding);
        h.Classifier.NoteGameLeft();
        h.Feed("You gain 16250 experience.");

        Assert.Empty(h.DeathEvents);
        Assert.Contains("no roster of the room read", h.Combat.RoomSpellKillSummary);
    }

    // ----- Stock: the *Combat Off* after a room spell's kill ------------------

    // Stock prints *Combat Off* after every kill, a room spell's included, and
    // queues the spell's next round all the same. Read as the spell ending, that
    // Off had the next combat line or round tick cast it again, breaking the one
    // still running. On Stock it is left alone; elsewhere it is read as before.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CombatOffAfterARoomSpellsKill_OnStockTheSpellIsLeftRunning(bool stock)
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Combat.SetStockRealmProbe(() => stock);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.AddMonster(1, "brute zombie");
        KnowTheRoomSpellsLandingLine(h);

        h.Feed("Also here: brute zombie, brute zombie, brute zombie, brute zombie.");
        Assert.Equal("hsto", h.LastSent);
        h.Feed("*Combat Engaged*");
        h.Tick();
        h.Feed(HstoLanding);
        h.Feed("The brute zombie keels over like a hewn tree!");
        h.Feed("You gain 16250 experience.");
        h.Feed("*Combat Off*");

        Assert.Single(h.DeathEvents);
        Assert.Equal(!stock, h.Combat.CombatOff);

        // Three are left and the room is read again: the spell still covers them.
        h.Feed("Also here: brute zombie, brute zombie, brute zombie.");
        // The re-attack after a *Combat Off* stands behind two real-time guards (an
        // attack sent in the last 1.5 s, a kill in the last 2 s), so the next round
        // is let come in real time.
        Thread.Sleep(2100);
        h.Tick();
        h.Feed("The brute zombie swings at you with its arm!");

        Assert.Equal(stock ? 1 : 2, h.AllSent.Count(s => s == "hsto"));
    }

    // Only that Off. One a between-round cast draws on Stock, with no kill behind it,
    // stops the spell there as anywhere, and the attack is brought back.
    [Fact]
    public void OnStock_ACombatOffWithNoRoomSpellKillBehindIt_IsStillAnInterrupt()
    {
        using Harness h = new(wireDeathWatcher: true);
        h.Combat.SetStockRealmProbe(() => true);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hsto", MinEnemies = 2 };
        h.AddMonster(1, "brute zombie");
        KnowTheRoomSpellsLandingLine(h);

        h.Feed("Also here: brute zombie, brute zombie, brute zombie.");
        h.Feed("*Combat Engaged*");
        h.Tick();
        h.Feed(HstoLanding);
        h.Feed("You gain 16250 experience.");
        h.Feed("*Combat Off*");
        Assert.False(h.Combat.CombatOff);

        h.Feed("Also here: brute zombie, brute zombie.");
        h.Cast.NotifyExternalCastSent();
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");

        Assert.Equal(2, h.AllSent.Count(s => s == "hsto"));
    }

    [Fact]
    public void PreAttackDebuff_DefersToHigherPrioritySurvivalCast()
    {
        using Harness h = new();
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        // Simulate the in-between director holding a higher-priority survival cast
        // ready: its Evaluate fires and reports "heal". The pre-attack pass must let
        // that win by the Spells+Ailments priority and NOT pre-empt it with the
        // debuff this engage — the attack resumes after the survival cast's Off.
        h.Combat.SetInBetweenEvaluator(() => "heal");
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.DoesNotContain("curse", h.AllSent);   // debuff deferred to priority
        Assert.DoesNotContain("blast", h.AllSent);   // attack deferred to the resume
    }

    // A listener keeping the round's between-round cast for the debuff (a loop's
    // lair-entry hold) is told when an engage has none to cast, and which debuff it
    // was when one goes out. Not when the debuff is only put off behind another cast.
    [Fact]
    public void Engage_TellsWhetherADebuffWentOutAheadOfTheAttack()
    {
        using Harness none = new();
        int noDebuff = 0;
        none.Combat.SetNoDebuffOnEngageSink(() => noDebuff++);
        none.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        none.AddMonster(1, "giant rat");
        none.Feed("Also here: giant rat.");
        Assert.Equal(1, noDebuff);                   // no debuff set: the attack goes with nothing ahead of it
        Assert.Contains("blast", none.AllSent);

        using Harness sent = new();
        List<string> debuffs = new();
        noDebuff = 0;
        sent.Combat.SetNoDebuffOnEngageSink(() => noDebuff++);
        sent.Combat.SetBetweenRoundSlotMarker(debuffs.Add);
        sent.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        sent.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        sent.AddMonster(1, "giant rat");
        sent.Feed("Also here: giant rat.");
        Assert.Equal(new[] { "curse" }, debuffs);    // sent, and named
        Assert.Equal(0, noDebuff);

        using Harness putOff = new();
        noDebuff = 0;
        putOff.Combat.SetNoDebuffOnEngageSink(() => noDebuff++);
        putOff.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        putOff.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        putOff.Combat.SetInBetweenEvaluator(() => "heal");   // a heal takes the cast: the debuff is still coming
        putOff.AddMonster(1, "giant rat");
        putOff.Feed("Also here: giant rat.");
        Assert.Equal(0, noDebuff);
    }

    // ----- backstab gate -----------------------------------------------

    [Fact]
    public void BackstabPending_SuppressesSpell_SendsBackstab()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        // Skip-backstab-if-room-spelling off here so the opener beats the spell — the
        // skip-on case is covered by CombatSpellChooserTests (paradigm-20260908-210406).
        h.Settings.SkipBackstabIfMultiAttack = false;
        h.Sneaking = true;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("bs giant rat", h.LastSent);
        Assert.DoesNotContain("blast", h.AllSent);
    }

    // The sneak-maintenance defer gate consults this so a between-round buff / cure
    // (which breaks sneak) is held through an engaging fight until the backstab
    // opener has fired — a buff first would forfeit the surprise round.
    [Fact]
    public void IsBackstabOpenerPending_TrueWhileOwed_FalseOnceConsumedOrBackstabOff()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Sneaking = true;
        h.AddMonster(1, "giant rat");

        // Auto-combat off: the room is observed but no opener dispatches, so it's
        // still owed — the defer gate should hold maintenance casts here.
        h.AutoCombatEnabled = false;
        h.Feed("Also here: giant rat.");
        Assert.True(h.Combat.IsBackstabOpenerPending());

        // Backstab disabled entirely → never pending, even sneaking with a mob here.
        h.Settings.DoBackstab = false;
        Assert.False(h.Combat.IsBackstabOpenerPending());

        // Re-enable, let auto-combat fire the opener, and it's spent → not pending,
        // so maintenance casting resumes for the rest of the fight.
        h.Settings.DoBackstab = true;
        h.AutoCombatEnabled = true;
        h.Feed("Also here: giant rat.");
        Assert.Equal("bs giant rat", h.LastSent);
        Assert.False(h.Combat.IsBackstabOpenerPending());
    }

    // ----- self-defense engage (report paradigm-20260921-132800) --------

    // A Friend NPC is never auto-engaged, but once it turns hostile and swings at us
    // the engine fights back in self-defense.
    [Fact]
    public void SelfDefense_FriendAttacksUs_EngineEngagesInSelfDefense()
    {
        using Harness h = new();
        h.AddMonster(1, "big gravedigger");
        h.SetOverlay(1, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: big gravedigger.");                 // Friend → not auto-engaged
        Assert.DoesNotContain(h.AllSent, s => s.Contains("gravedigger"));

        h.Feed("The big gravedigger hits you for 12 damage!"); // it attacks us

        Assert.Equal("a big gravedigger", h.LastSent);          // self-defense → engaged with the weapon
    }

    // A neutral we never provoked, swinging at us (a miss line still names it), is
    // engaged too.
    [Fact]
    public void SelfDefense_NeutralSwingsAtUs_EngineEngages()
    {
        using Harness h = new();
        h.AddMonster(1, "grumpy badger");
        h.SetOverlay(1, relationship: MonsterRelationship.Neutral);

        h.Feed("Also here: grumpy badger.");                   // passive neutral → left alone
        Assert.DoesNotContain(h.AllSent, s => s.Contains("badger"));

        h.FeedMiss("The grumpy badger claws at you!");         // a miss still names the attacker

        Assert.Equal("a grumpy badger", h.LastSent);
    }

    // Report paradigm-20261012-132421: `greet old man` was answered with "The old man
    // looks at you curiously, …", which has a miss's shape, and the client bashed a
    // Friend. The game prints that in the default colour and a miss in dark cyan.
    [Fact]
    public void SelfDefense_AnNpcsWordsShapedLikeAMiss_AreNoAttack_ItsRealMissIs()
    {
        using Harness h = new();
        h.AddMonster(1, "old man");
        h.SetOverlay(1, relationship: MonsterRelationship.Friend);
        h.Feed("Also here: old man.");

        h.Feed("The old man looks at you curiously, as if not expecting to see you here.");
        Assert.DoesNotContain(h.AllSent, s => s.Contains("old man"));

        h.FeedMiss("The old man kicks at you, but misses!");
        Assert.Equal("a old man", h.LastSent);
    }

    // A Flee monster is run from on sight, so while a run is under way or on its
    // way it is not fought, whatever the Hangup watch is doing.
    [Theory]
    [InlineData(false)]   // the run is coming
    [InlineData(true)]    // and no hang-up is, which is not its business
    [InlineData(null)]    // nothing wired
    public void SelfDefense_FleeMonsterAttacksUs_NotEngagedWhileARunIsComing(bool? hangupWatchOff)
    {
        using Harness h = new();
        if (hangupWatchOff is { } off)
            h.Combat.SetNoAnswerComingProbe(rel => rel == MonsterRelationship.Hangup && off);
        h.AddMonster(1, "fierce dragon");
        h.SetOverlay(1, relationship: MonsterRelationship.Flee);

        h.Feed("Also here: fierce dragon.");
        h.Feed("The fierce dragon hits you for 50 damage!");

        Assert.DoesNotContain(h.AllSent, s => s.Contains("dragon"));
    }

    // With no run coming for it (nothing to run along, no way out, a follower, the
    // flee's switches off), a Flee monster that attacks is fought back (user,
    // 2026-10-09). Like a Neutral, it is still never picked on sight.
    [Fact]
    public void SelfDefense_FleeMonsterAttacksUs_FoughtBackWhenNoRunIsComing()
    {
        using Harness h = new();
        h.Combat.SetNoAnswerComingProbe(rel => rel == MonsterRelationship.Flee);
        h.AddMonster(1, "fierce dragon");
        h.SetOverlay(1, relationship: MonsterRelationship.Flee);

        h.Feed("Also here: fierce dragon.");
        Assert.DoesNotContain(h.AllSent, s => s.Contains("dragon"));

        h.Feed("The fierce dragon hits you for 50 damage!");

        Assert.Equal("a fierce dragon", h.LastSent);
    }

    // The Auto-All master switch turns Auto-Combat off with every other auto, and
    // self-defence is gated on it for every relationship: a Flee monster no run is
    // coming for is not fought back then, exactly as a Neutral is not.
    [Theory]
    [InlineData(MonsterRelationship.Flee)]
    [InlineData(MonsterRelationship.Neutral)]
    public void SelfDefense_MasterSwitchOff_NothingIsFoughtBack(MonsterRelationship relationship)
    {
        using Harness h = new();
        h.AutoCombatEnabled = false;
        h.Combat.SetNoAnswerComingProbe(_ => true);
        h.AddMonster(1, "fierce dragon");
        h.SetOverlay(1, relationship: relationship);

        h.Feed("Also here: fierce dragon.");
        h.Feed("The fierce dragon hits you for 50 damage!");

        Assert.DoesNotContain(h.AllSent, s => s.Contains("dragon"));
    }

    // The watcher and the engine together, as the app wires them. A Hangup and a
    // Flee monster in the room with Disable Hangups on (user, 2026-10-09): with a
    // walk or loop running the Flee monster is run from; idle, neither is touched
    // on sight and both are fought back when they attack.
    [Theory]
    [InlineData(true, "fierce dragon", false)]    // running from it, not fighting it
    [InlineData(true, "ogre", true)]
    [InlineData(false, "fierce dragon", true)]
    [InlineData(false, "ogre", true)]
    public void SelfDefense_HangupAndFleeMonster_WithDisableHangupsOn(bool engineRunning, string attacker, bool foughtBack)
    {
        using Harness h = new();
        h.AddMonster(1, "fierce dragon");
        h.AddMonster(2, "ogre");
        h.SetOverlay(1, relationship: MonsterRelationship.Flee);
        h.SetOverlay(2, relationship: MonsterRelationship.Hangup);
        List<string> runs = new();
        using MonsterRelationshipWatcher watcher = new(
            h.Classifier,
            resolveOverlay: number => h.Overlays.TryGetValue(number, out MonsterOverlay? overlay) ? overlay : new MonsterOverlay(),
            hangUp: _ => Game.Health.EscapeOutcome.HangupsDisabled,
            flee: (reason, _) =>
            {
                runs.Add(reason);
                return engineRunning ? Game.Health.FleeOutcome.Started : Game.Health.FleeOutcome.NoEngine;
            },
            fleeInFlight: () => engineRunning,
            masterSwitchOff: () => false,
            hangupsDisabled: () => true,
            pvpHandles: _ => false,
            atBoardMenu: () => false,
            describeRoom: () => "in Town Square",
            schedule: (_, _) => { });
        h.Combat.SetNoAnswerComingProbe(watcher.NoAnswerComing);

        h.Feed("Also here: fierce dragon, ogre.");

        Assert.Equal("fierce dragon (#1) is here, relationship Flee", Assert.Single(runs));
        // Neither is picked on sight.
        Assert.DoesNotContain(h.AllSent, s => s.Contains("dragon") || s.Contains("ogre"));

        h.Feed($"The {attacker} hits you for 50 damage!");

        if (foughtBack)
            Assert.Equal($"a {attacker}", h.LastSent);
        else
            Assert.DoesNotContain(h.AllSent, s => s.Contains(attacker));
    }

    // A Hangup monster is answered with a hang-up on sight, so it is not fought:
    // the hang-up is on its way.
    [Theory]
    [InlineData(false)]   // the watch is on
    [InlineData(null)]    // nothing wired
    public void SelfDefense_HangupMonsterAttacksUs_NotEngagedWhileTheWatchIsOn(bool? hangupWatchOff)
    {
        using Harness h = new();
        if (hangupWatchOff is { } off)
            h.Combat.SetNoAnswerComingProbe(rel => rel == MonsterRelationship.Hangup && off);
        h.AddMonster(1, "fierce dragon");
        h.SetOverlay(1, relationship: MonsterRelationship.Hangup);

        h.Feed("Also here: fierce dragon.");
        h.Feed("The fierce dragon hits you for 50 damage!");

        Assert.DoesNotContain(h.AllSent, s => s.Contains("dragon"));
    }

    // With no hang-up coming for it (Disable Hangups on, or the minute after a
    // reconnect), a Hangup monster that attacks is fought back (user, 2026-10-09).
    // Like a Neutral, it is still never picked on sight: only once it attacks.
    [Fact]
    public void SelfDefense_HangupMonsterAttacksUs_FoughtBackWhileTheWatchIsOff()
    {
        using Harness h = new();
        h.Combat.SetNoAnswerComingProbe(rel => rel == MonsterRelationship.Hangup);
        h.AddMonster(1, "fierce dragon");
        h.SetOverlay(1, relationship: MonsterRelationship.Hangup);

        h.Feed("Also here: fierce dragon.");
        Assert.DoesNotContain(h.AllSent, s => s.Contains("dragon"));

        h.Feed("The fierce dragon hits you for 50 damage!");

        Assert.Equal("a fierce dragon", h.LastSent);
    }

    // Auto-combat off (or a do-not-attack / combat-suppressed room, same _isEnabled
    // gate) → no self-defense; we don't fight where the user said not to.
    [Fact]
    public void SelfDefense_AutoCombatOff_DoesNotEngage()
    {
        using Harness h = new();
        h.AutoCombatEnabled = false;
        h.AddMonster(1, "big gravedigger");
        h.SetOverlay(1, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: big gravedigger.");
        h.Feed("The big gravedigger hits you for 12 damage!");

        Assert.DoesNotContain(h.AllSent, s => s.Contains("gravedigger"));
    }

    // During a plain walk-to (travel), self-defense stands down — an evil character
    // running through a guarded town keeps running rather than stopping to fight.
    // Looping / Auto-Lair / idle still defend (the gate is false there).
    [Fact]
    public void SelfDefense_DuringWalkToTravel_DoesNotEngage()
    {
        using Harness h = new();
        h.Combat.SetSelfDefenseTravelGate(() => true);   // a plain walk-to is driving
        h.AddMonster(1, "silvermere guard");
        h.SetOverlay(1, relationship: MonsterRelationship.Neutral);

        h.Feed("Also here: silvermere guard.");
        h.Feed("The silvermere guard hits you for 20 damage!");

        Assert.DoesNotContain(h.AllSent, s => s.Contains("guard"));
    }

    // ----- attack-last re-fire re-issues our real action --------------

    [Fact]
    public void AttackTiming_SpellBuild_RefiresTheSpell_NotWeapon()
    {
        // Re-announcing a combat spell costs no mana — mana is spent once per round when
        // it fires (GAME_MECHANICS) — so under attack-last a party member's commit must
        // re-announce our SPELL so it lands last, never revert to the physical weapon
        // (the old bug: the re-fire hard-sent NormalAttackCommand, dropping spell mode).
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        h.Feed("*Combat Engaged*");   // the game answers our attack
        Assert.Equal("nuke giant rat", h.LastSent);        // engaged, spell mode
        int before = h.Sent.Count;

        h.Feed("Bob moves to attack giant rat.");          // party member commits
        Assert.True(h.Sent.Count > before);                // re-fired
        Assert.Equal("nuke giant rat", h.LastSent);        // re-announced the SPELL
        Assert.DoesNotContain("a giant rat", h.AllSent);   // never the weapon
    }

    [Fact]
    public void AttackTiming_AoeBuild_RefiresTheRoomSpell_NotWeapon()
    {
        // A room spell re-announces bare on the re-fire, not the weapon.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        h.Feed("*Combat Engaged*");   // the game answers our attack
        Assert.Equal("blast", h.LastSent);                 // AoE, cast bare
        int before = h.Sent.Count;

        h.Feed("Bob moves to attack giant rat.");
        Assert.True(h.Sent.Count > before);
        Assert.Equal("blast", h.LastSent);                 // re-announced bare room spell
        Assert.DoesNotContain("a giant rat", h.AllSent);
    }

    [Fact]
    public void AttackTiming_PartyRoomAttack_RefiresOurRoomSpell()
    {
        // "someone rooms, we're also rooming": their room announce names the whole room
        // ("everyone in the room"), not our mob, but it's a room-wide commit — so our
        // room spell must re-announce to land last. The old equality guard dropped it.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        h.Feed("*Combat Engaged*");   // the game answers our attack
        Assert.Equal("blast", h.LastSent);
        int before = h.Sent.Count;

        h.Feed("Client moves to attack everyone in the room.");   // party room commit
        Assert.True(h.Sent.Count > before);                       // we room last
        Assert.Equal("blast", h.LastSent);
    }

    [Fact]
    public void AttackTiming_ParadigmPoisedRoomer_RefiresOurRoomSpell()
    {
        // Paradigm shows "<player> is poised to assault the room!" for an already-active
        // roomer as we enter — also a room commit, so we room last behind them.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        h.Feed("*Combat Engaged*");   // the game answers our attack
        Assert.Equal("blast", h.LastSent);
        int before = h.Sent.Count;

        h.Feed("Client is poised to assault the room!");
        Assert.True(h.Sent.Count > before);
        Assert.Equal("blast", h.LastSent);
    }

    [Fact]
    public void AttackTiming_WeaponBuild_StillRefires()
    {
        // A weapon build (caster wired, no attack spell) still re-fires its swing on a
        // party member's commit so our *Combat Engaged* re-lands after theirs.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        h.Feed("*Combat Engaged*");   // the game answers our attack
        Assert.Equal("a giant rat", h.LastSent);
        int before = h.Sent.Count;

        h.Feed("Bob moves to attack giant rat.");
        Assert.True(h.Sent.Count > before);                // re-fired the swing
        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- attack-not-last: commit once, behind the first mover --------

    private static void AddParty(Harness h, params string[] names)
    {
        h.Party.IsInParty = true;
        foreach (string n in names) h.Party.Members.Add(new PartyMember { Name = n });
    }

    [Fact]
    public void AttackNotLast_HoldsThenFiresOnceOnFirstPartyAnnounce()
    {
        // Party of 3+: hold our pick on entry, commit once right after the FIRST party
        // announce, and never re-fire when others announce after us.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        AddParty(h, "MudPlay", "Client", "Buddy");
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.DoesNotContain(h.AllSent, s => s.Contains("giant rat"));   // HELD on entry

        h.Feed("Client moves to attack giant rat.");                     // first commit
        Assert.Equal("a giant rat", h.LastSent);                         // fired once
        int after = h.Sent.Count;

        h.Feed("Buddy moves to attack giant rat.");                      // later commit
        Assert.Equal(after, h.Sent.Count);                               // did NOT re-fire
    }

    [Fact]
    public void AttackNotLast_SpellBuild_HoldsThenCastsOnce()
    {
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        AddParty(h, "MudPlay", "Client", "Buddy");
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.DoesNotContain(h.AllSent, s => s.Contains("nuke"));       // held

        h.Feed("Client moves to attack giant rat.");
        Assert.Equal("nuke giant rat", h.LastSent);                     // cast once
        int after = h.Sent.Count;

        h.Feed("Buddy moves to attack giant rat.");
        Assert.Equal(after, h.Sent.Count);                              // no re-fire
    }

    [Fact]
    public void AttackNotLast_ReleasedByRoomAttackCommit()
    {
        // A party member's room attack is a valid "first commit" too.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        AddParty(h, "MudPlay", "Client", "Buddy");
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.DoesNotContain(h.AllSent, s => s.Contains("giant rat"));  // held

        h.Feed("Client moves to attack everyone in the room.");
        Assert.Equal("a giant rat", h.LastSent);                        // committed once
    }

    [Fact]
    public void AttackNotLast_PartyOfTwo_BehavesLikeDefault()
    {
        // 2 or fewer → no hold, attack immediately on entry like Default.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        AddParty(h, "MudPlay", "Client");
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);                        // not held
    }

    [Fact]
    public void AttackNotLast_Solo_BehavesLikeDefault()
    {
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);                        // not held
    }

    [Fact]
    public void AttackNotLast_NoAnnounceForARound_FallsBackToOwnPick()
    {
        // Never freeze: if no party member announces within a round, engage our pick.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        AddParty(h, "MudPlay", "Client", "Buddy");
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.DoesNotContain(h.AllSent, s => s.Contains("giant rat"));  // held

        h.Tick();                                                       // a round, no announce
        Assert.Equal("a giant rat", h.LastSent);                        // fell back to own pick
    }

    // The party walks in a beat after us on every step, so the room the hold reads is
    // an arrival when the release comes. Report paradigm-20261017-050150: a Mystic on
    // AttackNotLast never sent `pu` through a whole walk; each release and each
    // fallback went into the arrival settle, and the settle's own engage held again.
    private static void WalkIntoTunnelHorrorBehindTheParty(Harness h)
    {
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        h.Settings.NormalAttackCommand = "pu";
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.AddMonster(635, "tunnel horror");

        h.Feed("A tunnel horror stomps in from the south.");
        h.Feed("Also here: tunnel horror.");
        FeedPlayerLine(h, "Cidir walks into the room from the northwest.");
        FeedPlayerLine(h, "Thresh walks into the room from the northwest.");
        h.FireSettle();
    }

    // A player's name comes in the red the game paints players.
    private static void FeedPlayerLine(Harness h, string line)
    {
        CellAttributes red = CellAttributes.Default with { Foreground = TerminalColor.Indexed(9) };
        h.Router.Dispatch(new LineExtractor.EmittedLine(
            line, Enumerable.Repeat(red, line.Length).ToArray(),
            DateTimeOffset.UtcNow, IsPromptLine: false));
    }

    [Fact]
    public void AttackNotLast_PartyWalkedInBehindUs_FirstAnnounceStillEngages()
    {
        using Harness h = new();
        using RoomEntryWatcher entry = new(h.Router, h.Classifier, h.Log);
        WalkIntoTunnelHorrorBehindTheParty(h);
        Assert.DoesNotContain(h.AllSent, s => s.Contains("tunnel horror"));   // held

        h.Feed("Cidir moves to attack tunnel horror.");                       // first commit
        h.FireSettle();
        Assert.Equal("pu tunnel horror", h.LastSent);
        int after = h.Sent.Count;

        h.Feed("Thresh moves to attack tunnel horror.");
        h.FireSettle();
        Assert.Equal(after, h.Sent.Count);                                    // once
    }

    [Fact]
    public void AttackNotLast_FirstAnnounceInTheSameFlushAsTheWalkIn_Engages()
    {
        // The capture's 05:01:43 room: the walk-ins and the first attack came in one
        // flush, inside the settle the walk-ins armed, and waited a round for the fallback.
        using Harness h = new();
        using RoomEntryWatcher entry = new(h.Router, h.Classifier, h.Log);
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        h.Settings.NormalAttackCommand = "pu";
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.AddMonster(635, "tunnel horror");

        h.Feed("Also here: tunnel horror.");
        FeedPlayerLine(h, "Cidir walks into the room from the southwest.");
        FeedPlayerLine(h, "Thresh walks into the room from the southwest.");
        h.Feed("Cidir moves to attack tunnel horror.");
        h.FireSettle();

        Assert.Equal("pu tunnel horror", h.LastSent);
    }

    [Fact]
    public void FollowLeader_MemberWalkedInBehindUs_NoAnnounceStillFallsBack()
    {
        // The follow hold's round fallback goes through the same settle when a member
        // walks in behind us, and its engage must not hold again.
        using Harness h = new();
        using RoomEntryWatcher entry = new(h.Router, h.Classifier, h.Log);
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Settings.NormalAttackCommand = "pu";
        h.Players.RecordObservation("Cidir", "Priest", null, null, null, null, null, DateTime.UtcNow);
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.Party.LeaderName = "Cidir NineteenTr";
        h.AddMonster(635, "tunnel horror");
        h.AddMonster(636, "giant skeleton");

        h.Feed("Also here: tunnel horror, giant skeleton, Cidir.");
        FeedPlayerLine(h, "Thresh walks into the room from the northwest.");
        h.FireSettle();
        Assert.DoesNotContain(h.AllSent, s => s.StartsWith("pu "));          // held for Cidir

        h.Tick();                                                             // a round, no announce
        h.FireSettle();
        Assert.Equal("pu tunnel horror", h.LastSent);
    }

    // ----- attack-not-last + follow-leader: one wait for both -----------

    // Leader Cidir stands in the room with two monsters, so both holds apply on entry.
    private static void EnterUnderNotLastAndFollowLeader(Harness h,
        string alsoHere = "Also here: tunnel horror, giant skeleton, Cidir.",
        AttackTiming timing = AttackTiming.AttackNotLast)
    {
        h.Settings.AttackTiming = timing;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Settings.NormalAttackCommand = "pu";
        h.Players.RecordObservation("Cidir", "Priest", null, null, null, null, null, DateTime.UtcNow);
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.Party.LeaderName = "Cidir NineteenTr";
        h.AddMonster(635, "tunnel horror");
        h.AddMonster(636, "giant skeleton");
        h.Feed(alsoHere);
    }

    private static int Punches(Harness h) => h.AllSent.Count(s => s.StartsWith("pu "));

    // The holds' in-round deadline, wired as AppServices wires it, with the projected
    // round a test value and the one-shot held until the test fires it.
    private sealed class HoldDeadline
    {
        public TimeSpan? ToRound { get; set; } = TimeSpan.FromSeconds(3);
        public TimeSpan? Wait { get; private set; }
        public int Scheduled { get; private set; }
        private Action? _due;
        public HoldDeadline(Harness h)
            => h.Combat.SetHoldDeadline(() => ToRound, (wait, due) => { Wait = wait; _due = due; Scheduled++; });
        public void Fire() => _due?.Invoke();
    }

    [Fact]
    public void AttackNotLast_NoAnnounce_AttacksOnceBeforeTheProjectedRound()
    {
        using Harness h = new();
        HoldDeadline deadline = new(h);
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        h.Settings.NormalAttackCommand = "pu";
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.AddMonster(635, "tunnel horror");

        h.Feed("Also here: tunnel horror.");
        Assert.Equal(0, Punches(h));
        Assert.Equal(TimeSpan.FromSeconds(2), deadline.Wait);   // a second ahead of the round

        deadline.Fire();                                        // still inside the round
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Feed("*Combat Engaged*");
        h.Tick();
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void NotLastAndFollowLeader_NoAnnounce_AttacksOnceBeforeTheProjectedRound()
    {
        using Harness h = new();
        HoldDeadline deadline = new(h);
        EnterUnderNotLastAndFollowLeader(h);
        Assert.Equal(TimeSpan.FromSeconds(2), deadline.Wait);

        deadline.Fire();
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Feed("*Combat Engaged*");
        h.Tick();
        Assert.Equal(1, Punches(h));
    }

    // The production order of a step: the move goes out (NoteMoveSent), the new room's
    // "Also here:" is read, and only then does the tracker confirm the room on its exits
    // line (NoteRoomLeft).
    private static void StepIntoRoom(Harness h, string alsoHere)
    {
        DateTimeOffset sentAt = DateTimeOffset.Now;
        h.Combat.NoteMoveSent();
        h.Feed(alsoHere);
        h.Combat.NoteRoomLeft(sentAt);
    }

    private static void FightInRoomAThenKill(Harness h)
    {
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        h.Settings.NormalAttackCommand = "pu";
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.AddMonster(635, "tunnel horror");
        h.AddMonster(636, "giant skeleton");
        h.Feed("Also here: tunnel horror.");
        h.Feed("Thresh moves to attack tunnel horror.");
        h.Feed("*Combat Engaged*");
        h.Feed("You gain 100 experience.");
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void AttackNotLast_TrackerConfirmsAfterTheRoster_DeadlineStillAttacks()
    {
        using Harness h = new();
        HoldDeadline deadline = new(h);
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        h.Settings.NormalAttackCommand = "pu";
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.AddMonster(635, "tunnel horror");

        StepIntoRoom(h, "Also here: tunnel horror.");
        Assert.Equal(0, Punches(h));

        deadline.Fire();
        Assert.Equal("pu tunnel horror", h.LastSent);
    }

    [Fact]
    public void AttackNotLast_FightInTheRoomWeLeft_NextRoomStillHolds()
    {
        using Harness h = new();
        FightInRoomAThenKill(h);

        StepIntoRoom(h, "Also here: giant skeleton.");
        Assert.Equal(1, Punches(h));                              // held for the party
    }

    [Fact]
    public void AttackNotLast_LateDamageLineFromTheRoomWeLeft_NextRoomStillHolds()
    {
        using Harness h = new();
        bool inFlight = false;
        h.Combat.SetMoveInFlightProbe(() => inFlight);
        FightInRoomAThenKill(h);

        DateTimeOffset sentAt = DateTimeOffset.Now;
        h.Combat.NoteMoveSent();
        inFlight = true;
        h.Feed("Thresh stabs tunnel horror for 50 damage!");     // still room A's
        inFlight = false;
        h.Feed("Also here: giant skeleton.");
        h.Combat.NoteRoomLeft(sentAt);
        Assert.Equal(1, Punches(h));
    }

    private const string DragLine = " -- Following your Party leader north -- ";

    private static void HoldInRoomA(Harness h)
    {
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        h.Settings.NormalAttackCommand = "pu";
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.AddMonster(635, "tunnel horror");
        h.AddMonster(636, "giant skeleton");
        h.Feed("Also here: tunnel horror.");
        Assert.Equal(0, Punches(h));
    }

    [Fact]
    public void AttackNotLast_DraggedAfterAFightInRoomA_NextRoomStillHolds()
    {
        // A follower sends no move: the game drags it and prints the drag line.
        using Harness h = new();
        FightInRoomAThenKill(h);

        DateTimeOffset draggedAt = DateTimeOffset.Now;
        h.Feed(DragLine);
        h.Feed("Also here: giant skeleton.");
        h.Combat.NoteRoomLeft(draggedAt);
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void AttackNotLast_DraggedWhileHeld_NextRoomsDeadlineStillAttacks()
    {
        using Harness h = new();
        HoldDeadline deadline = new(h);
        HoldInRoomA(h);

        DateTimeOffset draggedAt = DateTimeOffset.Now;
        h.Feed(DragLine);
        h.Feed("Also here: giant skeleton.");
        h.Combat.NoteRoomLeft(draggedAt);

        deadline.Fire();
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void AttackNotLast_OldRoomRedisplayAfterOurMove_NextRoomsDeadlineStillAttacks()
    {
        using Harness h = new();
        HoldDeadline deadline = new(h);
        HoldInRoomA(h);

        DateTimeOffset sentAt = DateTimeOffset.Now;
        h.Combat.NoteMoveSent();
        h.Feed("Also here: tunnel horror.");                     // room A, shown again
        h.Feed("Also here: giant skeleton.");                    // room B
        h.Combat.NoteRoomLeft(sentAt);

        deadline.Fire();
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void AttackNotLast_MoveRefusedWhileHeld_DeadlineStillAttacksOnce()
    {
        // Our move went out and the game refused it: no new room is shown, and the
        // hold in the room we're still in must keep a deadline.
        using Harness h = new();
        HoldDeadline deadline = new(h);
        HoldInRoomA(h);

        h.Combat.NoteMoveSent();

        deadline.Fire();
        Assert.Equal("pu tunnel horror", h.LastSent);
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void AttackNotLast_SameRoomRedisplay_RoundPlaced_KeepsTheDeadline()
    {
        using Harness h = new();
        HoldDeadline deadline = new(h);
        HoldInRoomA(h);
        Assert.Equal(1, deadline.Scheduled);

        deadline.ToRound = TimeSpan.FromSeconds(1.5);           // the same round, read later
        h.Feed("Also here: tunnel horror.");
        Assert.Equal(1, deadline.Scheduled);                     // not pushed back
        Assert.Equal(TimeSpan.FromSeconds(2), deadline.Wait);
    }

    [Fact]
    public void AttackNotLast_SameRoomRedisplay_NoRoundPlaced_KeepsTheEarlierDueTime()
    {
        using Harness h = new();
        HoldDeadline deadline = new(h) { ToRound = null };
        HoldInRoomA(h);
        Assert.Equal(1, deadline.Scheduled);

        h.Feed("Also here: tunnel horror.");
        h.Feed("Also here: tunnel horror.");
        Assert.Equal(1, deadline.Scheduled);                     // the 1 s isn't restarted
    }

    [Fact]
    public void AttackNotLast_AnnounceBeforeTheDeadline_DeadlineSendsNothing()
    {
        using Harness h = new();
        HoldDeadline deadline = new(h);
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        h.Settings.NormalAttackCommand = "pu";
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.AddMonster(635, "tunnel horror");

        h.Feed("Also here: tunnel horror.");
        h.Feed("Thresh moves to attack tunnel horror.");
        Assert.Equal(1, Punches(h));

        deadline.Fire();
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void AttackNotLast_NoRoundPlaced_WaitsOneSecond()
    {
        using Harness h = new();
        HoldDeadline deadline = new(h) { ToRound = null };
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.AddMonster(635, "tunnel horror");

        h.Feed("Also here: tunnel horror.");
        Assert.Equal(TimeSpan.FromSeconds(1), deadline.Wait);
    }

    [Fact]
    public void NotLastAndFollowLeader_MemberWalksInBehindUs_LeaderAttack_FollowsTheLeader()
    {
        // The walk-in's observation used to clear the follow half of the hold, so the
        // leader's attack then released the not-last half onto our own pick.
        using Harness h = new();
        using RoomEntryWatcher entry = new(h.Router, h.Classifier, h.Log);
        EnterUnderNotLastAndFollowLeader(h);
        FeedPlayerLine(h, "Thresh walks into the room from the northwest.");

        h.Feed("Cidir moves to attack giant skeleton.");
        h.FireSettle();
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void NotLastAndFollowLeader_LeaderWalksInBehindUs_LeaderAttack_FollowsTheLeader()
    {
        // Only the not-last half was latched (the leader wasn't in the room yet).
        using Harness h = new();
        using RoomEntryWatcher entry = new(h.Router, h.Classifier, h.Log);
        EnterUnderNotLastAndFollowLeader(h, "Also here: tunnel horror, giant skeleton.");
        FeedPlayerLine(h, "Cidir walks into the room from the northwest.");

        h.Feed("Cidir moves to attack giant skeleton.");
        h.FireSettle();
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void AttackNotLast_PartyDamageLineWithNoAnnounce_ReleasesAtOnce()
    {
        // The party is fighting; we never read its announce.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        h.Settings.NormalAttackCommand = "pu";
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.AddMonster(635, "tunnel horror");
        h.AddMonster(636, "giant skeleton");
        h.Feed("Also here: tunnel horror, giant skeleton.");
        Assert.Equal(0, Punches(h));

        h.Feed("Thresh stabs giant skeleton for 100 damage!");
        Assert.Equal("pu tunnel horror", h.LastSent);
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void AttackNotLast_StrangersDamageLine_DoesNotRelease()
    {
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackNotLast;
        h.Settings.NormalAttackCommand = "pu";
        h.Players.RecordObservation("Bob", "Warrior", null, null, null, null, null, DateTime.UtcNow);
        AddParty(h, "MudPlay", "Cidir NineteenTr", "Thresh II");
        h.AddMonster(635, "tunnel horror");
        h.Feed("Also here: tunnel horror, Bob.");

        h.Feed("Bob stabs tunnel horror for 100 damage!");
        Assert.Equal(0, Punches(h));
    }

    [Fact]
    public void FollowLeader_LeadersDamageLineWithNoAnnounce_TakesTheirTarget()
    {
        using Harness h = new();
        EnterUnderNotLastAndFollowLeader(h, timing: AttackTiming.Default);
        Assert.Equal(0, Punches(h));

        h.Feed("Cidir smashes giant skeleton for 80 damage!");
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void NotLastAndFollowLeader_SpellBuild_MemberFirstThenLeader_SwitchesAtOnce()
    {
        // A re-announce before the round fires costs nothing and the last one counts
        // (GAME_MECHANICS "Attack announce lines and round commits"), so a caster moves
        // onto the leader's target at once, as a fighter does.
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        EnterUnderNotLastAndFollowLeader(h);

        h.Feed("Thresh moves to attack giant skeleton.");
        Assert.Equal("nuke tunnel horror", h.LastSent);
        h.Feed("*Combat Engaged*");

        h.Feed("Cidir moves to attack giant skeleton.");
        Assert.Equal("nuke giant skeleton", h.LastSent);
    }

    [Fact]
    public void NotLastAndFollowLeader_NoAnnounces_AttacksOnceAfterTheRound()
    {
        // The two holds' fallbacks used to re-latch each other, so with nobody
        // announcing we never attacked.
        using Harness h = new();
        EnterUnderNotLastAndFollowLeader(h);
        Assert.Equal(0, Punches(h));

        h.Tick();
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Feed("*Combat Engaged*");
        h.Tick();
        h.Tick();
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void NotLastAndFollowLeader_LeaderAnnounces_FollowsTheLeadersTarget()
    {
        using Harness h = new();
        EnterUnderNotLastAndFollowLeader(h);

        h.Feed("Cidir moves to attack giant skeleton.");
        Assert.Equal("pu giant skeleton", h.LastSent);
        h.Feed("*Combat Engaged*");
        h.Tick();
        Assert.Equal(1, Punches(h));
    }

    [Fact]
    public void NotLastAndFollowLeader_MemberAnnouncesFirst_AttacksOwnPickThenFollowsTheLeader()
    {
        // Attack Order sets when: the first party announce ends the wait, so we attack
        // right behind it on our own pick. Target Priority still sets who: the
        // leader's later announce moves us onto the leader's target.
        using Harness h = new();
        EnterUnderNotLastAndFollowLeader(h);

        h.Feed("Thresh moves to attack giant skeleton.");
        Assert.Equal("pu tunnel horror", h.LastSent);
        h.Feed("*Combat Engaged*");

        h.Feed("Cidir moves to attack giant skeleton.");
        Assert.Equal("pu giant skeleton", h.LastSent);
        Assert.Equal(2, Punches(h));
    }

    [Fact]
    public void AttackNotLast_PartyWalkedInBehindUs_NoAnnounceStillFallsBack()
    {
        using Harness h = new();
        using RoomEntryWatcher entry = new(h.Router, h.Classifier, h.Log);
        WalkIntoTunnelHorrorBehindTheParty(h);
        Assert.DoesNotContain(h.AllSent, s => s.Contains("tunnel horror"));   // held

        h.Tick();                                                             // a round, no announce
        h.FireSettle();
        Assert.Equal("pu tunnel horror", h.LastSent);
    }

    // ----- room clear resets the chooser bookkeeping -------------------

    [Fact]
    public void RoomCleared_ResetsCastCap_NextRoomReCasts()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell =
            new CombatSpellSlot { SpellName = "blast", MinEnemies = 1, MaxCastsPerRoom = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");      // room 1 — cast (cap 1 reached)
        Assert.Equal("blast", h.LastSent);

        h.Feed("Also here: Bob.");            // room cleared → chooser reset
        h.Tick();                             // round passes (clears cast cooldown)
        h.Feed("Also here: giant rat.");      // room 2 — cap reset, casts again

        Assert.Equal(2, h.AllSent.Count(s => s == "blast"));
    }

    // ----- damage-immunity fallback (CS-c) -----------------------------

    [Fact]
    public void SpellNoEffect_CascadesPrimaryToAlternateToWeapon()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "firebolt" };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "icebolt" };
        h.AddMonster(1, "acid slime");

        h.Feed("Also here: acid slime.");                 // primary attack spell
        Assert.Equal("firebolt acid slime", h.LastSent);

        // A round passes (firebolt repeats server-side; its result comes back next
        // round). The immunity line then swaps to the alternate SPELL *on the same
        // line* — no extra round burned (report paradigm-20260809-162350). This is
        // the fix: previously the spell branch idled until the NEXT tick, so the
        // assert here needed a Tick() after the no-effect line.
        h.Tick();
        h.Feed("Your spell has no effect on acid slime."); // firebolt immune → instant icebolt
        Assert.Equal("icebolt acid slime", h.LastSent);

        // Next round, the alternate is also immune → the cascade reaches the weapon.
        h.Tick();
        h.Feed("Your spell has no effect on acid slime."); // icebolt immune → weapon
        Assert.Equal("a acid slime", h.LastSent);
    }

    // Report paradigm-20260827-081223: the "no effect" reply arrives in the SAME
    // server burst as the primary cast (< 500ms later), not a round later. Without
    // bypassing CastCoordinator's MinRecastInterval, the alternate SPELL hit the
    // 500ms burst guard and deferred a full round (~3-5s late). The re-decide now
    // passes bypassRecastInterval, so the alternate fires THIS round with no defer.
    [Fact]
    public void SpellNoEffect_SameBurstAsPrimary_SwapsThisRound_NoRecastIntervalDefer()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "firebolt" };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "icebolt" };
        h.AddMonster(1, "acid slime");

        List<(CastFailureReason Reason, string Detail, string? Spell)> failures = new();
        h.Cast.CastFailed += (reason, detail, spell) => failures.Add((reason, detail, spell));

        h.Feed("Also here: acid slime.");                    // primary firebolt just sent
        Assert.Equal("firebolt acid slime", h.LastSent);
        int afterPrimary = h.Sent.Count;

        // No Tick / no clock advance — the no-effect reply lands in the same burst,
        // well within the 500ms MinRecastInterval window.
        h.Feed("Your spell has no effect on acid slime.");

        Assert.Equal("icebolt acid slime", h.LastSent);      // alternate fired THIS round
        Assert.Equal(afterPrimary + 1, h.Sent.Count);        // not deferred to the next tick
        Assert.DoesNotContain(failures, f => f.Detail == "recast-interval");
    }

    [Fact]
    public void SpellNoEffect_SameRoundBurst_SwapsOnceNotStraightToWeapon()
    {
        // The attack casts several times per round, so an immune target draws a
        // burst of "no effect" lines. The first swaps primary→alternate; the rest
        // of the burst (same round, no tick) must be ignored — else the alternate
        // we just chose is itself mis-marked immune and the cascade skips straight
        // to the weapon, never actually trying the alternate spell.
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "firebolt" };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "icebolt" };
        h.AddMonster(1, "acid slime");

        h.Feed("Also here: acid slime.");
        h.Tick();
        h.Feed("Your spell has no effect on acid slime.");   // firebolt immune → icebolt
        Assert.Equal("icebolt acid slime", h.LastSent);

        h.Feed("Your spell has no effect on acid slime.");   // leftover burst line, same round
        Assert.Equal("icebolt acid slime", h.LastSent);      // still icebolt — NOT "a acid slime"
        Assert.DoesNotContain("a acid slime", h.AllSent);
    }

    [Fact]
    public void SpellNoEffect_MultiAttack_NotGated_KeepsCasting()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "acid slime");

        h.Feed("Also here: acid slime.");                 // multi-attack room spell
        Assert.Equal("blast", h.LastSent);

        // One immune mob doesn't mean the room spell isn't damaging the
        // rest — multi-attack is never marked immune.
        h.Feed("Your spell has no effect on acid slime.");
        h.Tick();
        Assert.Equal("blast", h.LastSent);
        Assert.DoesNotContain("a acid slime", h.AllSent);
    }

    // ----- Alternating action orders: every-round command driving -------
    // The Alternate* orders can't lean on the server auto-repeat — the desired
    // action flips each round, so the engine re-issues a command every round. The
    // heartbeat drives the flip in BOTH the spell-phase and (critically) the
    // weapon-phase rounds, where the fixed-order heartbeat would return early.

    [Fact]
    public void AlternateSpellPhysical_OpensOnSpell_ThenFlipsEachRound()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.AlternateSpellPhysical;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // Engage — round 0 = spell phase.
        h.Feed("Also here: giant rat.");
        Assert.Equal("harm giant rat", h.LastSent);

        // Round 1 — physical (the weapon-phase flip the fixed heartbeat can't do).
        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);

        // Round 2 — back to the spell.
        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);

        // Round 3 — physical again.
        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void AlternatePhysicalSpell_OpensOnSwing_ThenFlipsEachRound()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.AlternatePhysicalSpell;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // Engage — round 0 = physical phase.
        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);

        // Round 1 — spell.
        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);

        // Round 2 — physical again.
        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);
    }

    // TickEngine.CombatTickElapsed fires on every hit/miss line (250ms-debounced),
    // not once per true ~5s round — a monster's counter-swing line landing a beat
    // after the player's own can trip a SECOND tick within the same round. That
    // must not flip the alternation phase twice (the reported "switched too fast,
    // moved to attack then instantly wanted the spell").
    [Fact]
    public void AlternatePhysicalSpell_RapidExtraTick_DoesNotDoubleAdvancePhase()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.AlternatePhysicalSpell;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // Engage — round 0 = physical phase.
        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);

        // A genuine round boundary — round 1 flips to spell.
        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);
        int afterRound1 = h.Sent.Count;

        // A stray extra tick lands a beat later — e.g. the mob's own counter-swing
        // line independently tripping CombatTickElapsed — well short of a real
        // round (the clock has NOT advanced). Must not flip the phase back.
        h.Cast.OnCombatTick();
        h.Combat.OnCombatTick();
        Assert.Equal(afterRound1, h.Sent.Count);

        // The next GENUINE round correctly flips back to physical.
        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void AlternateSpellPhysical_SpellPhaseUnaffordable_SwingsThatRound()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.AlternateSpellPhysical;
        h.Settings.SpellManaThresholdMode = ThresholdMode.Absolute;
        h.Settings.NormalAttackSpell =
            new CombatSpellSlot { SpellName = "harm", MinEnemies = 1, MinManaPerCast = 30 };
        h.Ma = 10;                                   // below the spell's reserve
        h.AddMonster(1, "giant rat");

        // Engage on a spell phase, but mana is too low — fall back to the swing.
        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- Round-cycle action order: configurable-length phases ---------
    // Unlike the fixed Alternate* orders above, a phase can span many rounds.
    // Continuing rounds within a phase must NOT resend — physical leans on the
    // server's own auto-repeat, and spell leans on the existing heartbeat's
    // same-decision dedup. Only a genuine phase boundary forces a fresh command,
    // and only the physical→spell edge needs an explicit push (the spell→physical
    // edge already falls out of the ordinary heartbeat re-deciding every tick).

    [Fact]
    public void CustomRoundCycle_PhysicalThenSpellsTillDeath_SwitchesOnceNoRepeats()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.CustomRoundCycle;
        h.Settings.CycleRoundsPhysical = 2;
        h.Settings.CycleRoundsSpell = 0;   // "spells till death" — never switches back
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // Round 0 (engage) — physical phase.
        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);
        int afterEngage = h.Sent.Count;

        // Round 1 — still physical (1 < 2 rounds configured). No resend: the
        // server's own auto-repeat is carrying the swing.
        h.Tick();
        Assert.Equal(afterEngage, h.Sent.Count);

        // Round 2 — phase boundary: forced switch to the spell (nothing else
        // would ever interrupt an otherwise-passive physical auto-repeat).
        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);
        int afterSwitch = h.Sent.Count;

        // Rounds 3–4 — spells till death: stay on the cast, no re-announce
        // (re-sending a spell the server is already repeating is the
        // double-cast / corpse-cast bug the ordinary heartbeat dedup prevents).
        h.Tick();
        Assert.Equal(afterSwitch, h.Sent.Count);
        h.Tick();
        Assert.Equal(afterSwitch, h.Sent.Count);
    }

    [Fact]
    public void CustomRoundCycle_OneOneMatchesFixedAlternation()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.CustomRoundCycle;
        h.Settings.CycleRoundsPhysical = 1;
        h.Settings.CycleRoundsSpell = 1;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);       // round 0 — physical (default open)

        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);    // round 1 — spell

        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);       // round 2 — physical again

        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);    // round 3 — spell again
    }

    [Fact]
    public void CustomRoundCycle_StartOnSpell_ThenPhysicalTillDeath()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.CustomRoundCycle;
        h.Settings.CycleStartOnSpell = true;
        h.Settings.CycleRoundsSpell = 1;
        h.Settings.CycleRoundsPhysical = 0;   // physical forever once reached
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // Round 0 (engage) — opens on the spell phase.
        h.Feed("Also here: giant rat.");
        Assert.Equal("harm giant rat", h.LastSent);

        // Round 1 — phase boundary: switches to physical.
        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);
        int afterSwitch = h.Sent.Count;

        // Rounds 2–3 — physical till death: no resend.
        h.Tick();
        Assert.Equal(afterSwitch, h.Sent.Count);
        h.Tick();
        Assert.Equal(afterSwitch, h.Sent.Count);
    }

    // ----- 0-mana: stand down only from MANA-COSTING actions ------------
    // ----- per-monster PHYSICAL override (replaces the weapon command) -----
    // The physical override swaps the weapon command on a round the engine already
    // chose physical — it does NOT force physical or suppress the spell rungs.

    [Fact]
    public void PhysicalOverride_ReplacesWeaponCommand_OnPhysicalRound()
    {
        using Harness h = new();
        h.Settings.NormalAttackCommand = "attack";
        h.AddMonster(1, "large zombie");
        h.Overlays[1] = new MonsterOverlay { OverridePhysicalCommand = "bash" };

        h.Feed("Also here: large zombie.");

        // No attack spell configured → the engine landed on physical; the per-monster
        // physical override replaces the configured "attack" command.
        Assert.Equal("bash large zombie", h.LastSent);
    }

    [Fact]
    public void PhysicalOverride_DoesNotForcePhysical_SpellStillCastsOnSpellRound()
    {
        using Harness h = new();
        h.Settings.NormalAttackCommand = "attack";
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm" };
        h.AddMonster(1, "large zombie");
        h.Overlays[1] = new MonsterOverlay { OverridePhysicalCommand = "bash" };
        h.Ma = 100;

        h.Feed("Also here: large zombie.");

        // SpellsFirst (default) + a configured normal spell → the spell rung fires;
        // the physical override does not force a physical round.
        Assert.Equal("harm large zombie", h.LastSent);
        Assert.DoesNotContain(h.AllSent, s => s == "bash large zombie");
    }

    [Fact]
    public void OutOfMana_SpellCascade_NotAttempted_SwingsWeapon()
    {
        // A configured attack spell must not be attempted at 0 mana even if its
        // MinManaPerCast is unset (0 = no floor, not "free").
        using Harness h = new();
        h.Settings.NormalAttackCommand = "attack";
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm" };
        h.AddMonster(1, "giant rat");
        h.Ma = 0;

        h.Feed("Also here: giant rat.");

        Assert.Equal("attack giant rat", h.LastSent);
    }

    // ----- user-engaged passive-neutral takeover (report paradigm-20260814) -----

    // A passive neutral (Neutral relationship, not KillOnSight) is left alone by the
    // engine — until the user hand-attacks it, which turns it hostile. The manual attack
    // marks that instance so the engine takes over killing it.
    [Fact]
    public void PassiveNeutral_LeftAlone_ManualAttackMarksItEngaged()
    {
        using Harness h = new(wireCaster: false);
        h.AddMonster(1, "townsperson");
        h.SetOverlay(1, relationship: MonsterRelationship.Neutral);   // passive — no KillOnSight

        h.Feed("Also here: townsperson.");
        Assert.False(h.Combat.IsUserEngagedInstance("townsperson"));
        Assert.DoesNotContain(h.AllSent, s => s.Contains("townsperson"));   // engine ignores it

        h.Combat.NoteAttackCommandObserved("a", "townsperson");             // user swings
        Assert.True(h.Combat.IsUserEngagedInstance("townsperson"));          // engine takes over
    }

    // The user types an abbreviated target to engage fast ("a towns" for "townsperson");
    // the mark must resolve the room instance from that prefix.
    [Fact]
    public void ManualAttack_AbbreviatedTarget_ResolvesAndMarks()
    {
        using Harness h = new(wireCaster: false);
        h.AddMonster(1, "townsperson");
        h.SetOverlay(1, relationship: MonsterRelationship.Neutral);
        h.Feed("Also here: townsperson.");

        h.Combat.NoteAttackCommandObserved("a", "towns");
        Assert.True(h.Combat.IsUserEngagedInstance("townsperson"));
    }

    // An enemy needs no per-instance override — it already engages on its own — so a
    // manual swing at one must NOT add it to the user-engaged set (which would only ever
    // matter for a passive neutral).
    [Fact]
    public void ManualAttack_OnEnemy_DoesNotAddInstanceOverride()
    {
        using Harness h = new(wireCaster: false);
        h.AutoCombatEnabled = false;   // engine won't swing → no echo-claim to consume the manual verb
        h.AddMonster(1, "giant rat");  // default relationship = Enemy
        h.Feed("Also here: giant rat.");

        h.Combat.NoteAttackCommandObserved("a", "giant rat");
        Assert.False(h.Combat.IsUserEngagedInstance("giant rat"));
    }

    // The takeover is per-room: once the marked neutral is gone (killed, or we changed
    // rooms), a full-roster observation prunes it so the flag can't leak onto a freshly
    // arrived same-named mob.
    [Fact]
    public void UserEngagedNeutral_ClearedWhenItLeavesRoom()
    {
        using Harness h = new(wireCaster: false);
        h.AddMonster(1, "townsperson");
        h.SetOverlay(1, relationship: MonsterRelationship.Neutral);
        h.AddMonster(2, "giant rat");
        h.Feed("Also here: townsperson.");
        h.Combat.NoteAttackCommandObserved("a", "townsperson");
        Assert.True(h.Combat.IsUserEngagedInstance("townsperson"));

        h.Feed("Also here: giant rat.");   // full-roster observe — townsperson gone
        Assert.False(h.Combat.IsUserEngagedInstance("townsperson"));
    }

    // Report paradigm-20260824-012300: AutoCombat toggled off mid-fight left
    // _castingSpellTarget / _spellAttackOwed latched to a target no longer being
    // fought ("between-round cast noted (manual) — resume armed
    // (spellTarget=small blue dragon hatchling)" with the dragon long gone).
    // The stale _castingSpellTarget leaves the attack resume waiting on a *Combat
    // Off* that never comes, stranding the attack. Disabling AutoCombat must drop
    // the whole cascade, not just CurrentTarget. (SpellAttackOwed is asserted
    // here as part of the cascade state; it's diagnostic now, not a cast gate.)
    [Fact]
    public void AutoCombatDisabled_ClearsStaleAttackSpellCascade()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "agon", MinEnemies = 0 };
        h.AddMonster(1, "small blue dragon hatchling");

        h.Feed("Also here: small blue dragon hatchling.");
        Assert.Equal("agon small blue dragon hatchling", h.LastSent);
        h.Combat.NoteBetweenRoundCast();   // a survival cast armed the round-owed latch
        Assert.True(h.Combat.Snapshot().SpellAttackOwed);
        Assert.Equal("small blue dragon hatchling", h.Combat.Snapshot().CastingSpellTarget);

        h.AutoCombatEnabled = false;
        h.Feed("Also here: small blue dragon hatchling.");   // next observation, combat off

        Assert.False(h.Combat.Snapshot().SpellAttackOwed);
        Assert.Null(h.Combat.Snapshot().CastingSpellTarget);
        Assert.Null(h.Combat.Snapshot().CurrentTarget);
    }

    // Same root cause, the death path: the corpse/respawn room has nothing to do
    // with whatever spell was mid-flight when the character died, but nothing
    // previously told CombatManager that (report paradigm-20260824-012300 also
    // documents CastingDirector buff timers surviving death for the same reason —
    // a full server-side state reset with no matching client-side reset).
    [Fact]
    public void OnPlayerDeath_ClearsStaleAttackSpellCascade()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "agon", MinEnemies = 0 };
        h.AddMonster(1, "small blue dragon hatchling");

        h.Feed("Also here: small blue dragon hatchling.");
        h.Combat.NoteBetweenRoundCast();
        Assert.True(h.Combat.Snapshot().SpellAttackOwed);

        h.Combat.OnPlayerDeath();

        Assert.False(h.Combat.Snapshot().SpellAttackOwed);
        Assert.Null(h.Combat.Snapshot().CastingSpellTarget);
        Assert.Null(h.Combat.Snapshot().CurrentTarget);
    }

    // Same root cause again, the disconnect/reconnect path (report
    // paradigm-20260827-203548): a between-round survival cast (a self-buff)
    // armed the round-owed latch, then the connection dropped before the
    // server's *Combat Off* for that cast ever arrived — the ONLY event the
    // resume logic listens for. The monster kept fighting server-side through
    // the link-death; on reconnect the CombatGate re-detects it fresh via
    // room-entry, but with the stale target/latch still set, the character
    // never resumed attacking and just stood there taking hits. Same fix
    // shape as OnPlayerDeath above.
    [Fact]
    public void OnDisconnected_ClearsStaleAttackSpellCascade()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "agon", MinEnemies = 0 };
        h.AddMonster(1, "small blue dragon hatchling");

        h.Feed("Also here: small blue dragon hatchling.");
        h.Combat.NoteBetweenRoundCast();   // self-buff mid-fight; *Combat Off* never lands — connection drops
        Assert.True(h.Combat.Snapshot().SpellAttackOwed);

        h.Combat.OnDisconnected();

        Assert.False(h.Combat.Snapshot().SpellAttackOwed);
        Assert.Null(h.Combat.Snapshot().CastingSpellTarget);
        Assert.Null(h.Combat.Snapshot().CurrentTarget);
    }

    // Report paradigm-20260916-033047: a between-round self-buff (Energy=0 —
    // prfl / vlwa / etc.) landing the instant a fresh monster arrives used to
    // hand the newcomer a free round. GAME_MECHANICS.md's confirmed rule (2026-08-16)
    // is that a between-round spell "rides between the round's main action, so
    // one is free each round on top of your attack" — a 0-energy buff and a real
    // attack spell (500+ energy) are independent slots server-side, never
    // competing for the same round. CastCoordinator's MinRecastInterval is a
    // pure client-side burst guard against CastingDirector re-firing its OWN
    // casts within one frame (its own doc comment: combat's dispatch is already
    // paced once-per-round, "so it can't burst") — it was never meant to model
    // the server's real per-round rule, but a fresh engage's DispatchRoundAction
    // call didn't bypass it, so an unrelated buff moments earlier deferred the
    // attack to the next tick regardless — a full free round for whatever just
    // walked in. bypassRecastInterval on the fresh-engage dispatch (mirroring the
    // already-fixed debuff-then-attack case) fires the attack immediately instead.
    [Fact]
    public void EngageAfterBetweenRoundBuff_AttacksImmediately_NotBlocked()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "turn", MinEnemies = 0 };
        h.AddMonster(1, "shade");

        // A prior kill's *Combat Off* leaves _combatOff true, same as production.
        h.Feed("*Combat Off*");

        // A between-round self-buff just went out — stamps CastCoordinator's
        // recast-interval clock, same as it would moments before a fresh arrival.
        Assert.True(h.Cast.TryCast("vlwa"));
        Assert.Equal("vlwa", h.LastSent);

        // A fresh engage arrives immediately after — well within MinRecastInterval
        // (500ms). The attack must fire the SAME tick, not defer to the next round.
        h.Feed("Also here: shade.");

        Assert.Equal("turn shade", h.LastSent);
        Assert.False(h.Combat.CombatOff);
        Assert.False(h.Combat.Snapshot().SpellAttackOwed);
    }

    // Follow-up report paradigm-20260824-235607 reproduced the same visible stall
    // after the _combatOff fix, but from a fresh process. With no prior hit/miss,
    // TickEngine.LastCombatTick was null. The initial attack was genuinely blocked
    // (CastCoordinator's failure latch — a fizzle, unlike the now-fixed burst-guard
    // collision covered by EngageAfterBetweenRoundBuff_AttacksImmediately_NotBlocked
    // above), no attack reached the server, and the shade's armour-block wording
    // ("reaches out for you") matched none of TickEngine's generic patterns. The
    // timer fallback therefore never started, so the "retry next tick" promised by
    // the earlier fix had no tick to run on. A blocked attack must seed the real
    // fallback and reserve that next round from CastingDirector.
    [Fact]
    public void FreshSession_BlockedEngage_SeedsTickFallbackAndReservesRetryRound()
    {
        using Harness h = new();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        using TickEngine tick = new(h.Router, () => now);
        h.Combat.SetCombatTickAnchor(tick.EnsureCombatTickAnchor);
        // Production subscription order: CastCoordinator clears the spent round,
        // then CombatManager retries the attack. CastingDirector sits between them
        // and observes SpellAttackOwed=true, asserted below.
        tick.CombatTickElapsed += () => h.Cast.OnCombatTick();
        tick.CombatTickElapsed += h.Combat.OnCombatTick;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "turn", MinEnemies = 0 };
        h.AddMonster(1, "shade");

        Assert.Null(tick.LastCombatTick); // brand-new process: no prior combat cadence
        // A genuine server-side failure (fizzle) latches CastCoordinator's block —
        // the ONE thing bypassRecastInterval must never punch through.
        h.Feed("You attempt to cast heal, but fail.");

        h.Feed("Also here: shade.");

        Assert.Empty(h.Sent);                    // attack genuinely blocked, nothing sent
        Assert.False(h.Combat.CombatOff);
        Assert.True(h.Combat.Snapshot().SpellAttackOwed); // no second buff may steal the retry
        Assert.NotNull(tick.LastCombatTick);      // production timer fallback can now fire

        // The seeded timer reaches the projected next round without any recognized
        // combat line—the exact production event that was missing in the report.
        now += TickEngine.CombatTickInterval;
        tick.PollTimersForTests();

        Assert.Equal("turn shade", h.LastSent);
        Assert.False(h.Combat.Snapshot().SpellAttackOwed);
    }
}
