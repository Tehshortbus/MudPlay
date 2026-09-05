using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.GameData;
using Xunit;

namespace MudPlay.Tests;

// ItemCastSpells reads what spell(s) an item delivers from its Abil-N / AbilVal-N
// slots. A %Spell (114) or CastOnKill% (1114) preceding a CastsSp (43) turns it into
// a combat proc; a bare CastsSp is a command-activated "use <item>" cast. The item's
// on-use / proc message anchors to PrimaryCastSpell — the command cast if any, else
// the first proc — so items sharing a cast spell share one message record.
public sealed class ItemCastSpellsTests
{
    private static JsonElement Item(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void BareCastsSp_IsOnUse()
    {
        JsonElement it = Item("{\"Abil-0\":43,\"AbilVal-0\":114}");
        IReadOnlyList<ItemCastSpells.CastEntry> casts = ItemCastSpells.ReadCasts(it);
        Assert.Single(casts);
        Assert.Equal(114, casts[0].SpellNumber);
        Assert.Equal(ItemCastSpells.CastTrigger.OnUse, casts[0].Trigger);
    }

    [Fact]
    public void PercentSpell_BeforeCastsSp_IsSwingProc()
    {
        // 40% swing proc of spell 170.
        JsonElement it = Item("{\"Abil-0\":114,\"AbilVal-0\":40,\"Abil-1\":43,\"AbilVal-1\":170}");
        IReadOnlyList<ItemCastSpells.CastEntry> casts = ItemCastSpells.ReadCasts(it);
        Assert.Single(casts);
        Assert.Equal(170, casts[0].SpellNumber);
        Assert.Equal(ItemCastSpells.CastTrigger.Swing, casts[0].Trigger);
        Assert.Equal(40, casts[0].Percent);
    }

    [Fact]
    public void OnUse_Preferred_OverProc_ForPrimary()
    {
        // shimmering longsword shape: bare CastsSp 114 (use-bless) THEN a 40%/swing proc of 170.
        JsonElement it = Item(
            "{\"Abil-0\":43,\"AbilVal-0\":114,\"Abil-1\":114,\"AbilVal-1\":40,\"Abil-2\":43,\"AbilVal-2\":170}");
        IReadOnlyList<ItemCastSpells.CastEntry> casts = ItemCastSpells.ReadCasts(it);
        Assert.Equal(2, casts.Count);
        Assert.Equal(ItemCastSpells.CastTrigger.OnUse, casts[0].Trigger);
        Assert.Equal(ItemCastSpells.CastTrigger.Swing, casts[1].Trigger);
        // Primary is the on-use cast, not the proc.
        Assert.Equal(114, PrimaryOf(it));
    }

    [Fact]
    public void ProcOnly_Weapon_PrimaryIsTheProc()
    {
        JsonElement it = Item("{\"Abil-0\":114,\"AbilVal-0\":25,\"Abil-1\":43,\"AbilVal-1\":979}");
        Assert.Equal(979, PrimaryOf(it));
    }

    [Fact]
    public void NoCast_ReturnsEmpty()
    {
        JsonElement it = Item("{\"Abil-0\":28,\"AbilVal-0\":1,\"Abil-1\":86,\"AbilVal-1\":50}");
        Assert.Empty(ItemCastSpells.ReadCasts(it));
    }

    [Fact]
    public void ZeroValueCastsSp_Ignored()
    {
        JsonElement it = Item("{\"Abil-0\":43,\"AbilVal-0\":0}");
        Assert.Empty(ItemCastSpells.ReadCasts(it));
    }

    // PrimaryCastSpell over a JsonElement item row (the ReadCasts overload feeds it).
    private static int? PrimaryOf(JsonElement it)
    {
        IReadOnlyList<ItemCastSpells.CastEntry> casts = ItemCastSpells.ReadCasts(it);
        if (casts.Count == 0) return null;
        foreach (ItemCastSpells.CastEntry c in casts)
            if (c.Trigger == ItemCastSpells.CastTrigger.OnUse) return c.SpellNumber;
        return casts[0].SpellNumber;
    }
}
