using System.Collections.Generic;

namespace FujinTerm.Game.Combat;

// One identity in a MonsterDeathEvent.Candidates list. Some monsters share the
// same death-line wording in stock MajorMUD data (e.g. multiple variant rats may
// use the same "falls to the ground" message), so a single observed line can map
// to several possible MonsterMessageRecords.
//
// Number is the MonsterRecord.Number when the message record carries a
// Monsters-table link; null when the record exists but isn't linked
// (user-curated entry that hasn't been bound to a specific monster row). Name is
// the monster's display name from the MonsterMessageRecord.Name field.
public readonly record struct MonsterDeathIdentity(int? Number, string Name);

// Payload of MonsterDeathWatcher.MonsterDied — fired either when a specific
// death-line pattern matches (strong signal, Candidates populated, IsFallback
// false) or when the fallback "experience gain + *Combat Off*" sequence is
// observed without a matching specific pattern (weak signal, Candidates empty,
// IsFallback true — consumers use their own state, typically
// CombatManager.CurrentTarget, to attribute the death).
//
// Candidates is all monsters whose DeathLine list contained the observed line;
// multiple entries when the pattern is shared across variants, empty for the
// fallback path. ExperienceGained is the number captured by the most recent
// "You gain N experience." line that landed before the death/combat-off, or null
// when no exp line was observed in the window. At is the wall-clock time the
// event fired. IsFallback is true for the exp + Combat Off path, false when a
// specific death pattern matched.
public readonly record struct MonsterDeathEvent(
    IReadOnlyList<MonsterDeathIdentity> Candidates,
    int? ExperienceGained,
    DateTimeOffset At,
    bool IsFallback);
