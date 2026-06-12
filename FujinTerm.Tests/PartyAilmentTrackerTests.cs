using FujinTerm.Game;
using FujinTerm.Game.Conditions;
using FujinTerm.Game.Remote;
using FujinTerm.Game.Spells;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR B inbound ailment-sync: a party member's <c>.@poisoned</c> /
/// <c>.@blind</c> / <c>.@confused</c> / <c>.@diseased</c> / <c>.@held</c>
/// announce on say sets their <see cref="PartyMember"/> chip; observing OUR
/// cure spell land on the member clears it. <c>.@held</c> additionally pauses
/// the leader via <see cref="PartyEssentialHandlers.NotePause"/> (same gate an
/// explicit <c>@wait</c> uses), released by the held member's own <c>@ok</c>.
/// </summary>
public sealed class PartyAilmentTrackerTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public ChatRouter Chat { get; }
        public PartyState State { get; } = new();
        public PartyManager Party { get; }
        public PlayerState Player { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public RemoteCommandManager Engine { get; }
        public PartyEssentialHandlers Essentials { get; }
        public LineExtractor Lines { get; }
        public PartyAilmentTracker Tracker { get; }
        public List<CureCastMatcher> Cures { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Party = new PartyManager(Router, State);
            Engine = new RemoteCommandManager(Chat, State, Players);
            Essentials = new PartyEssentialHandlers(Engine, Player, State);
            Terminal.TerminalEmulator emulator = new(80, 24);
            Lines = new LineExtractor(emulator);
            Tracker = new PartyAilmentTracker(Chat, Party, Essentials, () => Cures);
            Tracker.AttachLineExtractor(Lines);
        }

        /// <summary>Add a party member by replaying the follow signal.</summary>
        public PartyMember AddMember(string name)
        {
            Router.Dispatch(Line($"{name} started to follow you."));
            return State.Members.First(m => m.Name == name);
        }

        /// <summary>Dispatch a server line so ChatRouter classifies it.</summary>
        public void Say(string line) => Router.Dispatch(Line(line));

        /// <summary>Push a line through the tracker's LineEmitted clear path.</summary>
        public void EmitLine(string text)
        {
            System.Reflection.FieldInfo? field = typeof(LineExtractor)
                .GetField("LineEmitted",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(Lines) is Action<LineExtractor.EmittedLine> handler)
                handler(Line(text));
        }

        public void Dispose()
        {
            Tracker.Dispose();
            Essentials.Dispose();
            Engine.Dispose();
        }
    }

    // ----- SET path (inbound say announce → chip) --------------------

    [Fact]
    public void InboundPoisoned_SetsMemberChip()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");

        h.Say(@"Forged says ""@poisoned""");

        Assert.True(forged.Poisoned);
    }

    [Fact]
    public void InboundBlind_SetsMemberChip()
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");

        h.Say(@"Mage says ""@blind""");

        Assert.True(mage.Blinded);
    }

    [Fact]
    public void InboundConfused_SetsMemberChip()
    {
        using Harness h = new();
        PartyMember druid = h.AddMember("Druid");

        h.Say(@"Druid says ""@confused""");

        Assert.True(druid.Confused);
    }

    [Fact]
    public void InboundDiseased_SetsMemberChip()
    {
        using Harness h = new();
        PartyMember tank = h.AddMember("Tank");

        h.Say(@"Tank says ""@diseased""");

        Assert.True(tank.Diseased);
    }

    [Fact]
    public void InboundHeld_SetsChipAndPausesLeader()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");

        h.Say(@"Forged says ""@held""");

        // Held sets the chip like any other ailment AND pauses the leader:
        // a held member can't move, so the party waits for them exactly as an
        // explicit @wait would. The hold is released by the member's own @ok.
        Assert.True(forged.Held);
        Assert.Contains("Forged", h.Essentials.WaitingMembers);
        Assert.True(h.Essentials.IsPaused);
    }

    [Fact]
    public void OwnHeldEcho_DoesNotPauseOrSetChip()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");

        // Our own .@held echoes as "You say" → null speaker → ignored; our
        // own movement-prevented state is owned by ConditionTracker, not here.
        h.Say(@"You say ""@held""");

        Assert.False(forged.Held);
        Assert.False(h.Essentials.IsPaused);
    }

    [Fact]
    public void OwnEcho_DoesNotSetAnyChip()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");

        // Our own announce echoes as "You say" → null speaker → ignored.
        h.Say(@"You say ""@poisoned""");

        Assert.False(forged.Poisoned);
    }

    [Fact]
    public void NonMemberSpeaker_CreatesNoPhantomMember()
    {
        using Harness h = new();
        h.AddMember("Forged");

        h.Say(@"Stranger says ""@poisoned""");

        Assert.Single(State_Members(h));
        Assert.DoesNotContain(State_Members(h), m => m.Name == "Stranger");
    }

    private static System.Collections.Generic.IReadOnlyList<PartyMember> State_Members(Harness h)
        => h.State.Members;

    // ----- CLEAR path (observed cure land → chip cleared) ------------

    [Fact]
    public void CureLanding_ClearsChip()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");
        h.Say(@"Forged says ""@poisoned""");
        Assert.True(forged.Poisoned);

        h.Cures.Add(new CureCastMatcher(
            MessageFlags.Poisoned, "cure-poison",
            CasterMessageMatcher.TryCreate("You cast {s} on {s}!")!));

        h.EmitLine("You cast cure-poison on Forged!");

        Assert.False(forged.Poisoned);
    }

    [Fact]
    public void CureHoldsLanding_ClearsHeldChip()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");
        h.Say(@"Forged says ""@held""");
        Assert.True(forged.Held);

        // Held is curable (e.g. Druid "freedom"); observing the cure-holds
        // spell land on the member clears the chip like every other ailment.
        h.Cures.Add(new CureCastMatcher(
            MessageFlags.MovementPrevented, "freedom",
            CasterMessageMatcher.TryCreate("You cast {s} on {s}!")!));

        h.EmitLine("You cast freedom on Forged!");

        Assert.False(forged.Held);
    }

    [Fact]
    public void CureLanding_MatchesByGivenNameForFamilyMember()
    {
        using Harness h = new();
        PartyMember member = h.AddMember("Forged");
        // par lists can carry the full "Given Family" name; the follow
        // signal only gives the given name, so set it directly here.
        member.Name = "Forged Ravenoak";
        member.Diseased = true;

        h.Cures.Add(new CureCastMatcher(
            MessageFlags.Diseased, "cure-disease",
            CasterMessageMatcher.TryCreate("You cast {s} on {s}!")!));

        // Server prints only the given name; tracker confirms against it.
        h.EmitLine("You cast cure-disease on Forged!");

        Assert.False(member.Diseased);
    }

    [Fact]
    public void CureLanding_WrongTarget_LeavesChip()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");
        h.Say(@"Forged says ""@poisoned""");

        h.Cures.Add(new CureCastMatcher(
            MessageFlags.Poisoned, "cure-poison",
            CasterMessageMatcher.TryCreate("You cast {s} on {s}!")!));

        // Cure landed on someone else — Forged's chip stays.
        h.EmitLine("You cast cure-poison on Goblin!");

        Assert.True(forged.Poisoned);
    }

    [Fact]
    public void CureLanding_NoMatchersConfigured_IsNoOp()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");
        h.Say(@"Forged says ""@poisoned""");

        // No cure matchers → clear path short-circuits.
        h.EmitLine("You cast cure-poison on Forged!");

        Assert.True(forged.Poisoned);
    }

    [Fact]
    public void DifferentSpellOnAfflictedMember_DoesNotClearChip()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");
        h.Say(@"Forged says ""@poisoned""");
        Assert.True(forged.Poisoned);

        h.Cures.Add(new CureCastMatcher(
            MessageFlags.Poisoned, "cure-poison",
            CasterMessageMatcher.TryCreate("You cast {s} on {s}!")!));

        // Buffing the poisoned member fits the template + names them, but the
        // spell isn't the cure — the chip must stay set.
        h.EmitLine("You cast bless on Forged!");

        Assert.True(forged.Poisoned);
    }

    [Fact]
    public void WitnessedCure_ByAnotherMember_ClearsChip()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");
        h.Say(@"Forged says ""@poisoned""");
        Assert.True(forged.Poisoned);

        h.Cures.Add(new CureCastMatcher(
            MessageFlags.Poisoned, "cure-poison",
            CasterMessageMatcher.TryCreate("You cast {s} on {s}!")!,
            CasterMessageMatcher.TryCreate("{s} casts {s} on {s}!")));

        // We don't cast it — a different member does, and we see the room's
        // witness line. The caster (Mage) doesn't matter; the cure + target do.
        h.EmitLine("Mage casts cure-poison on Forged!");

        Assert.False(forged.Poisoned);
    }

    [Fact]
    public void SemanticWitnessTemplate_DoesNotClearTheCastersOwnChip()
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");
        PartyMember forged = h.AddMember("Forged");
        h.Say(@"Mage says ""@poisoned""");
        h.Say(@"Forged says ""@poisoned""");
        Assert.True(mage.Poisoned);
        Assert.True(forged.Poisoned);

        // Semantic witness template pins {source}/{spellname}/{target}. A
        // legacy {s}-only template would mistake the caster's name (Mage, in
        // the source slot) for a second target and false-clear Mage's chip;
        // the roles prevent that.
        h.Cures.Add(new CureCastMatcher(
            MessageFlags.Poisoned, "cure-poison",
            CasterMessageMatcher.TryCreate("You cast {spellname} on {target}!")!,
            CasterMessageMatcher.TryCreate("{source} casts {spellname} on {target}!")));

        // Mage (also poisoned) cures Forged — only Forged's chip should clear.
        h.EmitLine("Mage casts cure-poison on Forged!");

        Assert.False(forged.Poisoned);
        Assert.True(mage.Poisoned);
    }

    [Fact]
    public void WitnessedDifferentSpell_LeavesChip()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");
        h.Say(@"Forged says ""@poisoned""");

        h.Cures.Add(new CureCastMatcher(
            MessageFlags.Poisoned, "cure-poison",
            CasterMessageMatcher.TryCreate("You cast {s} on {s}!")!,
            CasterMessageMatcher.TryCreate("{s} casts {s} on {s}!")));

        // A witnessed buff on the poisoned member must not clear the chip.
        h.EmitLine("Mage casts bless on Forged!");

        Assert.True(forged.Poisoned);
    }
}
