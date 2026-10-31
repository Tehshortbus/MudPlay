using System.Text.Json;

namespace MudPlay.Game;

// The server's vitals and stats (Anomaly.Char.Vitals / Anomaly.Char.Stats) written
// to the fields the stat screen's read writes.
public sealed partial class StatParser
{
    private static readonly (string Key, StatSet Flag)[] ServerStats =
    [
        ("strength", StatSet.Strength), ("intellect", StatSet.Intellect), ("willpower", StatSet.Willpower),
        ("agility", StatSet.Agility), ("health", StatSet.Health), ("charm", StatSet.Charm),
    ];

    // expGainCounts: these vitals follow others of the same session, so a rise in exp
    // is a gain earned, as a "You gain N experience." line is on the text path (whose
    // handler stands down while the server owns the vitals). Level-up announcements
    // ride on it, and went quiet under the server's vitals (export
    // gmcp-20261029-152755: crossing the level line, the game prints only the gain
    // line). A session's first vitals can carry exp earned elsewhere, or another
    // character's, so it re-anchors without counting, as a stat screen does.
    public void ApplyGmcpVitals(JsonElement value, bool expGainCounts = false)
    {
        Stats.Hits = value.GetProperty("hp").GetInt32();
        Stats.MaxHits = value.GetProperty("maxhp").GetInt32();
        bool kai = value.GetProperty("manaType").GetString() == "kai";
        int mana = value.GetProperty("mana").GetInt32();
        int max = value.GetProperty("maxmana").GetInt32();
        Stats.Mana = kai ? 0 : mana;
        Stats.MaxMana = kai ? 0 : max;
        Stats.Kai = kai ? mana : 0;
        Stats.MaxKai = kai ? max : 0;
        int level = value.GetProperty("level").GetInt32();
        // A new level's span isn't known until the exp screen gives it.
        if (Stats.Level != level)
        {
            Stats.LevelExpSpan = 0;
            Stats.LevelPercent = 0;
        }
        Stats.Level = level;
        long exp = value.GetProperty("exp").GetInt64();
        long gained = exp - Stats.Exp;
        Stats.Exp = exp;
        Stats.ExpToNext = Math.Max(0, value.GetProperty("expNeeded").GetInt64() - Stats.Exp);
        HasParsed = true;
        _serverVitalsRead = true;
        if (expGainCounts && gained > 0) ExperienceGained?.Invoke(Stats.Exp);
    }

    public void ApplyGmcpStats(JsonElement value)
    {
        JsonElement stats = value.GetProperty("stats");
        int Current(string key) => stats.GetProperty(key).GetProperty("current").GetInt32();
        Stats.Strength = Current("strength");
        Stats.Intellect = Current("intellect");
        Stats.Willpower = Current("willpower");
        Stats.Agility = Current("agility");
        Stats.Health = Current("health");
        Stats.Charm = Current("charm");
        // A stat off its base is the one the stat screen would mark modified. Set in one
        // write: cleared first, the gear-raised stat read for a moment as trained at its
        // raised value, and the CP plan took that as a trusted reading (report
        // paradigm-20261031-101847).
        StatSet modified = StatSet.None;
        foreach ((string key, StatSet flag) in ServerStats)
            if (stats.GetProperty(key).GetProperty("base").GetInt32() != Current(key)) modified |= flag;
        Stats.ModifiedStats = modified;
        Stats.ModifiedMarksRead = true;
        Stats.ArmourClass = value.GetProperty("ac").GetProperty("shown").GetInt32();
        Stats.MaxArmourClass = value.GetProperty("dr").GetProperty("shown").GetInt32();
        Stats.Perception = value.GetProperty("perception").GetInt32();
        Stats.Stealth = value.GetProperty("stealth").GetInt32();
        Stats.Thievery = value.GetProperty("thievery").GetInt32();
        Stats.Traps = value.GetProperty("traps").GetInt32();
        Stats.Picklocks = value.GetProperty("picklocks").GetInt32();
        Stats.Tracking = value.GetProperty("tracking").GetInt32();
        Stats.MartialArts = value.GetProperty("martialArts").GetInt32();
        Stats.MagicRes = value.GetProperty("magicResist").GetInt32();
        Stats.Spellcasting = value.GetProperty("spellcasting").GetInt32();
        HasParsed = true;
    }
}
