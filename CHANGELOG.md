# Version history

Notable changes per merged PR, **newest first**. The top of the [README](README.md) mirrors the most recent entry. Versioning follows semver (post-1.0): **MAJOR** = whole-program refactor, **MINOR** = a large PR, **PATCH** = a small / bugfix PR.

## 1.2.2

Records how attack spells actually fail to damage a monster.

**Changed**
- **Attack-spell failure modes documented accurately.** `GAME_MECHANICS.md` now separates the three independent reasons an attack spell does no damage, each confirmed against 1.11p data: (1) `SpellImmu +N` — a level gate that blocks any spell whose base learnable level is below N (monster #184's `SpellImmu +10` blocks everything learnable at level ≤ 9); (2) a spell targeting restriction such as the priest `harm` spell's *living-only* tag, which no `NonLiving` monster (e.g. an acid slime) can be hurt by — the actual cause of the `Your spell has no effect on <monster>.` line, corrected from the earlier "immune to a damage type" framing; and (3) flat percentage resistance per damage type (#184's `Resist-Fire +50` halves fire damage; 100% deals 0, over-100% heals). The message-catalogue label for the no-effect line is corrected to a targeting mismatch rather than "immunity."

**Changed**
- **Weapon-swap message reference corrected.** `GAME_MECHANICS.md` claimed a weapon swap prints two lines (a removal then a wear). A live capture (swapping a quarterstaff and a dagger) confirms it prints a single line — `You are now holding <X>.` — with no removal line for the displaced weapon, which returns to the pack silently. The two-line pattern is the *armor*-into-an-occupied-slot case. The message catalogue and the Equipment prose now record the weapon-vs-armor distinction, matching the client's own inventory parser.

**Added**
- **Attack-spell immunity vs resistance recorded.** `GAME_MECHANICS.md` now documents that a spell's `Your spell has no effect on <monster>.` line is a hard, binary *immunity* (which the combat engine gates on) — distinct from percentage *resistance*, a numeric reduction where exactly 100% resist deals 0 damage and over-100% resist inverts into *healing* the monster with no "no effect" line to flag it.

## 1.2.0

Gear actuation gets a single owner, and enabled backstab loadouts now arm themselves in the auto-walker's pre-move sequence.

**Added**
- **Backstab gear auto-fire.** When a Backstab equipment set is enabled, the auto-walker now gears up for the next sneak as part of its pre-move sequence: the backstab weapon and the set's armor are equipped — sending only the pieces not already worn — and *then* the sneak fires, so the whole approach is weapon → armor → sneak → move. Because equipping breaks sneak, the gear has to land before the `sn`; sequencing it into the pre-move step guarantees that and puts the loadout in hand before the surprise round.

**Changed**
- **The Equipment Manager is now the sole actuator for gear.** Combat decides which weapon to wield and delegates the swap; equipping logic no longer lives in two places, so a set applied from the Workshop and a weapon flipped mid-fight take the exact same path. Swaps use the uniform `eq` verb and diff against the *live* worn loadout, so only the pieces that actually differ hit the wire — no redundant equips, and no cached "believed-equipped" shadow that could drift out of sync with what's really worn.
- The bug report's combat weapon state now reads the live worn weapon / off-hand straight from inventory instead of the removed shadow fields.

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
