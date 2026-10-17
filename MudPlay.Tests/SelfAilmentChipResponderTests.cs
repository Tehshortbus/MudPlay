using MudPlay.Game;
using MudPlay.Game.Conditions;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// SelfAilmentChipResponder — our own poison / blindness / disease
// must light the SELF party-window chip. PartyAilmentTracker only mirrors OTHER
// members' announced ailments; our own state is owned by ConditionTracker, so
// without this responder the self row never showed poison (the reported bug).
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
        // The game's output, fed the way the client receives it.
        public ServerFeed Server { get; } = new();
        public MessageRouter Router { get; } = new();
        public PartyManager Party { get; }
        public SelfAilmentChipResponder Responder { get; }

        public Harness()
        {
            Conditions = new ConditionTracker(Messages, Log);
            Conditions.AttachLineExtractor(Server.Lines);
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

        public void Feed(string text) => Server.Line(text);

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
    public void Solo_SelfRowPresent_LightsChip()
    {
        using Harness h = new();
        // Solo now keeps a lone self row (PartyWindow self-display), so an ailment
        // landing while solo lights that row's chip immediately.
        Assert.NotNull(h.Self);
        h.Feed("You feel ill.");

        Assert.True(h.Self!.Poisoned);
    }

    [Fact]
    public void PoisonWhileSolo_LightsImmediately_StaysThroughPartyForm()
    {
        using Harness h = new();
        // Poison lands while solo — the self row already exists, so it lights at once
        // (no longer a "waits for the party to form" case).
        h.Feed("You feel ill.");
        Assert.True(h.Self!.Poisoned);

        // Forming a party reuses the same self row, chip still lit.
        h.FormParty();

        Assert.True(h.Self!.Poisoned);
    }
}
