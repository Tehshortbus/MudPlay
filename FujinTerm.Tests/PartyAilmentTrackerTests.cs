using FujinTerm.Game;
using FujinTerm.Game.Conditions;
using FujinTerm.Game.Spells;
using FujinTerm.Models.GameData;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;
using FujinTerm.Terminal;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// PR B inbound ailment-sync: a party member's <c>.@poisoned</c> /
/// <c>.@blind</c> / <c>.@confused</c> / <c>.@diseased</c> announce on say sets
/// their <see cref="PartyMember"/> chip; observing OUR cure spell land on the
/// member clears it.
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
        public LineExtractor Lines { get; }
        public PartyAilmentTracker Tracker { get; }
        public List<CureCastMatcher> Cures { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Party = new PartyManager(Router, State);
            Terminal.TerminalEmulator emulator = new(80, 24);
            Lines = new LineExtractor(emulator);
            Tracker = new PartyAilmentTracker(Chat, Party, () => Cures);
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

        public void Dispose() => Tracker.Dispose();
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
}
