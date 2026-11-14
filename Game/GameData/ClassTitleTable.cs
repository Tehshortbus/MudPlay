using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// MajorMUD class titles by level: the lookup behind "infer class + level from who
// output".
//
// A set exported with a Titles table carries its realm's own titles, and those are
// used. Stock's titles part from Paradigm's above level 60 or so, and a realm with
// classes of its own (MudRev has 25) has titles no built-in list could know. A set
// imported from an older export has no such table and gets the built-in list,
// Paradigm 1.9.1's titles, written in the table's own form.
//
// Gender-split titles are "Male|Female"; either side matches. A shared title
// ("Apprentice", every class at level 1) returns several classes from LookupClasses,
// and the caller chooses between "Multiple" / "Inferred" / "(needs @health)".
public sealed partial class ClassTitleTable
{
    // Class name -> its title at each level (index 0 = level 1); null where none is given.
    private readonly Dictionary<string, string?[]> _titles;

    private ClassTitleTable(Dictionary<string, string?[]> titles, bool fromSet)
    {
        _titles = titles;
        FromSet = fromSet;
    }

    // Built on first use: the list it reads is declared below, after this.
    public static ClassTitleTable BuiltIn => _builtIn ??= new(Parse(BuiltInTitles), fromSet: false);
    private static ClassTitleTable? _builtIn;

    // The titles are the set's own Titles table, not the built-in list.
    public bool FromSet { get; }

    // Total number of distinct classes in the table: drives "universally shared
    // title" detection in callers.
    public int ClassCount => _titles.Count;

    // The set's Titles table, its Class numbers named by its Classes table. The
    // built-in list when the set has no Titles table or none of its rows reads.
    public static ClassTitleTable ForSet(GameTable? titles, GameTable? classes)
    {
        if (titles is null || classes is null) return BuiltIn;
        Dictionary<int, string> names = new();
        foreach (GameRow row in classes.Rows)
            if (Int(row, "Number") is > 0 and int number && Text(row, "Name") is { } name)
                names[number] = name;
        Dictionary<string, string> specs = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameRow row in titles.Rows)
            if (names.TryGetValue(Int(row, "Class"), out string? name) && Text(row, "Titles") is { } spec)
                specs[name] = spec;
        Dictionary<string, string?[]> parsed = Parse(specs);
        return parsed.Count == 0 ? BuiltIn : new ClassTitleTable(parsed, fromSet: true);
    }

    // For the bug report: where the titles come from.
    public string Describe() => FromSet
        ? $"the set's Titles table ({ClassCount} classes)"
        : "built-in (Paradigm 1.9.1's titles; the set has no Titles table)";

    // Range of levels that share title, across all matching classes. null when no
    // class matches.
    public (int MinLevel, int MaxLevel)? LookupLevelRange(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        int min = int.MaxValue;
        int max = -1;
        foreach (string?[] titles in _titles.Values)
        {
            for (int i = 0; i < titles.Length; i++)
            {
                if (titles[i] is { } entry && MatchesTitle(entry, title))
                {
                    int level = i + 1;
                    if (level < min) min = level;
                    if (level > max) max = level;
                }
            }
        }
        return max < 0 ? null : (min, max);
    }

    // Classes whose title list contains title. Empty when no class matches.
    public IReadOnlyList<string> LookupClasses(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<string>();
        List<string> hits = new();
        foreach ((string cls, string?[] titles) in _titles)
        {
            foreach (string? entry in titles)
            {
                if (entry is not null && MatchesTitle(entry, title))
                {
                    hits.Add(cls);
                    break;
                }
            }
        }
        return hits;
    }

    // True when entry (one level's title) matches title on either gender split.
    private static bool MatchesTitle(string entry, string title)
    {
        int pipe = entry.IndexOf('|');
        if (pipe < 0)
            return string.Equals(entry, title, StringComparison.OrdinalIgnoreCase);
        string male   = entry[..pipe];
        string female = entry[(pipe + 1)..];
        return string.Equals(male,   title, StringComparison.OrdinalIgnoreCase)
            || string.Equals(female, title, StringComparison.OrdinalIgnoreCase);
    }

    // Format a level range as "7" when a single level or "7-10" when a band.
    public static string FormatLevelRange((int Min, int Max) range)
        => range.Min == range.Max ? range.Min.ToString() : $"{range.Min}-{range.Max}";

    // "1-1:Apprentice,2-4:Warrior Novice,...,45-49:Lord|Lady" -> a title per level. A
    // piece with no level range ahead of it is a comma inside the title before it.
    private static Dictionary<string, string?[]> Parse(IReadOnlyDictionary<string, string> specs)
    {
        Dictionary<string, string?[]> parsed = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string cls, string spec) in specs)
        {
            List<(int Low, int High, string Title)> bands = [];
            foreach (string piece in spec.Split(','))
            {
                Match band = BandPattern().Match(piece);
                if (band.Success)
                {
                    int low = int.Parse(band.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    int high = int.Parse(band.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    string title = band.Groups[3].Value.Trim();
                    if (low >= 1 && high >= low && high <= MaxLevel && title.Length > 0)
                        bands.Add((low, high, title));
                }
                else if (bands.Count > 0)
                    bands[^1] = bands[^1] with { Title = bands[^1].Title + "," + piece };
            }
            if (bands.Count == 0) continue;
            string?[] levels = new string?[bands.Max(b => b.High)];
            foreach ((int low, int high, string title) in bands)
                for (int level = low; level <= high; level++)
                    levels[level - 1] = title;
            parsed[cls] = levels;
        }
        return parsed;
    }

    // Higher than any realm's top level; a band past it is a damaged cell, not a title.
    private const int MaxLevel = 1000;

    [GeneratedRegex(@"^\s*(\d+)\s*-\s*(\d+)\s*:(.*)$")]
    private static partial Regex BandPattern();

    // A text cell; a blank one reads as none. The export's empty value, a lone NUL,
    // holds no level range, so Parse finds no title in it.
    private static string? Text(GameRow row, string field) =>
        row.TryGetProperty(field, out GameValue value) && value.ValueKind == JsonValueKind.String
            && value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;

    private static int Int(GameRow row, string field) =>
        row.TryGetProperty(field, out GameValue value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            ? number
            : 0;

    // Paradigm 1.9.1's titles, as its Titles table gives them.
    private static readonly Dictionary<string, string> BuiltInTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Warrior"] = "1-1:Apprentice,2-4:Warrior Novice,5-9:Grunt,10-14:Fighter,15-19:Veteran,20-24:Mercenary,25-29:Duelist,30-34:Dragoon,35-39:Gladiator,40-44:Myrmidon,45-49:Lord|Lady,50-54:Hero|Heroine,55-59:Weaponmaster|Weaponmistress,60-64:Warmonger,65-69:Warlord|Warlady,70-74:Dragonslayer,75-79:High Warlord,80-84:Juggernaut,85-89:Conqueror,90-94:Dread Juggernaut,95-99:Unstoppable Force,100-109:Overlord",
        ["Witchunter"] = "1-1:Apprentice,2-4:Witchunter Novice,5-9:Persecutor,10-14:Magehunter,15-19:Magebane,20-24:Spellbreaker,25-29:Enforcer,30-34:Disenchanter,35-39:Eradicator,40-44:Demonhunter,45-49:Mageslayer,50-54:Annihilator,55-59:Inquisitor,60-64:High Inquisitor,65-69:Master Confessor,70-74:Demonbane,75-79:Witchflayer,80-84:Dread Confessor,85-89:Iconoclast,90-94:Vampire Hunter,95-99:God Hunter,100-105:Deicide",
        ["Paladin"] = "1-1:Apprentice,2-4:Paladin Novice,5-9:Knave,10-14:Squire,15-19:Gallant,20-24:Defender,25-29:Cavalier,30-34:Avenger,35-39:Knight,40-44:Crusader,45-49:Templar,50-54:Champion,55-59:First Knight,60-64:Lord Justice|Lady Justice,65-69:Supreme Justice,70-74:Grand Exemplar,75-79:Sword of Justice,80-84:High Justicar,85-89:Grand Crusader,90-94:Divine Champion,95-99:Praetor,100-110:Sword of the Divine",
        ["Cleric"] = "1-1:Apprentice,2-4:Cleric Novice,5-9:Auxiliary,10-14:Venerator,15-19:Acolyte,20-24:Fighter Priest|Fighter Priestess,25-29:Canon,30-34:Warrior Priest|Warrior Priestess,35-39:Guardian,40-44:Chaplain,45-49:Vicar,50-54:Chancellor,55-59:Rector,60-64:High Cleric,65-69:Divine Protector,70-74:Exalted Shield,75-79:Bastion,80-84:The Immovable,85-89:Eternal Bulwark,90-94:Divine Aegis,95-99:Shield of the Faith,100-109:Will of the Divine",
        ["Priest"] = "1-1:Apprentice,2-4:Priest Novice,5-9:Clergyman|Clergywoman,10-14:Curate,15-19:Pastor,20-24:Reverend,25-29:Parson,30-34:Minister,35-39:Cardinal,40-44:Pontiff,45-49:Bishop,50-54:High Priest|High Priestess,55-59:Archbishop,60-64:Chosen,65-69:Prophet,70-74:High Ecclesiast,75-79:Presbyter,80-84:High Pontiff,85-89:Divine Prophet,90-94:Voice of God,95-99:Demigod,100-110:Archdeity",
        ["Missionary"] = "1-1:Apprentice,2-4:Missionary Novice,5-9:Initiate,10-14:Witness,15-19:Rogue Priest|Rogue Priestess,20-24:Converter,25-29:Infiltrator,30-34:Oracle,35-39:Evangelist,40-44:Diviner,45-49:Faithbringer,50-54:Zealot,55-59:Divine Messenger,60-64:Apostle,65-69:Revelator,70-74:Divine Emissary,75-79:The Glorified,80-84:Righteous Messenger,85-89:Envoy of the Master,90-94:Hand of the Faith,95-99:Archon,100-117:Archangel",
        ["Ninja"] = "1-1:Apprentice,2-4:Ninja Novice,5-9:Menace,10-14:Cutthroat,15-19:Stalker,20-24:Killer,25-29:Nightstalker,30-34:Murderer,35-39:Manhunter,40-44:Nightblade,45-49:Assassin,50-54:Executioner,55-59:Revenant,60-64:Master Assassin,65-69:Shadow Master|Shadow Mistress,70-74:Hand of the Unseen,75-79:Shadow's Embrace,80-84:Whisper of Death,85-89:Death's Hand,90-94:Death Incarnate,95-99:Shadowlord,100-110:Shinobi",
        ["Thief"] = "1-1:Apprentice,2-4:Thief Novice,5-9:Rascal,10-14:Footpad,15-19:Pilferer,20-24:Pickpocket,25-29:Cutpurse,30-34:Bandit,35-39:Burglar,40-44:Rogue,45-49:Sharper,50-54:Magsman,55-59:Master Rogue,60-64:Rogue Prince|Rogue Princess,65-69:Underlord,70-74:The Hand,75-79:Deceiver,80-84:Pillager,85-89:Mastermind,90-94:Con Artist,95-99:Crime Lord,100-111:Kingpin",
        ["Bard"] = "1-1:Apprentice,2-4:Bard Novice,5-9:Jester,10-14:Lyricist,15-19:Entertainer,20-24:Sonnateer,25-29:Skald,30-34:Troubadour,35-39:Musician,40-44:Minstrel,45-49:Swashbuckler,50-54:Songweaver,55-59:Chanteur|Chanteuse,60-64:Virtuoso,65-69:Artiste,70-74:Maestro,75-79:Celebrity,80-84:Royal Entertainer,85-89:High Spellsong,90-94:Prodigy,95-99:Famous Dandy,100-110:Playboy|Playgirl",
        ["Gypsy"] = "1-1:Apprentice,2-4:Gypsy Novice,5-9:Scallywag,10-14:Trickster,15-19:Charlatan|Vixen,20-24:Traveler,25-29:Wanderer,30-34:Wayfarer,35-39:Nomad,40-44:Voyager,45-49:Arbiter,50-54:Visionary,55-59:Seer|Seeress,60-64:Gypsy Prince|Gypsy Princess,65-69:Lord of Fortune|Lady of Fortune,70-74:Fortune's Hand,75-79:Foreteller,80-84:Vagabond Prince|Vagabond Princess,85-89:High Artificier,90-94:Vagrant Lord|Vagrant Lady,95-99:Master of Fate|Mistress of Fate,100-114:Herald of Woe",
        ["Warlock"] = "1-1:Apprentice,2-4:Warlock Novice,5-9:Dabbler,10-14:Spellslinger,15-19:Occultist,20-24:Cabalist,25-29:Warrior Mage,30-34:Erudite,35-39:Rubicant,40-44:Evoker,45-49:Diabolist,50-54:Spellbinder,55-59:Swordsmage,60-64:Battlemage,65-69:Warmage,70-74:Witchknight,75-79:Djinn,80-84:Runewielder,85-89:Eldritch Knight,90-94:High Battlemage,95-99:Runic General,100-110:Supremus",
        ["Mage"] = "1-1:Apprentice,2-4:Mage Novice,5-9:Adept,10-14:Prestidigator,15-19:Illusionist,20-24:Theurgist,25-29:Conjurer,30-34:Magician,35-39:Sorcerer,40-44:Arcanist,45-49:Magus,50-54:Wizard,55-59:High Mage,60-64:Archmage,65-69:Lord Magus|Lady Magus,70-74:Timebender,75-79:Elder Wizard,80-84:Grand Magus,85-89:Supreme Archmagi,90-94:Eldritch Lord|Eldritch Lady,95-99:Arcane Conduit,100-109:Voice of the Aether",
        ["Druid"] = "1-1:Apprentice,2-4:Druid Novice,5-9:Naturalist,10-14:Cultivator,15-19:Herbalist,20-24:Elementalist,25-29:Nature's Servant,30-34:Sage,35-39:Savant,40-44:Shaman|Shamaness,45-49:Astromancer,50-54:High Druid,55-59:Archdruid,60-64:Woodland Protector,65-69:Lord of Nature|Lady of Nature,70-74:Nature's Spirit,75-79:Woodland Lord|Woodland Lady,80-84:High Shaman,85-89:Heart of the Wild,90-94:Omnimancer,95-99:Gaia's Chosen,100-105:One with the Cosmos",
        ["Ranger"] = "1-1:Apprentice,2-4:Ranger Novice,5-9:Strider,10-14:Excursionist,15-19:Scout,20-24:Explorer,25-29:Guide,30-34:Woodsman|Woodswoman,35-39:Courser,40-44:Tracker,45-49:Pathfinder,50-54:Hunter|Huntress,55-59:Ranger Lord|Ranger Lady,60-64:Master Hunter|Master Huntress,65-69:Lord of the Hunt|Lady of the Hunt,70-74:Nature's Fury,75-79:Predator,80-84:Dead Eye,85-89:Ranger General,90-94:Force of Nature,95-99:Spear of the Wild,100-118:Primal Avatar",
        ["Mystic"] = "1-1:Apprentice,2-4:Mystic Novice,5-9:Student,10-14:Disciple,15-19:Seeker,20-24:Monk,25-29:Kai Warrior,30-34:Monk Lord|Monk Lady,35-39:Maharishi,40-44:Sensei,45-49:Guru,50-54:Lama,55-59:Master of the Way|Mistress of the Way,60-64:Kai Lord|Kai Lady,65-69:Kai Master|Kai Mistress,70-74:Yogi,75-79:Miyagi,80-84:Kai Weaver,85-89:Supreme Kai,90-94:The Transcendent,95-99:Essence of Kai,100-104:The Ascended",
    };
}
