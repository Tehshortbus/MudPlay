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

## Timing & rounds

**Two round lengths** *([CONFIRMED])*
- **Combat round = 5 seconds** (precisely ~5.04s; the client surfaces it as a round "5" for
  human input). This is the cadence of combat lines, and a between-round spell can be cast
  **once per combat round**.
- **Spell round = 3 seconds.** Buff/debuff durations on a player, item durations, and spell
  durations (e.g. a teleport / boat-transit spell) are all counted in **spell rounds** — so a
  duration of `N` rounds lasts `N × 3` seconds. A debuff falls off on the same 3s cadence.
- A spell record's `Dur` field is therefore **spell rounds**: real seconds = `Dur × 3`.
  Boat-voyage length is the sum of the transit spells' `Dur` along the disembark chain, × 3s.
- **[OBSERVED, report paradigm-20260816-222917]** The **real** spell round runs slightly LONG,
  like the combat round is ~5.04s not 5.0 — a 50-round buff (`prev`, protection from evil) was
  observed lasting **~151-152s**, i.e. ~**3.04s/round**, not the nominal 150s. So a "recast within
  N s" slot, which is measured against the buff's **real** remaining seconds, must time off ~3.04s
  or it recasts ~1-2s early. The client keeps the nominal 3s for spell-data displays but uses
  `SpellCalculator.SpellRoundSecondsWallClock` (3.04) for the live recast clock + Buff Watchdog.
- If a specific duration's unit is ever ambiguous, **ask the user** — they can give the correct
  value rather than us guessing.

**Combat tick & exp accrual** *([CONFIRMED] 2026-08-02, user)*
- Combat fires on a **fixed 5-second global tick** (720 ticks/hour), whether or not you're doing
  anything. If you aren't engaged with a monster, the tick is a **no-op** on the engine side —
  you send no combat round out, so nothing can die and no exp is awarded that tick.
- **Engaged** = a monster is in the room AND you sent an attack command at it. Only then does a
  tick land a round.
- **Kill timing is counted in ticks from engagement.** A mob you kill "in 1 round" dies on the
  **next** combat tick after you engaged it; a 2-round mob dies after 2 ticks pass; etc. This is
  why the estimator's "rounds to kill a mob" accepts **decimals** — if some mobs die in 1 round
  and some in 3, the average is fractional.
- **A tick in a room with no (live) monster yields nothing** — no damage out, no exp in.
- **Movement rides the downtime between ticks.** A hop from one lair to the next that completes
  within the ~5s gap re-engages you before the next tick, so it drops **no** combat round — travel
  is effectively free when it fits. Travel only costs exp when a stretch is long enough that a tick
  fires while you're standing in a monster-less room mid-transit. Concretely (user): at **1.0–1.2s
  per step you can cross 4 empty (non-lair) rooms** between lairs without dropping a round; **above
  1.2s it drops to 3** — i.e. ~`floor(5s ÷ step-seconds)` free rooms per hop.
- **Each monster slot in a lair carries its own respawn timer**, tracked independently — separate
  from every other lair, even lairs with the same monster type and size. So a dense multi-lair loop
  desynchronises: with enough slots there's almost always one ready, and the loop runs pinned at the
  720/hr tick cap rather than clearing everything then idling for a synchronised repop.
- Consequence: a perfectly streamlined lair loop keeps a live mob engaged on all 720 ticks, so the
  ceiling is `720 × avg-exp-per-kill` (matches the "720 kills/hour" cave-worm cap). Charging travel
  as wall-clock time *added to* combat (as a naive lap model does) understates a tight loop, because
  in reality that travel overlaps the downtime and doesn't consume ticks. See [[project_nav_obstacle_traversal]]-adjacent
  exp-estimator work.
- **[CONFIRMED, user 2026-08-14] 720/hr is only the *single-target* ceiling, and few loops reach it.**
  It's the max a melee / single-target-spell player can kill (one mob per 5s tick); **rooming (AoE)
  clears a whole room per pass, so it runs ABOVE 720/hr.** Whether a loop approaches its ceiling
  depends entirely on geometry. **In a line (out-and-back / A→B→A) loop you re-cross just-cleared
  lairs on the return — an empty room you simply walk through** (no pause, no fight); with a 30s
  respawn and a sub-30s return you find them still down, so that return walk is dead time that wastes
  combat ticks. So the middle lairs of a line are hit less than a naive "each lair once per lap"
  count, and the end lairs less than the middle. A faithful estimate must **replay the actual room
  order** with per-mob respawn clocks (per "Lair respawn timers" below), not assume a uniform per-lap
  fire rate — that's what the Exp/Hr estimator now does.

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

## Character points (CP) & training

**Where CP comes from** *([CONFIRMED] — user rule, verified against a live level-10 build)*
- **Level 1** grants the race's **BaseCP** pool — **100** for the standard races (the `BaseCP`
  field on the race record; Kang = 100). This is character-creation seed, not a training gain.
- **Training to each level 2+** grants a step that rises every decade:
  - Levels **2–10** → **10** CP each
  - Levels **11–20** → **15** each
  - Levels **21–30** → **20** each
  - Levels **31–40** → **25** each … (+5 CP per decade thereafter)
- The step rises at the **first level of each new decade (11, 21, 31…)**, so a decade *top* pays
  the lower rate: **level 10 grants 10** (not 15) and **level 20 grants 15** (not 20).
- Formula for the per-level training gain (level ≥ 2): `((level - 1) / 10) * 5 + 10` (integer
  division). Total CP on reaching a level, spending none: `BaseCP + Σ gains(2..level)`.
- **A level-10 character who spent nothing has 190 CP** (100 base + 9×10). Confirmed by a
  level-10 Kang who trained straight to 190 spent.

**What CP costs to spend** *([CONFIRMED])*
- Raising a stat one point costs `((currentStat - raceMin) / 10) + 1` CP — it escalates by 1 for
  every full 10 the stat sits above its race minimum. **ParaMUD/Paradigm is uncapped; Stock caps
  the per-point cost at 10.**
- The in-game **Point Cost Chart** states the cumulative form: +10 above base = 10 CP, +20 = 30,
  +30 = 60, +40 = 100, +50 = 150 CP (each is the running sum of the per-point costs above).
- Worked example — Kang (mSTR 55, mAGL 30, mHEA 50) at level 10 with 190 CP: STR 55→99 = 120 CP,
  AGI 30→60 = 60, HEA 50→60 = 10 → exactly 190. The **next** STR point (99→100) costs
  `(99-55)/10 + 1 = 5` CP, which 190 can't afford, so **99 is the real ceiling** at that level —
  a plan must not offer 100.

**The `train stats` screen renders differently per realm** *([OBSERVED] — from a Paradigm capture)*
- **Paradigm** draws a full-screen, cursor-positioned box titled **"Char. Creation"** with a
  side **"Point Cost Chart"** panel (the banner reads `P A R A D I G M`, not `MAJOR MUD`). Because
  it's cursor-positioned, the marker row never completes as a scrolled line until the screen
  tears down — so a marker-gated "menu entered" detector never fires mid-session on Paradigm; the
  realm-independent signal is the outbound `train stats` command itself.
- **Stock** realms render the same screen inline (scrolling text), so the marker row is emitted
  normally.
- **Initial character creation reaches this box with NO outbound `train stats`** *([CONFIRMED]
  2026-08-02, capture `paradigm-20260802-164301`)* — the menu walks class → race → alignment →
  training on its own. So on Paradigm neither signal is available during creation (no command to
  arm on, and the cursor-drawn marker row never emits inline), which leaves the client's arrow keys
  bound to command-history recall — up/down cycle the just-typed given/family names instead of
  moving between stat fields. The only reliable entry signal is **scanning the live screen** for the
  box (the `Point Cost Chart` panel beside the `Char. Creation` / `Character Creation` title, both on
  the top row). The banner text is literally abbreviated **`Char. Creation`** on Paradigm, not the
  stock `Character Creation`.
- **The stat box's first field is the "Family Name" (surname / last name)** *([CONFIRMED] — user
  report)*. The cursor starts there, and a plain Enter advances past it (the auto-trainer replays a
  bare Enter and never edits it). So any automated `<text>\r` that fires while the user is parked on
  the form types into that field: a stray party `par\r` poll overwrites the character's last name with
  "par". This is why every wall-clock / on-demand automated wire send must gate on the realm-
  independent "screen owns the keyboard" signal (the outbound `train stats` command), not the
  marker — which on Paradigm never confirms.
- **The cursor-positioned box LINGERS on screen after the user exits back to the realm** *([CONFIRMED]
  2026-08-05, captures `paradigm-20260805-095320/095546/095653`)*. It isn't cleared when the in-game
  prompt returns — it only leaves once enough new output scrolls it off. So the "box is on screen"
  signal stays true well after the session is over; the authoritative "user is back in the realm"
  signal is the **in-game prompt returning**, not the box vanishing. A screen-scan detector must
  therefore arm on the box's *rising edge* only — a level-triggered re-arm flaps held→resume→held off
  the stale box every feed and holds the movement engine (the "walker stalls after training, moves one
  room per manual `rm`" bug).

## Light sources

- **[CONFIRMED]** `use <item>` readies a light (torch, lantern); `rem <item>` removes it.
  Lights follow the same trade-places rule as `eq` — `use`-ing a new light swaps out the
  current one (if usable).
- **[CONFIRMED, user 2026-08-11]** **`use <light>` only grants the ability to see — it does NOT
  re-display the room.** After lighting in a dark room you must send a **bare carriage return** to
  redisplay: if the light now lets you see, the full room prints (name / exits / `Also here:`,
  revealing any monsters that were standing there unseen); if it's still too dark, the "can't see"
  dark message prints again. This is the ONLY way to discover a *standing* (non-attacking) monster
  in a just-lit room — the dark display never listed it, and `DarkRoomCombatWatcher` only catches a
  monster that *swings* (its attack line). So auto-light must send the bare CR after readying the
  light or it walks past passive mobs (the "lights a torch but doesn't re-check the room" report).
- **[CONFIRMED, user 2026-07-24]** A **readied light is visible to other players** — an onlooker
  sees the lit source the way they see worn gear. So it counts as "shown," not hidden: the `@inv`
  remote report (carried items an onlooker can't see) deliberately excludes the readied light,
  reporting only the pack + key ring.
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
- **[CONFIRMED]** *(2026-07-21, user)* **In a dark room, a distinct display name is a distinct
  enemy — dedupe reveals by name, never by monster record.** A mob can swing more than once per
  round and the dark room re-emits its attack line every round, so four `The cave lizard swings
  at you` lines are **not** four lizards — the client collapses same-name reveals to one entity.
  But `cave lizard` and `small cave lizard` (e.g. we're on one, a partner announces the other)
  are **two separate enemies**, even if they share a Monsters-table record — so the dedupe keys on
  the **name string**, not the resolved monster number; collapsing by record would drop a live mob.
  Two genuinely same-named mobs can't be told apart in the dark, so they read as one until the
  first dies — the survivor's next swing re-reveals it and combat re-engages. Over-count is thus
  impossible and under-count self-heals.
- **[CONFIRMED]** *(2026-07-22, user)* **A dark room's monsters can populate *after* entry, not
  only at the moment you arrive.** A room that reads empty on arrival (no attack line yet) can still
  have a hostile reveal itself a beat later via its first swing. This is why the dark-room settle
  window (`DarkRoomSettleGate`, ~1.0s per dark advance) must hold before the walker moves on — it's
  the only late-reveal guard for a room that looked empty at entry. Empirically a reveal often
  lands at 0ms (synchronous with entry — over a 1-hour hunt all 334 populated rooms revealed at
  0ms), but that is area/condition-dependent, **not** a guarantee: the delayed-populate case is
  real, so the settle window is a justified safety buffer, not dead time. Do **not** trim or
  short-circuit it on the strength of an all-synchronous sample.

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
- **[CONFIRMED]** **Hit-magic (the "magical" to-hit level) only matters on weapons.** It's the
  weapon's magical hit level compared above against a monster's magical defense; nothing else
  consults it. If a non-weapon item (armour / jewellery) carries a hit-magic ability value it's
  inert — the game ignores it. So UI that surfaces the stat (e.g. the Item Finder) shows it on
  weapon rows only and blanks it everywhere else.
- **[CONFIRMED]** **Casting a spell mid-fight drops the auto-attack for that round** — the server
  emits `*Combat Off*` because a cast is a distinct action that interrupts the sustained weapon
  swing. If the target is **still alive** after the cast, the desired behaviour is to **re-attack
  immediately** (as soon as the `*Combat Off*` lands), not wait for the next combat-round tick or a
  manual room re-parse. Confirmed by the user casting a Kai power (`swan`) on a live target: without
  a prompt re-attack the client idled a full round. Applies to a **hand-typed** cast just as much as
  an engine-issued between-round cast — in this realm a spell is cast by typing its cast-code
  (`Spells.Short`) directly (`swan`, `swan rat`), with no `c` verb precursor, so the client
  recognises a manual cast by that cast-code on the wire.
- **[CONFIRMED]** *(2026-08-05, user)* **A spell attack command AUTO-REPEATS server-side every round,
  exactly like a weapon swing — on ALL realms.** You announce the spell once (type its cast-code +
  target); the next combat tick it fires, and it keeps firing each round while the target is present
  and mana suffices, with NO re-announcing. It stops when the target dies. So a spell attack is
  functionally identical to a physical attack — announce once, the server owns the per-round repeat —
  the only difference being the spell-slot gates (mana threshold + `MinManaPerCast`, `MaxCasts`,
  `MinEnemies`). **The client must therefore announce once and NOT re-issue the spell every round**;
  re-announcing a spell the server is already repeating double-fires the round (wasted mana on a live
  mob; a cast at the **corpse** when the round was the kill — "re-nukes the monster that just died").
  The client re-announces ONLY when the best action must change:
  - **Target dies** → announce at the next target.
  - **`MaxCasts` rounds elapsed** → switch to the next cascade action. Scope: **per-target** for the
    single-target slots (normal / alternate attack spell, single-target debuff); **per-room** for the
    two AoE slots (multi-attack, area debuff).
- **[CONFIRMED]** *(2026-08-11, user + fix PR #271)* **Room-wide spells are cast BARE — no target.** The
  two AoE slots (multi-attack e.g. `blad`/dancing blades, area debuff e.g. `stnk`/stinking cloud) hit the
  whole room and must be cast as the bare cast-code (`blad`, `stnk`) — NEVER `blad <mob>`. The server
  treats the targeted form of a room spell as an unknown command ("Combat Off / Combat Engaged" flip-flop,
  no cast). Single-target attack/debuff spells DO take the mob name. Only these two slots omit it.
  - **Current spell unaffordable** (`MinManaPerCast` vs live mana) **or the target is immune** →
    switch down the cascade: alternate attack spell (if configured) → physical main weapon → physical
    alternate weapon (if configured) → can't hit.
  - **An AoE spell's live enemy count drops below `MinEnemies`** → switch to a single-target action.
  - **Room / multi-target** attack spells fire at an empty room and emit a "nothing to hit here" style
    message (exact wording unconfirmed — no test character yet).
- **[CONFIRMED]** *(2026-08-12, user + fix v3.8.3)* **A configured debuff fires BEFORE the attack on
  engage — not a round later.** On entering a room, if a debuff slot is due (area debuff at ≥ `MinEnemies`,
  or a single-target / per-monster pre-attack debuff), the client casts the debuff first, then the attack
  re-announces on the debuff's `*Combat Off*`. The debuff still obeys the **Spells + Ailments spell-type
  priority**: a higher-priority in-between survival cast (heal / cure / buff) that's due wins and fires
  first, and the debuff waits for the next in-between pass. Only when nothing higher-priority is queued
  does the debuff pre-empt the attack. (Before v3.8.3 the attack dispatched immediately on engage while the
  debuff rode the next in-between tick, so the attack always went out first — the reported ordering bug.)
- **[CONFIRMED]** *(2026-08-05, user)* **`MaxCasts` counts combat ROUNDS, not individual casts.** It is
  the maximum number of rounds the client will spend casting this spell at a target — one round counts
  as one regardless of how many times the spell fires that round (e.g. a spell that casts twice per
  round still spends a single round). The spell keeps casting only *while* mana also stays at or above
  `MinManaPerCast`; whichever limit is hit first (rounds spent ≥ `MaxCasts`, **or** mana below the
  reserve) ends the spell for that target and drops to the next cascade action. **Once dropped, stay
  dropped for that target** — a mana-regen tick that lifts mana back above the reserve must NOT flip
  the client back to the spell mid-fight; it commits to the weapon until the monster dies (or the room
  clears). This is a per-target latch, mirroring the observed-immunity latch.
- **[CONFIRMED]** *(2026-08-05, user)* **"You attempt to cast <spell>, but fail." means the spell DID
  cast — mana was spent — but it missed the target (a hit-roll failure), NOT a fizzle and NOT
  out-of-mana.** So an attack spell drains mana every round it repeats whether it lands or misses; a
  "but fail" round is a spent round (mana down, zero damage), not a free retry. The client must not
  treat this line as an out-of-mana / interrupt signal.
- **[CONFIRMED]** *(2026-08-09, user)* **"Your spell has no effect on <monster>." spends NO round — it's
  free.** A no-effect cast (the living-only / immunity mismatch) doesn't consume the combat round, so
  you can cast a *different* spell that same round. The client therefore swaps the attack cascade's
  primary → alternate attack spell **immediately** on the no-effect line, the same round, rather than
  idling until the next ~5s tick (report `paradigm-20260809-162350`: `harm`→`hamm` was losing a round
  because only the weapon fallback swung immediately while the alternate *spell* waited a tick). The
  primary probe itself is still the unavoidable reactive detection (living-only immunity isn't
  pre-emptable from data — see the immunity section below), and the swap is one cascade step per round
  because the alternate's own no-effect can't arrive until it has cast next round.
- **[CONFIRMED]** *(2026-08-12, user — report `paradigm-20260812-200128`, ~6th on this issue)* **The
  single-target attack-spell cascade — the authoritative rules.** With ActionOrder = *Spells first* and
  a Normal + Alternate single-target attack spell configured (e.g. Normal `lbol` MaxCasts=1 / min-mana 75,
  Alt `mmis` unlimited / min-mana 0):
  - **One combat spell per combat round at one target.** A spell may fire multiple times in a round but
    that's still "one spell." You can NEVER announce two *different* attack spells the same round.
  - **A kill always wins over any cascade switch.** When the chosen attack spell kills the target, drop
    that target and re-pick — do NOT fire the OTHER attack spell (normal↔alternate) at the just-dead
    target. A cast at a corpse ("You don't see X here!") is a wasted round. This holds symmetrically:
    normal-kills-then-alt-at-corpse AND alt-kills-then-normal-at-corpse are both the bug.
  - **When the Alternate is considered (single-target only):** only against a still-ALIVE target the
    Normal can't handle — (a) we don't meet the Normal's cast conditions (mana below its floor, or its
    MaxCasts rounds elapsed while the target lives), or (b) the Normal came back "no effect" / immune
    (e.g. priest `harm` vs an acid slime).
  - **Every fresh target re-evaluates the Normal first.** The cascade state (which spell is current, the
    per-target cast count, the mana-drop latch) resets per target — a new mob always reconsiders the
    Normal, re-checking mana (regen ticks in / casts drain it round to round). It must NOT carry the
    previous target's cascade position, or a fresh mob opens on the alternate.
  - **MaxCasts=1 = one FULL round** (the spell actually fired), then switch ONLY if the target is still
    alive — never "announce once then immediately switch the same round before it fires."
  - **Mana fallback under Spells-first:** below the Normal's min-mana → consider the Alt; if mana is at
    or above the Alt's min-mana use the Alt, else fall back to physical. Once dropped off the Normal for
    mana, stay dropped for that target (a regen tick lifting mana back over the floor must not flip back
    mid-fight — the per-target latch).
- **[CONFIRMED]** **Martial-arts strikes (Punch / Kick / Jumpkick) are class-innate abilities, not a
  function of the trained Martial Arts skill.** A class grants a strike by listing its ability id in
  an `Abil-0..9` slot: **Punch = 29, Kick = 30, Jumpkick = 35** (Mystic carries all three at value 1
  across every observed stock + Paradigm set; no other class carries any). The Martial Arts *skill*
  stat can be raised by items/races without unlocking the strikes, so the Character Info combat panel
  gates each strike row on the class ability — not on `MartialArts > 0`.

### Per-monster overlay automation *([CONFIRMED] 2026-07-10, user design)*

Client-side automation policy for the Game Data → Monster overlay flags (not engine
behaviour — how the client's auto-combat interprets the per-monster overrides):

- **DontBackstab** — a monster flagged DontBackstab is never the backstab opener. On the
  opening (armed) round the target picker **prefers the highest-priority non-flagged**
  actionable monster to backstab; a flagged monster is only chosen when **every** actionable
  monster in the room is flagged, in which case the room is **still cleared** — the opener just
  falls through to a normal attack instead of `bs` (never skip the room over the flag).
- **Override Attack / Override pre-attack** — a per-monster attack that **substitutes** for the
  global Combat-tab choice **for that species only**. The **Override Attack** field takes EITHER:
  - a **`Spells.Number`** (resolved to the `Spells.Short` cast-code) — routed through the *Normal
    Attack Spell* rung with its mana floor + per-room cast cap; OR
  - a **raw command verb** ("attack", "bash") sent **verbatim** as the attack verb, forced over the
    whole spell/weapon flow, with **no** rung gating (the server auto-repeats it like any attack
    command). The editor disambiguates on save: a positive integer is the spell id; other text is
    checked against the active set's known spell **cast-codes** — a match (e.g. "turn") resolves to
    that spell's Number and takes the **gated spell rung** (a user who types the code they'd cast
    in-game gets mana/cap gating, not a raw command); only text matching no spell stays a raw command
    (this is what lets `attack` persist; an earlier int-only parse silently dropped it). The
    pre-attack override stays spell-only and occupies the *Single-Target Debuff* rung.
  - **Gate bypass:** when an override is set the client **bypasses the effectiveness gates**
    (observed "no effect" immunity, SpellImmu level-block, and ≥100% elemental resist) — the
    rationale is that a user who hand-picks the attack for a specific monster has done the due
    diligence that it works. So a forced command is **never** second-guessed by the "no effect"
    fallback below. The **physical constraints still apply** for the spell-id form: the rung's
    mana floor, the once-per-target guard (pre-attack), and the override's own per-room cast cap.
  - **Count = per-room cast cap** (spell-id form only). The override's configured count is the cap;
    the overlay documents **null = 0**, so a spell set with **no positive count is treated as
    inactive** and the client falls back to the global slot (likewise if the number doesn't resolve
    to a known cast-code). The command form carries no count — it's active whenever the text is
    non-blank. This "null/zero count ⇒ fall back to global" reading is the client's interpretation
    of the ambiguous count field — **flag for user confirmation** if override behaviour is ever questioned.
  - **0-mana stand-down.** At literal 0 mana nothing that costs MA can land — the server silently
    **no-ops** a cast or a mana-costing command with **no error line** to react to (an earlier build
    kept re-sending a forced `turn` every round while the player stood there getting hit until a
    regen tick). So at 0 mana the client marks spells unavailable (the chooser collapses to
    Backstab/Physical) and, for a forced command that resolves to a spell cast-code, falls back to
    the physical weapon; a genuinely free verb (bash/kick) still fires since it costs no MA.
    Re-evaluated each round, so it resumes the instant mana ticks back up.
- **Attack-command "no effect" fallback.** A spell driven through the **attack-command** slot
  (`NormalAttackCommand = "harm"`, not the attack-*spell* rung) draws the same
  `Your spell has no effect on <monster>.` immunity line, but reaches the wire as a plain command,
  so the spell-slot cascade can't see it. The client falls the COMMAND path back the same way it
  does a physical weapon's "no effect": it marks the species failed vs the normal command (the next
  pick prefers `AlternateAttackCommand`) and re-sends the alternate command this round; with no
  distinct alternate it concedes the species and re-picks. (Report `paradigm-20260809-131642` —
  priest `harm` command vs an acid slime never dropped to `attack`.)

## Neutral monsters & kill-on-sight *([CONFIRMED] 2026-08-15, user)*

A **neutral** monster **never attacks you on sight / never attacks first**. It attacks **only if
you've attacked it** and it's still alive — and once provoked it **keeps attacking every round until
it dies**, even if you `break` and sit there. So a room of un-engaged neutrals is **safe to rest in**;
the moment you engage one, *that* one is hitting you back (no resting mid-fight), but the others stay
passive until you turn on them. (A monster that *does* open on you unprovoked is an **enemy**, not a
neutral — e.g. storm giants; see the aggression rules below. The client models those as the `Enemy`
relationship.)

Client mapping: the per-monster overlay `Relationship` (`Enemy` / `Neutral` / `Friend` / `Flee` /
`Hangup`) drives auto-combat. `Enemy` = engage on sight; `Neutral` = leave alone. The **KillOnSight**
flag (Monster edit dialog, shown only for `Neutral`) makes auto-combat **engage a neutral like an
enemy** — but because neutrals never open on you, the *other* un-engaged neutrals still don't block
resting, so between kills the engine rests/meditates (only when below the rest trigger) before turning
on the next one.

There's one more path onto a passive neutral: **if *you* hand-attack one** (a manual swing or combat
cast), it turns hostile per the mechanic above, so the client marks that instance user-engaged and the
auto-combat engine **takes over finishing it** — treating it like an enemy until it dies (and holding
the walker in the room) instead of stopping the moment you engaged it. It's keyed per-instance by name
and pruned once the mob is gone, so it never leaks onto a freshly-arrived same-named passive neutral;
the *other* un-engaged neutrals stay passive and rest-safe. `Enemy` monsters are unchanged.

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

**Monster-kill message order** *([CONFIRMED] 2026-07-23, bug-report captures)*
- A kill prints in a fixed order: the monster's **death line** (e.g. `The toad croaks in agony, and
  collapses wetly.`) → `You gain N experience.` → `*Combat Off*`, all in the same server flush.
- `*Combat Off*` is **not** a reliable death signal on its own — non-sustaining attacks (thrown
  weapons, KAI pummel, a party member's throws) emit an off/engaged bounce **every strike**, so a
  `*Combat Off*` fires many times per fight with no death. The "exp + Combat Off within a window"
  fallback death is therefore a *weak* heuristic: only trust it for monsters whose specific death
  line isn't in the data. Because a specific death line always precedes its exp, an exp that lands
  right after a specific death belongs to that already-attributed kill and must not also arm the
  fallback — otherwise, with identical-exp mobs dying every few seconds (a swarm), the prior kill's
  exp stays inside the window and the next fight's non-death `*Combat Off*` fires a phantom fallback
  death on it, a beat before the current mob actually dies.
- **The exp line is the earliest reliable per-kill signal during combat** *([CONFIRMED] 2026-08-15,
  user)*. Every kill grants exp, and the exp line lands **before** the kill's `*Combat Off*` (order
  above). So while engaged with a target we've attacked, a `You gain N experience.` line means that
  target just died — recognize the kill on the **exp line**, not the later `*Combat Off*`. Waiting for
  the Off let the round's **alternate** attack corpse-cast: `lbol` kills → `mmis <corpse>` → "You don't
  see X here!" (report paradigm-20260814-230258). This is generic — the exp line is identical for
  every monster.
- **AoE clears the whole room as a burst of exp lines** *([CONFIRMED] 2026-08-15, user — "20 targets
  dead in 1 spell")*. One room spell prints a `<flavor>` + `You gain N experience.` **pair per monster
  it kills**, then a **single** `*Combat Off*` at the end. So exp-line count = kill count.
- **Death messages are arbitrary per-monster flavor** — no shared keyword (a scan of 1035 seed death
  lines: `…a tortured squeak`, `…to the ground`, `…without a sound`, `…a thousand pieces`, `…an
  agonized bellow`, `…in a heap`, most with no death word) and no distinctive colour (they render
  default/white). So a monster death **cannot be recognized by wording or colour generically** — the
  exp line is the generic signal, and our own targeting (`CombatManager.CurrentTarget`) names the mob.
  The per-monster `DeathLine` data was therefore **retired** (v3.16.0): every kill is recognized from
  exp + `*Combat Off*`, and the dead slot is refreshed by the forced room re-display. No per-monster
  death message is maintained anywhere.

**Attributing a kill to a specific monster** *([CONFIRMED] 2026-08-04, user)*
- **Monster numbers are never observable in-game** — the client only ever sees monster *names* on
  the wire. So the reliable way to attribute a death to a specific monster (e.g. "was that the
  boss?") is: the monster **name we were engaged with** (`CombatManager.CurrentTarget`, read live at
  the death) **plus** the death event. This works for the common fallback death too — the fallback
  `exp + *Combat Off*` carries no identity of its own, but it is by definition the death of whatever
  we were fighting, so the engaged name attributes it. Never key kill-attribution on a monster
  number the player can't have seen. (This is what boss-timer kill detection uses.)

## Lair respawn timers & NPC-placed monsters *([CONFIRMED] 2026-08-02, user)*

Two distinct spawn mechanisms, and they respawn on completely different rules. This matters
directly for exp/hr estimation of a loop (how fast a lair refills vs how fast you can lap it).

**Lair mobs — independent per-mob timer keyed to each kill.**
- A room's `Lair` group spawns N monsters with a respawn time `T` (the lair's `AvgDelay` — stock
  exports it in **minutes**, Paradigm/GreaterMUD differs; else the slowest member's `RegenTime`).
  This is the resolution `LairTimerStore` already performs.
- **Each monster carries its own independent clock of length `T`, started at the moment *that*
  monster was last killed** — not a shared lair clock. In a 3-mob, 60 s lair: a mob killed at t=0
  is killable again at t=60; one killed at t=10 is back at t=70. They come back **staggered**.
- To re-kill a mob you must be in the room at or after `(its last kill + T)`. Arriving earlier, it
  simply isn't there yet — being early does **not** let it spawn.
- **Loop consequence:** a lap that returns to a lair with period `P ≥ T` finds it fully respawned
  (full mobs that lap); `P < T` laps into a partial/empty room. So a lair's sustainable exp rate is
  capped at `mobs × exp ÷ T` regardless of how fast you loop — the "respawn-limited" regime — while a
  loop long enough that `P ≥ T` is "travel/kill-limited." The real rate per lair ≈ `min(the two)`.

**NPC-placed mobs — regenerate on entry, effectively no respawn cap.**
- A monster placed via the room's **`NPC`** field (a fixture, distinct from a `Lair` group) with
  `RegenTime` 0-ish **regenerates the moment you (re-)enter the room after killing it** — no timer to
  wait out. These are the classic "rooming" targets (kill as fast as you can fight; bounded by kill
  speed, not respawn). Verified stock examples: slime beast `1/1765` (`NPC=57`, `RegenTime 0`, 250 xp),
  cave worm `1/866` (`NPC=8`, `RegenTime 0`, 100 xp), barmaid `1/311` (`NPC=248`, `RegenTime 1`,
  **0 xp** — an evil-points target, not exp).
- A room can carry **both** an NPC fixture *and* a `Lair` group (cave-worm room `1/866` has `NPC=8`
  plus a lair), so a room's yield is the sum of its NPC target(s) + its lair contribution.
- For a **loop**, an instant mob still yields only once per lap (bounded by lap time); only a
  stay-in-room **rooming** setup kills it every round.

**Boss monsters — a game-limited / long-regen singleton.** *([CONFIRMED] 2026-08-03, user + reports `paradigm-20260803-035136`, `-094657`)*
- A monster is a **boss** if its **`GameLimit` is 1** (only one exists in the game at a time) OR its
  **`RegenTime` is ≥ 1 hour** — and this holds whether it's a **lair** member OR a **placed** monster
  (the room's `NPC` field). A boss can be killed **only as often as its regen timer** — the crowned
  spider (`Number 929`, lair, `GameLimit 1`, `RegenTime 15`) is killable **once per 15 hours** and can
  spawn in **any** room of a multi-room lair; the animated juggernaut (`Number 1211`, placed in
  `17/7055`, `GameLimit 1`, `RegenTime 3`, 1,300,000 exp) is killable **once per 3 hours**.
- **A placed boss is NOT an instant fixture.** Normal `NPC`-placed monsters regenerate on entry
  (instant, every pass — see above), but a *boss* placed monster is still gated by its regen, so it's
  amortised exactly like a lair boss, not grabbed every lap.
- **A boss's `RegenTime` is in HOURS** (the Game-Data browser renders it "15 hour"), distinct from the
  lair-mob `AvgDelay`/`RegenTime` path above.
- **True monster exp is `EXP × ExpMulti`.** The Monsters table stores a base `EXP` and a separate
  `ExpMulti` (the boss/exp multiplier); crowned spider `EXP 60000 × ExpMulti 20 = 1,200,000`. Read them
  together (the Game-Data browser already shows "60,000 (×20)"); `EXP` alone under-reports a
  multiplied monster.
- **Exp/hr estimation of a boss:** pull it OUT of its lair's per-mob average and add its amortised
  contribution **`boss exp ÷ regen-hours`, counted once** for the whole loop (a single time no matter
  how many rooms it can appear in) — `1,200,000 ÷ 15 = 80,000/hr`, not `1.2M` per lap in every room.
  The regular (non-boss) lair mobs still fire per-room on the room delay.

### Death-summon cascades — a monster that summons more on death *([CONFIRMED] 2026-08-03, user + game-data trace, Paradigm 1.9.1)*

Some monsters spawn **more monsters when they die**, and those can summon in turn — the Zombie Pen
(`17/2601`) is the canonical case, built for AoE ("rooming") crowds.

- **The mechanic is `DeathSpell` → summon slots.** A monster's **`DeathSpell`** field names a spell
  (`0` = none). In the Spells table that spell has ten ability slots `Abil-0..Abil-9` with values in
  `AbilVal-0..AbilVal-9`; a slot whose **`Abil` value is `12`** ("summon monster") summons the monster
  **`Number`** in the matching `AbilVal`. Only `Abil == 12` slots summon — a spell row carries unrelated
  payloads in its other `AbilVal` slots, so filtering on `Abil == 12` is required.
- **It recurses, and the data terminates it.** Each summoned monster has its own `DeathSpell`, so the
  chain continues until a tier whose members have `DeathSpell 0`. Real chains are shallow (≤3 tiers);
  follow the data, don't assume a fixed tier count.
- **A room holds at most 20 monsters at once, and a summon cast that would exceed it fails whole.** The
  engine caps a room at **20** living monsters. A dying monster's `DeathSpell` fires as **one atomic
  cast**: it lands only if *all* its summons fit under the cap — otherwise the whole cast **fails to
  cast, is never retried, and is permanently lost** (its summons do NOT appear on a later round when
  space frees up). So a wave fills with whole casts until the next won't fit, then drops the rest: e.g.
  15 monsters each summoning 2 want 30, but only 20 spawn (10 whole casts) — the other 5 casts fail
  outright. A fan-out room is therefore worth far less than the raw tree would suggest. (The Zombie Pen
  peaks at 15 with `Max 3` zombies, so its cap never bites; a higher `Max` or a wider fan-out would.)
- **Worked example — stitched zombie (`Number 1220`, `EXP 4000`, `DeathSpell 1032`):**
  - Spell `1032` (Abil-0/1 = 12) → **severed waist `881`** + **severed torso `888`**.
  - Waist `881` (`DeathSpell 1034`) → 2× **severed leg `889`** (3500 each).
  - Torso `888` (`DeathSpell 1036`) → 2× **severed arm `890`** (3000) + **severed head `891`** (3500).
  - Legs/arms/head have `DeathSpell 0` → terminal. **One stitched zombie = 8 monsters, 28,500 exp**
    (not the 4,000 its own `EXP` shows). Note each part summons via a **different** spell.
- **Exp/hr estimation of a summoner:** simulate the room tier by tier under the 20-cap and count the
  monsters that actually spawn. Its effective yield is the **whole (capped) tree's exp** (base + every
  descendant — `28,500`, not `4,000`, for one stitched zombie). The summons also cost combat time:
  **single-target** fights every monster the room becomes (kill count = tree size, `8` per zombie),
  while **AoE/rooming** clears one tier per pass (waves = tree depth, `3`). So a death-summon room yields
  far more than its face value, but the extra kill/wave time — and the cap on huge fan-outs — keep it
  below the naive exp-ratio multiple. Bosses are left on their base exp (their death-summon, if any, is
  not folded — a rare edge, and boss exp is already a flat amortised approximation).

**Cleanup-only boss respawns** *([CONFIRMED] 2026-08-04, user)*
- A subset of bosses (in the boss-timer seed: Lord Feyr, Iceforge, Huge Sandstone Sphinx, Mammoth
  Stone Scorpion, Bogwood Box) do **not** respawn on a kill-based countdown. They reset **only at the
  BBS's nightly server cleanup**, a fixed daily wall-clock time (per board — e.g. **2100 Pacific**).
- So once killed they stay dead until the next cleanup instant, then return. The client models this as
  a DEAD / ALIVE state keyed to the per-BBS cleanup time, not a duration timer.

### Room-spell monster summons *([CONFIRMED] 2026-08-06, user + game-data trace, Paradigm 1.9.1)*

Distinct from a monster's death-summon: a **room itself** can summon monsters via its entry spell.
`Rooms.Spell` names a `Spells` row cast **on room entry and re-cast every combat tick (~5s)** while
you're in the room (the user times it at **~5.3s** — the tick, firing just after entry). These are the
monster-spawning rooms that made the Exp/Hr estimator under-report: the exp comes from summons the
route resolver never counted.

- **The summon lives in a TextBlock, not an `Abil 12` slot.** The room spell carries a **`TextBlock`
  ability (`Abil == 148`)** whose `AbilVal` is a **TBInfo `Number`**. The TBInfo `Action` string is the
  roll table — so a room-summon is *not* found by scanning `Abil == 12` (that's the death-summon path).
- **`nomonsters:` gate.** A leading `nomonsters:` condition means the spell **only fires when the room
  holds no monsters** — so it can't stack summons: kill what's there, and the next tick may summon one.
- **d100 roll table with cumulative bands.** Each `Action` line is `<cumulativeThreshold>:<act>[:<act>…]`;
  a line's probability is `(threshold − prevThreshold) / topThreshold` (top is usually 100). A line whose
  actions include **`summon <monsterNumber>`** summons that monster (from the Monsters table, normal
  exp). Other actions (`addevil`, `message N`) are misses.
- **Worked example — "crypt summon 2" (`Rooms.Spell 5248`, e.g. room `13/3573`):** `Abil-0 = 148`,
  `AbilVal-0 = 3411`.
  - TBInfo `3411`: `nomonsters:random 3412` → gate + redirect to the roll table `3412`.
  - TBInfo `3412`: `60:addevil 0` (1–60, nothing) · `85:message 4064` (61–85, message) ·
    `90:…:summon 2111` (86–90, **cairn wraith** 13000) · `95:…:summon 2119` (91–95, **ogre skeleton**
    12000) · `100:…:summon 2122` (96–100, **zombie warrior** 12000).
  - → **15% summon chance**, **1,850 expected exp per roll** (`0.05 × (13000+12000+12000)`).
- **Estimator model** *(user design)*: credit each summoning room **one averaged roll per visit**
  (`Σ band% × monster exp`), plus a **second roll's worth when a quick kill (rounds ≤ 2)** lets another
  spawn before you leave (`× (1 + summonChance)`), scaled by laps/hr. A simplification of the true
  per-tick loop, but it lands close and stops the under-report.

## Mana regeneration & the ManaRgn breakpoints *([CONFIRMED] 2026-08-08, against the engine's own reference formula)*

Passive (non-resting, non-meditating) mana regen ticks **every 30 s** (6 rounds) and adds a whole-MP amount computed by one integer formula. `CharacterCalculator.CalcManaRegen` implements it; the Level Projection grid already relies on it.

```
S (base stat) by magery type:  mage = INT ·  priest = WIL ·  druid = (INT + WIL) / 2 ·  bard = CHM ·  mystic = fixed 1
base = trunc( (level + 20) · S · (mageryLevel + 2) / 1650 )
tick = trunc( (ManaRgn% + 100) · base / 100 )        [stock]
tick = base + trunc( ManaRgn% · base / 100 )          [Paradigm / GreaterMUD — functionally equivalent]
```

- **`mageryLevel`** is the class's magery tier (`Classes.MageryLVL`), a constant per class — NOT the character level. It also drives max mana: `MaxMana = (mageryLevel · level · 2) + 6`. (A stock ability code 145 = "ManaRgn".)
- **`ManaRgn%`** is the sum of every code-145 source: gear/quest `addability 145 N` bonuses (`+N ManaRgn`) **plus** a cast mana-regen roll spell's rolled magnitude. It is a **percent modifier on the tick, not flat mana**.
- **Breakpoints are emergent, not a table.** Because `tick` is truncated, it steps up by 1 MP only when `ManaRgn%` (or level / stat) crosses the integer threshold `(N·100/base − 100)`. Between thresholds, extra ManaRgn% does nothing.
- **Roll spells (nature tap / mana flux)** carry a code-145 slot with stored value 0; the magnitude is rolled per cast from the level-scaled range `Min/Max = base + trunc(inc/incLVLs · level)` (the same `SpellCalculator.AffectMagnitude` scaling every affect uses). So the worst roll = Min, best = Max, and the rolled value adds straight into `ManaRgn%`. This is why a reroll only helps when the range can cross a truncation breakpoint at the current level — otherwise it just burns mana.

**Unverified / modelled (not from the engine reference, don't hard-depend):** the roll's *distribution* across `[Min,Max]` is treated as linear for "where on the range" purposes but is not proven uniform; the meditate path (10 s tick, ManaRgn% excluded) is a reverse-engineered model; whether the live engine caps summed ManaRgn% is unknown. The mana-regen breakpoint calculator uses only the CONFIRMED passive formula above.

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

**Death fully clears all effects** *([CONFIRMED] 2026-08-09, user)*
- Death **wipes every ailment, status effect, buff, and debuff** off the character — poison, disease,
  blindness, confusion, held/knockdown, and every positive buff alike. This holds on **both stock and
  Paradigm** (realm-independent); the character respawns at the graveyard with a clean effect slate.
- **Client implication:** `ConditionTracker` is an observation log driven by the server's applied /
  wear-off lines, and death teleports you out **without emitting those wear-off lines** — so a
  condition latched at the moment of death (most dangerously *MovementPrevented*, whose stale flag
  keeps `MovementCoordinator.HeldGate` asserted and strands the walker "Paused by: Held") never
  auto-clears. Because the game clears *everything* on death, the client mirrors it with a full
  `ConditionTracker.ClearAll("death")` on `RoomTracker.PlayerDeathObserved` — no per-flag scoping —
  matching the mechanic exactly (report `paradigm-20260809-114444`, fixed v2.39.1).
- **[CONFIRMED]** (2026-08-03, user + captures) **The deathpile is a `corpse` object, recovered with
  one `recover corpse <given-name>` command — NOT a per-item `get`.** (This corrects an earlier note
  that said stock drops items loose to the ground; it does not.)
  - On death, non-loyal items **and** coins go into a **corpse of <player>** on the death-room floor;
    loyal items stay on the player.
  - The room's **floor survey names it by the player's GIVEN name only**, no article:
    `You notice corpse of Ermias here.` (character "Ermias Asghedom" → the corpse reads "Ermias").
  - Recover it with **`recover corpse <given-name>`** (e.g. `recover corpse ermias`). Bare
    `recover corpse` also works but **gets confused when several corpses share the room**, so always
    name it. **Recovering your OWN corpse never needs a password**, even with corpse passwords set;
    the password system only gates *other* players looting your corpse.
  - **One command pulls the WHOLE pile back at once** — coins and items together. The output is:
    `You begin to pick through the corpse of <name>...`, then `You picked up N <denom>.` per coin
    denomination and `You took <item>.` per item, ending with the green completion line
    **`You have recovered the corpse of <name>.`** — the single reliable "pile recovered" marker.
  - So auto-recovery must: on entering the death room, read the `You notice … here.` survey; if it
    holds `corpse of <ourGivenName>`, send one `recover corpse <name>` and finalise on
    `You have recovered the corpse of <name>.`; if the corpse is NOT in the survey, the pile is gone
    (looted / decayed) — mark it Missing and send nothing (never per-item `get`, which just spams
    `You don't see <item> here.`).

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
  character on each of the dragger's moves (observed: `MudPlay is dragging you around.`).
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

- **[CONFIRMED — user, 2026-07-22] Per-hop movement speed is realm-specific, and the two realms
  differ enough that no single fixed movement timer can be right for both.**
  - **Paradigm** paces each hop by a deterministic server formula (no lag term):
    `hop_ms = max(1000, 1100 + enc² · 2000 − quickness · 10)`, where `enc` is the encumbrance
    fraction (0–1 of max carry) and `quickness` is total quickness. There is a hard **1.0s cap —
    the fastest any hop can be.** Time rises quadratically with encumbrance and falls linearly with
    quickness: a light, high-quickness build sits pinned at the 1.0s floor (quickness 100 stays
    capped until ~67% enc), while a heavy or low-quickness build ranges up toward ~3.1s/hop. The
    server will not process a hop faster than this, so back-to-back move commands are throttled to
    it rather than executing instantly. (Cross-checked against the falls-below-cap points: quickness
    15 → 16% enc, 100 → 67%, 200 → 97%.)
  - **Stock** has no such floor. Empirical captures (8 sessions, 199 moves) show a true-speed floor
    around **0.25s** unencumbered, medians ~0.6–0.7s at light/medium loads rising to ~1.65s when
    Heavy (≈67%+ enc), with wide lag-driven variance per hop. A comparable character therefore moves
    roughly **2–4× faster per hop on stock** than the Paradigm 1.0s cap.
  - **Design consequence:** the dark-room settle window (and any fixed inter-move timer) is
    realm-coupled. At 1.0s it ≈ the Paradigm server cadence, so on Paradigm it costs almost nothing
    on an empty room; on stock the same 1.0s nearly doubles the natural ~0.6s hop — a heavy tax.
    Overshoot (stepping before a dark pursuer reveals) is therefore a *fast-mover* problem: it only
    bites a character near the Paradigm cap or a quick stock character; a slow/heavy mover has ample
    reveal margin. This argues for making dark-room room-clear detection **event-driven** (step once
    the room is confirmed clear via the attack→"no effect" + combat-line-silence signals) rather than
    a single global duration.
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
- **[CONFIRMED]** **Picklock + open verb wording (stock, capture 2026-07-30).** A successful
  `pick <dir>` prints **`You successfully unlocked the door.`** — **past tense**, and the *same*
  line the use-key unlock emits (the two are distinguished only by which command was in flight,
  not by wording). A pick failure is **`Your skill fails you this time.`**. Unlocking does **not**
  open the door — a separate `open <dir>` is required, whose success prints **`You open the
  door.`** (not "The door is now open."). `DoorOpenManager` keys on all three; matching only
  present-tense "unlock(s)" or "is now open" stranded the walker at a picked door
  (report stock-20260730-182812).
- **Corollary the tracker relies on:** *a room redisplay that still matches the room you moved
  from is never the result of a refused move.* While a move is pending, seeing the source room
  again can only be a **passive re-look** — a combat-clear, a monster/player arrival or
  departure notice, a bare re-glance — carrying no position signal. The tracker therefore
  ignores it and keeps waiting for the move's real outcome (a different room), rather than
  inferring a refusal from the redisplay alone. (A genuine self-loop exit that lands back in
  the same room is a real move with a real room display; it resolves as a normal
  predicted-neighbour match because the exit's target *is* the source, so it is not confused
  with a passive redisplay.)
- **[CONFIRMED, capture 2026-07-14 report 121106] Searchable hidden exits report success/failure
  with axis-dependent wording.** Revealing a hidden exit is `sea <dir>`; the game replies with one of:
  - **success** — cardinals `You found an exit to the <dir>!`; up/down `You found an exit upwards!` /
    `You found an exit downwards!` (no "to the", `<dir>wards` suffix). *`upwards` confirmed on the wire;
    `downwards` confirmed from an earlier capture.*
  - **failure** — cardinals `You notice nothing different to the <dir>.`; up/down `You notice nothing
    different above you.` / `You notice nothing different below you.` (no "to the", no direction word).
    *Both vertical forms confirmed (`above you` on the wire, `below you` by the user).*

  The client keys on both to drive the reveal retry loop (`HiddenExitRevealManager`): a failure line
  triggers another `sea` up to the attempt cap, a success line resolves the reveal so the walker sends
  the move. Because the up/down failure form drops "to the" entirely, a failure regex that only matched
  the cardinal `to the <dir>` shape never registered an up/down miss — so up/down searches never retried
  cleanly and stalled (the reported symptom). A "bonked" `sea` is distinct from a bonked *move*: the
  `sea` reply above is not a move refusal.
- **[CONFIRMED, capture 2026-07-15 report 132150] A trap search reports the found trap with the
  LONG-form direction word.** Searching a trapped exit is `sea <dir>`; on a hit the game replies
  `You found a trap to the <dir>!` where `<dir>` is spelled out long — `You found a trap to the
  southeast!` (confirmed on the wire, alongside the outbound `sea southeast` that produced it). The
  client keys on this to drive `TrapDisarmManager`'s search→disarm loop. Two consequences:
  - **Direction matching must normalise both sides.** The @trap remote handler enqueues the short
    form it parsed, but the walker enqueues the long-form direction word — and the game's reply is
    long-form. Comparing a short-normalised observed direction against an un-normalised stored one
    (long-form from the walker) never matched, so a successful search stalled in *Searching* and the
    disarm never fired (the reported bug). Both the observed and the stored/enqueued direction are
    now normalised to the short form before compare.
  - **The disarm send stays long-form** — `sea southeast` is wire-confirmed, so the walker keeps
    sending the long form it enqueued (matching the confirmed search) rather than re-shortening it.
    Whether `disarm trap <longdir>` (e.g. `disarm trap southeast`) is accepted the same way `sea
    <longdir>` is has NOT been directly wire-confirmed (the reported capture stalled before the
    disarm went out); it is the walker's existing send shape and is flagged for live verification.
- **[CONFIRMED, capture 2026-07-15 report 131801] Trap-disarm capability can be inferred from the
  character's race and class via game data — the parsed Traps stat is not the only signal.** Race and
  class are chosen at character creation (the player selects them) and are shown on the train-stats
  screen, so they are known even for a brand-new character that has never run `stat`. A class or race
  grants the Traps skill when its game-data record carries a trap-skill ability code — code 40
  (FindTraps, the single "Traps" skill governing both find and disarm in stock data), 41 (DisarmTraps),
  or 1002 (GrantTraps) for custom / ParaMUD sets (see `AbilityNames.HasTrapAbility`; this is the same
  grant the party-delegation capability check already reads for other players). The Traps *value*
  itself still comes only from the `stat` screen's `Traps:` row — the single-line `exp` output
  (`Exp: N Level: M Exp needed for next level: ...`) reports only progression and never carries it. So
  when the Traps value hasn't been captured yet (a freshly loaded profile with no `stat` this session,
  or a new character), the client falls back to the race/class game-data grant to decide capability:
  the walker self-disarms if the selected class or race grants Traps, rather than deciding on a
  defaulted-zero value and waltzing through. A positive parsed Traps value remains the primary signal.
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
- **[CONFIRMED, report 2026-08-13]** **An NPC can transport a player who ASKS it — a "greet
  teleport."** Some placed NPCs carry, inside their `Monsters.GreetTXT` chain, an askable keyword
  whose directive block ends in a `teleport <room> <map>` — asking the NPC that keyword
  (`ask <noun> <keyword>`, noun = last word of the monster's name, same convention as the guard-door
  ask commands) ports the asker to that room. Example: the Floating Citadel's **Grey Lord** (`#251`,
  GreetTXT 366) stands in the Great Hall (`1/160`), a sealed pocket whose only cardinal exit is S;
  **`ask lord teleport`** ports the character to **Town Square (`1/224`)** — the pocket's only egress
  toward the rest of the realm. The same greet also exposes quest keywords (`code` / `word`, TBInfo
  block 371) that reach the same room but sit behind `evilaligned` / `goodaligned` / `checkability`
  gates; the plain **`teleport`** keyword (block 370) is **ungated** and deterministic. The navigation
  engine models only the **ungated** greet teleport as a routable exit (GreetTeleportResolver →
  `RoomGraphManager.BuildGreetTeleportEdges`, a `Direction.Teleport` edge whose command is
  `ask <noun> <keyword>`); gated keywords are skipped because the client can't verify the gate and a
  failed transport would strand the walker. **Party behaviour is unverified** — treat a greet teleport
  the same as a CMD teleport (moves only the asker, likely party-splitting) until captured.
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
- **[CONFIRMED, user 2026-07-20 + OBSERVED from Paradigm-1.9.1 data]** **Boat travel — a sea-captain
  dock `CMD` that ferries a party across water via `secure passage to <place>`.** This is a *specific,
  more elaborate application* of the CMD-teleport-splits-party mechanic above: a dock room's `CMD`
  points at a TBInfo block listing one `secure passage to <place>` verb per reachable port. Typing the
  verb at the dock CMD-teleports **only the caster** onto a ship, so it splits the party exactly like
  `ring chime` — the leader `@party`-relays the verb, every member fires their own copy, and the party
  **re-forms at the destination port** (re-invite/wait, same arrival-ordering rules as above). The NPC
  behind it is the **old sea captain (#2344)** standing in the dock room.

  **Per-member gating — the captain rejects an individual, not the party.** Each `secure passage` line
  carries an independent **minlevel** *and* a **fare** (price). Every member is gated **individually**
  on both: a member below the minlevel, or without the fare on hand, is refused *at the captain* and
  **left behind on the dock** while the rest sail. So the pre-flight check is per-member, mirroring the
  toll gate: route the party to a port only when the **poorest / lowest** member clears both bars.
  - **Fare is in copper** (the base coin unit), so it folds into the same `@wealth` pre-flight gate as
    tolls — a member needs `price` copper-value on hand. (Contrast: a `(Toll: N)` exit is `N` **gold**
    = `N*100` copper; a boat `price` is already copper.)
  - **`checkability <flag> <rank>`** (optional, present only on talkiran below) is a **quest-flag /
    rank** gate the client does **not** read — attunement (e.g. MerchantCaptain rank 3) is the user's
    responsibility. If a member routes to that port un-attuned the captain rejects *them*; the client
    never boards and **fails out** (see fail-out below). We do not read or infer quest flags.

  **TBInfo Action format** (verified verbatim off `data-Paradigm-1.9.1`, TBInfo #4986 / #5012, each
  reached from the dock room's `CMD`). One newline-separated line per port; colon-separated directives:

  ```
  secure passage to <place>:[checkability <flag> <rank>:]minlevel <L> <failMsgId>:price <copperFare> <failMsgId>:random <tbId>:text <msgId>
  ```

  `random <tbId>` points at a **weighted-random TBInfo table** (a `Textblock(rndm)`), whose lines read
  `<roll>:cast <boardSpell>:cast <tripSpell>`. Both `cast`s fire in order: **`boardSpell` (5415 "board
  ship")** random-teleports the caster into a ship room (`Abil 140` val 0 over `MinBase..MaxBase` =
  rooms **14/715–723**, `Abil 141` = map 14); **`tripSpell`** starts the buff-locked voyage.

  **Arrival-room resolution is a spell EndCast chain** (uses the `140/141/151` mechanics documented in
  *Cast-teleport exits* below): follow `tripSpell` down its **`Abil 151` (EndCast → follow-on spell)**
  chain — `trip 1 → trip 2 → trip 3 → disembark to <port>` — to the terminal **`disembark`** spell,
  whose **fixed `Abil 140` room + `Abil 141` map** is the arrival `RoomKey`. Each trip leg carries
  `Abil 29` (buff-lock) so the player can't act mid-voyage; the final `<port> landing` spell is a
  cosmetic message. Worked chain (albion): random `#4987` → `cast 5415`+`cast 5416` → `5416 → 5418 →
  5419 → 5417 "disembark to kingsport"` (`Abil 140`=702, `Abil 141`=14) → arrival **14/702**.

  **Verified Paradigm-1.9.1 dock table** (discovered data-driven, *not* to be hardcoded — scan every
  room's `CMD` TBInfo for `secure passage to` lines and resolve as above):

  | Dock room | Port | Verb | Minlevel | Fare (copper) | checkability | Arrival room |
  |---|---|---|---|---|---|---|
  | 14/759 "Blackwater Harbor, Wharf" | albion | `secure passage to albion` | 50 | 2,000,000 | — | 14/702 (Kingsport Harbor) |
  | 14/759 | terra fuego | `secure passage to terra fuego` | 50 | 2,000,000 | — | 14/1812 (Shoreline, Sandy Beach) |
  | 14/759 | terra ista | `secure passage to terra ista` | 60 | 5,000,000 | — | 14/1813 (Cliffside Beach) |
  | 14/759 | talkiran | `secure passage to talkiran` | 65 | 6,000,000 | `211 3` (MerchantCaptain rk 3) | 14/15000 (Tal'kiran Shore) |
  | 14/702 "Kingsport Harbor, Wharf" | port blackwater | `secure passage to port blackwater` | 1 | 2,000,000 | — | 14/759 (Blackwater Harbor) |

  **Transit + fail-out.** After the verb: board → random ship room (14/715–723) → buff-locked trip
  legs → disembark teleport to the arrival room. The client **suppresses engines** during the voyage
  and waits for the arrival room to render. A member the captain rejected (minlevel / fare / attunement)
  **never boards** — detected as *no teleport into a ship room within a short window of firing the
  verb*, which is the fail signal.
- **[CONFIRMED by user 2026-07-30] Jail `bribe guard` is a cell-hop helper with an escalating "take
  the most you can afford" toll.** In the jail rooms (Paradigm `1/541–545`, `14/1326–1333`) the
  `bribe guard` command casts a jail-teleport (moves the player between cells so a jailed player can
  reach their gear when they can't bash / picklock the cell door or lack the jail key). The TBInfo
  `Action` lists **six escalating `price` tiers** — `100 / 1000 / 10000 / 100000 / 1000000 / 10000000`
  copper (1 Gold → 10 Runic). The guard charges the **largest tier the player can currently afford**,
  capped at 10 Runic per bribe: carry 11 Runic → charged 10 Runic; carry 8 Runic → charged 1 Runic
  (the next tier, 10 Runic, is unaffordable). The escalating cost *is* the catch. Only the ceiling is
  meaningful to surface, so the room tooltip renders it as "bribe guard — costs up to 10 Runic (takes
  the most you can afford)". This tiered multi-`price` shape is unique to bribe guard among room-`CMD`
  commands; ordinary paid services (`roll dice`, `buy <spell>`, `summon <x>`, `secure passage`) carry a
  single `price <copper> [failTextblockId]`.
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
     - **[CONFIRMED via game data, Paradigm 1.9.1]** The **lava / volcano biome** is this same shape.
       `Spell:526` "magma heat" (`Abil-0 1` Damage, a leaf spell — no EndCast chain) covers ~1000 rooms
       (Lava Tube / "salamander tubes", Magma/Molten River, Jagged Obsidian Field, Volcano Magma Tunnels,
       etc.); `Spell:218` "temple of fire fire" covers the Temple-of-Fire / Volcano-heart rooms. Both are
       negated by **either** the **magma amulet (item 487)** or the **phoenix feather (item 1000)** —
       each has `NegateSpell = [526, 218]`, and they're the only two items that do. `RoomHazardIndex`
       already indexes these (one any-of group {487, 1000}), so the router treats lava exactly like the
       river/desert: avoid unless the player carries a counter.
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
     - **[CONFIRMED by user 2026-08-10, reports `paradigm-20260810-201953` / `-202239`] Ice cavern
       (Frozen Cavern) up/down slide** — the descent rooms (`10/276`–`10/295`) cast `Spell:1144` "ice
       cavern level 1" / `Spell:1145` "level 2", which check for a **rope & grapple** (item **191**): hold
       it and the slide is safe, lack it and you're **teleported down and take heavy damage**. In the data
       the entry spell's TextBlock is **9407** (`failitem 191:failitem 930:random 9408`; `9408` does the
       `teleport … cast 1142` damage). The room-hazard machinery already handles this `failitem` shape —
       but see the encoding gotcha next.
     - **DATA ENCODING GOTCHA — a TextBlock spell's TB number is NOT always in `AbilVal`.** Most room-entry
       hazard spells put the `TBInfo.Number` in the `Abil 148` slot's `AbilVal` (e.g. desert 683 → 2653,
       river 753 → 2750). But a **large class** (~40 spells: the ice cavern 1144/1145, blackwood 1040,
       graveyard 1126, bone dock 1152, fungus 1205, the highlands/farms 5500-series, caverns 5788/5789, …)
       leave `AbilVal-0 = 0` and stash the TB number in the spell's **`MinBase`/`MaxBase`** instead
       (ice cavern 1144 → MinBase `9407`). `RoomHazardIndex.WalkSpellChain` now falls back to `MinBase`/
       `MaxBase` when the Abil-148 `AbilVal` is 0 — before that fix every one of these hazards was invisible
       to the router, so no protection was ever offered (the ice-cavern route picker never surfaced).

  3. **TextBlock action guarded by a buff check — `checkspell` OR `failspell`** (a buff check, not an item
     check). `checkspell S T` = "if buff S is active, branch to TBInfo T (safe); else fall through
     (damage)." **[CONFIRMED by user 2026-07-28]** `failspell S T` is the sibling directive: "if buff S is
     **not** active, the damage fires." Either way the survival model is identical — the room punishes you
     unless buff S is up. Worked example — Scorching Desert `12/853` `Spell:683` → TextBlock **2653**:
     `failspell 711 2654:random 2655` — **`failspell`, not `checkspell`** (an earlier draft of this entry
     misread the directive, which is why the client's hazard parser first handled only `checkspell` and
     walked the desert unprotected; report `paradigm-20260728-201619`). Map 12 also has a `failspell 711`
     variant on `Spell:684`. Buff 711 "waterskin" (`Dur 600`) is conferred by **using** the *waterskin*
     (item 283, `Abil 43` CastsSp→711, 3 uses). **Client encoding:** `RoomHazardIndex.ScanTextBlock` parses
     both `checkspell` and `failspell` into the same buff-counter, guarded on an item actually casting the
     buff (so a `failspell` on a buff no carried item raises is ignored).
     - **[CONFIRMED by user 2026-07-28, report `paradigm-20260728-201619`] The desert spell does two
       things — damage AND a random teleport — and the two protections are NOT equivalent.** The waterskin
       buff (711) only stops the **damage** portion; it does not stop the random teleport. The **sunstone
       wristband** (item 1180, worn) prevents the **entire** interaction (damage + teleport), so **if you
       have the sunstone you don't need a waterskin at all.** In the data the wristband is a `failitem 1180`
       guard sitting one-to-two `random` hops below the `failspell` (e.g. `2653 → random 2655 → random 2700`
       and `2658 → random 2660`), guarding the sandstorm/sinkhole casts (713/714/743) — which are the only
       desert damage that fires above `maxlevel 19`, i.e. what actually hits a high-level character. (Its
       `NegateSpell` covers only 713; the reliable signal is the `failitem`, not the negator.) **Client
       encoding:** a `failitem` guard found by chasing a buff-gate's `random`-linked failure branch is
       folded into the SAME requirement group as the buff source, so carrying **either** the waterskin **or**
       the sunstone clears the desert for routing; the buff-refresh provisioner still only ever `use`s the
       waterskin (the sunstone is passive/worn). This mirrors how the river raft/canoe `failitem` guards
       (top-level, not nested) let a boat-carrier route the river.
     - **[CONFIRMED by user]** Protection is *duration-based*, not carry-based. `use waterskin` applies
       buff 711, which lasts its listed `Dur` (600 = 10 min game-time). You are protected only **while
       the buff is up** — carrying the item alone does nothing. If you're still in a desert/hazard room
       that needs the buff when it **expires**, you must `use waterskin` **again** to re-apply it.
     - **[CONFIRMED by user]** Each `use` **consumes one charge**; a **fresh waterskin carries 3 charges**
       (the item's `Uses` field). When a waterskin is spent, you need another one — so players typically
       carry **2–3 waterskins** into the desert. Provisioning must therefore stock enough total charges
       to cover the expected time in the hazard stretch, not just "one waterskin."
     - **Routing model:** carry the source item(s), `use` on entering the first hazard room to raise the
       buff, and **re-`use` whenever the buff lapses while still inside a hazard room**, consuming a charge
       each time; when charges run out mid-stretch and no spare waterskin remains, halt rather than walking
       a room unprotected.
     - **[CONFIRMED by user] There is NO wear-off message for the waterskin buff.** So routine refresh
       cannot be reactive — the client **must TIME it** (predictively re-`use` a margin before the buff's
       `Dur` would expire; this is the PRIMARY refresh). The lapse prompt below is only a **reactive
       backstop**: when the timer's estimate is off and the buff drops early, the room re-emits the prompt
       and the client fires **exactly ONE** `use waterskin` to re-raise — not a client-side wear-off
       reaction (there's no such line), but a correction to a mistimed timer.
     - **Lapse / sandstorm spells are derivable from the checkspell chain.** `checkspell 711 2654` — the
       token's second int (2654) is the buff-ABSENT target TB; that block's `cast` is the lapse-damage
       spell. In the desert that's **spell 712 "desert damage"** (its CasterMessage is the thirst prompt);
       **spell 713 "desert sandstorm"** is the separate random-chance teleport, not a lapse signal. The
       client resolves the prompt via the Messages record linked to Spells#712, so it tracks the active
       set rather than hardcoded realm text.
     - **[CONFIRMED by user] Trigger + confirmation messages** (all in the Messages game-data table, so
       match by record number, not hardcoded realm text). **These lines are plain text with no `{s}`
       placeholder**, so they're matched by literal case-insensitive substring, not the caster-message
       regex:
       - Desert lapse prompt — drink now (Spells#712): `You suffer in the desert heat... you need water,
         soon!` The game's own signal that the buff has lapsed while still in the hazard. Fire ONE
         `use waterskin` on this line.
       - Self re-`use` success (Spells#711): `You take a swig of water from your waterskin.` Confirms a
         charge burned and buff 711 re-applied. A `use waterskin` that draws no such line before the NEXT
         lapse prompt means charges/waterskins are exhausted → halt, don't walk on unprotected.
       - Witnessing a party member drink: `<name> takes a swig of water from a waterskin.` How the leader
         observes a follower successfully re-buffing (each member reacts to their own desert prompt).

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
- **[CONFIRMED, user 2026-08-17]** **A "specific order" multi-action exit tracks ONLY its own sequence
  levers, in RELATIVE order — pulling unrelated levers in between does NOT break it; and nested
  action gates can chain.** Two facts that let the walker solve *nested* lever vaults (a lever whose room
  is itself behind another action-gated exit):
  - **Relative order, own levers only.** For `Needs N Actions, specific order`, the gate only requires
    that ITS OWN actions fire in relative order (#1 before #2, #2 before #3, …). Pulling a lever for a
    *different* exit (e.g. the entrance lever of an alcove you must open to reach the next sequence lever)
    between two of the gate's ordered steps does **not** reset the sequence. So the client may interleave:
    open alcove *i*, pull sequence-lever *i*, repeat — the sequence stays valid.
  - **Persistence covers the compound walk.** An opened action-gated exit stays passable for the same
    minutes-long window (above), and opening several nested gates and re-crossing them all lands well
    inside it — so a multi-gate compound detour is safe.
  - **Grounding example (data-Paradigm-1.9.1): the 6/861 tomb vault → 6/924.** Descent
    `6/861 →D→ 6/922 →D→ 6/923 →D→ 6/924`; `6/861` D needs 4 ordered levers, in alcoves 6/919 / 6/921 /
    6/920 / 6/918. Each alcove is behind its own `Needs 1 Actions` entrance gate whose lever sits in a
    freely-walked Ancestral Tomb room (6/889 / 6/917 / 6/903 / 6/875) — genuinely one level of nesting.
  - **Client encoding:** `RemoteActionPathExpander` opens nested gates recursively (open the inner door,
    then cross), bounded by a nesting-depth cap + a lever-cycle guard; past those it clean-fails. This is
    fully generic off the exit graph — no per-area code (the Asylum + Pyramid remain the only bespoke
    area solvers).
- **[CONFIRMED, capture 2026-07-28 report 180730 — SUPERSEDES the earlier report-195552 "both needed" claim]**
  **Two guardroom levers with identical commands on one gate are REDUNDANT alternatives — one pull raises
  it.** Some `Door` exits are raised not by a same-room verb but by a `pull lever` performed in one or
  more *other* rooms — the game data annotates the door exit with an `Action[#N] [on the {dir} exit of
  room M/R]: pull lever` cell per lever room. At the Newhaven castle inner gate `1/1331 N`, the two
  guardrooms `1/1345` and `1/1339` each carry a lever, and **both cells carry the identical command list
  and a bare `Action` (StepNumber 1)** with **no `Needs N` modifier** on the door. Scrollback proves one
  suffices: the exits line went `closed gate north` → `open gate north` after the *first* `pull lever`
  and stayed open through the redundant second pull. So same-command, same-StepNumber levers on one exit
  are interchangeable — pulling either raises it. (An earlier run misread a failure as "both must be
  pulled"; this direct capture retracts that.) A **genuine** multi-step gate is one whose data declares
  an explicit `Needs N Actions` modifier or distinct `Action#1`/`Action#2` StepNumbers — those pull every
  step. A same-room lever variant also exists (`1/1375 S`, the courtyard, whose lever is on this room's
  own W slot — one action, no remote detour). **Client encoding:** a lever `Door`/`KeyLocked` exit carrying
  action cells is promoted to `MultiActionHidden` at graph-build; the required-action count is the number
  of DISTINCT StepNumbers (same-StepNumber levers count as one), and the path expander pulls one cheapest
  alternative per StepNumber — so a redundant pair pulls once, a declared multi-step gate pulls all.
- **[CONFIRMED, capture 2026-07-14 report 091244]** **A lever-raised gate renders in the live room
  display as a *gate*, not a *door*.** At `1/1331` the `Obvious exits:` line reads `closed gate north,
  south, east, west`; after `pull lever` ("you hear the loud nearby rumbling of a gate") it becomes
  `open gate north, …`. So the barrier noun on the wire is **"gate"**, and the open/closed prefix
  carries its live state exactly like a door's. **Client encoding:** `RoomDisplayParser.ParseExits`
  strips an `<open|closed> <door|gate>` prefix off each exit token, feeding the open ones into
  `OpenDoorDirections` so the walker skips the door-open FSM on an already-raised gate. Treat "gate"
  and "door" as the same door-type barrier class for display parsing.
- **[CONFIRMED, game data v1.11p map 9]** **A `(Cast: pre-N, post-M)` exit fires a spell as part of
  the walk — pre-N before the move, post-M after — and when the post-cast spell is a *random* teleport
  the exit's landing is non-deterministic.** The exit stays a plain cardinal move (its cell modifier
  carries the two spell numbers; `0` means no cast on that side). The spell's game-data record classifies
  its landing: ability code **140 = TeleportRoom** (value `0` → a *random* room drawn from the spell's
  `MinBase..MaxBase` base range; value `>0` → a single *fixed* room), **141 = TeleportMap** (the
  destination map). A room-teleport with value 0 spanning more than one base room (and inside the
  defensive `MaxRandomRange` ceiling of 64) is the random case; a fixed room, a single-room range, or a
  non-teleport spell is deterministic. The confirmed real case is the **Warped Asylum** (map 9, rooms
  1183–1290 reached from the Rhudaur side): those rooms carry a mix of plain cardinals and cast exits
  whose post-cast spell (596 / 597) random-teleports the caster into roughly the `[1183,1206]` band.
  Two consequences the client relies on:
  - **The random-teleport exit is NOT routable.** BFS pathing (`FindPath` / `ComputeDistancesFrom`)
    skips any exit flagged `CastTeleportRandom`, even when exit gates are being ignored — the
    non-determinism, not a gate, is what rules it out. The walker can only be routed through the area's
    *plain* exits and its single-destination teleports; a random landing can't be planned.
  - **But the random-teleport exit still LAYS OUT on the map.** Its nominal target is a real adjacent
    room, and the Warped Asylum's cast grid is fully reciprocal — laying those exits out normally
    renders the whole connected area, where portalling them away would strand ~90% of the rooms
    (from room 1259, ~10 rooms are reachable by plain exits vs ~108 by cast). The map marks each
    cast-on-walk exit with a short perpendicular "wall" glyph in the Spell colour, drawn between the two
    rooms, so the player sees the whole area with its spell-gated exits visually flagged rather than a
    sparse fragment. (`RoomExit.CastsOnWalk` drives the render mark; `CastTeleportRandom`, set by the
    spell-catalog classification pass at graph build, drives only the router prune.)
  - **A one-way cast "pocket" is not overdrawn onto its housing map.** A cast area can be a *sink* —
    entered by a single cast-on-walk exit with no walk-back, so it lives on but topologically apart from
    the surrounding map. The Warped Asylum is one: its 108 rooms are reachable only via one cast mouth
    (`1182 W → 1183`, `(Cast: pre-0, post-596)`) and have zero exits back out. Laying the pocket out from
    a housing-map origin poured all 108 rooms into the housing map's coordinate grid, drawing them on top
    of it (the Rhudaur overlay). The fix classifies a cast exit `a→b` as a **pocket entrance** iff `b`
    cannot reach `a` by *any* directed route (`RoomExit.CastPocketEntrance`, set by a graph-build
    reachability pass). The planar mapper stops expanding through a pocket entrance, so from outside the
    pocket shows only as a spell-wall stub at its mouth — but a walker standing *inside* still lays the
    whole area out, because the pocket's internal cast exits are reciprocal (they have return paths) and
    so are never flagged as entrances. This is a *topology* discriminator, orthogonal to
    `CastTeleportRandom` (a *predictability* one): the asylum mouth is both, an internal reciprocal cast
    exit is neither, and a fixed one-way cast-teleport into a sink would be a pocket entrance without
    being random.
- **[CONFIRMED, game data v1.11p map 9, MMUD Explorer cross-check]** **A placed "guardian" monster
  whose greet dialogue raises a door on its own room is opened by `ask <monster-noun> <topic>` — the
  spoken password lifts the gate.** Some pick/bash-proof `Door` exits aren't operated by a room verb or
  a remote lever at all; the barrier is a stationed monster who lifts it when the player asks the right
  keyword. The confirmed case is the **grove shadow guard**: room `9/1423` carries
  `Lair='(Max 2): 503,[…]'` placing shadow guard **#503** (`GreetTXT 1433`), and the door `9/1423 W →
  9/1425` (Morukai's chamber) has an impassable stat requirement so it can't be picked or bashed. The
  greet decodes as: block `1433` lists topics `morukai / orfeo / passage / phoenix / prophecy`, each
  pointing at `1435` (empty, `LinkTo 1436`); block `1436` is
  `checkability 133 4 : remoteaction 1423 66 0 3 : message 1841`. So asking any of those five keywords
  fires `remoteaction 1423 … 3` — **direction index 3 = W** — operating this room's own west exit. The
  spoken command is **`ask <noun> <topic>`** where `<noun>` is the **last word of the monster's name**
  ("shadow guard" → `guard`), e.g. `ask guard morukai`. The five topics are **alternatives** that all
  open the same door — the walker sends only one.
  - **The open is quest-gated and the gate is untrackable by the client.** `checkability 133 4` gates
    the lift on ability **133 = PhoenixQuest**; the client can't read a character's quest abilities, so
    the crossing is **reactive**: promote the door to routable, issue the `ask`, attempt the move, and
    react to whether it actually opened (halt/replan if not). Do **not** try to pre-check the gate.
  - **Client encoding:** identical promotion to the lever-door case above — a `Door`/`KeyLocked` exit
    fronted by a greeting monster whose `remoteaction` targets *its own room* and names *that exit* is
    promoted to `MultiActionHidden` at graph-build, folding the resolved `ask` command into the same
    `byExit` action table the `Action#N` lever cells populate (`GuardDoorCommandResolver` +
    `RoomGraphManager.InjectGuardDoorActions`). The crossing then reuses `SpecialExitDispatch`'s
    ask-then-move path. Monster ids come from the room's `Lair` group (and its single placed `Npc`);
    only monsters carrying a `GreetTXT` are considered.
  - **The same `ask <noun> <keyword>` noun rule backs the path-item give router.** A giver NPC that
    hands over a path-gate item via a deterministic `giveitem` (an `ask <keyword>` dialogue award) is
    addressed by the identical single-word target — the game's `ask` parser takes one target token and
    treats the rest as the keyword, so a multi-word name (`Gnome Commander`) must reduce to its last-word
    noun (`ask commander orb`, **not** `ask gnome commander orb`). The give router reuses
    `GuardDoorCommandResolver.LastWord` for this; only the picker's human-readable "(ask …)" promise keeps
    the full name.

- **[CONFIRMED, game data Paradigm 1.9.1 map 9]** **A quest-gated portal keyword can teleport to a
  fixed room for flagged characters but a *random* room for everyone else — so it is routed as a
  last-resort "gateway", never a plain shortcut.** Room `9/1291` ("Ancient Darkwood Tree, Portal") has
  `CMD 1462`, whose TBInfo fires the same keyword three ways on ability **133 = PhoenixQuest**:
  `go portal:checkability 133 5:cast 620` / `go portal:testability 133 4:cast 621` /
  `go portal:failability 133:cast 621` (and identical `enter portal` lines). Spell **620** "lower portal
  (invited)" is `TeleportRoom 1424` → a **fixed** hop to `9/1424` (the character has talked to Morukai
  and is quest-flagged). Spell **621** "lower portal (uninvited)" has `TeleportRoom 0`,
  `MinBase 1292 / MaxBase 1327` → a **random** dump into the Caves of Chaos (`9/1292–1327`) for anyone
  not flagged. The only observable difference is *where you land*; the quest ability is untrackable by
  the client. Byte-identical across the stock and Paradigm data sets, so the rule is realm-generic.
  - **Client rule:** when a cast-teleport keyword's branches disagree — a fixed branch alongside a
    random (or a different-room fixed) sibling — the landing is non-deterministic, so it is minted as a
    **gateway** `Direction.Teleport` edge (flagged `GatewayTeleport`, nominal target = the fixed
    branch's landing `9/1424`) rather than a plain shortcut
    (`RoomGraphManager.TryFirstRoutableTeleport` classifies each keyword; a keyword with *every* branch a
    fixed hop to the *same* room stays a plain edge). BFS routes in two passes
    (`BfsMapper.FindPath`): a deterministic pass that ignores gateways, then — only if that finds
    nothing — a fallback pass that may cross one. So from *inside* the cluster the gateway is never used
    (BFS prefers the deterministic narrow-stair climb
    `9/1413 → U … → 9/1422 → N → 9/1423 →` guard door `W → 9/1425`), which is what stops an unflagged
    character looping down through the random portal; but from the *overworld* tree base (`7/1360`),
    where the portal is the only way up, the fallback pass crosses it. The walker re-plans from wherever
    the cast actually drops it (flagged → `9/1424` and continues; unflagged → a random caves room →
    re-plan into the cardinal stair climb to Morukai). A pure `IsRandomTeleport` cast with no fixed
    branch to anchor a nominal target stays fully non-routable (`CastTeleportRandom`, skipped in both
    passes).

- **[CONFIRMED, user design 2026-07-17]** **A "random-teleport maze" is a pocket of same-named rooms
  behind a one-way cast mouth whose interior random-teleports you on every step — normal position
  tracking collapses to Lost because every room shares a name and plain-exit fingerprint, so the walker
  can't source a route.** The Warped Asylum is the canonical one. The pocket is detected **structurally,
  with no hardcoded room numbers**: it's the set of rooms trapped behind a one-way cast-pocket entrance
  (`RoomExit.CastPocketEntrance` — a cast-on-walk mouth you can't walk back out of) whose interior holds
  at least one random-teleport exit (`RoomExit.CastTeleportRandom`). Because the asylum's entrance exit
  is *both* a pocket mouth and a random teleport, `BfsMapper.FindPath(outside, mazeRoom)` is always null
  — the clean signal the walker uses to hand the destination to the maze solver instead of failing.
  - **Relocalization is by a "1x2 signature", not the room name.** A random teleport only ever drops you
    into a *corridor* room, and within a pocket every corridor room's signature is unique. The signature
    is the room's own obvious-exits mask **plus, for each of those exits, the neighbour room's
    obvious-exits mask** — the neighbour read live via **`look <dir>`, a passive peek that renders the
    neighbour's exits without moving or firing the teleport**. `look <dir>` responses reach the solver
    because `RoomDisplayParser.RoomParsed` fires before the tracker's look-suppression drops the peek.
    Dead-end cells (reached only deterministically by walking, so never a teleport landing) have
    non-unique signatures and are deliberately omitted from the lookup rather than risk a mis-ID.
  - **Solving:** relocalize from the signature → if a plain BFS route to the goal now exists, hand the
    final walk back to the walker; if the goal sits in a plain-disconnected component (reachable only by
    re-teleporting), **reshuffle** — walk a `CastTeleportRandom` exit to re-teleport and retry. Runs on
    **every realm**: `rm` locates a room by number but does not relocalize inside a same-named
    random-teleport maze (the tracker sits at Suspect, not Confirmed), so the look-sweep is the only thing
    that can drive the asylum — Paradigm included. Implemented in `TeleportMazeIndex` (detection +
    signatures) and `TeleportMazeSolver` (the state machine).
  - **[CONFIRMED, user 2026-08-16] Once relocalized into a "solvable" room, the plain route to the
    goal is unhindered — drive it with no per-step re-location.** The initial teleport-landing
    relocalization (the 1x2 look-sweep on stock, `rm` on Paradigm) still runs — that's how the solver
    confirms which room it landed in. But the moment that relocalization yields a room from which a
    plain BFS route to the goal exists (`FindPath(here, goal)` non-empty — the "solvable room" test),
    that route is a **deterministic, teleport-free corridor with no cast end-casts on it**, so the
    solver paces the moves straight out with **no per-step verification**: no `look` sweep on stock,
    no `rm` on Paradigm. The per-step re-location only existed to catch surprise teleports / blocked
    doors that this route is confirmed not to have, so it was pure spam. The concrete asylum instance:
    **map 9, rooms 1200 / 1199 / 1197 / 1198** are the solvable rooms — from any of them the walk to
    the **old man (NPC #499, phoenix-feather quest start)** is unhindered. The trigger stays structural
    (any maze's plain-route rooms), with these four the confirmed real case.
  - **[CONFIRMED, user design 2026-07-17] Paradigm asylum pull-lever = pocket dimension.** Only the
    Paradigm 1.9.1 data (not stock v1.11p) gives room `9/1259` a `pull lever` CMD teleport back to the
    entry area `9/1180`. That one escape edge would otherwise defeat the one-way pocket test (reachability
    walks the lever back out; the pocket-collection BFS balloons through it into the overworld), so the
    asylum would never be flagged/indexed as a maze on Paradigm. The lever's routable edge is therefore
    **not synthesised** (`RoomGraphManager.ParadigmAsylumLeverRoom`), making the asylum act as the same
    one-way pocket it already is on stock. The lever is still a real in-game exit the player can pull
    manually — the client just doesn't route through it.

- **[CONFIRMED, user 2026-07-23] A walled city can have a "front door" that is a keyword→item→
  summon→kill→key chain, entirely separate from any teleport "backdoor" the map data also holds.**
  The dark-elf city (Paradigm 1.9.1) is the worked example the client got wrong: it has two ways in,
  and the walk-to diagnostic named the wrong one. The **front door** is a multi-step gauntlet the
  player performs by hand — the map graph only encodes its final locked-door hop:
  1. Walk to the **gnome commander** (NPC in `8/459`) and `ask gnome orb` — he hands over the
     **bloodstone orb (item 807)**.
  2. Carry the orb to `8/398` and `rub orb` — consumes the orb and opens the **south exit to `8/403`**
     toward the gate. This step's gate IS surfaced on the room exit ("Needs 1 action: rub bloodstone
     orb / hold bloodstone orb / rub orb"), so once the orb is in hand the walker already knows how to
     cross it.
  3. At the **Black Steel Gate (`8/461`)**, `touch statue` — summons the **obsidian statue
     (monster 347)**; kill it and the corpse drops the **gate key (item 806)**. This summon command is
     NOT surfaced anywhere on the map.
  4. The gate key opens the **town gate `8/461 → 8/462`** into the city — the exit shows
     `(Key: gate key)` but not how to obtain the key.
  The **backdoor** is a single **teleport item** (a "nightblack portal") that drops you inside the
  city map, gated behind a high **minimum character level**.
  - **What the map surfaces vs. what it hides (the crux of auto-traversal):** the room graph encodes
    how to *cross* an item/key gate — the consumption command and required item ride on the exit
    (`8/398` south names `rub orb` + bloodstone orb; `8/461` south names `Key: gate key`) — but it does
    NOT encode how to *acquire* the gating item. Acquisition provenance lives only in the **TBInfo**
    game-data table: `ask gnome orb` → `giveitem 807`, and `touch statue` → `summon 347` (kill → drop
    806) are invisible to the walker until it reads TBInfo. So the walker can cross a gate it holds the
    item for, but is blind to how to obtain that item.
  - **[CONFIRMED, user 2026-07-23] Keyword command form depends on the TBInfo trigger's root:** an
    **NPC-attached** keyword is issued as `ask <npc name> <keyword>` (e.g. `ask gnome orb`); a
    **room CMD** keyword is typed **verbatim** (e.g. `rub orb`, `touch statue`). This supersedes the
    earlier note that keyword strings "come from the NPC's dialogue at play time" — they are fixed in
    the TBInfo `Action`/keyword data, and the client can read them.
  - *[OBSERVED — Paradigm 1.9.1 game data, cross-referenced]* the front-door landmarks are gnome
    commander in room `8/459`, bloodstone **orb item 807** (given by monster #332 via keyword `orb`,
    Textblock #809→#814→#815 `giveitem 807`), the `rub orb` consume-gate at `8/398`, the **obsidian
    statue monster 347** summoned at the Black Steel Gate (`8/461`, Textblock #863 `touch statue:summon
    347`, Called From Room 8/461), which drops **gate key item 806**; the gate hop `8/461 → 8/462`
    carries `Key: 806 or 101 picklocks`. The backdoor is **nightblack-portal item 1419**, whose
    teleport exit into map 8 is gated `minlevel 40`.
  - **Why it mattered for pathing:** the backdoor portal is the *shorter* graph route, so a blocked
    walk-to that re-probed by ignoring **all** gates surfaced it and blamed "a level requirement" — a
    door the under-level character was never going to take. The real obstacle is the front door's
    **acquirable** gate (fetch the gate key / carry the orb). Fix: the failure diagnostic now re-probes
    with only the *acquirable* gates (item / ticket / key-door / hazard) suspended first — level / toll
    / class stay active — so it describes the route the crosser would actually walk and names the
    key/item to fetch, and only falls back to the ignore-all probe when even that finds nothing.

- **[CONFIRMED, user 2026-07-23] A teleport shortcut is usually far shorter than the equivalent
  walking route but can drop the character somewhere lethal — and whether it's lethal depends on the
  character, so the client must NOT silently take it: it surfaces a walk-vs-teleport choice.** An
  item/CMD-cast teleport exit (`RoomExitHint.Teleport`, promoted from an `(Item: N)` exit on a
  `CMD > 0` room) is a plain one-hop edge to BFS, so a walk-to will silently route through it as the
  shortest path — which is dangerous, because a teleport can land you in a **damaging plane** (negative
  power plane, black wasteland) or across **water with no boat** (Balthazar's teleport dropping you at
  the silver river, which is near-certain death without a boat). But the danger is **character-
  dependent**: a high-level priest can out-heal the silver-river damage spell and cross freely, so the
  same teleport that kills one character is a fine shortcut for another. The client cannot judge this,
  so — exactly like the acquire-item-vs-take-the-long-way choice — it presents the fork to the user:
  - **Client rule:** on a user-initiated walk-to, if the shortest route takes a teleport AND a
    teleport-free walking route also exists AND the teleport saves ≥ 2 rooms, pop the route picker
    ("walk it, don't teleport" vs "take the teleport"). If there's no walking alternative the walker
    just takes the teleport (no fork to offer); if the shortest route already walks the whole way there
    is nothing to weigh. `BfsMapper.FindPath(refuseTeleports: true)` backs the "walk it" side by
    refusing both `RoomExitHint.Teleport` and gateway-portal exits. Automated walks (loops, death
    recovery, deposits, party comeback, trainer routing) never prompt — they keep the default
    teleport-allowed shortest route.

## `testskill` obstacle checks *([CONFIRMED] 2026-08-18, user + game-data trace, Paradigm 1.9.1)*

A `testskill <stat> <difficulty> <failTextblock>` directive in a room-CMD / greet / spell action chain
is a **stat check with a random roll**: the game rolls a die in a range tied to the difficulty and
compares it to the character's `<stat>`. Roll **under** your stat → the check **passes** and the chain's
follow-on action fires (a `remoteaction` reveal, a `teleport`, etc.); roll over → the **fail action**
runs (jump to `<failTextblock>`), which often lands you somewhere unintended. Example that DOES roll: the
Slums slaver-rooftop `jump` exits carry `testskill agility` and can drop you into the wrong room on a miss.

**Difficulty `0` is the special case: it never actually checks anything — the action always fires.** So
`clear rubble:testskill strength 0 …:remoteaction …` behaves like a plain lever/reveal, not a gamble.
Client consequence: the walker treats a difficulty-0 `testskill` reveal as a deterministic action (send
the keyword, then take the exit — no fail-handling). A non-zero `testskill` exit is NOT auto-traversed as
a sure thing.

## Special exits the walker can route through *([CONFIRMED] 2026-08-18, user + game-data trace, Paradigm 1.9.1)*

Two exit shapes beyond ordinary cardinals / doors / CMD-teleports, both on the route to the Necromancer
(9/1431, phoenix-feather quest):

- **Room-command reveal (a lever whose opener is the room CMD).** A hidden exit (`(Hidden/Needs N
  Actions)`) whose unlock isn't an exit `Action` cell but a `remoteaction` in the **room's CMD chain**.
  Canonical: 9/1012 "Crumbling Ruin, Entrance" CMD 1422 = `clear rubble:testskill strength 0 …:
  remoteaction 1012 … 0` (synonyms `move`/`push` × `rubble`/`mound`/`rock`; the trailing `0` = the N
  exit). You type `clear rubble`, the N passage to 9/1013 opens. The reveal is **one-directional** — only
  the closed side (9/1012→N) needs it; the reverse (9/1013→S→9/1012) is a normal always-open exit — and
  the opened exit **stays open a few minutes**.
- **Item-use teleport (an item whose USE transports you).** Distinct from CMD / greet / room-spell
  teleports: the teleport lives entirely on the **item**, via the chain item `Abil 43 (CastsSp)` → spell
  → spell `Abil 148 (TextBlock)` → TBInfo whose Action has a literal `teleport <room> <map>`. Canonical:
  **potion of levitation** (item 992 → spell 607 → TBInfo 1421 `roomitem 993 …: teleport 1009 9`) — used
  in **3/1** it transports you to **9/1009**. It is gated two ways: you must **carry** the item (it's a
  single-use consumable, spent on use), and the TBInfo's `roomitem <fixture>` gate means it only fires in
  the room that holds that fixture (item 993 "waterfall" lives in 3/1, which is why it "must be used
  there"). The return trip is a normal exit (9/1009→D→3/1), so the potion is never needed to come back.
  This is likely the ONLY item-only-anchored teleport in the data; the client anchors its graph edge on
  the fixture item's own room (its `Obtained From`), since there's no exit / CMD / greet to hang it on.
  **Partied:** every member needs their OWN potion, and the crossing is a party-relay — the leader must
  tell the party to use theirs *before* using its own. The client already does this via its standard
  teleport party-relay (a leader with followers sends `.@party use potion of levitation` on the party
  channel, THEN uses its own `use potion of levitation`), because the item-use teleport is an ordinary
  `Teleport`-hint edge. The one thing the client can't verify is whether each follower actually carries
  a potion — a member without one is left behind (we don't track follower inventory).

Quest items on such routes (the potion, plus the titanium fork / magical quartz rod that gate the ruin's
barrier exits) are **never auto-obtained** — they're quest-locked, so a route missing one fails with a
named "go obtain the &lt;item&gt;" message rather than trying to fetch it.

## Great Pyramid puzzle climb *([CONFIRMED] 2026-07-29, user + capture `follower log of going up the pyramid.log` + hand-drawn map + game-data trace, Paradigm 1.9.1)*

- **Geography.** Starts at `Scorched Cavern, Firepit` = `12/1239` (its `up` casts spell 685 =
  timer). Great Pyramid = `12/1800–2085`, contiguous, 6 floors: F1 `1800–1920`, F2 `1921–2001`,
  F3 `2002–2051`, F4 `2052–2076`, F5 `2077–2084`, top `2085` (→ Tomb via `2085 U→2250`). Every
  room displays only as `Great Pyramid`, so room **number** is the sole identity.
- **Solver scope.** The solver only delivers the party to `12/2085` and stops there. The `e`
  sphinx at 2085, the Tomb, Pharaoh Rastep, and the Dao Lord portions are all player-handled.
- **Sphinx ascensions are in game data (on the monster, not the room).** Each floor's ascension
  exit is `Hidden/Needs 1 Action` with no command on the *exit*; the action is delivered by the
  **stone-sphinx monster's `GreetTXT` textblock** as a keyword → `remoteaction <top-room>`:
  `fire` (mon #548 @ 1920 → 1921), `sun` (#549 @ 2001 → 2002), `stars` (#550 @ 2051 → 2052),
  `e`/`letter e`/`the letter e` (#552 @ 2085 → 2250). The F4-entry sphinx (#551 @ 2052) is
  **`riddle`-only** — the footpath hint, no ascension word. `ask sphinx riddle` returns the clue
  on any sphinx. Success broadcast: `With a loud grinding noise, a concealed passage opens in the
  ceiling!` → `u`. (The hand-drawn "time" is the riddle mnemonic; the accepted keyword is `e`.)
  Not walker-BFS-routable — the graph builder doesn't synthesise monster-greet `remoteaction`
  edges — so the climb runs a **canned per-floor script**; game-data room numbers position /
  detect floor / read door state.
- **Fall/scatter.** The pyramid room-spells `691`/`692`/`700` (`cleanup`/`cleanup 2`/`cleanup 3`)
  each carry `Abil 115 = 66` with `MinBase/MaxBase = 1239/1278` — ability 115 reads Min/Max as a
  **random room range**, so a fail scatters you to a **random room in `12/1239–1278`** (the
  Scorched Cavern firepit cluster). A secondary path, `dao scatter` (742, cast only from
  `12/2251` `Elemental Plane of Earth`), drops to the single desert room `12/335` `Scorching
  Desert, Pyramid`. **Detection:** landing in a `Scorched Cavern` room (`12/1239–1278`) or `12/335`
  mid-climb = failed → halt+report.
- **F1 — timed, blind-fast.** Entry: `You have a strange feeling that time is running out!`; finish
  within ~5 min of the first firepit `up` or scatter. Lateral gates open with `push block` (encoded
  `push block, push square block, move block`; broadcast `<leader> pushes the stone block, and it
  slides into the wall.`). Never stop on F1.
- **Pre-flight timer gate.** F1 ≈ **126 moves + 6 actions** (5 push-blocks + `ask sphinx fire`),
  ~250 ms/action, under 5 min. **Stock:** `Heavy` (>66%) leader = guaranteed timeout → refuse.
  **Paradigm:** estimate `126·per-move + 6·250 ms` via `MovementSpeedCalculator` (live enc% +
  quickness, floored at the 1 s cap); over 5 min → refuse (crosses ~>80% enc, no quickness). Drives
  **leader/solo only**.
- **F2 — chaos, blind-fast.** Pitch-black (`The room is pitch black - you can't see anything`), wall
  darts/blades (poison), room spells whose damage **scales the longer you dwell** → keep everyone
  healed, don't stop for blind/poison/confuse. Undead priests may `hold person` a member; moving on
  leaves a held member behind (party cohesion is human-managed here in v1).
- **F3 — door-maze, paced.** Doors cycle on spell 700 → TB 2528/2529 (weighted `remoteaction … 0 0
  2/1` = open/close); timer broadcast `Doors on this level creak and thump!`, per-door `The door to
  <dir> just opened.`, exits carry state (`open/closed door <dir>`). **Per-door:** `(Door [1000
  picklocks/strength])` = unbashable → **wait** for the timer; lesser door on-path = **bash
  `<dir>`**. Golden lion key drops from the neutral `floating key` monster (**#598**) — its default
  client relationship was `Flee` in the Paradigm overlay (stock was already `Enemy`); set to
  **`Enemy`** so party auto-combat clears it for the key (the solver needs no kill logic). The client
  tracks who grabs it (`<name> picks up golden lion key`) and forces `@party give golden lion key to
  <leader>` at the key-door unless the leader grabbed it. (**No-drop bug** — a bugged kill drops
  nothing → exit E, re-enter W to respawn; not yet automated.) Key door: `unlock` → `The key breaks
  and crumbles apart.` → `open` → move.
- **F4 — footpath, forward-only.** Spell "fourteen" by walking the correct arches (`ask sphinx
  riddle` @ 2052 gives the clue). Each arch casts **701 (pass)** / **702 (fail)**; **702 →
  TB 2640→2641** = weighted teleport down; a **backtrack also falls** (backtracking = unsolved).
  Runs the footpath strictly forward (never back up); paces slower than the other floors for
  reaction time. Pass internally gates on ability 134 = 9 (Dao/Sunstone flag) — climbers already
  hold it, so the client doesn't check/encode it.
- **F5 — standard, paced.** `go shaft`/`go pit` (room CMD textblocks, e.g. 1800/2524, 1857/2521)
  escape **down** to the firepit.
- **Undead-priest holds** *([CONFIRMED] 2026-07-30, user + game-data trace).* The pyramid undead
  priest is monster **#770**; it casts `MidSpell-0 = 66` — **spell #66 `hold person`, the SAME spell
  ID the player casts** (25% at level 20; also casts #77). Wording: witness cast `<caster> casts hold
  person on <member>!`; the held target's own lines are `Your legs are paralyzed!` → `You can move
  again!` (private — a *witness* never sees a member's wear-off). Cure = **freedom (#70)** / **cure
  paralysis (#160)**, seen as `<caster> casts freedom on <member>!` / `<caster> casts cure paralysis
  on <member>!`. **Per-floor handling:** F1 (timed) and F2 (deadly-to-linger) keep moving through a
  hold; **F3/F4 wait it out** — pause until a freedom/cure cast frees the member (multiple can be
  held) or a wear-off cap (~hold person Dur 4) elapses; F5 has no priests. Combat and a **held
  leader** ride the shared `MovementCoordinator` `Combat`/`Held` gates (the solver waits on those on
  the paced floors); F1/F2 never gate.
- **Correction to earlier notes:** spell **698 = "crushing blocks" (damage), NOT a teleport**;
  F4 fail = spell **702**; scatter range = `12/1239–1278` (room-spells 691/692/700), desert
  secondary = spell **742** → `12/335`. Earlier "begins to stiffen up!" wording was spell **#327
  "paralyzed"** (a beholder-type), NOT the undead priest's hold person #66.

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

**2b. Drain / life-steal spells (mage)** *([CONFIRMED] 2026-08-14, user)*
- Some mage spells are **life-drain** spells — the damage they deal also **heals the caster**
  for a portion of it. Examples: `vamp`, `dtch`, and (high-level evil mage) `nebo`. Availability
  is gated by the class's **magery level**, like any spell.
- They are a **combat action** — they take the place of the round's attack and auto-repeat
  server-side like any attack spell (see the auto-repeat rule above), NOT a between-round cast.
  Tactically they're used like a heal: only worth casting when you actually want the HP back, so
  a drain-capable mage treats them as an emergency heal that also does damage.
- **They cannot affect NonLiving or Undead targets** — draining one produces the same
  `Your spell has no effect on <monster>.` line as any living-only spell (there's no life to
  drain). So a drain's valid target is **living (no NonLiving ability 109) AND not undead
  (Undead column != 0)** — the union of the living-only gate and an undead exclusion. Against an
  ineligible target the drain is skipped and the client falls back to the normal attack cascade.
- **Client model** — the Combat tab's **Drain spell** slot casts as the round's action when HP is
  at/under its configured %-trigger (and mana ≥ its floor, casts remain, and the target is
  drain-eligible), reverting to the normal attack pick once HP recovers or mana drops below the
  floor. By default the drain **yields to the room AoE** (multi-attack) whenever that would fire —
  rooming enough enemies to trigger the AoE is usually the safer play — and a **"Drains override
  AoE"** option lets it pre-empt the AoE too when the loop calls for it.

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

## The `spells` / `sp` command output *([CONFIRMED] 2026-08-13, user capture, Paradigm)*

`sp` is the accepted abbreviation of `spells` and produces the identical listing of the character's
obtained spells. The format (mana classes) is an intro line, a **padding-aligned** column header,
then one row per spell, terminated by the prompt:

```
You have the following spells:
Level Mana Short Spell Name
   1    1  harm   harm
   1    2  mihe   minor healing
   2    4  bles   bless
   ...
```

Key parsing points: the column header's inter-column **padding varies by class and realm** (Kai
classes render "Level Kai  Short …" with an extra space; a realm's mana header can be padded
differently again), so the header must be matched **whitespace-normalised**, not against a fixed
single-space string. Each row is `Level Mana Short <Spell Name…>`; the obtained set keys on the full
Name (not the Short cast-code). `You have no spells.` is the authoritative empty list. A parse that
opens on the header but reads zero rows is a **format miss, not an empty book** — it must not clear
the obtained set. (SpellListParser + report "sp didn't update spellbook".)

## BBS actions / emotes (the `action list` socials) *([CONFIRMED] 2026-08-14, user + live capture)*

MajorMUD / MajorBBS boards ship a **customizable action list** (MUD socials / emotes),
shown by typing `action list` — a bare **space-separated list of verbs** (e.g. `hug kiss
wave grin bow bleed nod laugh … smile … tickle`) that wraps across lines, with no header.

- **Using an action is guaranteed a full GREEN line** — ANSI palette **index 2**
  (`SGR 0;32`), the same green the board paints the whole `Obvious exits:` line.
  All-green is **necessary but not sufficient** — that exits line is all-green too.
  (The board greens only the *label* of `Wealth:` / `Encumbrance:` / stat rows, not
  their values, so those lines aren't all-green; `You are carrying …` isn't green at
  all — both fail the colour gate before any text test.)
- **Own POV** begins **`You <verb…>`** — non-targeted (`You growl.`; `tickle` with no
  target → `You look around looking for someone to tickle.`) or targeted at a player
  (`You hug Suijin close!`, `You wave to Suijin!`). **The output wording does NOT track
  the command verb**: `jump` → `You leap in the air!`, `egrin` → `You grin evilly.` —
  so there is no verb→output map to key on (colour + head shape is the signal).
- **Target POV** (aimed at you): `<Player> <verb…> [at] you…` (`Fujin hugs you close!`,
  `Fujin grins slyly at you.`).
- **3rd-party POV** (you witness it): `<Player> <verb…> [<other>]`; a self-action reads
  the same to everyone in the room (`Fujin growls ominously.`).
- **Targeting varies per action** — some are usable only at players, some also at
  monsters, some self-only. Actions are **room-local** (you only see others' actions when
  they share your room), so an others'-POV actor is always a **player in your room**.
- **`Your command had no effect.`** follows an action used with no/invalid target (or one
  not usable there); that line is **not** green.
- **Client model** — the Conversation window captures these as a **Social** channel entry,
  grouped under the room-local "say" filter (say chip stays white, message text renders
  green). Detection = all-green colour + head shape: own `You <lowercase verb>…` minus the
  known green status prefixes (`You are/have/notice/feel/…`); others' `<known room player>
  <lowercase verb>…` minus enter/exit/follow/logon/chat lines (see `ActionEmoteClassifier`).

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
- **[CONFIRMED]** (2026-07-16, user) **Monster drops land loose on the ground as the item.**
  When a monster we kill drops one of its `DropItem-N` items, the item appears on the floor as a
  normal ground item — there is no corpse-container split to loot. A plain `get <item>` collects
  it, exactly like any other ground item. The drop isn't announced on the kill line, so to see and
  auto-collect it the room must be re-surveyed (a bare `look` re-renders the `You notice … here.`
  list the auto-get engine already parses).
- **[CONFIRMED]** (2026-07-20, user) **A ground stack of 2+ identical items shows a leading count;
  a lone item shows the article form (no count).** The room survey renders a pile as
  `You notice 5 piece of amber here.` — the count is the true number on the floor. When there is
  only **one** of the item, the survey omits the count and uses the article: `You notice a piece
  of amber here.` (never `1 piece of amber`). There is no bulk-get verb, so each `get <name>` grabs
  a single unit; the auto-collect engine therefore issues **one `get` per counted unit** (the survey
  count for a stack, exactly one for the article/lone form). Count parsing mirrors
  `ItemNameStore.Normalize`, which strips the same leading count/article token when matching the
  item name.
- **[CONFIRMED]** (2026-07-27, user, report `paradigm-20260727-185836`) **A room-wide `search`
  (`sea`) is blocked only while you're *actively engaged* in combat.** Sent mid-fight — right after
  the attack-announcement lines — it's lost; the game won't process a whole-room search until the
  room is clear of hostiles. Out of combat it's a quick command→reply (just the ~150 ms network
  latency, no server-side delay), and the reveal doesn't always surface *everything* hidden (that's
  fine). Auto-search therefore holds the `sea` past the fight and fires it **once** the room clears,
  then keeps the walker held briefly so the revealed `You notice … here.` survey lands and the get
  engines collect it **before** the loop sets up sneaking and steps on. One search per room; empty
  rooms (no fight) search on entry as before. (Targeted `sea <dir>` hidden-exit reveals are a
  separate path, above.)

### Item-cast triggers — how a `CastsSp` fires *([CONFIRMED] 2026-07-18, user)*

An Items row's **`CastsSp`** ability (code **43**, `AbilVal` = the cast `Spells.Number`) does NOT
always mean "you command-cast this spell." How the cast fires depends on the ability that
**immediately precedes** the `43` slot in the item's `Abil-0..19` list:

- **`%Spell` (code 114) before `CastsSp`** → an **automatic per-swing combat proc**. The `%Spell`
  `AbilVal` is the proc chance; on a hit the weapon adds the cast spell as **extra damage lines
  during combat** (e.g. *hellblade*: `%Spell 25` → `sunsword`). Not player-triggered.
- **`CastOnKill%` (code 1114) before `CastsSp`** → an **on-kill proc**. Fires **only when the
  wearer lands a monster kill** (`AbilVal` = chance). Legitimately appears on **worn** gear, not
  just weapons (e.g. the *fukumen* / *shinobi mask* / *oni mask*, all worn masks → `invigorate` /
  `adrenaline rush` / `nimble`). Not player-triggered.
- **A single item can carry both** — one `%Spell→CastsSp` proc **and** a separate
  `CastOnKill%→CastsSp` proc (e.g. *Pulsar*: `%Spell 45 → blue ray` + `CastOnKill% 90 → energy
  barrier`). Two independent automatic procs.
- **Bare `CastsSp` (no `%Spell` / `CastOnKill%` modifier before it)** → a **command-activated
  "on use" cast**: the player deliberately activates the readied item to cast the spell (e.g. a
  wand/staff, or *jeweled longsword* → `weapon major valour`). **These are the item spell sources
  the Spell Book lists** alongside learnable spells.
- **`CastsSp` on a one-time consumable** (potion, food — `ItemType` Drink/Food, worn Nowhere) →
  technically activates on use (quaff/eat), but it's **single-use**, so it is **not** a repeatable
  cast source and is **excluded** from the Spell Book. (The equippable-slot gate already filters
  these — a cast source must ready into a real equipment slot.)

Client consequence: `KnownSpellCatalog.GetClassCastItems` (the Spell Book's item-cast list) must
skip any `CastsSp` slot preceded by a `%Spell` / `CastOnKill%` modifier — otherwise proc weapons
and on-kill gear masquerade as command-cast spell sources.

**Charge count (`Uses` / `UseCount` field):** a **positive** value is the item's real charge count
(consumed to zero, then the item is gone). **`<= 0` means unlimited** — MajorMUD stores **`-1`** for
a truly unlimited item (the common case — e.g. *shimmering greatsword*, *jeweled longsword*), and
occasionally **`0`**; **both are unlimited**, matching MMUD Explorer's own normalisation
`If uses <= 0 Then uses = -1`. Only unlimited items are safe to feed a buff-recast loop; the Spell
Book renders `<= 0` as the word "Unlimited" (never a raw "-1 uses").

**Equip → use → restore swap (a readied buff item)** *([CONFIRMED] 2026-08-06, user)*: to command-cast
from an item you must have it equipped, so the buff engine equips the cast item, `use`s it, then puts
back whatever it displaced. **A buff item can live in ANY equip slot — not just weapon / off-hand**
(a warhorn is off-hand, a charged amulet is neck, etc.). `eq <item>` drops the item into **its own**
slot and displaces only what was there, so restore is **slot-specific**:
- **1H weapon buff** → displaces the **weapon hand**; restore the weapon. (If you're on a 2H weapon,
  a 1H buff swaps cleanly — `eq buff`, `use`, `eq <2H weapon>` — no off-hand step, the off-hand was
  empty under the two-hander.)
- **Off-hand buff** (warhorn, a held item) → displaces the **off-hand**; restore the off-hand shield.
  **The weapon is never touched.** (Restoring the weapon instead — the bug — strands the buff in the
  off-hand and never puts the shield back.)
- **Worn buff** (amulet/ring/etc.) → displaces **that worn slot**; restore that slot's item.
- **2H weapon buff** while holding a 1H weapon + off-hand → it needs **both** hands, so the order is:
  `rem <off-hand>` → `eq <2H buff>` → `use` → `eq <1H weapon>` (this drops the two-hander and frees
  the off-hand) → `eq <off-hand>`.
Whatever slot was empty simply isn't restored (nothing to put back).

### Learning a spell from a teaching item *([CONFIRMED] 2026-08-15, user + wire capture)*

A teaching item (a spellbook / tome carrying the `LearnSp` ability, code **42**, whose value is
the `Spells.Number` it teaches) is used with **`read <code>`** — the SAME 4-letter cast-code the
spell is otherwise cast by. On success the game confirms with:

```
:read agon
You add agony to your spellbook!
```

Note the command speaks the short code (`agon`) but the confirmation names the **full spell name**
(`agony`). This is a distinct wording from the classic learn-scroll line ("You read <scroll> and
learn the spell <name>.") — the client recognises both (`KnownPatterns.LearnSpell` +
`LearnSpellFromItem`) and marks the spell obtained (`SpellbookState.MarkObtainedByName`, keyed on
the name), so the learned-spell set updates the instant a spell is learned mid-session rather than
waiting for the next `spells` poll.

### Armour Class contributions — shadow, Prot-Evil, VileWard *([CONFIRMED] 2026-07-18, user)*

Sources that feed a character's effective AC beyond the item/race/class/quest `+AC` (ability code
2 / blur 10) totals:

- **Shadow property** (ability code **9**) — a flat **+10 AC** that **stacks only once**, no matter
  how many sources carry it. Ten shadow items still grant a single +10, not +100. (Note: the client's
  `AbilityNames`/stat map currently labels code 9 "Shadow Resist" and accumulates its raw `AbilVal`;
  the *AC effect* is the flat +10-once, computed separately from that raw sum.)
- **Prot-Evil / PREV** (ability code **24**) — **1 AC per point, but ONLY versus evil monsters**
  (the majority of monsters). Because it's conditional, it is surfaced as its own "+N vs evil" line
  rather than folded into a flat AC total.
- **VileWard** (ability code **1113**) — an AC bonus whose **magnitude scales with the wearer's own
  evil**. The exact scale is unconfirmed (and it's unclear MME models it), so the client notes its
  **presence only** and never prints a magnitude.

### Blur AC (ability code 10) — encumbrance-scaled, NOT flat *([CONFIRMED] 2026-08-08, user)*

Blur AC (ability code **10**, the item field shown as "AC Blur") is **fundamentally different from
flat worn AC** (code 2): its effective value **scales inversely with carried encumbrance**. At **0%
load** the wearer gets the **full** listed value; at **100% (heavy)** it grants **0**. So a "AC Blur
12" item gives 12 AC when unburdened and nothing when maxed out — it linearly interpolates between.

Because of this, the client surfaces blur **as its own "AC Blur" line/column**, never merged into the
flat "Armour Class" figure (the Item Finder, trial-set readout, and Equipment Manager all split it
out). Internally the aggregate `PlusAC` still carries the nominal blur value for the combat/projected
formulas — the split is a **display** distinction — and the finder shows the nominal (max) value, not
an encumbrance-adjusted one, since it's a planning aid without a fixed load assumption.

## Currency & cash

- **[CONFIRMED]** Five denominations, each with its own full coin name:
  **copper farthings**, **silver nobles**, **gold crowns**, **platinum pieces**, **runic coins**.
  The **runic** coin noun can be **renamed per BBS** (a realm may call its top denomination
  something else); the other four are stable across the target realms.
- **[CONFIRMED]** Value ladder (in copper): 1 silver = 10, 1 gold = 100, 1 platinum = 10 000,
  1 runic = 1 000 000. Wealth is consolidated in copper farthings (the game's `Wealth:` line).
- **[CONFIRMED 2026-08-03, user]** **Ground cash: `You notice N <coin> here.` is the running room
  TOTAL, not a per-kill delta.** A kill drops coin with a `N <coin> drop to the ground.` line (the
  delta), and it merges into the room's single ground pile. A later room re-display's
  `You notice N <coin> here.` reports the *whole* pile — it already includes every coin dropped by
  kills plus anything present on entry (walk in on 5 silver / 20 copper, kill a mob dropping 1 silver
  / 5 copper, re-display shows 6 silver / 25 copper). So the kill-drop lines and the room-total line
  describe the **same coins**; summing both double-counts. Same-denomination piles merge, so if
  someone else grabs from the pile first you can only get what the current display shows. **Client
  consequence:** with collect-after-combat, re-`look` once the room clears and collect off the fresh
  `You notice` total — one authoritative pass — never replay the mid-fight drop deltas (they
  double-count against the total, re-queue on every re-render, and go stale). Implemented in
  `CashManager`.
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
    implemented: `MovementFilter.IsExitBlocked` + `PartyLevelProbe` / `PartyLevelTracker`.
    **Always-on** — routing a following party around a gate it can't clear is never wanted OFF
    (the alternative strands a member), so there's no opt-in toggle; the only gate is "am I
    leading a party". A member's exact level comes from an `@level` reply; **until they answer,
    their `who` title's level band is used with the LOW end as the conservative floor** (they
    could be as low as the band's minimum, so a `MinLevel` gate clears only if even that floor
    clears it). Every client answers `@level` — formats vary by client, parsed leniently (the
    parser also tolerates the `{ }` wrap another MudPlay client adds to its reply). An exact
    reading is stamped with the time it was learned (`PlayerObservation.LevelAt`) and counts as
    **fresh only for the current local day** — a member could have levelled since — so
    `PartyLevelTracker.WarmStaleLevels` re-fires `@level` for any unknown member, or one whose
    reading isn't from today, when a walk's planned route actually crosses a level gate. That
    freshness poll is route-scoped and debounced the same way the `@wealth` toll poll is: fired
    from `MovementFilter.WarmForRoute` only when the levels-permitted shortest route genuinely uses
    a level gate. The ordinary keep-warm refresh is the once-a-day party probe below, not a
    roster-change poll.)

### Party intel probe — `@level` / `@version` on partying *([CONFIRMED] 2026-08-08, user design)*

When we start partying with a player (`PartyProbeManager`, leadership-agnostic, gated by
Settings → Party "probe stats on partying", default on), the client telepaths them intel probes:

- **`@health`** fires on **every** join (this is `PartyPoller`, for the live party-window HP/MA
  vitals — unchanged).
- **`@level` + `@version`** fire only the **first time we party with that player on a given local
  day**, gated by `PlayerObservation.LastPartiedUtc` the same once-per-calendar-day way
  `GreetManager` rate-limits auto-greets. The `@level` reply is recorded by `PartyLevelProbe` (the
  sole `@level` recorder); the `@version` reply is recorded onto the player record
  (`PlayerObservation.Version` / `VersionAt`).
- **`@version` reply shape:** the answering client returns its name + version, brace-wrapped —
  `{MudPlay 2.37.0}`, `{MegaMud 1.03u}`. Recorded verbatim. (Correlated to the member we just
  probed within a short window; a brace-wrapped, letter-led payload carrying a digit — which
  rejects denial / chat lines and the `@level`/`@health` replies that share the window.)

**Exact-vs-title-band reconciliation** (`PartyLevelEstimate`): a recorded **exact** level supersedes
the title-derived band — **UNLESS** the band's floor has risen **above** the exact reading
(`TitleRange.Min > exact`), which means the player has clearly trained since we last asked (their
`who` title moved up to a band starting above our recorded level). In that case the **title band
wins** until we re-learn an exact level at or above the band's floor. A lower or overlapping band
never overrides a valid exact — only a band whose floor has passed it does. (Example: recorded
level 9, title band now 10-14 → the band wins, so the member reads 10-14, not a stale 9, until a
fresh `@level` lands ≥ 10.)
- **[CONFIRMED]** The **keyword** the client keys policy/value on is the denomination-defining
  first word (`copper`/`silver`/`gold`/`platinum`/`runic`); the second word is the flavour coin
  noun (`farthings`/`nobles`/`crowns`/`pieces`/`coins`). Some lines carry only the keyword,
  others the full pair — don't assume one form:
  - **Get / drop** commands the **client** sends now name the coin **in full** (`get 6 silver noble`,
    `drop 1 silver noble`) — a bare adjective collides with like-named items (see the item-collision
    note below). Internally the client still keys policy/tally/parsing on the bare first word.
  - **Corpse loot** drops name the bare keyword: `6 silver drop to the ground.`
  - **Pickup confirmation** names the full coin **and carries NO trailing period**:
    `You picked up 6 silver nobles` (singular `You picked up 1 silver noble`).
  - **Drop / stash confirmations** name the full coin **with** a trailing period:
    `You dropped 5 gold crowns.` / `You hid 219 copper farthings.`
  - **Bank deposit confirmation** names the **full multi-currency amount** as one comma-separated
    list with a trailing period: `You deposit 1 platinum piece, 93 gold crowns, 4 silver nobles,
    12 copper farthings.` A long list wraps at the ~78-col margin, so the client re-merges a
    non-`.`-terminated `You deposit …` row with the next physical row before parsing. Emitted for
    **both** a manual `dep` and the client's auto-deposit `dep`, so it's the authoritative
    both-paths signal. (Withdrawals mirror it: `You withdrew …` / `you withdrew …`.)
  - **Room survey** lists the full coin: `You notice 56 silver nobles, 198 copper farthings here.`
- **[CONFIRMED by user, capture 2026-07-19] Bank-room commands: balance / withdraw / deposit.**
  - **`bank`** prints the current balance: `Your balance at <Bank name> (#<n>) is:` then
    `On deposit: <N> copper farthings [<G> gold crowns]` — parse `(\d+) copper farthings` for the
    authoritative banked total in copper.
  - **`with <amount>`** withdraws, where **`<amount>` is in copper farthings**; coins arrive in the
    **largest denominations** (`with 2000` → 20 gold crowns). Success line: `You withdrew <amount> copper
    farthings.` (echoes the requested copper amount).
  - **Over-withdraw silently fails.** Requesting **more than the banked balance** produces **no output at
    all** — no error line. So verify a withdraw by watching for the `You withdrew …` success line (its
    absence within the reply window = failure), and/or read `bank` first and never request more than the
    balance.
  - **`dep <amount>`** deposits (amount in copper); confirmation names the actual carried denominations
    (`You deposit 5 platinum pieces, 29 gold crowns, 7 silver nobles.`), as noted above.
- **[CONFIRMED]** Item vs. coin disambiguation is by verb + shape. An **item** get is
  `You took <item>.`; an item drop is `You dropped <item>.` — the drop/hide verbs are **shared**
  with coins, so a colour-adjective item (`You dropped a silver key.`) is told apart from coin only
  by the trailing **coin noun** (`nobles`/`farthings`/…) and a numeric count. `You picked up …` is
  coin-exclusive (items never use it).
- **[CONFIRMED]** The pickup lines are the authoritative "the get landed" signal, and the client
  keys movement-resume (the acquisition gate) and collection dedup on them: `You took <item>.` per
  item (one line per collected item), and `You picked up <N> <coin>.` per coin get. **A
  `You picked up 0 <coin>.` is a FAILURE, not a success** — the character is at its carry limit and
  took nothing (the coins stay on the ground); for gate purposes the get has still *resolved*, so it
  stops the walker waiting on it, but nothing was collected. The item-get **failure** wording is not
  yet captured; until it is, a get that never yields a `You took` line is released by the settle
  timeout rather than by a confirmation.
- **[CONFIRMED — Paradigm-specific]** **Paradigm lets a single action name a count** —
  `buy <N> <item>`, `sell <N> <item>`, `get <N> <item>`, `drop <N> <item>`, `hide <N> <item>` — and
  its confirmation echoes the count with the **SINGULAR** item name: e.g. `You hid 35 orc-head.`
  (not `orc-heads`). **Stock has no such batching — one item per action.** Consequence for inventory
  tracking: a counted confirmation removes/adds N copies, not one, so the carried-list and running
  weight adjustments must strip the leading count and apply it N times, or the encumbrance estimate
  drifts (report `paradigm-20260812-201631`: 35 stashed orc-heads left the estimate ~1050 too heavy,
  so the cash "skip if Heavy" gate wrongly skipped a collect while the character was actually Medium).
  The bare-count confirmations for the other verbs (`You took/dropped <N> <item>.`, buy/sell) are the
  same shape but not all captured from a live session yet.
- **[CONFIRMED]** **A get/drop target given as the bare denomination adjective binds to any
  like-named item, not the coins.** `drop 1 silver` can resolve to a *silver ring* instead of a
  silver noble; the game picks whichever object matches first. Emitting the **full two-word coin
  noun** (`silver noble`, `gold crown`, `copper farthing`, `platinum piece`, runic `<word> coin`)
  forces the currency match. The client sends the full noun on every outgoing get/drop as a result.
- **[CONFIRMED]** **Another player grabbing ground cash emits a non-specific, count-less line:**
  `<Name> picks up some <coin-plural>` (e.g. `Tristian picks up some gold crowns`) — "some", the
  full coin plural, and **no trailing count and no trailing period**. It does **not** say how much
  they took, and it **may take part or all** of the pile. Consequence for automation: a witnessed
  third-party pickup of a denomination we've deferred makes our stored exact per-pile count stale
  and unrecoverable — the remaining amount can't be derived from the line.
- **[CONFIRMED]** **A bare `look` (or `l`) with no target re-displays the current room** — including
  the `You notice N <coin> here.` ground-cash survey line — so the exact remaining ground cash can be
  re-surveyed on demand. (A `look <direction>`/`l <dir>` peek renders the *adjacent* room's display
  instead and is peek-suppressed; only the target-less form surveys the room you stand in.) This is
  the recovery path for stale deferred counts after a witnessed third-party pickup: re-survey with
  `look` and collect the freshly-observed amount.

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
  **Self perspective (the character doing the training):** entering the train-stats screen breaks up
  OUR OWN party server-side, so our client must reset its own `PartyState` on train-stats entry to
  match — a **leader** clears its whole roster (party disbanded), a **follower** clears the
  "following `<leader>`" state (no longer following). Skipping this leaves a stale "following" state
  that makes the client **reject the leader's fresh re-invite**: both the `@join` handler and the
  invite auto-accept no-op on "already following `<leader>`", so the follower never rejoins (report
  `stock-20260801-002423`).
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
- **[CONFIRMED]** *(user, 2026-07-31)* `@comeback` is a **follower→leader telepath** — the follower
  asks the leader to come back and re-grab/re-invite it. It is messaging only; it must **never**
  drive a walk on the follower's own client. A follower sends it in exactly two left-behind cases:
  **(1)** after a disconnect+reconnect where it either doesn't see the leader in the room or the
  leader left before re-inviting; **(2)** when the follower is stunned / hit a movement-preventing
  affliction and the leader didn't wait for it to clear before leaving the room. The client's
  `ComebackRequester` is correctly telepath-only. A follower must not auto-navigate itself back to a
  stale self-selected walk-to target — and after **any** death every movement engine is cleanly
  stopped and every retained destination cleared (the same as hitting the Nav Stop button; no
  lingering halt, so a manual or remote nav action afterward runs freely), so nothing re-drives us
  into the room we died in (report `stock-20260731-082602`).
- **[CONFIRMED]** *(report 002413)* A **knockdown** is a movement-preventing (held) status. The
  hit lands as `You are knocked off your feet, and land with a heavy thump!` (third-person
  `{s} is knocked flat!`), then the standing status while down is **`You are flat on your back!`** —
  which is also what the server prints as the **move refusal** when you try to walk while knocked
  down (a bonk, no room redisplay, `MovementRefusalDetector` matches it). It clears with
  **`You get back on your feet.`**. The applied/clear pair maps to the `MovementPrevented` flag, so
  the local hold (`SelfHeldResponder` → `HeldGate`) holds our own loop for the duration exactly as a
  confused leader's does — the `.@held` pause a held follower sends the leader is eaten for a held
  leader / solo.
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
- **[CONFIRMED] 2026-08-04, user** — the **gang-channel speak verb is `bg`** (broadcast-gang), with
  `gb` and the `broadg…`/`broadgang` long forms as equivalents. **`gang` is NOT a speak command** —
  sending `gang <msg>` does not reach the gang. Anything we emit on the gangpath channel (remote
  `@`-command replies, level-up announces, party `bg @heal`) must use `bg`. The alias-collision
  table in `AliasEngine` already reserves `bg`/`gb`/`broadg…` as the gangpath forms.
- **[CONFIRMED] 2026-07-20, user** — when another player `look`s at us the wire prints
  **`<name> is looking at you.`** (`name` a single first-name token). The reactive-look-back
  feature (Settings → Talk) keys on this exact phrase; if a realm's wording differs it's a
  one-line regex tweak (`DefaultPatterns.PlayerLooksAtYou`). Not present in any imported MDB
  table — it's a live interaction line, so the wording is user-supplied, not data-derived.

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
| Buy — free item (stock) | `You just bought <item> for nothing.` |
| Buy — free item (ParaMUD) | `You just bought <item> for 0 copper farthings.` *([CONFIRMED] 2026-08-02, capture `paradigm-20260802-164843`)* |
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

## Status-effect wear-off lines *([CONFIRMED] 2026-07-14, user)*

- **`The effects of confusion wear off!` is a shared, generic wear-off** reused by
  many different confusion sources — a lot of confusion spells and monster effects
  emit the same line. The onset `You are confused!` is likewise generic. So from the
  wire alone the client cannot tell *which* confusion is on the character: a single
  applied line covers every confusion source, and a single wear-off line ends it.
- A few effects append their **own** specific wear-off (e.g. `The effect of hypnotic
  hands wears off.`) rather than the generic line, but they still share the generic
  `You are confused!` onset. They are therefore not independently distinguishable
  from text alone.
- **Consequence for condition tracking:** records that share an applied line are
  aliases of one effect and must be cleared as a group — when any of them wears off,
  all of them end. Keying each record's clear solely to its own end text strands the
  flag whenever a sibling with a specific wear-off never sees its matching line. (See
  `ConditionTracker`'s applied-line alias group-clear.)

## Poison prevents resting *([CONFIRMED] 2026-08-17, user + report `paradigm-20260817-092945`)*

- **While poisoned you cannot rest** — a `rest` (or meditate) issued while poisoned does
  **not** put you into the `(Resting)` state (poison refuses / breaks it), so the position
  never becomes Resting and the recovery you'd get from resting doesn't happen; you only
  get the slow standing regen.
- **Consequence for the auto-rest engine:** an optimistic "resting" latch armed on the send
  (`HealthManager._restInFlight`) never confirms while poisoned, and the interruption latch
  can't clear it (it needs a confirmed Resting first). So the client must **re-attempt the
  rest once poison clears** (the poison falling edge drops the stale latch) — otherwise it
  sits standing below the rest floor forever, which is what report 092945 hit.

## Confusion fumbles — actions fail and must be re-sent *([CONFIRMED] 2026-07-14, user)*

- Confusion does **not** block attacking (or acting) outright. Instead each action
  you send can *fumble*: the game consumes the command and it does **not execute** —
  surfaced as `You fumble in confusion!` (self) / `<name> fumbles about dazedly!`
  (others). The catalogue's `fumble` record carries the `LastActionFailed` flag (and
  `Confused`), and its wear-off is the generic `The effects of confusion wear off`.
- A fumbled action is **lost**. To actually perform it you must **re-send the same
  action**. Confusion can fumble several actions in a row; how many depends on the
  severity of the confusion.
- **Implication for auto-combat:** an attack command (`aa` / `a`) that fumbles is
  consumed without hitting, so the engine must **re-issue its last attack** when it
  sees a fumble rather than assume the swing landed. Otherwise the monster goes
  unattacked until the user manually re-sends — the reported symptom of "monsters in
  room but not attacking unless I manually send attack commands" (report
  `paradigm-20260714-093614`).
- **[CONFIRMED] 2026-07-28, report `paradigm-20260728-173036`: confusion is a single state — any
  confusion wear-off clears it entirely.** More than one record can hold `Confused` at once: a specific
  source (a monster confuse spell with its own wear-off line, matched by a user-defined message entry
  incl. caster/target/witness cases) plus the generic `fumble` record, which also carries `Confused` so a
  confuse whose *set*-line was missed is still recognised. When ANY real confusion wears off you are no
  longer confused, so **every** latched `Confused` source clears — not just the record whose wear-off
  fired. Clearing only the wearing-off record (or its applied-line aliases) strands the flag: a death-dog
  shriek that wore off left the co-latched `fumble` still holding `Confused`, keeping the nav
  ConfusionGate stuck. **Client encoding:** `ConditionTracker` clears every active `Confused` record when
  a `Confused`-carrying record's wear-off matches.

## One BETWEEN-ROUND spell per combat round; self-buff recast timers anchor on the 4-letter cast code *([CONFIRMED] 2026-08-16, user + report `paradigm-20260816-101702`)*

- **You may cast only ONE 0-energy "between-round" spell per combat round — total, across heals, buffs,
  debuffs, and use-item buffs.** These are the `EnergyCost = 0` spells (mageshield / holy armour, cures,
  regen HoTs, etc.); they ride *between* the round's main action, so one is free each round on top of your
  attack. A **second** between-round spell attempted the same round is rejected with **`You have already
  cast a spell this round!`**, and **the spell you just sent does NOT fire** — success or failure of the
  first doesn't matter, the round's single between-round slot is spent. **This line never appears for combat
  spells** (lbol / mmis / deathtouch / fireball are 500–1000 energy — the round's main action, not
  between-round), so it is purely the between-round coordinator's signal.
  - **Client encoding:** the between-round coordinator (`CastingDirector`) casts at most one between-round
    spell per round, gated on a latch cleared by the **combat round tick** — `TickEngine.CombatTickElapsed`
    (`NotifyRoundComplete`), the 5s combat heartbeat refreshed by damage lines. It must NOT clear on
    **`*Combat Off*`**: that fires per *kill*, so in a multi-mob room it lands several times a round and would
    re-open the slot mid-round (the recast storm's door). The bug that produced the "4 mageshields in a short
    span" storm was the coordinator's own one-per-round cooldown being cleared on every per-hit tick, letting
    it send several between-round spells a round. On the rejection the just-sent spell didn't fire, so its
    optimistic recast timer is dropped (it re-attempts next round) and the round's slot is latched spent
    (`CastingDirector.OnCastFailed`).
- **A self-buff's active/recast state is keyed to its own 4-letter cast code**, resolved from game data: the
  success line (`Spells` → *user definitions* → CasterMessage / AppliedMessage) starts the duration timer,
  and the buff's OWN wear-off (`AppliedEndsWith`) clears it. **Distinct buffs that merely share an applied /
  onset line must NOT cross-clear.** Unlike confusion (one shared state, many sources — a group clear is
  correct there), the five shields that all emit **`You feel protected!`** (mageshield #132, ethereal shield
  #4, holy/unholy armour #148/#149, heros tabard #859) are *separate* effects with their *own* distinct
  wear-offs. So a wear-off fires `ConditionTracker.ConditionEnded` only for the record whose own end-text
  matched — a sibling sharing the applied line is dropped from the active set (keeping flags honest) but
  does not fire the event, so it can't clear a different buff we actually cast.
- **[CONFIRMED, user 2026-08-16 + report `paradigm-20260816-232454`] The `stat` screen's buff readout is
  NEVER a fresh cast — ignore it for buff tracking.** On Paradigm, `stat` lists each active effect as
  **`You feel <effect>! (<remaining>s)`** (e.g. `You feel lucky! (411s)`, `You feel safe from evil! (12s)`).
  The effect text is *shared* across many records — one `You feel lucky!` line matched **11** catalogue
  records (bless + chant + several weapons/items) — so a readout can neither identify **which** buff is up
  nor legitimately "apply" one. Treating it as a cast falsely marked buffs active on **login** (the post-entry
  `stat` refresh), and because the tracker only fires on a not-active→active transition, that stale "active"
  state then **suppressed the confirm on the real manual cast** (a repeat applied line is no transition). The
  client keys off the trailing **`(<remaining>s)`** parenthetical to skip these readouts entirely
  (`ConditionTracker`); a genuine fresh-cast effect line has no parenthetical.
- **A hand-typed buff is confirmed by the CAST CODE, not the shared success text.** You type the 4-letter
  code (`bles`) → the client arms/refreshes that buff's timer anchored on the code (`CastingDirector.NoteManualBuffCast`,
  fed by the `OutboundCastObserver`), exactly as an engine cast does. The following success line just
  confirms it landed; identity comes from the code, never the ambiguous applied message.
- **[user 2026-08-16] Session vs drop for buff timers.** **Any** disconnect — manual, hangup, or an unexpected
  drop — **freezes** the buff timers (the buffs persist server-side through link-death); the Buff Watchdog
  display freezes at the drop instant too (its 1s heartbeat is a wall clock that keeps ticking offline). The
  first in-game prompt after reconnect **resumes** them shifted forward by the offline gap (same remaining),
  instead of clearing and recasting from full. Clearing (no buffs assumed) happens only on a **fresh character**
  (ProfileLoaded — a same-character reconnect does not reload the profile, so its paused timers survive) or when
  the offline gap exceeds the longest armed buff's full duration (they're surely gone by then).
- **[user 2026-08-17] Party-buff slots are party-only, and a superseding party buff covers self.**
  The party-bless slots are cast **only while in a party** (`PartyState.IsInParty`); solo, none fire —
  self-buffs come from the self-bless slots. A **single-target** party buff is cast per class-matched
  member and **never targets self** (self uses the self-slots); only a **party-wide** buff (`Spells.Targets`
  = Full/Divided Party Area, scope 13/10) lands on self. **Supersession:** a spell that carries **RemovesSpell
  (Abil 122)** removes the named spell (the Spell Book renders it "Removes <spell>"). So when a configured
  **party-wide** party buff removes a configured self-buff (e.g. **chant removes bless**), in a party we stop
  self-casting the removed one and let the party buff cover us — the Buff Watchdog shows that self-buff
  "covered by <party buff>". Only party-wide covers count (a single-target party buff can't cover self).

## Debuff slot spells — energy + targeting *([CONFIRMED] 2026-08-17, user + game-data trace, Paradigm 1.9.1)*

The Settings → Combat **debuff slots** (single-target debuff + AoE debuff) hold *between-round*
spells, not combat attacks. Two hard rules distinguish a valid debuff from a misconfiguration:

- **0 energy = between-round.** A debuff slot spell **must have `EnergyCost == 0`**. That's what
  separates a debuff from a combat attack spell: attacks cost energy (Paradigm `lbol`/`mmis` = 500,
  `fbal`/`dtch` = 1000), between-round spells cost 0 (`blin`/`frai`/`stnk`/`corr`-flesh = 0). Energy —
  not targeting — is the discriminator: `blin` (debuff, 0) and `lbol` (attack, 500) share the SAME
  `Targets` scope (8). A non-zero-energy spell in a debuff slot is an attack spell mis-slotted.
- **Targeting must fit the slot** (`Spells.Targets` scope):
  - **Single-target debuff** → a single-enemy scope: **Monster (4)** or **Monster or User (8)**
    (e.g. `blin`/`frai`/`corr`-flesh are 8).
  - **AoE debuff** → an area/room scope: **Divided Area not-self (3)**, **Divided Area incl-self (5)**,
    **Divided Attack Area (9)**, **Full Area (11)**, **Full Attack Area (12)** (e.g. `stnk`/`fbal` are 12).
  - The **party-area** scopes (Divided Party Area 10 / Full Party Area 13) are buffs/heals aimed at the
    party, **never** enemy debuffs.

  So a targeted spell can't be slotted as an AoE, nor an AoE as single-target (e.g. `stnk`, Targets 12,
  belongs in the AoE slot, not single-target). The client rejects a mis-slotted debuff before it casts
  and warns once in the program log.

**Gating:** the **single-target** debuff is gated by **Auto-Combat** (it's a pre-attack debuff, part of
the attack rotation); the **AoE** debuff is gated by **Auto-Nuke**.

## Guarded monsters redirect attacks *([CONFIRMED] 2026-07-14, user + wire capture)*

- Some monsters are **guarded** by others in the same room (e.g. a *brigand chief*
  guarded by *brigands*). A guarded monster **cannot be hit directly** while any
  guard is present: each attack aimed at it is **redirected to a guard**, announced
  by `<guard> moves to protect <protected>` — **no trailing period, no prompt
  prefix**, and both names are ordinary (multi-word) monster names.
- The redirect repeats — one guard interposes per attack — until **all guards are
  dead**, after which the protected monster is directly attackable. Confirmed on the
  wire in report `paradigm-20260714-115526`: three protect lines as each attack was
  shielded, then guard deaths, then the chief became hittable.
- **Implication for auto-combat:** when the protect line names our current priority,
  the engine keeps that priority "blocked" and, as **each guard falls**, re-issues an
  attack **by the priority's literal name** (`aa <priority>`) to test whether the
  guard wall is down yet. This is reactive (line-driven), not read off the game-data
  "guarded by" field. The block clears when the priority itself dies, on room change,
  or on a target-not-here / no-effect reply. Without this, killing the last guard
  emits a *Combat Off* with the chief alive but unengaged, and auto-combat stalls
  until the user manually attacks (`aa b`) — the reported symptom.

## Quest kill steps & monster placement *([CONFIRMED] 2026-07-16, user)*

- **A command-less quest step sourced from a monster's textblock is a "kill this
  monster" step.** The flag advances because the monster's **death spell** (or a
  room **`nomonster`** spell that fires when the room is cleared) grants the quest
  progress — you receive the flag by killing the monster, not by typing a command.
  So a crawled step whose Called-From is a `Monster #N` chain and carries no player
  command narrates `kill <monster> (<drop>)`.
- **A monster a quest requires you to kill is placed in a specific room** — the
  room's **NPC field** (`Rooms.json` `NPC` = the monster number) names it. That
  placement is the authoritative room a guide walks you to for the kill (e.g. queen
  ant #485 is placed at 9/717 via `room.NPC == 485`).
- **When the kill target is summoned rather than statically placed**, its Monsters
  `Summoned By` record resolves the room: either a room token directly (`Room 9/717`
  / `Group(lair): 1/531`), or a `Spell #N` that another NPC casts to summon it. In
  the spell case the summoner is the monster whose **`CreateSpell` == N**, and that
  summoner's own placement stands in as the target's room (e.g. *hydra head* is
  summoned by *hydra*'s CreateSpell, so you fight it where the hydra waits). (See
  `RoomSearchService.QuestKillRooms`.)

## Quest dialogue steps — NPC keyword dispatch *([CONFIRMED] 2026-07-16, user)*

- **A quest advances through NPC dialogue, not standalone room commands.** The
  player types `ask <npc> <keyword>`; the NPC's **root dispatch textblock** maps that
  keyword to a child textblock (`Action` is a `\n`-separated list of
  `keyword:textblock` chains, e.g. `crystal:7018`); the child block runs its own
  `Action` and eventually `giveability <flag> <step>` to grant progress. The child's
  **Called From** names its parent (`Textblock #N`), and the root dispatch block's
  Called From is the **`Monster #N`** — the NPC itself.
- **So a crawled step gated behind an NPC is recovered by walking its Called-From
  chain up to that `Monster #N`, then reading which dispatch keyword branches into
  the child that leads to the step.** That yields the exact `ask <npc> <keyword>` the
  player must type (e.g. Mandos quest: `ask archmage valduin crystal`,
  `ask kale mandos free`). The step is then re-anchored on the NPC so the guide links
  the NPC's placement room (same map used for kill steps). (See
  `QuestStepGraph.ResolveAsk`.)
- **Auto-shown blocks aren't askable.** Dispatch keywords `message`, `text`, and
  `greeting` are shown automatically on interaction (or as flavor), not typed — a
  step reached only through one of those has no `ask` command and isn't drafted as
  one.

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
| Bank deposit (manual or auto; multi-currency, may wrap) | `You deposit 1 platinum piece, 93 gold crowns, ... copper farthings.` |
| Corpse loot drop (bare keyword) | `N <keyword> drop to the ground.` |
| Room cash survey | `You notice ... N <coin> ... here.` |
| Move refused — no exit | `There is no exit in that direction!` |
| Move refused — blocked way | `You can't go that way.` / `You can't move that way.` |
| Move refused — shut door | `The door is closed.` |
| Room too dark to see (starves name + exits + Also-here) | `The room is very dark - you can't see anything.` |
| Room considerably darker (same starving) | `The room is pitch black...` |
| Guard interposes for a guarded monster (no trailing period, no prefix) | `<guard> moves to protect <protected>` |
| Incoming mob attack — miss (dark cyan; reveals a mob in a dark room) | `The <monster> <verb> at you` |
| Incoming mob attack — hit (dark cyan; reveals a mob in a dark room) | `The <monster> <verb> you for N damage!` |
| Thorns / ShockShield reflect (**white** line, follows the **red** hit that triggered it, inside a *Combat Engaged*…*Combat Off* window) | `The <item-wording> stab <attacker> for N damage!` — **[CONFIRMED 2026-08-15, user]** a worn item with the **ShockShield** property (value = max reflect damage, e.g. 5) strikes the attacker BACK for up to that much when the wearer is hit **physically**; fires after an **armor-block glance** (0 damage to us) OR a real hit. The item wording varies (`armour spikes`, `collar spikes`, …), so it's recognized by COLOUR (white/default, vs the red of a real incoming hit) + `for N damage` + a non-`you` target — NOT by wording. The **monster is the victim**, so it's OUR (or a party member's) damage, classified `Reflect`, not a monster hit |
| Monster leaves the room (e.g. dragged out by a fleeing player) | `<name> walks out of the room to <dir>.` **or** `<name> exits the room to <dir>.` — both confirmed; the "exits" form (no leading article) was the paradigm drag-out capture |
| Attacked a target not in the room | `Your command had no effect.` |
| Toll exit unaffordable | `You do not have enough to cover the toll of N gold crowns.` |
| Train success — stock (carries the attained level) | `You hand over <cost> and you receive training to attain level N.` |
| Train success — Paradigm/ParaMud (**level-less**) | `You hand over <cost> to train to the next level!` — a successful train with **no level number**; mutually exclusive with the stock line above, so auto-train infers the new level as current+1 |
| Server PvP announcement (**Paradigm-only**) | `Server PvP Message: <body>` — realm-wide server broadcast for PvP events; the kill form is `Server PvP Message: <killer> just killed <victim>!`, but other PvP bodies share the same `Server PvP Message: ` prefix. Not emitted on stock realms |

## Combat vs in-between spells (round energy cost)

- **[CONFIRMED]** *(2026-08-14, user)* A spell's **round energy cost** (`Spells.EnergyCost`)
  is the clean divider between a **combat/attack spell** and an **in-between/utility
  spell**:
  - **Combat spell** — `EnergyCost` between **1 and 1000**. It IS the round's combat
    action (spends the round's energy), so it competes with the weapon swing. Examples
    in Paradigm 1.9.1: `mmis` 500, `lbol` 500, `vamp` 1000, `fbal` 1000.
  - **In-between spell** — `EnergyCost` **0**. A heal / buff / cure that rides the shared
    in-between window and does NOT spend the round's combat action. Examples: `mend`,
    `armr`, `mshi`, `bles`, `cure` — all 0.
  - `AttType` does NOT distinguish them (both attack and utility spells carry an AttType,
    e.g. mmis and mend are both AttType 4). Energy cost is the reliable signal.
  - Used to classify a **manually-typed cast** during combat: a hand-cast combat spell is
    the user taking the round's attack (a user override — the engine must not re-send its
    auto attack that round), while a hand-cast in-between spell keeps the engine's
    resume-after-cast behaviour (it heals/buffs, then the engine resumes attacking).
  - **[CONFIRMED]** *(2026-08-14, capture `paradigm-20260814-210613`)* **A cast-code
    (`Spells.Short`) is AMBIGUOUS — it maps to several spells**, the player's plus monster
    variants, each with its own `EnergyCost`. In Paradigm 1.9.1 `vamp` is the player's
    **vampiric touch (1000, combat)** AND monster **vampiric hits / bite / rosebush (0)**.
    The player casts their own version, so a code is a **combat spell if ANY spell with it
    is** (energy 1–1000) — a last-writer-wins lookup that picked a 0-energy monster
    duplicate misfiled a hand-cast `vamp` as in-between and the engine re-announced its
    attack over it. Classify a shared cast-code by "any entry combat", not the last one.

## MegaMUD `messages.md` format *([CONFIRMED] 2026-08-17, user + decode of both stock/paramud files)*

The MegaMUD "Messages/Responses" catalogue ships as a plain-text `messages.md` (one per
game-type folder: `…(Stock)/Default/messages.md`, `…(Paramud)/Default/MESSAGES.md`). It is
the ORIGINAL source our Messages seed (and Triggers seed) were derived from. Structure —
**rigid, exactly 3 lines per record**, `\r\n`-terminated (validated: 612 stock / 840 paramud
records, zero misalignment):

- **Line 1** — `name : FLAGS(4-hex) : ACTION(decimal) : RESPONSE`
  - `name` — the message name. **A spell's message is named exactly after the spell; an item's
    after the item** (paramud names item procs after the item, e.g. `acid slasher`; stock used a
    generic effect name, e.g. `acid hits`). This name is how message→spell/item is linked.
  - `FLAGS` — the Effects checkboxes as an **additive hex bitfield**, identical bit layout to our
    `MessageFlags`: `0001` Blinded, `0002` Confused, `0004` Poisoned, `0008` Losing-HP, `0010`
    Movement-prevented, `0020` Attack-prevented, `0040` Diseased, `0080` HP-regen, `0100`
    Find-in-conversations*, `0200` Mana-regen, `0400` Find-in-text*, `0800` reserved*, `1000`
    Ends-combat, `2000` Last-action-failed, `4000` Use-when-chasing*, `8000` Disabled. (`*` = the
    find-mode/chasing bits the importer strips.) Validated end-to-end against message text.
  - `ACTION` — the Action radio, a top-down index 0–6 (0 Ignore, 1 Check-who's-in-room,
    2 Wait-until-wears-off, 3 Rest-full-HP, 4 Rest-full-Mana, 5 Don't-rest-run, 6 Hangup).
    **Read but NOT emitted** — every one of these is handled by the client's own engines /
    settings, so a MudPlay message is recognition only and carries no action.
  - `RESPONSE` — literal text the server-echo would trigger MegaMUD to send. **Read but NOT
    emitted** — a player response to a seen line is a *Trigger* in MudPlay, not a message.
- **Line 2** — the **Message** line (the pattern matched on the wire).
- **Line 3** — the **Ends-with** line (wear-off). **Blank when the effect has no wear-off.**

Tokens are `{dmg}` (numeric), `{target}`, `{source}` — already understood by
`CasterMessageMatcher` — plus `{1}` = a user-defined **wildcard** handled by the Triggers matcher.

**The .md is the source for THREE of our tables** (not just Messages): a record whose name matches
a Spell → Messages seed w/ `Spells#N`; matches an Item → Messages seed w/ `Items#N`; carries an
ailment flag / applied+wear-off → Messages seed (ConditionTracker detector); **everything else
(pattern + response/action) → the Triggers seed** (e.g. `1 life left` → say warning + hangup; the
separate `X end` records → response `stat` to refresh status). Decode → our record: Ends-with
present OR ailment flag → `AppliedMessage`(+`AppliedEndsWith`); else → `CasterMessage`.
