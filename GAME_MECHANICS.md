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

**Named-item uniqueness & paired slots** *([CONFIRMED])*
- Only **one of each *named* item** can be worn at a time. Two identically-named pieces
  (e.g. two *silver bracelets*) can't both be equipped; the second is refused. Distinct
  names are fine — a *silver bracelet* and an *ivory bracelet* equip together.
- The **finger** and **wrist** families each hold **two** physical pieces (Finger1/Finger2,
  Wrist1/Wrist2), so long as the two are distinct names. Every other slot holds one.

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
- **[CONFIRMED]** Worn gear **persists across logins** — you log back in wearing whatever you
  had on. There's no re-equip-on-connect step to do; the loadout is already correct. The one
  exception is the rare **cleanup EP-zap**: when an evil character's alignment drops below an
  item's Evil-Point threshold the game force-removes it, and re-equipping then fails with
  `You may not use that weapon.` (weapon) / `You may not wear that item!` (armor). This is why the
  client must not fire a speculative `eq` before the first `i` dump lands — the desired gear is
  already worn, so a blind equip only draws the already-on refusal (or the EP-zap refusal).

## Light sources

- **[CONFIRMED]** `use <item>` readies a light (torch, lantern); `rem <item>` removes it.
  Lights follow the same trade-places rule as `eq` — `use`-ing a new light swaps out the
  current one (if usable).
- **[CONFIRMED, capture 2026-07-11]** **A readied light burning out prints exactly
  `Your <item> flickers and goes out.`** (e.g. `Your torch flickers and goes out.`) — one line,
  period-terminated, no name/exits. It is the *only* signal the light is gone: the inventory
  `i` dump still lists the item as readied until the next dump lands, so the display lies about
  a light that no longer exists in the meantime. The auto-light path latches on this line
  (`AutoLightProvisioner.OnReadiedLightExpired`, pattern `^Your .+ flickers and goes out\.$`)
  to discount the stale readied value and re-ready a carried spare once the room's "can't see"
  line confirms it went dark. Anchored full-line so a mob-flavour "flickers" elsewhere can't
  false-trigger.
- **[CONFIRMED]** **A monster in a dark room is invisible to the room display but still
  attacks — engage it by the name in its attack line.** With no `Also here:` line (the dark
  room prints only the "can't see" line, see *Movement & navigation*), the only evidence a
  hostile shares the room is its incoming attack line, rendered in dark cyan: a miss
  `The <monster> <verb> at you` or a hit `The <monster> <verb> you for N damage!`. The
  `<monster>` token is the monster's real name, so `a <monster>` (e.g. `a cave bear`) attacks
  it exactly as if it had been listed under `Also here:`. The client injects that
  attack-revealed monster into the room's entity list so auto-combat engages it
  (`DarkRoomCombatWatcher`). Attacking a monster that **isn't** in the room draws
  `Your command had no effect.` — the signal that the target is gone (retract it and stop
  swinging).

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

**Observing another player's failed sneak into your room** *([CONFIRMED] 2026-07-12, user)*
- `You notice <name> sneaking in from the <dir>.` — you *perceived* another player entering your
  room while sneaking (their sneak failed against you). **This line is always a player, never a
  monster** — monsters do not sneak-enter with this wording. The realm may paint the line the
  monster hue (yellow), so wire colour is **not** a reliable kind hint here.
- **Client note:** `SneakArrivalNotice` captures the bare `<name>` and `RoomEntryWatcher`
  classifies it Player unconditionally. The generic `RoomEntryArrival` pattern carries a
  `(?!You notice )` guard so it doesn't also grab `"You notice <name>"` as a null-numbered
  Monster — which previously held the combat gate open and froze the loop.

**Sneak vs hide — both enable backstab** *([CONFIRMED])*
- **Sneaking** and **hidden** are distinct stealth states and **either one enables a backstab**:
  - *Sneaking* lets you **move** silently and open on a target you approach, but does **not** remove
    your name from the room's `Also here:` line — others still see you listed there.
  - *Hidden* removes your name from `Also here:` (you're invisible in the room) but you **cannot
    move** while hidden. A **player** has to `search` the room to reveal (unhide) you; **monsters do
    not search rooms**, so a monster that walks into a room you're hidden in never reveals you — it
    just becomes a backstab target. (A monster's passive **see-hidden** ability is a separate thing:
    it reveals a stealthed character to the whole room on sight, defeating the opener — see below.)

**Hide state machine** *(lines all [CONFIRMED] — from paired two-character POV captures)*
- `Attempting to hide...` (alone, no suffix) — the attempt fired and the server ran a hide check,
  but the outcome is **NOT reported to you**. This line is **ambiguous**: it means "a check
  happened," not "you are hidden." You cannot tell success from failure off this line alone.
- `Attempting to hide...You don't think you are hidden.` — explicit hide **FAILURE**. This is the
  only self-observable failure signal.
- **Hide SUCCESS is not self-observable.** There is no self-side "you are now hidden" confirmation.
  The only 100%-reliable confirmation is **external**: another player displaying the room and finding
  you **absent** from the `Also here:` line (or their `search` failing to turn you up). From your own
  output stream, the best you can know is "an attempt fired" (`Attempting to hide...`) or "it
  failed" (`...You don't think you are hidden.`) — never a positive success.
- **Reveal (search) mechanic:**
  - A player runs `search` / `sea`. On a hit they see `You see <name> hiding in the shadows.` and the
    hidden character is revealed (returned to `Also here:`); on a miss they see
    `Your search revealed nothing.`.
  - The hidden character sees `<name> is searching the area.` while someone searches — i.e. you get
    a warning that a reveal attempt is in progress.
- **Party warning [CONFIRMED]:** **do not hide while in a party.** A hidden member is removed from
  `Also here:`, and a player who isn't listed there **cannot be single-target-targeted** by other
  players — including party heals and buffs — until revealed. Only room-wide spells (relevant in PvP)
  and possibly party-wide spells still reach a hidden member. Auto-hide must therefore be suppressed
  whenever the character is in a party.

- **Client note:** the engine arms the opening `bs` off **either** stealth state (sneaking OR
  hidden — the backstab gate reads `StealthManager.IsStealthed`). Because hide success is **not**
  self-observable, the hidden side is handled **optimistically**: a bare `Attempting to hide...`
  latches `Hidden = true` on faith, and the backstab surprise-round resolver confirms or denies it
  after the opener swings (a real hide lands the `surprise`; a false one whiffs and, with
  `RunIfBackstabFails`, flees). The one ground-truth signal, `...You don't think you are hidden.`,
  drops the optimistic state. A move breaks hide (you can't move while hidden). A fresh in-place
  hide re-arms the surprise round, so a hidden character that kills one monster and re-hides can
  backstab the next one that wanders in. **Auto-hide is suppressed while in a party** — a hidden
  member falls off the Also-here line and can't be single-target-healed/buffed until revealed.

**ShadowRest** *([CONFIRMED] — user, Paradigm; not present in stock)*
- Some Paradigm classes have a **ShadowRest** class ability. It is not a stock MajorMUD mechanic.
  In the imported game data it is **class-ability code 1103** on the Classes table (`AbilityNames`
  maps `1103 → "ShadowRest"`); a class row carrying that code in any `Abil-N` slot has the ability.
- **What it does:** while **hidden or sneaking**, the character can `rest` (or meditate) and **stay
  stealthed while resting in the room** — monsters in the room **do not attack** the resting
  stealthed character. Normally a hostile in the room means you can't safely rest; ShadowRest lets a
  stealthed character rest right there without being engaged.
- Some ShadowRest classes gain an **HP-regen bonus** while resting this way (e.g. thief gets extra
  regen). The bonus is server-side; the client's `RegenTracker` measures the actual rate off the
  stat line, so it needs no separate model of the magnitude.
- **No special messages** mark the state. The only observable sequence is a successful hide/sneak
  followed by `rest` — there is no "you shadow-rest" line. So the client can't detect ShadowRest
  from the stream; it gates on the **class ability (code 1103) + the user setting** instead.
- **Ideally used solo.** Resting while hidden un-targets you from party single-target heals/buffs
  (same reason auto-hide is party-suppressed above), so ShadowRest resting is a solo behavior.

## Combat & backstab

- **[OBSERVED]** Backstab command: `bs <target>`.
- **[OBSERVED]** A monster in the room with the **see-hidden** ability reveals the sneaker to
  the whole room, so the opening move falls back to a normal attack rather than `bs`.
- **[CONFIRMED]** **Backstab only lands on the opening round** — the very first action taken in a
  freshly-approached room while sneaking. Once ANY combat action has fired here (a `bs`, a spell,
  or a normal swing), the surprise is spent and a later `bs` can no longer connect. So after the
  opener the client must fall back to the configured normal attack priority; re-issuing `bs` on a
  re-engage (a cast interrupt's re-attack, a target re-pick) wastes the round. The client tracks
  the spent opener per room and re-arms it only on the next sneak-approach.
- **[CONFIRMED]** **Success line:** a landed backstab is a **single** swing containing the word
  **`surprise`** — e.g. `You surprise punch large wild dog for 36 damage!`. A surprise line making
  it through **proves the sneak did not fail** — the opener connected.
- **[CONFIRMED]** **The opener is always `bs`, never `pu`.** The stock realm has been observed to
  still run the surprise round even when the opener was a normal `pu` on the mystic — but that
  leeway is **realm-specific** and must not be relied on; other game types may require the literal
  `bs` opener to trigger the surprise at all. So whenever backstab is enabled and the character is
  armed (a successful sneak, or hidden with a monster in the room), the opening command is
  **always** `bs <target>` — the client must never substitute `pu` and hope the surprise still fires.
- **[OBSERVED, mechanism unconfirmed]** Only the **opener** needs to be `bs`, and follow-on attacks
  must stay quiet. In one live capture the opener `bs large wild dog` was followed by two
  client-sent `pu large wild dog` during the `*Combat Off*` / `*Combat Engaged*` interrupt bounce,
  and `You surprise punch ... for 36 damage!` still landed. **Do not read this as "the engine
  continues the backstab through follow-on attacks"** — the likelier explanation is timing: the
  `pu` commands simply hadn't registered server-side before the `bs` surprise round resolved. So a
  well-timed follow-on `pu` *could* have sabotaged the surprise. Practical rule for the client:
  send `bs` as the opener, then stay quiet — don't spam follow-on attack commands that might
  register and clobber the surprise (let the server's auto-repeat carry the fight). Never send a
  second `bs`. **The client enforces this by suppressing all Attack-Order re-fire while a `bs` is
  pending resolution.**
- **[CONFIRMED]** **Attack announce, and its backstab exception.** Any normal attack command
  against an NPC produces a public announce: the attacker sees `*Combat Engaged*` and everyone
  else in the room sees `<player> moves to attack <target>`. A **backstab round is silent** — it
  emits no `moves to attack` line to other players, so the surprise opener doesn't tip off
  onlookers. (Consequence for the client: it can't confirm its own backstab landed from a
  `moves to attack` echo — there won't be one; use the `surprise` swing line instead.)
- **[CONFIRMED]** **A round action is announced by ONE of two lines — melee OR spell.** A party
  member has "gone" for the round when the room shows *either* `<player> moves to attack <target>.`
  (melee/ranged) *or* `<player> moves to cast <spell name> upon <target>.` (a combat spell). Both
  forms count as that member's announce; a caster's turn produces the second form only. So
  attack-last coordination (waiting until every other member has committed before our own
  `*Combat Engaged*` lands) must treat the two lines as equivalent per-member announce signals —
  keying only on `moves to attack` misses every spellcaster in the party.
- **[CONFIRMED]** **Failure signals — the reliable single-line tell.** The surprise round is a
  **single** swing, so the **first** of the player's own combat-result lines after the `bs` settles
  the outcome: it either **carries `surprise`** (landed) or **lacks it** (failed). A failure surfaces
  either as a **whiff** (`You swing at <target>!` — no "for N damage", renders dark-cyan) or as a
  **folded normal round** (`You punch <target> for N damage!` with no "surprise"). Detection is
  **text-only** — the `surprise` token, not the color. The client keys off this first-line tell and,
  when *Run if BS fails* is on, flees on a detected failure (routed through the normal
  break-before-flee escape path).
- **[CONFIRMED]** `You cannot backstab with this weapon.` — you tried to `bs` while sneaking with a
  weapon that isn't backstab-capable. (No weapon-type flag in the game data exposes this ahead of
  time; it is only knowable reactively from this line.)
- **[OBSERVED]** `Your weapon has no effect against this monster!` — the current weapon can't
  hurt this monster; the client swaps to the configured alternate weapon.
- **[OBSERVED]** `Your fists have no effect against this monster!` — you're swinging bare-handed
  (no weapon in hand, or it left your hand).
- **[CONFIRMED]** **A magical creature needs a magical weapon (or a spell) to be damaged.**
  Physical un-hittability is deterministic from game data: a weapon can damage a monster iff the
  weapon's magical "hit" level is at least the monster's magical-defense level
  (`ItemMagic.HitMagic(weapon) >= MonsterMagic.MagicalLevel(monster)`; a monster whose
  `MagicalLevel <= 0` is hittable by any weapon). When the deterministic check can't decide
  (weapon unknown to the tables), the `Your weapon has no effect` line is the reactive backstop —
  the client records the species as un-hittable by that weapon. **Spells are not bound by this
  physical gate** — an attack spell can damage a magical creature that no configured weapon can
  touch. So when the whole weapon path is exhausted (normal weapon can't hit, and either no
  alternate is configured or the alternate also can't hit), the *Physical first* action order
  falls back to the attack-spell cascade for that target rather than swinging uselessly.
- **[CONFIRMED]** **Casting a spell mid-fight drops the auto-attack for that round** — the server
  emits `*Combat Off*` because a cast is a distinct action that interrupts the sustained weapon
  swing. If the target is **still alive** after the cast, the desired behaviour is to **re-attack
  immediately** (as soon as the `*Combat Off*` lands), not wait for the next combat-round tick or a
  manual room re-parse. Confirmed by the user casting a Kai power (`swan`) on a live target: without
  a prompt re-attack the client idled a full round. Applies to a **hand-typed** cast just as much as
  an engine-issued between-round cast — in this realm a spell is cast by typing its cast-code
  (`Spells.Short`) directly (`swan`, `swan rat`), with no `c` verb precursor, so the client
  recognises a manual cast by that cast-code on the wire.

### Per-monster overlay automation *([CONFIRMED] 2026-07-10, user design)*

Client-side automation policy for the Game Data → Monster overlay flags (not engine
behaviour — how the client's auto-combat interprets the per-monster overrides):

- **DontBackstab** — a monster flagged DontBackstab is never the backstab opener. On the
  opening (armed) round the target picker **prefers the highest-priority non-flagged**
  actionable monster to backstab; a flagged monster is only chosen when **every** actionable
  monster in the room is flagged, in which case the room is **still cleared** — the opener just
  falls through to a normal attack instead of `bs` (never skip the room over the flag).
- **Override attack spell / Override pre-attack spell** — a per-monster spell (stored as a
  `Spells.Number`, resolved to the `Spells.Short` cast-code) that **substitutes** for the global
  Combat-tab choice **for that species only**. The attack override occupies the *Normal Attack
  Spell* rung; the pre-attack override occupies the *Single-Target Debuff* rung (cast through the
  in-between window). Because only two single-target chooser slots exist, this mapping is
  structurally forced.
  - **Gate bypass:** when an override is set the client **bypasses the effectiveness gates**
    (observed "no effect" immunity, SpellImmu level-block, and ≥100% elemental resist) — the
    rationale is that a user who hand-picks a spell for a specific monster has done the due
    diligence that it works. The **physical constraints still apply**: the rung's mana floor,
    the once-per-target guard (pre-attack), and the override's own per-room cast cap.
  - **Count = per-room cast cap.** The override's configured count is the cap; the overlay
    documents **null = 0**, so a spell set with **no positive count is treated as inactive** and
    the client falls back to the global slot (likewise if the number doesn't resolve to a known
    cast-code). This "null/zero count ⇒ fall back to global" reading is the client's
    interpretation of the ambiguous count field — **flag for user confirmation** if override
    behaviour is ever questioned.

## Monster aggression — who opens on you unprovoked

A monster is **hostile** (attacks without being engaged first) as a function of the **monster's
`Align`** (the Monsters-table `Align` column, int 0–6) and **your character's alignment title**.
Two independent layers stack. In the source tables: **columns = your (player) alignment, rows =
the monster's alignment.**

**`Align` values** *([CONFIRMED] — matches `LookupEnums.MonAlignmentNames`)*
`0` Good · `1` Evil · `2` Chaotic Evil · `3` Neutral · `4` Lawful Good · `5` Neutral Evil ·
`6` Lawful Evil.

**Your alignment-title ladder** (lawful → evil, from the who column):
Saint / Lawful → Good → Neutral → **Seedy → Outlaw → Criminal → Villain → Fiend**. The last five
(Seedy and worse) are the **"Evil bucket."** (`AlignmentBucket` collapses this to Good / Neutral /
Evil for item filtering; the criminal layer below needs the finer title.)

**Layer 1 — alignment auto-aggro (every monster, straight from `Align`)** *([CONFIRMED])*
- `Align` **1 / 2 / 5** (Evil / Chaotic Evil / Neutral Evil) — **opens on everyone**, every title.
- `Align` **0 / 3** (Good / Neutral) — **never** aggros anyone.
- `Align` **6** (Lawful Evil) — "honor among the wicked": aggros **Lawful / Good / Neutral** titles,
  but **spares the Evil bucket** (Seedy and worse).
- `Align` **4** (Lawful Good) — **never aggros by alignment**; the only Align-4 aggro is the guard
  subset via Layer 2.

So the only alignment-driven aggro that depends on *your* title is Align-6 (spares Seedy+); 1/2/5
are unconditional, 0/3/4 never bite on alignment alone.

**Layer 2 — criminal / guard system (the Align-4 `*`; runtime reputation, NOT in the monster
table)** *([CONFIRMED] behaviour)*
Keyed on **your title**, enforced by **guard** NPCs plus special actors:

| Your title | Guards | Extra actors |
|---|---|---|
| Lawful / Good / Neutral | ignore | — |
| Seedy | ignore | bad deeds done *to* you are ignored (you lose guard protection, but guards don't aggro) |
| Outlaw | **attack on sight**, but spare your life | — |
| Criminal | **slay on sight** | — |
| Villain | **slay** | bounty hunters also attack |
| Fiend | **slay** | bounty hunters + archons / gods smite you with lightning |

**Identifying a guard from imported data** *([CONFIRMED])*
The game's monster-`Type` field distinguishes an ordinary NPC from a law-enforcing *guard*, but that
distinction is **not exported into the MDB we import** — the imported `Type` only carries Solo /
Leader / Follower / Stationary (0–3), never the guard value. So we can't read guard-ness off the
type. The reliable proxy we *do* have: a monster that **casts spell 583 (`jail`)** is a guard, and
it attacks us when our title is **Outlaw or worse**. Detection = the monster references spell `583`
in any of its castable-spell fields (`AttHitSpell-*`, `MidSpell-*`, `DeathSpell`, `CreateSpell`). In
the shipped set that flags the guardsmen (#13/#14/#905/#538), Sheriff Lionheart (#40), and elite
guardsman (#757). This is a **partial** list — other mobs aggro the evil-titled without casting
`jail` (e.g. Templar is a guard yet has no `jail`); those get added here as they're recognised.

**Client hostile-in-room test.** For each monster in the room, read its `Align`: hostile if
`Align ∈ {1,2,5}` (always), or `Align == 6` and our title is Lawful / Good / Neutral, or the monster
is a **guard** (casts `jail` 583, per above) **and** our title is Outlaw-or-worse. Our own title
comes from the stat screen / who line (`AlignmentTracker` / `PlayerStats`).

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
- **A dropped character can still hang up.** Dropping blocks in-realm *actions* (move / fight / cast),
  but the **carrier drop / main-menu exit** (the Game-Exit command, e.g. `=x` / `;o`) **still goes
  through at 0 HP or below**. So the emergency-hangup escape stays available all the way through the
  bleeding-out window — the client's low-HP auto-hangup fires down to (but not past) the BBS death
  floor, giving a dropped-but-not-yet-dead character a last chance to disconnect before dying.
- **HP percentage goes negative while bleeding out.** HP% is a plain `hp / maxHp` ratio with no clamp
  at zero, so a dropped character reads a **negative percentage** — the `par` party display shows it
  as such (e.g. a member driven to −12/200 HP reads a negative HP%). So a percentage-based threshold
  is a continuous scale from 100 % down through 0 % into the negatives, exactly like an absolute-HP
  scale — the auto-hangup's "hang up if below" trigger can be set anywhere on it, including negative.

**Death — the BBS negative-HP threshold** *([CONFIRMED])*
- Each **BBS sets its own negative-HP death threshold**; not every BBS advertises the number. When
  HP **reaches or passes** it (at, or more negative than, the threshold), the character **dies**:
  - loses a **life**,
  - **all non-loyal items are lost from the player** (loyal items stay on the player); *where* the
    dropped items land is realm-type dependent — see the deathpile/corpse note below,
  - the character is **teleported to the graveyard room** appropriate to the **map** they died on.
- Graveyard rooms are **per-map**; two known graveyards are **`1/2189`** (map 1, room 2189) and
  **`16/542`** (map 16, room 542).
- **[CONFIRMED]** **Deathpile vs corpse depends on the realm type.**
  - **Stock** realms have **no corpse object**: non-loyal items and coins drop **loose to the
    ground** of the death room, and **loyal items stay on the player**. Anyone in the room can pick
    the loose pile up.
  - **Paradigm** realms put the dropped items into a **container "corpse"** rather than loose on the
    ground — recoverable only by the **owning player**, or by another player who has that player's
    **corpse password**.

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
- **One death message, both cases.** There is exactly **one death line** — `You have been slain by
  <killer>.` — for **every** death, an overkill blow *and* a slow bleed-out alike; a bleed-out still
  names the **last attacker**, so the line by itself cannot tell a slow death from an overkill. The
  only runtime signal that separates them is the **HP trajectory** into death: a gradual, small-step
  descent through the bleeding-out band (slow, accurate) versus a single large HP drop that blows
  past the floor (overkill, discard). So the client's floor auto-refinement must classify off the
  observed HP steps, not the message — and, per the trace's stated assumption, only while the
  killing blow isn't a huge hit that leaps right past the floor.
- **An overkill can mask the reached HP entirely.** A killing blow that jumps well past the floor may
  emit **no sub-floor HP prompt at all** — the client sees the pre-death HP and then the death, with
  the intermediate value the blow drove HP to never printed. (Observed: at HP `-241` a `9`-point hit
  simply killed the character; no `-250` prompt appeared.) So a single terminal reading can never be
  trusted as a floor measurement. The reliable complement is **live-survival evidence**: while HP
  ticks down through the negatives and the character is confirmed **still alive** (a *later* in-band
  prompt proves the previous one was survived), each survived reading is a valid lower bound — the
  floor sits **below** it — so the estimate ratchets down progressively as HP rolls further negative,
  and simply **stops at the death message**. The terminal/masked reading is structurally excluded
  because it is never followed by another in-band prompt.

**Miracle-save — a death, not a rescue** *([CONFIRMED])*
- When a character who still has lives dies, the engine prints a three-line miracle sequence in
  place of the plain slain line:
  ```
  You have been killed!
  But, due to a miracle, you have been saved.
  You have N lives left.
  ```
  Despite the "saved" wording this **IS a death** — a life is spent (N is the post-death count),
  non-loyal items drop, HP resets to full, and the character is teleported to the graveyard / temple
  room, exactly like any other death. The "miracle" text is **flavor that comes with having lives**,
  not a rescue that avoids the death. Only at **0 lives** does the engine instead force-exit the
  character from the game (permadeath) rather than print the miracle line.
- The lives readout on this path is `You have N lives left.` — a **different line** from the
  slow / normal-death `You now have N lives remaining.`. A death-capture that keys only off the
  "remaining" form misses every miracle-save death. The **reliable death marker across all forms** is
  the `You have been killed!` line (DoT / no-named-killer deaths) alongside `You have been slain by
  <killer>.` (attacker-named deaths) — capture off those, not off the lives readout.
- **Coins on hand drop into the deathpile** too, alongside the non-loyal items — recoverable
  from the deathpile / corpse like the rest of the drop (per the stock-vs-paradigm note above). The five denominations (largest first) are `runic coin`,
  `platinum piece`, `gold crown`, `silver noble`, `copper farthing`, at the 1 000 000 / 10 000 / 100 /
  10 / 1 copper-farthing ratio ladder. The deathpile display lists each denomination the character
  held by its own count (e.g. `100 gold crowns` + `1 platinum piece`), **not** re-bucketed into a
  consolidated wealth total.

**On-death effect wipe** *([CONFIRMED])*
- Death removes **all active effects — buffs and debuffs alike**. A poison ticking at the moment of
  death clears with it: the death sequence carries `The effects of the poison wear off!` right
  alongside `You have been killed!`. So after a death the character is at full HP with **no lingering
  effects of any kind**; any client-side effect / buff tracking must be flushed on death.

**Drop removes you from your party** *([CONFIRMED])*
- Dropping (hitting 0 HP) doesn't just immobilize — it **removes you from the party game-side**. After
  a miracle-save death the `par` check reads `You are not in a party at the present time.` even though
  the client still believed it was partied and following the leader. The **only** reason a dropped
  character still tracks the leader's room is that the leader `drag`s them — following is an artifact
  of the drag, not live party membership.
- While dropped / mortally wounded the game **rejects every action command**: movement, casting,
  aiding, telepaths all bounce with `You may not do that while you are mortally wounded!`,
  `Your command had no effect.`, or (for remote / telepath commands) `{command invalid or not
  allowed}`. Client engines that keep firing commands in this state accomplish nothing but noise — a
  dropped / mortally-wounded local player must suppress engine command output until healed / aided.
- **The drop line — party-side and self.** When a character drops, everyone in the room (the party
  included) sees `<name> drops to the ground!`; the dropped character sees it with their **own** name
  (observed: `Raijin drops to the ground!`). That line is the party-side signal a member has gone
  down. The drag, once someone starts it, prints `<leader> is dragging you around.` to the dragged
  character on each of the dragger's moves (observed: `Fujin is dragging you around.`).
- **Drag is a manual leader command, not automatic.** A dropped ally is only dragged when the party
  **leader types `drag <name>`** after seeing the drop line — nothing drags them on its own. Dragging
  merely relocates the still-mortally-wounded body; it does **not** revive them or restore party
  membership.
- **Reviving a dropped ally (leader-side reaction).** A dropped ally sits at 0 HP or below and can't
  act for themselves — they must be brought back by **`aid <name>`** and/or a **heal** that lifts
  their HP above 0. So a party leader watching `<member> drops to the ground!` should **aid and heal
  that member** (drag is a separate, optional relocation choice, not the rescue).
- **A dropped ally leaves `par`.** Once a member drops they no longer appear in the party's `par`
  roster (`par` lists live membership only). Their vitals therefore stop refreshing from `par` — so
  tracking a dropped, then partially-recovered ally's HP needs an out-of-band poll.
- **`@health` telepath polls a member's vitals** *([CONFIRMED])*. Sending an ally a telepath
  `@health` triggers their client's @health responder to reply with their current HP / MA — an
  out-of-band way to read a member's health when `par` won't show it (e.g. after they've dropped off
  the roster).
- **A name-targeted heal still lands on a dropped ally who's been aided** *([CONFIRMED])*. Even though
  an aided-but-still-dropped ally isn't in `par` anymore, a heal cast **at them by name** still
  reaches them, so a party healer can keep topping them up until they fully recover / rejoin.
- **Recovery to positive HP does NOT auto-rejoin the party — a re-invite is required** *([CONFIRMED])*.
  Because the drop removed the character from the party game-side, bringing them back above 0 HP (via
  `aid` + heal) restores their ability to act but **not** their membership. The **party leader must
  `invite <name>` again** to pull them back into the group; until then the recovered character is solo
  even though they're standing right there. This holds both ways: when the **local** character recovers
  from a self-drop, the client must NOT resurrect the wiped roster — it waits for a real
  follow / `par` signal (which only arrives after the leader's re-invite); when a **leader** revives a
  dropped member, the rescue sequence is `aid` + heal **then** `invite <name>`.
- Client reaction (party healer, self is a member with party heals): treat a member's drop as a
  **wait condition** — pause farming / movement to stay with them — and, once they've been **aided**
  back above 0, keep **healing them by name** despite their absence from `par`, polling their HP
  periodically via an `@health` telepath until they recover, then (if leading) **re-invite** them.
  (Implemented in `AllyDroppedHandler`: asserts `MovementCoordinator.AllyDownGate`, sends
  `aid <name>`, exposes the aided ally to `CastingDirector`'s downed-ally heal category, polls
  `@health`, releases on full-HP reply / rejoin / rescue timeout, and re-invites when leading. Its
  own recent-leader memory recognises a dropped leader that a leader-disconnect already wiped from
  the roster.)

## Looking at a monster — coarse wound bands

**`look <monster>` reveals a wound band, never a number** *([CONFIRMED])*
- A player look prints a bracketed `[ Name ]` header and ends `He is unwounded.`; a **monster** look
  has **no header** — the monster's name is the first response line, prose follows, and the **last
  line** is `(It|He|She) appears to be <wound>.`. The `appears to be` phrasing is monster-exclusive
  (players read `is unwounded`), so it never false-matches a player look. The server echoes the typed
  command as its own content line (`look ca`) ahead of the name.
- The game only ever states the condition as one of **eight coarse wound bands**, never a number.
  Each band is a **fixed percentage window of the monster's max HP** (from game data), so
  `max HP × band` gives an absolute HP range. Validated live: a **70-HP cave worm** reading
  **heavily wounded** was **35–48 HP** (actual 38). Bands, percentage of max HP, lower bound
  inclusive:

  | Descriptor | % of max HP | 70-HP cave worm |
  |---|---|---|
  | unwounded | = 100 (full) | 70 |
  | slightly wounded | [85, 100) | 60–69 |
  | moderately wounded | [70, 85) | 49–59 |
  | heavily wounded | [50, 70) | 35–48 |
  | severely wounded | [30, 50) | 21–34 |
  | critically wounded | [20, 30) | 14–20 |
  | very critically wounded | (0, 20) | 1–13 |
  | mortally wounded | ≤ 0 (dead/dying) | ≤0 |

  For a band `[lo, hi)`: `Low = ceil(lo·M/100)`, `High = ceil(hi·M/100) − 1` — exactly the integer
  HP values that read as that band.
- **Why it's worth the range and not just a number:** against a **high-HP boss with fast regen /
  self-heal**, the per-round scroll outpaces any attempt to tally HP by counting damage lines, so the
  wound band is the only reliable read of where the boss's "HP gate" sits. (Implemented in
  `MonsterLookParser` → status-bar `Target: min-max`. Name→HP resolution goes through
  `RoomEntityClassifier.ResolveLookedMonsterNumber`, which prefers the monster variant actually in
  the room so shared names / adjective prefixes resolve to the right HP.)

## Movement & navigation

- **[CONFIRMED, capture 2026-07-12] Paradigm-only `rm` command prints authoritative position.**
  On a ParaMud (Paradigm) realm, typing `rm` returns a fixed three-line block, each label
  left-justified with the value padded to a column:
  ```
  Location:      1,1729
  Regen Time:      2m 30s
  Room Illu:      -100 (-100)
  ```
  `Location: <map>,<room>` is the authoritative (map, room) — no guessing needed. `Regen Time:`
  is a duration, `Room Illu:` an illumination pair `<n> (<n>)`. The prompt returns immediately
  after. **`rm` does NOT exist on stock realms** — stock keeps relying on the heuristic position
  tracker. Because `rm` reports the *player's own* position it is correct for followers too (no
  leader/follower divergence). The client keys on the `Location:` line to re-anchor `RoomTracker`
  via `SetLocated`; if that (map,room) isn't in the imported graph, `SetLocated` logs a warning and
  refuses rather than writing a stale anchor.
- **[CONFIRMED]** **A refused ("bonked") move always prints an explicit line and never
  redisplays the room.** When a move command can't be honoured — no exit that way, a shut
  door, an impairment — the game emits a one-line refusal *instead of* a room display. The
  wording varies by the reason for the bonk, e.g.:
  - `There is no exit in that direction!`
  - `You can't go that way.` / `You can't move that way.`
  - `The door is closed.`
  - impairment forms (paralyzed / confused / stunned / dazed / too encumbered / can't see well
    enough to move).

  The player's on-screen room does **not** re-print on a refusal. This is the authoritative
  signal the client keys on: `MovementRefusalDetector` matches these lines and calls
  `RoomTracker.NoteMoveBlocked` (which drops the pending move and re-confirms at the source).
- **Corollary the tracker relies on:** *a room redisplay that still matches the room you moved
  from is never the result of a refused move.* While a move is pending, seeing the source room
  again can only be a **passive re-look** — a combat-clear, a monster/player arrival or
  departure notice, a bare re-glance — carrying no position signal. The tracker therefore
  ignores it and keeps waiting for the move's real outcome (a different room), rather than
  inferring a refusal from the redisplay alone. (A genuine self-loop exit that lands back in
  the same room is a real move with a real room display; it resolves as a normal
  predicted-neighbour match because the exit's target *is* the source, so it is not confused
  with a passive redisplay.)
- **[CONFIRMED]** **A dark room shows no name and no exits — traversal is inferred from the
  absence of a bonk.** A room too dark to see in replaces the *entire* room display (name,
  `Obvious exits:`, `Also here:`) with a single line — `The room is very dark - you can't see
  anything.`, or in a considerably darker room `The room is pitch black...`. **Every** dark
  room emits the same line, so the line itself carries no position signal. But combined with
  the bonk rule above it makes traversal deducible: once we send a move into the dark, **no
  bonk line means the move succeeded** — we advanced into the room the sent direction leads to.
  The tracker keeps position by projecting that direction onto the current room's graph edge
  (`RoomTracker.NoteDarkRoomEntered`): when the pending move resolves to a known neighbour it
  advances there; when the edge is unmapped it holds the last position (stays Pending) rather
  than guessing. Only the *very dark* / *pitch black* forms starve the display this way — a
  normally-lit room always prints its name + exits.
- **[CONFIRMED, capture 2026-07-11]** **A move made while *blinded* succeeds but starves the room
  display, printing only `You are blind.`** — same shape as the dark-room case, but it's the
  player who can't see, not the room. A blinded player who sends a move gets no name, no
  `Obvious exits:`, no description — just the single line `You are blind.` (period) — yet the
  move **traverses** (party followers are dragged in the sent direction). Distinguish three
  lines that all mention blindness: the **onset** `You are blind!` (exclamation, applies the
  Blinded flag), the **move-succeeded** `You are blind.` (period, starves the display), and the
  **refusal** `You can't see well enough to move.` (a bonk — the move did *not* happen, caught
  as an impairment refusal). Only the period form drives dead-reckoning: `RoomTracker.NoteBlindMove`
  advances along the pending move's mapped edge just like `NoteDarkRoomEntered`, but leaves
  `IsInDarkRoom` untouched (carried light can't cure blindness, and the dark-room attack-line
  combat path must not switch on). Verified from a live capture: `:s` → `You are blind.` →
  `Suijin walks into the room from the north.` (the party followed south) with no room render;
  the map had frozen at the source room until this path landed.
- **[CONFIRMED, capture 2026-07-11]** **A water crossing keyed `borrow skiff` is a *free*
  text-exit ferry, not an item gate.** At a shore room the exit is a Text exit whose command is
  `borrow skiff`; sending it prints `You climb into one of the skiffs, and row to <place>.` and
  lands the player in the far room (e.g. Silvermere at 1/2335). It costs nothing — the capture
  crossed with `Gold: 0` — so it's a *borrow*, distinct from the buy-a-raft `(Item: N)` carry
  gates the route picker weighs elsewhere. The client crosses it like any other text exit
  (`RoomTracker` Confirmed → Pending on the sent command; the walker resolves the Text exit's
  deterministic target), so `borrow skiff` must be treated as a plain traversal command, never
  a purchase or a carried-item requirement.
- **[CONFIRMED]** **A party follower is dragged one room per leader step, announced by
  ` -- Following your Party leader <dir> --`.** Movement is leader-driven: when the party
  leader walks, the game moves every follower one room the same way and prints this line
  immediately *before* the follower's new room display. The follower issues no movement command
  of its own (`PartyFollowerMovementGate` holds its engines), so this line is the **only** signal
  that a dragged follower moved. The client keys on it (`FollowMoveObserver` →
  `RoomTracker.NoteMoveSent`) to stay located; without it the tracker keeps its old anchor, reads
  every new room as a mismatch and falls to Lost within a few rooms. The direction is the long-form
  word the game prints (`north`, `northeast`, `up`, …). Verified from a live follower capture walking
  Darkwood Forest (northeast / east / southeast / southwest / south drags).
- **[CONFIRMED]** **`par` output must never be read as a room name.** The party-list command replies
  with a fixed block whose **first line is `You are following <leader>.`** (the follower's follow
  status), then `The following people are in your travel party:`, then one indented roster row per
  member (`<name>  (<class>)  [K/M: N%] [H: N%]  - Frontrank/Backrank`). A follower's party tracking
  polls `par` constantly, so this block routinely lands in the room-display buffer just before a
  dragged room. `You are following <leader>.` renders in the **same bright cyan** the room title uses,
  so the colour-anchored room-name detector will grab it unless the `par` lines and the drag line are
  treated as block boundaries. (`RoomDisplayParser.PartyChatterBoundaryPattern` does this — the room
  the follower lands in is displayed immediately after ` -- Following your Party leader <dir> --`, so
  that drag line is the natural boundary.)
- **[CONFIRMED]** **Hidden/foliage exits drag the follower with no direction.** Some Darkwood Forest
  exits are text-only: the leader prints `<leader> shoves aside the foliage, and disappears among the
  trees.` and the follower is pulled through with `You push through the dense foliage, and walk onto a
  small path.` — **no** ` -- Following your Party leader <dir> --` line and **no** cardinal direction.
  The follower's room changes but there is nothing to feed `NoteMoveSent`, so the tracker sees the new
  room as a mismatch and must recover via replay/candidate resolution rather than a predicted step.
- **[CONFIRMED]** **A CMD-driven room teleport splits the party — every member must fire it
  themselves.** Some rooms carry a command-triggered teleport in the room's `CMD` → TBInfo action
  chain rather than as a directional exit — e.g. Slum Street (`1/1182`) has TBInfo `#4087`:
  `ring chime:message …:teleport 65 1:message …` / `use chime:…` (a `ring chime` / `use chime` verb
  that teleports the caster). This is **not** a `Text` ("go path") exit where the leader traverses and
  followers are dragged along: a CMD teleport moves **only the one character who types it**, and it
  **breaks the party apart** (the teleport removes the mover from the group). So a leader taking a
  party through one must:
  1. relay the verb to the whole party first — `@party ring chime` — so every member's client fires it
     and teleports, then
  2. fire the verb itself (`ring chime`), and
  3. because the teleport disbanded the party, **re-invite every member and wait for them to rejoin**
     before continuing the route.
  **Arrival ordering [CONFIRMED, capture 2026-07-10]:** the leader teleports *first* (`You ring the
  chime…` → `You find yourself…elsewhere.`), then each relayed follower materialises a beat later,
  one per line: **`%name% appears in a blinding flash of light!`** (the generic teleport/recall
  arrival line for another player entering your room — no direction). So a re-invite fired the instant
  the leader crosses races **ahead** of the members' arrival and the server answers
  **`You don't see %name% here!`** — the invite is silently lost and that member is left out of the
  reformed party. The re-invite for each member must therefore wait until that member is observed in
  the room (their `appears in a blinding flash of light!` line, or an `Also here:` listing if they
  landed ahead of the leader). A member whose invite lands after arrival rejoins cleanly
  (`You have invited %name% to follow you.` → `%name% started to follow you.`).
- **[CONFIRMED, capture 2026-07-10]** A follower client that receives `@join` while it is **already
  following someone** answers the telepath with **`I'm following someone; denied.`** — `@join` is not
  idempotent against an existing follow. This surfaces as a downstream symptom when a reform re-invite
  was lost (above): the leader's `@join` nag then telepaths a member who never dropped their follow
  state, and the join is refused. Landing the re-invite at the right time (post-arrival) avoids the nag
  path entirely.
- **[NEEDS CONFIRMATION]** The believed general rule (user's inference, not yet verified across all
  cases): a teleport driven by a room **`CMD`** (TBInfo chain) splits the party and needs each member
  to execute it (→ `@party` relay + re-invite/wait), whereas a teleport/traversal that is **exit-driven**
  (a `Text` exit like `go path`) needs **only the leader** to execute it and is party-safe (followers
  follow normally). Confirm before extending the split/re-invite behaviour to teleport shapes other than
  the `ring chime` CMD case above.
- **[CONFIRMED]** **`look <dir>` peeks the adjacent room with a full room display, but the player never
  moves.** Looking into an exit (`look north`, `l e`, `peer …`) renders the neighbouring room exactly
  like walking in would — its title, its `You notice … here.` item/cash survey, and its `Also here:`
  monster/player list — yet the player stays put. This is a *preview*, not an entry, so any
  room-entry automation keyed on the room display (auto-get items, cash pickup, combat engage) must be
  suppressed for it — otherwise the client fires `get`/attacks at a room it isn't standing in (the
  reported bug). The client arms a short suppression window on sending the look (`RoomTracker.NoteLookSent`);
  the display consumers that run *before* the `Obvious exits:` line poll `IsPeekSuppressed()` to skip
  the peeked room, and the window is consumed when `NoteRoomObserved` fires on the exits line. The
  player's *own* room is unaffected: walking in for real re-renders the room outside the window and the
  automation runs normally.
- **[CONFIRMED]** **Some rooms harm you on entry unless you carry (or wear, or drink) a protective
  item — either exit-gated or room-spell-gated.** Encoding fully decoded off the 1.11p data set below.
  There are TWO gate locations (exit vs room-spell) and, within room-spells, THREE distinct
  protection encodings.

  **A. Exit-gated** — the exit string itself carries the modifier; already parsed:
  - `Item: (item#)` → `RoomExit.KeyItemId` + `RoomExitHint.Item`. Traversal needs the item in the pack.
    Examples: `6/79 → 6/80` needs *rope and grapple* (item 191); `6/1549 → 6/1550` needs *climbing
    harness* (item 930).
  - `Level: X to Y` → `MinLevel`/`MaxLevel`. Example: `12/2369 → 12/2371` needs level 50+.
  - A level-restricted *action* can also be `CMD`-driven (TBInfo), not a Spells hazard — e.g. `17/2854`
    (`CMD:4328`), a min-level gate expressed as an action chain, not `Room.Spell`.

  **B. Room-spell-gated** — the room carries a cast-on-enter spell (`Room.Spell` = a record number into
  the Spells table). 3981 rooms carry an entry `Spell` but only ~82 distinct spell numbers are used,
  and most are benign (light/ambiance/message). The hazardous ones use one of three shapes:

  1. **Direct-damage spell, negated by an item's `NegateSpell-N`.** The entry spell has `Abil 1`
     (Damage) directly, and may chain a death-timer via `Abil 151` (EndCast → follow-on spell).
     Protection is a held/worn item whose `NegateSpell-0..9` list (an Items.json field) contains the
     spell number. Worked example — the underwater/frozen passage:
     - `6/1139` `Spell:511` "freezing water" = `Abil-0 1`(Damage) + `Abil-2 151`(EndCast→**512**
       "holding breath", `Dur 25`). Spell 512 in turn `151`(EndCast→**513**) — that is the **death
       timer**: hold-breath runs 25 ticks, then 513 drowns you.
     - `8/647` Black Moat `Spell:453` "black water" = `Abil-0 1`(Damage), constant chip each entry.
     - Protection: **gnomish fish-helm (item 929)**, `NegateSpell = [512, 513, 514, 453]`. Worn, it
       negates the drown chain (512/513/514) and the black-water damage (453). You still take the minor
       direct 511 chip but never drown.
     - Timer cancel on exit: the `6/1139` up-exit is `(Cast: pre-516, post-0)` → spell **516**
       `151`(EndCast→**515** "stop drowning"), and 515 `153`(KillSpell) **512** & **513** — leaving the
       water cancels the drown timer. (`Cast: pre-N` = cast spell N *before* moving through the exit.)

  2. **TextBlock action guarded by `failitem <itemNum>`.** The entry spell has `Abil 148`(TextBlock →
     a `TBInfo.Number`); the TBInfo `Action` is a colon-separated command chain. A leading run of
     `failitem N` tokens before a harmful `cast <spell>` means **"if you HOLD any listed item N, abort
     the chain (safe); if you hold none, fall through to the damage cast."** Worked example — Silver
     River: `Spell:753` → `Abil-0 148`(TextBlock **2750**). TBInfo 2750 `Action`:
     `failitem 690:failitem 691:failitem 1181:message 2096:cast 754`. Items 690 *log raft* / 691
     *wooden skiff* / 1181 *silverbark canoe* are the boats; holding any one aborts before `cast 754`.
     (`failitem` is used 139× across TBInfo — many are quest "don't re-give" guards like
     `failitem 622:giveitem 622`; only the ones ending in a harmful `cast` are hazards.)

  3. **TextBlock action guarded by `checkspell <spellNum> <tbTarget>`** (a buff check, not an item
     check). `checkspell S T` = "if effect/buff S is active, branch to TBInfo T (safe); else fall
     through (damage)." Worked example — Scorching Desert `12/1047` `Spell:683` → TextBlock **2653**:
     `checkspell 711 2654:random 2655`. Buff 711 "waterskin" (`Dur 600`) is conferred by **using** the
     *waterskin* (item 283, `Abil 43` CastsSp→711, 3 uses). So the protection is "carry the waterskin
     and re-drink periodically to keep buff 711 up." We can't guarantee a buff stays up mid-walk, so
     for routing treat this as "carry the source item + auto-use on entry," else gate/ask.

  - **Routing takeaway:** a room is *safe to route through* if, for its `Room.Spell` hazard, the
    player satisfies the protection — holds a `failitem` item, wears/holds an item that `NegateSpell`s
    the damage/timer spell (or its EndCast follow-on), or carries the buff-source item for a
    `checkspell` gate. Otherwise the node is hazardous: avoid it, or offer acquire/ask, same as an
    item-gated exit. Detecting a hazard therefore needs: (i) read `Room.Spell`; (ii) walk its
    `Abil/AbilVal` for a direct `1`(Damage) or `151`(EndCast) chain, and for `148`(TextBlock) parse the
    TBInfo `Action` for `failitem` / `checkspell` before a `cast`; (iii) resolve protective items via
    Items `NegateSpell-N`, `failitem` item ids, and `checkspell` buff-source `CastsSp` items.
- **[CONFIRMED]** **A cross-room multi-action exit opens for a timed window (~3–5 min) after its
  action(s) are performed, and each action's server response is unique + not in the game data.**
  A `(Hidden, Needs N Actions, {any|specific} order)` exit unlocks by issuing the listed command(s)
  from the named room + exit direction. The action room can differ from the room the exit lives in
  (the "cross-room" case): e.g. pull a lever in room A to open an exit in room B. Confirmed behaviour:
  - **Persistence.** Once the required action opens an exit, that exit **stays open for a set window —
    roughly 3–5 minutes — that is NOT encoded anywhere in the game data.** Long enough to walk from the
    action room to the exit room and cross without racing a re-lock.
  - **Specific-order across rooms.** For "Needs 2 Actions, specific order," performing action #1 (in its
    room) stays satisfied through the same ~3–5 min timer; you then walk to action #2's room and perform
    it, which opens the target exit, and *that* exit then stays open another ~3–5 min. So the sequence is
    tolerant of the walk time between steps — no tight contiguous-run requirement.
  - **Confirmation is unmatchable.** Each unlock action **does** produce a visible server response, but
    the wording is **different per action** and those TextBlocks are **not shipped in the game data**, so
    the client cannot await a known confirmation string. Treat each action command as **fire-and-forget**:
    send it, don't wait for a specific reply, then proceed to the next step / the cardinal.
  - **Walker takeaway:** walk-to-action-room → send the command(s) in `StepNumber` order → walk-back to
    the exit's room → send the cardinal. The generous open window makes normal walk distances safe; do
    not gate on a data-supplied timer (there isn't one) or on parsing a confirmation line.

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

## Currency & cash

- **[CONFIRMED]** Five denominations, each with its own full coin name:
  **copper farthings**, **silver nobles**, **gold crowns**, **platinum pieces**, **runic coins**.
  The **runic** coin noun can be **renamed per BBS** (a realm may call its top denomination
  something else); the other four are stable across the target realms.
- **[CONFIRMED]** Value ladder (in copper): 1 silver = 10, 1 gold = 100, 1 platinum = 10 000,
  1 runic = 1 000 000. Wealth is consolidated in copper farthings (the game's `Wealth:` line).
- **[CONFIRMED]** **Toll exits gate on total wealth, not a specific coin.** A room exit tagged
  `(Toll: N)` in the map data requires the crosser to carry a **wealth value of `N × 100`**
  (copper farthings — the same consolidated `Wealth:` figure), held **on them** (carried coin, not
  banked). The refusal line reads `You do not have enough to cover the toll of N gold crowns.` —
  but "N gold crowns" is just how the message phrases the copper-value bar (`N` gold = `N × 100`
  copper), NOT a demand for that coin specifically: any mix of denominations totalling `N × 100`
  copper-value passes. So affordability is `TotalCopperValue >= TollGold * 100`. The check is
  **per-crosser**: every party member needs their own `N × 100` on hand, and a member who can't
  cover it is refused at the gate and left behind while the rest pass.
- **[CONFIRMED]** **Gating a party's route through a toll / level exit.** Because a toll is
  per-crosser, a leader routing the party must confirm **every** member can pay before taking the
  route:
  - **Toll:** poll the party with **`@wealth`** (each member's client replies with their wealth,
    same round-trip shape as `@health` / `@level`). If **all** members reply AND each can cover the
    toll (`wealth >= TollGold * 100`), the route may use the toll room; if **any** member can't
    cover it (or doesn't reply), **avoid that toll room for this passing**. Wealth changes
    constantly (loot / spend), so it's polled fresh at planning time rather than cached. (This half
    is now implemented: `MovementFilter.IsTollGateBlocked` + `PartyWealthProbe` / `PartyWealthTracker`.
    Unlike the level half — which keeps every member's level warm on each roster change — wealth is
    **demand-polled**: `MinWealth` fires the `@wealth` probe only while BFS is evaluating a toll exit,
    and a follower with no fresh reading gates the toll, so the first plan routes around it while the
    probe warms up.)
  - **Level:** use the member level already **stored in game data** (each player's recorded level);
    only when it's suspected stale, **re-poll in the room with `@level`**. A member outside the
    exit's `(Level: MIN to MAX)` window means the party routes around that exit. (This half is
    already implemented: `MovementFilter.IsExitBlocked` + `PartyLevelProbe` / `PartyLevelTracker`.)
- **[CONFIRMED]** The **keyword** the client keys policy/value on is the denomination-defining
  first word (`copper`/`silver`/`gold`/`platinum`/`runic`); the second word is the flavour coin
  noun (`farthings`/`nobles`/`crowns`/`pieces`/`coins`). Some lines carry only the keyword,
  others the full pair — don't assume one form:
  - **Get** command sends the bare keyword: `get 6 silver` (never `get 6 silver nobles`).
  - **Corpse loot** drops name the bare keyword: `6 silver drop to the ground.`
  - **Pickup confirmation** names the full coin **and carries NO trailing period**:
    `You picked up 6 silver nobles` (singular `You picked up 1 silver noble`).
  - **Drop / stash confirmations** name the full coin **with** a trailing period:
    `You dropped 5 gold crowns.` / `You hid 219 copper farthings.`
  - **Room survey** lists the full coin: `You notice 56 silver nobles, 198 copper farthings here.`
- **[CONFIRMED]** Item vs. coin disambiguation is by verb + shape. An **item** get is
  `You took <item>.`; an item drop is `You dropped <item>.` — the drop/hide verbs are **shared**
  with coins, so a colour-adjective item (`You dropped a silver key.`) is told apart from coin only
  by the trailing **coin noun** (`nobles`/`farthings`/…) and a numeric count. `You picked up …` is
  coin-exclusive (items never use it).

## Party

- **[CONFIRMED]** Party size: minimum 2, maximum 6.
- **[CONFIRMED]** Losing the leader disbands the whole party — whether the leader **disconnects or
  dies**. No grace-window auto-invite for a lost leader; on the leader's own death the party is gone
  by the time they respawn in the graveyard.
- **[CONFIRMED]** **Training (`train` / `train stats`) is a realm excursion — it briefly drops you
  out of and back into the realm**, emitting `<Name> just left the Realm.` then `<Name> just entered
  the Realm.` to everyone in the room. Its party effect matches who trained: a **follower's** train
  drops only that follower (same as a disconnect — removed server-side, requires a fresh leader
  invite to rejoin; they do **not** auto-rejoin on return), while the **leader's** train disbands the
  whole party (leader-loss rule above — the leader sees `You are not in a party at the present time.`
  on return). Consequence for automation: route `<Name> just left the Realm.` through the same
  member-drop correlation as a disconnect so a trained follower is stamped into the reconnect grace
  window and auto-re-invited on their `just entered the Realm.` — and members who train at staggered
  times each re-invite as they individually re-enter within the window.
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
- **[CONFIRMED]** A party member sitting down to rest is announced to everyone else in the room as
  **`<name> stops to rest.`** (`<name>` is the given name). The actor's own view uses a different
  verb form (`You stop to rest.`), so the third-person line never matches the resting player's own
  row. Used to flip `PartyMember.Resting` the instant it's seen, ahead of the 5-second `par` poll,
  so a follower can mirror the leader's rest immediately. *(The equivalent meditate-observation line
  is not yet confirmed — do not guess it.)*
- **[DESIGN]** *(user directive, 2026-07-11)* Rest-to-use-the-wait: when the party **leader** is
  `@wait`-held and **not poisoned**, the leader rests (or meditates) to use the forced downtime,
  until the wait clears. A **follower** that sees the leader rest/meditate rests/meditates too —
  **unless the follower is poisoned** (poison ticks break rest and waste the downtime). The normal
  below-threshold rest is unaffected by poison; only these two downtime-rest paths gate on it.

## Talk / chat

- **[CONFIRMED]** Talk modes (say / talk-fast / slow) differ **per realm** — that's game
  configuration, not a client bug. The keyboard period is a say-precursor and stays unbindable.

## Shop prices — buy & sell *([CONFIRMED] — extracted from the reference client)*

An item's cost is derived from its MDB `Price` + `Currency`, the shop's `Markup%`, and the
buyer's Charm. Charm 50 is the neutral "retail" point (no discount, no surcharge); a Charm of 0
in the data means "unknown," so the client prices unknown Charm at 50.

- **Base value → copper.** `copper = Price × {Copper:1, Silver:10, Gold:100, Platinum:10000,
  Runic:1000000}` (Currency codes 0–4). All the math below is in copper; the display then
  reduces to the friendliest denomination that keeps the value ≥ 10 (or copper when < 100).
- **BUY (per shop; identical formula in both realms).** Markup first, then charm:
  `buy = baseCopper + Fix(baseCopper × Markup%/100)`; if Charm > 0,
  `buy = (1 − ((Fix(Charm/5) − 10)/100)) × buy`. (`Fix` truncates toward zero.) Charm below 50
  discounts, above 50 marks up, exactly 50 is retail.
- **SELL (ignores markup → same at every shop for a given charm).**
  - **Stock:** `sell = Fix((Fix(Charm/2) + 25) × baseCopper / 100)`.
  - **Paradigm/GreaterMUD:** `sell = (baseCopper/2) × (1 + Fix((Charm − 50)/5)/100)`.
- **Charm no-op.** Charm 0 or exactly 50 leaves BUY at retail; the two SELL branches both land on
  ~half base at Charm 50.
- The reference client wraps charm-scaled totals above 4,294,967,295 copper (a legacy 32-bit
  overflow bug); the client deliberately does **not** replicate that wrap.

## Shop stock & restock

- **[CONFIRMED]** Each shop carries a **fixed list of items it can stock** (the Shops table's
  Item-0..19 slots). Every stocked item has one of two replenishment behaviours:
  - **Restocking** — regenerates on its own, a **percentage chance over a time period**, so it
    trickles back into stock without player involvement.
  - **No-stock** — never spawns on its own; the shop only has one to sell **if a player sold one to
    that shop**. Player sells are what seed a no-stock item.
- **[CONFIRMED]** **One item per command.** `buy <item>` and `sell <item>` each transact exactly one
  unit. Selling ten daggers means sending `sell dagger` ten times; there is no quantity argument.
- **[CONFIRMED]** Sell nets money by **shop + character charm**; buy takes the item's **stock price**
  with a **charm-based markup or discount** — both already formalised under *Shop prices* above.
- **[CONFIRMED]** **Chests.** Some monsters drop a `chest`; `open chest` **dumps a set of random
  items straight into inventory** that the player does not get to choose. This is the case AutoDiscard
  exists to clean up (drop the unwanted dumped items down to the keep band).
- **[CONFIRMED — verified against the 1.11p / Paradigm / Euphoria data, 2026-07-10]** **A chest's loot
  table is data-driven through a three-hop chain.** A container is `Items.ItemType == 8`. Its
  `open` behaviour is an ability pair `Abil == 43` (CastSpell) whose `AbilVal` is a **Spells** row; that
  spell carries `Abil == 148` (castsp) whose `AbilVal` is a **TBInfo** row. That top TBInfo entry's
  `Action` is a **single colon-separated directive line** — `message N` (flavour, ignore), `giveitem I`
  (a **guaranteed** drop), and `random T` tokens. **Each `random T` token is one independent draw** from
  weighted table `T`; the token is **repeated once per draw** (oak chest = `random 898` ×3 + `random 874`
  ×3 = six draws). A weighted table's lines are `threshold:directives`, the thresholds **cumulative**
  (per-bracket chance = `thisThreshold − prevThreshold`, tables normally ending at 100); the selected
  bracket runs its own directives — `giveitem I`, a nested `random M` (a sub-draw, possibly repeated
  within the bracket), or `message`/`failitem`/`price` (no item). **`failitem` yields nothing** (a dud).
  The **per-item drop chance** is therefore *at-least-once across all draws* — `1 − ∏(1 − p_draw)` — and
  the **item count** a single open yields is fixed by the number of draws (a bracket that only messages
  or fails contributes 0, so min ≤ draws ≤ max). Chests **do** drop coins in-game, but the loot tables
  in the imported data carry **no `givecoins` token in any installed set** — the coin amount isn't
  encoded, so it can't be derived from the data (the readout shows items only).
- **[CONFIRMED — verified against the 1.11p Shops table, 2026-07-10]** **Trainers carry a level band.**
  A training room is `Shops.ShopType == 8`; its `MinLVL` / `MaxLVL` fields are the **level range it can
  train** and `ClassRest` the single class it serves (a `Classes` row, `0` = any class). The range is
  one contiguous band per shop — the schema has no way to express a gap, so a trainer never splits into
  multiple bands. A trainer **can also stock items** (the Bard Training Room sells songsheets, the Thief
  Training Room lockpicks) — same 20-slot stock table as a merchant — so a training room is a trainer
  *and* a merchant at once, not either/or.
- **[CONFIRMED — verified against the 1.11p Shops table]** Each of the twenty stock slots is **five
  fields**, not one: `Item-N` (item id), `Max-N` (the shop's stock **cap** for that item), `Time-N`
  (restock **period**), `Amount-N` (units replenished per period), `%-N` (restock **chance** per
  period). So the restock rate is fully data-driven. In the shipped set `%-N` splits cleanly: **100**
  = always restocks (344 slots), **0** = never self-restocks → the **no-stock** items that only exist
  in stock when a player sold one to the shop (330 slots), everything between = a probabilistic
  trickle (e.g. 35 / 25 / 5). `ShopType` 10 is the ordinary buy/sell merchant (7 = bank, 8 = trainer);
  `Markup%` is the buy markup fed to *Shop prices* above.
- **[CONFIRMED — MMUD Explorer Shops tab rendering, 2026-07-10]** `Time-N` is in **minutes**. The
  reference client renders each slot's restock in a **Regen** column as `<%-N>% for <Amount-N> per
  <Time-N humanised>` — humanising the minutes into `10m`, `2h` (120), `4h` (240), `12h` (720), etc.
  A `%-N = 0` slot renders as **`no regen`** regardless of its `Max-N` (the cap still shows in its own
  column, but nothing spawns on its own). The reference's stock table columns are `# | Name | Max |
  Regen | Cost`, Cost being the buy price at the chosen Charm with `Markup%` applied.
- **Data-model gap for the loot feature.** `ShopStockIndex` today reads only `Item-N` (item → shops
  that *can* carry it — the candidate list). AutoBuy/AutoSell that reason about real availability need
  `Max/Time/Amount/%-N` read too; but since live stock count isn't knowable from static data, the
  engines should treat the index as "shops capable of stocking X" and confirm off the **live buy/sell
  result** (a `%-N = 0` item may simply be out until someone sells one).

### `list` — live shop stock readout *([CONFIRMED] 2026-07-10, in-game capture)*

In a shop, `list` prints a three-column table — this is the **live** stock, so real availability *is*
readable at runtime (parse `list`; don't predict from the static `%-N` restock data):

```
The following items are for sale here:

Item                    Quantity        Price
-----------------------------------------------
torch                   250             Free
lantern                 40              4 gold crowns
rope and grapple        56              10 gold crowns
iron ration             430             10 silver nobles
crowbar                 35              6 gold crowns (You can't use)
glass jug               5               2 gold crowns
```

- **Item** = the name to feed `buy <item>`. **Quantity** = current stock count. **Price** = formatted
  currency (or `Free`), with a trailing **`(You can't use)`** suffix when the character's class / stats
  bar the item from being *used*. This suffix is **informational only** — it does **not** gate auto-buy.
  If the user flagged the item AutoBuy, buy it regardless; the player may want it for a mule, a party
  member, resale, or a quest. User intent (the AutoBuy flag) always wins over the usability hint.

### Buy / sell result lines *([CONFIRMED] 2026-07-10)*

| Event | Line |
|---|---|
| Buy OK | `You just bought <item> for <amount> <currency>.` |
| Buy — free item | `You just bought <item> for nothing.` |
| Buy — can't afford | `You cannot afford <item>.` |
| Sell OK | `You sold <item> for <amount> <currency>.` |
| Sell — worthless | `You sold <item> for 0 copper farthings.` |
| Sell — shop refuses | `You cannot sell <item> here.` |

### Auto-buy / auto-discard band semantics *([CONFIRMED] 2026-07-10, user design)*

- **Auto-discard, no Min/Max band set → discard *all*** of that item (drop every copy).
- **Auto-buy, no band → buy as many as affordable**; but when the user first ticks Auto-buy on in the
  item-edit dialog, **default `MaxToGet` to 10** (they change it from there). So a freshly-flagged
  auto-buy item is bounded at 10 by default, never unbounded-by-accident.

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
| Local player death (lives readout, slow / normal) | `You now have N lives remaining.` |
| Local player death (DoT / no named killer) | `You have been killed!` |
| Miracle-save lives readout (a death, still has lives) | `You have N lives left.` |
| Local player slain (attacker named) | `You have been slain by <killer>.` |
| Party member / other player killed in room | `<Name> has died.` |
| Character drops (0 HP, party/room-side; self sees own name) | `<Name> drops to the ground!` |
| Being dragged while dropped (dragged char's view, per move) | `<Leader> is dragging you around.` |
| Action attempted while dropped (rejection) | `You may not do that while you are mortally wounded!` |
| Coin pickup (no trailing period) | `You picked up N <coin>` (e.g. `6 silver nobles`) |
| Coin drop | `You dropped N <coin>.` |
| Coin stash / hide | `You hid N <coin>.` |
| Corpse loot drop (bare keyword) | `N <keyword> drop to the ground.` |
| Room cash survey | `You notice ... N <coin> ... here.` |
| Move refused — no exit | `There is no exit in that direction!` |
| Move refused — blocked way | `You can't go that way.` / `You can't move that way.` |
| Move refused — shut door | `The door is closed.` |
| Room too dark to see (starves name + exits + Also-here) | `The room is very dark - you can't see anything.` |
| Room considerably darker (same starving) | `The room is pitch black...` |
| Incoming mob attack — miss (dark cyan; reveals a mob in a dark room) | `The <monster> <verb> at you` |
| Incoming mob attack — hit (dark cyan; reveals a mob in a dark room) | `The <monster> <verb> you for N damage!` |
| Attacked a target not in the room | `Your command had no effect.` |
| Toll exit unaffordable | `You do not have enough to cover the toll of N gold crowns.` |
