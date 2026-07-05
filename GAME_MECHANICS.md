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

## Vitality — HP, dropping, and death

**Max-HP sources** *([CONFIRMED])*
- **Class** sets the base health rolls; **race** applies an adjustment to those rolls; **level**
  scales max HP within the bounds those two establish. Same class differs by race, and climbs with
  level between the race/class-determined floor and ceiling.
- The **Health stat** scales **HP regen** (higher Health → faster natural recovery), not the max
  itself — it's the regen rate, on a scale, not a cap.

**Positive HP — fully functional** *([CONFIRMED])*
- At any positive HP (**1 … max**) the character keeps **every** normal action; HP level alone
  imposes no restriction. Ailments / status afflictions are a separate axis and can still block
  actions (e.g. a *held* status stops movement server-side) — but that's independent of HP.

**0 HP — "dropped" / bleeding out** *([CONFIRMED])*
- Hitting **0 HP** *drops* the character: they can **no longer move on their own**, and can **no
  longer fight or cast spells** — a dropped character is out of the action entirely, not merely
  immobile. A drop leaves them **bleeding out** — left unreversed, HP keeps trending toward the BBS
  death threshold (below).
- Two reversals bring a dropped character back into the positive:
  - **another player** issues `aid <name>` on them, or
  - a **healing spell** lifts their HP above 0.
- While dropped, another player can also `drag <name>` — the dropped character then **follows
  wherever the dragging player moves**, their only way out of the room until aided or healed.

**Death — the BBS negative-HP threshold** *([CONFIRMED])*
- Each **BBS sets its own negative-HP death threshold**; not every BBS advertises the number. When
  HP **reaches or passes** it (at, or more negative than, the threshold), the character **dies**:
  - loses a **life**,
  - **all non-loyal items drop to the ground** (loyal items stay on the corpse/player),
  - the character is **teleported to the graveyard room** appropriate to the **map** they died on.
- Graveyard rooms are **per-map**; one known graveyard is **`1/2189`** (map 1, room 2189).

**Death readout & overkill** *([CONFIRMED])*
- There is **no "overkill" message**. The HP figure visible at death is just the value HP was driven
  to by the killing event. A **single large hit** can drive it **far below the true floor** — the
  blow overshoots the threshold with no clamp or announcement — so an overkill death's HP reading
  **over-negatives** (understates) the real floor.
- A **slow death** — bleeding out, HP crossing the floor one tick at a time — lands right at the
  floor, so its reading is an **accurate** measurement of the true threshold.
- Consequence: the stored death threshold is only a starting estimate (the client seeds it at `-25`,
  a guess). Refine it from **slow deaths only**; an overkill reading is unreliable and must not push
  the estimate more negative.

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
  **harm** spell carries `AffectsLivingOnly` (ability code 108), so a monster flagged
  **NonLiving** (code 109) takes no damage from it — this is the
  `Your spell has no effect on <monster>.` case (e.g. `harm` on an acid slime). A spell with **no**
  targeting tag hits everything: `magic missile` carries no such tag, so it damages living,
  nonliving, **and** undead alike.
- This is **not** a resistance and **not** a level gate — it's a hard eligibility mismatch
  between a spell attribute and a monster attribute. Currently caught only **reactively**, off
  the `no effect` line: `OnSpellNoEffect` marks the species + spell immune for the rest of the
  room and gates that spell down the attack cascade (primary → alternate → weapon).
- The full tag/flag taxonomy (living / nonliving / undead / animal, and the charm family) is in
  **Spell targeting: monster type tags** below.

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
- The value is **signed**. A **negative** `Resist-<type>` is a *vulnerability* — that element
  deals **extra** damage (e.g. `Resist-Fire -50` → +50% fire damage). Across 1.11p the column
  runs roughly **-200 … +300**. So the full curve is: negative = bonus damage → `0` = normal →
  `100` = zero damage → `>100` = healing.
- Because the curve is flat and deterministic, a **≥100%** elemental resist is the **only**
  resistance the engine can safely **pre-empt** — skip the spell before casting when the target
  resists its element ≥100%. A negative (or 1–99%) resist must still **fire** the spell: it's a
  damage bonus or a partial cut, never a reason to skip.
- There is **no dedicated message**: every spell's verbose hit text differs, so the only
  runtime tell is the **damage number** in that spell's own hit line — **0 or negative is the
  resist signal.** Not modeled today: a resisted 0 / heal cast produces no `no effect` line, so
  nothing currently stops the engine from re-casting a spell that heals the monster.

*3b. Magic Resist (M.R., code 36) — probabilistic, NOT pre-emptable.* `AttType 4` "Normal" spells
(mage `magic missile`, priest `harm`) are **not** elemental, so the elemental Select-Case above
explicitly **skips** them (it skips `AttType 4` Normal and `AttType 6` Poison). Their only
damage-type mitigation is the monster's `M.R.` ability, **not** a `Resist-<type>` — and M.R.
never nulls a spell deterministically from its value alone. It works through **two independent
effects, each separately gated** (equations below are the reference client's own combat math):

- **Partial damage reduction** — gated by the spell's *damage ability code*. Applies only to code
  **1** `Damage`; code **17** `Damage(-MR)` **bypasses** it. `baseline M.R. is 50` (the no-change
  point): for M.R. ≥ 50 the reduction is `(M.R. − 50) / 200`, climbing to a hard **cap of 50%** at
  M.R. 150 and stopping (the target's own AntiMagic raises the cap to **75%**, via `M.R. / 200`).
  Below M.R. 50 the term goes negative — low M.R. *amplifies* damage taken. So even an enormous
  M.R. only ever **halves** (or, under AntiMagic, three-quarters) the damage — it can't reach 0.
- **Full-resist chance** — gated by the spell's `TypeOfResists` (below). A separate per-cast roll
  can negate the spell entirely, with probability `M.R. / 2` percent (M.R. 100 → 50% chance,
  capped at 98% for M.R. ≥ 196) — a *chance*, never a certainty short of the cap.
- Net: **100 M.R. never means 0 damage**, so M.R. must **never** feed a ≥100%→skip guard. Both
  example spells actually carry code **17** (bypass the partial cut), which shows how the two
  gates combine: `magic missile` (code 17 + `TypeOfResists 0`) takes **neither** effect — it
  always lands full, the reliable nuker; `harm` (code 17 + `TypeOfResists 2`) takes no partial
  cut but *can* be fully-resist-rolled; a code-**1** Normal spell would eat the capped partial cut
  on top. In every case a high-M.R. monster can still take Normal-spell damage.

*3b-note. `TypeOfResists` — the full-resist eligibility flag.* The Spells-table `TypeOfResists`
column (values 0/1/2) gates whether the full-resist roll above can fire, independent of the
damage type: **0 = never** (no full-resist roll — the spell always lands its post-reduction
damage), **1 = only when the target has AntiMagic**, **2 = always eligible**. Elemental attack
spells are typically `TypeOfResists 0` (fireball / frost jet / lightning bolt / acid jet all 0),
so their only mitigation is the deterministic elemental cut in 3a — which is exactly why a ≥100%
elemental resist is safely pre-emptable. Among Normal spells, `magic missile` is `TypeOfResists 0`
(never rolled-resisted) while `harm` is `TypeOfResists 2`.

*3c. Poison (`AttType 6`) — not resistible, binary immunity.* Poison has **no** resist value and
**no** `Resist-Poison` code — a target is either affected or immune, never "partially resisted."
- Immunity is sourced from **race / items**, not a resist stat: the **Kang** race is
  poison-immune, the **golden headdress** item grants poison immunity, and **swamp boots** /
  **snakeskin boots** negate certain room-cast "swamp poison" effects — snakeskin also grants
  immunity to certain poisons, varying by game-data set.

## Spell targeting: monster type tags

A spell's eligibility against a monster is a match between a **spell-side targeting tag** and a
**monster-side type flag**. A spell with no targeting tag affects every monster; a tagged spell
only affects monsters carrying the matching flag (or, for `living-only`, *lacking* the NonLiving
flag). These are hard eligibility gates, independent of resistance and level immunity above.

**Monster-side type flags** *([CONFIRMED] — verified against 1.11p)*
- **NonLiving** — the `NonLiving` ability (code 109). Its **absence** means the monster is living;
  there is no separate "living" flag.
- **Undead** — a **dedicated `Undead` column** on the Monsters row, *separate* from NonLiving. It
  is a **byte-boolean**: **0 = not undead, any non-zero = undead.** The MDB stores the Boolean
  `True` as `-1`, so across 1.11p the column holds `0` (986 rows), `1` (107 rows), **and `255`**
  (8 rows — `-1` as a byte); all non-zero values mean undead. **Test `Undead != 0`, never
  `== 1`.**
- **Animal** — the `Animal` ability (code 78). Gates the animal-charm spells below.
- These are independent axes: a monster can be NonLiving without being Undead. Worked examples:

  | Monster | NonLiving (109) | Undead (col) | Animal (78) | Net |
  |---|---|---|---|---|
  | thug (#10) | — | 0 | — | living |
  | lashworm (#2) | — | 0 | ✓ | living animal |
  | acid slime (#5) | ✓ | 0 | — | nonliving, **not** undead |
  | skeleton (#11) | ✓ | 1 | — | nonliving **and** undead |

**Spell-side targeting tags** *([CONFIRMED])*
- `AffectsLivingOnly` (code 108) — only affects monsters **without** the NonLiving flag (e.g.
  `harm`, `enslave`).
- `AffectsUndeadOnly` (code 23) — only affects monsters with `Undead != 0`.
- `AffectsAnimalsOnly` (code 80) — only affects monsters with the Animal flag (e.g. `charm
  animal`).
- No tag — affects all monster types (e.g. `magic missile`).

**Charm / enslave family** *([CONFIRMED] except where noted)*
- All charm-type control spells share the same base ability, `Enslave` (code 6); they differ
  **only** by their targeting tag. `enslave` (#55) is `Enslave` + `AffectsLivingOnly` (any living
  target); `charm animal` (#92) is `Enslave` + `AffectsAnimalsOnly` (needs the Animal flag);
  `song of charming` (#49, bard) is `Enslave` + `AffectsLivingOnly`.
- **[NEEDS CONFIRMATION]** A "charm level" is believed to cap what these can affect (possibly the
  caster's minimum level for the spell to take). This could **not** be verified: the reference
  client only *displays* these tags — it does not model charm success, and no "charm level" column
  exists on the Spells row (only `ReqLevel` / `MageryLVL` / `Cap`, which are learn/scaling params).
  Ask before building on a charm-level rule.

## Items & acquisition

- **[CONFIRMED]** Items are acquired via `buy` / `get` / `search`+`get`. There is no "hunt"
  verb — don't describe path-item sourcing as "hunting."

## Party

- **[CONFIRMED]** Party size: minimum 2, maximum 6.
- **[CONFIRMED]** Losing the leader disbands the whole party — whether the leader **disconnects or
  dies**. No grace-window auto-invite for a lost leader; on the leader's own death the party is gone
  by the time they respawn in the graveyard.
- **[CONFIRMED]** When a **non-leader party member dies**, they leave the active party — but in the
  leader's `par` the name shows as an **invited** (pending) slot **indistinguishable from a genuine
  pending invite**. So a member death is recognized **not** from `par` but from the room line
  **`<Name> has died.`** emitted where they're killed. The leader keys roster cleanup off that named
  member — **uninviting** them; there's no automatic removal. (Consequence for automation: never
  infer a death by diffing `par` alone — a died-and-now-invited name looks identical to a recruit
  we're still waiting on; only the death line disambiguates.)
- **[CONFIRMED]** A `par` row's secondary-resource bracket — mana `[M:N%]` for casters, kai
  `[K:N%]` for Mystics / monks — is **omitted entirely when the resource is exactly 0 points**,
  and this holds for mana and kai alike. It's a 0-*points* rule, not a 0-*percent* one: a caster
  with a few points left still prints `[M: 0%]` (bracket present). The row keeps its `[H:N%]`
  bracket, so a drained member is a member row missing its secondary field — not a dropped
  member. Consequence for parsing: a bracket-less row must still parse (or reconciliation drops
  the member), and an absent bracket on a known-caster row (`BaselineMp > 0`) means 0, not
  "unchanged."
- **[CONFIRMED]** `@wait` / `@ok` is a leader-directed **pause flag**, not a momentary signal. A
  follower telepaths `@wait` to the leader to hold the party; the leader stays paused until
  **either** the same member telepaths `@ok`, **or** the leader's own wait timer expires. The
  timer is the "If leading, wait only (s)" cap (`PartySettings.IfLeadingWaitTotalSec`); on expiry
  the leader gives up and resumes so a dropped / AFK member can't strand the party forever. A
  `.@held` say routes through the same pause (a held member can't move, so the party waits for
  them) and releases via that member's `@ok` on cure. The leader-side "ignore @wait when leading"
  opt-out drops inbound `@wait` before it ever pauses.

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
| Local player death (lives readout) | `You now have N lives remaining.` |
| Local player slain | `You have been slain by <killer>.` |
| Party member / other player killed in room | `<Name> has died.` |
