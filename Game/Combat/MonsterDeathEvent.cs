using System.Collections.Generic;

namespace FujinTerm.Game.Combat;

/// <summary>
/// One identity in a <see cref="MonsterDeathEvent.Candidates"/> list.
/// Some monsters share the same death-line wording in stock MajorMUD
/// data (e.g. multiple variant rats may use the same "falls to the
/// ground" message), so a single observed line can map to several
/// possible <see cref="MonsterMessageRecord"/>s.
/// </summary>
/// <param name="Number"><see cref="Models.GameData.MonsterRecord.Number"/>
/// when the message record carries a Monsters-table link; <c>null</c>
/// when the record exists but isn't linked (user-curated entry that
/// hasn't been bound to a specific monster row).</param>
/// <param name="Name">The monster's display name from the
/// <see cref="Models.GameData.MonsterMessageRecord.Name"/> field.</param>
public readonly record struct MonsterDeathIdentity(int? Number, string Name);

/// <summary>
/// Payload of <see cref="MonsterDeathWatcher.MonsterDied"/> — fired
/// either when a specific death-line pattern matches (strong signal,
/// <see cref="Candidates"/> populated, <see cref="IsFallback"/> false)
/// or when the fallback "experience gain + *Combat Off*" sequence is
/// observed without a matching specific pattern (weak signal,
/// <see cref="Candidates"/> empty, <see cref="IsFallback"/> true —
/// consumers use their own state, typically CombatManager.CurrentTarget,
/// to attribute the death).
/// </summary>
/// <param name="Candidates">All monsters whose
/// <see cref="Models.GameData.MonsterMessageRecord.DeathLine"/> list
/// contained the observed line. Multiple entries when the pattern is
/// shared across variants. Empty for the fallback path.</param>
/// <param name="ExperienceGained">Experience number captured by the
/// most recent <c>You gain N experience.</c> line that landed before
/// the death/combat-off, when available. <c>null</c> when no exp line
/// was observed in the window.</param>
/// <param name="At">Wall-clock time the event fired.</param>
/// <param name="IsFallback">True for the exp + Combat Off path; false
/// when a specific death pattern matched.</param>
public readonly record struct MonsterDeathEvent(
    IReadOnlyList<MonsterDeathIdentity> Candidates,
    int? ExperienceGained,
    DateTimeOffset At,
    bool IsFallback);
