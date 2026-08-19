using System.Reflection;
using MudPlay.Game;
using MudPlay.Game.Conditions;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// <see cref="SelfAilmentChipResponder"/> — our own poison / blindness / disease
/// must light the SELF party-window chip. PartyAilmentTracker only mirrors OTHER
/// members' announced ailments; our own state is owned by ConditionTracker, so
/// without this responder the self row never showed poison (the reported bug).
/// </summary>
public sealed class SelfAilmentChipResponderTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, System.Array.Empty<CellAttributes>(), DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static MessageRecord Ail(string name, MessageFlags flags, string applied, string ends) => new(
        Id: MessageRecord.ComputeId(name, "", "", "", applied, ends),
        Name: name,
        Flags: flags,
        RawFlagsHex: (ushort)flags,
        CasterMessage: string.Empty,
        TargetMessage: string.Empty,
        WitnessMessage: string.Empty,
        AppliedMessage: applied,
        AppliedEndsWith: ends);

    private sealed class Harness : IDisposable
    {
        public LogService Log { get; } = new();
        public MessageStore Messages { get; } = new();
        public ConditionTracker Conditions { get; }
        public MessageRouter Router { get; } = new();
        public PartyManager Party { get; }
        public SelfAilmentChipResponder Responder { get; }

        public Harness()
        {
            Conditions = new ConditionTracker(Messages, Log);
            Messages.Messages.Add(Ail("poison",  MessageFlags.Poisoned, "You feel ill.",        "You feel better."));
            Messages.Messages.Add(Ail("blind",   MessageFlags.Blinded,  "You are blinded!",     "Your vision clears."));
            Messages.Messages.Add(Ail("disease", MessageFlags.Diseased, "You feel diseased.",   "The disease fades."));
            DefaultPatterns.Seed(Router);
            PartyState partyState = new();
            Party = new PartyManager(Router, partyState) { LocalCharacterName = "Forged" };
            Responder = new SelfAilmentChipResponder(Conditions, Party, Log);
        }

        public void FormParty() => Router.Dispatch(Line("Helper started to follow you."));

        public PartyMember? Self
        {
            get
            {
                foreach (PartyMember m in Party.State.Members)
                    if (m.IsSelf) return m;
                return null;
            }
        }

        public void Feed(string text)
        {
            var emitted = Line(text);
            typeof(ConditionTracker)
                .GetMethod("OnLine", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Conditions, new object[] { emitted });
        }

        public void Dispose()
        {
            Responder.Dispose();
            Party.Dispose();
            Conditions.Dispose();
        }
    }

    [Fact]
    public void Poison_LightsSelfChip()
    {
        using Harness h = new();
        h.FormParty();

        h.Feed("You feel ill.");

        Assert.True(h.Self!.Poisoned);
    }

    [Fact]
    public void PoisonCleared_DropsSelfChip()
    {
        using Harness h = new();
        h.FormParty();
        h.Feed("You feel ill.");
        Assert.True(h.Self!.Poisoned);

        h.Feed("You feel better.");

        Assert.False(h.Self!.Poisoned);
    }

    [Fact]
    public void BlindAndDisease_LightTheirOwnChips()
    {
        using Harness h = new();
        h.FormParty();

        h.Feed("You are blinded!");
        h.Feed("You feel diseased.");

        Assert.True(h.Self!.Blinded);
        Assert.True(h.Self!.Diseased);
        // Poison stayed untouched — per-flag edge tracking, not a blanket write.
        Assert.False(h.Self!.Poisoned);
    }

    [Fact]
    public void Solo_NoSelfRow_NoThrow()
    {
        using Harness h = new();
        // No party — SetMemberAilment is find-only, so the chip write is a harmless
        // no-op and nothing throws.
        h.Feed("You feel ill.");

        Assert.Null(h.Self);
    }

    [Fact]
    public void PoisonBeforeParty_LightsOncePartyForms()
    {
        using Harness h = new();
        // Poison lands while solo (no self row yet). When the party forms the
        // self-row add re-evaluates and stamps the still-active poison — no
        // ActiveFlags edge follows the join, so the CollectionChanged hook is what
        // makes this show.
        h.Feed("You feel ill.");
        Assert.Null(h.Self);

        h.FormParty();

        Assert.True(h.Self!.Poisoned);
    }
}
