using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// PR 9.A — <see cref="CombatManager"/> target selection, swing
/// command emission, room-clear detection, and TargetOrder
/// (Normal vs Reverse) dispatch.
/// </summary>
public sealed class CombatManagerTests
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
        public List<byte[]> Sent { get; } = new();
        public CombatSettings Settings { get; set; } = new()
        {
            NormalAttackCommand = "a",
            TargetOrder = TargetOrder.Normal,
        };

        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();
        public string? OwnName { get; set; } = "MudPlay";
        public bool AutoCombatEnabled { get; set; } = true;
        // Drives the AttackPrevented gate — true = a stun/petrify/bind message is
        // active, so the engine must issue no attack (weapon or spell).
        public bool AttackPrevented { get; set; }
        // Drives the dark-room probe CombatManager reads to suppress its CR
        // "where am I" refreshes. Default false (lit) → refreshes fire as before.
        public bool Dark { get; set; }
        // Every weapon/off-hand swap the engine asked the actuator for, in order.
        // The actual wire commands (worn-diff + two-handed rule) are
        // EquipmentManager's job now, tested in EquipmentManagerTests; here we
        // pin the decision — which loadout combat requested and when.
        public List<(string? Weapon, string? OffHand)> Swaps { get; } = new();
        public List<bool> SwapForced { get; } = new();   // parallel to Swaps: the force flag per swap

        // When true, UI posts queue instead of running inline so a test can feed
        // a whole announce burst, then PumpUi() to flush the single coalesced
        // re-fire. Default false → posts run synchronously (matches the historic
        // one-line-per-Feed tests that assert on Sent right after Feed).
        public bool DeferUi { get; init; }
        private readonly Queue<Action> _uiJobs = new();
        public void PumpUi()
        {
            while (_uiJobs.Count > 0) _uiJobs.Dequeue()();
        }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: n => Overlays.TryGetValue(n, out MonsterOverlay? o)
                                     ? o : new MonsterOverlay(),
                party: Party,
                readSettings: () => Settings,
                isEnabled: () => AutoCombatEnabled,
                readOwnGivenName: () => OwnName,
                post: a => { if (DeferUi) _uiJobs.Enqueue(a); else a(); },
                log: Log);
            Combat.SetWireSender(b => Sent.Add(b));
            Combat.SetWeaponActuator((w, oh, force) => { Swaps.Add((w, oh)); SwapForced.Add(force); });
            Combat.SetDarkRoomProbe(() => Dark);
            Combat.SetAttackPreventedGate(() => AttackPrevented);
        }

        public void SetOverlay(int monsterNumber, MonsterAttackPriority? priority = null,
                               MonsterRelationship? relationship = null)
        {
            Overlays[monsterNumber] = new MonsterOverlay
            {
                Priority = priority,
                Relationship = relationship,
            };
        }

        public void AddMonster(int number, string name, bool killable,
                               bool allowNoPrefix = true,
                               params string[] flavorPrefixes)
        {
            string[] deathLines = killable
                ? new[] { $"The {name} dies." }
                : Array.Empty<string>();
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                Links: new[] { new GameDataLink("Monsters", number) }));
        }

        // Register a player so RoomEntityClassifier tags a matching "Also here:"
        // token as EntityKind.Player (needed for the follow-deferral tests, which
        // only defer when the followed player is visible in the room).
        public void AddPlayer(string givenName)
        {
            Players.Players.Add(new PlayerRecord(
                GivenName: givenName,
                FamilyName: string.Empty,
                Class: "Warrior",
                Race: "Human",
                Alignment: "Neutral",
                Title: null,
                Gang: null,
                Role: null,
                FirstSeenUtc: DateTime.UtcNow,
                LastSeenUtc: DateTime.UtcNow));
        }

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public string LastSent => Sent.Count == 0
            ? string.Empty
            : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        // Count the bare-CR "where am I" room-refresh nudges (a CR decodes to ""
        // once trimmed) — distinct from real attack commands.
        public int CrRefreshSends => Sent.Count(b =>
            Encoding.Latin1.GetString(b).TrimEnd('\r').Length == 0);

        public void Dispose()
        {
            Combat.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- master switch -----------------------------------------------

    [Fact]
    public void MasterOff_NoSwingSent()
    {
        using Harness h = new();
        h.AutoCombatEnabled = false;
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.Empty(h.Sent);
        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void MasterOn_OneMonster_SendsAttackBaseName()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.Single(h.Sent);
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    [Fact]
    public void AttackPrevented_HoldsAttack_NoCommandSent()
    {
        // A stun/petrify/bind message (AttackPrevented) is active, so the server
        // refuses every attack — the engine issues none. Same engage as
        // MasterOn_OneMonster_SendsAttackBaseName, which sends "a giant rat" with no
        // block; here nothing goes out until the wear-off clears the gate.
        using Harness h = new();
        h.AttackPrevented = true;
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void ExpGain_MidFight_DropsTargetPromptly()
    {
        // The exp line lands on the wire BEFORE the kill's *Combat Off* (death flavour
        // → exp → Off). Recognizing the kill here — not on the later Off — drops the
        // target so the round's alternate attack can't fire at the corpse (report
        // paradigm-20260814-230258: lbol kills → `mmis` at the dead mob). Generic:
        // needs no per-monster death message.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");                 // engage + swing (attack committed)
        Assert.Equal("giant rat", h.Combat.CurrentTarget);

        h.Feed("You gain 100 experience.");              // the kill — before any *Combat Off*

        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void CombatOff_WithoutExp_KeepsTarget()
    {
        // Breaking combat to cast a between-round (0-energy) spell emits a *Combat Off*
        // with NO exp line — that's our own attack being interrupted, NOT a kill. The
        // exp line is the discriminator, so with no exp the target must survive.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");
        Assert.Equal("giant rat", h.Combat.CurrentTarget);

        h.Feed("*Combat Off*");                          // no exp preceded it

        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    [Fact]
    public void AoeMultiKill_ReParsesWithCr_NoCorpseCast()
    {
        // report paradigm-20260817-105650: a manual AoE (fbal) wiped a whole room, then the
        // engine re-picked a survivor off the STALE roster and corpse-cast `mmis` at it
        // ("You don't see dark goblin archer here!"). On a multi-exp-line round the death
        // path must force ONE CR re-parse and re-pick from the true roster — not peel mobs
        // off one-by-one, which re-fires the re-pick against still-dying survivors.
        using Harness h = new();
        h.AddMonster(1, "dark goblin", killable: true);
        h.AddMonster(2, "dark goblin archer", killable: true);
        h.AddMonster(3, "angry dark goblin", killable: true);

        h.Feed("Also here: dark goblin, dark goblin archer, angry dark goblin.");
        int sentAfterEngage = h.Sent.Count;
        Assert.True(sentAfterEngage >= 1);   // engaged + swung

        // AoE wipe: several exp lines this round, then *Combat Off*.
        h.Feed("You gain 150 experience.");
        h.Feed("You gain 80 experience.");
        h.Feed("You gain 150 experience.");
        h.Feed("*Combat Off*");

        // The death event (AppServices wires MonsterDied → NoteUnattributedDeath).
        h.Combat.NoteUnattributedDeath();

        var after = h.Sent.Skip(sentAfterEngage)
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'))
            .ToList();
        Assert.Contains("", after);                    // a CR re-parse went out
        Assert.All(after, s => Assert.Equal("", s));   // and ONLY CRs — no attack sent at a corpse
    }

    [Fact]
    public void AoeMultiKill_MidRound_NoCombatOff_StillReParsesWithCr()
    {
        // report paradigm-20260818-052120: an AoE (fbal) dropped most of a room but a mob
        // survived and kept combat engaged, so NO *Combat Off* landed to trigger the
        // roster re-parse — the stale enemy count kept the room spell auto-repeating at
        // the lone survivor round after round ("FBAL keeps rooming despite only Queen Ant
        // left"). A 2nd+ exp gain in one round must force the CR re-parse mid-round, with
        // no Off, so the chooser re-picks the thinned roster and drops to single-target.
        using Harness h = new();
        h.AddMonster(1, "soldier ant", killable: true);
        h.AddMonster(2, "soldier ant", killable: true);
        h.AddMonster(3, "queen ant", killable: true);

        h.Feed("Also here: soldier ant, soldier ant, queen ant.");
        int sentAfterEngage = h.Sent.Count;

        // The AoE drops the two soldiers this round (two exp lines); the queen survives
        // and keeps combat, so no *Combat Off* / NoteUnattributedDeath ever comes.
        h.Feed("You gain 80 experience.");
        h.Feed("You gain 80 experience.");

        var after = h.Sent.Skip(sentAfterEngage)
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'))
            .ToList();
        Assert.Contains("", after);   // a CR re-parse went out mid-round without an Off
    }

    // ----- confusion-fumble retry (OnActionFailed) ----------------------

    [Fact]
    public void OnActionFailed_ResendsLastAttack_WhenFighting()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        // A fumble consumed the swing — re-send it verbatim.
        h.Combat.OnActionFailed();

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void OnActionFailed_NoOp_WhenNoTarget()
    {
        using Harness h = new();

        h.Combat.OnActionFailed();

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void OnActionFailed_NoOp_WhenDisabled()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        h.AutoCombatEnabled = false;
        h.Combat.OnActionFailed();

        Assert.Single(h.Sent);   // still just the original swing
    }

    // ----- guard-redirect retargeting -----------------------------------
    //
    // A MajorMUD "guarded" monster (brigand chief guarded by brigands) can't be
    // hit while a guard shields it — the server redirects our swing and emits
    // "<guard> moves to protect <chief>". CombatManager remembers the shielded
    // priority so it re-attacks it once the guard dies; the block is cleared when
    // the priority itself dies / leaves / on a room change. (The timed re-attack
    // on *Combat Off* is paced by the same stamps the interrupt-resume uses, so
    // its positive path is verified against a live BBS; here we pin the state
    // machine — when the block is set and when it's dropped.)

    [Fact]
    public void GuardProtect_SetsGuardBlock_WhenProtectedIsOurTarget()
    {
        using Harness h = new();
        h.AddMonster(1, "brigand chief", killable: true);
        h.AddMonster(2, "brigand", killable: true, allowNoPrefix: true, "large", "small");

        h.Feed("Also here: brigand chief.");
        Assert.Equal("brigand chief", h.Combat.CurrentTarget);
        Assert.Single(h.Sent);

        // A guard steps in front of the chief we're already trying to kill.
        h.Feed("large brigand moves to protect brigand chief");

        Assert.Equal("brigand chief", h.Combat.Snapshot().GuardBlockedTarget);
        // We stay on record as targeting the chief; recognising the guard is a
        // no-op on the wire (the server is already swinging the guard for us).
        Assert.Equal("brigand chief", h.Combat.CurrentTarget);
        Assert.Single(h.Sent);
    }

    [Fact]
    public void GuardProtect_Ignored_WhenProtectedIsNotOurTarget()
    {
        using Harness h = new();
        h.AddMonster(1, "brigand chief", killable: true);
        h.AddMonster(2, "brigand", killable: true, allowNoPrefix: true, "large", "small");

        // We're engaging a plain brigand, not the chief.
        h.Feed("Also here: brigand.");
        Assert.Equal("brigand", h.Combat.CurrentTarget);

        // A protect line shielding some OTHER monster isn't ours to chase.
        h.Feed("large brigand moves to protect brigand chief");

        Assert.Null(h.Combat.Snapshot().GuardBlockedTarget);
    }

    [Fact]
    public void GuardProtect_Ignored_WhenAutoCombatOff()
    {
        using Harness h = new();
        h.AddMonster(1, "brigand chief", killable: true);
        h.AddMonster(2, "brigand", killable: true, allowNoPrefix: true, "large", "small");

        h.Feed("Also here: brigand chief.");
        Assert.Equal("brigand chief", h.Combat.CurrentTarget);

        h.AutoCombatEnabled = false;
        h.Feed("large brigand moves to protect brigand chief");

        Assert.Null(h.Combat.Snapshot().GuardBlockedTarget);
    }

    [Fact]
    public void GuardBlock_ClearedWhenPriorityDies()
    {
        using Harness h = new();
        h.AddMonster(1, "brigand chief", killable: true);
        h.AddMonster(2, "brigand", killable: true, allowNoPrefix: true, "large", "small");

        h.Feed("Also here: brigand chief.");
        h.Feed("large brigand moves to protect brigand chief");
        Assert.Equal("brigand chief", h.Combat.Snapshot().GuardBlockedTarget);

        // The chief finally falls (last guard dead → direct hit → death line).
        h.Combat.NoteMonsterDied("brigand chief");

        Assert.Null(h.Combat.Snapshot().GuardBlockedTarget);
        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void GuardBlock_SurvivesGuardDeath_ClearsOnlyOnPriorityDeath()
    {
        using Harness h = new();
        h.AddMonster(1, "brigand chief", killable: true);
        h.AddMonster(2, "brigand", killable: true, allowNoPrefix: true, "large", "small");

        h.Feed("Also here: brigand chief.");
        h.Feed("large brigand moves to protect brigand chief");
        Assert.Equal("brigand chief", h.Combat.Snapshot().GuardBlockedTarget);

        // A GUARD dying must not drop the block — the chief is still shielded /
        // pending until it itself dies.
        h.Combat.NoteMonsterDied("brigand");
        Assert.Equal("brigand chief", h.Combat.Snapshot().GuardBlockedTarget);
    }

    [Fact]
    public void GuardBlock_ClearedOnRoomChange()
    {
        using Harness h = new();
        h.AddMonster(1, "brigand chief", killable: true);
        h.AddMonster(2, "brigand", killable: true, allowNoPrefix: true, "large", "small");

        h.Feed("Also here: brigand chief.");
        h.Feed("large brigand moves to protect brigand chief");
        Assert.Equal("brigand chief", h.Combat.Snapshot().GuardBlockedTarget);

        h.Classifier.NoteRoomChanged();

        Assert.Null(h.Combat.Snapshot().GuardBlockedTarget);
    }

    [Fact]
    public void GuardBlock_ClearedOnTargetNotHere()
    {
        using Harness h = new();
        h.AddMonster(1, "brigand chief", killable: true);
        h.AddMonster(2, "brigand", killable: true, allowNoPrefix: true, "large", "small");

        h.Feed("Also here: brigand chief.");
        h.Feed("large brigand moves to protect brigand chief");
        Assert.Equal("brigand chief", h.Combat.Snapshot().GuardBlockedTarget);

        // Server says the priority isn't here anymore — end the chase.
        h.Feed("You don't see brigand chief here!");

        Assert.Null(h.Combat.Snapshot().GuardBlockedTarget);
    }

    // ----- dark-room CR-refresh suppression -----------------------------

    [Fact]
    public void CommandNoEffect_LitRoom_FiresCrRefresh()
    {
        // Baseline: in a lit room a no-effect swing drops the target and fires the
        // recovery CR so the server re-displays the room for a fresh pick.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
        Assert.Equal(0, h.CrRefreshSends);

        h.Feed("Your command had no effect.");

        Assert.Null(h.Combat.CurrentTarget);   // target dropped
        Assert.Equal(1, h.CrRefreshSends);     // recovery CR fired
    }

    [Fact]
    public void CommandNoEffect_DarkRoom_SuppressesCrRefresh()
    {
        // In a dark room the same recovery path must NOT send a CR: a refresh
        // there returns only "you can't see anything", and that stale dark line is
        // dead-reckoned as a false confirmation of the movement loop's in-flight
        // step — the double-step-past-lairs bug. The target still drops; only the
        // blind CR is withheld.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");
        Assert.Equal("giant rat", h.Combat.CurrentTarget);

        h.Dark = true;
        h.Feed("Your command had no effect.");

        Assert.Null(h.Combat.CurrentTarget);   // target still dropped
        Assert.Equal(0, h.CrRefreshSends);     // no blind CR
    }

    [Fact]
    public void TargetNotHere_DarkRoom_SuppressesCrRefresh()
    {
        // The "You don't see X here!" recovery path shares the same suppression:
        // no CR refresh while we can't see.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");
        Assert.Equal("giant rat", h.Combat.CurrentTarget);

        h.Dark = true;
        h.Feed("You don't see giant rat here!");

        Assert.Null(h.Combat.CurrentTarget);
        Assert.Equal(0, h.CrRefreshSends);
    }

    [Fact]
    public void TargetNotHere_DarkRoom_ReEngagesPresentAttacker()
    {
        // Report paradigm-20260827-133337: after a dark room-move the old target
        // lingers in the roster (the CR refresh is a no-op in the dark), so
        // "You don't see X here!" used to just null the target and FREEZE — a
        // different attacker already in the room never got engaged, and the client
        // sat taking hits while only self-healing. Dropping the phantom directly
        // re-fires an observation that re-picks the present attacker.
        using Harness h = new();
        h.AddMonster(1, "big zombie cat", killable: true);
        h.AddMonster(2, "angry zombie cat", killable: true);
        h.Feed("Also here: big zombie cat, angry zombie cat.");   // engage the first
        Assert.Equal("big zombie cat", h.Combat.CurrentTarget);

        // In the dark, the big zombie cat didn't follow us into the new room.
        h.Dark = true;
        h.Feed("You don't see big zombie cat here!");

        Assert.Equal("angry zombie cat", h.Combat.CurrentTarget);   // re-engaged, not frozen
        Assert.Equal(0, h.CrRefreshSends);                          // still no blind CR
    }

    [Fact]
    public void PrefixedDisplay_AttackUsesPrefixedName()
    {
        // Critical for ambiguity resolution: when two variants of the
        // same base ("angry kobold thief" + "large kobold thief") are
        // in the same room, the server can't disambiguate
        // `attack kobold thief`. We always send the full prefixed
        // display name so the target uniquely identifies the
        // instance — and `attack nasty giant rat` works fine in the
        // single-instance case too.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true, allowNoPrefix: true, "nasty");

        h.Feed("Also here: nasty giant rat.");

        Assert.Equal("a nasty giant rat", h.LastSent);
    }

    [Fact]
    public void TwoSameBaseMonsters_AttackBySpecificPrefix()
    {
        // The user's reference scenario: "angry kobold thief" +
        // "large kobold thief" in the same room. We want to engage
        // the angry one (first by appearance, default priority +
        // Normal target order); the wire MUST send "attack angry
        // kobold thief", not "attack kobold thief", or the server
        // picks an instance for us.
        using Harness h = new();
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large", "fierce");

        h.Feed("Also here: angry kobold thief, large kobold thief.");

        Assert.Equal("a angry kobold thief", h.LastSent);
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void TwoSameBaseMonsters_OneDies_SwingsAtRemainingByPrefix()
    {
        // First instance dies (room re-displays without it); we must
        // detect the change and send a new attack against the
        // remaining instance by its prefixed name.
        using Harness h = new();
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Single(h.Sent);
        Assert.Equal("a angry kobold thief", h.LastSent);

        h.Feed("Also here: large kobold thief.");

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a large kobold thief", h.LastSent);
        Assert.Equal("large kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void TwoSameNameMonsters_FirstDies_ReSwingsAtRemaining()
    {
        // Live repro: Newhaven Arena had two "giant rat" entries with
        // no flavor prefix to distinguish them. After we killed one,
        // the other was still in the Also-Here list under the same
        // RawName. The pre-fix CombatManager hit the "server still
        // swinging at current" short-circuit (RawName matched) and
        // went silent while the surviving rat (and lashworm) kept
        // biting. NoteMonsterDied must clear _currentTarget so the
        // next observation re-issues `pu giant rat`.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true, allowNoPrefix: true);

        h.Feed("Also here: giant rat, giant rat, lashworm.");
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
        int initialSent = h.Sent.Count;

        // One giant rat dies — simulate the MonsterDeath path that
        // AppServices wires (NoteMonsterDied THEN remove).
        h.Combat.NoteMonsterDied("giant rat");
        Assert.Null(h.Combat.CurrentTarget);

        // The classifier's next Also-Here observation now shows the
        // surviving rat + lashworm; CombatManager must re-issue.
        h.Feed("Also here: giant rat, lashworm.");
        Assert.True(h.Sent.Count > initialSent,
            "expected a fresh `pu giant rat` after the same-name kill");
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    [Fact]
    public void UnattributedDeath_AttributesToCurrentTarget_ReSwingsAtSurvivor()
    {
        // Fallback-death repro (paradigm-20260716-095716 / -101002): the dataset
        // has no per-monster DeathLine, so every kill routes through the fallback
        // path (exp + *Combat Off*) that lands on NoteUnattributedDeath with no
        // roster slot handed to us. The pre-fix method left _currentTarget set and
        // only nudged a CR, so the dead mob lingered in the roster: the next tick
        // re-swung at the corpse and recovery waited out the idle-stall watchdog.
        // Now the death is attributed to _currentTarget, dropped from the roster,
        // and the survivor is re-picked immediately.
        using Harness h = new();
        h.AddMonster(1, "big margoyle", killable: false);
        h.AddMonster(2, "small margoyle", killable: false);

        h.Feed("Also here: big margoyle, small margoyle.");
        Assert.Equal("a big margoyle", h.LastSent);
        Assert.Equal("big margoyle", h.Combat.CurrentTarget);
        int initialSent = h.Sent.Count;

        // The fallback death fires — AppServices routes it to NoteUnattributedDeath.
        h.Combat.NoteUnattributedDeath();

        Assert.True(h.Sent.Count > initialSent,
            "expected a fresh swing at the survivor after the fallback kill");
        Assert.Equal("a small margoyle", h.LastSent);
        Assert.Equal("small margoyle", h.Combat.CurrentTarget);
    }

    [Fact]
    public void ExpInferredKill_ThenUnattributedDeath_StillDropsCorpse_ReSwingsAtSurvivor()
    {
        // Multi-mob regression (paradigm-20260815-081045): the exp line lands BEFORE the
        // kill's *Combat Off* and nulls _currentTarget so the round's alternate can't
        // corpse-cast. NoteUnattributedDeath then fires on that Off with _currentTarget
        // already null — before the fix it bailed, leaving the dead mob in the roster, so
        // the re-pick re-swung at the corpse ("aa <dead>" → "no effect") after a pause
        // until the fallback re-display. The stashed inferred-kill identity now lets the
        // Off-path drop the corpse and re-pick the survivor immediately.
        using Harness h = new();
        h.AddMonster(1, "nasty orc lieutenant", killable: false);
        h.AddMonster(2, "angry orc lieutenant", killable: false);

        h.Feed("Also here: nasty orc lieutenant, angry orc lieutenant.");
        Assert.Equal("a nasty orc lieutenant", h.LastSent);
        Assert.Equal("nasty orc lieutenant", h.Combat.CurrentTarget);

        // Exp line = prompt kill signal, before *Combat Off*. Drops the target (and now
        // stashes its identity for the Off-path roster removal).
        h.Feed("You gain 950 experience.");
        Assert.Null(h.Combat.CurrentTarget);
        int afterExp = h.Sent.Count;

        // The kill's *Combat Off* routes to NoteUnattributedDeath — _currentTarget is
        // already null, so the stashed identity drives the roster removal + re-pick.
        h.Combat.NoteUnattributedDeath();

        Assert.True(h.Sent.Count > afterExp,
            "expected a fresh swing at the survivor after the exp-inferred kill's Off");
        Assert.Equal("a angry orc lieutenant", h.LastSent);
        Assert.Equal("angry orc lieutenant", h.Combat.CurrentTarget);
    }

    [Fact]
    public void UnattributedDeath_LastMonster_ClearsTarget()
    {
        // The sole monster in the room dies via the fallback path. Attributing the
        // death to _currentTarget and dropping it empties the roster, so the
        // synthetic EntitiesObserved releases the Combat gate — no CR-nudge wait,
        // no re-swing at the corpse (the "sits idle after a kill" stall).
        using Harness h = new();
        h.AddMonster(1, "big margoyle", killable: false);

        h.Feed("Also here: big margoyle.");
        Assert.Equal("big margoyle", h.Combat.CurrentTarget);

        h.Combat.NoteUnattributedDeath();

        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void CustomAttackCommand_UsedVerbatim()
    {
        using Harness h = new();
        h.Settings.NormalAttackCommand = "attack";
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.Equal("attack giant rat", h.LastSent);
    }

    [Fact]
    public void BlankAttackCommand_DefaultsToLetterA()
    {
        // Empty / whitespace command falls back to "a" — matches the
        // DTO default + canonical MajorMUD alias.
        using Harness h = new();
        h.Settings.NormalAttackCommand = "";
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- engageable filter ------------------------------------------

    [Fact]
    public void ShopkeeperFlaggedFriend_NoSwing()
    {
        // Engageability is Relationship-based: a shopkeeper marked
        // Friend (via MonsterOverlay seed at the active set) gets
        // skipped. An empty DeathLine on its message record no
        // longer matters — the user's clarification: DeathLine is
        // the *death-message pattern*, not a "killable" flag, so
        // we can't use it as the engage gate.
        using Harness h = new();
        h.AddMonster(7, "shopkeeper", killable: true);
        h.SetOverlay(7, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: shopkeeper.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void UnknownToDataMonster_EmptyDeathLine_StillEngaged()
    {
        // Regression check for the acid-slime bug: 152 of 1100
        // monsters in stock data ship with empty DeathLine
        // (incomplete data, not unkillable). Without the
        // Relationship filter, those mobs would be silently
        // skipped — walker keeps walking while the server beats
        // on the player.
        using Harness h = new();
        h.AddMonster(99, "acid slime", killable: false);   // killable=false → empty DeathLine

        h.Feed("Also here: acid slime.");

        Assert.Single(h.Sent);
        Assert.Equal("a acid slime", h.LastSent);
    }

    [Fact]
    public void PlayerInRoom_Ignored_NoSwing()
    {
        using Harness h = new();

        h.Feed("Also here: Bob.");

        Assert.Empty(h.Sent);
    }

    // ----- target order ------------------------------------------------

    [Fact]
    public void TargetOrderNormal_PicksFirstInAlsoHere()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat",  killable: true);
        h.AddMonster(2, "goblin",     killable: true);

        h.Feed("Also here: giant rat, goblin.");

        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    [Fact]
    public void TargetOrderReverse_PicksLastInAlsoHere()
    {
        using Harness h = new();
        h.Settings.TargetOrder = TargetOrder.Reverse;
        h.AddMonster(1, "giant rat",  killable: true);
        h.AddMonster(2, "goblin",     killable: true);

        h.Feed("Also here: giant rat, goblin.");

        Assert.Equal("a goblin", h.LastSent);
    }

    // ----- room-clear detection ---------------------------------------

    [Fact]
    public void RoomCleared_CurrentTargetReset()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.NotNull(h.Combat.CurrentTarget);

        h.Feed("Also here: Bob.");      // monster gone — only player left
        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void TargetStillPresent_NoExtraSwing()
    {
        // Server keeps swinging at the named target each round; we
        // must NOT re-send the attack on every Also-Here re-display.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        h.Feed("Also here: giant rat.");     // re-display mid-fight
        h.Feed("Also here: giant rat.");

        Assert.Single(h.Sent);
    }

    [Fact]
    public void TargetGoneButNewMonsterPresent_RepicksAndSwings()
    {
        // First mob dies, room re-displays with a different mob —
        // CombatManager swings at the next one.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "goblin",    killable: true);

        h.Feed("Also here: giant rat, goblin.");
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Single(h.Sent);

        h.Feed("Also here: goblin.");
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a goblin", h.LastSent);
    }

    [Fact]
    public void IdenticalNameMonsters_OneCommandCoversAll()
    {
        // Three giant rats present — server treats `a giant rat` as
        // a continuous fight against the named base. We send once
        // and stay quiet through subsequent re-displays until ALL
        // are dead.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat, giant rat, giant rat.");
        h.Feed("Also here: giant rat, giant rat.");        // one died
        h.Feed("Also here: giant rat.");                    // two died

        Assert.Single(h.Sent);
        Assert.Equal("a giant rat", h.LastSent);

        h.Feed("Also here: Bob.");                          // last died
        Assert.Null(h.Combat.CurrentTarget);
    }

    // ----- master toggle off mid-fight --------------------------------

    [Fact]
    public void MasterOffMidFight_ClearsCurrentTarget()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.NotNull(h.Combat.CurrentTarget);

        h.AutoCombatEnabled = false;
        h.Feed("Also here: giant rat.");        // re-display
        Assert.Null(h.Combat.CurrentTarget);
    }

    // ----- no wire sender bound ---------------------------------------

    [Fact]
    public void NoWireSender_LogsButDoesNotThrow()
    {
        Harness h = new();
        // Don't bind the sender — verify the null-path no-throw shape.
        CombatManager combat = new(h.Router, h.Classifier, h.Monsters,
            resolveOverlay: _ => new MonsterOverlay(),
            party: h.Party,
            readSettings: () => h.Settings,
            isEnabled: () => h.AutoCombatEnabled,
            readOwnGivenName: () => h.OwnName,
            post: a => a(),
            log: h.Log);

        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");

        // No throws; the engine tracked the target for state purposes
        // but didn't send anything.
        Assert.Equal("giant rat", combat.CurrentTarget);
        combat.Dispose();
        h.Dispose();
    }

    // ----- MonsterOverlay priority sort --------------------------------

    [Fact]
    public void HigherPriorityMonster_PickedBeforeLower()
    {
        // First (=0 in enum) beats Normal (=2) → goblin gets picked
        // even though it appears second in the Also-Here line.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "goblin",    killable: true);
        h.SetOverlay(2, priority: MonsterAttackPriority.First);

        h.Feed("Also here: giant rat, goblin.");

        Assert.Equal("a goblin", h.LastSent);
    }

    [Fact]
    public void EqualPriority_TiebreakOnAppearanceOrder()
    {
        // Both Normal → first in Also-Here wins under TargetOrder.Normal.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "goblin",    killable: true);

        h.Feed("Also here: giant rat, goblin.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void TargetOrderReverse_PicksLowestPriority()
    {
        // First > Normal > Last (enum value 0 / 2 / 4). Reverse mode
        // picks the LAST sorted entry — the lowest-priority mob.
        using Harness h = new();
        h.Settings.TargetOrder = TargetOrder.Reverse;
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "goblin",    killable: true);
        h.AddMonster(3, "rat king",  killable: true);
        h.SetOverlay(1, priority: MonsterAttackPriority.First);
        h.SetOverlay(3, priority: MonsterAttackPriority.Last);

        h.Feed("Also here: giant rat, goblin, rat king.");

        Assert.Equal("a rat king", h.LastSent);
    }

    // ----- MonsterOverlay relationship filter --------------------------

    [Fact]
    public void RelationshipFriend_SkippedEvenIfKillable()
    {
        using Harness h = new();
        h.AddMonster(1, "guardian", killable: true);
        h.SetOverlay(1, relationship: MonsterRelationship.Friend);

        h.Feed("Also here: guardian.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RelationshipNeutral_SkippedByDefault()
    {
        // Neutral defaults to "don't attack unless attacked first".
        // First-cut treats Neutral the same as Friend (skip); a future
        // PR can wire "attack all monsters" to bypass this.
        using Harness h = new();
        h.AddMonster(1, "merchant", killable: true);
        h.SetOverlay(1, relationship: MonsterRelationship.Neutral);

        h.Feed("Also here: merchant.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void RelationshipEnemy_Engaged()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.SetOverlay(1, relationship: MonsterRelationship.Enemy);

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void RelationshipNull_DefaultsToEnemy()
    {
        // No overlay entry at all → null Relationship → engineering
        // default is Enemy (skip is too aggressive; everything in the
        // game-data monster table is fightable unless tagged otherwise).
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- AttackTiming re-fire ---------------------------------------

    [Fact]
    public void AttackTimingDefault_DoesNotRefire()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");       // initial swing
        Assert.Single(h.Sent);

        h.Feed("Bob moves to attack giant rat.");
        Assert.Single(h.Sent);                  // no re-fire
    }

    [Fact]
    public void AttackTimingLastRoom_RefiresOnAnyone()
    {
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        h.Feed("Bob moves to attack giant rat.");
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void AttackTimingLastParty_RefiresOnPartyMemberOnly()
    {
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastParty;
        h.Party.Members.Add(new PartyMember { Name = "Bob" });
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        // Party member — re-fire.
        h.Feed("Bob moves to attack giant rat.");
        Assert.Equal(2, h.Sent.Count);

        // Non-party stranger — no re-fire.
        h.Feed("Stranger moves to attack giant rat.");
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void AttackTimingLastParty_FullNamedMember_MatchedByGivenName()
    {
        // Regression: PartyMember.Name holds the full `par` name
        // ("Raijin WuzHere") while the announce line carries only the given
        // name ("Raijin"). AttackLastParty must match by given name — a
        // full-vs-given mismatch meant the re-fire silently never happened
        // and the character stopped attacking after the party member.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastParty;
        h.Party.Members.Add(new PartyMember { Name = "Raijin WuzHere" });
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        h.Feed("Raijin moves to attack giant rat.");
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void AttackTimingLastParty_CoalescesAnnounceBurst()
    {
        // A combat round resolves the whole party's attacks at once, so their
        // "moves to attack <target>" announces arrive as one burst. We should
        // re-fire ONCE that round (landing after the last announce), not once
        // per party member. DeferUi holds the coalesced flush until PumpUi, so
        // the whole burst is processed first — the production shape where one
        // server flush emits every announce line before the dispatcher runs the
        // posted flush.
        using Harness h = new() { DeferUi = true };
        h.Settings.AttackTiming = AttackTiming.AttackLastParty;
        h.Party.Members.Add(new PartyMember { Name = "Suijin" });
        h.Party.Members.Add(new PartyMember { Name = "Raijin" });
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");        // initial swing (synchronous)
        Assert.Single(h.Sent);

        // Burst of party announces on our target — one round. The duplicated
        // Suijin line mirrors a live capture where the same announce emitted
        // twice; it must not add a second send.
        h.Feed("Suijin moves to attack giant rat.");
        h.Feed("Suijin moves to attack giant rat.");
        h.Feed("Raijin moves to attack giant rat.");
        Assert.Single(h.Sent);                  // flush deferred — nothing sent yet

        h.PumpUi();
        Assert.Equal(2, h.Sent.Count);          // exactly one coalesced re-fire
        Assert.Equal("a giant rat", h.LastSent);

        // A later, separate burst still re-fires — coalescing is per-burst, not
        // a once-per-fight cap, so we keep landing last each round.
        h.Feed("Suijin moves to attack giant rat.");
        h.PumpUi();
        Assert.Equal(3, h.Sent.Count);
    }

    [Fact]
    public void AttackTimingAfter_RefiresOnNamedPlayerOnly()
    {
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackAfter;
        h.Settings.AttackAfterPlayerName = "Tank";
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        // Wrong player — no re-fire.
        h.Feed("Bob moves to attack giant rat.");
        Assert.Single(h.Sent);

        // Named player — re-fire.
        h.Feed("Tank moves to attack giant rat.");
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void OwnAnnounce_NeverRefires()
    {
        // Critical: if our own "MudPlay moves to attack giant rat" line
        // came through and we re-fired, we'd swing twice per round.
        using Harness h = new() { OwnName = "MudPlay" };
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        h.Feed("MudPlay moves to attack giant rat.");
        Assert.Single(h.Sent);                  // no re-fire
    }

    [Fact]
    public void Announce_NoCurrentTarget_NoRefire()
    {
        // Re-fire requires a target to re-issue against. Announce
        // before we've engaged → no-op.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;

        h.Feed("Bob moves to attack giant rat.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Announce_MasterOff_NoRefire()
    {
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        h.AutoCombatEnabled = false;
        h.Feed("Bob moves to attack giant rat.");
        Assert.Single(h.Sent);                  // no re-fire when master off
    }

    [Fact]
    public void AnnounceWithBracketedPromptPrefix_StillMatched()
    {
        // Real wire form: "[HP=100/MA=50]:Bob moves to attack giant rat."
        // — the regex tolerates the prompt prefix.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        h.Feed("[HP=100/MA=50]:Bob moves to attack giant rat.");

        Assert.Equal(2, h.Sent.Count);
    }

    // ----- Attack-last works UNDER a Follow priority (who + when are
    // independent knobs) + caster announces count -----------------------

    [Fact]
    public void AttackLastParty_UnderFollowLeader_MemberAnnounceRefires()
    {
        // Report: attack-last stopped working under a Follow priority. Following
        // the leader's TARGET (who) and attacking LAST (when) are independent — a
        // non-leader member announcing our target must still re-fire so our
        // *Combat Engaged* lands after them. (Regression: the Follow* early-return
        // skipped Attack Order entirely, so this never re-fired.)
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Settings.AttackTiming = AttackTiming.AttackLastParty;
        h.Party.LeaderName = "Boss";
        h.Party.Members.Add(new PartyMember { Name = "Boss" });
        h.Party.Members.Add(new PartyMember { Name = "Sidekick" });
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);

        h.Feed("Sidekick moves to attack giant rat.");
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);   // never switched
    }

    [Fact]
    public void AttackLastParty_UnderFollowLeader_LeaderReannounceRefires()
    {
        // Two-member party: the leader is the ONLY other member, so attack-last
        // must re-fire on the leader's own announce. Its already-engaged follow
        // hold now schedules the coalesced re-fire; without it a duo could never
        // land after the leader.
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Settings.AttackTiming = AttackTiming.AttackLastParty;
        h.Party.LeaderName = "Boss";
        h.Party.Members.Add(new PartyMember { Name = "Boss" });
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);

        h.Feed("Boss moves to attack giant rat.");
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);   // held, not switched
    }

    [Fact]
    public void AttackLastParty_UnderFollowLeader_BurstCoalescesToOneRefire()
    {
        // Leader + member both announce our target in one round burst. The
        // leader's follow-hold and the member's attack-order path share the
        // coalescing guard, so the whole burst still collapses to a SINGLE
        // re-fire landing last.
        using Harness h = new() { DeferUi = true };
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Settings.AttackTiming = AttackTiming.AttackLastParty;
        h.Party.LeaderName = "Boss";
        h.Party.Members.Add(new PartyMember { Name = "Boss" });
        h.Party.Members.Add(new PartyMember { Name = "Sidekick" });
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");            // initial swing (synchronous)
        Assert.Single(h.Sent);

        h.Feed("Boss moves to attack giant rat.");
        h.Feed("Sidekick moves to attack giant rat.");
        Assert.Single(h.Sent);                       // flush deferred

        h.PumpUi();
        Assert.Equal(2, h.Sent.Count);               // one coalesced re-fire
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void FollowLeader_DefaultTiming_MemberAnnounceOnOurTarget_NoRefire()
    {
        // The earlier fix (ac11796) stopped a redundant re-fire when a member
        // announced our target under a Follow priority with Default (own-cadence)
        // timing. That stays fixed: re-fire only wakes for an explicit attack-last
        // mode, so Default never re-fires even under FollowLeader.
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;   // AttackTiming left Default
        h.Party.LeaderName = "Boss";
        h.Party.Members.Add(new PartyMember { Name = "Boss" });
        h.Party.Members.Add(new PartyMember { Name = "Sidekick" });
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        h.Feed("Sidekick moves to attack giant rat.");
        Assert.Single(h.Sent);                       // Default timing — no re-fire
    }

    [Fact]
    public void CastAnnounce_PartyMemberOnOurTarget_Refires()
    {
        // GAME_MECHANICS: a caster's "moves to cast <spell> upon <mob>" is an
        // equivalent per-member round announce. Under attack-last it re-fires just
        // like a melee announce so we still land after the party's casters.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastParty;
        h.Party.Members.Add(new PartyMember { Name = "Priest" });
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        h.Feed("Priest moves to cast lightning bolt upon giant rat.");
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void CastAnnounce_HealOnPlayer_NoRefire()
    {
        // A heal/buff cast names a PLAYER ("... upon Boss"). It must never re-fire
        // — that would swing us at a teammate. The "must equal our current target"
        // guard makes a non-mob cast a safe no-op.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastParty;
        h.Party.Members.Add(new PartyMember { Name = "Priest" });
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        h.Feed("Priest moves to cast heal upon Boss.");
        Assert.Single(h.Sent);                       // heal target ≠ our mob → no-op
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    [Fact]
    public void CastAnnounce_OwnCast_NeverRefires()
    {
        using Harness h = new() { OwnName = "MudPlay" };
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        h.Feed("MudPlay moves to cast lightning bolt upon giant rat.");
        Assert.Single(h.Sent);                       // our own cast — no re-fire
    }

    // ----- Attack Order: re-fire OUR target, ONLY on announces against
    // that same target — never switches the monster -------------------

    [Fact]
    public void AttackLastParty_RePositionsOnlyAgainstOurTarget()
    {
        // Attack Order is the "when", not the "who". Two kobold thiefs;
        // we pick angry by default. A party announce against the LARGE
        // one is a different monster — ignored. A party announce against
        // OUR target (angry) re-fires to reclaim last position; it never
        // switches the monster (that's Target Priority's job).
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastParty;
        h.Party.Members.Add(new PartyMember { Name = "Tank" });
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);

        // Different monster — ignored, never switches.
        h.Feed("Tank moves to attack large kobold thief.");
        Assert.Single(h.Sent);
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);

        // Our target — re-fire to reclaim last.
        h.Feed("Tank moves to attack angry kobold thief.");
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a angry kobold thief", h.LastSent);
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void AttackLastRoom_RePositionsOnlyAgainstOurTarget()
    {
        // Room-mode is "be last in initiative on MY target" for ANY
        // player. A stranger on a different monster is ignored; a
        // stranger on our target re-fires.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);

        // Different monster — ignored.
        h.Feed("Stranger moves to attack large kobold thief.");
        Assert.Single(h.Sent);
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);

        // Our target — any player re-fires.
        h.Feed("Stranger moves to attack angry kobold thief.");
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a angry kobold thief", h.LastSent);
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);
    }

    // ----- Target Priority: switch our target to follow leader/member -

    [Fact]
    public void TargetPriorityFollowLeader_SwitchesToLeadersTarget()
    {
        // Default pick is angry; party leader announces the LARGE one.
        // FollowLeader switches our target to match the leader's instance.
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Party.LeaderName = "Boss";
        h.Party.Members.Add(new PartyMember { Name = "Boss" });
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);

        h.Feed("Boss moves to attack large kobold thief.");

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a large kobold thief", h.LastSent);
        Assert.Equal("large kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void TargetPriorityFollowLeader_FullNamedLeader_MatchedByGivenName()
    {
        // LeaderName is the full `par` name; the announce carries the given
        // name. FollowLeader must normalise both to the given name or it never
        // recognises a full-named leader's target announce.
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Party.LeaderName = "Boss McBossface";
        h.Party.Members.Add(new PartyMember { Name = "Boss McBossface" });
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);

        h.Feed("Boss moves to attack large kobold thief.");

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a large kobold thief", h.LastSent);
        Assert.Equal("large kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void TargetPriorityFollowLeader_IgnoresNonLeaderAnnounce()
    {
        // Only the leader's announce drives the switch. A non-leader
        // party member shouting a different target is ignored.
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Party.LeaderName = "Boss";
        h.Party.Members.Add(new PartyMember { Name = "Boss" });
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);

        h.Feed("Sidekick moves to attack large kobold thief.");

        Assert.Single(h.Sent);                              // no switch, no re-fire
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void TargetPriorityFollowMember_SwitchesToNamedMembersTarget()
    {
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowMember;
        h.Settings.TargetPriorityMemberName = "Healer";
        h.Party.Members.Add(new PartyMember { Name = "Healer" });
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);

        h.Feed("Healer moves to attack large kobold thief.");

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a large kobold thief", h.LastSent);
        Assert.Equal("large kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void TargetPriorityFollowMember_IgnoresOtherAnnouncers()
    {
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowMember;
        h.Settings.TargetPriorityMemberName = "Healer";
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);

        h.Feed("Tank moves to attack large kobold thief.");

        Assert.Single(h.Sent);                              // wrong member — ignored
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void TargetPriorityFollowLeader_NoLeader_DoesNotSwitch()
    {
        // FollowLeader with no leader set has nothing to follow.
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);

        h.Feed("Boss moves to attack large kobold thief.");

        Assert.Single(h.Sent);
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void TargetPriorityFollowMember_TargetNotInRoomView_LiteralAttack()
    {
        // The member announces a monster we don't have in our classifier
        // view (e.g. abbreviated / unseen). We still follow by sending a
        // literal attack against the announced name so we don't desync.
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowMember;
        h.Settings.TargetPriorityMemberName = "Healer";
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);

        h.Feed("Healer moves to attack cave bear.");

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a cave bear", h.LastSent);
        Assert.Equal("cave bear", h.Combat.CurrentTarget);
    }

    // ----- Target Priority: follow deferral (hold own pick, wait for announce) -

    [Fact]
    public void FollowLeader_InPartyLeaderPresent_DefersOwnPickUntilAnnounce()
    {
        // In a party, FollowLeader, leader in the room, multiple mobs: we must
        // NOT eagerly burst-attack our own pick. Hold, wait for the leader's
        // announce, then engage THAT mob — no wasted swing on the wrong monster.
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Party.LeaderName = "Boss";
        h.Party.Members.Add(new PartyMember { Name = "Boss" });
        h.AddPlayer("Boss");
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: Boss, angry kobold thief, large kobold thief.");
        Assert.Empty(h.Sent);                       // held — nothing sent yet
        Assert.Null(h.Combat.CurrentTarget);

        h.Feed("Boss moves to attack large kobold thief.");
        Assert.Single(h.Sent);                      // first swing is the leader's mob
        Assert.Equal("a large kobold thief", h.LastSent);
        Assert.Equal("large kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void FollowLeader_DeferNoAnnounce_TickFallsBackToOwnPick()
    {
        // Held awaiting a leader announce, but the round elapses with none — the
        // combat tick falls back to our own game-data pick so we don't stall.
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Party.LeaderName = "Boss";
        h.Party.Members.Add(new PartyMember { Name = "Boss" });
        h.AddPlayer("Boss");
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: Boss, angry kobold thief, large kobold thief.");
        Assert.Empty(h.Sent);                       // held

        h.Combat.OnCombatTick();                     // round heartbeat, no announce
        Assert.Single(h.Sent);
        Assert.Equal("a angry kobold thief", h.LastSent);   // own pick (highest priority / first)
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void FollowLeader_AnnounceOnAlreadyEngaged_NoRedundantRefire()
    {
        // Once we're on the leader's target, a repeat announce against that SAME
        // mob must not re-issue the attack — the server already auto-swings at it.
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Party.LeaderName = "Boss";
        h.Party.Members.Add(new PartyMember { Name = "Boss" });
        h.AddPlayer("Boss");
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: Boss, angry kobold thief, large kobold thief.");
        h.Feed("Boss moves to attack large kobold thief.");
        Assert.Single(h.Sent);
        Assert.Equal("large kobold thief", h.Combat.CurrentTarget);

        h.Feed("Boss moves to attack large kobold thief.");   // repeat on same mob
        Assert.Single(h.Sent);                                // suppressed
        Assert.Equal("large kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void FollowLeader_NotInParty_NoDeferNoFollow()
    {
        // Party disbanded / we left: TargetPriority reverts to our own game-data
        // pick. No deferral, and the ex-leader's announce no longer switches us.
        using Harness h = new();
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Party.LeaderName = "Boss";
        h.AddPlayer("Boss");
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: Boss, angry kobold thief, large kobold thief.");
        Assert.Single(h.Sent);                      // eager own pick, no hold
        Assert.Equal("a angry kobold thief", h.LastSent);

        h.Feed("Boss moves to attack large kobold thief.");
        Assert.Single(h.Sent);                      // no follow — not partied
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void FollowLeader_SingleMob_NoDefer()
    {
        // One engageable mob — nothing to coordinate, so we take it immediately
        // rather than holding for an announce.
        using Harness h = new();
        h.Party.IsInParty = true;
        h.Settings.TargetPriority = TargetPriority.FollowLeader;
        h.Party.LeaderName = "Boss";
        h.Party.Members.Add(new PartyMember { Name = "Boss" });
        h.AddPlayer("Boss");
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: Boss, giant rat.");
        Assert.Single(h.Sent);
        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- room change drops current target ----------------------------

    [Fact]
    public void RoomChange_DropsCurrentTarget_NoExtraSwing()
    {
        // User's "wasted round on move" scenario. We attack X in room
        // A; classifier wipes on the room change; CombatManager
        // clears _currentTarget. No spurious wire send during the
        // wipe — the next Also-Here parse rebuilds the picture and
        // drives a fresh pick (covered separately).
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
        Assert.Single(h.Sent);

        // Walker moves rooms — the classifier's NoteRoomChanged
        // wipes.
        h.Classifier.NoteRoomChanged();

        Assert.Null(h.Combat.CurrentTarget);
        Assert.Single(h.Sent);                  // no extra send during wipe
    }

    [Fact]
    public void RoomChange_ThenNewRoomAlsoHere_PicksFreshTarget()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "goblin",    killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Equal("giant rat", h.Combat.CurrentTarget);

        h.Classifier.NoteRoomChanged();
        Assert.Null(h.Combat.CurrentTarget);

        h.Feed("Also here: goblin.");
        Assert.Equal("goblin", h.Combat.CurrentTarget);
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a goblin", h.LastSent);
    }

    [Fact]
    public void AttackAfter_RePositionsOnlyAgainstOurTarget()
    {
        // AttackAfter is pure timing: re-fire OUR target when the named
        // player announces against THAT target, keeping our swing right
        // after theirs. A named-player announce on a different monster is
        // ignored; it never switches to their monster (Target Priority's
        // job).
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackAfter;
        h.Settings.AttackAfterPlayerName = "Tank";
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);    // own pick

        // Named player, different monster — ignored.
        h.Feed("Tank moves to attack large kobold thief.");
        Assert.Single(h.Sent);
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);

        // Named player, our target — re-fire.
        h.Feed("Tank moves to attack angry kobold thief.");
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a angry kobold thief", h.LastSent);    // re-fire, unchanged
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);
    }

    // ----- safety net: combat line + empty room → send bare CR --------

    [Fact]
    public void CombatLine_WithNoEngageable_SendsCarriageReturnToRefreshRoom()
    {
        // Real-world bug: monster swings at us but our classifier shows
        // no engageable. Without the safety net we sit dumbstruck. With
        // it, we send a bare CR (^M) — the server's compact re-display
        // includes Also Here + prompt without the full room dump, so
        // the classifier rebuilds the picture cheaply.
        using Harness h = new();
        h.AddMonster(1, "kobold thief", killable: true);

        // No room observation yet — classifier.Current is null.
        h.Feed("The kobold thief swings at you but misses!");

        Assert.Single(h.Sent);
        // Bare CR — LastSent strips trailing \r so the body is empty.
        Assert.Equal("\r", Encoding.Latin1.GetString(h.Sent[^1]));
    }

    [Fact]
    public void CombatLine_WithEngageableInRoom_DoesNotRefresh()
    {
        // Normal case: we have a target. Combat line is expected; no
        // wire send.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Single(h.Sent);

        h.Feed("The giant rat swings at you but misses!");

        Assert.Single(h.Sent);                  // no CR — engageable still here
    }

    [Fact]
    public void CombatLine_Debounced_NoSecondRefreshWithinCooldown()
    {
        // Burst of miss lines must not flood the wire with bare CRs.
        using Harness h = new();

        h.Feed("The kobold thief swings at you but misses!");
        h.Feed("The kobold thief swings at you but misses!");
        h.Feed("The kobold thief swings at you but misses!");

        Assert.Single(h.Sent);
        Assert.Equal("\r", Encoding.Latin1.GetString(h.Sent[^1]));
    }

    // ----- target-not-here: server says we whiffed against a phantom -

    [Fact]
    public void TargetNotHere_DropsCurrentTarget_AndRefreshesRoom()
    {
        // Our `attack giant rat` raced the rat's death (or it fled);
        // server replies "You don't see giant rat here!". Drop our
        // target and force a re-display so the next round picks fresh.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);

        h.Feed("You don't see giant rat here!");

        Assert.Null(h.Combat.CurrentTarget);
        Assert.Equal("\r", Encoding.Latin1.GetString(h.Sent[^1]));
    }

    [Fact]
    public void TargetNotHere_WithoutCurrentTarget_NoOp()
    {
        // No target → nothing to drop. Don't send CR either; some
        // other system can react to the target-gone signal if needed.
        using Harness h = new();
        h.Feed("You don't see giant rat here!");
        Assert.Empty(h.Sent);
    }

    // ----- Weapon-swap matrix ----------------------------------------

    [Fact]
    public void Attack_RequestsNormalWeapon_BeforeFirstSwing()
    {
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.Settings.NormalOffHand = "shield";
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        // The engine hands the normal loadout to the actuator, then swings. The
        // worn-diff / wire commands are EquipmentManager's concern.
        Assert.Equal(("longsword", "shield"), Assert.Single(h.Swaps));
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void WeaponNoEffect_RequestsAlternate_AndReSwings()
    {
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.Settings.AlternateWeapon = "warhammer";
        h.Settings.AlternateAttackCommand = "swing";
        h.AddMonster(1, "stone golem", killable: true);

        h.Feed("Also here: stone golem.");
        h.Swaps.Clear();

        h.Feed("Your weapon has no effect against this monster!");

        // Requested the alternate loadout and re-swung with the alt command.
        Assert.Equal(("warhammer", (string?)null), Assert.Single(h.Swaps));
        Assert.Equal("swing stone golem", h.LastSent);
    }

    [Fact]
    public void WeaponNoEffect_OnAlt_ForcesPhysicalSwapAndRetries()
    {
        // 210839: the alternate was "believed" equipped but never physically
        // swapped (a phantom swap off a stale snapshot). A no-effect while on the
        // alt must FORCE the real swap (bypassing the actuator's worn/carried
        // gates) and retry the alternate attack once — not concede with the weapon
        // path unswapped (the reported symptom: kept swinging the useless normal
        // weapon against a margoyle immune to it).
        using Harness h = new();
        h.Settings.NormalWeapon = "throwing hammers";
        h.Settings.AlternateWeapon = "golden broadsword";
        h.Settings.AlternateAttackCommand = "aa";
        h.AddMonster(1, "margoyle", killable: true);
        h.Feed("Also here: margoyle.");

        // First no-effect → request the alternate (now believed on it, unforced).
        h.Feed("Your weapon has no effect against this monster!");
        h.Swaps.Clear();
        h.SwapForced.Clear();
        h.Sent.Clear();

        // Second no-effect while believed on the alternate → force-swap + retry.
        h.Feed("Your weapon has no effect against this monster!");

        Assert.Contains(("golden broadsword", (string?)null), h.Swaps);
        Assert.Contains(true, h.SwapForced);            // the swap was forced this time
        Assert.Equal("aa margoyle", h.LastSent);        // alternate attack re-sent
    }

    [Fact]
    public void WeaponNoEffect_WholeChainExhausted_MarksUnactionable_AndMovesOn()
    {
        // No caster wired → the chain is just the two weapons. Once BOTH have drawn
        // "no effect" (the alt after its forced retry), the species' whole configured
        // chain is exhausted: the engine drops the target, fires a CR to re-pick /
        // move on, and CanEngageMonster reports it un-killable so the walker gate
        // releases instead of halting on it forever.
        using Harness h = new();
        h.Settings.NormalWeapon = "throwing hammers";
        h.Settings.AlternateWeapon = "golden broadsword";
        h.Settings.AlternateAttackCommand = "aa";
        h.AddMonster(1, "margoyle", killable: true);
        h.Feed("Also here: margoyle.");

        Assert.True(h.Combat.CanEngageMonster(1));       // actionable before any failure

        h.Feed("Your weapon has no effect against this monster!");   // normal → swap to alt
        h.Feed("Your weapon has no effect against this monster!");   // alt 1st → force-retry
        h.Sent.Clear();
        h.Feed("Your weapon has no effect against this monster!");   // alt 2nd → concede

        Assert.False(h.Combat.CanEngageMonster(1));      // whole chain exhausted this room
        Assert.Null(h.Combat.CurrentTarget);             // target dropped so we re-pick
        Assert.True(h.CrRefreshSends >= 1);              // CR fired to force a re-observe
    }

    [Fact]
    public void SpellCommandNoEffect_FallsBackToAlternateCommand()
    {
        // Report paradigm-20260809-131642: a SPELL used as the attack COMMAND
        // ("harm" in NormalAttackCommand) reaches the wire via the command path,
        // so the server's immunity reply is the SPELL line, not the weapon line.
        // The command path must still fall back to the alternate command.
        using Harness h = new();
        h.Settings.NormalAttackCommand = "harm";
        h.Settings.AlternateAttackCommand = "attack";
        h.AddMonster(1, "acid slime", killable: true);

        h.Feed("Also here: acid slime.");
        Assert.Equal("harm acid slime", h.LastSent);

        h.Feed("Your spell has no effect on acid slime.");

        Assert.Equal("attack acid slime", h.LastSent);   // dropped to the alternate command
    }

    [Fact]
    public void SpellCommandNoEffect_NoDistinctAlternate_ConcedesAndMovesOn()
    {
        // Same command-slot spell, but no distinct alternate to fall to → the
        // command is genuinely ineffective here: drop the target and CR to re-pick
        // rather than re-hammering the immune command every round.
        using Harness h = new();
        h.Settings.NormalAttackCommand = "harm";
        h.Settings.AlternateAttackCommand = "harm";   // same → nothing to fall to
        h.AddMonster(1, "acid slime", killable: true);
        h.Feed("Also here: acid slime.");
        h.Sent.Clear();

        h.Feed("Your spell has no effect on acid slime.");

        Assert.Null(h.Combat.CurrentTarget);
        Assert.True(h.CrRefreshSends >= 1);
    }

    [Fact]
    public void AttackCommandOverride_ForcesConfiguredCommand_OverGlobal()
    {
        // A per-monster "Override Attack" command forces that verb for the species,
        // ignoring the global NormalAttackCommand.
        using Harness h = new();
        h.Settings.NormalAttackCommand = "harm";
        h.AddMonster(1, "acid slime", killable: true);
        h.Overlays[1] = new MonsterOverlay { OverrideAttackCommand = "attack" };

        h.Feed("Also here: acid slime.");

        Assert.Equal("attack acid slime", h.LastSent);
        Assert.Equal("acid slime", h.Combat.CurrentTarget);
    }

    [Fact]
    public void AttackCommandOverride_IsTrusted_NoFallbackOnNoEffect()
    {
        // The forced command is trusted — a "no effect" line must NOT flip it to the
        // alternate (mirrors the spell-id override's bypass-gates contract).
        using Harness h = new();
        h.Settings.NormalAttackCommand = "harm";
        h.Settings.AlternateAttackCommand = "attack";
        h.AddMonster(1, "acid slime", killable: true);
        h.Overlays[1] = new MonsterOverlay { OverrideAttackCommand = "smite" };

        h.Feed("Also here: acid slime.");
        Assert.Equal("smite acid slime", h.LastSent);
        h.Sent.Clear();

        h.Feed("Your spell has no effect on acid slime.");

        Assert.Empty(h.Sent);                            // no fallback re-send
        Assert.Equal("acid slime", h.Combat.CurrentTarget);
    }

    [Fact]
    public void ExhaustedMonster_Skipped_EngineRetargetsAnotherHostile()
    {
        // With a second, killable hostile present, exhausting the first monster's
        // chain must not strand the engine — the next observation skips the
        // un-actionable one and attacks the other.
        using Harness h = new();
        h.Settings.NormalWeapon = "throwing hammers";
        h.Settings.AlternateWeapon = "golden broadsword";
        h.Settings.AlternateAttackCommand = "aa";
        h.AddMonster(1, "margoyle", killable: true);
        h.AddMonster(2, "giant rat", killable: true);
        h.Feed("Also here: margoyle, giant rat.");       // engages margoyle (first)

        h.Feed("Your weapon has no effect against this monster!");   // normal → alt
        h.Feed("Your weapon has no effect against this monster!");   // alt 1st → retry
        h.Feed("Your weapon has no effect against this monster!");   // alt 2nd → concede
        h.Sent.Clear();

        // Fresh room view (what the concede CR would draw): the retarget loop skips
        // the exhausted margoyle and swings at the giant rat.
        h.Feed("Also here: margoyle, giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    [Fact]
    public void Snapshot_ReflectsTargetAndAltFlag()
    {
        // Backs the bug-report engine-state dump. The worn weapon is no longer
        // shadowed here (live inventory owns that); the decision state is the
        // current target + whether we're on the alternate weapon.
        using Harness h = new();
        CombatManager.DebugState pre = h.Combat.Snapshot();
        Assert.Null(pre.CurrentTarget);
        Assert.False(pre.UsingAlternateWeapon);

        h.Settings.NormalWeapon = "longsword";
        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");

        CombatManager.DebugState post = h.Combat.Snapshot();
        Assert.Equal("giant rat", post.CurrentTarget);
        Assert.False(post.UsingAlternateWeapon);
    }

    [Fact]
    public void RoomCleared_RequestsNormal_WhenWasOnAlt()
    {
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.Settings.AlternateWeapon = "warhammer";
        h.AddMonster(1, "stone golem", killable: true);

        h.Feed("Also here: stone golem.");
        h.Feed("Your weapon has no effect against this monster!");
        h.Swaps.Clear();

        // Simulate room-cleared: drop the species from the classifier
        // so the next observation has no engageable. Use the
        // RemoveDeadEntity path which fires EntitiesObserved with the
        // post-removal list.
        h.Classifier.RemoveDeadEntity("stone golem");

        Assert.Contains(("longsword", (string?)null), h.Swaps);
    }

    [Fact]
    public void RoomCleared_DoesNotRequestBackstabWeapon_DeferredToPreMove()
    {
        // Equipping breaks sneak, so the backstab re-gear must happen in the
        // pre-move sequence (PrepBackstabForMove), immediately before the sn —
        // NOT raced at room-clear.
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.Settings.BackstabWeapon = "dagger";
        h.Settings.DoBackstab = true;
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        h.Swaps.Clear();

        h.Classifier.RemoveDeadEntity("giant rat");

        Assert.Empty(h.Swaps);
    }

    [Fact]
    public void PrepBackstabForMove_RequestsBackstabWeapon_WhenConfigured()
    {
        using Harness h = new();
        h.Settings.BackstabWeapon = "dagger";
        h.Settings.BackstabOffHand = "buckler";
        h.Settings.DoBackstab = true;

        h.Combat.PrepBackstabForMove();

        Assert.Equal(("dagger", "buckler"), Assert.Single(h.Swaps));
    }

    [Fact]
    public void PrepBackstabForMove_NoOp_WhenBackstabDisabled()
    {
        using Harness h = new();
        h.Settings.BackstabWeapon = "dagger";
        h.Settings.DoBackstab = false;

        h.Combat.PrepBackstabForMove();

        Assert.Empty(h.Swaps);
    }

    [Fact]
    public void PrepBackstabForMove_NoOp_WhenNoBackstabWeapon()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Settings.BackstabWeapon = "";

        h.Combat.PrepBackstabForMove();

        Assert.Empty(h.Swaps);
    }

    // ----- Backstab window (PR 4.c) ----------------------------------

    [Fact]
    public void Backstab_SendsBs_WhenSneaking_NoSeeHidden()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.AddMonster(1, "giant rat", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => true, hasSeeHidden: _ => false);

        h.Feed("Also here: giant rat.");

        // Opening swing into the room is `bs`, and the BS path must NOT
        // re-equip — the BS weapon is pre-equipped at room-clear.
        Assert.Equal("bs giant rat", h.LastSent);
        Assert.Empty(h.Swaps);
    }

    [Fact]
    public void Backstab_FallsBackToNormal_WhenSeeHiddenPresent()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "crystal golem", killable: true);
        // The golem (monster #2) carries SeeHidden — its presence reveals
        // the sneaker to the whole room, so the opening swing is a normal
        // attack, not a backstab.
        HashSet<int> seeHidden = new() { 2 };
        h.Combat.SetBackstabHooks(
            isStealthed: () => true,
            hasSeeHidden: n => seeHidden.Contains(n));

        h.Feed("Also here: giant rat, crystal golem.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void Backstab_Off_NormalAttack_EvenWhenSneaking()
    {
        using Harness h = new();
        h.Settings.DoBackstab = false;
        h.AddMonster(1, "giant rat", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => true, hasSeeHidden: _ => false);

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void Backstab_NotSneaking_NormalAttack()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.AddMonster(1, "giant rat", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => false, hasSeeHidden: _ => false);

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void Backstab_HooksUnset_NormalAttack()
    {
        // No SetBackstabHooks call — the BS branch is a safe no-op and
        // the engine falls through to a normal attack.
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void Backstab_ReopensOnManualRoomChange_WithoutPreMoveHook()
    {
        // Hand-walking never fires the walker's pre-move hook
        // (PrepBackstabForMove), so the classifier's RoomChange observation is
        // the only signal that re-opens the surprise round. Without keying the
        // opener reset off it, every backstab after the session's first would
        // fall through to a normal attack (the under-backstab report).
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "goblin", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => true, hasSeeHidden: _ => false);

        // Room A: opener fires, then is consumed.
        h.Feed("Also here: giant rat.");
        Assert.Equal("bs giant rat", h.LastSent);

        // Manual walk to room B — only the RoomChange wipe fires, NOT the
        // pre-move hook. The next room must still open with a backstab.
        h.Classifier.NoteRoomChanged();
        h.Feed("Also here: goblin.");

        Assert.Equal("bs goblin", h.LastSent);
    }

    // ----- Backstab surprise-round resolution ------------------------

    [Fact]
    public void Backstab_Failure_FoldedNormalRound_TriggersFlee()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Settings.RunIfBackstabFails = true;
        h.AddMonster(1, "dark cultist", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => true, hasSeeHidden: _ => false);
        bool fled = false;
        h.Combat.SetBackstabFailureFlee(() => fled = true);

        h.Feed("Also here: dark cultist.");
        Assert.Equal("bs dark cultist", h.LastSent);

        // First swing after bs lacks "surprise" — a folded normal round means
        // the backstab failed, so the flee hook fires.
        h.Feed("You punch dark cultist for 2 damage!");
        Assert.True(fled);
    }

    [Fact]
    public void Backstab_Failure_Whiff_TriggersFlee()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Settings.RunIfBackstabFails = true;
        h.AddMonster(1, "dark cultist", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => true, hasSeeHidden: _ => false);
        bool fled = false;
        h.Combat.SetBackstabFailureFlee(() => fled = true);

        h.Feed("Also here: dark cultist.");
        // A whiff ("You swing at X!", no "for N damage") is also a failure.
        h.Feed("You swing at dark cultist!");
        Assert.True(fled);
    }

    [Fact]
    public void Backstab_Success_SurpriseLine_NoFlee()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Settings.RunIfBackstabFails = true;
        h.AddMonster(1, "orc rogue", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => true, hasSeeHidden: _ => false);
        bool fled = false;
        h.Combat.SetBackstabFailureFlee(() => fled = true);

        h.Feed("Also here: orc rogue.");
        // The surprise line proves the opener landed — no flee.
        h.Feed("You surprise punch orc rogue for 30 damage!");
        Assert.False(fled);
    }

    [Fact]
    public void Backstab_Failure_NoFlee_WhenSettingOff()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Settings.RunIfBackstabFails = false;   // opt-out
        h.AddMonster(1, "dark cultist", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => true, hasSeeHidden: _ => false);
        bool fled = false;
        h.Combat.SetBackstabFailureFlee(() => fled = true);

        h.Feed("Also here: dark cultist.");
        h.Feed("You punch dark cultist for 2 damage!");
        Assert.False(fled);
    }

    [Fact]
    public void Backstab_SelfEmoteDuringWindow_DoesNotResolve()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Settings.RunIfBackstabFails = true;
        h.AddMonster(1, "dark cultist", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => true, hasSeeHidden: _ => false);
        bool fled = false;
        h.Combat.SetBackstabFailureFlee(() => fled = true);

        h.Feed("Also here: dark cultist.");

        // A self-emote also matches the broad UserMisses pattern, but it doesn't
        // name the target — it must NOT be read as the resolution swing.
        h.Feed("You feel much better!");
        Assert.False(fled);

        // The watch is still armed, so the real failure line that follows still
        // triggers the flee.
        h.Feed("You punch dark cultist for 2 damage!");
        Assert.True(fled);
    }

    [Fact]
    public void Backstab_SuppressesRefire_UntilResolved()
    {
        // The surprise round must stay quiet — a party announce that would
        // normally re-fire our attack is held while the bs is pending, so the
        // repositioning `pu` can't clobber the surprise (the over-send report).
        using Harness h = new() { DeferUi = true };
        h.Settings.DoBackstab = true;
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.AddMonster(1, "giant rat", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => true, hasSeeHidden: _ => false);

        h.Feed("Also here: giant rat.");          // opener: bs (synchronous)
        Assert.Single(h.Sent);

        // Announce during the pending surprise round — no re-fire scheduled.
        h.Feed("Bob moves to attack giant rat.");
        h.PumpUi();
        Assert.Single(h.Sent);

        // The surprise round resolves (success) — re-fire suppression lifts.
        h.Feed("You surprise punch giant rat for 30 damage!");

        // A later announce now re-fires normally.
        h.Feed("Bob moves to attack giant rat.");
        h.PumpUi();
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void Backstab_ArmedOpener_AttackLastRefiresWithBs()
    {
        // Report 074641/074756: attack-last + a re-armed surprise round. When a
        // party announce triggers the attack-last re-fire while the backstab
        // opener is still ARMED (stealthed, unspent), the re-fire IS the opener
        // — it must send `bs <target>`, not the configured normal attack. The
        // mystic was re-firing `pu giant rat` and wasting the surprise; the
        // re-fire now opens with bs while still landing us last in initiative.
        using Harness h = new() { DeferUi = true };
        h.Settings.DoBackstab = true;
        h.Settings.NormalAttackCommand = "pu";
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.AddMonster(1, "giant rat", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => true, hasSeeHidden: _ => false);

        // Opener fires bs and is consumed; the target is held.
        h.Feed("Also here: giant rat.");
        Assert.Equal("bs giant rat", h.LastSent);

        // Opener lands — the surprise-resolution watch clears so the re-fire
        // path is no longer suppressed.
        h.Feed("You surprise punch giant rat for 30 damage!");

        // A fresh sneak-approach re-arms the surprise round for the same target
        // (a same-name mob still in view keeps _currentTarget across the re-arm).
        h.Combat.PrepBackstabForMove();
        int before = h.Sent.Count;

        // Party member announces — attack-last re-fires to reclaim last, but the
        // opener is armed, so the re-fire opens with bs, never `pu`.
        h.Feed("Bob moves to attack giant rat.");
        h.PumpUi();

        Assert.Equal(before + 1, h.Sent.Count);
        Assert.Equal("bs giant rat", h.LastSent);
    }

    [Fact]
    public void Backstab_RearmForHide_ReopensOpenerWithoutRoomChange()
    {
        // The stationary hidden opener: a hidden character opens on a monster that
        // walks in, spends the surprise round, then re-hides IN PLACE (no room
        // change) and a new monster wanders in. RearmBackstabForHide re-opens the
        // surprise round off the fresh hide so the newcomer is a backstab target,
        // even though the RoomChange re-arm never fired.
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "goblin", killable: true);
        h.AddMonster(3, "dark elf", killable: true);
        h.Combat.SetBackstabHooks(isStealthed: () => true, hasSeeHidden: _ => false);

        // First arrival: opener fires, then is consumed.
        h.Feed("Also here: giant rat.");
        Assert.Equal("bs giant rat", h.LastSent);

        // A different monster in the SAME room (no room change) — opener spent, so
        // it falls back to a normal attack.
        h.Feed("Also here: goblin.");
        Assert.Equal("a goblin", h.LastSent);

        // Re-hide in place re-arms the surprise round; the next arrival opens `bs`.
        h.Combat.RearmBackstabForHide();
        h.Feed("Also here: dark elf.");
        Assert.Equal("bs dark elf", h.LastSent);
    }

    // ----- seehidden clear override (PR 4.c-b) -----------------------

    [Fact]
    public void SeeHiddenOverride_EngagesDespiteCombatOff()
    {
        // Combat OFF normally means no swing. When CombatStateTracker has
        // the force-clear latched (SeeHiddenClearActive=true), CombatManager
        // engages anyway so the stealth runner clears the room.
        using Harness h = new();
        h.AutoCombatEnabled = false;
        h.Combat.SetSeeHiddenClearGate(() => true);
        h.AddMonster(1, "crystal golem", killable: true);

        h.Feed("Also here: crystal golem.");

        Assert.Equal("a crystal golem", h.LastSent);
        Assert.Equal("crystal golem", h.Combat.CurrentTarget);
    }

    [Fact]
    public void SeeHiddenOverride_CombatOff_NoLatch_DoesNotEngage()
    {
        // Gate wired but not latched → combat-off still means no swing.
        using Harness h = new();
        h.AutoCombatEnabled = false;
        h.Combat.SetSeeHiddenClearGate(() => false);
        h.AddMonster(1, "crystal golem", killable: true);

        h.Feed("Also here: crystal golem.");

        Assert.Empty(h.Sent);
        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void SeeHiddenOverride_BypassesMaxMonstersGate()
    {
        // The whole point of the override is to clear the WHOLE room so
        // re-sneak is possible — the Min/Max gate must not skip it even
        // when the count is way past Max.
        using Harness h = new();
        h.AutoCombatEnabled = false;
        h.Settings.MaxMonstersInRoom = 1;     // would normally skip a 2-mob room
        h.Combat.SetSeeHiddenClearGate(() => true);
        h.AddMonster(1, "crystal golem", killable: true);
        h.AddMonster(2, "giant rat", killable: true);

        h.Feed("Also here: crystal golem, giant rat.");

        Assert.NotEmpty(h.Sent);
        Assert.NotNull(h.Combat.CurrentTarget);
    }

    [Fact]
    public void FistsNoEffect_ClearsAltFlag_AndReRequestsOnNextRoom()
    {
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        h.Swaps.Clear();

        h.Feed("Your fists have no effect against this monster!");

        // Force a fresh observation — should re-request the normal weapon.
        h.Feed("Also here: giant rat.");
        Assert.Contains(("longsword", (string?)null), h.Swaps);
    }

    [Fact]
    public void NextRoom_PreemptivelySwapsAlt_WhenSpeciesInFailSet()
    {
        // Stone golems fail vs longsword. After the first no-effect
        // fires + species lands in the fail-set, the next pick of
        // the same species in the same room skips longsword.
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.Settings.AlternateWeapon = "warhammer";
        h.Settings.AlternateAttackCommand = "swing";
        h.AddMonster(1, "stone golem", killable: true);

        // First golem — normal weapon, no-effect fires, swap to alt.
        h.Feed("Also here: stone golem.");
        h.Feed("Your weapon has no effect against this monster!");

        // Simulate the same species in another instance — classifier
        // re-observes with the same species. EquipForAttack should
        // pre-pick the alt (already on it; idempotent) and SendAttack
        // should use the AlternateAttackCommand.
        h.Sent.Clear();
        h.Classifier.NoteRoomChanged();
        h.Feed("Also here: stone golem.");

        // Failed set clears on room change, so this is a fresh test
        // of state cleanliness.
        List<string> lines = h.Sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains("a stone golem", lines);     // back to normal command on new room
    }

    // ----- Min/Max monsters gate -------------------------------------

    [Fact]
    public void MinMonsters_BelowThreshold_NoAttack()
    {
        using Harness h = new();
        h.Settings.MinMonstersInRoom = 2;
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void MaxMonsters_AboveThreshold_NoAttack()
    {
        using Harness h = new();
        h.Settings.MaxMonstersInRoom = 2;
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "kobold", killable: true);
        h.AddMonster(3, "goblin", killable: true);

        h.Feed("Also here: giant rat, kobold, goblin.");

        Assert.Empty(h.Sent);
    }

    // The Min/Max window is meant to stop a walker/loop from parking in a
    // too-crowded room mid-route — it must not also leave an idle character
    // (nothing moving it, freshly logged in or manually parked) standing
    // undefended against a room the exact same shape would otherwise decline
    // to fight. Reproduces the report: log in, land on a 5-hostile room with
    // Max=2, "it buffed, but sat there doing nothing."
    [Fact]
    public void MaxMonsters_AboveThreshold_NoMovementActive_AttacksAnyway()
    {
        using Harness h = new();
        h.Settings.MaxMonstersInRoom = 2;
        h.Combat.SetMovementActiveGate(() => false);   // no walker/loop attached — idle
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "kobold", killable: true);
        h.AddMonster(3, "goblin", killable: true);

        h.Feed("Also here: giant rat, kobold, goblin.");

        List<string> lines = h.Sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains(lines, l => l.StartsWith("a "));
    }

    [Fact]
    public void MaxMonsters_AboveThreshold_MovementActive_StillSkips()
    {
        using Harness h = new();
        h.Settings.MaxMonstersInRoom = 2;
        h.Combat.SetMovementActiveGate(() => true);   // a walker/loop IS driving us through
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "kobold", killable: true);
        h.AddMonster(3, "goblin", killable: true);

        h.Feed("Also here: giant rat, kobold, goblin.");

        // Still skips — the window applies while genuinely passing through.
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void MinMonsters_InRange_AttacksNormally()
    {
        using Harness h = new();
        h.Settings.MinMonstersInRoom = 1;
        h.Settings.MaxMonstersInRoom = 5;
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "kobold", killable: true);

        h.Feed("Also here: giant rat, kobold.");

        // Two monsters in range [1, 5] → attack fires.
        List<string> lines = h.Sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains(lines, l => l.StartsWith("a "));
    }

    [Fact]
    public void CombatLine_MasterOff_NoRefresh()
    {
        // Auto-combat disabled → safety net also disabled.
        using Harness h = new();
        h.AutoCombatEnabled = false;

        h.Feed("The kobold thief swings at you but misses!");

        Assert.Empty(h.Sent);
    }

    // ----- combat-off interrupt resume ---------------------------------

    [Fact]
    public void CombatOff_ThenMobLine_ResumesAttack()
    {
        // Live repro: fighting an acid slime (empty DeathLine, like 152
        // stock monsters), an in-between self-heal / buff casts mid-round.
        // The cast emits *Combat Off* with no following *Combat Engaged* —
        // the server stops swinging for us but the slime is still alive.
        // *Combat Off* alone does NOT resume (a non-sustaining attack like
        // KAI pummel emits Off after every strike; resuming on the bare Off
        // would spin). The next mob swing arms the paced resume.
        using Harness h = new();
        h.AddMonster(1, "acid slime", killable: false);

        h.Feed("Also here: acid slime.");
        Assert.Single(h.Sent);
        Assert.Equal("a acid slime", h.LastSent);
        Assert.Equal("acid slime", h.Combat.CurrentTarget);

        // A full round elapsed before the interrupting cast — backdate the
        // swing stamp so the resume isn't mistaken for a redundant double on
        // the heels of a fresh swing (see ResumeAfterAttackGuard).
        BackdateLastAttack(h.Combat);

        // In-between cast turns combat off; no room re-display, no Engaged.
        // No resume yet — Off by itself is ambiguous.
        h.Feed("*Combat Off*");
        Assert.Single(h.Sent);

        // The slime's next swing confirms the fight is ongoing — resume now.
        h.Feed("The acid slime claws you for 5 damage!");
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a acid slime", h.LastSent);
        Assert.Equal("acid slime", h.Combat.CurrentTarget);
    }

    [Fact]
    public void CombatOff_ThenTick_ResumesAttack()
    {
        // The combat tick is the round heartbeat — it deterministically
        // resumes a weapon attack one round after an in-between cast turned
        // it off, without waiting for the mob's swing line to parse. This is
        // the path that kills the "long pause before re-attack" symptom for
        // mobs whose attack message we don't match.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: false);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        // Resume happens a round later — backdate so the guard doesn't read it
        // as a double on a fresh swing (see ResumeAfterAttackGuard).
        BackdateLastAttack(h.Combat);

        // In-between cast: Off, no Engaged. No resume on the Off line.
        h.Feed("*Combat Off*");
        Assert.Single(h.Sent);

        // Next round's tick re-issues the attack.
        h.Combat.OnCombatTick();
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    [Fact]
    public void MatchedDeath_ThenSameKillsExpAndCombatOff_DoesNotDropTheRePickedSurvivor()
    {
        // Report 215511: the first kill's death LINE cleared the corpse and
        // re-picked the next survivor, then the SAME kill's exp + *Combat Off*
        // tripped the exp-inference — which dropped the freshly-picked survivor
        // and re-attacked it, double-firing the attack command. A matched death
        // line must gate that inference off (this Off is the matched kill's Off).
        using Harness h = new();
        h.AddMonster(1, "orc lieutenant", killable: true, allowNoPrefix: false,
            "small", "short");

        h.Feed("Also here: small orc lieutenant, short orc lieutenant.");
        Assert.Equal("small orc lieutenant", h.Combat.CurrentTarget);

        // First orc's death line matches → clear + re-display re-picks the survivor.
        h.Combat.NoteMonsterDied("small orc lieutenant");
        h.Feed("Also here: short orc lieutenant.");
        Assert.Equal("short orc lieutenant", h.Combat.CurrentTarget);
        int sentAfterRepick = h.Sent.Count;

        // The SAME kill's exp + *Combat Off* arrive — must NOT infer a second kill.
        h.Feed("You gain 100 experience.");
        h.Feed("*Combat Off*");

        Assert.Equal("short orc lieutenant", h.Combat.CurrentTarget);   // survivor still engaged
        Assert.Equal(sentAfterRepick, h.Sent.Count);                    // no double-fire
    }

    [Fact]
    public void BetweenRoundCast_ThenCombatOff_ResumesImmediately()
    {
        // The reported case: a between-round CastingDirector self-heal fires
        // mid-fight. NoteBetweenRoundCast arms the engine; the *Combat Off*
        // the cast triggers is then attributed to it and the weapon attack
        // resumes on that very line — no full-round idle, no mob-swing wait.
        using Harness h = new();
        h.AddMonster(1, "cave bear", killable: false);

        h.Feed("Also here: cave bear.");
        Assert.Single(h.Sent);
        Assert.Equal("a cave bear", h.LastSent);

        // The heal fires a round into the fight — backdate the swing stamp so
        // the resume isn't suppressed as a fresh-swing double (see
        // ResumeAfterAttackGuard).
        BackdateLastAttack(h.Combat);

        // CastingDirector sent a heal; the server stops our auto-attack.
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");

        Assert.Equal(2, h.Sent.Count);     // resumed on the Off line itself
        Assert.Equal("a cave bear", h.LastSent);
        Assert.Equal("cave bear", h.Combat.CurrentTarget);
    }

    [Fact]
    public void BetweenRoundCast_FreshSwing_StillResumes()
    {
        // Report 152453: the director's heal fired within a beat of the
        // combat engine's own attack (heal armed as we engaged). The generic
        // ResumeAfterAttackGuard read that fresh swing as "still going" and
        // suppressed the cast-resume, so the engine idled a round until the
        // NEXT heal (by then the swing was old enough to pass the guard). The
        // between-round-cast branch must bypass that guard: the *Combat Off*
        // is provably our cast interrupting the swing, not a swing in flight.
        using Harness h = new();
        h.AddMonster(1, "large orc rogue", killable: false);

        h.Feed("Also here: large orc rogue.");
        Assert.Single(h.Sent);
        Assert.Equal("a large orc rogue", h.LastSent);

        // No BackdateLastAttack — the swing is FRESH (within the guard). The
        // director heals immediately after engaging, then the cast turns
        // combat off. The resume must still fire on this Off line.
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");

        Assert.Equal(2, h.Sent.Count);     // resumed despite the fresh swing
        Assert.Equal("a large orc rogue", h.LastSent);
        Assert.Equal("large orc rogue", h.Combat.CurrentTarget);
    }

    [Fact]
    public void BetweenRoundCast_TargetDroppedByResync_StillResumes()
    {
        // Report 163947 (cast mihe, then missed a combat round): a between-round
        // self-heal landed right after a resync (command-no-effect /
        // unattributed-death) had already nulled _currentTarget. The old resume
        // gate required a non-null _currentTarget, so it stood down and the
        // engine idled a full round before re-attacking. Presence of a live
        // target is proven by the room (HasEngageable), and ResumeEngage re-picks
        // from it — so a null target must NOT block the resume.
        using Harness h = new();
        h.AddMonster(1, "cave bear", killable: true);

        h.Feed("Also here: cave bear.");
        Assert.Single(h.Sent);
        Assert.Equal("a cave bear", h.LastSent);
        Assert.Equal("cave bear", h.Combat.CurrentTarget);

        // A resync nulls the target while the mob is still in the room (no fresh
        // observation fed, so the classifier still holds the cave bear).
        h.Feed("Your command had no effect.");
        Assert.Null(h.Combat.CurrentTarget);

        int before = h.Sent.Count;

        // The heal fires and turns combat off with the target still null.
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");

        // Resumed anyway — re-picked the surviving cave bear and re-swung.
        Assert.Equal(before + 1, h.Sent.Count);
        Assert.Equal("a cave bear", h.LastSent);
        Assert.Equal("cave bear", h.Combat.CurrentTarget);
    }

    [Fact]
    public void BetweenRoundCast_KillInSameBurst_DoesNotPhantomAttackCorpse()
    {
        // Report paradigm-20260715-181944 (phantom "aa <trog>" at a cleared room):
        // the mob took its killing blow the same round a mihe party-heal fired. The
        // kill's *Combat Off* lands inside CastInterruptResumeWindow, so the resume
        // misread it as the heal's interrupt and re-engaged — but the kill's forced
        // re-display hadn't cleared the roster yet, so it re-picked the corpse and
        // sent an attack at the emptied room ("Your command had no effect."). A
        // death detected this burst must suppress the between-round-cast resume and
        // hand re-engagement to the death→re-observe path (a fresh, resynced roster).
        using Harness h = new();
        h.AddMonster(1, "large troglodyte warrior", killable: true);

        h.Feed("Also here: large troglodyte warrior.");
        Assert.Single(h.Sent);
        Assert.Equal("a large troglodyte warrior", h.LastSent);

        // Age the swing so the fresh-swing guard isn't the suppressor — the
        // between-round branch bypasses that guard anyway, isolating the death guard.
        BackdateLastAttack(h.Combat);

        // The trog dies but the death can't be pinned to a roster slot (flavored
        // wording), so the roster still lists it and a re-display is forced. This
        // stamps the death so the imminent Off reads as the KILL's Off.
        h.Combat.NoteUnattributedDeath();
        int before = h.Sent.Count;     // includes the forced \r re-display

        // The heal fires this same round, then the kill's *Combat Off* lands with
        // the (now-dead) trog still in the classifier's stale roster.
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");

        // No phantom "a large troglodyte warrior" — the resume stood down.
        Assert.Equal(before, h.Sent.Count);
    }

    [Fact]
    public void BetweenRoundCast_AfterDeathReObserveReengaged_StillResumes()
    {
        // Report paradigm-20260716-124255 (cast a buff mid-fight, then missed a
        // combat round): a fallback kill re-observed and re-swung at the surviving
        // mob, THEN a between-round buff cast fired and turned combat off. The death
        // guard suppressed the cast-resume as if the kill's re-observe were still
        // pending — but it had already run, so the fresh swing sat interrupted for a
        // full round until the cast-block latch cleared. Once an attack has gone out
        // since the death, the Off is the cast's interrupt and must resume.
        using Harness h = new();
        h.AddMonster(1, "angry bugbear", killable: false);
        h.AddMonster(2, "short bugbear", killable: false);

        h.Feed("Also here: angry bugbear, short bugbear.");
        Assert.Equal("a angry bugbear", h.LastSent);
        Assert.Equal("angry bugbear", h.Combat.CurrentTarget);

        // The fallback death re-observes and re-swings at the survivor — the death→
        // re-observe path completes here (an attack goes out AFTER the death stamp).
        h.Combat.NoteUnattributedDeath();
        Assert.Equal("a short bugbear", h.LastSent);
        Assert.Equal("short bugbear", h.Combat.CurrentTarget);
        int before = h.Sent.Count;

        // A between-round buff cast now fires and interrupts that fresh swing.
        h.Combat.NoteBetweenRoundCast();
        h.Feed("*Combat Off*");

        // Resumed on the Off line — the survivor is re-swung, no missed round.
        Assert.Equal(before + 1, h.Sent.Count);
        Assert.Equal("a short bugbear", h.LastSent);
    }

    [Fact]
    public void BetweenRoundCast_BeforeDeathReObserveSwing_DoesNotDouble()
    {
        // Report paradigm-20260805-220759 ("doubled down on the physical attack"):
        // a self-bless fired BETWEEN rounds (arming the cast-interrupt window), THEN
        // a spell kill's death→re-observe re-engaged the survivor a beat later. The
        // survivor's swing is fresh and in flight, but the kill's *Combat Off* still
        // lands inside CastInterruptResumeWindow of the earlier bless — the old
        // unconditional bypassAttackGuard defeated ResumeAfterAttackGuard and fired a
        // redundant second `aa <survivor>`. Because the swing post-dates the cast, the
        // cast did NOT interrupt it, so the resume must fall through to the guarded
        // path and be suppressed.
        using Harness h = new();
        h.AddMonster(1, "angry bugbear", killable: false);
        h.AddMonster(2, "short bugbear", killable: false);

        h.Feed("Also here: angry bugbear, short bugbear.");
        Assert.Equal("a angry bugbear", h.LastSent);

        // The bless fires FIRST (between rounds), arming the cast-interrupt window.
        h.Combat.NoteBetweenRoundCast();

        // THEN the kill's death→re-observe re-engages the survivor — a fresh swing
        // that post-dates the bless.
        h.Combat.NoteUnattributedDeath();
        Assert.Equal("a short bugbear", h.LastSent);
        Assert.Equal("short bugbear", h.Combat.CurrentTarget);
        int before = h.Sent.Count;

        // The kill's *Combat Off* lands inside the bless's resume window. No double —
        // the fresh survivor swing is guarded, not re-fired.
        h.Feed("*Combat Off*");
        Assert.Equal(before, h.Sent.Count);
    }

    [Fact]
    public void NullNumberArrival_UnresolvedName_IsEngaged()
    {
        // Report paradigm-20260716-124409 (didn't react to a monster entering the
        // room, had to manually attack): a mid-walk arrival whose colour-stripped
        // name ("dragon serpent") misses the colour-prefixed game-data records
        // ("red/white dragon serpent") is classified Monster with a null number.
        // HasEngageable fail-opens on it to hold the Combat gate (walker pause),
        // but the attacker used to DROP null-number monsters — so the walker
        // stopped on a mob that was never hit and the player just got pummelled.
        // The attacker must engage it too, by RawName.
        using Harness h = new();

        h.Classifier.AppendArrivalEntity(
            new RoomEntity("dragon serpent", "dragon serpent",
                           EntityKind.Monster, MonsterNumber: null),
            rawWireLine: "A dragon serpent slithers in from the northwest!");

        Assert.Equal("a dragon serpent", h.LastSent);
        Assert.Equal("dragon serpent", h.Combat.CurrentTarget);
    }

    [Fact]
    public void NoCombatOff_MobLine_DoesNotReswing()
    {
        // Guard: a mob line while combat is live (server still swinging
        // at our target) must NOT burn an extra swing. Only a preceding
        // *Combat Off* arms the resume.
        using Harness h = new();
        h.AddMonster(1, "acid slime", killable: false);

        h.Feed("Also here: acid slime.");
        Assert.Single(h.Sent);

        h.Feed("The acid slime claws you for 5 damage!");

        Assert.Single(h.Sent);     // no re-swing — combat wasn't off
        Assert.Equal("acid slime", h.Combat.CurrentTarget);
    }

    [Fact]
    public void CombatOff_ThenEngaged_DisarmsResume()
    {
        // *Combat Off* immediately followed by *Combat Engaged* is the
        // "we re-fired the attack / changed attack pattern" case — the
        // server is swinging for us again, so the interrupt flag is cleared
        // and no resume fires. A following mob swing must NOT re-attack.
        using Harness h = new();
        h.AddMonster(1, "acid slime", killable: false);

        h.Feed("Also here: acid slime.");
        Assert.Single(h.Sent);

        h.Feed("*Combat Off*");
        Assert.Single(h.Sent);             // no resume on the bare Off

        h.Feed("*Combat Engaged*");        // disarms the interrupt flag
        h.Feed("The acid slime claws you for 5 damage!");

        Assert.Single(h.Sent);             // resume disarmed — no extra swing
    }

    [Fact]
    public void KillThenReObserve_CombatOffAndMobLine_DoesNotDoubleSwing()
    {
        // Report 203503: a solo double-send. On a kill the death→re-observe
        // path re-engages the surviving mob, but the kill's *Combat Off*
        // arrives a beat later and re-arms the interrupt flag; the next mob
        // swing line then fired a SECOND, redundant attack at the same target
        // (the 0-dmg/1-miss ghost round in the report log). The resume must see
        // the just-sent swing and stand down.
        using Harness h = new();
        h.AddMonster(1, "dark goblin archer", killable: true);
        h.AddMonster(2, "nasty thug", killable: true);

        h.Feed("Also here: dark goblin archer, nasty thug.");
        Assert.Single(h.Sent);
        Assert.Equal("a dark goblin archer", h.LastSent);

        // Archer dies — the death path clears the target, and the room
        // re-display re-picks + swings at the surviving thug (send #2).
        h.Combat.NoteMonsterDied("dark goblin archer");
        h.Feed("Also here: nasty thug.");
        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a nasty thug", h.LastSent);

        // The kill's delayed *Combat Off* re-arms the interrupt, then the thug
        // swings. Without the guard this resumes and doubles the swing; with it,
        // the fresh send #2 keeps the resume from firing.
        h.Feed("*Combat Off*");
        h.Feed("The nasty thug swings at you but misses!");

        Assert.Equal(2, h.Sent.Count);          // no double — resume stood down
        Assert.Equal("nasty thug", h.Combat.CurrentTarget);
    }

    // ----- engage-verification safety net ------------------------------

    /// <summary>Backdate the private engage-wait timestamp so the next
    /// tick sees the confirmation window as elapsed — lets the test
    /// exercise the timeout path without sleeping the real clock.</summary>
    private static void ExpireEngageWindow(CombatManager combat)
    {
        System.Reflection.FieldInfo f = typeof(CombatManager).GetField(
            "_awaitingEngageSince",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!;
        Assert.NotNull(f.GetValue(combat));   // must actually be awaiting
        f.SetValue(combat, DateTimeOffset.Now - TimeSpan.FromSeconds(30));
    }

    /// <summary>Backdate the last-swing stamp so the interrupt-resume guard
    /// sees the attack as a full round old — the real timing when a mob swing
    /// or between-round cast resumes. The guard only suppresses a resume that
    /// lands right on the heels of a fresh swing (see ResumeAfterAttackGuard),
    /// so time-compressed tests must age the stamp to exercise a real resume.</summary>
    private static void BackdateLastAttack(CombatManager combat)
    {
        System.Reflection.FieldInfo f = typeof(CombatManager).GetField(
            "_lastAttackSentAt",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!;
        f.SetValue(combat, DateTimeOffset.Now - TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AttackUnconfirmed_AfterWindow_FiresRoomRefreshCr()
    {
        // Report 2 repro: a movement was in flight, so the swing we sent
        // landed on a stale room view. The server never confirms
        // *Combat Engaged*; after the window the tick must drop the stale
        // target and fire a bare CR to force a fresh room display.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);

        ExpireEngageWindow(h.Combat);
        h.Combat.OnCombatTick();

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("\r", Encoding.Latin1.GetString(h.Sent[^1]));
        Assert.Null(h.Combat.CurrentTarget);
    }

    [Fact]
    public void AttackConfirmed_NoRoomRefreshCr()
    {
        // The server acknowledged the swing — the engage-verify net must
        // stay disarmed; no spurious CR even once the window would lapse.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        h.Feed("*Combat Engaged*");          // server confirms
        h.Combat.OnCombatTick();

        Assert.Single(h.Sent);               // no extra CR
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }
}
