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

## Attack spells: immunity vs resistance

Two **distinct** mechanics decide why an attack spell fails to hurt a monster — do not
conflate them.

**Immunity** *([CONFIRMED])*
- A monster immune to a spell's damage type draws `Your spell has no effect on <monster>.` —
  a hard, binary immunity to that spell type (e.g. a `harm` spell vs an acid slime). The
  client reads this line as species-scoped attack-spell immunity and gates that spell out of
  the attack cascade (primary → alternate → weapon) for the rest of the room.

**Percentage resistance** *(mechanic [CONFIRMED]; the exact wire line for the resist / heal
case is not yet recorded — ask before parsing it)*
- A monster's resistance to a spell type is a **percentage**, and is **not** the immunity
  above — it's a numeric reduction on the damage, not the `no effect` line.
- At **exactly 100%** resist the spell lands but deals **0 damage**.
- **Above 100%** resist the damage goes **negative** — the spell **heals** the monster
  instead of harming it.
- Consequence: immunity is the only one of the two that emits `Your spell has no effect on
  <monster>.` (and the engine gates on it). An over-100%-resist cast produces no such line;
  it silently heals the target, so "full resist" must never be treated as equivalent to
  immunity.

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
| Spell immunity | `Your spell has no effect on <monster>.` |
