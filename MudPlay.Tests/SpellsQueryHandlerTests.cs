using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins @spells, answered from the Spell Book: `learned` lists the learned spells with
// their cast codes, `unlearned` only what the character could learn now (its level
// reached, a trainer's higher level included, and the alignment allowing it), and a
// cast code or name says learned / not learned, a code two spells share resolving to
// the one the alignment allows. Of two names a query matches equally, the learned spell
// answers.
public sealed class SpellsQueryHandlerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-spells-q-" + Path.GetRandomFileName());
    private readonly SpellbookState _book;
    private readonly MessageRouter _router = new();
    private readonly PlayerDatabase _players = new(new ProfileService(), () => null);
    private readonly List<byte[]> _wire = new();

    public SpellsQueryHandlerTests()
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Classes.json"), JsonSerializer.Serialize(new object[]
        {
            new Dictionary<string, object> { ["Number"] = 12, ["Name"] = "Mage", ["MageryType"] = 1, ["MageryLVL"] = 3 },
        }));
        File.WriteAllText(Path.Combine(dir, "Spells.json"), JsonSerializer.Serialize(new object[]
        {
            Spell(100, "magic missile", "mmis", 1),
            Spell(101, "fireball", "fbal", 10),
            Spell(102, "meteor", "mete", 30),
            Spell(103, "holy word", "word", 5, abil0: 97),     // GoodOnly
            Spell(104, "unholy word", "word", 5, abil0: 98),   // EvilOnly
            Spell(105, "late lesson", "late", 5),
            Spell(106, "fire bolt", "fbol", 15),
        }));
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), JsonSerializer.Serialize(new object[]
        {
            new Dictionary<string, object> { ["Number"] = 900, ["Action"] = "class 12:minlevel 25:learnspell 105" },
        }));
        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        _book = new SpellbookState(new KnownSpellCatalog(cache));
        _book.Refresh(classNumber: 12, level: 20, charAlign: 1);   // Good
        _book.SetObtainedByNames(new[] { "magic missile", "holy word", "fire bolt" });

        DefaultPatterns.Seed(_router);
        RemoteCommandManager engine = new(new ChatRouter(_router), new PartyState(), _players);
        engine.SetWireSender(_wire.Add);
        _ = new SpellsQueryHandler(engine, _book);
        _players.RecordObservation("Bob", null, null, null, null, null, null, Now);
        _players.EditCustomization("Bob", new PlayerCustomization(RemoteControls: PlayerRemoteControls.QuerySpells));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static Dictionary<string, object> Spell(int number, string name, string code, int reqLevel, int abil0 = 1)
    {
        Dictionary<string, object> row = new()
        {
            ["Number"] = number, ["Name"] = name, ["Short"] = code, ["Magery"] = 1, ["MageryLVL"] = 1,
            ["ReqLevel"] = reqLevel, ["Learnable"] = 1, ["Learned From"] = "\0", ["Classes"] = "(*)",
            ["MinBase"] = 1, ["MaxBase"] = 0, ["MinInc"] = 0, ["MinIncLVLs"] = 0, ["MaxInc"] = 0, ["MaxIncLVLs"] = 0,
            ["Dur"] = 0, ["DurInc"] = 0, ["DurIncLVLs"] = 0, ["Cap"] = 0, ["EnergyCost"] = 0, ["ManaCost"] = 0,
        };
        for (int x = 0; x < 10; x++)
        {
            row[$"Abil-{x}"] = x == 0 ? abil0 : 0;
            row[$"AbilVal-{x}"] = 0;
        }
        return row;
    }

    private string Ask(string message)
    {
        _wire.Clear();
        ChatLines.Hear(_router, new ChatLine(ChatChannel.TelepathIncoming, "Bob", message));
        string wire = Encoding.Latin1.GetString(Assert.Single(_wire)).TrimEnd('\r');
        int open = wire.IndexOf('{'), close = wire.LastIndexOf('}');
        return open >= 0 && close > open ? wire[(open + 1)..close] : wire;
    }

    [Fact]
    public void Learned_ListsEachLearnedSpellWithItsCastCode()
    {
        Assert.Equal("learned: magic missile (mmis), holy word (word), fire bolt (fbol)", Ask("@spells learned"));
    }

    [Fact]
    public void Unlearned_ListsOnlyWhatCanBeLearnedNow()
    {
        // meteor is above level 20, the trainer teaches late lesson at 25, and unholy
        // word is evil-only for a good character.
        Assert.Equal("learnable now: fireball (fbal)", Ask("@spells unlearned"));
    }

    [Theory]
    [InlineData("@spells word", "holy word (word): learned")]
    [InlineData("@spells MMIS", "magic missile (mmis): learned")]
    [InlineData("@spells fire", "fire bolt (fbol): learned")]   // the learned one of two that start so
    [InlineData("@spells fireb", "fireball (fbal): not learned")]
    [InlineData("@spells meteor", "meteor (mete): not learned")]
    [InlineData("@spells unholy word", "unholy word (word): not learned")]
    [InlineData("@spells wand", "no spell matches 'wand'")]
    public void OneSpell_ByCodeOrName_SaysWhetherItIsLearned(string ask, string reply)
    {
        Assert.Equal(reply, Ask(ask));
    }
}
