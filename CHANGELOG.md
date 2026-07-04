# Version history

Notable changes per merged PR, **newest first**. The top of the [README](README.md) mirrors the most recent entry. Versioning follows semver (post-1.0): **MAJOR** = whole-program refactor, **MINOR** = a large PR, **PATCH** = a small / bugfix PR.

## 1.4.0

A batch of party, movement, and navigation bug fixes plus two UI quality-of-life touches.

**Fixed**
- **A party Mystic/monk was dropped and re-added every `par` poll, spamming mid-combat `@health`.** The `par`-row parser only recognized the mana bracket `[M:N%]`, but a Mystic's secondary resource is *kai*, rendered `[K:N%]`. So every poll where a Mystic member had kai, their row failed to match, end-of-block reconciliation read them as having left the party and dropped their roster row — then re-added them on the one poll where their kai happened to read 0% (the game omits the bracket entirely at 0%), and that re-add fired PartyPoller's on-join `@health` round-trip *mid-fight*. The row regex now accepts both `[M:…]` and `[K:…]` into the same resource field, so a Mystic parses whether their kai is full or drained and their roster row stays put across the cycle.
- **A drained party caster's mana/kai bar froze at its last non-zero reading instead of dropping to 0.** `par` omits the secondary-resource bracket entirely when the pool is at *exactly 0 points* (a 0-*points* rule, not 0-*percent* — a caster with a few points left still prints `[M: 0%]`), and this holds for mana and kai alike. The row parser left the percentage untouched on an absent bracket, so a fully-drained member's bar stayed pinned at whatever it last showed. When the member is a known caster (a prior `@health` established their pool), an absent bracket is now read as 0, so the PartyWindow bar empties correctly; no-pool classes and not-yet-`@health`'d members are unaffected.
- **A `@health` round-trip was sent the instant an invite went out, before the invitee had joined.** Inviting a player added their roster row with the invited flag still unset, and `ObservableCollection.Add` fires its change notification *synchronously* — so the party poller saw the new member and fired the on-join `@health` before the code that marks the row invited had even run. The row is now constructed with the invited flag already set, so the poller's "skip invited members" gate holds and `@health` only fires once the player actually joins the party.
- **The auto-walker / movement loop stalled after crossing a text exit (e.g. `go path`, `go manhole`).** The walker announces a text exit to the room-tracker *with* its resolved cardinal, then sends the bytes — which flow back through the outbound-movement observer, which announced the *same* step a second time (cardinal-less). That phantom pending move kept the tracker's queue non-empty after the walker had already landed, holding it in the `Pending` state and stalling the walk until the next room re-display flushed the phantom. The observer's text-exit path now debounces against the engine's own announcement, exactly as the cardinal path already did, so a text-exit step enqueues once and the walk continues without a stall.
- **A navigation loop's blue loop-path overlay vanished when the Navigation window was closed and reopened mid-loop.** Reopening constructed a fresh view-model that seeded the walk path and status text but not the loop polyline, so the loop line only reappeared after the next step redrew it. The overlay is now seeded on construction, so an active loop's path shows immediately on reopen.

**Added**
- **Session Stats is now on the terminal right-click context menu.** The main-window right-click menu gains an *Open Session Stats* entry (mirroring its hotkey), alongside the other quick-open window shortcuts.

**Changed**
- **The Settings *Spells* tab is now labelled *Spells + Ailments*,** reflecting that it configures both spell casting and ailment handling. The persistence keys are unchanged, so existing per-tier settings carry over untouched.

## 1.3.0

Pre-emptive elemental-resistance guard for attack spells.

**Added**
- **Attack spells now skip a target that resists their element ≥ 100%.** Before casting a configured Normal / Alternate attack spell, the combat engine looks up the spell's damage element (`AttType`) and the monster's matching `Resist-<type>` value; when the target resists that element **≥ 100%** — where the spell deals **0 damage** (exactly 100%) or **heals** the monster (> 100%) — the slot is skipped down the attack cascade (Normal → Alternate → weapon), exactly like the existing "no-effect" immunity and `SpellImmu` level gates. It's deterministic and pre-emptive, so a round and its mana are never wasted casting into a wall. Only the **five elemental** types (Cold / Fire / Stone / Lightning / Water) are guarded — **Magic Resist** (`AttType 4`, a capped, probabilistic cut) and **poison** (`AttType 6`, a binary immunity) are never pre-empted, and a **negative** or partial (1–99%) resist still fires the spell (it's a damage bonus or a mere reduction, not a reason to skip). Two new game-data indexes back it — `MonsterResistIndex` (elemental resist codes 3/5/65/66/147 by monster) and `SpellAttackTypeIndex` (`AttType` by cast-code) — each failing open when the data is silent, so a thin data set never suppresses a spell.

**Changed**
- `GAME_MECHANICS.md` mechanism 3a now records that an elemental resist is **signed**: a negative `Resist-<type>` is a *vulnerability* (the element deals extra damage). The full curve runs vulnerability (negative) → normal (0) → immunity (100) → healing (> 100), and only the ≥ 100% end is safely pre-emptable.

**Fixed**
- **Undead monsters stored as `255` were shown as *not* undead in the Game Data browser.** The Monsters detail pane tested the `Undead` byte-boolean with `== 1`, but the MDB stores Boolean `True` as `-1`, which arrives as **`255`** for 8 of 1.11p's undead (banshee, zombie cat, skeletal steed, …). The test is now `!= 0`, so every undead flag renders regardless of whether the source stored `1` or `255`.

## 1.2.3

Corrects how damage-type resistance splits into three unlike flavors, and records the spell-targeting monster-type taxonomy.

**Changed**
- **Damage-type resistance split into its three real flavors, with the reference client's exact math.** `GAME_MECHANICS.md` previously lumped every `Resist-*` code — including Magic Resist — under one "flat N% cut, 100% = 0 damage, >100% = heal" rule. That's only true for the five *elemental* types (Cold/Fire/Stone/Lightning/Water, `AttType` 0/1/2/3/5 → resist codes 3/5/65/66/147), which is the one flavor deterministic enough to *pre-empt* (skip a spell whose element the target resists ≥100%). **Magic Resist** (M.R., code 36) — the cut on `AttType 4` "Normal" spells like `magic missile` and `harm` — is now recorded with its actual two-part equation: a partial damage reduction that baselines at M.R. 50 and **caps at 50%** (75% under AntiMagic), plus a separate full-resist *roll* at `M.R. / 2` percent. So 100 M.R. is **not** 0 damage (it's ~25% less damage and a ~50% negate chance); M.R. must never feed a ≥100%→skip guard, and code 17 `Damage(-MR)` bypasses the reduction. **Poison** (`AttType 6`) is recorded as *not resistible at all* — binary affected/immune, immunity sourced from race/items (Kang race, golden headdress, swamp/snakeskin boots) rather than a stat. Adds the `AttType`→element→resist-code mapping table and documents the `TypeOfResists` column (0 = never full-resistable, 1 = only under AntiMagic, 2 = always) that gates the full-resist roll.

**Added**
- **Spell-targeting monster-type taxonomy recorded.** `GAME_MECHANICS.md` gains a *Spell targeting: monster type tags* section: a spell's eligibility against a monster is a match between a **spell-side targeting tag** (`AffectsLivingOnly` 108, `AffectsUndeadOnly` 23, `AffectsAnimalsOnly` 80, or *no tag* = affects everything) and a **monster-side type flag** (the `NonLiving` ability 109 whose *absence* means living, the `Animal` ability 78, and a dedicated `Undead` column). The `Undead` column is a **byte-boolean** — `0` = not undead, any non-zero = undead — and across 1.11p it holds `0` (986 rows), `1` (107), **and `255`** (8 rows, the MDB's Boolean `True` stored as `-1`), so the correct test is `Undead != 0`, **never `== 1`**. `harm` (living-only, blocked by NonLiving) versus `magic missile` (no tag → hits living, nonliving, and undead alike) is the worked contrast, with a thug / lashworm / acid slime / skeleton example table; the charm family (`enslave`, `charm animal`, `song of charming`) all share the `Enslave` base ability (code 6) and differ *only* by targeting tag. A "charm level" cap is flagged **[NEEDS CONFIRMATION]** — unverifiable against the reference client, which only *displays* these tags.

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
