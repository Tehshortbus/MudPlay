namespace FujinTerm.Models.Profile;

/// <summary>
/// BBS-scoped identifier for a character profile. Profiles live under
/// <c>Data/BBS/{Bbs}/profiles/{Name}/profile.json</c>, so the same
/// character name on two different BBSes is two distinct profiles — the
/// (<see cref="Bbs"/>, <see cref="Name"/>) pair is the only unambiguous
/// key. Used by the recent-profiles list, last-used persistence, and the
/// File → Open profile picker. Value equality lets callers de-dup refs
/// directly.
/// </summary>
public sealed record ProfileRef(string Bbs, string Name);
