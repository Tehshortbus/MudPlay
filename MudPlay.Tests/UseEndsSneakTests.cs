using System.Collections.Generic;
using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Stealth;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// An item command ends a sneak and a hide when the item's spell is cast, and only
// then (GAME_MECHANICS "What ends a sneak", the item commands). The game prints
// nothing when it does, so the client has to know from the command it sent.
public sealed class UseEndsSneakTests : IDisposable
{
    private readonly string _root;

    public UseEndsSneakTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-use-sneak-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----- the rule ------------------------------------------------------

    // Item rows shaped like the game data's: kind, then ability slots in order.
    // Ability 43 casts a spell on use, 114 marks the cast after it as a swing proc,
    // 42 teaches a spell, 54 is a light's illumination, 18 heals outright.
    private static readonly Dictionary<string, string> Items = new(StringComparer.OrdinalIgnoreCase)
    {
        ["waterskin"]            = Row(5, (43, 711)),
        ["iron ration"]          = Row(4, (43, 316)),
        ["torch"]                = Row(6, (54, 100)),
        ["incense"]              = Row(6, (43, 118)),
        ["brass key"]            = Row(7),
        ["scroll of shockshield"] = Row(9, (28, 1), (42, 64)),
        ["scroll of warding"]    = Row(9, (43, 500)),
        ["serpent wand"]         = Row(1, (43, 800)),
        ["flame blade"]          = Row(1, (114, 25), (43, 801)),
        ["nexus spear"]          = Row(1, (43, 72), (114, 25), (43, 431)),
        ["salve"]                = Row(0, (18, 20)),
        ["odd charm"]            = Row(0, (43, 999)),
        ["glowing token"]        = Row(10, (43, 72)),
        ["iron key"]             = Row(7, (119, 0)),
        ["healing herbs"]        = Row(0, (18, 20)),
        ["healing potion"]       = Row(0, (43, 800)),
        ["poisoned shuriken"]    = Row(1, (114, 10), (43, 304)),
    };

    // A second item record under a name already in Items: the game data has two
    // poisoned shurikens, one a swing proc and one that casts on use.
    private static readonly Dictionary<string, string> Namesakes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["poisoned shuriken"]    = Row(1, (43, 304)),
    };

    // Spell Targets: 1 is cast with no target; 8 needs one named; 999 isn't on record.
    private static int? TargetsOf(int spell) => spell switch
    {
        711 or 316 or 118 or 500 or 72 or 304 => 1,
        800 or 801 or 431 => 8,
        _ => null,
    };

    private static string Row(int itemType, params (int Abil, int Val)[] slots)
    {
        StringBuilder sb = new($"{{\"ItemType\":{itemType}");
        for (int i = 0; i < slots.Length; i++)
            sb.Append($",\"Abil-{i}\":{slots[i].Abil},\"AbilVal-{i}\":{slots[i].Val}");
        return sb.Append('}').ToString();
    }

    private static ItemUseStealthRule Rule(params string[] held) => Rule(null, held);

    private static ItemUseStealthRule Rule(List<string>? debug, params string[] held) =>
        new(() => held, FactsOf, debug is null ? null : debug.Add);

    private static IReadOnlyList<ItemUseStealthRule.Facts> FactsOf(string name)
    {
        List<ItemUseStealthRule.Facts> records = new();
        foreach (Dictionary<string, string> table in new[] { Items, Namesakes })
            if (table.TryGetValue(name, out string? json))
                records.Add(ItemUseStealthRule.Facts.Read(TestRows.Row(json), TargetsOf));
        return records;
    }

    private static readonly string[] Pack =
    {
        "waterskin", "2 iron ration", "torch", "incense", "brass key", "scroll of shockshield",
        "scroll of warding", "serpent wand", "flame blade", "nexus spear", "salve", "odd charm",
        "mystery box", "glowing token",
    };

    [Theory]
    // A cast with no target: the sneak ends.
    [InlineData("use waterskin", true)]
    [InlineData("us waterskin", true)]
    [InlineData("USE Waterskin", true)]
    [InlineData("drink waterskin", true)]
    [InlineData("dri waterskin", true)]
    [InlineData("eat iron ration", true)]
    [InlineData("use incense", true)]
    [InlineData("light incense", true)]
    [InlineData("lig incense", true)]
    [InlineData("read scroll of warding", true)]
    [InlineData("use nexus spear", true)]            // its on-use cast comes before the proc
    // Nothing is cast: the sneak holds.
    [InlineData("use torch", false)]
    [InlineData("light torch", false)]
    [InlineData("use brass key n", false)]
    [InlineData("use brass key", false)]
    [InlineData("read scroll of shockshield", false)]
    [InlineData("use salve", false)]
    [InlineData("use flame blade", false)]           // a swing proc isn't cast by a use
    // Refused before anything is cleared.
    [InlineData("use serpent wand", false)]          // `You must specify a target for that spell!`
    [InlineData("eat waterskin", false)]             // `eat` finds only food
    [InlineData("drink iron ration", false)]
    [InlineData("light waterskin", false)]
    [InlineData("use", false)]                       // the syntax line
    [InlineData("light", false)]                     // the light level
    [InlineData("read plaque", false)]               // not carried: a look
    // A word after the item may name a player or a monster: counted as ended.
    [InlineData("use serpent wand orc", true)]
    [InlineData("use waterskin bob", true)]
    // The client can't tell: counted as ended.
    [InlineData("use lever", true)]                  // not in the pack
    [InlineData("drink ale", true)]
    [InlineData("use mystery box", true)]            // carried, not in the game data
    [InlineData("use odd charm", true)]              // its spell isn't in the game data
    public void ItemCommand_EndsTheSneakOnlyWhenItsSpellIsCast(string command, bool ends) =>
        Assert.Equal(ends, SneakBreakingCommands.EndsSneak(command, items: Rule(Pack)));

    // Spellings the game doesn't take as these commands aren't item commands.
    [Theory]
    [InlineData("u waterskin")]
    [InlineData("rea scroll of warding")]            // `rea` is ready
    [InlineData("dr waterskin")]
    [InlineData("li incense")]
    [InlineData("ea iron ration")]
    public void ItemCommand_OnlyTheSpellingsTheGameTakes(string command) =>
        Assert.Null(ItemUseStealthRule.VerbOf(command.Split(' ')[0]));

    [Fact]
    public void ItemCommand_WithThePackUnread_CountsAsEnded_ExceptARead()
    {
        ItemUseStealthRule nothingHeld = Rule();
        Assert.True(SneakBreakingCommands.EndsSneak("use waterskin", items: nothingHeld));
        Assert.True(SneakBreakingCommands.EndsSneak("eat iron ration", items: nothingHeld));
        Assert.False(SneakBreakingCommands.EndsSneak("read scroll of warding", items: nothingHeld));
    }

    // The item is the longest run of leading words that names something held, not
    // the first word alone. With an iron ration carried beside the key, `use iron key
    // n` is the key, and the sneak stands.
    [Fact]
    public void ItemWords_TheLongestRunThatNamesSomethingHeld_IsTheItem()
    {
        Assert.False(SneakBreakingCommands.EndsSneak("use iron key n", items: Rule("iron key")));
        Assert.False(SneakBreakingCommands.EndsSneak("use iron key n", items: Rule("iron ration", "iron key")));
        Assert.False(SneakBreakingCommands.EndsSneak("use iron key", items: Rule("iron ration", "iron key")));
        // The other way round: herbs that cast nothing held ahead of a potion that casts.
        Assert.True(SneakBreakingCommands.EndsSneak("use healing potion bob", items: Rule("healing herbs", "healing potion")));
        Assert.True(SneakBreakingCommands.EndsSneak("use healing potion bob", items: Rule("healing potion")));
        // The key isn't held at all (it is being picked up as the command goes out).
        Assert.True(SneakBreakingCommands.EndsSneak("use iron key n", items: Rule("torch")));
    }

    // Words that fit more than one held item: which one the game takes isn't known,
    // so the sneak counts as ended when any of them would end it.
    [Fact]
    public void ItemWords_FittingSeveralHeldItems_EndTheSneakIfAnyWould()
    {
        Assert.True(SneakBreakingCommands.EndsSneak("use iron", items: Rule("iron key", "iron ration")));
        Assert.True(SneakBreakingCommands.EndsSneak("use iron", items: Rule("iron ration", "iron key")));
        Assert.True(SneakBreakingCommands.EndsSneak("use healing bob", items: Rule("healing herbs", "healing potion")));
        // None of them casts with no target: intact.
        Assert.False(SneakBreakingCommands.EndsSneak("use healing", items: Rule("healing herbs", "healing potion")));
    }

    // Two item records under one name: the held name doesn't say which it is.
    [Fact]
    public void ItemName_SharedByTwoRecords_EndsTheSneakIfEitherWould()
    {
        Assert.True(SneakBreakingCommands.EndsSneak("use poisoned shuriken", items: Rule("poisoned shuriken")));
        Assert.False(SneakBreakingCommands.EndsSneak("use flame blade", items: Rule("flame blade", "poisoned shuriken")));
    }

    // Each verdict is written out, the ones that leave the sneak alone included.
    [Fact]
    public void EachVerdict_IsLogged_WithTheItemAndTheReason()
    {
        List<string> log = new();
        ItemUseStealthRule rule = Rule(log, "iron ration", "iron key", "waterskin");

        SneakBreakingCommands.EndsSneak("use iron key n", items: rule);
        SneakBreakingCommands.EndsSneak("use waterskin", items: rule);
        SneakBreakingCommands.EndsSneak("read plaque", items: rule);

        Assert.Equal(new[]
        {
            "'use iron key n': iron key casts nothing on use: sneak intact",
            "'use waterskin': waterskin casts with no target (Targets 1): sneak ended",
            "'read plaque': nothing held by that name, so a look: sneak intact",
        }, log);
    }

    // ----- hide ----------------------------------------------------------

    [Theory]
    [InlineData("use waterskin", true)]
    [InlineData("sea", true)]
    [InlineData("wear plate mail", true)]
    [InlineData("rem torch", true)]
    [InlineData("open chest", true)]
    [InlineData("buy torch", true)]
    [InlineData("rest", true)]
    [InlineData(".hello", true)]
    [InlineData("disarm trap n", false)]
    [InlineData("follow bob", false)]
    [InlineData("move rock", false)]
    public void AlsoEndsHide_AllButTheCommandsWithNoHiddenClear(string command, bool endsHide) =>
        Assert.Equal(endsHide, SneakBreakingCommands.AlsoEndsHide(command));

    private sealed class World : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PlayerState State { get; } = new();
        public StealthManager Stealth { get; }
        // The manager's re-sneak, cooldown-retry, answer and held-cast timers.
        public ManualTimers StealthTimers { get; } = new();
        public List<string> Wire { get; } = new();
        public bool EngineDriving;
        public bool AutoSneak = true;
        public bool AutoHide;
        private readonly ItemUseStealthRule _items;

        public World()
        {
            DefaultPatterns.Seed(Router);
            _items = new ItemUseStealthRule(() => { RuleConsulted++; return Pack; }, FactsOf);
            Stealth = new StealthManager(Router, State, new LogService(), schedule: StealthTimers.Arm);
            Stealth.SetAutoToggles(() => AutoSneak, () => AutoHide);
            Stealth.SetEngineDrivingCheck(() => EngineDriving);
            Stealth.SetWireSender(SendBytes);
        }

        // How many times the item rule was asked what the pack holds.
        public int RuleConsulted;

        // Every command goes out through here and is reported once, as
        // AppServices.NoteSentForSneak reports it.
        public void Send(string command)
        {
            Wire.Add(command);
            Stealth.NoteCommandSent(command, () => false, _items);
        }

        public void SendBytes(byte[] bytes) => Send(Encoding.Latin1.GetString(bytes).TrimEnd('\r'));

        public void Feed(string line) =>
            Router.Dispatch(new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Dispose() => Stealth.Dispose();
    }

    [Fact]
    public void Hidden_AUseThatCasts_DropsTheHide()
    {
        using World w = new();
        w.Feed("Attempting to hide...");
        Assert.True(w.State.IsHidden);

        w.Send("use waterskin");

        Assert.False(w.State.IsHidden);
        Assert.Equal(StealthState.Idle, w.Stealth.State);
        Assert.False(w.Stealth.IsStealthedHere);
    }

    [Fact]
    public void Hidden_ACommandThatCastsNothing_OrHasNoHiddenClear_KeepsTheHide()
    {
        using World w = new();
        w.Feed("Attempting to hide...");

        w.Send("use torch");
        w.Send("disarm trap n");
        w.Send("get sword");

        Assert.True(w.State.IsHidden);
        Assert.Equal(StealthState.Hidden, w.Stealth.State);
    }

    [Fact]
    public void Hidden_ASearchOrAGearSwap_DropsTheHide()
    {
        using World w = new();
        w.Feed("Attempting to hide...");
        w.Send("sea");
        Assert.False(w.State.IsHidden);

        w.Feed("Attempting to hide...");
        w.Send("wear plate mail");
        Assert.False(w.State.IsHidden);
    }

    // The game drops a hide as it takes the move command, so a move that goes nowhere
    // ends it without any room change to say so.
    [Fact]
    public void Hidden_ADirectionSent_DropsTheHide_BeforeAnyRoomChange()
    {
        using World w = new() { EngineDriving = true };
        w.Feed("Attempting to hide...");

        w.Stealth.NoteDirectionalMoveSent();

        Assert.False(w.State.IsHidden);
        Assert.Equal(StealthState.Idle, w.Stealth.State);
    }

    // A text exit (`go path`) isn't the move command, so what that command does on
    // being taken isn't assumed of it. The hide goes when the room changes.
    [Fact]
    public void Hidden_ATextExitSent_KeepsTheHide_UntilTheRoomChanges()
    {
        using World w = new();
        w.Feed("Attempting to hide...");

        w.Stealth.NoteTypedMove();                  // MoveSent alone: no direction went out
        Assert.True(w.State.IsHidden);
        Assert.Empty(w.Wire);

        w.Stealth.NoteRoomChanged();
        Assert.False(w.State.IsHidden);
    }

    // `hid` sent, then a command that ends a hide: the game runs the hide and then
    // ends it, and the answer to the `hid` arrives after. It isn't read as hidden.
    [Fact]
    public void HideStillBeingAttempted_ThenAHideEndingCommand_ItsAnswerIsNotReadAsHidden()
    {
        using World w = new() { AutoSneak = false, AutoHide = true };
        w.Stealth.NoteIdleOpportunity();            // `hid`
        Assert.Equal(StealthState.AttemptingHide, w.Stealth.State);

        w.Send("sea");
        w.Feed("Attempting to hide...");

        Assert.Equal(new[] { "hid", "sea" }, w.Wire);
        Assert.False(w.State.IsHidden);
        Assert.False(w.Stealth.IsStealthedHere);

        // The next hide's answer counts again.
        w.Stealth.NoteIdleOpportunity();
        w.Feed("Attempting to hide...");
        Assert.True(w.State.IsHidden);
    }

    // A direction sent while the hide is still being attempted ends it the same way.
    [Fact]
    public void HideStillBeingAttempted_ThenADirection_ItsAnswerIsNotReadAsHidden()
    {
        using World w = new() { AutoSneak = false, AutoHide = true };
        w.Stealth.NoteIdleOpportunity();

        w.Stealth.NoteDirectionalMoveSent();
        w.Feed("Attempting to hide...");

        Assert.False(w.State.IsHidden);
    }

    // With Auto-Sneak off there is no sneak to take back after a cast, but the hide is
    // ended all the same, by a spell cast as by an item's.
    [Fact]
    public void AutoSneakOff_ASpellCastAndAnItemCast_BothDropTheHide()
    {
        using World spell = new() { AutoSneak = false };
        spell.Feed("Attempting to hide...");
        spell.Stealth.ReSneakAfterCast();
        Assert.False(spell.State.IsHidden);

        using World item = new() { AutoSneak = false };
        item.Feed("Attempting to hide...");
        item.Send("use waterskin");
        Assert.False(item.State.IsHidden);

        Assert.Empty(spell.Wire);                   // and nothing is sent for either
        Assert.Equal(new[] { "use waterskin" }, item.Wire);
    }

    // A step out of a hide: the move ends the hide and sets no sneak, so with
    // Auto-Sneak on an `sn` goes out first, for a step an engine takes as for one typed.
    [Fact]
    public void StepOutOfAHide_SneaksFirst_TypedOrEngine()
    {
        using World typed = new();
        typed.Feed("Attempting to hide...");
        typed.Stealth.NoteDirectionalMoveSent();    // the observer raises this ahead of MoveSent
        typed.Stealth.NoteTypedMove();
        Assert.Equal(new[] { "sn" }, typed.Wire);

        using World engine = new() { EngineDriving = true };
        engine.Stealth.SetMovementCoordinator(new MovementCoordinator());
        engine.Feed("Attempting to hide...");
        Assert.False(engine.Stealth.ReadyToMoveSneaking());   // held for the `sn` answer
        Assert.Equal(new[] { "sn" }, engine.Wire);
        engine.Feed("Attempting to sneak...");
        Assert.True(engine.Stealth.ReadyToMoveSneaking());
        Assert.Equal(new[] { "sn" }, engine.Wire);
    }

    // A monster in the room: an `sn` can't take, so the hide isn't given up for one.
    [Fact]
    public void StepOutOfAHide_WithAMonsterHere_GoesAsItStands()
    {
        using World w = new() { EngineDriving = true };
        w.Stealth.SetMovementCoordinator(new MovementCoordinator());
        w.Stealth.SetSneakBlockCheck(() => true);
        w.Feed("Attempting to hide...");

        Assert.True(w.Stealth.ReadyToMoveSneaking());

        Assert.Empty(w.Wire);
        Assert.True(w.State.IsHidden);
    }

    // ----- the sneak under a hide ----------------------------------------
    // `hid` never touches the game's sneak flag, so a character that sneaked in and
    // then hid is still sneaking. Its step out is a sneaked one as it stands, and an
    // `sn` for it would throw that sneak away and roll again.

    [Fact]
    public void SneakedInThenHid_EngineStep_SendsNoSn_IsNotHeld_AndIsSneakingAgain()
    {
        using World w = new() { EngineDriving = true };
        MovementCoordinator coordinator = new();
        w.Stealth.SetMovementCoordinator(coordinator);
        w.Feed("Sneaking...");
        w.Feed("Attempting to hide...");
        Assert.Equal(StealthState.Hidden, w.Stealth.State);

        Assert.True(w.Stealth.ReadyToMoveSneaking());
        Assert.False(coordinator.IsGateAsserted(MovementCoordinator.SneakSettleGate));
        w.Stealth.RequestPreMoveStealth();
        w.Stealth.NoteDirectionalMoveSent();        // the step's direction goes out
        w.Stealth.NoteTypedMove();

        Assert.Empty(w.Wire);
        Assert.Equal(StealthState.Sneaking, w.Stealth.State);
        Assert.True(w.State.IsSneaking);
        Assert.False(w.State.IsHidden);

        // The room stepped into still has to confirm it.
        Assert.False(w.Stealth.IsStealthedHere);
        w.Feed("Sneaking...");
        w.Stealth.NoteRoomChanged();
        Assert.True(w.Stealth.IsStealthedHere);
        Assert.Empty(w.Wire);
    }

    [Fact]
    public void SneakedInThenHid_TypedStep_SendsNoSn_AndIsSneakingAgain()
    {
        using World w = new();
        w.Feed("Sneaking...");
        w.Feed("Attempting to hide...");

        w.Stealth.NoteDirectionalMoveSent();
        w.Stealth.NoteTypedMove();

        Assert.Empty(w.Wire);
        Assert.Equal(StealthState.Sneaking, w.Stealth.State);
        Assert.True(w.State.IsSneaking);
    }

    // Both toggles on, an engine walking: arrive sneaking, Auto-Hide hides, the step
    // goes on from there.
    [Fact]
    public void BothTogglesOn_ArriveSneaking_AutoHide_ThenStep_SendsNoSn()
    {
        using World w = new() { EngineDriving = true, AutoHide = true };
        w.Stealth.SetMovementCoordinator(new MovementCoordinator());
        w.Feed("Sneaking...");
        w.Stealth.NoteRoomChanged();
        w.Stealth.NoteIdleOpportunity();
        w.Feed("Attempting to hide...");
        Assert.Equal(new[] { "hid" }, w.Wire);

        Assert.True(w.Stealth.ReadyToMoveSneaking());
        w.Stealth.NoteDirectionalMoveSent();

        Assert.Equal(new[] { "hid" }, w.Wire);
        Assert.Equal(StealthState.Sneaking, w.Stealth.State);
    }

    // The sneak under the hide is ended by anything that ends a sneak. A cast item
    // ends the hide with it; `disarm` ends the sneak and leaves the hide. Either way
    // the step out has no sneak to ride on and sends `sn` first.
    [Theory]
    [InlineData("use waterskin", false)]
    [InlineData("disarm trap n", true)]
    public void SneakUnderTheHide_EndedByACommandMeanwhile_TheStepSneaksFirst(string command, bool stillHidden)
    {
        using World w = new() { EngineDriving = true };
        w.Stealth.SetMovementCoordinator(new MovementCoordinator());
        w.Feed("Sneaking...");
        w.Feed("Attempting to hide...");

        w.Send(command);
        Assert.Equal(stillHidden, w.State.IsHidden);

        Assert.False(w.Stealth.ReadyToMoveSneaking());   // held for the `sn` answer
        Assert.Equal(new[] { command, "sn" }, w.Wire);
        Assert.False(w.State.IsHidden);
    }

    // A hide that fails over a sneak leaves the sneak where it was.
    [Fact]
    public void AutoHideOverASneak_ThatFails_IsStillSneaking()
    {
        using World w = new() { AutoHide = true };
        w.Feed("Sneaking...");
        w.Stealth.NoteIdleOpportunity();

        w.Feed("Attempting to hide... You don't think you are hidden.");

        Assert.Equal(StealthState.Sneaking, w.Stealth.State);
        Assert.True(w.State.IsSneaking);
        Assert.False(w.State.IsHidden);
    }

    // ----- a hide the game turns away, and one typed by hand ----------------

    // An `hid` refused with no "Attempting to hide..." used to stay open: the next
    // hide-ending command counted an answer as owed, and the next real hide in the
    // room was thrown away as that answer.
    [Theory]
    [InlineData("You must wait before you may do that!")]
    [InlineData("You can't seem to move anywhere to hide!")]
    [InlineData("You are too stunned to move anywhere to hide!")]
    public void AutoHideRefused_SettlesTheAttempt_AndALaterHideIsRead(string refusal)
    {
        using World w = new() { AutoSneak = false, AutoHide = true };
        w.Stealth.NoteIdleOpportunity();
        w.Feed(refusal);
        Assert.Equal(StealthState.Idle, w.Stealth.State);

        w.Send("sea");
        w.Send("hid");
        w.Feed("Attempting to hide...");

        Assert.True(w.State.IsHidden);
    }

    // The wait line answers many commands; with no hide unanswered it settles nothing.
    [Fact]
    public void MustWaitLine_WithNoHideUnanswered_LeavesAHideAlone()
    {
        using World w = new();
        w.Feed("Attempting to hide...");
        w.Feed("You must wait before you may do that!");
        Assert.True(w.State.IsHidden);
    }

    // An attempt the game answers with a line the client doesn't read at all: a fresh
    // `hid` starts the count of owed answers again.
    [Fact]
    public void AFreshHid_ClearsAnswersOwedToOlderOnes()
    {
        using World w = new() { AutoSneak = false, AutoHide = true };
        w.Stealth.NoteIdleOpportunity();            // answered by nothing we read
        w.Send("sea");                              // counts its answer as owed

        w.Send("hid");
        w.Feed("Attempting to hide...");

        Assert.True(w.State.IsHidden);
    }

    // An `hid` typed by hand (or sent by a macro) and still unanswered when a
    // hide-ending command goes out: its answer isn't read as hidden either.
    [Fact]
    public void TypedHideStillUnanswered_ThenAHideEndingCommand_ItsAnswerIsNotReadAsHidden()
    {
        using World w = new() { AutoSneak = false };
        w.Send("hid");
        Assert.Equal(StealthState.Idle, w.Stealth.State);   // a typed one changes no state

        w.Send("sea");
        w.Feed("Attempting to hide...");
        Assert.False(w.State.IsHidden);

        w.Send("hid");                              // and the next one counts
        w.Feed("Attempting to hide...");
        Assert.True(w.State.IsHidden);
    }

    [Fact]
    public void HideWithAnObject_IsAStash_NotAHideAttempt()
    {
        Assert.True(SneakBreakingCommands.IsHideAttempt("hid"));
        Assert.True(SneakBreakingCommands.IsHideAttempt("HIDE"));
        Assert.False(SneakBreakingCommands.IsHideAttempt("hide 50 gold"));
        Assert.False(SneakBreakingCommands.IsHideAttempt("hi"));
    }

    // ----- what a verdict costs ------------------------------------------

    // With no sneak or hide to end, what a command would do isn't worked out at all.
    [Fact]
    public void NothingToEnd_TheItemRuleIsNotConsulted()
    {
        using World w = new() { AutoSneak = false };
        w.Send("use waterskin");
        w.Send("eat iron ration");
        Assert.Equal(0, w.RuleConsulted);

        w.Feed("Sneaking...");
        w.Send("use torch");
        Assert.Equal(1, w.RuleConsulted);
    }

    // The name → facts map is built once per game-data set and keeps every record of
    // a name, so two items sharing one are both weighed.
    [Fact]
    public void FactsIndex_BuiltOncePerSet_KeepsEveryRecordOfAName()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), """
            [
              { "Number": 559,  "Name": "poisoned shuriken", "ItemType": 1, "Abil-0": 114, "AbilVal-0": 10, "Abil-1": 43, "AbilVal-1": 304 },
              { "Number": 1825, "Name": "poisoned shuriken", "ItemType": 1, "Abil-0": 43, "AbilVal-0": 304 },
              { "Number": 283,  "Name": "waterskin", "ItemType": 5, "Abil-0": 43, "AbilVal-0": 711 },
              { "Number": 175,  "Name": "torch", "ItemType": 6, "Abil-0": 54, "AbilVal-0": 100 }
            ]
            """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), """
            [
              { "Number": 304, "Name": "poison", "Targets": 1 },
              { "Number": 711, "Name": "waterskin", "Targets": 1 }
            ]
            """);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");

        IReadOnlyList<ItemUseStealthRule.Facts> shurikens = ItemUseFactsIndex.Lookup(cache, "Poisoned  Shuriken ");
        Assert.Equal(2, shurikens.Count);
        Assert.False(shurikens[0].Casts);
        Assert.True(shurikens[1].Casts);
        Assert.Equal(new ItemUseStealthRule.Facts(5, true, 1), Assert.Single(ItemUseFactsIndex.Lookup(cache, "waterskin")));
        Assert.Equal(new ItemUseStealthRule.Facts(6, false, null), Assert.Single(ItemUseFactsIndex.Lookup(cache, "torch")));
        Assert.Empty(ItemUseFactsIndex.Lookup(cache, "lever"));

        // Answered from the map, not from the table: the same list comes back, and it
        // still does once the Items file is gone.
        Assert.Same(shurikens, ItemUseFactsIndex.Lookup(cache, "poisoned shuriken"));
        File.Delete(Path.Combine(_root, "alpha", "Items.json"));
        Assert.Same(shurikens, ItemUseFactsIndex.Lookup(cache, "poisoned shuriken"));

        ItemUseStealthRule rule = new(() => new[] { "poisoned shuriken", "torch" }, name => ItemUseFactsIndex.Lookup(cache, name));
        Assert.True(SneakBreakingCommands.EndsSneak("use poisoned shuriken", items: rule));
        Assert.False(SneakBreakingCommands.EndsSneak("use torch", items: rule));
    }

    // ----- sneak ---------------------------------------------------------

    [Fact]
    public void Sneaking_AUseThatCasts_EndsTheSneak_AndTheNextMoveReSneaks()
    {
        using World w = new() { EngineDriving = true };
        w.Feed("Sneaking...");

        w.Send("use waterskin");
        Assert.False(w.State.IsSneaking);
        Assert.False(w.Stealth.IsStealthedHere);

        w.Stealth.RequestPreMoveStealth();
        Assert.Equal(new[] { "use waterskin", "sn" }, w.Wire);
    }

    [Fact]
    public void Sneaking_LightingATorch_KeepsTheSneak_AndSendsNoSn()
    {
        using World w = new() { EngineDriving = true };
        w.Feed("Sneaking...");

        w.Send("use torch");
        w.Stealth.RequestPreMoveStealth();

        Assert.True(w.State.IsSneaking);
        Assert.Equal(new[] { "use torch" }, w.Wire);
    }

    // An item cast marks the sneak broken on its `use` and is then followed by the
    // cast path's own re-sneak. One `sn` answers both.
    [Fact]
    public void ItemCast_UseThenCastFired_ReSneaksOnce()
    {
        using World w = new();                      // nothing driving: the in-place re-sneak is live
        w.Feed("Sneaking...");

        w.Send("use waterskin");
        Assert.Contains(StealthManager.InPlaceReSneakDelay, w.StealthTimers.PendingDelays);
        w.Stealth.ReSneakAfterCast();

        Assert.Equal(new[] { "use waterskin", "sn" }, w.Wire);
        Assert.DoesNotContain(StealthManager.InPlaceReSneakDelay, w.StealthTimers.PendingDelays);
        w.StealthTimers.RunAll();
        Assert.Equal(new[] { "use waterskin", "sn" }, w.Wire);
    }

    // A token, or any item that teleports: the `use` is the move. The sneak is spent
    // as it goes out, so the landing isn't read as a sneak that failed on the way in,
    // and one `sn` takes it again there.
    [Fact]
    public void ItemTeleport_TheUseIsTheMove_LandingReSneaksOnce_AndIsNotALostEntry()
    {
        using World w = new() { EngineDriving = true };
        w.Feed("Sneaking...");
        int lostEntries = 0;
        w.Stealth.SilentSneakLost += () => lostEntries++;

        w.Stealth.RequestPreMoveStealth();          // the walker's hook, ahead of the step
        w.Send("use glowing token");
        w.Stealth.NoteRoomChanged();

        Assert.Equal(new[] { "use glowing token", "sn" }, w.Wire);
        Assert.Equal(0, lostEntries);
        Assert.False(w.Stealth.TakeSneakBrokeOnEntry());
    }

    // ----- the approach hook --------------------------------------------

    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly RoomKey Oasis = new(1, 1);
    private static readonly RoomKey Desert = new(1, 2);

    // The desert room's heat (spell 700) is countered by buff 300, raised with `use
    // waterskin` (item 60).
    private static AutoHazardCounterProvisioner Waterskin(World w)
    {
        Room desert = new()
        {
            Key = Desert, Name = "B", Spell = 700, Exits = new Dictionary<Direction, RoomExit>(),
        };
        RoomHazardIndex.RoomHazard hazard = new(
            new IReadOnlyList<int>[] { new[] { 60 } },
            new[] { new RoomHazardIndex.BuffCounter(300, 712, 600, new[] { 60 }, Array.Empty<int>()) });
        AutoHazardCounterProvisioner hazards = new(
            resolveRoom: key => key == Desert ? desert : null,
            hazardForSpell: spell => spell == 700 ? hazard : null,
            carriedCount: id => id == 60 ? 1 : 0,
            itemName: id => id == 60 ? "waterskin" : null);
        hazards.SetWireSender(w.SendBytes);
        return hazards;
    }

    private (RoomGraphManager Graph, BfsMapper Bfs, RoomTracker Tracker, MovementCoordinator Coordinator) NewMap()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), RoomsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        tracker.SetLocated(Oasis);
        return (graph, new BfsMapper(graph), tracker, new MovementCoordinator());
    }

    // The walker raises the hazard buff as it commits to the step, ahead of the
    // pre-move sneak. The `use` ends the sneak, so the pre-move hook takes it again
    // and the step itself goes out sneaked.
    [Fact]
    public void Walker_StepIntoAHazardRoom_UsesThenSneaksThenMoves()
    {
        using World w = new() { EngineDriving = true };
        var (graph, bfs, tracker, coordinator) = NewMap();
        AutoWalkManager walker = new(graph, bfs, tracker, coordinator);
        AutoHazardCounterProvisioner hazards = Waterskin(w);
        walker.SetWireSender(w.SendBytes);
        walker.SetApproachRoomHook(hazards.OnApproachingRoom);
        walker.SetPreMoveHook(w.Stealth.RequestPreMoveStealth);
        w.Feed("Sneaking...");

        Assert.True(walker.WalkTo(Desert));

        Assert.Equal(new[] { "use waterskin", "sn", "n" }, w.Wire);
    }

    [Fact]
    public void LoopRunner_StepIntoAHazardRoom_UsesThenSneaksThenMoves()
    {
        using World w = new() { EngineDriving = true };
        var (graph, bfs, tracker, coordinator) = NewMap();
        LoopRunner runner = new(tracker, coordinator, graph: graph, bfs: bfs, postToUi: a => a());
        AutoHazardCounterProvisioner hazards = Waterskin(w);
        runner.SetWireSender(w.SendBytes);
        runner.SetApproachRoomHook(hazards.OnApproachingRoom);
        runner.SetPreMoveHook(w.Stealth.RequestPreMoveStealth);
        w.Feed("Sneaking...");

        Assert.True(runner.Start(new Loop("oasis", new[] { Oasis, Desert })));

        Assert.Equal(new[] { "use waterskin", "sn", "n" }, w.Wire);
    }

    // With the buff still up no `use` goes out, the sneak stands and no `sn` is spent.
    [Fact]
    public void Walker_HazardBuffStillUp_MovesWithoutUseOrSn()
    {
        using World w = new() { EngineDriving = true };
        var (graph, bfs, tracker, coordinator) = NewMap();
        AutoWalkManager walker = new(graph, bfs, tracker, coordinator);
        AutoHazardCounterProvisioner hazards = Waterskin(w);
        walker.SetWireSender(w.SendBytes);
        walker.SetApproachRoomHook(hazards.OnApproachingRoom);
        walker.SetPreMoveHook(w.Stealth.RequestPreMoveStealth);
        hazards.OnApproachingRoom(Desert);          // raised a moment ago
        w.Wire.Clear();
        w.Feed("Sneaking...");

        Assert.True(walker.WalkTo(Desert));

        Assert.Equal(new[] { "n" }, w.Wire);
    }
}
