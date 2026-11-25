using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Conditions;

// Whether a message record's spell could have been cast where the character
// stands: by the room's own spell, by a monster the client has in the room, or,
// for a learned hostile spell, by a player in it outside the party on a PvP
// realm. ConditionTracker asks it of the records one applied line matched, to
// leave out the ones nobody here casts (KeepWhatCouldBeCastHere).
//
// It answers OutOfReach sparingly, because that is the answer that drops a
// condition: only when every caster of every spell the record is for is a monster,
// a room or such a player (SpellCasterIndex), and the client can vouch that none of
// them is here. Anything it can't vouch for is Unknown, and an Unknown record is
// latched as it always was.
public sealed class CastReach
{
    public enum Verdict
    {
        // No spell on the record, a spell the set lacks, a caster that is none of
        // those, or a roster that can't be trusted.
        Unknown,
        InReach,
        OutOfReach,
    }

    // Where the character stands, as far as the client can vouch for it. RoomSpell
    // is the spell of the room, 0 for none. Monsters are the names of the monsters
    // in it; null when the roster is not the whole of who is here. Strangers are the
    // players in it outside the party whose hostile spell can reach the character: none
    // off a PvP realm, null when the roster can't say.
    public readonly record struct Here(
        int RoomSpell, IReadOnlyCollection<string>? Monsters, IReadOnlyCollection<string>? Strangers);

    private readonly SpellCasterIndex _casters;
    private readonly Func<Here?> _here;

    // here: null while the character's room is not confirmed, and then nothing is
    // ruled out at all.
    public CastReach(SpellCasterIndex casters, Func<Here?> here)
    {
        ArgumentNullException.ThrowIfNull(casters);
        ArgumentNullException.ThrowIfNull(here);
        _casters = casters;
        _here = here;
    }

    public Here? Now() => _here();

    // For the bug report: what a shared applied line would be read by right now.
    public static string Describe(Here? here)
    {
        if (here is not { } at) return "position not confirmed: nothing is ruled out";
        static string Names(IReadOnlyCollection<string>? names) =>
            names is null ? "not known" : names.Count == 0 ? "none" : string.Join(", ", names);
        return $"room spell {(at.RoomSpell > 0 ? "#" + at.RoomSpell : "none")}; "
            + $"monsters {Names(at.Monsters)}; players outside the party who can cast at us {Names(at.Strangers)}";
    }

    // by: what casts it here, for the log; null unless InReach.
    public Verdict Of(MessageRecord record, Here here, out string? by)
    {
        by = null;
        if (record.Links is null) return Verdict.Unknown;

        bool anySpell = false;
        bool ruledOut = true;
        foreach (GameDataLink link in record.Links)
        {
            if (!link.Table.Equals("Spells", StringComparison.OrdinalIgnoreCase)) continue;
            anySpell = true;
            if (_casters.Of(link.Number) is not { } casters)
            {
                ruledOut = false;
                continue;
            }
            if (here.RoomSpell > 0 && casters.RoomSpells.Contains(here.RoomSpell))
            {
                by = $"room spell #{here.RoomSpell}";
                return Verdict.InReach;
            }
            if (here.Monsters is { } present)
                foreach (string monster in present)
                    if (casters.Monsters.Contains(monster))
                    {
                        by = $"{monster} here";
                        return Verdict.InReach;
                    }
            if (casters.PlayersAtFoes && here.Strangers is { Count: > 0 } strangers)
            {
                by = $"{strangers.First()} here, a player outside the party";
                return Verdict.InReach;
            }
            // A monster's or a player's spell is ruled out only by a roster that can
            // be trusted; a spell rooms alone cast, by the room.
            if (!(casters.OnlyMonstersAndRooms || casters.PlayersAtFoes)
                || (casters.Monsters.Count > 0 && here.Monsters is null)
                || (casters.PlayersAtFoes && here.Strangers is null))
                ruledOut = false;
        }
        return anySpell && ruledOut ? Verdict.OutOfReach : Verdict.Unknown;
    }

    // The monster on a roster that can be vouched for whom a cast line names, as the
    // line shows it: "fierce dark priest" for the listed dark priest. Null when the
    // roster can't be vouched for or doesn't list it.
    public static string? MonsterNamed(Here here, string shownName)
    {
        if (here.Monsters is not { } present) return null;
        string name = shownName.Trim();
        string? found = null;
        foreach (string monster in present)
            if ((name.Equals(monster, StringComparison.OrdinalIgnoreCase)
                 || name.EndsWith(" " + monster, StringComparison.OrdinalIgnoreCase))
                && (found is null || monster.Length > found.Length))
                found = monster;
        return found;
    }

    // Whether the monster a cast line names casts the record's spell: InReach when it
    // casts one of them, OutOfReach when every spell on the record has known casters
    // and it is none of them. Items, rooms and players don't matter here: the line
    // says the monster cast it.
    public Verdict OfCaster(MessageRecord record, string monster)
    {
        if (record.Links is null) return Verdict.Unknown;
        bool anySpell = false;
        bool unknown = false;
        foreach (GameDataLink link in record.Links)
        {
            if (!link.Table.Equals("Spells", StringComparison.OrdinalIgnoreCase)) continue;
            anySpell = true;
            if (_casters.Of(link.Number) is not { } casters) unknown = true;
            else if (casters.Monsters.Contains(monster)) return Verdict.InReach;
        }
        return anySpell && !unknown ? Verdict.OutOfReach : Verdict.Unknown;
    }

    // Where the character stands, from what the client holds of it; null while the
    // position is not confirmed, and then nothing is ruled out at all. The room's
    // spell is read off the map, so it stands whenever the position does. The
    // monsters and the players stand only when the room can be seen and its roster
    // is whole, but off a PvP realm no player's hostile spell reaches the character.
    public static Here? Standing(
        Map.RoomConfidence confidence, Map.Room? room,
        bool inDarkRoom, bool enteredBlind, bool blinded,
        RoomEntitiesObservation? roster, bool rosterIsWhole, Func<string, bool> isMonsterOfTheSet,
        bool pvpRealm, Func<string, bool> isPartyMember)
    {
        if (confidence != Map.RoomConfidence.Confirmed || room is null) return null;
        bool cannotSee = inDarkRoom || enteredBlind || blinded;
        return new Here(
            room.Spell,
            MonstersSeen(roster, cannotSee, rosterIsWhole, isMonsterOfTheSet),
            pvpRealm ? StrangersSeen(roster, cannotSee, rosterIsWhole, isPartyMember) : Array.Empty<string>());
    }

    // The names of the monsters the roster lists, or null when it can't be taken for
    // everyone in the room: nothing was read (no roster since the game was entered),
    // the room can't be seen (dark, or the character blind), someone may have come
    // in since it was read whom it doesn't name (RoomEntityClassifier.RosterIsWhole),
    // or it lists a name the set has no monster for, which could be anything.
    public static IReadOnlyCollection<string>? MonstersSeen(
        RoomEntitiesObservation? roster, bool cannotSee, bool rosterIsWhole, Func<string, bool> isMonsterOfTheSet)
    {
        ArgumentNullException.ThrowIfNull(isMonsterOfTheSet);
        if (cannotSee || !rosterIsWhole || roster is not { } seen) return null;

        List<string> names = new();
        foreach (RoomEntity entity in seen.Entities)
        {
            if (entity.Kind == EntityKind.Player) continue;
            if (entity.Kind != EntityKind.Monster || !isMonsterOfTheSet(entity.ResolvedName)) return null;
            names.Add(entity.ResolvedName);
        }
        return names;
    }

    // For the bug report: why MonstersSeen and StrangersSeen can't take the roster
    // for everyone in the room, or null when they can. notWholeBecause is the
    // classifier's reason for a roster not read whole. It restates their rules, so a
    // change to MonstersSeen or StrangersSeen is made here too.
    public static string? WhyRosterNotWhole(
        RoomEntitiesObservation? roster, bool inDarkRoom, bool enteredBlind, bool blinded,
        bool rosterIsWhole, string? notWholeBecause, Func<string, bool> isMonsterOfTheSet)
    {
        ArgumentNullException.ThrowIfNull(isMonsterOfTheSet);
        if (inDarkRoom) return "the room is dark";
        if (enteredBlind) return "the room was entered blind";
        if (blinded) return "the character is blind";
        // The classifier's reason first: it says why there is no roster too, such as
        // the game having been left since the last one.
        if (!rosterIsWhole && notWholeBecause is not null) return notWholeBecause;
        if (roster is not { } seen) return "no room list has been read";
        if (!rosterIsWhole) return "not read whole";
        foreach (RoomEntity entity in seen.Entities)
        {
            if (entity.Kind == EntityKind.Unknown) return $"it lists '{entity.ResolvedName}', who can't be placed";
            if (entity.Kind == EntityKind.Monster && !isMonsterOfTheSet(entity.ResolvedName))
                return $"it lists '{entity.ResolvedName}', a monster the game data has none of";
        }
        return null;
    }

    // The names of the players the roster lists who are not in the party, or null
    // when it can't be taken for everyone in the room, as for MonstersSeen. A name
    // nobody can place may be a player's, so it leaves the answer unknown too.
    public static IReadOnlyCollection<string>? StrangersSeen(
        RoomEntitiesObservation? roster, bool cannotSee, bool rosterIsWhole, Func<string, bool> isPartyMember)
    {
        ArgumentNullException.ThrowIfNull(isPartyMember);
        if (cannotSee || !rosterIsWhole || roster is not { } seen) return null;

        List<string> names = new();
        foreach (RoomEntity entity in seen.Entities)
        {
            if (entity.Kind == EntityKind.Unknown) return null;
            if (entity.Kind == EntityKind.Player && !isPartyMember(entity.ResolvedName))
                names.Add(entity.ResolvedName);
        }
        return names;
    }
}
