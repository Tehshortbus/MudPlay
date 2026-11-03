using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Game.Health;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Simulation;

// Plays a character around a loop in simulated time and measures what Session
// Stats measures live. Every decision the client makes goes through the client's
// own code — CombatSpellChooser picks the round's attack and debuff (with
// CombatSpellGates' per-monster overrides and blocks), SelfHealPicker the
// between-round heal — and every outcome comes from the combat math Monster Intel
// already uses (hit chance, spell damage after resists, cast chance, monster
// energy, debuff deltas). Rolls are seeded, so a run is repeatable; several seeds
// give the spread.
//
// Time runs in 50 ms steps. Combat resolves on the global 5 s round, one
// between-round cast per round (GAME_MECHANICS "Combat round (5s) and the
// between-round cast cycle"); regen ticks on the realm's cadence (RealmRegenProfile).
// Reaching 0 HP ends the run as a death; the Health tab's hang-up trigger ends it
// as a hang-up.
//
// A character that opens with a backstab walks sneaked. By the user's call
// (2026-09-30, report paradigm-20260930-114254) a sneak always holds on a move and
// is re-taken the moment the room it leaves is clear; see-hidden breaks it, and so
// does anything on GAME_MECHANICS "Sneaking — commands, equip order, and the sneak
// state machine" → "What ends a sneak" the simulation does (an attack, a cast, a
// rest). Arriving sneaked at a fight, the first round is the
// surprise stab (GAME_MECHANICS "Backstab"); every round after it is the usual
// attack choice.
public static class LoopSimulator
{
    // Which simulation a saved result came from. Area rankings are kept on disk
    // between sessions (AreaRankingFiles) and a kept one is only listed while this is
    // the number it was made with: a ranking by an older simulation is not comparable
    // with a new one and would be shown as current.
    //
    // BUMP THIS whenever a simulated result could change for the same character, lap
    // and game data: a change here, in SimCharacterBuilder, SimLevelProjection,
    // SimFreeze or SimProc, in RouteExpResolver.ResolveSimLap, or in the combat,
    // healing and casting code a simulated round calls.
    //
    // SimulatorVersionPinTests fails when the result of either of two fixed runs
    // moves, to say so. Those runs start from a character and a lap written out by
    // hand, so they catch a change to what a simulated hour does and NOT a change to
    // what it is given. Nothing fails, and this still has to be bumped by whoever
    // makes the change, for:
    //   - SimCharacterBuilder: how a stat screen, worn gear and the spellbook become
    //     the character played;
    //   - SimLevelProjection: that character moved to another level;
    //   - RouteExpResolver.ResolveSimLap: how waypoints become the rooms of a lap,
    //     with their lairs and the steps between them;
    //   - AreaTours: which lairs make an area and the order its tour takes them in;
    //   - the walk pace (AppServices.LoopSimulationWalkSeconds and the travel models
    //     behind it), which sets how many laps fit in the hour.
    public const int SimulatorVersion = 3;

    public static LoopSimSummary RunMany(
        SimCharacter character, IReadOnlyList<SimRoom> lap, SimWorld world,
        double secondsPerStep, double hours, int runs, CancellationToken cancel = default)
    {
        var results = new List<LoopSimRun>(Math.Max(0, runs));
        for (int seed = 1; seed <= runs; seed++)
        {
            cancel.ThrowIfCancellationRequested();
            results.Add(Run(character, lap, world, secondsPerStep, hours, seed, cancel));
        }
        return new LoopSimSummary(results, BossCredits(lap, world));
    }

    public static LoopSimRun Run(
        SimCharacter character, IReadOnlyList<SimRoom> lap, SimWorld world,
        double secondsPerStep, double hours, int seed, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(lap);
        ArgumentNullException.ThrowIfNull(world);
        return new Session(character, lap, world, secondsPerStep, seed).Play(hours * 3600.0, cancel);
    }

    // A boss is killable once per its regen, however often the lap passes it, so it
    // adds a flat exp ÷ regen-hours, counted once per boss across the loop — the same
    // amortisation the estimate uses (GAME_MECHANICS "Boss monsters").
    private static IReadOnlyList<ExpBossStat> BossCredits(IReadOnlyList<SimRoom> lap, SimWorld world)
    {
        var credits = new List<ExpBossStat>();
        foreach (int id in lap.SelectMany(r => r.Bosses ?? Array.Empty<int>()).Distinct())
        {
            if (world.Monster(id) is not { } e || e.EffectiveExp <= 0) continue;
            double regenHours = Math.Max(1, e.RegenTime);
            credits.Add(new ExpBossStat(e.Name, e.EffectiveExp / regenHours, (int)Math.Round(regenHours)));
        }
        return credits;
    }

    private enum Posture { Standing, Resting, Meditating }

    private enum Walk { Next, Flee, Return }

    private sealed class Mob
    {
        public required MonsterCatalogEntry Entry { get; init; }
        public required string Key { get; init; }
        public required int LairSlot { get; init; }          // -1 = placed NPC, -2 = summoned
        public required bool Hostile { get; init; }          // opens on us unprovoked
        public required bool Fightable { get; init; }        // the client engages it and can hurt it
        public int Hp { get; set; }
        public double Energy { get; set; }
        public bool Engaged { get; set; }
        // The debuffs landed on it, one entry per spell: a re-cast of a spell it
        // already carries (an area debuff re-firing for a new arrival, a re-cast after
        // a flee's return) adds nothing.
        public Dictionary<string, MonsterDebuffEffect> Debuffs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public MonsterDebuffEffect Debuff
        {
            get
            {
                MonsterDebuffEffect sum = default;
                foreach (MonsterDebuffEffect y in Debuffs.Values)
                    sum = new MonsterDebuffEffect(sum.AcDelta + y.AcDelta, sum.DrDelta + y.DrDelta,
                        sum.DodgeDelta + y.DodgeDelta, sum.AccDelta + y.AccDelta, sum.Slowed || y.Slowed);
                return sum;
            }
        }
        public bool Alive => Hp > 0;
        public bool AttacksUs => Alive && (Hostile || Engaged);
    }

    // A lair's respawn clock: Stock keeps one for the whole room, restarted by every
    // kill in it (the placed fixture's too), and refills every empty slot together once it runs out. Paradigm is
    // played with one clock per slot, started by that slot's kill — still unconfirmed
    // against a room clock like Stock's (GAME_MECHANICS "Lair respawn timers"). Only
    // the clock the realm uses is read.
    private sealed class RoomState(int slots)
    {
        public double[] SlotReadyAt { get; } = new double[slots];
        public double RoomReadyAt { get; set; }
        public Mob?[] SlotMob { get; } = new Mob?[slots];
        public List<Mob> Mobs { get; } = new();
    }

    private sealed class Session
    {
        // Fine enough that a 1.1 s walk isn't rounded up to the next step.
        private const double Step = 0.05;
        private const int RoundSteps = 100;            // 5 s combat round
        // The room spell's own tick, every second spell round on both realms
        // (GAME_MECHANICS "Room-spell monster summons"): Stock's 6 s medium tick, and
        // 6.05 s measured on Paradigm (reports paradigm-20261009-120757,
        // paradigm-20261009-122342).
        private const int StockRoomSpellSteps = 120;
        private const int ParadigmRoomSpellSteps = 121;
        private const int DotTickSteps = 60;           // the 3 s update an effect slot's damage ticks on
        // The engine's monster-create pass, which refills the lair a player stands
        // in, runs every 5 s by default (GAME_MECHANICS "Lair respawn timers").
        private const int SpawnPassSteps = 100;
        private const int CancelCheckSteps = 20000;    // every 1000 simulated seconds
        private const int MaxSwingsPerRound = 20;

        // Between-round cast categories in CastingDirector's tie-break order.
        private const int EmergencyOrder = 0, MinorOrder = 4, MajorOrder = 5, BuffOrder = 7, DebuffOrder = 8;

        private readonly SimCharacter _ch;
        private readonly IReadOnlyList<SimRoom> _lap;
        private readonly SimWorld _world;
        private readonly double _secondsPerStep;
        private readonly Random _rng;
        private readonly CombatSpellChooser _chooser = new();
        private readonly Dictionary<RoomKey, RoomState> _rooms = new();
        private readonly RoomState _away = new(0);
        private readonly Dictionary<string, int> _casts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (double Until, int ManaRegen, int HpRegen)> _buffs =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly int _standingSteps, _manaSteps, _restingSteps, _meditatingSteps;
        private readonly int _spawnPassPhase;
        private readonly bool _perSlotClock;
        // The realm's room monster cap, and whether a death cast lands whole or not at
        // all (Paradigm) or summon by summon (Stock) — DeathSummonCascade.RulesFor.
        private readonly int _roomMonsterCap;
        private readonly bool _wholeDeathCasts;

        private long _step;
        private double _hp, _ma;
        private Posture _posture = Posture.Standing;
        private long _postureSince;
        private bool _hpGate, _maGate;
        private int _pos;
        private bool _sneaking, _stabPending;
        private double? _moveDoneAt;
        private Walk _walk;
        private bool _isAway;
        private Mob? _target;
        private double _swingCarry;
        private int _mobSerial;
        private readonly Dictionary<int, (double Since, double Until, int Value, SimProc Proc)> _procs = new();
        private string? _pendingReroll;
        private (string Spell, int Used, bool Waiting)? _reroll;
        private long _exp;
        private int _kills, _laps, _flees;
        private long _damageTaken;
        private double _moving, _attacking, _resting, _meditating, _waiting;
        private int _lowHp = 100, _lowMa = 100;
        private double? _diedAt, _hungUpAt;

        public Session(SimCharacter ch, IReadOnlyList<SimRoom> lap, SimWorld world, double secondsPerStep, int seed)
        {
            _ch = ch;
            _lap = lap;
            _world = world;
            _secondsPerStep = Math.Max(Step, secondsPerStep);
            _rng = new Random(seed);
            _hp = ch.MaxHp;
            _ma = ch.MaxMana;
            _standingSteps = StepsOf(ch.Regen.Cadence.StandingInterval);
            _manaSteps = StepsOf(ch.Regen.Cadence.ManaInterval);
            _restingSteps = StepsOf(ch.Regen.Cadence.RestingInterval);
            _meditatingSteps = StepsOf(ch.Regen.Cadence.MeditatingInterval);
            // The spawn pass runs on the server's own clock, unrelated to the round.
            _spawnPassPhase = _rng.Next(SpawnPassSteps);
            _perSlotClock = ch.Realm == RealmType.ParaMud;
            (_roomMonsterCap, _wholeDeathCasts) = DeathSummonCascade.RulesFor(ch.Realm);
            _sneaking = ch.Backstab is not null;
        }

        private double Now => _step * Step;

        private bool Ended => _diedAt is not null || _hungUpAt is not null;

        private static int StepsOf(TimeSpan t) => Math.Max(1, (int)Math.Round(t.TotalSeconds / Step));

        public LoopSimRun Play(double durationSeconds, CancellationToken cancel)
        {
            if (_lap.Count > 0) Enter(0);
            while (_lap.Count > 0 && Now < durationSeconds && !Ended)
            {
                if (_step % CancelCheckSteps == 0) cancel.ThrowIfCancellationRequested();
                if (_moveDoneAt is { } done && Now >= done) Arrive();
                if (_moveDoneAt is null && !_isAway && _step % SpawnPassSteps == _spawnPassPhase)
                    Spawn(Here(), entering: false);
                if (_step % RoundSteps == 0) Round();
                if (Ended) break;
                if (_moveDoneAt is null && !_isAway
                    && _step % (_ch.Realm == RealmType.ParaMud ? ParadigmRoomSpellSteps : StockRoomSpellSteps) == 0)
                    RoomSummon();
                Burn();
                if (Ended) break;
                Regen();
                Decide();
                Account();
                _step++;
            }
            return new LoopSimRun(durationSeconds, Now, _exp, _kills, _laps, _moving, _attacking, _resting, _meditating,
                _waiting, _lowHp, _lowMa, _diedAt, new Dictionary<string, int>(_casts, StringComparer.OrdinalIgnoreCase),
                _flees, _hungUpAt, _damageTaken);
        }

        // ----- Rooms and spawns ---------

        private RoomState Here() => _isAway ? _away : State(_lap[_pos]);

        private RoomState State(SimRoom room)
        {
            if (!_rooms.TryGetValue(room.Key, out RoomState? state))
                _rooms[room.Key] = state = new RoomState(room.HasLair ? room.LairMax : 0);
            return state;
        }

        private void Enter(int pos)
        {
            _pos = pos;
            SimRoom room = _lap[pos];
            RoomState state = State(room);
            // A placed fixture is back the moment you re-enter (GAME_MECHANICS
            // "NPC-placed monsters").
            if (room.NpcMonster > 0 && !state.Mobs.Any(m => m.LairSlot == -1 && m.Alive))
                AddMob(state, room.NpcMonster, lairSlot: -1);
            Spawn(state, entering: true);
            RoomSummon();
            ArriveSneaking(state);
        }

        // A sneak carried into the room: a see-hidden monster there spots it, else a
        // fight here opens with the surprise stab.
        private void ArriveSneaking(RoomState state)
        {
            _stabPending = false;
            if (!_sneaking) return;
            if (state.Mobs.Any(m => m.Alive && m.Entry.SeesHidden)) { _sneaking = false; return; }
            _stabPending = state.Mobs.Any(m => m.Alive && m.Fightable);
        }

        // Fill every empty lair slot whose clock has run out. Walking in refills at once;
        // standing in the room refills only when the spawn pass comes round, so a
        // lair comes back slower under a character who waits in it. A lair whose
        // timer couldn't be resolved (RespawnSeconds 0) refills on entry only — a
        // zero clock checked while standing would respawn each kill on the spot.
        private void Spawn(RoomState state, bool entering)
        {
            SimRoom room = _lap[_pos];
            if (!room.HasLair || (!entering && room.RespawnSeconds <= 0)) return;
            if (!_perSlotClock && state.RoomReadyAt > Now) return;
            for (int slot = 0; slot < state.SlotMob.Length; slot++)
            {
                if (state.SlotMob[slot] is not null || (_perSlotClock && state.SlotReadyAt[slot] > Now)) continue;
                int id = room.LairMonsters[_rng.Next(room.LairMonsters.Count)];
                state.SlotMob[slot] = AddMob(state, id, slot);
            }
        }

        // The room's entry spell rolls its d100 table: on entry, then on the realm's
        // room-spell tick (6.05 s on Paradigm, 6 s on Stock) while we're here; a
        // `nomonsters:` table only rolls in an empty room, and a line with its own
        // `nomonsters` brings nothing into an occupied one (GAME_MECHANICS "Room-spell
        // monster summons"). A line brings every monster it names, up to the room cap.
        private void RoomSummon()
        {
            if (_lap[_pos].Summon is not { } table) return;
            RoomState state = Here();
            bool occupied = state.Mobs.Any(m => m.Alive);
            if (table.NoMonstersGate && occupied) return;
            double roll = _rng.NextDouble();
            foreach (RoomSummonEntry e in table.Entries)
            {
                roll -= e.Probability;
                if (roll >= 0) continue;
                if (e.EmptyRoomOnly && occupied) return;
                foreach (int monster in e.Monsters)
                    if (state.Mobs.Count(m => m.Alive) < _roomMonsterCap) AddMob(state, monster, lairSlot: -2);
                return;
            }
        }

        private Mob? AddMob(RoomState state, int number, int lairSlot)
        {
            if (_world.Monster(number) is not { } entry) return null;
            // Lawful evil spares Outlaw-or-worse, as StockAggroCalculator.Acquire rules.
            bool hostile = entry.Align is 1 or 2 or 5
                || (entry.Align == 6 && _ch.AlignmentValue < StockAggroCalculator.OutlawValue);
            bool engageable = MonsterEngagement.IsEngageable(_ch.Overlay(number));
            var mob = new Mob
            {
                Entry = entry,
                Key = $"{entry.Name}#{++_mobSerial}",
                LairSlot = lairSlot,
                Hostile = hostile,
                Fightable = (engageable || hostile) && CanHurt(entry),
                Hp = Math.Max(1, entry.Hp),
            };
            state.Mobs.Add(mob);
            return mob;
        }

        // Whether anything in the configured cascade can damage it — the weapon clears
        // its Magical level, or the Normal / Alternate attack spell (or, at an undead
        // monster, the undead spell) isn't blocked by game data. The client walks past a monster it provably can't hurt.
        private bool CanHurt(MonsterCatalogEntry entry)
        {
            if (_ch.Melee.HasWeapon && _ch.WeaponHitMagic >= entry.Magical) return true;
            string? normal = NullIfBlank(_ch.Combat.NormalAttackSpell.SpellName);
            string? alt = NullIfBlank(_ch.Combat.AlternateAttackSpell.SpellName);
            MonsterOverlay overlay = _ch.Overlay(entry.Number);
            normal = _ch.SpellShortByNumber(overlay.OverrideAttackSpellId ?? 0) ?? normal;
            alt = _ch.SpellShortByNumber(overlay.OverrideAltAttackSpellId ?? 0) ?? alt;
            string? undead = _world.MonsterLife?.IsUndead(entry.Number) == true
                ? NullIfBlank(_ch.Combat.UndeadAttackSpell.SpellName) : null;
            bool Blocked(string? code, CombatSpellAction action) =>
                code is null || !_ch.Spells.ContainsKey(code)
                || (Gates(entry.Number, null, normal, alt, undead) is { } b && b.Contains(action));
            return !Blocked(normal, CombatSpellAction.NormalAttackSpell)
                || !Blocked(alt, CombatSpellAction.AlternateAttackSpell)
                || !Blocked(undead, CombatSpellAction.UndeadAttackSpell);
        }

        private IReadOnlySet<CombatSpellAction>? Gates(int number, string? single, string? normal, string? alt, string? undead)
        {
            HashSet<CombatSpellAction>? all = null;
            foreach (IReadOnlySet<CombatSpellAction>? set in new[]
            {
                CombatSpellGates.LevelBlocked(_world.MonsterMagic, _world.SpellReqLevel, number, single, normal, alt, OwnSpell, undead),
                CombatSpellGates.ResistBlocked(_world.MonsterResist, _world.SpellAttackType, number, normal, alt, OwnSpell, undead),
                CombatSpellGates.TargetTypeBlocked(_world.SpellTargetType, _world.MonsterLife, number, normal, alt, OwnSpell, undead),
            })
                if (set is not null) (all ??= new HashSet<CombatSpellAction>()).UnionWith(set);
            return all;
        }

        private bool Fighting => Here().Mobs.Any(m => m.Alive && m.Fightable);

        // The character's learned spell for a cast-code, from the frozen spell list (the
        // run is off the UI thread, so never the live spell book).
        private int? OwnSpell(string code) => _ch.Spells.TryGetValue(code, out SimSpell? s) ? s.Number : null;

        // ----- The round ---------

        private void Round()
        {
            bool present = _moveDoneAt is null;
            bool combat = present && Fighting;
            if (combat)
            {
                _posture = Posture.Standing;
                PlayerAttack();
            }
            if (present) MonstersAttack();
            Track();
            if (_hp <= 0) { _diedAt = Now; return; }
            if (present && HealthTriggers()) return;
            BetweenRoundCast(combat && Fighting);
            Here().Mobs.RemoveAll(m => !m.Alive);
        }

        // The Health tab's run / hang-up triggers, read after the round lands with
        // HealthManager's gates, whenever a monster still attacking us is here — one
        // we can't hurt included, since its hits put the live client in combat too:
        // hang up at or below its trigger (unless hang-ups are disabled) ends the run;
        // run at or below either pool's trigger leaves RunDistance rooms and rests
        // there, walking back once both pools are above their run triggers. The
        // simulated room is left as it was (no pursuit is modelled). A hold
        // (knockdown) refuses the moves, so the flee waits for it to wear off
        // (GAME_MECHANICS "Knockdown — a movement-preventing hold").
        private bool HealthTriggers()
        {
            HealthSettings h = _ch.Health;
            if (!Here().Mobs.Any(m => m.AttacksUs)) return false;
            if (!_ch.HangupsDisabled && _ch.MaxHp > 0
                && _hp <= PoolThreshold.Resolve(h.HpThresholdMode, h.HangIfBelowHp, _ch.MaxHp))
            {
                _hungUpAt = Now;
                return true;
            }
            bool runHp = h.RunIfBelowHp > 0 && _ch.MaxHp > 0 && _hp > 0
                && _hp <= PoolThreshold.Resolve(h.HpThresholdMode, h.RunIfBelowHp, _ch.MaxHp);
            bool runMa = h.RunIfBelowMa > 0 && _ch.MaxMana > 0
                && _ma <= PoolThreshold.Resolve(h.MaThresholdMode, h.RunIfBelowMa, _ch.MaxMana);
            if (!runHp && !runMa) return false;
            if (Held) return false;
            _flees++;
            _target = null;
            StartWalk(Walk.Flee, Math.Max(1, _ch.Combat.RunDistance) * _secondsPerStep);
            return true;
        }

        private void PlayerAttack()
        {
            RoomState room = Here();
            List<Mob> fightable = room.Mobs.Where(m => m.Alive && m.Fightable).ToList();
            if (_target is null || !_target.Alive || !room.Mobs.Contains(_target))
            {
                _target = fightable[0];
                _chooser.ResetForNewTarget();
            }
            Mob target = _target;
            target.Engaged = true;

            CombatSpellDecision decision = _chooser.Choose(_ch.Combat, Context(target, fightable));
            // Any attack spends the surprise and ends the sneak.
            _stabPending = false;
            _sneaking = false;
            bool area = decision.Action is CombatSpellAction.MultiAttack or CombatSpellAction.MultiAttack2;
            if (decision.Action == CombatSpellAction.Backstab)
            {
                Stab(target);
                Count("bs");
            }
            else if (decision.Spell is { } code && _ch.Spells.TryGetValue(code, out SimSpell? spell))
            {
                if (CastAttack(spell, target, area ? fightable : null))
                {
                    _chooser.MarkCast(decision, target.Key, fightable.Select(m => m.Key).ToList());
                    Count(spell.Short);
                }
            }
            else
            {
                Swing(target);
                Count(_ch.Combat.NormalAttackCommand);
            }

            if (!room.Mobs.Any(m => m.Alive && m.Fightable)) _chooser.ResetForRosterClear();
        }

        private CombatSpellContext Context(Mob target, List<Mob> fightable)
        {
            int ma = (int)_ma;
            int number = target.Entry.Number;
            MonsterOverlay o = _ch.Overlay(number);
            ThresholdMode mode = _ch.Combat.SpellManaThresholdMode;
            (string? atk, int? atkCap) = CombatSpellGates.ResolveOverride(o.OverrideAttackSpellId, o.OverrideAttackCount,
                o.OverrideAttackMinMana, ma, _ch.MaxMana, mode, _ch.SpellShortByNumber);
            (string? alt, int? altCap) = CombatSpellGates.ResolveOverride(o.OverrideAltAttackSpellId, o.OverrideAltAttackCount,
                o.OverrideAltAttackMinMana, ma, _ch.MaxMana, mode, _ch.SpellShortByNumber);
            (string? pre, int? preCap) = CombatSpellGates.ResolveOverride(o.OverridePreAttackSpellId, o.OverridePreAttackCount,
                o.OverridePreAttackMinMana, ma, _ch.MaxMana, mode, _ch.SpellShortByNumber);
            string? normalEff = atk ?? NullIfBlank(_ch.Combat.NormalAttackSpell.SpellName);
            string? altEff = alt ?? NullIfBlank(_ch.Combat.AlternateAttackSpell.SpellName);
            string? singleEff = pre ?? NullIfBlank(_ch.Combat.SingleTargetDebuffSpell.SpellName);
            bool undead = _world.MonsterLife?.IsUndead(number) == true;
            string? undeadEff = undead ? NullIfBlank(_ch.Combat.UndeadAttackSpell.SpellName) : null;
            bool hpBelowDrain = _ch.Combat.DrainHpTrigger > 0
                && _hp <= Math.Round(_ch.MaxHp * _ch.Combat.DrainHpTrigger / 100.0);

            return new CombatSpellContext(
                EnemyCount: fightable.Count,
                TargetRawName: target.Key,
                Mana: ma,
                MaxMana: _ch.MaxMana,
                BackstabPending: _stabPending,
                SpellsAvailable: ma > 0,
                LevelBlockedActions: CombatSpellGates.LevelBlocked(_world.MonsterMagic, _world.SpellReqLevel, number, singleEff, normalEff, altEff, OwnSpell, undeadEff),
                ResistBlockedActions: CombatSpellGates.ResistBlocked(_world.MonsterResist, _world.SpellAttackType, number, normalEff, altEff, OwnSpell, undeadEff),
                TargetTypeBlockedActions: CombatSpellGates.TargetTypeBlocked(_world.SpellTargetType, _world.MonsterLife, number, normalEff, altEff, OwnSpell, undeadEff),
                TargetDontBackstab: o.DontBackstab ?? false,
                OverrideAttackSpell: atk,
                OverrideAttackMaxCasts: atkCap,
                OverridePreAttackSpell: pre,
                OverridePreAttackMaxCasts: preCap,
                OverrideAltAttackSpell: alt,
                OverrideAltAttackMaxCasts: altCap,
                WeaponIneffective: !_ch.Melee.HasWeapon || _ch.WeaponHitMagic < target.Entry.Magical,
                HpBelowDrainTrigger: hpBelowDrain,
                DrainTargetEligible: _world.MonsterLife?.CanDrain(number) ?? true,
                TargetIsUndead: undead,
                RoomMobKeys: fightable.Select(m => m.Key).ToList(),
                ManaCostOf: c => _ch.Spells.TryGetValue(c, out SimSpell? s) ? s.ManaPerCast : null);
        }

        // One round of an attack spell: up to FiresPerRound fires, each paying its
        // mana whether it lands or not, the round's damage range split across them.
        // A single-target spell stops firing once its target dies. False when not one
        // fire could be paid for.
        private bool CastAttack(SimSpell spell, Mob target, List<Mob>? room)
        {
            int fires = Math.Max(1, spell.FiresPerRound);
            bool fired = false;
            for (int f = 0; f < fires; f++)
            {
                if (_ma < spell.ManaPerCast) break;
                _ma -= spell.ManaPerCast;
                fired = true;
                if (!Roll(spell.CastChance ?? 100)) continue;
                if (room is null)
                {
                    SpellHit(target, spell, fires);
                    if (!target.Alive) break;
                }
                else
                {
                    foreach (Mob m in room.Where(m => m.Alive).ToList()) SpellHit(m, spell, fires);
                    if (!room.Any(m => m.Alive)) break;
                }
            }
            return fired;
        }

        private void SpellHit(Mob mob, SimSpell spell, int fires)
        {
            MonsterCatalogEntry e = mob.Entry;
            mob.Engaged = true;
            if (_rng.NextDouble() * 100 < SpellDamageCalculator.FullResistChance(
                    spell.TypeOfResists, e.SpellMagicResist, e.AntiMagic, _ch.Realm)) return;
            long lo = spell.MinDamagePerRound / fires, hi = spell.MaxDamagePerRound / fires;
            long raw = lo >= hi ? hi : lo + (long)(_rng.NextDouble() * (hi - lo + 1));
            int code = MonsterResistIndex.ElementalResistCode(spell.AttType);
            int resist = code >= 0 && e.ElementalResists.TryGetValue(code, out int pct) ? pct : 0;
            long dmg = SpellDamageCalculator.AfterTargetResists(raw, spell.UsesMagicResist, e.SpellMagicResist, e.AntiMagic, resist, _ch.Realm);
            Damage(mob, dmg);
        }

        // The weapon round: swings carry their fractional remainder into the next
        // round (energy rolls over — GAME_MECHANICS "Player physical swings per round:
        // cap and energy formula").
        // A debuff's AC / Dodge / DR strip lands here (MonsterDebuffCalculator).
        private void Swing(Mob target)
        {
            PlayerMatchupProfile melee = _ch.Melee;
            if (!melee.HasWeapon || _ch.WeaponHitMagic < target.Entry.Magical) return;
            _swingCarry += melee.SwingsPerRound;
            int swings = (int)_swingCarry;
            _swingCarry -= swings;
            MonsterDebuffEffect d = target.Debuff;
            double hit = CombatCalculator.CalculateHitChance(melee.NormalAccuracy + ProcSum(p => p.AccuracyDelta), target.Entry.ArmourClass - d.AcDelta,
                target.Entry.Dodge - d.DodgeDelta, realmType: _ch.Realm).OverallHitChance;
            int dr = (int)Math.Max(0, (target.Entry.DamageResist - d.DrDelta) * melee.MonsterDrMultiplier);
            for (int i = 0; i < swings && target.Alive; i++)
            {
                if (!Roll(hit)) continue;
                int dmg = Roll(melee.CritChancePercent) ? melee.AvgCritDamage : melee.AvgWeaponDamage;
                Damage(target, Math.Max(0, dmg - dr));
            }
        }

        // The surprise round is one stab: the backstab roll against the monster's
        // backstab defence and the stab's range less its DR, as Monster Intel judges
        // it (BackstabMatchupCalculator). A miss is the whole round.
        private void Stab(Mob target)
        {
            if (_ch.Backstab is not { } bs || _ch.BackstabHitMagic < target.Entry.Magical) return;
            MonsterCatalogEntry e = target.Entry;
            MonsterDebuffEffect d = target.Debuff;
            var monster = new MonsterMatchupProfile(e.ArmourClass - d.AcDelta,
                (int)Math.Max(0, e.DamageResist - d.DrDelta), target.Hp, e.Dodge - d.DodgeDelta,
                HasPhysicalAttack: false, AvgAttackDamage: 0);
            BackstabMatchup stab = BackstabMatchupCalculator.Evaluate(
                bs with { NormalAccuracy = bs.NormalAccuracy + ProcSum(p => p.AccuracyDelta) }, monster, e.BsDefense, seesHidden: false);
            if (!Roll(stab.HitChance)) return;
            Damage(target, Between(stab.MinDamage, stab.MaxDamage));
        }

        private void Damage(Mob mob, long amount)
        {
            if (!mob.Alive) return;
            mob.Hp = (int)Math.Min(mob.Entry.Hp, mob.Hp - amount);   // a negative amount heals, never past max
            if (mob.Alive) return;

            _exp += mob.Entry.EffectiveExp;
            _kills++;
            RoomState room = Here();
            double readyAt = Now + _lap[_pos].RespawnSeconds;
            if (mob.LairSlot >= 0)
            {
                room.SlotMob[mob.LairSlot] = null;
                if (_perSlotClock) room.SlotReadyAt[mob.LairSlot] = readyAt;
            }
            // Stock stamps the room's last kill on every death in it, the placed
            // fixture's included (GAME_MECHANICS "Lair respawn timers").
            if (!_perSlotClock) room.RoomReadyAt = readyAt;
            if (ReferenceEquals(mob, _target))
            {
                _target = null;
                _chooser.ResetForNewTarget();
            }
            // Its death spell summons the next tier into the room under the room's
            // monster cap: on Paradigm as one cast, all of it or none of it; on Stock
            // summon by summon, as many as fit (GAME_MECHANICS "Death-summon cascades").
            if (_world.DeathSummons?.Invoke(mob.Entry.Number) is { Count: > 0 } summons)
            {
                int alive = room.Mobs.Count(m => m.Alive);
                if (!_wholeDeathCasts || alive + summons.Count <= _roomMonsterCap)
                    foreach (int id in summons)
                        if (room.Mobs.Count(m => m.Alive) < _roomMonsterCap) AddMob(room, id, lairSlot: -2);
            }
        }

        // Each monster fighting us spends its round's energy (plus last round's
        // leftover) on attacks picked by their use-weight, then rolls its
        // between-rounds spells (GAME_MECHANICS "Monster swings per round: energy
        // budget and rollover"). A slowing debuff raises its attack energy ×1.5 and an
        // accuracy debuff comes off its to-hit.
        private void MonstersAttack()
        {
            PlayerDefenseProfile def = _ch.Defense;
            foreach (Mob mob in Here().Mobs.Where(m => m.AttacksUs).ToList())
            {
                MonsterCatalogEntry e = mob.Entry;
                MonsterDebuffEffect d = mob.Debuff;
                var slots = e.Attacks.Where(a => a.Percent > 0 && a.Type is 1 or 2).ToList();
                mob.Energy += e.Energy > 0 ? e.Energy : 1000;
                for (int n = 0; n < MaxSwingsPerRound && slots.Count > 0; n++)
                {
                    MonsterAttackSlot a = PickWeighted(slots);
                    int cost = a.Energy > 0 ? a.Energy : (e.Energy > 0 ? e.Energy : 1000);
                    if (d.Slowed) cost = cost * 3 / 2;
                    if (mob.Energy < cost) break;
                    mob.Energy -= cost;
                    if (a.Type == 1)
                    {
                        // The same unrounded chance Monster Intel shows: a whole percent
                        // truncated the dodge share away near the floor.
                        PlayerDefenseProfile now = def with
                        {
                            Ac = def.Ac + ProcSum(p => p.AcDelta),
                            Dodge = def.Dodge + ProcSum(p => p.DodgeDelta),
                        };
                        if (!Roll(IncomingHitEstimator.AttackHitChance(a, e.Align, now, _ch.Realm, d.AccDelta))) continue;
                        Hurt(Math.Max(0, Between(a.MinDamage, a.MaxDamage) - _ch.Melee.DamageResist));
                        // A landed attack fires its hit spell (GAME_MECHANICS "Monster
                        // on-hit procs (`AttHitSpell-N`) are physical attacks, not casts").
                        if (a.HitSpell > 0 && _world.HitSpell?.Invoke(a.HitSpell) is { } proc)
                            LandProc(a.HitSpell, proc);
                    }
                    else if (a.SpellDmgMax > 0 && Roll(a.MinDamage))
                    {
                        // A spell slot's MinDamage field holds its cast-success percent.
                        Hurt(Between(a.SpellDmgMin, a.SpellDmgMax));
                    }
                }
                foreach (MonsterMidSpellSlot m in e.MidSpells)
                    if (m.DmgMax > 0 && Roll(m.Percent)) Hurt(Between(m.DmgMin, m.DmgMax));
            }
        }

        // One hit spell landing. Without a duration it hits once for a cast's damage.
        // With one it takes an effect slot, one per spell, following the Stock engine's
        // monster re-cast rule for both realms (GAME_MECHANICS "Poison and damage over
        // time — ticks, stacking and cures"): a spell already on us is refreshed (new
        // value, duration reset) only by a strictly higher roll, else the landing is
        // dropped; different spells stack. A burn's roll is its per-tick damage.
        private void LandProc(int spell, SimProc proc)
        {
            // One that doesn't burn hits now; its effect (if timed) still takes a slot.
            if (!proc.DamageOverTime && proc.DamageMax > 0) Hurt(Between(proc.DamageMin, proc.DamageMax));
            if (proc.DurationSeconds <= 0) return;
            int value = proc.DamageOverTime ? Between(proc.DamageMin, proc.DamageMax) : 0;
            if (_procs.TryGetValue(spell, out var on) && on.Until > Now && value <= on.Value) return;
            _procs[spell] = (Now, Now + proc.DurationSeconds, value, proc);
        }

        // The summed stat change of the hit-spell effects still on us.
        private int ProcSum(Func<SimProc, int> part)
        {
            int sum = 0;
            foreach (var x in _procs.Values)
                if (x.Until > Now) sum += part(x.Proc);
            return sum;
        }

        private bool Held => _procs.Values.Any(x => x.Until > Now && x.Proc.Holds);

        // Burn slots (envelops' "You are on fire!") take their stored value off HP on
        // every 3 s medium update, landed before this step, until they run out; a
        // slot that has run out is dropped.
        private void Burn()
        {
            if (_procs.Count == 0) return;
            bool tick = _step % DotTickSteps == 0;
            foreach ((int spell, var x) in _procs.ToList())
            {
                if (x.Until <= Now) { _procs.Remove(spell); continue; }
                if (tick && x.Proc.DamageOverTime && x.Since < Now) Hurt(x.Value);
            }
            if (_hp <= 0 && _diedAt is null) { Track(); _diedAt = Now; }
        }

        private void Hurt(int amount)
        {
            _hp -= amount;
            _damageTaken += amount;
        }

        private MonsterAttackSlot PickWeighted(List<MonsterAttackSlot> slots)
        {
            double total = slots.Sum(Weight);
            double r = _rng.NextDouble() * total;
            foreach (MonsterAttackSlot s in slots)
            {
                r -= Weight(s);
                if (r < 0) return s;
            }
            return slots[^1];

            static double Weight(MonsterAttackSlot s) => s.TruePercent > 0 ? s.TruePercent : s.Percent;
        }

        // ----- The between-round slot ---------

        // The round's one between-round cast: the self-heal tiers, buff upkeep and the
        // combat debuff, ranked by the Spells-tab priorities (ties in CastingDirector's
        // category order); the first that's due and affordable fires. A cast stands a
        // resting character up (GAME_MECHANICS "Casting a spell interrupts resting /
        // meditating").
        private void BetweenRoundCast(bool inCombat)
        {
            SpellsSettings spells = _ch.SpellSlots;
            HealthSettings health = _ch.Health;
            var inputs = new SelfHealInputs((int)_hp, _ch.MaxHp, (int)_ma, _ch.MaxMana, inCombat,
                Resting: _posture == Posture.Resting,
                HealHpTrigger: (mode, value) => RestThresholds.ResolveValue(mode, value, _ch.DefaultMaxHp, _ch.MaxHp, _ch.MaxHp),
                Affordable: code => !_ch.Spells.TryGetValue(code, out SimSpell? s) || _ma >= s.ManaPerCast,
                HpRegenRecastDue: code => !BuffUp(code, 0));

            (int Priority, int Order, Func<bool> Cast)[] tiers =
            {
                (spells.PriorityEmergencyHeal, EmergencyOrder, () => Heal(SelfHealPicker.Emergency(spells, health, inputs))),
                (spells.PriorityMinorSelfHeal, MinorOrder, () => Heal(SelfHealPicker.Minor(spells, health, inputs))),
                (spells.PriorityMajorSelfHeal, MajorOrder, () => Heal(SelfHealPicker.Major(spells, health, inputs))),
                (spells.PriorityBuffing, BuffOrder, () => Buff(inCombat)),
                (spells.PriorityDebuffing, DebuffOrder, () => inCombat && Debuff()),
            };
            foreach (var tier in tiers.OrderBy(t => t.Priority).ThenBy(t => t.Order))
                if (tier.Cast()) return;
        }

        private bool Heal(string? code)
        {
            if (!TryPay(code, out SimSpell? heal)) return false;
            if (Roll(heal.CastChance ?? 100))
            {
                if (heal.DurationSeconds > 0 && heal.MaxHeal <= 0)
                    Land(heal);                                       // an HP-regen spell in the heal slot
                else
                    _hp = Math.Min(_ch.MaxHp, _hp + Between(heal.MinHeal, heal.MaxHeal));
            }
            return true;
        }

        // Buff upkeep, CastingDirector's solo path: a staged mana-regen reroll leads,
        // then the Buffs list in priority order. Each buff waits for its own mana
        // floor, and waits out combat and triggered rests unless its own switches
        // allow them.
        private bool Buff(bool inCombat)
        {
            if (_ch.Buffs is not { Count: > 0 } buffs) return false;
            ResumeReroll(inCombat);
            HealthSettings h = _ch.Health;
            bool resting = _posture != Posture.Standing;
            bool ManaClears(SimBuff b) =>
                _ch.MaxMana > 0 && _ma >= PoolThreshold.Resolve(h.MaThresholdMode, b.BlessIfAboveMa, _ch.MaxMana);
            bool SelfAllowed(SimBuff b) => !(inCombat && !b.DuringCombat) && !(resting && !b.WhileResting);

            if (_pendingReroll is { } reroll
                && buffs.FirstOrDefault(b => string.Equals(b.Spell, reroll, StringComparison.OrdinalIgnoreCase)) is { } rb
                && SelfAllowed(rb))
            {
                _pendingReroll = null;     // CastBuff re-stages it when the cycle needs another go
                // CastingDirector drops a reroll it can't pay for now.
                if (ManaClears(rb) && CastBuff(rb, inCombat)) return true;
                if (ManaClears(rb)) _pendingReroll = reroll;
            }
            int hpFull = PoolThreshold.Resolve(h.HpThresholdMode, h.RestMaxHp, _ch.MaxHp);
            int maFull = PoolThreshold.Resolve(h.MaThresholdMode, h.RestMaxMa, _ch.MaxMana);
            foreach (SimBuff b in buffs)
            {
                if (!ManaClears(b)) continue;
                if (b.OnlyWhenHpFull && _hp < hpFull) continue;
                if (b.OnlyWhenMaFull && _ma < maFull) continue;
                if (!(b.BeforeRestingForMana ? _maGate : SelfAllowed(b))) continue;
                if (BuffUp(b.Spell, b.RecastMarginSec)) continue;
                if (CastBuff(b, inCombat)) return true;
            }
            return false;
        }

        // ManaRegenReroller judges on the send, not the landing: a failed cast reads
        // back the roll still on, so a cycle goes on re-judging it; with nothing on to
        // read, the reroll is simply tried again.
        private bool CastBuff(SimBuff b, bool inCombat)
        {
            if (!TryPay(b.Spell, out SimSpell? spell)) return false;
            bool rollSpell = spell.ManaRegenMin != spell.ManaRegenMax && b.RerollBelow is not null;
            if (Roll(spell.CastChance ?? 100))
            {
                int rolled = Land(spell);
                if (rollSpell) JudgeRoll(b, rolled, inCombat);
            }
            else if (rollSpell && _buffs.TryGetValue(spell.Short, out var on) && on.Until > Now)
                JudgeRoll(b, on.ManaRegen, inCombat);
            else if (rollSpell && _reroll is { Waiting: false })
                _pendingReroll = b.Spell;
            return true;
        }

        // ManaRegenReroller's cycle: a cast of the roll spell while idle opens a cycle
        // (counter zeroed), a cast mid-cycle is the reroll and keeps the counter. A roll
        // under the threshold is rerolled up to the cap — held while fighting (a
        // mid-fight recast breaks combat) and paused at the mana floor with its counter
        // kept, both resumed by ResumeReroll.
        private void JudgeRoll(SimBuff b, int rolled, bool inCombat)
        {
            if (_reroll is not { } cycle || !string.Equals(cycle.Spell, b.Spell, StringComparison.OrdinalIgnoreCase))
                _reroll = cycle = (b.Spell, 0, false);
            if (rolled >= b.RerollBelow || (!b.RerollInfinite && cycle.Used >= b.RerollCount))
            {
                _reroll = null;
                return;
            }
            if (inCombat || !CanAffordReroll(b.Spell))
            {
                _reroll = (cycle.Spell, cycle.Used, true);
                return;
            }
            _reroll = (cycle.Spell, cycle.Used + 1, false);
            _pendingReroll = b.Spell;
        }

        // ManaRegenReroller.OnRecoveryTick: a held or paused reroll goes out once the
        // fight is over and the recast leaves mana at or above the buff floor.
        private void ResumeReroll(bool inCombat)
        {
            if (_reroll is not { Waiting: true } cycle || inCombat || !CanAffordReroll(cycle.Spell)) return;
            _reroll = (cycle.Spell, cycle.Used + 1, false);
            _pendingReroll = cycle.Spell;
        }

        // AppServices.CanAffordManaRegenReroll: the recast must leave mana at or above
        // that buff's own BlessIfAboveMa floor.
        private bool CanAffordReroll(string code)
        {
            if (_ch.MaxMana <= 0 || !_ch.Spells.TryGetValue(code, out SimSpell? spell)) return false;
            int value = _ch.Buffs?.FirstOrDefault(b => string.Equals(b.Spell, code, StringComparison.OrdinalIgnoreCase))
                ?.BlessIfAboveMa ?? BuffSlot.DefaultBlessIfAboveMa;
            int floor = PoolThreshold.Resolve(_ch.Health.MaThresholdMode, value, _ch.MaxMana);
            return _ma - spell.ManaPerCast >= floor;
        }

        // The combat debuff CombatSpellChooser picks this round (area or single
        // target), landing its stat strip on the monster(s) it hits. A landed debuff
        // stays for the fight: its duration and the monster's resist roll are a
        // stated simplification, not modelled.
        private bool Debuff()
        {
            RoomState room = Here();
            List<Mob> fightable = room.Mobs.Where(m => m.Alive && m.Fightable).ToList();
            if (fightable.Count == 0) return false;
            Mob target = _target is { Alive: true } t && fightable.Contains(t) ? t : fightable[0];
            if (_chooser.ChooseDebuff(_ch.Combat, Context(target, fightable)) is not { Spell: { } code } decision) return false;
            if (!TryPay(code, out SimSpell? spell)) return false;
            _chooser.MarkCast(decision, target.Key, fightable.Select(m => m.Key).ToList());
            if (!Roll(spell.CastChance ?? 100)) return true;
            IEnumerable<Mob> hit = decision.Action == CombatSpellAction.AreaDebuff ? fightable : new[] { target };
            foreach (Mob m in hit)
            {
                m.Engaged = true;
                m.Debuffs.TryAdd(spell.Short, spell.Debuff);
            }
            return true;
        }

        // Charge a between-round cast: known, affordable, counted, and it stands us up.
        private bool TryPay(string? code, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SimSpell? spell)
        {
            spell = null;
            if (code is null || !_ch.Spells.TryGetValue(code, out spell) || _ma < spell.ManaPerCast) return false;
            _ma -= spell.ManaPerCast;
            Count(spell.Short);
            _posture = Posture.Standing;
            _sneaking = false;
            _stabPending = false;
            return true;
        }

        // A landed buff runs for its duration; a roll spell rolls its regen percent
        // now. Returns the mana-regen percent it landed with.
        private int Land(SimSpell spell)
        {
            int mana = Between(spell.ManaRegenMin, spell.ManaRegenMax);
            _buffs[spell.Short] = (Now + spell.DurationSeconds, mana, Between(spell.HpRegenMin, spell.HpRegenMax));
            return mana;
        }

        private bool BuffUp(string code, int marginSec) =>
            _buffs.TryGetValue(code, out var b) && b.Until - marginSec > Now;

        // ----- Between rounds: regen, rest, move ---------

        private void Regen()
        {
            SimRegen r = _ch.Regen;
            int manaBonus = 0, hpBonus = 0;
            foreach (var b in _buffs.Values)
                if (b.Until > Now) { manaBonus += b.ManaRegen; hpBonus += b.HpRegen; }

            long since = _step - _postureSince;
            bool resting = _posture == Posture.Resting;
            bool standingHp = _step > 0 && _step % _standingSteps == 0 && (!resting || !r.RestReplacesStanding);
            if (standingHp) _hp += r.HpStanding(hpBonus);
            if (_step > 0 && _step % _manaSteps == 0)
            {
                _ma += r.MaStanding(manaBonus);
                if (standingHp) _hp += r.StandingPassExtra(hpBonus);
            }
            if (resting && r.Cadence.RestingOnRoundGrid)
            {
                // The gains fall on the game's own grid; only their count runs from
                // lying down.
                if (since > 0 && _step % _restingSteps == 0)
                    _hp += r.RestTickHp(_step / _restingSteps - _postureSince / _restingSteps, hpBonus);
            }
            else if (resting && since > 0 && since % _restingSteps == 0)
                _hp += r.RestTickHp(since / _restingSteps, hpBonus);
            if (_posture == Posture.Meditating && since > 0 && since % _meditatingSteps == 0)
                _ma += r.MaMeditating;
            _hp = Math.Min(_hp, _ch.MaxHp);
            _ma = Math.Min(_ma, _ch.MaxMana);
        }

        // Out of combat: rest while a pool sits under its rest trigger until it's
        // back to its rest target (HealthManager's gates), else walk on — or, after a
        // flee, walk back to the fight.
        private void Decide()
        {
            if (_moveDoneAt is not null || Fighting) return;
            HealthSettings h = _ch.Health;
            (int hpLow, int hpFull) = RestThresholds.Resolve(h.HpThresholdMode, h.RestIfBelowHp, h.RestMaxHp,
                _ch.DefaultMaxHp, _ch.MaxHp, _ch.MaxHp);
            (int maLow, int maFull) = RestThresholds.Resolve(h.MaThresholdMode, h.RestIfBelowMa, h.RestMaxMa,
                _ch.DefaultMaxMana, _ch.MaxMana, _ch.MaxMana);
            _hpGate = _hpGate ? _hp < hpFull : _hp < hpLow;
            _maGate = _ch.MaxMana > 0 && (_maGate ? _ma < maFull : _ma < maLow);

            if (_hpGate || _maGate)
            {
                Posture want = RestPosture(h);
                if (_posture != want)
                {
                    _posture = want;
                    _postureSince = _step;
                    _sneaking = false;
                }
                return;
            }
            _posture = Posture.Standing;
            if (Held) return;      // knocked down / held: can't walk off until it wears off
            if (_isAway)
            {
                if (RecoveredFromRun(h)) StartWalk(Walk.Return, Math.Max(1, _ch.Combat.RunDistance) * _secondsPerStep);
            }
            else StartWalk(Walk.Next, _secondsPerStep + _lap[_pos].PauseSeconds);
        }

        // HealthManager's auto-resume after a flee: both pools back above their run
        // triggers (a pool whose run trigger is off never holds it), so the walk back
        // doesn't run straight into another flee.
        private bool RecoveredFromRun(HealthSettings h)
        {
            bool hp = h.RunIfBelowHp <= 0 || _hp > PoolThreshold.Resolve(h.HpThresholdMode, h.RunIfBelowHp, _ch.MaxHp);
            bool ma = h.RunIfBelowMa <= 0 || _ch.MaxMana <= 0
                || _ma > PoolThreshold.Resolve(h.MaThresholdMode, h.RunIfBelowMa, _ch.MaxMana);
            return hp && ma;
        }

        // HealthManager.ChooseRestCommand: meditate for mana alone (or first, when
        // MeditateBeforeResting), rest otherwise.
        private Posture RestPosture(HealthSettings h)
        {
            if (!h.UseMeditateAbility || _ch.MaxMana <= 0) return Posture.Resting;
            if (_maGate && !_hpGate) return Posture.Meditating;
            if (_hpGate && _maGate && h.MeditateBeforeResting) return Posture.Meditating;
            return Posture.Resting;
        }

        // A broken sneak is re-taken before the step only where nothing is alive in the
        // room (GAME_MECHANICS "Sneaking — commands, equip order, and the sneak state
        // machine" → "NPCs and sneaking").
        private void StartWalk(Walk kind, double seconds)
        {
            _walk = kind;
            _posture = Posture.Standing;
            _moveDoneAt = Now + seconds;
            _stabPending = false;
            if (!_sneaking && _ch.Backstab is not null && !Here().Mobs.Any(m => m.Alive)) _sneaking = true;
        }

        private void Arrive()
        {
            _moveDoneAt = null;
            _target = null;
            _chooser.ResetForNewRoom();
            switch (_walk)
            {
                case Walk.Flee:
                    _isAway = true;
                    break;
                case Walk.Return:
                    _isAway = false;
                    ArriveSneaking(Here());
                    break;
                default:
                    int next = (_pos + 1) % _lap.Count;
                    if (next == 0) _laps++;
                    Enter(next);
                    break;
            }
        }

        private void Account()
        {
            if (_moveDoneAt is not null) _moving += Step;
            else if (Fighting) _attacking += Step;
            else if (_posture == Posture.Meditating) _meditating += Step;
            else if (_posture == Posture.Resting) _resting += Step;
            else _waiting += Step;
        }

        private void Track()
        {
            if (_ch.MaxHp > 0) _lowHp = Math.Min(_lowHp, (int)Math.Floor(Math.Max(0, _hp) * 100 / _ch.MaxHp));
            if (_ch.MaxMana > 0) _lowMa = Math.Min(_lowMa, (int)Math.Floor(Math.Max(0, _ma) * 100 / _ch.MaxMana));
        }

        private void Count(string code) => _casts[code] = _casts.GetValueOrDefault(code) + 1;

        private bool Roll(int percent) => percent >= 100 || (percent > 0 && _rng.Next(100) < percent);

        // A fractional chance, from one draw like the whole-percent roll; for a whole
        // percent the two agree draw for draw.
        private bool Roll(double percent) => percent >= 100 || (percent > 0 && _rng.NextDouble() * 100 < percent);

        private int Between(long min, long max) =>
            (int)(min >= max ? max : min + (long)(_rng.NextDouble() * (max - min + 1)));

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
