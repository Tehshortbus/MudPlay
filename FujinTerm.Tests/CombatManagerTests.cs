using System.Text;
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
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatManager Combat { get; }
        public List<byte[]> Sent { get; } = new();
        public CombatSettings Settings { get; set; } = new()
        {
            MasterAutoAttackEnabled = true,
            NormalAttackCommand = "a",
            TargetOrder = TargetOrder.Normal,
        };

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Combat = new CombatManager(Classifier, Monsters,
                readSettings: () => Settings, log: Log);
            Combat.SetWireSender(b => Sent.Add(b));
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
        h.Settings.MasterAutoAttackEnabled = false;
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
    public void PrefixedDisplay_AttackUsesBaseName()
    {
        // "nasty giant rat" displayed — wire sends `a giant rat`.
        using Harness h = new();
        h.AddMonster(1, "giant rat", killable: true, allowNoPrefix: true, "nasty");

        h.Feed("Also here: nasty giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
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
    public void ShopkeeperOnly_NoSwing()
    {
        using Harness h = new();
        h.AddMonster(7, "shopkeeper", killable: false);

        h.Feed("Also here: shopkeeper.");

        Assert.Empty(h.Sent);
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

        h.Settings.MasterAutoAttackEnabled = false;
        h.Feed("Also here: giant rat.");        // re-display
        Assert.Null(h.Combat.CurrentTarget);
    }

    // ----- no wire sender bound ---------------------------------------

    [Fact]
    public void NoWireSender_LogsButDoesNotThrow()
    {
        Harness h = new();
        // Don't bind the sender — set the field manually to null path.
        CombatManager combat = new(h.Classifier, h.Monsters,
            readSettings: () => h.Settings, log: h.Log);

        h.AddMonster(1, "giant rat", killable: true);
        h.Feed("Also here: giant rat.");

        // No throws; the engine tracked the target for state purposes
        // but didn't send anything.
        Assert.Equal("giant rat", combat.CurrentTarget);
        combat.Dispose();
        h.Dispose();
    }
}
