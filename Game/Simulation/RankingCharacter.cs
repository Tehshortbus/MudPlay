using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MudPlay.Game.Simulation;

// The character a ranking was made for, as the simulator read it: one short hash
// for each group of inputs, so a ranking listed again later can say which groups
// are no longer what they were. A ranking whose character has changed is still a
// ranking of that character, of an earlier state of it: it is listed, marked.
//
// Three groups come from the SimCharacter the simulation played, so they cover what
// the simulator reads and nothing it doesn't (encumbrance, for one, only through
// what it does to the swings):
//   stats or gear     level, pools, the melee and defence profiles, regen, alignment,
//                     the Default-gear basis and the backstab opener
//   spells            every spell known, with its figures at that level
//   settings          the Combat, Health and Spells sections, the buff list, and
//                     the hang-up switch
// Two are handed in, being about the game data and the map and not the character
// record:
//   game-data edits   the user's record overrides at every tier (Game Data Browser)
//   reach             what decides which areas can be walked to, as far as it holds
//                     still: avoided and blacklisted rooms, class and race, gate
//                     items carried (RankingReach)
// The purse and the walk pace are left out: they move with every kill and every
// item picked up, and a ranking marked for them would always be marked.
//
// The user's own loops in a ranking are compared too, but apart from these, loop by
// loop (RankingLoops): they are not the character's, and the check names them.
public sealed record RankingCharacter(IReadOnlyDictionary<string, string> Groups)
{
    public const string StatsOrGear = "stats or gear";
    public const string Spells = "spells";
    public const string Settings = "settings";
    public const string GameDataEdits = "game-data edits";
    public const string Reach = "reach";

    // The order groups are named in.
    public static readonly IReadOnlyList<string> Order = new[] { StatsOrGear, Spells, Settings, GameDataEdits, Reach };

    // The groups that are not what they were when made was taken. A group either
    // side lacks counts as changed: in doubt, the ranking is marked.
    public IReadOnlyList<string> ChangedSince(RankingCharacter made)
    {
        ArgumentNullException.ThrowIfNull(made);
        return Order
            .Where(g => !Groups.TryGetValue(g, out string? now) || !made.Groups.TryGetValue(g, out string? then)
                        || !string.Equals(now, then, StringComparison.Ordinal))
            .ToList();
    }

    public static RankingCharacter Of(SimCharacter c, string gameDataEdits, string reach)
    {
        ArgumentNullException.ThrowIfNull(c);
        SimRegen r = c.Regen;
        return new RankingCharacter(new Dictionary<string, string>
        {
            [StatsOrGear] = HashOf(new
            {
                c.Realm, c.Level, c.MaxHp, c.MaxMana, c.Melee, c.WeaponHitMagic, c.Defense,
                // The regen is functions of the percent a buff adds: sampled at none
                // and at a hundred, which fixes a straight line.
                Regen = new[]
                {
                    r.HpStanding(0), r.HpStanding(100), r.HpResting(0), r.HpResting(100),
                    r.MaStanding(0), r.MaStanding(100), r.MaMeditating,
                    r.HpRestingLow?.Invoke(0) ?? -1, r.HpRestingLow?.Invoke(100) ?? -1,
                    r.HpLowExtra?.Invoke(0) ?? -1, r.HpLowExtra?.Invoke(100) ?? -1,
                },
                r.Cadence, r.RestReplacesStanding, r.RestRun,
                c.AlignmentValue, c.DefaultMaxHp, c.DefaultMaxMana, c.Backstab, c.BackstabHitMagic,
            }),
            [Spells] = HashOf(c.Spells.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s => s.Value).ToList()),
            [Settings] = HashOf(new { c.Combat, c.Health, c.SpellSlots, c.Buffs, c.HangupsDisabled }),
            [GameDataEdits] = gameDataEdits,
            [Reach] = reach,
        });
    }

    private static readonly JsonSerializerOptions Form = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        IncludeFields = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { LeaveOutWhatTheSimulationDoesNotRead } },
    };

    // A setting the simulation never reads is left out of the hash, so changing it
    // doesn't mark every kept ranking as made for another character. An attack
    // slot's "fire regardless of mana" acts only in a party, and the simulated
    // character is always solo. The cast-when-full slots aren't played at all, and
    // which combat lines are hidden is a matter of display.
    private static void LeaveOutWhatTheSimulationDoesNotRead(JsonTypeInfo info)
    {
        string[] unread;
        if (info.Type == typeof(Models.Profile.CombatSpellSlot))
            unread = [nameof(Models.Profile.CombatSpellSlot.FireRegardlessOfMana)];
        else if (info.Type == typeof(Models.Profile.CombatSettings))
            unread = [nameof(Models.Profile.CombatSettings.HiddenCombatLines)];
        else if (info.Type == typeof(Models.Profile.SpellsSettings))
            unread =
            [
                nameof(Models.Profile.SpellsSettings.CastWhenHpFullSpell),
                nameof(Models.Profile.SpellsSettings.CastWhenMaFullSpell),
                nameof(Models.Profile.SpellsSettings.CastWhenHpFullTarget),
                nameof(Models.Profile.SpellsSettings.CastWhenMaFullTarget),
                nameof(Models.Profile.SpellsSettings.CastWhenHpFullWhileResting),
                nameof(Models.Profile.SpellsSettings.CastWhenMaFullWhileResting),
            ];
        else return;
        for (int i = info.Properties.Count - 1; i >= 0; i--)
            if (unread.Contains(info.Properties[i].Name))
                info.Properties.RemoveAt(i);
    }

    // A short hash of value's JSON form: enough to tell two states apart, not a
    // record of either.
    public static string HashOf<T>(T value) => HashBytes(JsonSerializer.SerializeToUtf8Bytes(value, Form));

    // The same of a text as it stands (a file's contents).
    public static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text));

    private static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
}
