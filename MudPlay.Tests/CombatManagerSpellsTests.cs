using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
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

        public bool AutoCombatEnabled { get; set; } = true;
        public bool AutoNukeEnabled { get; set; } = true;
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

        public Harness(bool wireCaster = true, bool deferPost = false)
        {
            _deferPost = deferPost;
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
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
                log: Log);
            Combat.SetWireSender(b => Sent.Add(b));
            Combat.SetClock(() => _clock);
            // Production counts MaxCasts off RoundDamageTracker.RoundCount; mirror it.
            Combat.ReadRoundCount = () => _roundCount;
            Combat.SetBackstabHooks(() => Sneaking, n => SeeHidden.Contains(n));
            Combat.SetAutoNukeGate(() => AutoNukeEnabled);
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

    // Corpse-cast on the arrival-settle path (report paradigm-20260916-141344: nebo at a
    // dead muckworm). A straggler kill's death line lands INSIDE the settle window — its
    // roster resync (a forced re-display) is still in flight when the window elapses, so
    // the accumulated roster can still name the just-killed mob (or a fresh same-named
    // arrival not yet confirmed in-room). Engaging then re-picks that name and casts at a
    // target the server rejects a beat later. The settle-elapsed path must defer to the
    // resync — force the re-display, cast nothing — instead of racing it.
    [Fact]
    public void ArrivalSettle_KillLandsInsideWindow_DefersToResync_NoCorpseCast()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nebo", MinEnemies = 1 };
        h.AddMonster(1, "muckworm");

        h.Arrive("muckworm");                       // arms the settle hold (no target yet)
        Assert.Equal(string.Empty, h.LastSent);

        h.Combat.NoteMonsterDied("muckworm");       // a kill's death line lands inside the window
        int sentBeforeSettle = h.Sent.Count;

        h.FireSettle();                             // window elapsed — a kill happened mid-window

        Assert.DoesNotContain("nebo muckworm", h.AllSent);   // never corpse-cast
        Assert.Equal(sentBeforeSettle + 1, h.Sent.Count);    // exactly the CR resync went out
        Assert.Equal(string.Empty, h.LastSent);              // = the bare CR (trimmed to empty)
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
        h.Combat.ResolveAttackSpellMatcher = code => string.Equals(code, "soul", StringComparison.OrdinalIgnoreCase)
            ? CasterMessageMatcher.TryCreate("Spiritual power strikes {target} for {damage} damage!")
            : null;

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
    // (CombatManager.IsSpellAttackOwed) is what actually prevents the collision
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

        // Next round (tick) clears the override; a fresh interrupt resumes.
        h.Tick();
        int afterTick = h.Sent.Count;
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");
        Assert.True(h.Sent.Count > afterTick);        // resumes — override cleared at the tick
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
    // the whole cascade, not just CurrentTarget. (IsSpellAttackOwed is asserted
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
        Assert.True(h.Combat.IsSpellAttackOwed);
        Assert.Equal("small blue dragon hatchling", h.Combat.Snapshot().CastingSpellTarget);

        h.AutoCombatEnabled = false;
        h.Feed("Also here: small blue dragon hatchling.");   // next observation, combat off

        Assert.False(h.Combat.IsSpellAttackOwed);
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
        Assert.True(h.Combat.IsSpellAttackOwed);

        h.Combat.OnPlayerDeath();

        Assert.False(h.Combat.IsSpellAttackOwed);
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
        Assert.True(h.Combat.IsSpellAttackOwed);

        h.Combat.OnDisconnected();

        Assert.False(h.Combat.IsSpellAttackOwed);
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
        Assert.False(h.Combat.IsSpellAttackOwed);
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
        // and observes IsSpellAttackOwed=true, asserted below.
        tick.CombatTickElapsed += h.Cast.OnCombatTick;
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
        Assert.True(h.Combat.IsSpellAttackOwed); // no second buff may steal the retry
        Assert.NotNull(tick.LastCombatTick);      // production timer fallback can now fire

        // The seeded timer reaches the projected next round without any recognized
        // combat line—the exact production event that was missing in the report.
        now += TickEngine.CombatTickInterval;
        tick.PollTimersForTests();

        Assert.Equal("turn shade", h.LastSent);
        Assert.False(h.Combat.IsSpellAttackOwed);
    }
}
