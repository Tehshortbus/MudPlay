using System.Text;

namespace MudPlay.Services;

// How a status-bar item draws. Every kind also has a plain-text reading, which a
// custom text shows when it names the item in braces.
public enum StatusBarItemKind
{
    Text,
    EngineChip,
    StatlineWarning,
    ConnectionLight,
    CustomText,
}

// One thing the status bar can show. Sample is what the Settings preview shows when
// the live value is empty (nothing loaded, not connected).
public sealed record StatusBarItemDef(
    string Id, string Group, string Label, string Description, string Sample,
    StatusBarItemKind Kind = StatusBarItemKind.Text, bool Mono = false);

// Everything the status bar can show, in the order the Settings picker lists it.
// Ids are saved in the global settings and typed into custom texts — never rename
// one.
public static class StatusBarItemCatalogue
{
    public const string CustomTextId = "text";

    private const string Bar = "Standard bar", Who = "Character", Vitals = "Vitals",
        Where = "Location and movement", Fight = "Combat", Session = "Session stats", Other = "Other";

    public static IReadOnlyList<StatusBarItemDef> All { get; } = new StatusBarItemDef[]
    {
        new("engine", Bar, "Engine state chip", "IDLE / WALK / LOOP / LAIR, coloured, with the recovery-tier border.", "LOOP", StatusBarItemKind.EngineChip),
        new("location", Bar, "Location", "Your map/room; the walk readout during a walk-to; the lap and the step within it during a loop.", "1/297"),
        new("exprate", Bar, "Exp rate", "This session's exp per hour.", "1.2M/hr"),
        new("tnl", Bar, "Time to next level", "How long until the next level at this rate, and how many levels you have banked.", "TNL: 42m (+0.35 lvls)"),
        new("target", Bar, "Looked-at target HP", "The HP range of the monster you last looked at. Empty until you look.", "TGT HP: wounded [~340]", Mono: true),
        new("statline", Bar, "Statline warning", "Shows only while the game's prompt doesn't match Settings → Statline. Click opens that tab.", "STATLINE MISMATCH", StatusBarItemKind.StatlineWarning),
        new("tick", Bar, "Combat tick", "Countdown to the next combat round.", "Tick 2.4", Mono: true),
        new("hptick", Bar, "HP tick", "Countdown to the next HP regen tick (and the resting one while resting).", "HP 12.0", Mono: true),
        new("matick", Bar, "Mana tick", "Countdown to the next mana regen tick (and the meditating one while meditating).", "MA 8.5", Mono: true),
        new("connection", Bar, "Connection light", "Red idle, yellow connecting, green connected, with the reconnect countdown.", "Connected", StatusBarItemKind.ConnectionLight),

        new("profile", Who, "Profile name", "The loaded character profile.", "Cidir"),
        new("name", Who, "Character name", "Your character's name as the game reports it.", "Cidir Stormcrow"),
        new("level", Who, "Level", "Your trained level.", "Lvl 35"),
        new("class", Who, "Race and class", "Your race and class.", "Half-Elf Mystic"),
        new("lives", Who, "Lives", "Lives remaining.", "Lives 9"),
        new("bbs", Who, "BBS", "The BBS (and realm) this character plays on.", "ParadigmBBS:Paradigm"),
        new("gamedata", Who, "Game data set", "The active game data set.", "Paradigm"),
        new("combatprofile", Who, "Combat profile (number and name)", "The active combat spell profile: its number and name.", "P1 Default"),
        new("combatprofilenumber", Who, "Combat profile (number)", "The active combat spell profile's number only.", "P1"),
        new("combatprofilename", Who, "Combat profile (name)", "The active combat spell profile's name only.", "Default"),

        new("gearset", Who, "Gear set", "The equipment set last put on; empty until one is applied this session.", "Gear: Default"),

        new("hp", Vitals, "HP", "Current and maximum HP.", "HP 182/240"),
        new("hppct", Vitals, "HP percent", "HP as a percentage.", "HP 76%"),
        new("mana", Vitals, "Mana", "Current and maximum mana (or kai).", "MA 61/88"),
        new("manapct", Vitals, "Mana percent", "Mana as a percentage.", "MA 69%"),
        new("hpregen", Vitals, "HP regen (last tick)", "What the last HP regen tick gave, with its posture when that isn't yours now. A dash after two minutes without one.", "HP +7", Mono: true),
        new("hpregenexp", Vitals, "HP regen (expected)", "What your level, Health, gear, race, class and quests say an HP regen tick gives in your posture. Buffs aren't counted.", "HP +6/tick", Mono: true),
        new("maregen", Vitals, "Mana regen (last tick)", "What the last mana (or kai) regen tick gave, with its posture when that isn't yours now. A dash after two minutes without one, which in a fight is usual: a tick mixed with casts can't be told apart.", "MA +31", Mono: true),
        new("maregenexp", Vitals, "Mana regen (expected)", "What your stats, gear and mana-regen roll say a mana (or kai) tick gives in your posture; a range while the roll isn't read.", "MA +29/tick", Mono: true),
        new("position", Vitals, "Posture", "Resting or meditating; empty while standing.", "Resting"),
        new("stealth", Vitals, "Stealth", "Sneaking or hidden; empty otherwise.", "Sneaking"),
        new("encumbrance", Vitals, "Encumbrance", "How loaded you are: the word, the weight carried out of your limit, and the percentage.", "Medium 1420/2400 (59%)"),

        new("roomkey", Where, "Map / room number", "The room you are in, as map/room.", "1/297"),
        new("room", Where, "Room name", "The name of the room you are in.", "Town Square"),
        new("loop", Where, "Loop name", "The running loop; empty when none.", "Sewer circuit"),
        new("lap", Where, "Lap", "The lap of the running loop; empty when none.", "Lap 12"),
        new("loopstep", Where, "Loop step", "Which step of its circle the running loop is on; empty when none.", "Step 36 of 60"),
        new("destination", Where, "Walk destination", "Where a walk-to is headed; empty when none.", "To 1/1376"),

        new("fighting", Fight, "Combat target", "The monster you are attacking; empty out of combat.", "orc chieftain"),
        new("expneeded", Fight, "Exp to next level", "Exp still needed for the next level.", "Needs 2.4M"),
        new("party", Fight, "Party", "Party size and leader; empty when solo.", "Party 4 · Tank leads"),
        new("hitpct", Fight, "Hit rate", "The share of your swings that landed this session.", "Hit 88%"),
        new("critpct", Fight, "Crit rate", "The share of your landed swings that were criticals.", "Crit 12%"),
        new("backstabpct", Fight, "Backstab rate", "The share of your backstabs that landed; empty until you try one.", "BS 71%"),
        new("avghit", Fight, "Average hit", "Your average damage per landed swing.", "Avg hit 41"),
        new("avground", Fight, "Average round", "Your average damage in a round that did damage.", "Round 132"),
        new("roundstokill", Fight, "Rounds to kill", "The average number of combat rounds a monster took to die this session.", "1.5 rds/kill"),
        new("dodgepct", Fight, "Dodge rate", "The share of attacks on you that you dodged.", "Dodge 23%"),
        new("hittakenpct", Fight, "Hit-taken rate", "The share of attacks on you that landed.", "Hit by 41%"),

        new("sessiontime", Session, "Time online", "How long this session has been connected.", "3:14:07"),
        new("sessionexp", Session, "Exp earned", "Exp earned this session.", "Exp 5.7M"),
        new("kills", Session, "Kills", "Monsters killed this session.", "Kills 412"),
        new("killrate", Session, "Kills per hour", "This session's kill rate.", "128 kills/hr"),
        new("sessioncash", Session, "Cash collected", "The worth of the coins picked up this session.", "Got 8.7 platinum"),
        new("cashrate", Session, "Cash per hour", "The worth of the coins picked up per hour.", "2.7 platinum/hr"),
        new("itemsgot", Session, "Items collected", "Items picked up this session.", "Items 34"),
        new("itemssold", Session, "Items sold", "Items sold this session.", "Sold 21"),
        new("steps", Session, "Steps walked", "Rooms walked this session.", "Steps 1204"),
        new("steptime", Session, "Average step time", "The average time from sending a move to arriving.", "Step 0.62s"),
        new("sneakpct", Session, "Sneak success", "The share of sneaking room entries where the sneak held.", "Sneak 92%"),

        new("auto", Other, "Auto engines", "Which automatic engines are on.", "Auto: Combat Nuke Heal Rest Bless"),
        new("nextevent", Other, "Next event", "The Event due to fire soonest, and how long until it does; empty when none is counting down.", "Next: Bank run in 12m"),
        new("cash", Other, "Cash carried", "The coins you are carrying.", "Cash 3p 40g"),
        new("clock", Other, "Clock", "Your computer's time.", "21:42"),
        new(CustomTextId, Other, "Custom text…", "Your own text. Put another item's name in braces to show its value, for example: Lap {lap} of {loop}.", "Your text", StatusBarItemKind.CustomText),
    };

    // The catalogue as the Add menu shows it: one fly-out per group, in order.
    public static IReadOnlyList<IGrouping<string, StatusBarItemDef>> Groups { get; } =
        All.GroupBy(d => d.Group).ToList();

    // The names a custom text can put in braces, as one line.
    public static string TokenNames { get; } = string.Join("  ",
        All.Where(d => d.Kind != StatusBarItemKind.CustomText).Select(d => "{" + d.Id + "}"));

    public static StatusBarItemDef? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    // A custom text with each {name} replaced by that item's value. A name that
    // isn't an item is left as typed, so a typo is visible rather than swallowed.
    public static string ExpandTokens(string? template, Func<string, string> valueOf)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        StringBuilder text = new(template.Length + 32);
        int at = 0;
        while (at < template.Length)
        {
            int open = template.IndexOf('{', at);
            int close = open < 0 ? -1 : template.IndexOf('}', open + 1);
            if (close < 0)
            {
                text.Append(template, at, template.Length - at);
                break;
            }
            text.Append(template, at, open - at);
            string name = template[(open + 1)..close];
            if (Find(name) is { Kind: not StatusBarItemKind.CustomText } def) text.Append(valueOf(def.Id));
            else text.Append(template, open, close - open + 1);
            at = close + 1;
        }
        return text.ToString();
    }
}
