namespace FujinTerm.Models.Profile;

// BBS-scoped identifier for a character profile. Profiles live under
// Data/BBS/{Bbs}/profiles/{Name}/profile.json, so the same character name on
// two different BBSes is two distinct profiles — the (Bbs, Name) pair is the
// only unambiguous key. Used by the recent-profiles list, last-used
// persistence, and the File → Open profile picker. Value equality lets callers
// de-dup refs directly.
public sealed record ProfileRef(string Bbs, string Name);
