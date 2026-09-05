using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// Resolves the spell(s) an item casts, from its Abil-N / AbilVal-N slots. A CastsSp
// ability (code 43) names a spell the item delivers; a %Spell (114) or CastOnKill%
// (1114) slot modifies the CastsSp that FOLLOWS it into a per-swing / per-kill combat
// proc, while a bare CastsSp is a command-activated "use <item>" cast. This mirrors
// ItemMdbViewBuilder's cast-row interpretation (the modifier precedes the CastsSp it
// tags) so the two never disagree on what an item casts.
//
// The item's on-use / proc MESSAGE lives on the CAST SPELL's message record (Spells#N),
// not the item — many weapons share one cast spell and therefore one set of messages, so
// editing from any of them edits the same record. PrimaryCastSpell is the spell that
// message anchors to: the command-activated cast if the item has one, else its first proc.
public static class ItemCastSpells
{
    public enum CastTrigger { OnUse, Swing, Kill }

    public readonly record struct CastEntry(int SpellNumber, CastTrigger Trigger, int Percent);

    // Every spell the item casts, in slot order, each tagged with how it fires. Empty when
    // the item isn't in the set or casts nothing. Scans 20 ability slots (the MDB max).
    public static IReadOnlyList<CastEntry> ReadCasts(GameDataCache cache, int itemNumber)
    {
        System.ArgumentNullException.ThrowIfNull(cache);
        if (cache.FindRowByNumber("Items", itemNumber) is not { } row)
            return System.Array.Empty<CastEntry>();
        return ReadCasts(row);
    }

    public static IReadOnlyList<CastEntry> ReadCasts(JsonElement itemRow)
    {
        List<CastEntry>? casts = null;
        int pendingPercent = 0;
        CastTrigger? pendingTrigger = null;
        for (int i = 0; i < 20; i++)
        {
            int code = ReadInt(itemRow, $"Abil-{i}");
            if (code == 0) continue;
            int val = ReadInt(itemRow, $"AbilVal-{i}");

            if (code == 114) { pendingPercent = val; pendingTrigger = CastTrigger.Swing; continue; }
            if (code == 1114) { pendingPercent = val; pendingTrigger = CastTrigger.Kill; continue; }
            if (code == 43 && val > 0)
            {
                (casts ??= new()).Add(new CastEntry(val, pendingTrigger ?? CastTrigger.OnUse, pendingTrigger is null ? 0 : pendingPercent));
                pendingPercent = 0;
                pendingTrigger = null;
            }
        }
        return (IReadOnlyList<CastEntry>?)casts ?? System.Array.Empty<CastEntry>();
    }

    // The spell the item's on-use / proc MESSAGE anchors to: the command-activated cast
    // (bare CastsSp) when the item has one, else its first combat proc. Null when the item
    // casts nothing — such an item's message (if any) is a plain wield/remove event that
    // stays anchored to the item itself, not a spell.
    public static int? PrimaryCastSpell(GameDataCache cache, int itemNumber)
    {
        IReadOnlyList<CastEntry> casts = ReadCasts(cache, itemNumber);
        if (casts.Count == 0) return null;
        foreach (CastEntry c in casts)
            if (c.Trigger == CastTrigger.OnUse) return c.SpellNumber;
        return casts[0].SpellNumber;
    }

    private static int ReadInt(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }
}
