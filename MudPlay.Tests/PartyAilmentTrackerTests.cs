using MudPlay.Game;
using MudPlay.Game.Conditions;
using MudPlay.Game.Remote;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// PR B inbound ailment-sync: a party member's .@poisoned /
// .@blind / .@confused / .@diseased / .@held
// announce on say sets their PartyMember chip; observing OUR
// cure spell land on the member clears it. .@held additionally pauses
// the leader via PartyEssentialHandlers.NotePause (same gate an
// explicit @wait uses), released by the held member's own @ok.
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
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public RemoteCommandManager Engine { get; }
        public PartyEssentialHandlers Essentials { get; }
        // The game's output, fed the way the client receives it.
        public ServerFeed Server { get; } = new();
        public PartyAilmentTracker Tracker { get; }
        public List<CureCastMatcher> Cures { get; } = new();
        // Apply-witness wiring (PR B): matchers the tracker witnesses to SET chips,
        // a spell# → duration-seconds map, and a mutable test clock (ms) the
        // tracker reads for arming + sweeping expiries.
        public List<ApplyCastMatcher> Applies { get; } = new();
        public Dictionary<int, double?> Durations { get; } = new();
        public List<string?> Sources { get; } = new();
        public long NowMs;

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Party = new PartyManager(Router, State);
            Engine = new RemoteCommandManager(Chat, State, Players);
            Essentials = new PartyEssentialHandlers(Engine, Player, State);
            Tracker = new PartyAilmentTracker(Chat, Party, Essentials, () => Cures,
                readApplyMatchers: () => Applies,
                resolveDurationSeconds: (n, source) =>
                {
                    Sources.Add(source);
                    return Durations.TryGetValue(n, out double? d) ? d : null;
                },
                nowMs: () => NowMs);
            Tracker.AttachLineExtractor(Server.Lines);
        }

        // Add a party member by replaying the follow signal.
        public PartyMember AddMember(string name)
        {
            Router.Dispatch(Line($"{name} started to follow you."));
            return State.Members.First(m => m.Name == name);
        }

        // Dispatch a server line so ChatRouter classifies it.
        public void Say(string line) => Router.Dispatch(Line(line));

        // A server line through the extractor the tracker's clear path reads.
        public void EmitLine(string text) => Server.Line(text);

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
    public void InboundBlindOn_SetsMemberChip()
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");

        // Explicit 'on' suffix (the paired-toggle apply form) sets the chip
        // exactly like the bare token.
        h.Say(@"Mage says ""@blind on""");

        Assert.True(mage.Blinded);
    }

    [Fact]
    public void InboundBlindOff_ClearsMemberChip()
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");
        h.Say(@"Mage says ""@blind on""");
        Assert.True(mage.Blinded);

        // The paired-toggle 'off' clears the chip with no cure witnessed — the
        // natural-wear-off case that left the chip stuck before.
        h.Say(@"Mage says ""@blind off""");

        Assert.False(mage.Blinded);
    }

    [Fact]
    public void InboundConfused_SetsMemberChip()
    {
        using Harness h = new();
        PartyMember druid = h.AddMember("Druid");

        h.Say(@"Druid says ""@confused""");

        Assert.True(druid.Confused);
    }

    // ----- RECONCILE path (@status reply → chip resync) --------------

    [Fact]
    public void StatusReply_NoAilments_ClearsStuckChip()
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");
        h.Say(@"Mage says ""@blind on""");
        Assert.True(mage.Blinded);

        // A @status reply reporting no ailments clears a chip a dropped 'off'
        // left stuck — the pull-based resync.
        h.Say(@"Mage telepaths: {Standing; not moving; no ailments}");

        Assert.False(mage.Blinded);
    }

    [Fact]
    public void StatusReply_ListsAilments_SetsChips()
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");

        h.Say(@"Mage telepaths: {Standing; not moving; ailments: blind, poisoned}");

        Assert.True(mage.Blinded);
        Assert.True(mage.Poisoned);
        Assert.False(mage.Confused);
        Assert.False(mage.Diseased);
    }

    [Fact]
    public void StatusReply_Reconciles_ClearsAbsentAndSetsPresent()
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");
        h.Say(@"Mage says ""@poisoned on""");
        Assert.True(mage.Poisoned);

        // Reply reports blind but not poison: blind set, poison cleared — the
        // reconcile matches the chips to exactly the reported set.
        h.Say(@"Mage telepaths: {Standing; not moving; ailments: blind}");

        Assert.True(mage.Blinded);
        Assert.False(mage.Poisoned);
    }

    [Fact]
    public void StatusReply_OverSay_AlsoReconciles()
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");
        h.Say(@"Mage says ""@blind on""");
        Assert.True(mage.Blinded);

        // The reply can ride the say channel too (a leader broadcasting @status).
        h.Say(@"Mage says ""{Standing; not moving; no ailments}""");

        Assert.False(mage.Blinded);
    }

    // A `@loop send` line names a loop, and a loop can be named anything: it is no
    // @status reply, on any channel.
    [Theory]
    [InlineData(@"Mage telepaths: {@loopresult 9da659db saved as 'no ailments'}")]
    [InlineData(@"Mage telepaths: {@loopresult 9da659db already have it as 'no ailments 2'}")]
    [InlineData(@"Mage telepaths: {sending loop 'no ailments' (3 rooms, 1 line)}")]
    [InlineData(@"Mage telepaths: {preparing to send: no ailments, yes to confirm, no to deny (code 9da659db)}")]
    [InlineData(@"Mage says ""{@loopresult 9da659db saved as 'no ailments'}""")]
    public void LoopSendLineNamingALoop_IsNoStatusReply(string line)
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");
        h.Say(@"Mage says ""@blind on""");

        h.Say(line);

        Assert.True(mage.Blinded);
    }

    // An @events or @event reply names an event, and an event can be named
    // anything as well: it is no @status reply, on any channel.
    [Theory]
    [InlineData(@"Mage telepaths: {events: no ailments check on}")]
    [InlineData(@"Mage telepaths: {events (2/3): no ailments off, Bank on}")]
    [InlineData(@"Mage telepaths: {event 'no ailments' switched off}")]
    [InlineData(@"Mage telepaths: {event 'no ailments' is on: fires on a timer, sends a command}")]
    [InlineData(@"Mage telepaths: {no event named 'no ailments'}")]
    [InlineData(@"Mage telepaths: {event name 'ailments' matches 2: 'no ailments', 'no ailments 2'}")]
    [InlineData(@"Mage telepaths: {events: 1 no ailments on, 2 Bank off}")]
    [InlineData(@"Mage telepaths: {event 2 'no ailments' switched off}")]
    [InlineData(@"Mage says ""{events: no ailments check on}""")]
    public void EventReplyNamingAnEvent_IsNoStatusReply(string line)
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");
        h.Say(@"Mage says ""@blind on""");

        h.Say(line);

        Assert.True(mage.Blinded);
    }

    [Fact]
    public void StatusReply_NoAilments_LeavesHeldChipUntouched()
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");
        h.Say(@"Mage says ""@held""");
        Assert.True(mage.Held);

        // Held is never in the @status clause, so a "no ailments" reply must not
        // clear the held chip — its lifecycle rides @ok, not the reconcile.
        h.Say(@"Mage telepaths: {Standing; not moving; no ailments}");

        Assert.True(mage.Held);
    }

    [Fact]
    public void NonStatusTelepath_LeavesChipsUntouched()
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");
        h.Say(@"Mage says ""@poisoned on""");
        Assert.True(mage.Poisoned);

        // A telepath with no ailment clause isn't a @status reply — no-op.
        h.Say(@"Mage telepaths: {HP=200/200, Resting}");

        Assert.True(mage.Poisoned);
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
    public void InboundOk_ClearsVerboseChips_LeavesPoison()
    {
        using Harness h = new();
        PartyMember mage = h.AddMember("Mage");

        h.Say(@"Mage says ""@blind""");
        h.Say(@"Mage says ""@held""");
        h.Say(@"Mage says ""@poisoned""");
        Assert.True(mage.Blinded);
        Assert.True(mage.Held);
        Assert.True(mage.Poisoned);

        // @ok from the member means every NON-ignored ailment cleared on their side
        // (the sender holds @ok until its last one clears), so drop their VERBOSE
        // chips (blind / confused / diseased / held). Poison is par-owned — left for
        // its own P-flag drop.
        h.Say(@"Mage telepaths: @ok");

        Assert.False(mage.Blinded);
        Assert.False(mage.Held);
        Assert.True(mage.Poisoned);   // untouched by @ok
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

    // ----- Witnessed APPLY + duration-timeout (PR B) -----------------

    [Fact]
    public void WitnessedApply_SetsChip_ThenDurationTimeoutClears()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");
        h.Durations[100] = 10.0;   // the spell lasts 10s at the caster's level
        h.Applies.Add(new ApplyCastMatcher(
            MessageFlags.Blinded, "blind", 100,
            CasterMessageMatcher.TryCreate("{source} casts {spellname} on {target}!")!));

        h.NowMs = 1_000;
        h.EmitLine("A kobold shaman casts blind on Forged!");
        Assert.True(forged.Blinded);   // witnessed apply lit the chip

        // Inside the 10s window → a sweep (fires on every line) leaves it set.
        h.NowMs = 9_000;
        h.EmitLine("The kobold shaman swings.");
        Assert.True(forged.Blinded);

        // Past the window → the spell-data duration times out and clears it.
        h.NowMs = 12_000;
        h.EmitLine("The kobold shaman swings.");
        Assert.False(forged.Blinded);
    }

    [Fact]
    public void WitnessedApply_TargetOnlyLine_SetsChipWithResolvedDuration()
    {
        // An on-hit effect's line names only the victim. Two records share the
        // line; the one no monster in the room carries resolves nothing and must
        // not replace the resolved duration with the fallback cap.
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");
        h.Durations[318] = 12.0;
        h.Applies.Add(new ApplyCastMatcher(
            MessageFlags.MovementPrevented, "knockdown", 318,
            CasterMessageMatcher.TryCreate("{target} is knocked flat!")!));
        h.Applies.Add(new ApplyCastMatcher(
            MessageFlags.MovementPrevented, "knockdown", 1245,
            CasterMessageMatcher.TryCreate("{target} is knocked flat!")!));

        h.NowMs = 1_000;
        h.EmitLine("Someone is knocked flat!");
        Assert.False(forged.Held);     // names somebody else
        h.EmitLine("Forged is knocked flat!");
        Assert.True(forged.Held);

        // The member's own announce arrives after the hit and keeps the armed window.
        h.Say(@"Forged says ""@held""");
        h.NowMs = 12_000;
        h.EmitLine("The ultangir tramples Forged.");
        Assert.True(forged.Held);
        h.NowMs = 14_000;
        h.EmitLine("The ultangir tramples Forged.");
        Assert.False(forged.Held);
    }

    [Fact]
    public void WitnessedApply_PassesTheNamedCasterToTheResolver()
    {
        using Harness h = new();
        h.AddMember("Forged");
        h.Durations[100] = 10.0;
        h.Applies.Add(new ApplyCastMatcher(
            MessageFlags.Blinded, "blind", 100,
            CasterMessageMatcher.TryCreate("The {source} casts {spellname} on {target}!")!));

        h.EmitLine("The kobold shaman casts blind on Forged!");

        Assert.Equal("kobold shaman", Assert.Single(h.Sources));
    }

    [Fact]
    public void WitnessedApply_ClearedEarlyByCure()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");
        h.Durations[100] = 60.0;
        h.Applies.Add(new ApplyCastMatcher(
            MessageFlags.Blinded, "blind", 100,
            CasterMessageMatcher.TryCreate("{source} casts {spellname} on {target}!")!));
        h.Cures.Add(new CureCastMatcher(
            MessageFlags.Blinded, "cure-blindness",
            CasterMessageMatcher.TryCreate("You cast {s} on {s}!")!));

        h.NowMs = 0;
        h.EmitLine("A kobold shaman casts blind on Forged!");
        Assert.True(forged.Blinded);

        // A cure lands well before the 60s duration — clears immediately.
        h.NowMs = 3_000;
        h.EmitLine("You cast cure-blindness on Forged!");
        Assert.False(forged.Blinded);
    }

    [Fact]
    public void SayAnnounced_NoWitness_ClearsByFallbackCapSoItCannotStick()
    {
        using Harness h = new();
        PartyMember forged = h.AddMember("Forged");

        // A bare say announce carries no spell → no real duration, and confusion
        // has no cure to witness. The fallback cap still clears it eventually so
        // the chip can't stick forever.
        h.NowMs = 0;
        h.Say(@"Forged says ""@confused""");
        Assert.True(forged.Confused);

        h.NowMs = 200_000;   // past the ~180s fallback cap
        h.EmitLine("Something happens.");
        Assert.False(forged.Confused);
    }
}
