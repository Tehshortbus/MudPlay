# Version history

Notable changes per merged PR, **newest first**. The top of the [README](README.md) mirrors the most recent entry. Versioning follows semver (post-1.0): **MAJOR** = whole-program refactor, **MINOR** = a large PR, **PATCH** = a small / bugfix PR.

## 1.1.1

Richer in-app bug-report capture, so a single report pins more of the failing state.

**Added**
- Session section now stamps the app **version** and the **Debug / Combat diagnostics** on/off state.
- New **Party** section — roster, roles, leader, and pending-invite flags (the state party-targeting and `@join`-nag bugs hinge on).
- New **Live engine state** section — the `@join`-nag table (who's being chased and how far along) and the combat weapon-swap shadow (believed-equipped weapon + current target).
- Movement section now reports the suspect-strike count and the last observation's observed / open-door exit sets.
- Program log flags when both diagnostic channels were off, so an absent decision trail isn't mistaken for a quiet engine.

**Changed**
- Scrollback lines in the report are now timestamped (the live-screen tail stays unmarked), so log timestamps can be aligned against the wire I/O.
- Introduced this version history; the README top block now mirrors the current release.

## 1.1.0

Seven bugs surfaced in a live party session, batched into one PR.

**Fixed**
- Redundant hidden-exit search after a manual `sea` had already uncovered the exit.
- `@poisoned` / `@blind` / `@confused` / `@diseased` / `@held` party-sync announces bouncing an "invalid command" reply.
- Toggling an ignore-ailment setting mid-poison not releasing the standing `@wait`.
- Self-targeted party heals including the family name (`mihe Raijin Par` instead of a bare `mihe`).
- Backscroll window freezing when opening a ~10k-line transcript.
- The `@join` nag being cancelled by an unrelated automated telepath (an `@health` reply).
- Combat re-equipping an already-worn weapon on the first round.

## 1.0.0

Initial release. A faithful CP437 / VT100 Telnet client for MajorMUD with a MegaMUD-style automation suite — combat, party, navigation, healing, spells, character workshop, scripting, game-data import, a 4-tier settings hierarchy, and quality-of-life tooling — all in modeless, dockable windows.
