# Game Mechanics Reference — MajorMUD / MegaMUD

How the game engine actually behaves and what messages it emits. This is the trusted record
so a session doesn't re-guess engine behavior — **read it before reasoning about the game, and
append to it when the user confirms something new.** Per CLAUDE.md: never invent a mechanic; if
it isn't here and you're unsure, ask.

**Confidence tags**
- **[CONFIRMED]** — the user confirmed it directly.
- **[OBSERVED]** — grounded in the client's own parsers / message handling or a real
  bug-report capture; strongly evidenced but not explicitly user-confirmed.
- **[NEEDS CONFIRMATION]** — the code currently relies on it, but it's unverified; ask before
  extending anything that depends on it.

---

## Equipment & gear

**Equip / remove verbs** *(all [CONFIRMED])*
- `eq <item>` — the universal equip verb; works for **every** slot (armor *and* weapons).
- `wear <item>` — alternative for **armor** items only.
- `wield <item>` — alternative for **weapons** only.
- `rem <item>` — the universal remove verb; removes any equipped item (armor, weapon, or light).
- `hold` — **not used** by this game.

**Trade-places on equip** *([CONFIRMED])*
- If a slot is occupied and you `eq` (or `use`, for a light) another item from your inventory,
  the new item **trades places** with the current one — the old item returns to inventory —
  **provided the new item is actually usable** (class/level/slot constraints, a two-hander vs
  an occupied off-hand, etc.). If it isn't usable, the swap fails and nothing changes. So a
  single-slot swap needs no explicit `rem` first.

**Other**
- **[CONFIRMED]** A weapon equip / swap prints a **single** line — `You are now holding <new>.`.
  Swapping into an occupied weapon hand emits **no** removal line for the old weapon; the
  displaced weapon returns to the pack silently.
- **[CONFIRMED]** An armor swap into an occupied slot prints **two separate lines**, in order:
  `You have removed <old>.` then `You are now wearing <new>.`. (This is the split from a weapon
  swap: armor names the displaced piece with an explicit removal line, a weapon does not.) The
  two lines arrive back-to-back but are distinct — the client matches each on its own.
- **[CONFIRMED]** No effect in the game force-unequips gear (no disarm / removal effects).
  Worn state changes *only* from commands the player or the client issues.
- **[OBSERVED]** A two-handed weapon needs both hands free; the game rejects the wield while an
  off-hand is occupied (it isn't "usable" until the off-hand is gone), so the off-hand must be
  `rem`'d first.
- **[OBSERVED]** Re-equipping an item that's already worn draws
  `You do not have <X> left unequipped.`

## Light sources

- **[CONFIRMED]** `use <item>` readies a light (torch, lantern); `rem <item>` removes it.
  Lights follow the same trade-places rule as `eq` — `use`-ing a new light swaps out the
  current one (if usable).

## Stealth (sneak & hide)

**Commands** *([OBSERVED] — the client issues these)*
- `sn` — attempt to sneak. `hid` — attempt to hide.

**Equip before sneak** *([CONFIRMED])*
- Equipping / removing gear breaks sneak, so any gear change for an approach must be sent
  **before** the `sn`, never after. The correct approach order is **equip → sneak → move**.
  (This is why the backstab loadout is applied in the walker's pre-move step, ahead of the
  `sn`, rather than raced at room-clear.)

**Sneak state machine** *(lines all [OBSERVED] — parsed by the client)*
- `Attempting to sneak...` (alone, no suffix) — the server ACK: the sneak took and you're armed
  to move. A move made now carries the sneak into the next room.
- `Attempting to sneak...You don't think you're sneaking.` — soft rejection; the attempt didn't
  take. Resend `sn`.
- `Sneaking...` — emitted on each room entry while sneak holds; post-move confirmation you
  arrived unseen.
- `You make a sound as you enter the room!` — loud loss of sneak.
- `You may not sneak right now!` — hard block; no auto-retry.
- **[OBSERVED]** Sneak breaks *silently* when you move into a room that doesn't re-emit
  `Sneaking...` — no failure line, the stealth is just gone.
- **[OBSERVED]** Any NPC in the room prevents a sneak from taking — an `sn` is wasted while a
  monster shares the room.

**Backstab requires sneaking** *([OBSERVED])*
- Backstab needs the **sneaking** state specifically (approaching an unseen target while moving
  silently) — being merely *hidden* is not enough.

## Combat & backstab

- **[OBSERVED]** Backstab command: `bs <target>`.
- **[OBSERVED]** A monster in the room with the **see-hidden** ability reveals the sneaker to
  the whole room, so the opening move falls back to a normal attack rather than `bs`.
- **[OBSERVED]** `Your weapon has no effect against this monster!` — the current weapon can't
  hurt this monster; the client swaps to the configured alternate weapon.
- **[OBSERVED]** `Your fists have no effect against this monster!` — you're swinging bare-handed
  (no weapon in hand, or it left your hand).

## Attack spells: why one fails to damage a monster

**Three independent mechanics** decide whether an attack spell damages a monster — do not
conflate them. (Worked examples use the 1.11p data set.)

**1. SpellImmu +N — level immunity** *([CONFIRMED])*
- The monster's `SpellImmu` ability carries a value N and blocks any spell whose **base
  learnable level** (the Spells table `ReqLevel`) is **below N**; such a spell deals no damage.
  A spell learnable at level ≥ N still lands.
- Example: monster **#184** has `SpellImmu +10`, so every spell learnable at level 9 or lower
  can't hurt it — only spells learnable at 10+ work.
- Deterministic from game data, so the engine **pre-empts** it: `LevelBlockedFor` /
  `AttackSpellCanLand` skip a level-blocked spell before casting, and fold it into whether the
  monster is engageable at all.

**2. Spell targeting restriction (e.g. living-only)** *([CONFIRMED])*
- A spell can carry a targeting tag that disqualifies whole classes of monster. The priest
  **harm** spell is tagged **living only**, so a monster flagged **NonLiving** takes no damage
  from it — this is the `Your spell has no effect on <monster>.` case (e.g. `harm` on an acid
  slime).
- This is **not** a resistance and **not** a level gate — it's a hard eligibility mismatch
  between a spell attribute and a monster attribute. Currently caught only **reactively**, off
  the `no effect` line: `OnSpellNoEffect` marks the species + spell immune for the rest of the
  room and gates that spell down the attack cascade (primary → alternate → weapon).

**3. Damage-type resistance** *([CONFIRMED])*

A spell's damage type is its Spells-table `AttType` column (the same values `LookupEnums`
labels for the Browser). How resistance applies depends on which type it is — **do not treat all
three flavors alike**, because only the first supports a pre-emptive skip.

*3a. Elemental resistance — flat, deterministic, pre-emptable.* The five elemental `AttType`s
map one-to-one onto a monster `Resist-<type>` ability:

| `AttType` | Element | Monster resist ability (code) |
|---|---|---|
| 0 | Cold | `Resist-Cold` (3) |
| 1 | Fire | `Resist-Fire` (5) |
| 2 | Stone | `Resist-Stone` (65) |
| 3 | Lightning | `Resist-Lightning` (66) |
| 5 | Water | `Resist-Water` (147) |

- For these five, `Resist-<type> +N` is a **flat N% reduction** of that element. Example: #184
  (adolescent red dragon) has `Resist-Fire +50`, so fire spells deal **half** damage. At
  **100%** the element does **0 damage**; **above 100%** the damage goes **negative** and the
  spell **heals** the monster instead of harming it.
- Because the curve is flat and deterministic, a ≥100% elemental resist is the **only**
  resistance the engine can safely **pre-empt** — skip the spell before casting when the target
  resists its element ≥100%.
- There is **no dedicated message**: every spell's verbose hit text differs, so the only
  runtime tell is the **damage number** in that spell's own hit line — **0 or negative is the
  resist signal.** Not modeled today: a resisted 0 / heal cast produces no `no effect` line, so
  nothing currently stops the engine from re-casting a spell that heals the monster.

*3b. Magic Resist (M.R., code 36) — probabilistic, NOT pre-emptable.* The `AttType 4` "Normal"
spells (e.g. mage `magic missile`, priest `harm`) are **not** elemental and are cut by the
monster's `M.R.` ability, **not** a `Resist-<type>`. The elemental Select-Case above explicitly
**skips** `AttType 4` (Normal) and `AttType 6` (Poison) — M.R. is their mitigation path instead.
M.R. works through **two independent effects**, and neither ever nulls the spell deterministically
from the M.R. value alone (equations below are the reference client's own combat math):

- **Partial damage reduction.** `baseline M.R. is 50` (the no-change point). For M.R. ≥ 50 the
  reduction is `(M.R. − 50) / 200`, i.e. it climbs to a hard **cap of 50%** at M.R. 150 and stops
  (with the target's own AntiMagic active the cap rises to **75%**, via `M.R. / 200`). Below M.R.
  50 the term goes negative — low M.R. *amplifies* damage taken. So even an enormous M.R. only
  ever **halves** (or, under AntiMagic, three-quarters) the Normal damage — it can't reach 0.
- **Full-resist chance.** A separate per-cast roll can negate the spell entirely, with probability
  `M.R. / 2` percent (so M.R. 100 → 50% chance, capped at 98% for M.R. ≥ 196). This roll only
  fires when the spell's `TypeOfResists` allows it (see below) — it is a *chance*, never a
  certainty short of the cap.
- Net: a value of **100 in M.R. does not mean 0 damage** — it means ~25% less damage *and* a ~50%
  chance to fully resist that cast. So M.R. must **never** feed a ≥100%→skip guard; a high-M.R.
  monster can still take Normal-spell damage. Ability code **17** `Damage(-MR)` is damage that
  **bypasses** the M.R. partial-reduction cut entirely.

*3b-note. `TypeOfResists` — the full-resist eligibility flag.* The Spells-table `TypeOfResists`
column (values 0/1/2) gates whether the full-resist roll above can fire, independent of the
damage type: **0 = never** (no full-resist roll — the spell always lands its post-reduction
damage), **1 = only when the target has AntiMagic**, **2 = always eligible**. Elemental attack
spells are typically `TypeOfResists 0` (magic missile 0, fireball / frost jet / lightning bolt /
acid jet all 0), so their only mitigation is the deterministic elemental cut in 3a — which is
exactly why a ≥100% elemental resist is safely pre-emptable. `harm` is `TypeOfResists 2`.

*3c. Poison (`AttType 6`) — not resistible, binary immunity.* Poison has **no** resist value and
**no** `Resist-Poison` code — a target is either affected or immune, never "partially resisted."
- Immunity is sourced from **race / items**, not a resist stat: the **Kang** race is
  poison-immune, the **golden headdress** item grants poison immunity, and **swamp boots** /
  **snakeskin boots** negate certain room-cast "swamp poison" effects — snakeskin also grants
  immunity to certain poisons, varying by game-data set.

## Items & acquisition

- **[CONFIRMED]** Items are acquired via `buy` / `get` / `search`+`get`. There is no "hunt"
  verb — don't describe path-item sourcing as "hunting."

## Party

- **[CONFIRMED]** Party size: minimum 2, maximum 6.
- **[CONFIRMED]** Leader disconnect disbands the whole party — no grace-window auto-invite for
  a dropped leader.

## Talk / chat

- **[CONFIRMED]** Talk modes (say / talk-fast / slow) differ **per realm** — that's game
  configuration, not a client bug. The keyboard period is a say-precursor and stays unbindable.

---

## Message catalogue (lines the client parses)

| Event | Line |
|---|---|
| Weapon equip / swap (one line) | `You are now holding <X>.` |
| Armor wear, empty slot (names no slot) | `You are now wearing <X>.` |
| Armor swap into an occupied slot (two lines) | `You have removed <old>.` then `You are now wearing <new>.` |
| Remove | `You have removed <X>.` |
| Already worn | `You do not have <X> left unequipped.` |
| Sneak armed (ACK) | `Attempting to sneak...` |
| Sneak soft-fail | `Attempting to sneak...You don't think you're sneaking.` |
| Sneak confirmed (room entry) | `Sneaking...` |
| Sneak lost (loud) | `You make a sound as you enter the room!` |
| Sneak blocked (hard) | `You may not sneak right now!` |
| Weapon ineffective | `Your weapon has no effect against this monster!` |
| Fists ineffective | `Your fists have no effect against this monster!` |
| Spell can't affect target (e.g. living-only vs NonLiving) | `Your spell has no effect on <monster>.` |
