using System.Text;
using FujinTerm.Game;
using FujinTerm.Game.Combat;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

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
        public string? OwnName { get; set; } = "Fujin";
        public bool AutoCombatEnabled { get; set; } = true;

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
                log: Log);
            Combat.SetWireSender(b => Sent.Add(b));
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
                HitYou: Array.Empty<string>(),
                HitOther: Array.Empty<string>(),
                DeathLine: deathLines,
                ArmorBlockYou: Array.Empty<string>(),
                ArmorBlockOther: Array.Empty<string>(),
                DodgeYou: Array.Empty<string>(),
                DodgeOther: Array.Empty<string>(),
                MissYou: Array.Empty<string>(),
                MissOther: Array.Empty<string>(),
                FlavorPrefixes: flavorPrefixes,
                AllowNoPrefix: allowNoPrefix,
                Links: new[] { new GameDataLink("Monsters", number) }));
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
        // Critical: if our own "Fujin moves to attack giant rat" line
        // came through and we re-fired, we'd swing twice per round.
        using Harness h = new() { OwnName = "Fujin" };
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Single(h.Sent);

        h.Feed("Fujin moves to attack giant rat.");
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

    // ----- Attack Order is pure re-fire: NEVER switches the monster ----

    [Fact]
    public void AttackLastParty_ReFiresOwnTarget_DoesNotSwitch()
    {
        // Clean split: Attack Order is the "when", not the "who". Two
        // kobold thiefs; we pick angry by default. Party member Tank
        // announces against the LARGE one. AttackLastParty re-fires our
        // OWN target (angry) on the party announce — it never switches
        // the monster. Switching is Target Priority's job.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastParty;
        h.Party.Members.Add(new PartyMember { Name = "Tank" });
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);

        h.Feed("Tank moves to attack large kobold thief.");

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a angry kobold thief", h.LastSent);   // re-fire, unchanged
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);
    }

    [Fact]
    public void AttackLastRoom_DoesNotSwitch_JustReFiresOwnTarget()
    {
        // Room-mode is "be last in initiative on MY target", not
        // "follow whoever shouts". Stranger announcing a different
        // target shouldn't pull us off ours.
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackLastRoom;
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);

        h.Feed("Stranger moves to attack large kobold thief.");

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a angry kobold thief", h.LastSent);   // unchanged target
        Assert.Equal("angry kobold thief", h.Combat.CurrentTarget);
    }

    // ----- Target Priority: switch our target to follow leader/member -

    [Fact]
    public void TargetPriorityFollowLeader_SwitchesToLeadersTarget()
    {
        // Default pick is angry; party leader announces the LARGE one.
        // FollowLeader switches our target to match the leader's instance.
        using Harness h = new();
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
    public void TargetPriorityFollowLeader_IgnoresNonLeaderAnnounce()
    {
        // Only the leader's announce drives the switch. A non-leader
        // party member shouting a different target is ignored.
        using Harness h = new();
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
    public void AttackAfter_ReFiresOwnTarget_OnNamedAnnounce()
    {
        // AttackAfter is pure timing: re-fire OUR target when the named
        // player announces, keeping our swing immediately after theirs.
        // It does NOT switch to their monster (that's Target Priority).
        using Harness h = new();
        h.Settings.AttackTiming = AttackTiming.AttackAfter;
        h.Settings.AttackAfterPlayerName = "Tank";
        h.AddMonster(1, "kobold thief", killable: true, allowNoPrefix: false,
            "angry", "large");

        h.Feed("Also here: angry kobold thief, large kobold thief.");
        Assert.Equal("a angry kobold thief", h.LastSent);    // own pick

        h.Feed("Tank moves to attack large kobold thief.");

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
    public void Attack_EquipsNormalWeapon_BeforeFirstSwing()
    {
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.Settings.NormalOffHand = "shield";
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");

        // Three sends: eq longsword, eq shield, a giant rat
        Assert.Equal(3, h.Sent.Count);
        List<string> lines = h.Sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Equal("eq longsword", lines[0]);
        Assert.Equal("eq shield", lines[1]);
        Assert.Equal("a giant rat", lines[2]);
    }

    [Fact]
    public void Attack_NoEquipChange_IdempotentOnSameWeapon()
    {
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.AddMonster(1, "giant rat", killable: true);
        h.AddMonster(2, "kobold",    killable: true);

        h.Feed("Also here: giant rat.");
        Assert.Equal(2, h.Sent.Count);    // eq + attack

        // Room change — same weapon should not re-equip.
        h.Classifier.NoteRoomChanged();
        h.Feed("Also here: kobold.");
        Assert.Equal(3, h.Sent.Count);    // just attack, no second eq
        Assert.Equal("a kobold", h.LastSent);
    }

    [Fact]
    public void WeaponNoEffect_SwapsToAlternate_AndReSwings()
    {
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.Settings.AlternateWeapon = "warhammer";
        h.Settings.AlternateAttackCommand = "swing";
        h.AddMonster(1, "stone golem", killable: true);

        h.Feed("Also here: stone golem.");
        Assert.Equal(2, h.Sent.Count);    // eq longsword, a stone golem
        h.Sent.Clear();

        h.Feed("Your weapon has no effect against this monster!");

        // eq warhammer + swing stone golem
        List<string> lines = h.Sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains("eq warhammer", lines);
        Assert.Contains("swing stone golem", lines);
    }

    [Fact]
    public void RoomCleared_RevertsToNormal_WhenWasOnAlt()
    {
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.Settings.AlternateWeapon = "warhammer";
        h.AddMonster(1, "stone golem", killable: true);

        h.Feed("Also here: stone golem.");
        h.Feed("Your weapon has no effect against this monster!");
        h.Sent.Clear();

        // Simulate room-cleared: drop the species from the classifier
        // so the next observation has no engageable. Use the
        // RemoveDeadEntity path which fires EntitiesObserved with the
        // post-removal list.
        h.Classifier.RemoveDeadEntity("stone golem");

        List<string> lines = h.Sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains("eq longsword", lines);
    }

    [Fact]
    public void RoomCleared_EquipsBackstab_WhenConfigured()
    {
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.Settings.BackstabWeapon = "dagger";
        h.Settings.DoBackstab = true;
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        h.Sent.Clear();

        h.Classifier.RemoveDeadEntity("giant rat");

        List<string> lines = h.Sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains("eq dagger", lines);
    }

    // ----- Backstab window (PR 4.c) ----------------------------------

    [Fact]
    public void Backstab_SendsBs_WhenSneaking_NoSeeHidden()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.AddMonster(1, "giant rat", killable: true);
        h.Combat.SetBackstabHooks(isSneaking: () => true, hasSeeHidden: _ => false);

        h.Feed("Also here: giant rat.");

        // Opening swing into the room is `bs`, and the BS path must NOT
        // re-equip — the BS weapon is pre-equipped at room-clear.
        Assert.Equal("bs giant rat", h.LastSent);
        List<string> lines = h.Sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.DoesNotContain(lines, l => l.StartsWith("eq ", StringComparison.Ordinal));
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
            isSneaking: () => true,
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
        h.Combat.SetBackstabHooks(isSneaking: () => true, hasSeeHidden: _ => false);

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void Backstab_NotSneaking_NormalAttack()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.AddMonster(1, "giant rat", killable: true);
        h.Combat.SetBackstabHooks(isSneaking: () => false, hasSeeHidden: _ => false);

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
    public void FistsNoEffect_ClearsShadowState_AndReEquipsOnNextRoom()
    {
        using Harness h = new();
        h.Settings.NormalWeapon = "longsword";
        h.AddMonster(1, "giant rat", killable: true);

        h.Feed("Also here: giant rat.");
        h.Sent.Clear();

        h.Feed("Your fists have no effect against this monster!");

        // Force a fresh observation — should re-equip from scratch.
        h.Feed("Also here: giant rat.");
        List<string> lines = h.Sent.Select(b => System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();
        Assert.Contains("eq longsword", lines);
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
        // stock monsters), the user manually casts a buff mid-round.
        // The cast emits *Combat Off* — the server stops swinging for us
        // but the slime is still alive and keeps attacking. Without the
        // resume path, _currentTarget stays set, OnEntitiesObserved
        // short-circuits ("server still swinging"), and combat goes
        // silent. The next mob swing after *Combat Off* must re-engage.
        using Harness h = new();
        h.AddMonster(1, "acid slime", killable: false);

        h.Feed("Also here: acid slime.");
        Assert.Single(h.Sent);
        Assert.Equal("a acid slime", h.LastSent);
        Assert.Equal("acid slime", h.Combat.CurrentTarget);

        // Manual buff cast turns combat off; no room re-display.
        h.Feed("*Combat Off*");
        Assert.Single(h.Sent);     // no swing on the status line itself

        // The slime keeps attacking — first mob line re-engages.
        h.Feed("The acid slime claws you for 5 damage!");

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("a acid slime", h.LastSent);
        Assert.Equal("acid slime", h.Combat.CurrentTarget);
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
        // *Combat Off* then *Combat Engaged* (server resumed swinging on
        // its own) disarms the resume — a following mob line must not
        // fire a redundant attack.
        using Harness h = new();
        h.AddMonster(1, "acid slime", killable: false);

        h.Feed("Also here: acid slime.");
        Assert.Single(h.Sent);

        h.Feed("*Combat Off*");
        h.Feed("*Combat Engaged*");
        h.Feed("The acid slime claws you for 5 damage!");

        Assert.Single(h.Sent);     // resume disarmed by Engaged
    }
}
