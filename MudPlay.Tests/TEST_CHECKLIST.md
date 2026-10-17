# Test checklist

The record of every file in MudPlay.Tests: what client code it covers, what it checks, and why that behaviour matters. One `###` entry per file, sorted by file name.

How to keep it current:
- Adding, removing, renaming or repurposing a test updates its file's entry in the same change.
- Every entry names the client behaviour it protects. A test that can't name one is deleted, not added.
- Tests drive the client's real input and output paths: game output through `ServerFeed`, chat through `ChatLines`, the wire through each engine's `SetWireSender`, time through injected schedulers (`ManualTimers`) and clocks (`ManualClock` or a `now` parameter). Never a test-only hook or a `…ForTests` member in production.
- A file that is test infrastructure (a helper, fixture, collection or guard) says so. A test entry point production still carries for a test is named in the entry of the file that uses it, with the reason it is kept.

### AbilBreakdownParserTests.cs
- Covers: Game/AbilBreakdownParser (AbilBreakdownParser, AbilBreakdown)
- Checks: `abil 145` rows parse per source; missing sources read as 0; the `total:` row is held out of the sum; negative spell rolls parse. A block closes on a prompt or a non-row line; the command echo doesn't open a block; back-to-back codes give two breakdowns.
- Why: the mana-regen reroller (ManaRegenReroller) decides rerolls from the `spells:` slice of a live Paradigm capture.

### AbilityNamesTests.cs
- Covers: Game/GameData/AbilityNames
- Checks: code to name mapping (named codes, the QuestFlagN range, the `Ability N` fallback, null for codes 0 and below), FormatId; trap-grant detection on class/race rows; ShadowRest (1103) detection per row and across the Classes table.
- Why: ability names show throughout the UI and quest text. The trap grant gates trap delegation, and ShadowRest gates the Health-tab toggle and stealth on rest.

### AcquisitionGateTests.cs
- Covers: Game/Inventory/AcquisitionGate (with Game/Map/MovementCoordinator)
- Checks: the Acquisition gate is held while deferred pickups, unconfirmed gets or a re-look are pending, and released when the last one resolves or on Dispose. Assert is idempotent, and stray confirmations do nothing.
- Why: the walker must not step out between combat clearing and the loot flush (the synchronous walker-resume race).

### ActionEmoteClassifierTests.cs
- Covers: Game/ActionEmoteClassifier
- Checks: the all-green colour gate; own-POV `You <verb>` actions vs non-action `You …` lines; known-player actions vs unknown actors; movement, party and chat lines from a known player are excluded.
- Why: ChatRouter and AppServices route green action/emote lines to chat. Uses real PlayPen captures.

### AddBuffDialogViewModelTests.cs
- Covers: ViewModels/AddBuffDialogViewModel
- Checks: mana-floor defaults, label and percent/absolute conversion text; Ok clamps the floor and returns the conditions; the reroll box range for Stock and Paradigm, including negatives; draw-item outcome tick boxes; no-redraw decks.
- Why: the buff slot dialog; Stock's no-redraw deck (user, 2026-10-02).

### AilmentSyncEngineTests.cs
- Covers: Game/Conditions/AilmentSyncEngine (with ConditionTracker, PartyRestSync)
- Checks: a bare `.@blind` / `.@held` say on apply and never an `off`; poison telepaths @wait without a say. Ignore<X> suppresses both say and wait, and a configured cure suppresses only the say. One @wait / @ok pair is held across several ailments. ReevaluateWaits reconciles when an Ignore is toggled. The say and wait are given again on re-following a leader.
- Why: MegaMUD parity; reports `paradigm-20260820-122200`, `paradigm-20260820-153540`, `paradigm-20261014-214037`.

### AliasEngineTests.cs
- Covers: Services/AliasEngine
- Checks: TryExpand matching (case, disabled, empty); `{0}` and `{N}` substitution with trimming; `^M` and `;` multi-step splits; no trigger-variable leakage. NameConflictReason rejects chat-channel prefixes and gossip/auction/broadcast/gangpath forms. IsDuplicate.
- Why: an alias must not hijack the user's own chat channels; input expansion in MainWindowViewModel.

### AlignmentBandsTests.cs
- Covers: Game/Calculators/AlignmentBands
- Checks: band title to value ladder; parsing `Alignment: X to Y` exit gates, including reversed and unknown; inclusive Admits window.
- Why: alignment-gated exits in MovementFilter / RoomExit (report `paradigm-20260827-144553`).

### AlignmentGearCheckTests.cs
- Covers: Game/Inventory/AlignmentGearCheck
- Checks: the alignment query goes out only on a prompt, only when asked (gear-set mismatch or the game saying alignment moved), at most once a minute, and never on a timer.
- Why: alignment-gated gear is stripped one line per item; avoid flooding `who`/`pro`.

### AlignmentTrackerTests.cs
- Covers: Game/AlignmentTracker (with Services/PlayerDatabase)
- Checks: a dark cloud marks the alignment stale and a `who` of ourselves clears it; SelfAlignment comes from our own player row. A dark cloud while Good reads Neutral and raises LeftGood. Paradigm `pro` EPs and min EPs; a Stock evil-only refusal narrows the range; Paradigm gives an exact range, which becomes a floor after a cloud.
- Why: drives gear blocks, spellbook seeding and the bug-report alignment line.

### AllyDroppedHandlerTests.cs
- Covers: Game/AllyDroppedHandler
- Checks: recognising a downed ally (roster member, recent former leader within the grace window; not self or strangers). Aid plus the AllyDown hold, and aid without a party heal. A re-invite only when leading and the ally is up. Health-reply release at the heal bar; the worst-case-climb poll timing and grace release; the master switch ends and resumes rescues.
- Why: the off-roster former-leader gap from a bug report; a non-healer must still `aid`.

### AnsiColorSchemesTests.cs
- Covers: Terminal/AnsiColorSchemes
- Checks: colour-blind schemes are 16 distinct opaque colours readable on black, with bold brighter. Saved scheme names parse; Custom lays user colours over the standard set; invalid custom entries are dropped; CustomDelta saves only the differences and round-trips.
- Why: Settings → General colour-blind palettes.

### AnsiPaletteBaseColorTests.cs
- Covers: Terminal/AnsiPalette
- Checks: redrawn base colours apply to foreground, background and bold-bright; the default fg/bg follow white and the backdrop stays black; 256-colour and true-colour cells are untouched; Changed fires only on a real change; a set that isn't 16 colours falls back to the standard set.
- Why: only how an index is drawn may change, never the index itself.

### AnsiPaletteCollection.cs
- Test infrastructure.
- Covers: (xUnit collection definition, no production class)
- Checks: serialises the tests that mutate process-wide AnsiPalette state.
- Why: keeps other threads from seeing moved base colours.

### AppPathsMigrationTests.cs
- Covers: Services/AppPaths (CopyMissing, FlattenDataSubfolder)
- Checks: the legacy FujinTerm copy backfills only missing files and never overwrites; the 3.0–3.2 `Data/` subfolder is lifted to the app-folder root, merging without clobbering newer flat files.
- Why: non-destructive upgrades from older installs.

### AreaRankingFilesSafetyTests.cs
- Covers: Services/AreaRankingFiles path building (through Services/PathGuard)
- Checks: - Hostile set, BBS and profile names (`..`, absolute, empty, slash) are refused by every read, write, move and delete, with canaries untouched. A refusal is logged once per operation and name. - Odd but harmless names still get a file inside the Simulator folder. - BBS, Simulator and set folders that are links are never followed.
- Why: names come from user-chosen folder names and drive recursive deletes. A bad path must never reach outside the set's Simulator folder (user, 2026-10-17 for linked set folders).

### AreaRankingFilesTests.cs
- Covers: Services/AreaRankingFiles, Game/Simulation/AreaRankingStore (over real files), Game/Simulation/RankingWorldWatch, Services/GameDataCache (table stamps), Services/GameDataSetManager (copy/move/delete vs rankings)
- Checks: - A saved ranking round-trips whole. A file of another world, owner, format or age, or a damaged or truncated one, is dropped and deleted. - Files follow character and BBS renames (including capitals-only), deletes and set replacement. Reads that race a forget, rename or reload are not put back. - The start-up sweep goes by the stamp inside the file, skips held, locked and linked files, and counts only what it deleted. Rankings are stamped with the table files actually read.
- Why: rankings must outlive the client yet never be trusted across game-data or character changes. User rulings 2026-10-11 (a set read in again keeps its rankings) and 2026-10-17 (linked set folders hold no rankings). The sweep must not stall start-up on locked files.

### AreaRankingStoreTests.cs
- Covers: Game/Simulation/AreaRankingStore (memory side)
- Checks: - Each set/character pairing gets its own ranking back, matched without case. A re-rank replaces it. - A switch or same-set reload keeps rankings of the same tables and drops changed or unrecorded ones on the next ask. ForgetSet works from another set. - A rename carries rankings across sets. Only MaxKept rankings are held, least recently shown first out.
- Why: the Simulator must never list a ranking made on other tables or for another character (user, 2026-10-11: a reload deletes nothing by itself).

### AreaRankingTests.cs
- Covers: Game/Simulation/AreaRanking (KeptNote, IsCurrentAt), Game/Simulation/RankingCheck
- Checks: the "Made HH:mm / date at Ln" note for a kept ranking, including level-changed, changed-group ("stats or gear", "reach" …), not-checked and no-record wording, and when a kept ranking counts as current.
- Why: the user must see when a listed ranking is stale relative to the character.

### AreaToursTests.cs
- Covers: Game/Simulation/AreaTours (Group, Order, SearchSources), Game/Simulation/AreaRank (Rank, Safe, Label)
- Checks: - A lair room joins its monsters' majority area. A tour walks nearest-unvisited and drops unreachable lairs. Large areas search only from the start. - Safe options rank ahead of deadly ones. A live loop record at or below the ranked level makes a loop safe; a higher-level record does not.
- Why: the risk that the area ranking puts a deadly area or a mis-ordered tour at the top.

### ArrivalRoomLearnerTests.cs
- Covers: Game/Recovery/ArrivalRoomLearner (with RoomTracker, LearnedArrivalRoomStore, LearnedArrivalLookup, StandardListContradictions)
- Checks: learning where a room's dead arrive from a witnessed death. Only the first display after the death counts, with no move or exit command in between and within the window; not arena deaths or unsure rooms. Standard-list and Albion agreement or contradiction; the board is marked edited after two contradictions; land-wide readings, exceptions and disputes.
- Why: corpse recovery routing, so the client walks to where the dead really arrive.

### AttackEstimatorTests.cs
- Covers: Game/Calculators/AttackEstimator, ViewModels/CharacterWorkshop/TrialDamageText
- Checks: routing only, not the formulas. No target means every swing lands; DR comes off both ends with a floor of 0; Stock bash counts DR three times; backstab meets the backstab defence; which readout rows show per attack type.
- Why: the Item Finder's trial damage readout.

### AttackTargetingRemoteHandlerTests.cs
- Covers: Game/Remote/AttackTargetingRemoteHandler (via RemoteCommandManager)
- Checks: `@atkprio` and `@atkorder` bare query replies; numeric sets write TargetPriority / AttackTiming to the profile; options that need a name; `command failed` on bad syntax; no write without AlterSettings.
- Why: remote-command parity and the permission gate.

### AutoActionDefaultsTests.cs
- Covers: Models/Profile/AutoActionDefaults
- Checks: Clone is independent; SameAs compares every flag; ReconcileToBase seeds the base from live, settles live to the base, or does nothing.
- Why: the base-modes reconcile skips needless writes and saves.

### AutoBuyManagerTests.cs
- Covers: Game/Inventory/AutoBuyManager
- Checks: the shop `list` readout closes on a prompt. Buys up to the cap, one per result (Stock) or one counted buy (Paradigm), capped by live stock and carried count. Refusals and can't-afford move to the next ware; stale or foreign refusals are ignored; leaving the room or not being in a shop drops the queue.
- Why: report `paradigm-20261005-091552` (the readout closes at the prompt); Stock refusal lines stalled the pump.

### AutoDepositManagerTests.cs
- Covers: Game/Cash/AutoDepositManager (with Game/Cash/CashManager gate, Game/Cash/StashRoomManager, Game/Map/AutoWalkManager / LoopRunner / AutoLairManager, Game/Light/AutoLightProvisioner + AutoLightShopRouter)
- Checks: a wealth/coin gate crossing while looping or lairing stops the engine, walks to the bank (re-reads `i`, sends `dep <wealth − keep>`) or to an off-route stash room (`hide`), walks back (a loop resumes at its nearest room) and restarts the engine. Idle and plain walk-to never reroute; an on-route stash is stashed on pass-through. Sell-first hand-over. Aborts, user stops and zero-deposit trips re-arm the gate after the cooldown. A dark return leg detours via a light shop (only with Auto-Light on and a stocking shop).
- Why: reports paradigm-20260730-125716 (gate wedged after a no-deposit trip), paradigm-20260819-054200 (pass-through `hide` ordering), "Report #5 / #1" (stop mid-reroute resurrected the old loop); toll-stale deposit amount; user 2026-09-30 / 2026-10-05 (nearest loop room, sale before bank).

### AutoDiscardInventoryWiringTests.cs
- Covers: Game/Inventory/AutoDiscardManager with the real Game/Inventory/InventoryManager and Game/Inventory/BulkCommandPacer, wired as AppServices wires them (Changed, FullInventoryParsed, ItemHidden, ItemSold).
- Checks: - A pile with a keep amount goes out once and its answers send nothing more. Run in both line orders (router or inventory first), on both realms, in both verbs, with and without the pacer. - Worn copies and the lit light from the real `i` listing count toward keep. Wear, remove and swap lines never trigger a discard. A discarded lit light is not counted again. - Paced piles stop when a hide is refused or sight is lost. Listings that land before the answers don't re-send. A reset waits for the next read.
- Why: the re-entry through Inventory.Changed with the pack already changed, which the by-hand tests can't show. Guards against a worn piece being taken off and dropped, and against a second discard for a copy already gone.

### AutoDiscardManagerTests.cs
- Covers: Game/Inventory/AutoDiscardManager (driven by hand: fake pack, resolver, clock and stand-in pacer).
- Checks: - What gets discarded: the keep floor counted in copies, not entries; worn copies and the lit light; the trip's party-carry raising the floor; Stock one command per copy vs Paradigm one counted command; drop vs hide mode; in-flight counting so its own confirmations don't re-send. - The pacer: piles taken back or re-checked when the switch, flag, keep amount, sweep, send gate or sight changes. Room-refused drops and hides are held per room and retried once per new room, called off by sale, drop or by-hand hide, and kept through disconnect and reset. - By-hand `EmitDiscard` (Chest Offload); the stash-ledger claim (`TryConsumeSuppressedHide`); quiet-read forgetting with the settle time.
- Why: report paradigm-20261016-031735 (party-carry floor). Rulings: user 2026-10-09 (worn counts toward keep; held hide retried per room), user 2026-10-10 (lit light is a pack copy). GAME_MECHANICS "Hiding items in a room (stashing)" and "Room item capacity: drop refusal". Guards against discards for copies not held (Stock speaks an unanswered hide aloud) and against re-sending a pile.

### AutoEquipCoordinatorTests.cs
- Covers: Game/Inventory/AutoEquipCoordinator, plus Game/Inventory/EquipmentManager directly (hazard-room slot hold, pre-rest wear diff)
- Checks: - The rest decision: ClassifyRest, ResolveTarget, pre-rest swaps on sit, Default on stand only once recovery is done, re-resting after a hand-typed rest's gear wear, sits confirmed after the rest already ended. - The combat, Auto-All and item-cast rules: SwapToDefaultOnCombat in and out of a fight, no pre-rest swap mid-fight, the Auto-All kill switch, the item-cast hold. - The movement, lair and boss sets (manual-move idle revert, combat debounce, keep-Bossing between bosses), held Equip-menu sets, re-applying gear that came back to the pack, and gear swaps never removing a worn room-hazard counter. A held set deselected mid-fight settles at the clear, or when a boss room comes first, keeping the same set or going through Default to the one wanted.
- Why: Reports `paradigm-20260815-130733`, `paradigm-20260826-132742`, `paradigm-20260827-125103`, `paradigm-20260914-154019`, `paradigm-20260915-061646`, `paradigm-20260915-124359`, `paradigm-20260916-104923`, `paradigm-20260924-113530`, `paradigm-20260929-115648`, `paradigm-20261008-202210` and `paradigm-20261015-202943`. Also the user's 2026-10-09 ruling on gear that comes back to the pack, and GAME_MECHANICS "Gear swaps interrupt resting, not meditating". Guards against gear thrash and against fighting in rest gear.

### AutoGetItemsManagerTests.cs
- Covers: Game/Inventory/AutoGetItemsManager. Also one stray theory for Game/Inventory/InventorySnapshot.ParseKeyEntry, which is a production helper but has nothing to do with this class.
- Checks: - `You notice` survey → `get` only for AutoCollect items. Gated by: the master switch, CannotBeTaken, Roomba sweep, look-peek, collect-after-combat deferral (flush or room change), the MaxToGet cap and the encumbrance hard cap / Light bracket (with coin dropped for Auto-sell items). - Stacked piles: per unit on Stock, counted on Paradigm. Wrapped surveys. - Floor dedup across re-renders and confirmations. The post-kill bare-Enter re-look (cooldown, walker hold until the room displays). `CollectNamed` for hang-up drops.
- Why: four ground orc-heads becoming eight-plus gets (re-render dedup); getting items from a peeked room; the walker leaving before the post-kill survey lands; Roomba headroom.

### AutoHazardCounterProvisionerTests.cs
- Covers: Game/Map/AutoHazardCounterProvisioner (with AutoModeController)
- Checks: `use` of the buff item when approaching a hazard room, with a debounce window and a fallback refresh. An immunity item skips it. A thirst prompt re-raises after a swig, or halts and says out-of-items. Followers raise on arrival. The master switch blocks and counts the skip.
- Why: report `-112011` (sunstone immunity); master switch (user, 2026-10-10).

### AutoLairManagerTests.cs
- Covers: Game/Map/AutoLairManager (with AutoWalkManager, LairTimerStore, MovementCoordinator, MovementFilter door rules)
- Checks: marker CRUD (mark/toggle/override/clear and change events), Start refusals, first dispatch to the wait room, Stop/Pause/Resume without a self-supersede loop. Engaging exits: leave when the Combat gate clears after a fight and the loot is in, short empty-lair window, full timeout once fighting, master-switch freeze. The wait room is chosen on the route the walk takes (doors, marked rooms on the way). Lairs with no route are left out and the no-route verdict is cached. A failed entry at an unopenable door is abandoned for the run (fair-odds doors retried); failed entries back off 2→60 s; an entry owed during a freeze is made when it lifts.
- Why: report stock-20260908-192900 (30 s idle in empty lairs, fight-end detection); user 2026-10-10 (picks failed and the run stood at the door; wait-room choice); ~1/sec reschedule loop regression; GAME_MECHANICS "Kill detection and monster-kill message order".

### AutoLairMarksOtherGameDataTests.cs
- Covers: Game/Map/AutoLairManager (MarksGameData, DropMarksOfOtherGameData, RestoreMarks, MarksOfOtherGameDataDropped)
- Checks: marks are stamped with the game-data set they were placed on and dropped (with a notice and an Info log) when another or no set becomes active, kept when the same set is re-read (case-insensitive). Marking under a new set starts a fresh set. An event's restore puts marks back only on their own set.
- Why: marks saved as a setup on the wrong set would land in another set's Loops folder, shared by every character on it.

### AutoLairSchedulerTests.cs
- Covers: Game/Map/AutoLairScheduler (PickNext, LairCandidate/LairDecision, with FlatTravelCostModel)
- Checks: empty or unreachable candidates give null. Default heuristic: the closest lair up by arrival (a closer lair that pops during the walk counts), never idling for a nearer lair, falling back to the least idle when none is ready, freshest tie-break. Throughput ignores idle. A null ReadyAt means ready now. Decision payload (wait room, approach, entry arrival, slack).
- Why: reported 112 s wait-room idle and "near lair popping soon beat a ready far lair" (hits per run).

### AutoLairSettingsTests.cs
- Covers: Models/Profile/AutoLairSettings + EncumbranceHopTimes, Game/Map/EncumbranceGatedTravelCostModel
- Checks: the shipped defaults the scheduler runs on (heuristic, engage timeout, Auto cost mode, flat hop time), the measured two-band hop times 0.7/1.7 s by bucket with the Unknown→Light fallback, and the encumbrance-gated model's hop→duration math (live encumbrance read, zero/negative hops, 0.1 s floor).
- Why: realm-correct hop timing out of the box; a 0 s/hop bucket would collapse scheduler scoring.

### AutoLightManagerTests.cs
- Covers: Game/Light/AutoLightManager
- Checks: A "pitch black" / "very dark" line posts one deduped LightSource need when AutoLight is on. Lit and penalized lines don't post. Disabled or disposed posts nothing.
- Why: originally the reactive light-need poster. Note that no client code consumes the posted need.

### AutoLightPlannerTests.cs
- Covers: Game/Light/AutoLightPlanner
- Checks: The ready/buy/reorder/none verdict for lit and dark routes. Carried, preferred and worn-illu coverage. Catalogue buy sizing from CarryHours. Reorder threshold, latching inputs and provisioning-off cases. The room-light spell as a coverage tier and spell-only mode.
- Why: the decision core behind auto-light provisioning (whether to detour to a shop and how many to buy).

### AutoLightProvisionerTests.cs
- Covers: Game/Light/AutoLightProvisioner
- Checks: The route plan only provisions (Buy/Reorder handed to the shop router, with a reorder latch) and never `use`s. Reactive `use` on a can't-see line (count-stripped name, preferred vs strongest, no downgrade, pending latch, bare-CR redisplay, room-light spell fallback). Predictive one-room lookahead `use`. `rem` on entering a seeable room. Burn-out clears the latches.
- Why: the over-lit-town report (route-wide ready suppressed), the 154840 / 175844 stuck-blind cases (burn-out latches), and the lights-a-torch-but-doesn't-recheck report (bare CR, GAME_MECHANICS "Light sources").

### AutoLightSettingsTests.cs
- Covers: Models/Profile/AutoLightSettings
- Checks: DTO defaults (CarryHours 6, ReorderThresholdMinutes 60, auto-pick, items allowed) and JSON round-trip.
- Why: the defaults are what the engine uses for a character with no saved AutoLight section (AppServices.ReadSection).

### AutoLightShopRouterTests.cs
- Covers: Game/Light/AutoLightShopRouter
- Checks: The provisioning detour: master-toggle and engine-walk gates, fewest-added-steps reachable shop, buy batch only for the missing delta, resume on the first copy or a found-en-route copy, timeout and unreachable-shop resume, user-redirect abandon, self-supersede Stopped not read as a takeover, and per-destination failure suppression.
- Why: a re-announced dark route must not loop the detour forever without buying (the supersede-Stopped report).

### AutoModeControllerTests.cs
- Covers: Game/AutoModeController
- Checks: kill switch vs all-toggles-off; off/on remember and restore exactly what was ticked, with base modes only when nothing is remembered. Reconcile is held under the switch; owed restores (climb, sprint, run); SwitchedByUserPress; Blocks counting per system; ReEnableOnReconnect with the switch off; ResetSnapshot on profile load.
- Why: user rulings 2026-10-09 and 2026-10-10 on master-switch semantics.

### AutoModeRemoteHandlerTests.cs
- Covers: Game/Remote/AutoModeRemoteHandler (via RemoteCommandManager)
- Checks: `@auto-<engine> on|off|toggle` write the profile's AutoMode; rest and heal are separate; `@auto-all` kills and restores; garbage replies `?`; `@settings` reports each engine.
- Why: remote control of the auto engines.

### AutoOpenChestOffloadTests.cs
- Covers: Game/Inventory/AutoOpenManager + Game/Inventory/ChestOpenTracker on a real InventoryManager / OutboundOpenObserver
- Checks: A flagged container arriving is opened once and its loot listed (and said to the room when ticked), separately from concurrent pickups. The list and coin tally follow items sold, dropped, hidden, given or batch-discarded, and full reads. Opens wait for a fight, a rest, another open, or flag changes. Containers carried at connect, recovered after a death, or taken by a hang-up are handled. Unanswered or stale `i` reads end an open as "not read" without poisoning later opens.
- Why: auto-open must never open a container the player kept shut, never mis-attribute loot, and the Chest Offload list must track the pack with no window open.

### AutoOpenManagerTests.cs
- Covers: Game/Inventory/AutoOpenManager
- Checks: The silent baseline at load, one open per new flagged copy, one at a time. The master switch, Roomba sweep, send gate, rest, sneak and busy-tracker holds. No retry after an empty open. Hang-up pick-backs, owed-across-reconnect and unwitnessed-death bookkeeping (OwedIfReturned / AwaitedFromDeath). Reset per character.
- Why: unit-level decision logic of the auto-open engine. The risk is opening containers the player meant to keep closed.

### AutoPartyManagerTests.cs
- Covers: Game/AutoPartyManager (with Game/TrainerMenuTracker and Game/Map/MovementCoordinator as real collaborators)
- Checks: - Inviting players seen in the room (InviteToPartyIfSeen, OnlyWhileNavigating, cooldown, follower gate, peek suppression, uninvite suppression) and accepting invites (JoinPartyIfInvited, stale-leader re-accept). - The `@join` nag (initial delay, cadence, cap, `{Ok}` / braced / `@`-command replies, join / uninvite / follower cancels, manual-invite echo) and the invite-wait loop hold (gate, timeout uninvite, Stock left-behind exception). - Party reform after a split teleport or a leader reconnect (deferred invite on arrival, origin-room wait, redisplay backstop, disband survival), plus re-inviting after the trainer menu.
- Why: - Reports `paradigm-20261004-061346`, `stock-20260801-002423`, `stock-20260924-145545`, `stock-20260924-175224`, `paradigm-20260930-192042`, `paradigm-20260923-092317`, `stock-20260731-015726`. - GAME_MECHANICS "A follower who can't move is left behind", and the chime-teleport arrival ordering (Party chapter). - Guards against invite spam, a stranded loop hold, and a leader walking off without a split party.

### AutoSearchManagerTests.cs
- Covers: Game/Map/AutoSearchManager (with MovementCoordinator)
- Checks: a clear-room `sea` after the classify delay; master and demand gates. The settle hold only when a get engine is armed; the reveal-in-flight window; release on "revealed nothing". Movement-start searches the start room once. Deferred during a fight with the gate held. Queued moves skip transit rooms; a death clears the deferred search.
- Why: reports `paradigm-20260818-060742`, `paradigm-20260829-212158`, `paradigm-20260909-004947`, `paradigm-20260909-055045`, `stock-20260730-163244`, `-074607`, `paradigm-20260820-090736`.

### AutoSellManagerTests.cs
- Covers: Game/Inventory/AutoSellManager
- Checks: sells flagged items down to the keep floor on entering a trading shop, one per result (Stock) or counted (Paradigm, stacks included). Refusals abandon the item; disabled or non-trading shops do nothing; the Selling gate holds until done and a timeout releases it.
- Why: report `paradigm-20260929-015929` (stacked entries).

### AutoTrainApplyTests.cs
- Covers: Game/AutoTrainManager (with StatParser, TrainerMenuTracker)
- Checks: the CP pass plans from the `train stats` form, not a buffed `stat`. SAVE only once the form shows what was typed; partial or unaffordable rows are kept; the form closing; never `train stats` into an open form. Fallback to a trusted `stat` and fresh CP; the user's own form; Stock holds on altered stats; the game's refusal ends the pass.
- Why: report `paradigm-20260930-160602`.

### AutoTrainSequenceBuilderTests.cs
- Covers: Game/AutoTrainSequenceBuilder
- Checks: 11 Enter-driven form payloads, with digits only for raised stats; HasRaise.
- Why: typing the wrong field order would train the wrong stats.

### AutoWalkManagerBoatTests.cs
- Covers: Game/Map/AutoWalkManager (BoatStep), Game/Map/BoatRoutePlanner
- Checks: The walker plans a sea-captain sailing when the boat is the only route: land leg to the dock, passage keyword, wait through transit churn, land leg from the port. The voyage deadline is sized from transit rounds, the backstop fails a voyage that never lands, and the sailing state (IsSailing / ETA / place) feeds the nav label. Party relay as leader. The PartyTollGate sailing check holds or closes boarding.
- Why: a voyage spans many unmapped rooms and can be silently refused at the dock, so the walk must neither hang nor misfire. The party must all be able to pay the fare.

### AutoWalkManagerBossRoomTests.cs
- Covers: Game/Map/AutoWalkManager (boss-room rules), Services/RouteChoicePlanner.EvaluateBossRoom
- Checks: Walk-around, halt-one-room-short with resume, and straight-through choices. A walk to a stop-before boss room ends one room short and reports the requested room. The enter go-ahead and the route rule don't carry to the next walk. The route-card fork is offered only when a way around exists.
- Why: report paradigm-20261007-224918 (walk passing through a boss room marked stop-before).

### AutoWalkManagerGreetTeleportTests.cs
- Covers: Game/Map/AutoWalkManager (greet / ask-transport step)
- Checks: The walker asks the NPC transport, completes on landing, re-asks via the watchdog when a failed skill roll leaves no render, isn't derailed by a source-room redisplay, and fails after bounded retries when the NPC refuses outright.
- Why: issue #455 (bard-only barmaid transport behind an unmodelled `testskill` roll that can fail silently).

### AutoWalkManagerPickedRouteTests.cs
- Covers: Game/Map/AutoWalkManager journeys (WalkJourney, JourneyFetch, Game/Map/DetourResume, RouteChoicePlanner)
- Checks: a route-card walk keeps its closed gates, teleport preference and agreed hazard rooms across re-plans, deferred starts, detour legs, recovery re-plans and errand restarts (ResumeJourney / DetourResume). A new walk, an arrival, a failure or a Stop (even idle between legs) ends the journey; stop-before boss legs count as legs to the goal. Side trips go on foot when possible and never teleport on a walk-it journey. A door-flow reset inside a silent redirect raises nothing. A card's agreed trade stays in force only for its own journey and connection, and ends when the key is in hand.
- Why: reports paradigm-20261008-173911 (re-plan went through the gates the card avoided), paradigm-20261008-174236 (teleport preference lost on legs).

### AutoWalkManagerRoomCommandRefusalTests.cs
- Covers: Game/Map/AutoWalkManager (empty-room command hold), Game/Map/TBInfoActionResolver.NeedsEmptyRoom
- Checks: A `nomonsters`-gated room command is held unsent while a monster is present, the room is cleared, and the command goes once it's empty. The walk fails with the reason if the monster never leaves, and the limit doesn't end a walk paused by combat. Ungated commands and empty-room refusals take the ordinary blocked-retry path.
- Why: report paradigm-20261007-194430 (`go hole` refused because a monster followed the party in).

### AutoWalkManagerSysGotoTests.cs
- Covers: Game/Map/AutoWalkManager (SysGotoStep), Game/Map/SysopGotoRoutePlanner
- Checks: The walker fires a `sys goto` jump when it's the only route, waits for the landing, then walks the land leg. The jump fails at the deadline if the landing never confirms. No jump with an empty location table.
- Why: the sysop jump is a multi-room shortcut with no graph edge, so arrival must be confirmed and a non-landing must not wedge the walk.

### AutoWalkManagerTests.cs
- Covers: Game/Map/AutoWalkManager, and through it RoomTracker, MovementCoordinator, EngineRecoveryGate, MovementFilter, SpecialExitDispatch, ParadigmPositionResolver and Game/Health/HealthManager (flee). It also holds tests for Game/Map/LoopRunner, LoopWalkHandoff and TeleportCatalog, and for ViewModels/Settings/TeleportChoiceViewModel.
- Checks: - Walking a route: wire payloads, advancing on confirmed landings, a refused move's retry and replan budgets (confusion exempt), replanning on desync / Suspect / Lost, and supersede (loud vs silent, deferred mid-step). - Pause and resume: gate holds, in-flight steps not re-sent, asking `rm` while Suspect, the stall watchdog, flee peek and hand-back across a disconnect. - Special exits: doors (open, abandon and go round, not here), hidden / text / teleport / multi-action / rolled-reveal / trapped exits; gate-item, boss-stop and item-announce holds; blocked-route failure text; which teleports an automatic walk may take; the loop walk-to handoff; the teleport catalog.
- Why: reports `paradigm-20260901-091527`, `-100523`, `-201514`, `paradigm-20260902-173754`, `paradigm-20260911-100708`, `-103315`, `paradigm-20260924-053941`, `-120529`, `paradigm-20260928-125823`, `paradigm-20261004-201232`, `-204354`, `paradigm-20261006-221209`, `paradigm-20261007-202509`, `paradigm-20261009-011133`, `-082958`, `paradigm-20260810-201953`, `paradigm-20260818-000725`, `stock-20260731-010401`, `stock-20260914-000112`. They guard against a walk that stalls, double-sends, drifts past its route or fails too eagerly.

### BackscrollViewModelTests.cs
- Covers: ViewModels/BackscrollViewModel
- Checks: the snapshot is the history plus the live screen and is frozen at open; the ring keeps capturing and a reopen catches up; on-screen rows keep their own write time; Find Next walks from newest to oldest and wraps.
- Why: live-appending lagged when following a fast leader; mistimed gutter stamps.

### BackstabMatchupCalculatorTests.cs
- Covers: Game/Calculators/BackstabMatchupCalculator
- Checks: verdicts (sure kill at the realm's hit ceiling, kill-if-it-lands, high roll only, can't kill, no damage, sees hidden) judged on the minimum stab after DR against the backstab defence.
- Why: Monster Intel's one-stab verdict (the user's 37-vs-35-HP example).

### BankBalanceProbeTests.cs
- Covers: Game/Remote/BankBalanceProbe
- Checks: parses `Your balance at X is:` / `On deposit:` blocks for Paradigm and Stock `(#N)`, single and multiple banks; QueryAsync sends `bank` and returns the balances. A query sent inside a bank updates only that bank and isn't the whole listing.
- Why: deposit routing and the remote bank-balance replies.

### BankCatalogStandingInBankTests.cs
- Covers: Game/GameData/BankCatalog.StandingInBank
- Checks: no only for a confirmed non-bank room in a set that lists banks; yes in the bank room at any confidence; unknown otherwise.
- Why: a remote @deposit-all is turned down only when we're sure we're outside a bank.

### BatchEditTests.cs
- Covers: ViewModels/GameData/Batch (Monster/Item/PlayerBatchChanges)
- Checks: only chosen fields apply and the rest are kept; tri-state on/off/leave; setting and clearing a spell rung; item min/max opt-in; permission grant and revoke.
- Why: batch edits must not clobber overlay fields the user didn't choose.

### BbsCredentialsTests.cs
- Covers: Models/Profile/BbsCredentials, SysopGotoLocation
- Checks: the legacy `HasSysopPowers` migrates one way into SysopStatus and is never written back; the three sysop flags and the goto table round-trip; the starter set is the default only when the field is absent.
- Why: profiles saved by releases through 3.50.x.

### BbsProfileFieldsTests.cs
- Covers: Models/Settings/BbsProfile, RealmProfile
- Checks: shipped connection, reconnect, terminal and realm defaults; JSON round trip; partial JSON falls back to defaults.
- Why: older bbs.json files must load with sane defaults.

### BbsRealmTests.cs
- Covers: Services/BbsProfileStore, ProfileService, RealmCatalog, Models/Settings/BbsProfile.RealmFor
- Checks: a pre-realm BBS migrates to one realm with its settings and data files; RealmFor matching; renaming a realm repoints its characters; the realm catalog add/rename/remove cascades.
- Why: the realm-tier migration from BBS-tier files.

### BbsRenameCascadeTests.cs
- Covers: Services/BbsProfileStore.Rename / Get, ProfileService.RenameBbs
- Checks: rename moves the whole subtree and rewrites the name; Get reconciles a stale Name to the folder; refuses an existing destination; credentials re-keyed on disk and in the loaded profile.
- Why: the old Delete+Save rename destroyed nested profiles; report `paradigm-20260825-102259`.

### BbsSectionCollection.cs
- Test infrastructure.
- Covers: (xUnit collection definition, no production class)
- Checks: serialises the test classes that build BbsSectionViewModel.
- Why: two VMs would load and save each other's scratch boards.

### BbsSectionCredentialStagingTests.cs
- Covers: ViewModels/Settings/BbsSectionViewModel
- Checks: credential edits on several boards all commit on Apply; returning to a board shows the staged value; Discard drops edits on boards no longer selected.
- Why: the first board's username, password and logon steps were silently dropped.

### BenignChatterMatcherTests.cs
- Covers: Game/BenignChatterMatcher
- Checks: known chatter shapes (departures, disconnects, also-here, the suicide-password block, the 2026-10-08 export families) are benign; player look headers; look-alike and genuine spell lines are left alone; gear swaps only for known players.
- Why: the unrecognized-line review queue (MessageCandidateWatcher) must drop noise without swallowing real spell lines.

### BfsMapperTests.cs
- Covers: Game/Map/BfsMapper (FindPath, FirstAvoidBlockingRoute, ComputeDistancesFrom/To, DistanceBetween, DistanceMemo, RouteUsesToll/LevelGate, CountTrapsOnPath, BuildLayout, NearestVisibleRoom, RetryImproves, blacklist and cache), Game/Map/RoomGraphManager (cast-pocket marking), Game/Map/RoomLayout
- Checks: - Shortest paths, and routing around avoids, level gates, un-openable hidden action exits, random cast-teleport exits and traps (fewest-traps mode). - The distance maps and the toll / level-gate flags on a route. - The planar map layout: axes, U/D only as hints, non-Euclidean stubs, cross-plane text portals stubbed, cast pockets hidden from outside, river-under-bridge tunnelling, the retry metric, blacklist hide with dangling stubs, keepVisible, and cache invalidation.
- Why: v1.11p Dragon's Teeth disjoint rooms, v1.11p map-1 sewers coverage loss, the Crypt go-portal, the Darkwood shortcut, the Warped Asylum overlay, Paradigm 9/1050 W un-openable exit, the right-click "Add to blacklist" wiring.

### BlockedExitDescriberTests.cs
- Covers: Game/Map/BlockedExitDescriber, RoomExit.TryParseWire
- Checks: blocked-exit text names the direction, the barrier room, the key name or id, and the picklocks/strength needed.
- Why: a blocked walk must not be confused with the door's far side.

### BoatPassageResolverTests.cs
- Covers: Game/Map/BoatPassageResolver
- Checks: dock CMD → passage → roll table → trip-spell chain resolves to the fixed disembark room; level/fare/checkability; voyage rounds; cyclic, empty-LinkTo and broken chains end safely and report the looped block.
- Why: the Paradigm dock 14/759 shape; a damaged set must not hang the walk.

### BoatRoutePlannerTests.cs
- Covers: Game/Map/BoatRoutePlanner
- Checks: picks the sailing with the fewest total land hops; gated sailings with filters and allowGated; null for unreachable source, destination or no docks.
- Why: the route must stitch both land legs, not pick the nearest pier.

### BossCatalogTests.cs
- Covers: Game/GameData/BossCatalog, the shipped BossDefs.seed.json
- Checks: boss = GameLimit 1; seed counts and shape (timed/cleanup/realm/rooms).
- Why: catches a bad seed regeneration.

### BossDeathLootTests.cs
- Covers: Game/Inventory/BossDeathLoot
- Checks: which chain record a death was, by name, exp paid, and exact-name match.
- Why: Grab All must target the record that drops (Lallim, Chisholm).

### BossGotoRoomsTests.cs
- Covers: ViewModels/CharacterWorkshop/BossGotoRooms
- Checks: goto options ordered by hop count, unreachable last, labels, dedupe.
- Why: the Bosses-tab Go-to dropdown.

### BossGrabAllTests.cs
- Covers: Game/Inventory/BossGrabAllCommands, Services/BossGrabClassifier, BossDef.Clone/MatchesSeed
- Checks: one `get` per distinct named drop; boss kind classification; leading-article name candidates; GrabAll persists as an overlay delta.
- Why: Grab-All-on-boss-death.

### BossReachFilterTests.cs
- Covers: ViewModels/CharacterWorkshop/BossReachFilter
- Checks: hit-magic min/max and spell-immunity filter options and bounds.
- Why: Bosses-tab reach filters.

### BossStoreTests.cs
- Covers: Services/BossStore
- Checks: seed/overlay resolve, tombstones, realm visibility, delta-only save, legacy-set copy, multi-client file pickup, StopBefore/GrabAll defaults, per-character flags, FindInCatalog.
- Why: realm-tier boss list; user rulings 2026-10-01 (StopBefore off list).

### BossTimerSyncCodecTests.cs
- Covers: Game/Remote/BossTimerSyncCodec
- Checks: encode/decode round trip to the second; number-vs-name identity; chunking; base64url safety; garbage throws.
- Why: @timer sync wire payload.

### BossTimerSyncCollectorTests.cs
- Covers: Game/Remote/BossTimerSyncCollector
- Checks: chunk reassembly per sender; data taken only on the channel asked (plus telepath after a say); own echoes ignored; broadcast switch; malformed sets dropped.
- Why: no unasked player can inject timers.

### BossTimerSyncMergeTests.cs
- Covers: ViewModels/CharacterWorkshop/BossTimerSyncViewModel.Classify/SameTimer, BossTimerSyncRowViewModel
- Checks: merge/sync/conflict/in-sync classification by standing; row state transitions and labels.
- Why: merge-window rules.

### BossTimerSyncStandingTests.cs
- Covers: Game/Remote/BossTimerSyncCollector (TimerSyncStanding)
- Checks: one-asked / one-of-several / not-asked for each request shape, including telepath confirmations and client-sent requests.
- Why: who may overwrite a timer without a pick.

### BossTimerTests.cs
- Covers: Game/Map/BossTimerMath, Services/BossTimerStore, Game/Remote/BossTimerQueryHandler, ViewModels BossTimerSyncViewModel/BossRowViewModel/MarkTimer/ManageBosses dialogs, BossCatalog regen
- Checks: window math per realm; mark/reset/persist across clients; kill detection (name, roster fallback, dark-den exp); @timer reply format and broadcast budget; the merge window end to end; cleanup bosses; summoned-loot drops.
- Why: reports paradigm-20260928-163621 and others; user 2026-10-07 and 2026-10-17 rulings.

### BoundedRun.cs
- Test infrastructure.
- Covers: test helper (no production class)
- Checks: runs work on a background thread with a time limit.
- Why: keeps an unbounded chain walk from hanging the suite.

### BuffClassifierTests.cs
- Covers: Game/Spells/BuffClassifier
- Checks: whole-party targets; buff vs instant vs attack shapes; alignment eligibility.
- Why: the "Add all blesses" report and the evil-priest picks.

### BuffCombatCalculatorTests.cs
- Covers: Game/Spells/BuffCombatCalculator
- Checks: only buffs actually cast on us count; whole-party in a party; no double count.
- Why: user 2026-09-30.

### BuffConflictAnalyzerTests.cs
- Covers: Game/Spells/BuffConflictAnalyzer, BuffAffectSet
- Checks: co-landing; remover resolution and tooltip; Abil-122 removes; self-bless candidate picks and order; one-directional losers/removers.
- Why: zeal/greater-zeal; RemovesSpell conflicts.

### BuffDefenseCalculatorTests.cs
- Covers: Game/Spells/BuffDefenseCalculator
- Checks: AC/DR from self-applicable buffs; blur/prot-evil/shadow/vile ward folds; tokens skipped.
- Why: defence projection.

### BuffManaUpkeepCalculatorTests.cs
- Covers: Game/Spells/BuffManaUpkeepCalculator
- Checks: mana per second per slot and total; guards; effective maintenance under a remover.
- Why: Buff Watchdog upkeep readout.

### BuffOffenseCalculatorTests.cs
- Covers: Game/Spells/BuffOffenseCalculator
- Checks: shadowform folds; lowest-roll mode; stealth skipped when up; AffectsOffense.
- Why: report paradigm-20261002-142319 (Monster Intel sure-kill).

### BuffPriorityOrderTests.cs
- Covers: Game/Spells/BuffPriorityOrder
- Checks: category buckets; manual/top-down ordering; stable remover-first reorder.
- Why: cast order matches the shown order; Stock collision ordering.

### BuffSlotRowViewModelTests.cs
- Covers: ViewModels/BuffSlotRowViewModel
- Checks: name abbreviation and recast text; All/Self independence; member auto-assign; solo visibility; overwrite tooltip; self-activation hook; learned flag; negated marks.
- Why: the "unchecking All cleared Self" bug; user 2026-10-17 (negates).

### BuffTimerKeeperTests.cs
- Covers: Game/Spells/BuffTimerKeeper (+ CastingDirector, AutoHazardCounterProvisioner)
- Checks: timers stand still out of game, persist across restart, are dropped for another realm/profile, tossed on an in-game profile swap, wiped by a found death.
- Why: user 2026-10-10 (pause timers while out).

### BuffWatchdogRowViewModelTests.cs
- Covers: ViewModels/BuffWatchdogRowViewModel
- Checks: fill and recast-marker math; covered/conflict states; negative-margin red zone; draw-card name.
- Why: watchdog bar rendering.

### BugReportAutomationSectionsTests.cs
- Covers: Services/BugReportAutomationSections
- Checks: triggers/aliases/macros listed, account-detail definitions omitted, chat censored; death history.
- Why: privacy in bug reports.

### BugReportBudgetTests.cs
- Covers: Services/BugReportBudget, BugReportBuilder.Render
- Checks: oldest entries trimmed from the largest section; weights; wire not counted; giant static sections cut; code fence closed; byte budget held after masking.
- Why: the 1000 KB report cap.

### BugReportBuilderTests.cs
- Covers: Services/BugReportBuilder (FileName, Render, RenderStateOnly)
- Checks: file name; issue-first layout; placeholder; state-only render.
- Why: report format.

### BugReportChatCensorTests.cs
- Covers: Services/BugReportChatCensor
- Checks: normalisation and hash pins; listed-word matching; chat-only masking (rows, wraps through the emulator, sent commands, log messages, raw wire); shipped table sanity.
- Why: public issue reports must not carry slurs.

### BugReportHoldsAndWaitsTests.cs
- Covers: Services/EngineStateWatch, BugReportHoldsAndWaits
- Checks: state-change stamping and history; thread-safe snapshots; movement gates and send-gate rendering.
- Why: the "Holds and waits" section for "it just sat there" reports.

### BugReportLogSectionsTests.cs
- Covers: Services/LogService (problem ring), BugReportLogSections
- Checks: the warn/error ring outlives noise; program log formatting; diagnostics-off and cleared notes.
- Why: report log sections.

### BugReportScrubTests.cs
- Covers: Services/BugReportScrub, BugReportBuilder render/prescrub
- Checks: login/passwords masked in every section, including wrapped, colour-split, corrected and non-ASCII values; suicide-password star echoes; exemptions and notes.
- Why: account-detail privacy.

### BugReportWireAndContextTests.cs
- Covers: Services/WireBuffer, BugReportBuilder.RenderRawWire, ClientHeartbeat, BugReportContext
- Checks: arrival marks; raw-wire rendering; heartbeat cadence; startup lines; home-relative paths.
- Why: report wire/context sections.

### BulkCommandPacerTests.cs
- Covers: Game/Inventory/BulkCommandPacer
- Checks: window of 6; release per prompt; min gap; answer timeout; rate-limit backoff; owner cancel; mayGo veto.
- Why: Stock action-delay queue limits.

### BulkObservableCollectionTests.cs
- Covers: Services/BulkObservableCollection
- Checks: ReplaceAll raises one Reset.
- Why: the O(n²) startup fix.

### CarriedStealthPenaltyTests.cs
- Covers: Game/Stealth/CarriedStealthPenalty
- Checks: hopeless-sneak verdict from carried items vs the stealth reading and the user threshold.
- Why: report paradigm-20261007-213809 (log raft).

### CashManagerTests.cs
- Covers: Game/Cash/CashManager
- Checks: per-currency collect/ignore/discard; two-word coin nouns; auto-deposit gates and re-arm; collect limits; collect-after-combat defer; encumbrance gate and cascade; death-stall re-get.
- Why: many paradigm/stock reports cited inline.

### CastCoordinatorTests.cs
- Covers: Game/Spells/CastCoordinator
- Checks: wire emission; cooldown; failure latches; independent attack vs between-round slots; already-cast attribution.
- Why: reports paradigm-20260923-103506, -20260916-113009, -20260831-091839.

### CastWalkTeleportTests.cs
- Covers: Game/Map/RoomGraphManager, RoomTracker, AutoWalkManager, LoopRunner, MovementFilter (cast-on-walk exits)
- Checks: exit landings and item gate; landing booking from two or one displays, dark/blind/follower cases; party-split hold and reinvite for walks and loops.
- Why: reports paradigm-20261007-134305 and -134824 (golden idol passage).

### CasterMessageMatcherLongLineTests.cs
- Covers: Game/Spells/CasterMessageMatcher (literal walk)
- Checks: the walk never refuses a regex match (catalogue + exhaustive small cases); floor lists reach few regexes.
- Why: report paradigm-20261009-164508 (freeze on long lines).

### CasterMessageMatcherTests.cs
- Covers: Game/Spells/CasterMessageMatcher
- Checks: placeholder tokenising; captures by role; target/spell confirmation; damage capture; shorthand target resolution.
- Why: cast confirmation for timers and cures.

### CastingDirectorTests.cs
- Covers: Game/Spells/CastingDirector (+ CastCoordinator, ConditionTracker, TickEngine)
- Checks: heal tiers and priorities; mana gates; cures; buff slot walk, timers and margins; burst-settle; pause/resume and restore; party heal, cure and bless; negates; draw items; item casts.
- Why: dozens of reports cited inline; user rulings 2026-10-10 and 2026-10-17.

### CharacterCalculatorMatchupProfileTests.cs
- Covers: Game/Calculators/CharacterCalculator.BuildNormalAttackProfile, MonsterMatchupCalculator
- Checks: armed vs unarmed profile; rounds-to-kill positive.
- Why: Monster Intel rounds-to-kill column.

### CharacterCalculatorTests.cs
- Covers: Game/Calculators/CharacterCalculator
- Checks: CP gain step and stat cost (Stock cap); HP/mana/kai/regen realm branches; spellcasting bonus.
- Why: realm formula divergences.

### ChatHistoryStoreTests.cs
- Covers: Services/ChatHistoryStore
- Checks: chronological entries; day separators; seed and clear; reseed on profile swap.
- Why: the PVE/PVP "same conversation log" bleed.

### ChatLine.cs
- Test infrastructure.
- Covers: test helper (another player's line on a chat channel, in the game's own wording per channel)
- Checks: n/a (helper for ChatLines).
- Why: tests hear chat as the game prints it.

### ChatLines.cs
- Test infrastructure.
- Covers: test helper: dispatches a ChatLine's wire text through the MessageRouter a ChatRouter wraps
- Checks: n/a (helper).
- Why: chat reaches handlers through the client's real router path, not a test-only dispatch.

### ChatLinesInertTests.cs
- Covers: Terminal/ChatLineDetector, LineExtractor chat lane, ConditionTracker, MessageRouter, ChatRouter
- Checks: player chat never drives game patterns; chat still classified.
- Why: report paradigm-20260921-053754 (broadcast "flat on your back").

### ChatRouterTests.cs
- Covers: Game/ChatRouter (+ TelepathPacer)
- Checks: channel classification; telepath send/confirmation pairing and whose send; stale discards; not-sent/ambiguous telepaths; yells/says/realm/server events.
- Why: misattributed telepath replies (paradigm-20260915-055754); timer-sync standing.

### ChestContentsReaderTests.cs
- Covers: Game/GameData/ChestContentsReader
- Checks: loot-tree decode, at-least-once odds, min/max count, MinBase textblock quirk.
- Why: the blackwood chest showing no contents.

### ChestLootLedgerTests.cs
- Covers: Game/Inventory/ChestLootLedger, ChestOffloadPlanner.MatchContainer, OutboundOpenObserver
- Checks: only each open's gains count; sales come off; capped at carried; typed opens.
- Why: Chest Offload list accuracy.

### ChestOffloadItemRowTests.cs
- Covers: ViewModels/CharacterWorkshop/ChestOffloadItemRow
- Checks: sell/discard reconcile math.
- Why: confirmed-count bookkeeping.

### ChestOffloadPlannerTests.cs
- Covers: Game/Inventory/ChestOffloadPlanner
- Checks: carried gains; fewest-shops grouping.
- Why: sell-tour planning.

### ChestOpenTrackerTests.cs
- Covers: Game/Inventory/ChestOpenTracker, OutboundOpenObserver
- Checks: open flow reads only its own loot; room announce opt-in; persistence; sales/hides; queued, typed and abbreviated opens; master switch and refusal.
- Why: user 2026-10-10 (stop the reads when the switch is off).

### ChestSellTourGameDataTests.cs
- Covers: Game/Inventory/ChestSellTour
- Checks: a game-data switch cancels the tour in any phase.
- Why: room numbers change with the set.

### ChestSellTourTests.cs
- Covers: Game/Inventory/ChestSellTour
- Checks: walk/sell/move-on; never sells more than chests gave; quiet window; stops and skips.
- Why: sell-tour safety.

### ClassCapabilitiesTests.cs
- Covers: Game/Calculators/ClassCapabilities
- Checks: smash classes from TBInfo; stealth and martial-arts abilities.
- Why: capability from game data.

### ClassTitleTableTests.cs
- Covers: Game/GameData/ClassTitleTable
- Checks: title → class and level range; format.
- Why: party level/class inference.

### ClassesSectionViewModelTests.cs
- Covers: ViewModels/GameData/Tables/ClassesSectionViewModel.FormatExpModifier
- Checks: exp modifier shown with the 100 baseline.
- Why: Classes-tab display matches the game.

### CleanupLogoutOrchestratorTests.cs
- Covers: Game/CleanupLogoutOrchestrator
- Checks: arming gates; wait for safe; exit; completion on saved/menu/prompt; timeouts; interruptions and the cap; latch and reset.
- Why: report stock-20260904-230111; user 2026-10-09 (master switch).

### CleanupWarningWatcherTests.cs
- Covers: Services/CleanupWarningWatcher
- Checks: shutdown warning parse (wrapped, CSI, chunked); latest wins; ETA.
- Why: nightly cleanup log-off trigger.

### CombatCalculatorTests.cs
- Covers: Game/Calculators/CombatCalculator
- Checks: hit caps and floors, dodge, prot-good/vile-ward per realm; backstab; accuracy vs `stat all`; melee and martial-arts damage; crit; swings; Stock landing percent.
- Why: Paradigm `stat all` captures; GAME_MECHANICS caps.

### CombatCastingDirectorContentionTests.cs
- Covers: Game/Combat/CombatManager + Game/Spells/CastingDirector wired together
- Checks: self-heals every round never starve the attack-spell resume.
- Why: the "won't re-engage after buffing/healing" stall.

### CombatEntryRefusalHandlerTests.cs
- Covers: Game/Map/CombatEntryRefusalHandler
- Checks: combat-gated entry → `break`, then revert the move; only while an engine drives a pending move; anchored match.
- Why: a walker spinning on "may not enter … while in combat".

### CombatLineClassifierTests.cs
- Covers: Game/Combat/CombatLineClassifier, MonsterAttackLine
- Checks: colour+wording classes (miss/block/hit/reflect/poison/smash); room titles untagged; captured bytes end to end; ledger tags.
- Why: report paradigm-20261010-215619 (room title read as a miss).

### CombatManagerBreakHoldTests.cs
- Covers: Game/Combat/CombatManager (user-break hold) + CombatStateTracker, OutboundBreakObserver, HealthManager
- Checks: what sets the hold (typed, @do, @party); no attack path fires under it; every way it ends; silent-round look-and-judge, the 30 s back-off, and the heartbeat sending a look another re-display's cooldown held back; flee still moves.
- Time: one `ManualClock` read by every component in the harness (combat manager, classifier, cast coordinator, death watcher, state tracker); each fed line lands 1 ms after the last, Tick() is a 5 s round, and nothing waits on real time.
- Why: user 2026-10-10 rulings on manual break.

### CombatManagerSpellsTests.cs
- Covers: Game/Combat/CombatManager (spell economy) + CastCoordinator, MonsterDeathWatcher, TickEngine
- Checks: spell vs weapon choice; room-channel lifetime; arrival settle and moves in flight; MaxCasts counting; corpse-cast guards; resumes after interrupts; manual overrides; overrides and immunity; alternating/cycle orders; self-defence; attack timing; room-spell kills as deaths.
- Time: one `ManualClock` read by every component in the harness (combat manager, classifier, cast coordinator, death watcher); each fed line or arrival lands 1 ms after the last, Tick() is a 5 s round, and nothing waits on real time. A fight that runs past its opening round is fed the game's `*Combat Engaged*`, or the engage-verify window (5 s) re-displays the room as it would live.
- Why: dozens of paradigm reports cited inline.

### CombatManagerTests.cs
- Covers: Game/Combat/CombatManager (and MartialArtsCommand, EngineSendGate).
- Checks: - Target pick (Target Order and priority, relationship filter, Min/Max window), the attack wire command, room-clear and re-pick, kill detection (exp line, unattributed death, AoE re-parse). - Attack-last / attack-after re-fire, follow-leader and follow-member targeting, guard redirect, weapon and alternate no-effect chain, magic-hit gating. - Backstab and Hit-and-Run, interrupt resume after Combat Off, engage-verify CR, dark-room CR suppression.
- Why: many reports (e.g. paradigm-20260814-230258, -20260922-130230, -20261007-185740, stock-20261004-150645); stops a stalled or doubled attack.

### CombatProfileGroupTests.cs
- Covers: Game/Combat/CombatProfileGroupCopy, CombatProfileManager; ViewModels/Settings/CombatProfileStagingSession.
- Checks: every profile field belongs to exactly one "include in profile" group; shared groups survive a profile switch; Settings staging of the shared flags and the Party tab.
- Why: a field that no group copies would silently stop switching with profiles.

### CombatProfileStagingSessionTests.cs
- Covers: ViewModels/Settings/CombatProfileStagingSession.
- Checks: a Settings commit keeps SchemaVersion, so the back-fill never re-runs.
- Why: a save that wiped the version overwrote every profile with the active one's values.

### CombatRedisplaySettleTests.cs
- Covers: Game/Combat/CombatRedisplaySettle.
- Checks: a combat line in an apparently empty room holds the loop gate; the gate releases on the next observation or the timeout; no hold when a hostile is visible.
- Why: a loop stepped past a pursuing hostile ("skipping past hostile targets").

### CombatSessionTrackerTests.cs
- Covers: Game/Combat/CombatSessionTracker (with RoundDamageTracker); AppServices.IsPartyMemberOrSelfName.
- Checks: hit, crit, backstab and miss counting; dodge vs miss de-dupe; per-round damage; spell and proc rows; retraction of a cast-emote miss; follow-up spell lines.
- Why: the Session Stats panel and bug-report figures (paradigm-20260929-063213).

### CombatSettingsTests.cs
- Covers: Models/Profile/CombatSettings, CombatSpellSlot.
- Checks: defaults; a round-trip through JsonStore.Options (enums by name, as profiles store them); older saves of the round-totals row follow the table switch.
- Why: settings must persist and default conservatively.

### CombatSpellChooserTests.cs
- Covers: Game/Combat/CombatSpellChooser.
- Checks: backstab gate, multi-attack 1→2 hand-off, normal→alternate→weapon cascade, mana gating and caps, per-target latch, debuff once-per-room/target, level/resist/target-type blocks, overrides, drain, entry-debuff reserve.
- Why: per-round spell choice (paradigm-20260908-210406, -20260902-160110, -20260820-082741, …).

### CombatSpellIndexTests.cs
- Covers: Game/Combat/CombatSpellIndex.
- Checks: a cast code is a combat spell when its EnergyCost is 1-1000; ambiguous codes resolve as any-combat-wins.
- Why: `vamp` was misfiled as an in-between spell.

### CombatSpellProfileTests.cs
- Covers: Game/Combat/CombatSpellProfileMatcher, CombatSpellProfileReport, CombatProfileManager; Models/Profile/CombatSpellProfile; Game/Inventory/EquipmentWeaponSync.
- Checks: @profile name matching, roster/report text, capture/apply/clone deep copies, Default-set weapon sync, schema back-fill, engage-log dedupe.
- Why: profile switches must carry every slot; log noise (paradigm-20261010-145330).

### CombatStateTrackerTests.cs
- Covers: Game/Combat/CombatStateTracker.
- Checks: - When the Combat gate holds or releases; engaged-target abandonment; HasHostileMonster / HasRoomNpc; the see-hidden and failed-sneak clears. - Break-before-run when auto-combat is toggled off; monster-count window; the InCombat drive; the idle-stall watchdog.
- Why: walker hangs or walks off mid-fight (paradigm-20261010-145330, stock-20260914-003246, paradigm-20261007-143049).

### ComebackRequesterTests.cs
- Covers: Game/Remote/ComebackRequester (with FollowMoveObserver, MovementRefusalDetector, PartyManager, TooHeavyWaitSignal).
- Checks: - One @comeback when left behind; none for leave, uninvite, own move, death or teleport. - Held / retried / dropped requests; leader answers (refuse vs coming); Stock self-`join`.
- Why: GAME_MECHANICS "@comeback (follower → leader)"; paradigm-20260922-085609.

### CommandHistoryNavigatorTests.cs
- Covers: Services/CommandHistoryNavigator.
- Checks: Up/Down recall, stash and restore of the in-progress line, clamping, Reset, and history changing mid-browse.
- Why: input recall UX.

### CommandHistoryTests.cs
- Covers: Services/CommandHistory.
- Checks: newest-last order, a 10-entry cap, skipping blanks and consecutive duplicates, and the Changed event.
- Why: input recall UX.

### CompletedQuestBonusesTests.cs
- Covers: Game/Quests/CompletedQuestBonuses.
- Checks: a completed quest step's TBInfo `addability` bonus is returned; a step not completed yields nothing; a blank class name resolves to no class id.
- Why: Monster Intel's per-capture read should stay cheap.

### ConditionTrackerCastReachTests.cs
- Covers: Game/Conditions/ConditionTracker + CastReach.
- Checks: a shared applied line latches only the records whose caster could be here (monsters, room spell, PvP strangers); a cast line decides first; holds and blinds are never left out; logging and the bug-report value.
- Why: false Confused latches (freezing family; user ruling 2026-10-17).

### ConditionTrackerRosterTests.cs
- Covers: Game/Conditions/ConditionTracker, Game/Combat/RoomEntityClassifier (RosterIsWhole), RoomEntryWatcher, the shipped message seeds.
- Checks: a caster that followed in leaves nothing ruled out; a named caster not listed breaks the roster; paralysis is always a hold; seed flags and Ids.
- Why: a held character must never read as free; user ruling 2026-10-17.

### ConditionTrackerTests.cs
- Covers: Game/Conditions/ConditionTracker.
- Checks: applied/ends latching; shared-line narrowing by cast line; shadowing by a longer applied text or another spell's cast line; negated casts; flag-wide wear-off; stat readouts; fumble ActionFailed; hold lines; shipped-seed cases.
- Why: stuck conditions (paradigm-20261011-165726, -20260904-214452, -20261002-120516).

### ConfirmServiceTests.cs
- Covers: Services/ConfirmService; Models/Settings/ConfirmSettings.
- Checks: every confirm returns true without a prompt when its flag is off; defaults are off.
- Why: the confirm settings gate the dialogs.

### ContinuationPump.cs
- Test infrastructure.
- Covers: test helper (SynchronizationContext pump), used by AutoTrainApply and GameDataChange/Switch tests.
- Checks: n/a (not a test class).
- Why: deterministic async continuation order.

### ConversationMessageInlinesTests.cs
- Covers: Views/ConversationMessageInlines (Segment, EmoteImageSize).
- Checks: URL splitting and punctuation trimming; picture-emote size held to the line height.
- Why: clickable links; emotes overdrawing rows.

### ConversationViewModelTests.cs
- Covers: ViewModels/ConversationViewModel, ConversationRowViewModel; ChatHistoryStore.
- Checks: at the store cap, add and trim happen in place without a Reset; re-pin after a filtered trim; Clear All; yell-direction rows; search and filter.
- Why: the Conversation box jumped on every line.

### CorpseHandoffTests.cs
- Covers: Game/Recovery/CorpseHandoffManager, CorpseRecoveryLines, CorpseHandoffPlan; BugReportScrub; SentCommandTrail; CommandHistory; DoHandler.
- Checks: another player's corpse is recorded from the recovery lines; the password stays out of storage, log, trail and history; gives are planned per realm and struck off on confirmation.
- Why: handing a friend's gear back without leaking their password.

### CountedCommandTests.cs
- Covers: Game/Inventory/CountedCommand.
- Checks: Paradigm sends one counted line, Stock one line per copy; leading-count split.
- Why: realm command-form difference.

### CpAllocationReconcileTests.cs
- Covers: ViewModels/CharacterWorkshop/CpAllocationSectionViewModel.IsRowTrained.
- Checks: a row is pruned only when its level is reached and its stats are allocated.
- Why: the prune deletes persisted plan rows.

### CpPlanBaselineTrustTests.cs
- Covers: Game/Calculators/CharacterPlanContext, StatParser marks, ListedEffectCatalog, CpAllocationSectionViewModel, Game/Train/AlteredStatsWait.
- Checks: when a `stat` baseline is trusted (Paradigm buffs explained, Stock any mark not); an untrusted baseline never rewrites a row; the bounded altered-stats wait.
- Why: rows being rewritten or pruned on buffed stats.

### CpPlanCalculatorTests.cs
- Covers: Game/Calculators/CpPlanCalculator.
- Checks: CP cost and carry-over, clamping, overspend trim (edited cell first), ClampRowToBudget.
- Why: CP plan math.

### CpPlanStateTests.cs
- Covers: ViewModels/CharacterWorkshop/CpPlanState.
- Checks: stats at a level carry forward from the plan; the Changed event.
- Why: the Level Projection tab reads it.

### CrashReporterTests.cs
- Covers: Services/CrashReporter.Build.
- Checks: the crash report's header, inner and aggregate faults, the build and log path, the null-exception and no-state fallbacks.
- Why: crash reports must stay readable.

### CureSpellIndexTests.cs
- Covers: Game/GameData/CureSpellIndex.CuredFlags.
- Checks: ability code → cured ailment (CurePoison, Freedom, DispellMagic, RemovesSpell on disease).
- Why: cure-spell selection.

### CurrencyFormatTests.cs
- Covers: Game/Cash/CurrencyFormat.
- Checks: Denominate flips to the largest coin; Full itemises; the renamed runic word.
- Why: currency display.

### CurrencyHoldingsTests.cs
- Covers: Game/Inventory/CurrencyHoldings.
- Checks: the copper ratio ladder; offload plan above the keep floor with a coin-type filter.
- Why: stashing coins.

### CurrencyNamingTests.cs
- Covers: Game/Cash/CurrencyNaming.
- Checks: runic-name normalising, recognising the stock and renamed words, canonicalising, Refresh.
- Why: realms that rename the runic coin.

### CurrentRouteDetailsTests.cs
- Covers: ViewModels/Navigation/CurrentRouteDetails, RouteDetailRow, RouteStepWarning.
- Checks: step rows; monster, hazard, item-gate and trap annotations; arrival row.
- Why: the route Details dialog.

### DamageLineAttributorTests.cs
- Covers: Game/Combat/DamageLineAttributor.
- Checks: source and target of each Stock damage wording; heals and absorbs are not damage; NoDealer.
- Why: combat stats and attack-line attribution.

### DarkRoomCombatIntegrationTests.cs
- Covers: Game/Combat/DarkRoomCombatWatcher + CombatManager + RoomEntry/DepartureWatcher.
- Checks: a dark-room reveal engages through the same hostility filter and Target Order; dedupe; re-pick after a death; the compass arrival/departure typo forms.
- Why: idle while a mob attacks in a dark tunnel (reported capture).

### DarkRoomCombatWatcherTests.cs
- Covers: Game/Combat/DarkRoomCombatWatcher.
- Checks: inject on an attack line or announce only in the dark; longest-name match; unknown attackers skipped; dedupe; refusal and no-effect retraction.
- Why: the same report; stock-20261004-150645.

### DarkRoomMovementSettleTests.cs
- Covers: Game/Map/DarkRoomMovementSettle.
- Checks: the settle gate on a dark advance, its timeout clear, no gate on a lit arrival.
- Why: the loop marching past a reveal in a dark room.

### DeathDetectorTests.cs
- Covers: Game/DeathDetector + RoomTracker.NoteDeath.
- Checks: both lives-readout phrasings register a death, set PendingRespawn, record DeathHistory, and drain recent steps.
- Why: miracle-save deaths were dropped.

### DeathFloorTracerTests.cs
- Covers: Game/Health/DeathFloorTracer.
- Checks: a slow death refines PlayerDiesAtHp; overkills are discarded; live-survival refine; guards.
- Why: report 193417; the death-floor estimate.

### DeathGroundSweepTests.cs
- Covers: Game/Recovery/DeathGroundSweep.
- Checks: exits are peeked in the given order, and neighbours holding our items are reported.
- Why: the Stock spillover look-sweep.

### DeathLineWatcherTests.cs
- Covers: Game/Combat/DeathLineWatcher.
- Checks: the "slain by" line fires PlayerDied with the killer; Warn log; dispose.
- Why: the death trigger for recovery, the floor tracer and sounds.

### DeathLogRenderTests.cs
- Covers: Game/Recovery/DeathLogFormat; Terminal/AnsiEncoder.
- Checks: header and transcript rendering; colour round-trip through the file; plain logs parse.
- Why: the "How did I die?" log.

### DeathLootCaptureTests.cs
- Covers: Game/Inventory/DeathLootCapture.
- Checks: worn vs carried split, spares, lit light and keys go on the lost list.
- Why: the deathpile record.

### DeathQueryHandlerTests.cs
- Covers: Game/Remote/DeathQueryHandler (through RemoteCommandManager).
- Checks: @death reports unrecovered deaths; `all`; other-game-data note; the grant gate.
- Why: the QueryDeaths remote command.

### DeathRecordCoinTests.cs
- Covers: Models/Profile/DeathRecord.LostItemsText.
- Checks: coins listed per denomination; singular and plural; the empty text.
- Why: the death grid display.

### DeathRecoveryManagerArenaTests.cs
- Covers: Game/Recovery/DeathRecoveryManager + DeathDetector + RoomTracker.
- Checks: an arena death (Stock colliseum line, Paradigm room) records nothing lost and arms nothing; ordinary deaths keep their pile.
- Why: GAME_MECHANICS "Death threshold & consequences".

### DeathRecoveryManagerCorpseTests.cs
- Covers: Game/Recovery/DeathRecoveryManager (Paradigm corpse grab).
- Checks: which display reads the corpse and which may mark it Missing; dark, blind and placed arrivals; Recover Now; hand-backs; lights and keys; spares.
- Why: piles written off wrongly; owner's ruling 2026-10-10.

### DeathRecoveryManagerGameDataTests.cs
- Covers: Game/Recovery/DeathRecoveryManager (game-data stamp).
- Checks: a death is stamped with its set; recovery is refused or allowed per active set; removed sets; JSON round-trip.
- Why: the same room number is another room under another set; user 2026-10-17.

### DeathRecoveryManagerPickupTests.cs
- Covers: Game/Recovery/DeathRecoveryManager (death-room pickup).
- Checks: an armed grab is never spent on a guess (suspect tracker, typed-ahead, dark, blind, disconnect); pass-through grabs; Recover Now cards closed.
- Why: a missed or doubled pickup.

### DeathRecoveryManagerSweepTests.cs
- Covers: Game/Recovery/DeathRecoveryManager spill sweep + AutoWalkManager + MovementController.
- Checks: engine spill order, the trail search, 12-stop limit, unreachable or barred stops; what starts, holds, pauses and ends a sweep; walk-back failures; Recover Now walks.
- Why: GAME_MECHANICS "Deathpile — where the items go"; review rounds.

### DeathRecoveryManagerTests.cs
- Covers: Game/Recovery/DeathRecoveryManager (core flow; shared GraphHarness).
- Checks: corpse recovery by live name; Missing never re-arms; auto-equip order and pacing in combat; held re-equip under Auto-All off; hand-backs; Stock floor gets, stacks, returned and staying items.
- Why: reports stock-20260828-104653, paradigm-20261004-201808, paradigm-20260926-102406.

### DeathRecoveryManagerVerdictTests.cs
- Covers: Game/Recovery/DeathRecoveryManager (Missing verdict).
- Checks: the verdict waits for the display's exits line; peeks; reopen on the same visit; reconnect arrivals; whose corpse; copies and keys.
- Why: piles wrongly written off or left unfinishable.

### DeathSpillOrderTests.cs
- Covers: Game/Recovery/DeathSpillOrder, DeathPileRules.
- Checks: the engine's exit order, depth-first to 5 rooms, skipped exits, equal to a literal replay on random graphs; loyal / cursed-major items stay.
- Why: GAME_MECHANICS "Deathpile — where the items go".

### DeathSummonCascadeTests.cs
- Covers: Game/Map/DeathSummonCascade.
- Checks: cascade folding, room cap (Paradigm 20 whole casts, Stock 15 per summon), seed clamp.
- Why: GAME_MECHANICS "Death-summon cascades"; exp estimates.

### DebuffTargetingTests.cs
- Covers: Game/Combat/DebuffTargeting.
- Checks: Spells.Targets single vs area enemy; between-round by energy.
- Why: GAME_MECHANICS "Debuff slot spells".

### DefaultPatternsTests.cs
- Covers: Services/Patterns/DefaultPatterns, RegexPattern, MessageRouter.Seed.
- Checks: the registry's ids; and per-pattern regex coverage of game wording (refusals, combat, arrivals and departures, shop, equip, follow mode, realm exit, …).
- Why: many reports (stock-20260913-233911, -232235, paradigm-20260914-055853, …).

### DefaultPoolBaselineKeeperTests.cs
- Covers: Game/Health/DefaultPoolBaselineKeeper.
- Checks: the baseline is recorded only in Default gear; staleness on level or gear change; a throttled `stat` re-read; drop vs keep when the set is missing.
- Why: a `stat` in pre-rest gear moved the basis (the report case).

### DefaultProfilePersistenceTests.cs
- Covers: Services/ProfileService (default template).
- Checks: a draft's Save writes nothing; naming and realm assignment; discard; startup-animation round-trip; template sanitising; one-time reset.
- Why: user 2026-10-12 (sessions must not write the template).

### DefaultTaskRunnerTests.cs
- Covers: Game/DefaultTaskRunner.
- Checks: the party-rebuild hold arms only on a reconnect after a party session; latches clear on profile load; a hang-up death calls the task off; Dispose.
- Why: the default task starting too soon, or walking a stripped character out.

### DeferredCollectReconnectReleaserTests.cs
- Covers: Game/Map/DeferredCollectReconnectReleaser
- Checks: the deferred-collect hold is released once on the first in-game prompt after a reconnect; not on first connect; re-armed on each reconnect.
- Why: a disconnect stranded the acquisition hold and paused the loop until a manual `rm`.

### DepositAllBankCheckTests.cs
- Covers: Game/Remote/InventoryActionHandler (@deposit-all)
- Checks: outside a known bank nothing is sent and the reply says so (even with Warn off); in a bank or an unplaceable room the deposit runs.
- Why: a stash transfer waits on this answer.

### DetourCombatHoldTests.cs
- Covers: Game/Map/DetourCombatHold
- Checks: the deposit/sell detour holds combat only outside the loop's rooms, releases when the detour ends, never holds with the box off, holds for a whole lair detour.
- Why: report paradigm-20260929-224330 (a deposit trip walked past monsters).

### DivertHandlerTests.cs
- Covers: Game/Remote/DivertHandler (@divert)
- Checks: an authorised start/stop/retarget, forwarding incoming telepaths as ASCII, skipping the target's own and the @divert verb, permission gate, dispose.
- Why: telepath forwarding must not leak or echo.

### DoHandlerTests.cs
- Covers: Game/Remote/DoHandler (@do), RemoteCommandManager hard-blocks
- Checks: an authorised passthrough to the wire with {ok}, ASCII clean-up, refusal of suicide/reroll, master switch/disable/channel gates, SendingFor attribution, dispose.
- Why: the highest-trust remote verb; destructive verbs must stay blocked.

### DoorDetourTests.cs
- Covers: Game/Map/DoorPolicy (pick odds), BfsMapper + Services/MovementFilter door routing
- Checks: the pick-chance formula; impossible and poor-odds doors routed round within the detour bound; a held key goes through; distance maps agree with FindPath; refusal text.
- Why: GAME_MECHANICS "Locked doors — picking, opening and bashing"; Ancient Coliseum shape.

### DoorOpenManagerTests.cs
- Covers: Game/Map/DoorOpenManager
- Checks: the bash/pick/use-key/open FSM on real reply lines; watchdog re-sends; rest gating; unopenable vs ordinary failure; keyed-door fallbacks; "no effect"/left-room ends as NotHere; queue order.
- Why: reports stock-20260730-182812, paradigm-20260924-053941; doors that hung the walker.

### DoorPolicyTests.cs
- Covers: Game/Map/DoorPolicy
- Checks: IsAchievable and ChooseVerb across stat requirements, the bash ceiling and the pick preference.
- Why: the walker must not try an impossible verb.

### DraggedTrackerTests.cs
- Covers: Game/DraggedTracker
- Checks: the dragger is recorded only while mortally wounded, overwritten by a new dragger, cleared on recovery, stopped by dispose.
- Why: avoid a phantom dragger on a standing character.

### DupeHandlerTests.cs
- Covers: Game/Remote/DupeHandler (@dupe), RemoteCommandCatalog path-channel rule, PlayerCustomization lock
- Checks: copies only query/roomba/quest permissions, additively; one use per sender reset only in the client; refusals; telepath/gangpath only; local API refused; logging; JSON of the lock.
- Why: a trust-granting command; it must not escalate.

### EditorRoomSearchOtherGameDataTests.cs
- Covers: ViewModels/Navigation/LairEditorDialogViewModel, LoopEditorDialogViewModel room search
- Checks: no rooms of another game-data set are listed or added; a refused add drops stale rows; saves refused under another set.
- Why: room numbers belong to their set.

### EmbeddedSeedsTests.cs
- Covers: Services/AppPaths.ExtractEmbeddedSeeds/ExtractEmbeddedNavSeed, shipped seed content
- Checks: every seed file is materialised; the Witchunter quest lock; guardian = Friend; location triples complete; landmass names; nav-seed unzip.
- Why: report paradigm-20261001-235024; a csproj regression would drop the embedded seeds.

### EmoteInputCompleterTests.cs
- Covers: Game/Emotes/EmoteInputCompleter
- Checks: the ":" token detection (not clock times), prefix-before-substring suggestions with a cap, splice with a trailing space.
- Why: the conversation-window emote picker.

### EmotePackageNameTests.cs
- Covers: Services/EmoteStore.ReadPackageDrafts
- Checks: package image names are unpacked only into the import folder (no traversal or absolute paths); duplicate and no-file-name entries handled.
- Why: a package manifest is another player's text (path-traversal guard).

### EmoteRuntimeCollection.cs
- Test infrastructure.
- Covers: test infrastructure (xUnit collection definition), no production class
- Checks: n/a — serialises the test classes that touch EmoteRuntime.
- Why: EmoteRuntime is process-wide static state.

### EmoteRuntimeCollectionTests.cs
- Test infrastructure.
- Covers: test-suite structure (IL scan of the test assembly)
- Checks: every test class touching EmoteRuntime/EmoteStore/ConversationViewModel joins the collection.
- Why: a forgotten class brought back a cross-class race.

### EmoteScannerTests.cs
- Covers: Game/Emotes/EmoteScanner, EmoteCatalog
- Checks: shortcode/emoticon/image segmentation, case rules, whitespace bounds, unknown codes stay literal, the bundled pepe set.
- Why: chat rendering of emotes.

### EmoteStoreTests.cs
- Covers: Services/EmoteStore
- Checks: commit/reload of unicode and image emotes, pruning removed images, hidden defaults, export/read-package round trip, a bad package returns null.
- Why: persistence of the user's emote library.

### EncumbranceCategoryTests.cs
- Covers: Game/Inventory/EncumbranceCategory
- Checks: the percent → band thresholds and the ceiling percent per band.
- Why: shared by the live parse and the Item Finder overlay.

### EncumbranceDebuffIndexTests.cs
- Covers: Game/GameData/EncumbranceDebuffIndex, shipped Messages seeds
- Checks: negative Encum% spells are indexed; no "weak and powerless" record is a hold; globe of darkness is not a hold.
- Why: report paradigm-20261003-201253 (frail held the loop).

### EncumbranceGateTests.cs
- Covers: Game/Inventory/EncumbranceGate
- Checks: the 90% cap, no-gate cap = max, the heavy gate's boundary.
- Why: coin/item collection must stop before a gated bracket.

### EncumbranceParserTests.cs
- Covers: Game/EncumbranceParser
- Checks: bracket parse (case-insensitive, unknown), dispatch writes PlayerState.Encumbrance, dispose, hydrate from the snapshot.
- Why: sole writer of PlayerState.Encumbrance.

### EngineRecoveryGateTests.cs
- Covers: Game/Map/EngineRecoveryGate (+ FootprintMatcher, RecoveryLookSweep, RoomTracker)
- Checks: the tier ladder, rm/sysop-locate fast paths, forced and terminal rm, the confusion retry cap, teleport never reversed, look-sweep/dark/combat gating, forward footprint, FailTier3 detach safety.
- Why: issue #455 / report paradigm-20260902-223159; Crash-20260908-181131; Darkwood "Lost" reports.
- Test entry point kept: EngineRecoveryGate.SetLookSweepForTests and RecoveryLookSweep.FireLookTimeoutForTests: held with the room-tracking PR (#1091), to be replaced once it lands.

### EngineReplyLinesTests.cs
- Covers: Game/EngineReplyLines
- Checks: engine chatter lines match; spell-like lines don't; no entry matches a catalogued spell message in either seed.
- Why: keeps engine chatter out of the review queue without hiding spells.

### EngineSendGateTests.cs
- Covers: Services/EngineSendGate
- Checks: named holds compose; the wrapper drops while held; replay of the last client command (moves skipped, held = no-op); route steps never taken for later by sneak; reporting.
- Why: report paradigm-20261007-225617; concurrent hold reasons.

### EquipmentManagerTests.cs
- Covers: Game/Inventory/EquipmentManager
- Checks: wear/equip command building (availability, blocks, 2H/off-hand rems, paired-slot eviction per realm, fallback fill); apply by keyword/id/trigger; in-flight guard; SwapWeapon; backstab armour; slot blocks; UpdateSetFromWorn; manual-equip signal.
- Why: reports paradigm-20260831-071637, -20260716-095547, -20260901-130100, -20260903-111522, -20260825-103537, -20260926-194514, -20260926-221353.

### EquipmentMaxPoolSyncTests.cs
- Covers: Game/Health/EquipmentMaxPoolSync
- Checks: baseline seeding, +/- deltas on equip/remove, no repeat delta, reset re-seeds.
- Why: worn HP/mana bonuses must compose with the prompt-derived max.

### EquipmentSlotMapTests.cs
- Covers: Game/Inventory/EquipmentSlotMap
- Checks: Worn code → slot string round-trip (paired codes), unknown → null, DisplayOrder.
- Why: incremental "now wearing" slotting; in-game look order.

### EquipmentStatAggregationTests.cs
- Covers: Game/Calculators/CharacterCalculator.AggregateEquipmentStats/ApplyAbilityBonuses, ViewModels/CharacterWorkshop/EquipBonusRowBuilder
- Checks: AC/DR ÷10, ability → stat mapping, blur AC split, weapon accuracy/StrReq, hit magic breakdown, unknown item skipped, race abilities.
- Why: Workshop equipment bonus readout.

### EquipmentWeaponSyncTests.cs
- Covers: Game/Inventory/EquipmentWeaponSync.ApplyWeapons
- Checks: normal/alternate weapons come from the Default set; backstab from an enabled Backstab set else Default; stale values cleared.
- Why: gear sets are the single source of combat weapons.

### EventEditDialogViewModelTests.cs
- Covers: ViewModels/Settings/EventEditDialogViewModel
- Checks: hydration/defaults, stash-transfer fields, sound, WHEN validation gating Save, missing-target messages, Save/Cancel CloseRequested, stale-field clearing.
- Why: the modeless edit-dialog contract.

### EventEngineHarness.cs
- Test infrastructure.
- Covers: test fixture over the real EventManager + walker/loop/Auto-Lair engines
- Checks: n/a (shared harness)
- Why: lets event tests run against the real engines.

### EventLairMarksGameDataTests.cs
- Covers: Game/Events/EventManager resume of Auto-Lair
- Checks: the snapshot carries the marks' game data; resume puts marks back only on that set; setup marks are stamped with the active set.
- Why: marks are room numbers of one set.

### EventManagerResumeTests.cs
- Covers: Game/Events/EventManager (snapshot/resume, Then handling)
- Checks: snapshot precedence; resume of walker/loop/lair; walk-to arrival incl. stop-before boss rooms; failed vs user-stopped walks; Wait with the master switch; lap limits; chained events and cycles.
- Why: events going back to what they interrupted.

### EventManagerTests.cs
- Covers: Game/Events/EventManager, Models ScheduledEvent
- Checks: DTO JSON, AtTime/Every parsing, the command splitter, CRUD, Command fire (multi-chunk, bare CR, disabled, master switch), the ConvertToThen migration.
- Why: the core event dispatch and schedule parsing.

### EventQueueTests.cs
- Covers: Game/Events/EventManager queue
- Checks: events wait behind a running one in order; dedup; caps and wait limits from settings; master-switch holds and the held-queue user choice; logoff jumps; death/stop/disconnect; teleport-refused go-back notice.
- Why: reports paradigm-20261009-185106 and -220128; user decisions 2026-10-10.

### EventRemoteHandlerTests.cs
- Covers: Game/Remote/EventRemoteHandler (@events/@event), EventManager.FindByName/SetEnabled, EventsSectionViewModel/EventEditDialogViewModel follow
- Checks: list/inspect/switch by name or number; nothing secret leaks; refusals and permissions; Warn/master switch; local API; replies fit one telepath; the open tab/editor follow; persistence; reply-line recognition.
- Why: remote control of events must not reveal what they send.

### EventRunSurvivalTests.cs
- Covers: Game/Events/EventManager with DetourResume, MovementController, walker/loop runner
- Checks: runs survive failures raised in teardown, detours, reconnects; suspended-event idle limit (not counting master-switch time); user stops end the queue; the open-ended loop hand-on.
- Why: events stranded or dropped across detours and outages.

### EventSchedulerTests.cs
- Covers: Game/Events/EventScheduler (+ EventManager.FireLogoffEvents, CleanupWarningWatcher)
- Checks: Logon on first prompt, Re-log latching, Logoff firing and count, cleanup warning → Logoff once, GetNextFire per trigger, countdowns kept across a switch, dispose.
- Why: lifecycle triggers fire once and in order.

### EventStashTransferTests.cs
- Covers: Game/Events/EventManager stash-transfer action
- Checks: hands over the saved rooms; Then on done/failed, not on a user stop; a refused start; queueing behind; a Logoff event stops the transfer.
- Why: the event-driven stash transfer.

### EventStateTriggerTests.cs
- Covers: Game/Events/EventConditionEvaluator, EventStateWatcher, Roomba action, editor conditions
- Checks: comparisons, money by denomination, unknown readings never hold, fires once per crossing, out-of-game, Roomba mode, editor save.
- Why: the "When" condition trigger.

### EventWalkToRoomSearchTests.cs
- Covers: ViewModels/Settings/EventEditDialogViewModel room search + boss-stop hints
- Checks: room suggestions by name/coordinate, picked rooms save and reopen with names, stop-before-boss notices and the Then-block hints, the enter-boss-room choice persistence.
- Why: users pick rooms off the map instead of typing them.

### EventsSectionViewModelTests.cs
- Covers: ViewModels/Settings/EventsSectionViewModel
- Checks: rows mirror events; trigger/action labels; Remove; the master switch persists; queue limit settings clamp and persist as deltas.
- Why: Settings → Events tab.

### EvilPointRangeTests.cs
- Covers: Game/Calculators/EvilPointRange, ItemEquipFilter, KnownSpellCatalog, BuffClassifier
- Checks: title → evil-point band per realm, evil-only (ability 98) gates, narrowing by a refusal, straddles left to the game.
- Why: evil-only items/spells on Paradigm vs Stock.

### ExitGateItemsTests.cs
- Covers: Game/Map/ExitGateItems
- Checks: which exit hints contribute carried-item requirements (item/ticket/teleport, multi-action held items); locked doors never do; Collect dedups.
- Why: the planner and the blocked-route message must agree.

### ExperienceQueryHandlerTests.cs
- Covers: Game/Remote/ExperienceQueryHandler (@exp/@level)
- Checks: before a stat parse; to-next/unknown; rate/ETA/ready lines; rate abbreviation tiers; permission gate.
- Why: MegaMUD-style experience reply.

### ExperienceTableCalculatorTests.cs
- Covers: Game/Calculators/ExperienceTableCalculator
- Checks: the exp chart; Stock vs ParaMUD exp needed (level-34 taper); monotonic; saturation; time-to-level and its format tiers.
- Why: level ETA readouts.

### FavoriteEditDialogViewModelTests.cs
- Covers: ViewModels/Navigation/FavoriteEditDialogViewModel
- Checks: Save returns a trimmed label (blank → null) with coordinates and star; Cancel returns null; room preview; star cap toggle.
- Why: the favourite edit dialog contract.

### FavoriteFilterTests.cs
- Covers: ViewModels/Navigation/FavoriteFilter
- Checks: which filter text reads as a "favourite" query (3+ letter parts of either spelling).
- Why: the navigation search shortcut.

### FavoritesStoreTests.cs
- Covers: Services/FavoritesStore
- Checks: per-character copy of the old shared list; new characters get the bundled starters; Restore defaults; late game data; star toggle with a 10 cap; persistence on the profile.
- Why: report paradigm-20261006-220135; user 2026-10-12.

### FlavorPrefixStoreTests.cs
- Covers: Services/FlavorPrefixStore
- Checks: built-in defaults; add/remove persist per set; in-memory with no set; duplicates refused; reset; Changed events.
- Why: the editable monster flavour-adjective vocabulary.

### FloorListLineTests.cs
- Covers: Game/Inventory/FloorListLine, Game/BenignChatterMatcher.IsListingHeader
- Checks: wrapped floor lists open/read; corpse contents and other "You notice" sentences aren't floor lists; player/corpse look headers.
- Why: report paradigm-20261009-001932 (got coins off a corpse).

### FloorSurveySourceTests.cs
- Covers: Game/Inventory/GroundItemTracker.LastSurveySource, FloorListLine.IsRoomSearch
- Checks: a search reply vs a room display vs unknown by echo/room name through the real emulator, incl. redisplays, wrapped replies, knocked-away echoes, custom statlines.
- Why: report paradigm-20261009-164508.

### FolderNameLeftoverTests.cs
- Covers: Services/SettingsResolver realm tier, GameDataCache.RefusedSetNotice, LoopManager/LairManager/NavFolderManager, NavFolderNameDialogViewModel
- Checks: unusable realm names get no tier and nothing goes to the holding folder; unusable folder names refused for save/move/create; the holding folder is never listed; the dialog check.
- Why: path-safety for user-named folders.

### FolderPickerDialogViewModelTests.cs
- Covers: ViewModels/Navigation/FolderPickerDialogViewModel
- Checks: root first then sorted; preselection; Save returns the path ("" for root); Cancel null.
- Why: the move-to-folder dialog contract.

### FollowModeTrackerTests.cs
- Covers: Game/FollowModeTracker
- Checks: follow mode from the `pro` row and `set follow` replies; Blind noticed once per turn-on; reset.
- Why: Blind follow hides room displays.

### FollowMoveObserverTests.cs
- Covers: Game/Map/FollowMoveObserver (+ RoomTracker)
- Checks: the drag line records the move direction to resolve identical rooms; without it the tracker can't; no anchor → stays unlocated.
- Why: dragged followers in Darkwood-style identical rooms.

### FootprintMatcherTests.cs
- Covers: Game/Map/FootprintMatcher
- Checks: seeding, step narrowing, trap/no-exit drops, exhaustion, neighbour filter (look-sweep), blind steps, merge convergence.
- Why: recovery and passive relocation in look-alike rooms.

### GameDataBrowserViewModelTests.cs
- Covers: ViewModels/GameData/GameDataBrowserViewModel
- Checks: sections seeded and grouped; initial selection; search filter; status text.
- Why: the Game Data window.

### GameDataCacheSharedReadTests.cs
- Covers: Services/GameDataCache (shared reads, ActiveLoad, FileStampAsRead)
- Checks: ActiveLoad moves with every load of a set and nothing else; FileStampAsRead is the file as read, as a prewarm read it, and is forgotten when the next read finds the file gone or unparseable.
- Why: background index builds racing set loads.

### GameDataCacheSwitchTests.cs
- Covers: Services/GameDataCache set switch atomicity
- Checks: one load per switch, seen by every handler; lookups racing switches never throw and never serve another set's table (a ~0.2 s stress run: forcing the interleaving would need a gate only tests use).
- Why: a stale table survived a whole set load.

### GameDataCacheTests.cs
- Covers: Services/GameDataCache
- Checks: available sets; ActiveSetChanged on transitions; set-load logging; eviction/reload/idle sweep; malformed JSON once; FindRowByName; prewarm claim/discard; ActiveRealm from Info.Legit.
- Why: corrupted MDB imports; @uses whitespace report.

### GameDataChangeDropsHeldStartsTests.cs
- Covers: Game/Map/StopOnGameDataChange + MovementController with HeldDetourResume/PvpFight, DefaultTaskRunner, PartyTrainCoordinator
- Checks: a game-data change drops the run a fight held, the default task held for the party, and a training trip in a wait; without the hook each would start on the other set's rooms.
- Why: runs restarting on wrong room numbers.
- Test entry point kept: PartyTrainCoordinator.RunTripForTests: the real start needs a running movement engine.

### GameDataDeltaTests.cs
- Covers: Services/SettingsResolver game-data deltas, ViewModels MonstersSectionViewModel
- Checks: a delta filed under a row's Number is found and merged over the seed; source tier shown; the row shows the import.
- Why: edits are deltas beside the imported table.

### GameDataLoadChangeTests.cs
- Covers: Game/Map/GameDataLoadChange
- Checks: nothing / another set / read-in-again between cache loads, with wording.
- Why: what each stop names.

### GameDataLongTextCheckTests.cs
- Covers: Services/GameDataLongTextCheck.IsDamaged
- Checks: long cells missing their first two letters are flagged; sound and short cells aren't.
- Why: damage from the old Access reader.

### GameDataRowTests.cs
- Covers: ViewModels/GameData/Tables/GameDataRow
- Checks: raw vs display values by column (case-insensitive), cells in order, computed cells, number text preserved.
- Why: grid display and search.

### GameDataSetManagerTests.cs
- Covers: Services/GameDataSetManager
- Checks: copy/move of loops, messages and record overrides across tiers; validation; active-set reload with what changed; delete set; replaced-set reporting.
- Why: Manage Sets operations.

### GameDataSwitchEndsHeldRunsTests.cs
- Covers: Game/Map/MovementController.StopForGameDataChange with EventManager, PartyComebackManager
- Checks: an event waiting at the switch is ended without going back; the user's stop still reads as the user's; a pick-up waiting for its member is cancelled.
- Why: held runs restarting on the old set's rooms.

### GameDataSwitchStopsMovementTests.cs
- Covers: Game/Map/StopOnGameDataChange, MovementController, LoopRunner, AutoWalkManager, AutoLairManager, RoutePlanInFlight, PathItemFloorCollector, HeldLoopsOnProfileSwap
- Checks: a switch or re-read stops loops/lairs/walks and drops held starts with notices; no-op for the same set; plans in flight are dropped; route item needs forgotten; loop stamps; profile swaps drop held loops.
- Why: room numbers differ between sets.

### GameDataTableSectionTests.cs
- Covers: ViewModels/GameData/Tables/GameDataTableSectionViewModel, MonstersSectionViewModel, ItemsSectionViewModel
- Checks: toggle summary; lazy load; lair columns; search on Enter incl. display labels; range/bool/category/flag filters; synthesised columns; reload on set change; status text.
- Why: report paradigm-20260814-103219 (stacked filters).

### GameTableTests.cs
- Covers: Services/GameTable, GameTableReader, GameRow, GameValue
- Checks: compact table reads equal JsonDocument (awkward values, one-byte texts, field order); malformed/invalid UTF-8 refused; block/limit handling; lookups; thread safety; memory footprint; in-place search.
- Why: the compact game-data store must match the document it replaced.

### GatePickSourcesTests.cs
- Covers: Services/GatePickSources
- Checks: the route card's source tail per pick: giver/shop/dropper only for what the pick fetches; trades only for door keys; carried and stopping picks name none.
- Why: route cards must promise only what the pick does.

### GeneralSettingsTests.cs
- Covers: Models/Profile/GeneralSettings, AutoActionDefaults
- Checks: safe defaults; a round-trip through JsonStore.Options; partial and legacy profiles; the combined heal/rest flag migration.
- Why: profile compatibility; reconnect re-enable flags default off.

### GetStashHandlerTests.cs
- Covers: Game/Remote/GetStashHandler (@get-stash)
- Checks: search, hold pickup, take within weight limits, answer once loaded; nothing found / at limit; busy refusal; permission.
- Why: the member side of a leader's stash transfer.

### GhDestinationResolverTests.cs
- Covers: Game/Map/GhDestinationResolver
- Checks: first matching room, backup rooms, the catch-all chain, null when all full; candidate rooms.
- Why: Roomba overflow routing.

### GhItemClassifierTests.cs
- Covers: Game/Map/GhItemClassifier
- Checks: classify from the item table; broad vs narrowed rules; leather and finger/wrist code collapsing; worn-slot rules ignore type; multi-rule rooms.
- Why: Roomba room labels.

### GhItemLocationStoreTests.cs
- Covers: Game/Map/GhItemLocationStore
- Checks: per-(item, room) sightings with counts; rescans drop stale entries; substring search; persistence per realm; sync records out/in by item number and recency; latest-seen.
- Why: @roomba needs every room an item is in.

### GhItemSyncCodecTests.cs
- Covers: Game/Remote/GhItemSyncCodec
- Checks: item and label line round-trips, per-room timestamp, compact packing within budget, self-contained lines, wire-safe chars, malformed/crafted input bounded.
- Why: Roomba sync over telepath.

### GhManagedRoomStoreTests.cs
- Covers: Game/Map/GhManagedRoomStore
- Checks: per-character managed rooms set/clear/persist to the profile, hydrate, dropped on a profile swap.
- Why: alts manage their own gang-house subset.

### GhRoomLabelStoreTests.cs
- Covers: Game/Map/GhRoomLabelStore
- Checks: searches-per-room default/clamp; search-for-hidden; Changed; sync merge keeps local and catch-all flags; persistence; legacy per-character migration.
- Why: shared gang-house labels.

### GhRouteOrdererTests.cs
- Covers: Game/Map/GhRouteOrderer
- Checks: nearest-neighbour ordering of labelled rooms; unreachable rooms appended once.
- Why: a zigzag label order reported.

### GhSortPlannerTests.cs
- Covers: Game/Map/GhSortPlanner
- Checks: fill before deliver; nearest fitting source; full rooms drained first; unload hysteresis; split into trips; null when nothing to do.
- Why: Roomba trip efficiency (14-hops-per-item shuttle).

### GhSortQueueBuilderTests.cs
- Covers: Game/Map/GhSortQueueBuilder (+ GhSurveyMerger)
- Checks: misplaced items queued to their room; correct/unmatched left; guard emblems never moved; multi-rule and catch-all rooms; flagged items sorted.
- Why: GAME_MECHANICS "Gang houses & guard emblems".

### GhSurveyMergerTests.cs
- Covers: Game/Map/GhSurveyMerger
- Checks: repeated searches settle on the max count, never a sum; hidden delta; visible + hidden totals; unattributed lists.
- Why: reports 20260827 and paradigm-20261009-164508.

### GhSuspendedSweepStoreTests.cs
- Covers: Game/Map/GhSuspendedSweepStore
- Checks: an unfinished load persists to the profile, an empty save clears it, no profile = no-op, Load returns a copy, LoadCarried.
- Why: an interrupted sweep's load must not be lost.

### GhSweepFinalLapTests.cs
- Covers: Game/Map/GhSweepManager final recon (via RoombaHouse)
- Checks: the final lap logs rooms as sorted; changes since the sort reported once; empty rooms; hidden stacks kept; unread rooms written from the sort's account in one write.
- Why: the item log kept pre-sort floors.

### GhSweepHugeRoomTests.cs
- Covers: Game/Map/GhSweepManager + GroundItemTracker + ConditionTracker on a huge floor
- Checks: a 600-stack vault is queued stack for stack with a bounded log line; allocation stays linear.
- Why: report paradigm-20261009-164508 (UI lock-ups).

### GhSweepManagerIntegrationTests.cs
- Covers: Game/Map/GhSweepManager end to end
- Checks: recon/search/sort/deliver; paced dispatch and rate-limit resend; refusal handling; capacity resync; resume from a persisted queue (refused across sets); stalled-sort finish; multi-unit confirms; full destinations; inventory-only mode; managed-room gate.
- Why: many Roomba field reports (120-room flood, 70-lap bug, 42× get).

### GhSweepReconRecordTests.cs
- Covers: Game/Map/GhSweepManager recon records (via RoombaHouse)
- Checks: lists not shown to be displays only add; inventory-only empties logged; a display with description/others read as the floor.
- Why: a wiped war hammer and short mace count.

### GhSweepRecoveryResyncTests.cs
- Covers: Game/Map/RoomTracker
- Checks: SetLocated raises a real StateChanged transition that a reactor registered early sees.
- Why: an unreproduced "rooms missed after rm" report.

### GhSweepRefusedPickupTests.cs
- Covers: Game/Map/GhSweepManager refused pickups (via RoombaHouse)
- Checks: visible stacks smaller by sort time on Paradigm/Stock; hidden twins held back; one look settles several; too-heavy batch give-up; record kept true.
- Why: copies left in the pack unsaid.

### GhSweepSearchCountsTests.cs
- Covers: Game/Map/GhSweepManager + GroundItemTracker + GhItemLocationStore
- Checks: display + hidden totals at the highest count; one item-log write per room; visible and hidden moves kept apart; sort pickups with refusals; inventory-only totals; late replies ignored.
- Why: report paradigm-20261009-164508.

### GhSweepSortRegressionTests.cs
- Covers: Game/Map/GhSweepManager sort rules (via RoombaHouse), GhFloorCounts
- Checks: hidden stacks not taken while visible copies lie there; split stacks; Stock re-search misses; knocked-away echoes; custom statline; counted-get refusals; latest display wins; one log line per room; linear floor counts.
- Why: each case once lost items or their record.

### GlobalSettingsTests.cs
- Covers: Models/Settings/GlobalSettings.StartupProfile
- Checks: auto-load last profile only when on and one exists; default off.
- Why: the startup profile decision.

### GlobeOfDarknessSeedTests.cs
- Covers: Game/Conditions/ConditionTracker with shipped Messages seeds
- Checks: a shadow's globe of darkness is not blindness; a blind globe still blinds.
- Why: a nanati shadow's globe set Blinded and asked for a cure.

### GotoHistoryStoreTests.cs
- Covers: Game/Map/GotoHistoryStore
- Checks: newest-first, promote, cap 10, write-back as "map/room", ignored with no profile, hydrate.
- Why: the goto button's recent list.

### GreetManagerTests.cs
- Covers: Game/GreetManager (+ RoomEntityClassifier)
- Checks: greet+look once per local day on first sighting; disabled; self/party skipped; greets by given name.
- Why: the auto-greet feature.

### GreetTeleportResolverTests.cs
- Covers: Game/Map/GreetTeleportResolver
- Checks: ungated/class/minlevel teleport topics surfaced; alignment/ability-gated skipped; class-branch tables bound right; paid fares.
- Why: issue #455, report stock-20260907-175035 (bard-only transport mis-gated).

### GroundItemTrackerTests.cs
- Covers: Game/Inventory/GroundItemTracker
- Checks: the floor survey keeps items and drops cash; supersede; wrapped lists; room change clears; the item table keeps a denomination-named stack ("2 gold key") and a compound name ("rope and grapple") as items.
- Why: reports stock-20260825-101612, 20260825-172400.

### GuardDoorCommandResolverTests.cs
- Covers: Game/Map/GuardDoorCommandResolver
- Checks: ask commands that open a door on the guard's own room, with ability gates; other rooms/no opener/no name empty; Read answers each room as Resolve.
- Why: the grove shadow guard door (Phoenix quest).

### HangupDeathArrivalRoomsTests.cs
- Covers: Game/Recovery/HangupDeathArrivalRooms, GameDataCache.ActiveSetIsVanillaStock, RoomGraphManager.CouldBeTakenFor
- Checks: where a room's dead arrive (Stock list, Paradigm Albion, learned rooms) and whether the first room back says a death, per list trust; look-alike arrival rooms.
- Why: hang-up death detection (owner confirmations 2026-10-10/11).

### HangupDeathTests.cs
- Covers: Game/Recovery/HangupDeath, Game/Health/HangupPenaltyNotice.HpShareTop, Game/Map/RoomTracker.NoteUnwitnessedDeath, Game/PlayerDeathMovementHalt
- Checks: when a hang-up counts as a suspected death and the verdict rules (lives, HP at re-entry, entry room, arena rooms); which items make up the pile; the record an unwitnessed death writes, and the engine stop without the respawn resync.
- Why: a death caused by a hang-up penalty that nobody saw (owner rulings of 2026-10-10)

### HangupHandlerTests.cs
- Covers: Game/Remote/HangupHandler
- Checks: @hangup permission gating; sends the configured exit command; raises HangupSignal; logs the penalty line; unregisters on dispose.
- Why: a stranger must not be able to drop the link or poison the suppress-entry flag

### HangupItemRecheckTests.cs
- Covers: Game/Inventory/HangupItemRecheck, HangupItemPlan, HeldAtDisconnect persistence
- Checks: picking dropped items back up after a penalised hang-up (the item cap, room, fight and Auto-All gates, movement hold); when the held-item list is written; hang-up death detection by lives, room, arena and learned arrival rooms; realm differences.
- Why: the board's hang-up penalty; several reports and owner rulings

### HangupPenaltyTests.cs
- Covers: Models/Settings/RealmProfile hang-up fields, Game/Health/HangupPenaltyNotice, ViewModels/Settings/BbsSectionViewModel
- Checks: defaults and how they're stored per realm; value limits; the wording in the log line and the bug report; how BBS-tab edits are staged and applied.
- Why: realm-level setting for the hang-up penalty

### HangupSignalTests.cs
- Covers: Services/HangupSignal
- Checks: the two flags each fire once; AllowNextEntry; Reset when the profile changes.
- Why: paradigm-20260909-172633 (a stale suppress-entry flag left a different character stuck at the menu)

### HazardBuffRefreshTests.cs
- Covers: Game/Map/AutoHazardCounterProvisioner, Services/RoomBuffStripIndex, Services/RoomHazardIndex, CastingDirector client-use path, AutoWalkManager step hold
- Checks: the hazard-buff clock (pause/resume, dispel rooms and commands); the refresh window and last call; round refusals and the use refusals that hold; Paradigm's #712 follow-on; master switch; sneak; casting it through the pass; the walker step.
- Why: user rulings 2026-10-10 (desert waterskin)

### HealCommandHandlerTests.cs
- Covers: Game/Remote/HealCommandHandler
- Checks: @heal makes a configured healer poll `par` and acknowledge; stays silent with no heal configured, in the trainer menu, or for a denied sender; unregisters on dispose.
- Why: party healing on request; protects the stat-form last name

### HealLineReaderTests.cs
- Covers: Game/Spells/HealLineReader
- Checks: which lines are heals, who they targeted and how much (own, room, party, "is healed of"); averaging by caster level; message records shared with other spells; InstantHeals filter.
- Why: estimating party HP

### HealthManagerTests.cs
- Covers: Game/Health/HealthManager
- Checks: rest and meditate gates with their confirm ticks and pacing; ShadowRest; rooms whose spell does damage; flee (backward, forward, closed rooms, refused moves, hit-and-run, from a monster or player); the emergency hang-up, wimpy jump and @panic matrix; party wait/ok and opportunistic rests; freezing the gate while gear swaps change max mana.
- Why: many paradigm-/stock- reports cited inline; user rulings 2026-09/10

### HealthSettingsTests.cs
- Covers: Models/Profile/HealthSettings
- Checks: the default thresholds.
- Why: what a fresh character starts with on the Health tab

### HeldEventsPromptViewModelTests.cs
- Covers: ViewModels/HeldEventsPromptViewModel
- Checks: the rows, waited time and confirm label; select all/none; confirm hands back the ticked events in order; withdraw.
- Why: prompt for events held while Auto-All was off

### HelpBookTests.cs
- Covers: Services/HelpBook.Parse
- Checks: how headings nest into topics and subtopics, and what each body keeps.
- Why: the Help window's table of contents

### HelpHandlerTests.cs
- Covers: Game/Remote/HelpHandler, RemoteCommandManager.GetPermittedCommands
- Checks: lists only commands the sender may use (party commands excluded), split into chunks under the length cap; `@help <cmd>` syntax; bare "help" doesn't trigger it.
- Why: remote command discovery

### HelpMarkupTests.cs
- Covers: Services/HelpMarkup.ParseInline
- Checks: bold, italic, code, links shown as their text only; an unclosed marker stays as plain text.
- Why: rendering the Help content

### HelpWebsitesSettingsTests.cs
- Covers: Models/Settings/HelpWebsitesSettings
- Checks: the four default links, seeded on a new instance.
- Why: the Help → websites menu

### HelpWindowViewModelTests.cs
- Covers: ViewModels/Help/HelpWindowViewModel
- Checks: search highlights matches without hiding the rest; clearing the search keeps the selection visible; status text.
- Why: a regression where clearing the filter buried the selected topic

### HiddenExitRevealManagerTests.cs
- Covers: Game/Map/HiddenExitRevealManager
- Checks: `sea` is sent; a find is recognised from the room redisplay or the success line; the retry cap; leaving the room mid-search; waits while blind or resting; StopAll.
- Why: paradigm-20260924-135311, paradigm-20260714-121106

### HostileNameTests.cs
- Covers: Services/ProfileService, BbsProfileStore, RealmCatalog, GameDataSetManager, MdbImporter, GameDataCache, LoopManager, SettingsResolver
- Checks: every path built from a BBS, realm, profile or set name refuses `..`, absolute, drive, device and similar names without changing anything on disk.
- Why: `..` as a BBS name aimed a recursive delete at the data root

### HpMaHistoryTrackerTests.cs
- Covers: Game/Combat/HpMaHistoryTracker
- Checks: min/max/average per step, adding up across laps; characters with no mana; clamping; filling gaps forward; reset and the Changed event.
- Why: the HP/MA History band graph in Session Stats

### HpRegenExpectationTests.cs
- Covers: Game/HpRegenExpectation
- Checks: the HP gains passive regen can produce, per realm, level and Health.
- Why: paradigm-20261004-140923, stock-20261004-150847

### HugeFloor.cs
- Test infrastructure.
- Covers: (test helper) builds huge "You notice … here." floor lines and a matching Items table
- Checks: n/a
- Why: paradigm-20261009-164508 (floor lines over 2,000 characters)

### InGameCaptureTests.cs
- Covers: Game/InGameCapture, CaptureWindow, TranscriptSnapshot.Tail
- Checks: the report transcript only includes rows written while in the game (no login screen); the board-menu state; the engine-send hold at the menu.
- Why: privacy of the account name; paradigm-20261008-125639

### InboundMoveEchoScannerTests.cs
- Covers: Game/Map/InboundMoveEchoScanner → RoomTracker
- Checks: a command echo split off the prompt, a detached echo, or a custom statline confirms the move; a room line after a bare prompt doesn't; a detached echo doesn't latch the statline-readable flag.
- Why: stock-20260914-000155, -000112 (rooms with identical names in a grid)

### IncomingHitEstimatorTests.cs
- Covers: Game/Calculators/IncomingHitEstimator
- Checks: AC is floored, not rounded; AcExact keeps the tenths; FormatPercent.
- Why: paradigm-20260907-154641

### IndexesAfterReimportTests.cs
- Covers: MonsterSpawnIndex, ItemSourceIndex, RoomFloorItemIndex, EncumbranceDebuffIndex, SpellAilmentIndex, CureSpellIndex, QuestFlagIndex
- Checks: each index notices a re-import under the same set name; nothing built mid-switch is served stale.
- Why: indexes kept answering from old tables after a re-import

### InstalledFontCatalogTests.cs
- Covers: Services/InstalledFontCatalog
- Checks: the fixed-pitch probe on the bundled fonts; WidthsUniform on design-unit advances at the client's 2%-of-em tolerance (zero, missing-glyph and empty sets fail); the saved catalogue's stamp and read-back.
- Why: offering only monospace fonts for the terminal

### InventoryActionHandlerTests.cs
- Covers: Game/Remote/InventoryActionHandler
- Checks: @get-all, @drop-all (full/coins/keys; batching on Paradigm, items that can't be dropped or are cursed), @hide-all, @deposit-all, @share; permission gating.
- Why: stock-20260730-215247; user 2026-10-02

### InventoryAutoCompleterTests.cs
- Covers: Game/Inventory/InventoryAutoCompleter
- Checks: completes from the item's leading word; cycles forward and back; ignores stack counts; multi-word names; completes at the caret; a fresh match after an edit or caret move.
- Why: paradigm-20260911-231808

### InventoryManagerTests.cs
- Covers: Game/Inventory/InventoryManager
- Checks: parsing the full `i` dump; incremental changes to coins, items, worn gear, keys and the lit light; give/receive and their events; the weight estimate; carrying encumbrance between sessions.
- Why: many reports cited inline (paradigm-20260909-074126, -20261007-164408, -20261001-085357, …)

### InventoryQueryHandlerTests.cs
- Covers: Game/Remote/InventoryQueryHandler
- Checks: @wealth, @enc, @have (stacks, the key ring), @inv (chunked), @what; "parse inventory first" before the first `i`; permission gating.
- Why: remote inventory queries

### ItemCastSequencerTests.cs
- Covers: Game/Spells/ItemCastSequencer
- Checks: the equip → use → re-equip sequence; handling of off-hand, two-handed and worn-slot items; falling back to the Default set; waits when the worn loadout isn't known; items used from the pack.
- Why: paradigm-20260819-234712; reports -144339/-150242

### ItemCastSpellsTests.cs
- Covers: Game/GameData/ItemCastSpells
- Checks: on-use casts versus swing procs; which cast is primary.
- Why: matching an item's cast message

### ItemCastTokenTests.cs
- Covers: Game/Spells/ItemCastToken
- Checks: formatting, detecting and resolving `#item` tokens.
- Why: buff slots that cast from an item

### ItemChargeTrackerTests.cs
- Covers: Game/Inventory/ItemChargeTracker
- Checks: Paradigm charge counts read from `look` and saved; recharge after cleanup; auto-look; counting down from use lines; refusals; stacks; dropping the last copy.
- Why: paradigm-20260926-220239, -20261002-140153, -20260928-053429

### ItemDamageModelTests.cs
- Covers: Game/Inventory/ItemDamageModel, ItemFinderEntry.BuildCatalog
- Checks: backstab and damage-per-round estimates go through CombatCalculator; the wielded weapon's bonuses are swapped out; gear is scored by what it adds.
- Why: the Item Finder's damage columns

### ItemDropRuleTests.cs
- Covers: Game/Inventory/ItemDropRule
- Checks: no-drop, cursed-while-worn and loyal refusals; IsCursed.
- Why: user 2026-10-02

### ItemEditDialogViewModelTests.cs
- Covers: ViewModels/GameData/Edit/ItemEditDialogViewModel
- Checks: the installed-defaults tier; EqualsInstalledDefaults; sell-detour round-trip and its warning; the Given-by filtering; the exit-uses list cap.
- Why: paradigm-20260930-183614

### ItemEquipFilterTests.cs
- Covers: Game/Inventory/ItemEquipFilter
- Checks: alignment buckets; CanEquip by level, alignment, class, weapon type and armour tier; class grants; anti-magic classes; Stock's Seedy rule.
- Why: a Mystic was offered a longsword

### ItemFinderCatalogTests.cs
- Covers: Game/Inventory/ItemFinderEntry.BuildCatalog, SwingContext
- Checks: drops In Game 0 items; swing averages; bonus columns; hit magic counted on weapons only; martial-arts rows; jumpkick speed by realm.
- Why: Item Finder correctness

### ItemFinderViewModelTests.cs
- Covers: ViewModels/CharacterWorkshop/ItemFinderViewModel
- Checks: the level filters; the Missing tint against worn gear; the trial-damage readout following the trial gear, stats and target.
- Why: Item Finder / Gear Finder wiring

### ItemMagicIndexTests.cs
- Covers: Game/Combat/ItemMagicIndex
- Checks: hit-magic level is the sum of codes 28 and 142; unknown items return -1.
- Why: the shimmering-longsword regression

### ItemMdbViewBuilderTests.cs
- Covers: ViewModels/GameData/Edit/ItemMdbViewBuilder
- Checks: flag abilities show "Yes"; shops with several rooms; casts, teleports and summons from TBInfo; leaving out blacklisted rooms; exit and action uses.
- Why: paradigm-20260818-080337

### ItemNameStoreTests.cs
- Covers: Services/ItemNameStore
- Checks: lookup by number; IsRecordName; the off-hand and slot lists and their clearing on a null set; PackStealthOf.
- Why: item name resolution

### ItemSourceIndexTests.cs
- Covers: Services/ItemSourceIndex
- Checks: chests as containers; givers (turn-in, purchase, quest reward, chains, menus, plain trades); room commands; drops from summoned monsters; warm-ups running concurrently.
- Why: paradigm-20261006-095806, -20261008-175938, -20260911-010954

### ItemUseCastEffectTests.cs
- Covers: ItemMdbViewBuilder casts, ItemsSectionViewModel row builders
- Checks: the cast effect and damage (per-level scaling, random pools); shop rows and charm pricing; Dropped by; Placed in.
- Why: the Game Data Items record

### ItemUseCountTrackerTests.cs
- Covers: Game/Inventory/ItemUseCountTracker
- Checks: Stock use counting confirmed by the cast message; recharge versus finite items; does nothing on Paradigm; stacks; use with a target.
- Why: Stock charge display

### ItemUseTeleportResolverTests.cs
- Covers: Game/Map/ItemUseTeleportResolver
- Checks: the chain from item through spell and textblock to a teleport, with the roomitem gate; finding the cast code however the row is written.
- Why: potion of levitation route

### ItemUsedOnIndexTests.cs
- Covers: Game/GameData/ItemUsedOnIndex
- Checks: which exits need an item (key, item, ticket); the room actions that need it; switching sets; the warm-up.
- Why: the "Used on" section of an item record

### ItemUsesQueryHandlerTests.cs
- Covers: Game/Remote/ItemUsesQueryHandler, Game/Inventory/CarriedChargeReadout
- Checks: @uses with a name or bare; best-match by shorthand; infinite and non-charged items; replies even with denials silenced.
- Why: the "9998 charges" bug

### ItemsSectionViewModelFlagKeywordTests.cs
- Covers: ViewModels/GameData/Tables/ItemsSectionViewModel.FlagKeywords
- Checks: each filter word matches its own flag and no other.
- Why: the Items tab filter

### JetLvalChainTests.cs
- Covers: Vendor JetDatabaseReader AccessReader.ParseLvalChainRow
- Checks: the data in a chained memo page starts after the 4-byte pointer.
- Why: the "class 1" → "ass 1" truncation when importing

### JsonEntryIdentityTests.cs
- Covers: Services/JsonEntryIdentity
- Checks: identity versus equal content for settings entries.
- Why: change detection in the settings cache

### JsonStoreTests.cs
- Covers: Services/JsonStore
- Checks: round-trip; no temp files left behind; concurrent saves to the same file don't fail.
- Why: two clients crashing on the same boss-timers.json

### KeybindEditDialogViewModelTests.cs
- Covers: ViewModels/Keybind/KeybindEditDialogViewModel
- Checks: taking a key from another built-in shows a warning, a macro or reserved key is an error; Escape is captured as a binding; the capture toggle.
- Why: the rebind dialog

### KeybindRegistryTests.cs
- Covers: Services/KeybindRegistry
- Checks: excluded and forbidden keys; reserved keys follow rebinds; system-reserved keys; bindable list; the keyboard period versus the numpad period.
- Why: macro and keybind validity

### KeybindingStoreTests.cs
- Covers: Services/KeybindingStore
- Checks: default keys; Rebind and its event; FindAction; an empty chord never conflicts.
- Why: keyboard shortcuts

### KillHandlerTests.cs
- Covers: Game/Remote/KillHandler
- Checks: @kill retargets (multi-word names); silent on success; the failure or refusal reply depends on WarnOnDenial; permission gating.
- Why: the remote-command reply policy

### KnownSpellCatalogTests.cs
- Covers: Game/Spells/KnownSpellCatalog
- Checks: which spells each class can use (magery, class lists, level, Kai autolearn, alignment); targets; RoomAttackFromLevel; class resolution.
- Why: the Spell Book

### KnownSpellCatalogWearSlotTests.cs
- Covers: KnownSpellCatalog.WearSlotFor
- Checks: the wear slot comes from the numeric Worn field.
- Why: the warhorn was labelled as Weapon Hand

### LairEditorDialogViewModelTests.cs
- Covers: ViewModels/Navigation/LairEditorDialogViewModel
- Checks: validation; marker rows; Save/Cancel; renaming deletes the old file.
- Why: the lair editor flow

### LairEditorOtherGameDataTests.cs
- Covers: LairEditorDialogViewModel game-data guard
- Checks: no save, rename or marker add while another set is active; stamped sets; no active set.
- Why: saves were landing in another set's Loops folder

### LairEntryDebuffHoldTests.cs
- Covers: Game/Map/LairEntryDebuffHold, LairEntryDebuffModes, NavActivity
- Checks: the three wait modes; buffs held and released (debuff sent, fight ends, after two rounds, engine signal); fights at the door; the one cast owed at the next lair; waiting for mana; priority-buff limits.
- Why: paradigm-20261007-120006, -20261012-080902; user 2026-10-12

### LairEntryPriorityBuffWaitTests.cs
- Covers: LairEntryDebuffHold wired to the real CastingDirector
- Checks: casting priority buffs while waiting; holding ordinary buffs; skipping buffs that can't be paid for; give-ups; the mana wait; lairs in a row; casts owed after a short room in between.
- Why: the "Wait for priority buff spells" mode

### LairManagerTests.cs
- Covers: Game/Map/LairManager
- Checks: LoadAll; save, round-trip, delete, ordering and events; migrating the old suffix and the BBS-folder lairs; names with illegal filename characters.
- Why: storing lair setups

### LairSetupRowViewModelTests.cs
- Covers: ViewModels/Navigation/LairSetupRowViewModel
- Checks: the anchor key and the "N lairs" label.
- Why: the right-rail row labels

### LairTagShapeTests.cs
- Covers: Game/Map/RoomTooltipBuilder.ParseLairTag
- Checks: the "(Max N)" lair-cell shape the imported data uses is read; ids aren't taken from the group key; empty cells are tolerated.
- Why: loops were reported as having zero lair monsters

### LairTimerStoreTests.cs
- Covers: Game/Map/LairTimerStore
- Checks: default respawn from the lair room's per-room Delay, read by realm; set-switch cache drop; max respawn; ready-time from last kill (Stock) vs entry (Paradigm); local/UTC kill stamps.
- Why: report paradigm-20261010-145330; issue #839 (Paradigm clock).

### LeaderBossTravelProbeTests.cs
- Covers: Game/Remote/LeaderBossTravelProbe
- Checks: follower asks the leader `@path`; boss destination yes / other no; no reply or idle leader asks again on the next move then gives up; other senders ignored.
- Why: keeps the Bossing gear set on only when the party heads to a boss.

### LeaderDoorAssistManagerTests.cs
- Covers: Game/Map/LeaderDoorAssistManager
- Checks: the leader's failed bash makes a follower send bash/pick the same way; non-leader, toggle off, no party do nothing; family name matched by given name.
- Why: Party setting "help leader open doors".

### LeaderRouteResolverTests.cs
- Covers: Game/Map/LeaderRouteResolver
- Checks: picks the route whose step count matches the leader's reported steps-left (usual vs through avoided rooms), flags the closest when none match.
- Why: @path route redraw.

### LeaderboardCaptureTrackerTests.cs
- Covers: Game/Leaderboard/LeaderboardCaptureTracker
- Checks: a `top N` block is captured from header to prompt / non-row line with its requested count; truncated request marks complete; no header, no capture.
- Why: the realm rankings feed off this capture.

### LeaderboardParserTests.cs
- Covers: Game/Leaderboard/LeaderboardParser
- Checks: column starts from the header row; rows with spaced names/guilds, rank 100, abutting columns; separators/prompts rejected; `top N` echo parsed.
- Why: fixed-width columns can't be whitespace-split.

### LeaderboardSnapshotStoreTests.cs
- Covers: Services/LeaderboardSnapshotStore
- Checks: newest-first history capped at 5, widest board pinned, unchanged recaptures dropped (Captured still raised), class/roster change kept, clear, per-realm persistence.
- Why: XP/HR needs a stable history; small views mustn't evict the full board.

### LeaderboardXpRateCalculatorTests.cs
- Covers: Game/Leaderboard/LeaderboardXpRateCalculator
- Checks: XP/HR over elapsed time, idle captures skipped, reroll/new-name notes, rank deltas, previous rate, dropout notices on complete vs truncated boards, merged board ranking.
- Why: reported bug — viewing 10 of 100 pruned the other 90.

### LearnedArrivalLookupTests.cs
- Covers: Game/Recovery/LearnedArrivalLookup
- Checks: a room with no reading takes its land's readings; majority reading wins and the odd room is an exception; nearby deaths count; per-land caching invalidated on store change/map rebuild; disputed rooms give nothing; temple/evil-tomb pair.
- Why: hang-up death arrival prediction.

### LearnedArrivalRoomStoreTests.cs
- Covers: Services/LearnedArrivalRoomStore (and Services/SharedFileWrite)
- Checks: learning/dispute/settle/take-over counts, per-set files shared between clients, merge on write, broken/later-format/locked/unwritable files never written over, temp-file cleanup, hold timeout. The hold-timeout cases wait SharedFileWrite's real 250 ms: a wait knob would exist only for tests, and the bounds are one-sided.
- Why: multi-client safety of a shared per-set file.

### LearnedRoomNameInListsTests.cs
- Covers: Game/GameData/RoomNameIndex, TrainerCatalog, BankCatalog, QuestScriptNames, Services/ItemSourceIndex, Game/Quests/QuestFlagIndex
- Checks: lists label unnamed rooms with the realm's learned name and drop it on another realm; kept lists rebuilt once (on keep, realm change, or a quiet walk), not per room.
- Why: learned names are per realm and no longer written to the Rooms table.

### LearnedRoomNameStoreTests.cs
- Covers: Game/Map/LearnedRoomNameStore, RoomGraphManager (stored names), ViewModels/RoomNameLearnedDialogViewModel
- Checks: keep/refuse rules, per-realm and per-set filing, Rooms.json untouched, damaged/later-format/locked files, multi-client merge, realm renames, renamed-room relearn, prompt text. The hold cases wait SharedFileWrite's real 250 ms, as in LearnedArrivalRoomStoreTests.
- Why: user 2026-10-11 (names per realm; gang-house names change).

### LevelGatedRoomsTests.cs
- Covers: Game/Map/LevelGatedRooms
- Checks: only the room holding a level gate is marked, any window marks, doors don't, CMD-teleport minlevel marks its room.
- Why: map overlay; the stock 3/756 vortex miss.

### LevelIgnoringFilterTests.cs
- Covers: Game/Map/LevelIgnoringFilter
- Checks: with a level, floors/caps judged against it (exits and boats); without, live level gates lifted while other gates kept.
- Why: area ranking / route planning "what could I reach at level N".

### LevelProjectionCalculatorTests.cs
- Covers: Game/Calculators/LevelProjectionCalculator, ViewModels/CharacterWorkshop/LevelProjectionRow
- Checks: XP thresholds, HP min/max and regen, mana vs Kai, gear folding, resting regen; row formatting (exp to level, cost ungrouped, sneak chance).
- Why: the Level Projection grid.

### LevelProjectionColumnTests.cs
- Covers: ViewModels/CharacterWorkshop/LevelProjectionColumn
- Checks: hidden-set persistence (new columns appear for old saves), pinned Level column, catalogue order/uniqueness, bindings resolve, opt-in defaults, realm tooltips.
- Why: a column typo would silently show empty.

### LevelUpAnnouncerTests.cs
- Covers: Game/LevelUpAnnouncer
- Checks: "I can now train to level: N" on crossing, multi-level bursts, pre-banked levels silent, no repeats, channel → wire verb, profile swap reseeds.
- Why: avoid login spam.

### LightItemIndexTests.cs
- Covers: Game/Light/LightItemIndex
- Checks: only light items catalogued, strength/use count/burn time read (any ability slot), case-insensitive lookup, set switch rebuilds.
- Why: auto-light planning.

### LightModelTests.cs
- Covers: Game/Light/LightModel
- Checks: visibility bands with strict boundaries, illumination added to room light, light strength needed to see, server phrasing.
- Why: map tooltip and auto-light route predictor share one formula.

### LineExtractorTests.cs
- Covers: Terminal/LineExtractor
- Checks: cell row → text/attributes (trim rules), soft-wrap stitching (incl. 1000 rows), hard breaks kept, slow-line notice (no line text leaked), prompt/content split with rest/meditate tags.
- Why: gossip truncation bug; report paradigm-20260913-210626.

### LiveLoopSessionsTests.cs
- Covers: Game/Simulation/LiveLoopSessions
- Checks: sessions read from program logs (BeginCircle → Stop/Ended, renames, exp/kills/deaths, midnight, level/character carry rules, locked logs skipped) and pooled per character/loop/level.
- Why: simulator-vs-live-play check.

### LocalApiActionsTests.cs
- Covers: Services/Api/LocalApiActions
- Checks: destructive classification derived from catalogue categories, suffix forms inherit base category, unknown = destructive, normalisation and case.
- Why: the local API's destructive opt-in.

### LocalApiAuthTests.cs
- Covers: Services/Api/LocalApiAuth
- Checks: loopback AND bearer token both required, scheme case-insensitive, token persisted / regenerated / invalidated, fixed-time compare.
- Why: security of the local API.

### LocalApiLogCursorTests.cs
- Covers: Services/LogService (sequence/SnapshotAfter), Services/Api/LocalApiServer.ParseSeverities
- Checks: monotonic sequence across ring wrap and Clear, limit cursor resumes without skipping, severity set parsing.
- Why: tailing the program log without gaps.

### LocalApiPrivacyTests.cs
- Covers: Services/Api/LocalApiState, LocalApiMaskedEvents, Services/BugReportScrub
- Checks: /log, /scrollback, /events, /gates mask login/passwords (also JSON-escaped values), scrollback limited to in-game time, events masked on the pump in order, lost events reported.
- Why: the API is a second exit for bug-report content.

### LocalInputBufferTests.cs
- Covers: Terminal/LocalInputBuffer
- Checks: 254-char cap, backspace, Latin-1 + CR flush, Set/recall, Changed events.
- Why: MajorMUD line limit; overlay repaint.

### LocationEquipManagerTests.cs
- Covers: Game/Inventory/LocationEquipManager
- Checks: room-key parsing (exact and bare), name / room / And / Or matching.
- Why: location-based equip rules.

### LocationFiltersTests.cs
- Covers: ViewModels/GameData/Tables/LocationFilters, CategoryFilter
- Checks: Landmass → Region → Area narrowing, "(not set)", reload keeps picks, commit-then-filter, clear.
- Why: Monsters tab location filters.

### LogDiagnosticsTests.cs
- Covers: Services/LogService, LogDiagnosticState, Models/Settings LogDiagnosticsSettings
- Checks: Debug/Combat gated by toggles, Info/Warn/Error always on, Changed events, DTO defaults.
- Why: decision trail in bug reports.

### LogServiceContextTests.cs
- Covers: Services/LogService
- Checks: Context carried through Log/Warn and Snapshot; detail handlers (entry-aware and plain) coexist.
- Why: LogPane double-click detail.

### LoginAutomatorTests.cs
- Covers: Services/LoginAutomator
- Checks: step matching (CSI stripped), CR appended, pager Enter, username/password placeholders and aliases, one-shot credential locks, skips when credentials missing, TryBuild.
- Why: never resend a login; auto-login.

### LookParserTests.cs
- Covers: Game/LookParser
- Checks: player look header suffixes (gang/Immortal), race/class longest match, equipment slots, empty loadout, merge with who data, truncated blocks keep equipment.
- Why: TrapDelegationManager re-probe loop when gangs blocked recording.

### LookupEnumsTests.cs
- Covers: Game/GameData/LookupEnums
- Checks: code → label mapping for item/slot/weapon/armour/currency/spell/shop/monster/magery types; unknown and non-numeric input.
- Why: Game Data tables display.

### LoopBuilderSessionTests.cs
- Covers: ViewModels/Navigation/LoopBuilderSessionViewModel
- Checks: clicks expand to a closed cycle, per-waypoint actions and flags ride into the loop, remove/move clicks, unreachable legs block save, a set switch drops the build.
- Why: building loops on the map.

### LoopCombatSuppressionTests.cs
- Covers: Game/Map/LoopCombatSuppression
- Checks: do-not-attack and only-lair precedence, detour loops still judged, which room is judged mid-move, when the loop is "driving".
- Why: reports paradigm-20261015-160835, -20260925-070016, -20261012-080902.

### LoopEditorOtherGameDataTests.cs
- Covers: ViewModels/Navigation/LoopEditorDialogViewModel
- Checks: an open editor refuses to save or add rooms under another game-data set; saving back on the original set works.
- Why: rooms belong to the set they were picked on.

### LoopEditorRenameTests.cs
- Covers: ViewModels/Navigation/LoopEditorDialogViewModel, Game/Map/LoopManager
- Checks: a rename saves the new file before deleting the old; a failed save loses nothing; same-file renames.
- Why: no data loss on rename.

### LoopExpSimulatorTests.cs
- Covers: Game/Map/LoopExpSimulator
- Checks: 720-round cap, Stock vs Paradigm lair clocks, travel ticks, AoE vs single target, bosses amortised, summon rolls (gated/ungated, tick per realm), ring vs out-and-back.
- Why: the Exp Estimator; GAME_MECHANICS "Room-spell monster summons".

### LoopFavoritesStoreTests.cs
- Covers: Services/LoopFavoritesStore, Game/Map/LoopManager, LairManager
- Checks: favourites are per character, taken once from old shared files, never rewrite the loop file, survive saves, forgotten on delete, applied when loops load after the profile.
- Why: one character's favourites were everyone's.

### LoopManagerTests.cs
- Covers: Game/Map/LoopManager
- Checks: load/save/delete under the set's Loops folder, events, ordering, illegal names, shared vs character settings, lair-wait mode by name or number, v2 → v3 migration, numbered names, waypoint expansion.
- Why: loop file format and catalogue.

### LoopOutsideChangeTests.cs
- Covers: Game/Map/NavFolderWatcher, LoopManager, LairManager, NavFolderManager
- Checks: another client's loop/lair writes, deletes, moves seen on the second beat; own writes not echoed; unreadable files and folders handled; running loops keep their copy; walk and parse run off the UI thread.
- Why: two clients sharing a set's Loops folder.
- Test entry point kept: NavFolderWatcher.InFlight: the poll hands work across threads by design, so a test needs the barrier to know a pass is done.

### LoopRunnerEmptyRoomCommandTests.cs
- Covers: Game/Map/LoopRunner (empty-room commands)
- Checks: a `nomonsters` room command waits for the room to clear, a refusal with a monster present holds instead of recovering, the time limit ends the loop with a reason unless a fight holds it.
- Why: recoveries were spent on `go hole` refusals.

### LoopRunnerTests.cs
- Covers: Game/Map/LoopRunner
- Checks: start/approach/stop, waypoint commands and delays, recovery (blocked, rm resync, stall watchdog, budgets), disconnect/reconnect resume and holds, flee resume, same-burst pause races, doors/traps/hidden exits, lap timing.
- Why: many reports (paradigm-20260901-*, -20260913-*, -20260927-*, -20261005-194751, -20261014-180148, …).

### LoopSettingsStoreTests.cs
- Covers: Services/LoopSettingsStore, LoopManager, LoopEditor/LoopBuilder VMs, Game/Remote/LoopShareReceiver
- Checks: only-lair and lair-wait are per character; the file keeps its own pair; edits, renames, deletes, builder and `@loop send` keep them on the character; same-loop rejection and numbered copies.
- Why: one character's ticks used to change every character's loop.

### LoopShareTests.cs
- Covers: Game/Remote/LoopShareCodec, LoopShareHandler, LoopShareReceiver, RemoteCommandManager (answers)
- Checks: codec round-trip and hostile payloads, offer → yes/no → send, offer codes verified, telepath-only, result answers and their gating, numbered copies.
- Why: `@loop send` safety and integrity.

### LoopSignatureTests.cs
- Covers: Game/Map/LoopSignature
- Checks: fixed known hashes, short code, labels and file-held settings excluded, every route/setting change alters it, no collisions.
- Why: two client versions must agree on "same loop".

### LoopSimulatorTests.cs
- Covers: Game/Simulation/LoopSimulator, SimCharacterBuilder, LoopSimSummary
- Checks: lair respawn clocks (Stock room clock vs Paradigm slots), deaths end runs, rest/run/hang-up triggers, spells/buffs/debuffs/rerolls, procs and burns, death summons and caps, sneak backstab, alignment sparing, dodge at the floor.
- Why: reports paradigm-20260930-114254, -20261004-024314, -20261014-121847.

### LoopUnpaidCrossingTests.cs
- Covers: Game/Map/LoopRunner (tolls), Services/MovementFilter, MovementRefusalDetector, OwedPurseRead
- Checks: loops over unaffordable tolls aren't started, mid-lap refusals read the purse then stop or re-plan round, party-toll refusal, no crash on zero legs.
- Why: user 2026-10-10; crash from an empty expansion.

### MacroDispatcherTests.cs
- Covers: Services/MacroDispatcher
- Checks: chord → send with CR, multi-step `;`/`^M`, modifiers, disabled macros, excluded keys never fire, Escape bindable.
- Why: macro keys.

### MacroStoreTests.cs
- Covers: Services/MacroStore
- Checks: command splitting rules (`;`, `^M`, newline, game `;` kept), typed-input splitting, duplicate chords, default compass macros.
- Why: the slow-talk `;` and chained commands.

### MainMenuEntryAutomationTests.cs
- Covers: Game/MainMenuEntryAutomation
- Checks: entry fires only when armed and in window (one shot), hang-up suppresses it, KeepArmed refresh, move suppressor ordering, room-gated stat/i startup.
- Why: a gossiped menu line must not re-enter the realm.

### ManaRegenBreakpointCalculatorTests.cs
- Covers: Game/Calculators/ManaRegenBreakpointCalculator
- Checks: tick formula, breakpoint ladder inside the roll range, gear folding, druid stat average.
- Why: reroll planning.

### ManaRegenRerollerTests.cs
- Covers: Game/Spells/ManaRegenReroller
- Checks: Paradigm abil-145 and Stock tick-monitor reroll cycles, threshold/cap/unlimited, mana floor pause, held during combat, reconsidering the active roll.
- Why: report paradigm-20260924-123009; user 2026-09-28.

### ManualClock.cs
- Test infrastructure.
- Covers: test helper (clock, Stopwatch timestamp, delays) used by 5 test files.
- Checks: n/a (helper; all members used). `RegenTrackerTests` / `RegenDiagnosticsRecorderTests` keep private FakeClock copies that could use it.
- Why: deterministic timing.

### ManualTimers.cs
- Test infrastructure.
- Covers: test helper: the injected one-shot scheduler (Schedule / Arm, RunDue, RunAll, Advance, PendingDelays)
- Checks: n/a (helper).
- Why: components take an optional scheduler that production never passes; tests fire the captured timers by hand.

### MapControlConnectionTests.cs
- Covers: Controls/MapControl.ClassifyConnection
- Checks: adjacent / gap-bridge / stub decision by placement and distance cap.
- Why: non-tiling diagonals (sewers 603↔604).

### MapControlHeatTests.cs
- Covers: Controls/MapControl.HeatBucketIndex
- Checks: 30 s lair-heat buckets and clamp.
- Why: lair heat colours.

### MapControlStandInTests.cs
- Covers: Controls/MapControl.StandInServes
- Checks: when the cached picture still covers a pan/zoom.
- Why: routes drawn over blank space.

### MapSpellPaletteTests.cs
- Covers: Controls/MapControl spell palettes
- Checks: by-name swatches are chromatic and far from the room grey; by-teleport swatches stand off every neighbouring fill and read green/yellow/red.
- Why: icy-mountain report (spell rooms vanished).

### MasterSwitchGateTests.cs
- Covers: Game/Map/MovementCoordinator, Game/PartyRestSync, PostDeathInventoryRefresh, GreetManager, PlayerLookManager, Game/Remote/DivertHandler
- Checks: holds parked and restored around the master switch, @wait/@ok quiet then resynced, small senders follow the switch.
- Why: master switch semantics (user, 2026-10-09).

### MasterSwitchRemoteCommandTests.cs
- Covers: Game/Remote/RemoteCommandManager, AutoModeRemoteHandler, Game/AutoModeController
- Checks: switch off: nothing obeyed or answered except @auto-all; base modes on switch-on; kept @comeback answered/dropped correctly.
- Why: user 2026-10-09.

### MaxStrengthIndexTests.cs
- Covers: Game/Map/MaxStrengthIndex
- Checks: strongest race cap + best wearable/held +STR gear, default fallback, set switch.
- Why: door bash ceiling replaces hardcoded 200.

### MdbImporterTests.cs
- Covers: Services/MdbImporter, ViewModels/MainWindowViewModel.BuildMdbCompleteStatus
- Checks: missing file, filename sanitising, empty-folder removal (populated kept), JSON round-trip check, completion status text.
- Why: crash report with a corrupt table.

### MegaMudHashTests.cs
- Covers: Game/Map/MpFile/MegaMudHash
- Checks: name hash, exit codes (door ×2, hidden excluded), door-like hints, hash/exits split.
- Why: .mp import room matching.

### MegaMudImportDialogViewModelTests.cs
- Covers: ViewModels/Profile/MegaMudImportDialogViewModel, MegaMudImportRowViewModel
- Checks: only changed values handed back, rest-command caution follows edits, BBS settings ticked only for an empty board.
- Why: import review.

### MegaMudProfileImporterTests.cs
- Covers: Services/MegaMudProfileImporter, MegaMudIni, MegaMudImportPlan
- Checks: ini parsing, active profile becomes live settings, combat profiles, buff slots, other tabs, left-behind lines, encrypted login, redial/cleanup onto the BBS, review edits, party rank, rest-command advice.
- Why: MegaMUD character import.

### MeleeMatchupProfilesTests.cs
- Covers: Game/Calculators/CharacterCalculator (melee profiles)
- Checks: usable melee attacks by class/race, normal profile equals the generic one, by-hand stats match, damage range, smash single swing.
- Why: Monster Intel / Item Finder matchups.

### MemberGateJudgeTests.cs
- Covers: Game/Remote/MemberGateJudge
- Checks: level/class/race/toll/fare/item/alignment gates judged from what is known of a member (true/false/unknown).
- Why: party gate planning.

### MenuActionCatalogueTests.cs
- Covers: Services/MenuActionCatalogue, ToolbarItemCatalogue, ContextMenuDefaults, ContextMenuConfig, ViewModels/Settings/ContextMenuRowViewModel
- Checks: every catalogue command/toggle/gesture resolves on MainWindowViewModel by reflection, submenus and deep links valid, defaults resolve, ContextMenuConfig.ApplyFrom takes entries, folders and custom labels.
- Why: a reflection typo is a silent dead menu item.

### MenuNavImportOptionTests.cs
- Covers: ViewModels/Settings/MenuNavImportOption
- Checks: which logon sequences are offered for import, exclusions, ordering, label.
- Why: import logon steps from another character.

### MessageCandidateReportReplayTests.cs
- Covers: Game/MessageCandidateWatcher (with real seeds and exclusions)
- Checks: lines from real unrecognized-line exports stay out; genuine room-spell unknowns still captured.
- Why: exports unrecognized-lines-20260912-235152, -235315, -20260922-014747.

### MessageCandidateStoreTests.cs
- Covers: Services/MessageCandidateStore
- Checks: sightings create/bump records, dismiss freezes, first location kept, remove, contains.
- Why: unrecognized-line review queue.

### MessageCandidateWatcherTests.cs
- Covers: Game/MessageCandidateWatcher, Game/BenignChatterMatcher
- Checks: what is and isn't staged (catalogue/templates, router patterns, rosters, room names, echoes, quotes, art, listings, death flavour, menus), burst cap, gates.
- Why: report paradigm-20261008-125639 and others; keep the queue to real unknowns.

### MessageEditExporterTests.cs
- Covers: Services/MessageEditExporter
- Checks: diff against the seed (changed/added/unchanged), markdown + JSON render.
- Why: "Upload edits" export.

### MessageEditLinkFillTests.cs
- Covers: ViewModels/GameData/Edit/MessageEditDialogViewModel
- Checks: linking a spell fills empty slots, surfaces conflicts, keep-record vs keep-line resolution.
- Why: committing a candidate onto an existing record.

### MessageRecordSentinelTests.cs
- Covers: Models/GameData/MessageRecord
- Checks: {null}/{void}/{empty} sentinels, blank-or-absent, too-short patterns.
- Why: Incomplete Messages worklist; corrupt one-letter patterns.

### MessageRouterTests.cs
- Covers: Services/MessageRouter, Patterns (Regex, Prefix, Exact)
- Checks: pattern matching, fan-out, priority order, dispose unsubscribes, snapshot during dispatch, read-only probe.
- Why: CLAUDE.md "MessageRouter is fan-out".

### MessageTemplateIndexTests.cs
- Covers: Game/Spells/MessageTemplateIndex, CasterMessageMatcher
- Checks: real seeds index templates, known casts recognised, uncatalogued lines not claimed, placeholder-only templates dropped, longest literal word.
- Why: under-matching flooded the queue; over-matching would hide unknowns.

### MessagesSectionFilterTests.cs
- Covers: ViewModels/GameData/Tables/MessagesSectionViewModel
- Checks: spell-number column, missing/malformed slots, records claimed by present spells/items hidden.
- Why: no record stranded without an editor.

### MessagesSeedContractTests.cs
- Covers: Models/GameData/MessageRecord (JSON contract)
- Checks: decoder-shaped seed loads (enum names, links, legacy keys tolerated, defaults).
- Why: drift would blank the catalogue.

### MonsterAggroCalculatorTests.cs
- Covers: Game/Calculators/ParadigmAggroCalculator, StockAggroCalculator
- Checks: Paradigm scores/lottery/floor; Stock acquisition by alignment, spread, follow lock, stickiness.
- Why: Monster Aggro calculator.

### MonsterCastLevelTests.cs
- Covers: Game/Combat/MonsterCatalogEntry.CastLevelFor
- Checks: only spell slots and mid-spells give a cast level; physical accuracy collisions and procs don't.
- Why: ailment chip durations timed off unrelated attacks (431 collisions).

### MonsterCatalogTests.cs
- Covers: Game/Combat/MonsterCatalog
- Checks: field parsing, effective exp, 255 Undead byte, attack/drop/mid-spell slots and spell damage, resist/magic fields, cast elements, physical accuracy, in-play list, set switch.
- Why: report paradigm-20260928-105509.

### MonsterCountGateTests.cs
- Covers: Game/Combat/MonsterCountGate
- Checks: min/max room count window and the kill-all-engaged override.
- Why: finishing leftovers below the floor.

### MonsterDeathSummonIndexTests.cs
- Covers: Game/Combat/MonsterDeathSummonIndex
- Checks: summons-on-death only when DeathSpell carries the Summon ability.
- Why: report paradigm-20260729-211336.

### MonsterDeathWatcherTests.cs
- Covers: Game/Combat/MonsterDeathWatcher
- Checks: exp then *Combat Off* is a death (once, within window); Offs answering echoed commands aren't; room-spell kills not double counted.
- Why: report paradigm-20261010-145330.

### MonsterDebuffCalculatorTests.cs
- Covers: Game/Spells/MonsterDebuffCalculator, Game/Calculators/MonsterMatchupCalculator
- Checks: debuff folding (DR ÷10, slow flag), stat vs crowd-control spells, monster swings from energy.
- Why: Monster Intel "Apply Debuffs".

### MonsterDropIndexTests.cs
- Covers: Services/MonsterDropIndex
- Checks: droppers per item, zero slots skipped, spawn rooms from Summoned By, set clears.
- Why: path-item monster-drop routing.

### MonsterDropRouterTests.cs
- Covers: Game/Map/MonsterDropRouter
- Checks: prompt and reroute to the nearest spawn for an unsold path item; every gate that prevents it; hunting, found, failed, redirected; stale confirms.
- Why: path-item detours.

### MonsterEditDialogViewModelTests.cs
- Covers: ViewModels/GameData/Edit/MonsterEditDialogViewModel
- Checks: spell override resolution (spell-only), cast codes round-trip, installed-defaults equality, rung save, location boxes vs seed, wrong-kind spells block save.
- Why: reports paradigm-20260813-070249, -131658.

### MonsterEngagementTests.cs
- Covers: Game/Combat/MonsterEngagement
- Checks: which relationships auto-combat engages; kill-on-sight neutrals; user-engaged instances.
- Why: auto-combat targeting.

### MonsterFlavorLineSeedTests.cs
- Covers: Messages seeds (both realms)
- Checks: the Blood God cultists' cry is seeded, linked to its 15 monsters, with a matching id.
- Why: imported data has no column for it.

### MonsterHitBandTests.cs
- Covers: ViewModels/Navigation/MonsterHitBand
- Checks: green/amber/red/muted bands with inclusive boundaries.
- Why: route Details colours.

### MonsterHpTrackerTests.cs
- Covers: Game/Combat/MonsterHpTracker
- Checks: damage lowers estimates, regen every 30 s, looks pull into the band and retime regen, shared names, area damage, room change.
- Why: TGT HP readout.

### MonsterIntelIncomingHitTests.cs
- Covers: ViewModels/MonsterIntelViewModel, Game/Combat/MonsterIntelEntry
- Checks: Hits You % uses the class floor and dodge, matches the matchup panel, moves with applied debuffs.
- Why: report paradigm-20261014-121847.

### MonsterIntelViewModelTests.cs
- Covers: ViewModels/MonsterIntelViewModel, Game/Combat/MonsterIntelEntry
- Checks: list populated on open, rounds cap persisted and reported, attack basis picker, spells count, defense sim seeds from loadout, hits bands, hide unfightable / 0 exp.
- Why: empty-list bug; report paradigm-20260928-105509.

### MonsterLifeIndexTests.cs
- Covers: Game/Combat/MonsterLifeIndex
- Checks: drain eligibility and target-class (living/undead/animal) checks, fail-open.
- Why: drain/turn spell gating.

### MonsterLookParserTests.cs
- Covers: Game/MonsterLookParser
- Checks: wound bands to HP windows, wound-line parsing, end-to-end look block, shorthand labels.
- Why: user-confirmed band ladder.

### MonsterMatchupCalculatorTests.cs
- Covers: Game/Calculators/MonsterMatchupCalculator, MonsterMatchupCalculatorSpells
- Checks: hit chance and DPS (dodge at floors, Stock DR multipliers, crits), prot/vile wards by alignment, spell ranking (immunity, resist, target type), weighted incoming hit.
- Why: report paradigm-20261014-121847.

### MonsterMovementPatternTests.cs
- Covers: Services/Patterns (DefaultPatterns monster arrival/departure)
- Checks: Stock appear/leave line shapes match, chat doesn't.
- Why: missed arrivals/departures confuse targeting.

### MonsterObservationCastLineTests.cs
- Covers: Game/Combat/MonsterObservationTracker with CombatSessionTracker / RoundDamageTracker
- Checks: a cast line counted as a miss is retracted when the spell lands; real whiffs kept.
- Why: report paradigm-20261010-145330.

### MonsterObservationTrackerTests.cs
- Covers: Game/Combat/MonsterObservationTracker
- Checks: hits/misses/no-effect attributed per monster, own negated buffs aren't misses, retraction, landed hit retires physical no-effect.
- Why: report paradigm-20260910-214553; user 2026-10-17.

### MonsterRelationshipWatcherTests.cs
- Covers: Game/Combat/MonsterRelationshipWatcher (with HealthManager)
- Checks: Hangup/Flee monsters answered once per sighting, the minute after a reconnect, what holds it (switches, menu, PvP), fight-back when no run is coming, end-to-end through HealthManager.
- Why: user 2026-10-09.

### MonsterResistIndexTests.cs
- Covers: Game/Combat/MonsterResistIndex
- Checks: Maps attack type to elemental resist code. Reads a monster's signed resist; zero and non-elemental values are dropped.
- Why: The pre-emptive resist guard, which skips spells a monster fully resists.

### MonsterSpawnIndexTests.cs
- Covers: Services/MonsterSpawnIndex
- Checks: Classifies placed, assigned and lair "Summoned By" tokens; dedups them, including across rows; handles 20k monsters in one room; Warm(); invalidates on a set switch.
- Why: paradigm-20260911-231808, a 29 s UI stall.

### MonsterSpellEfficiencyTests.cs
- Covers: Game/Calculators/MonsterMatchupCalculatorSpells, Game/Combat/ElementalResistIndex
- Checks: Damage per mana; rounds and mana to kill; ranks spells by efficiency; folds in the magic-resist average and full-resist chance; element code/name round trip.
- Why: Monster Intel's spell ranking.

### MonsterSummonTargetsIndexTests.cs
- Covers: Game/Combat/MonsterSummonTargetsIndex
- Checks: Summoned-child lookup; AbilVal wins, MinBase is the fallback; non-summon spells yield nothing.
- Why: Widens the room-aware resolver's candidate set.

### MonsterSummonWatcherTests.cs
- Covers: Game/Spells/SummonLineSet, Game/Combat/MonsterSummonWatcher, RoomEntityClassifier.RosterIsWhole
- Checks: Reads the summon shout; a summon by a roster monster redisplays the room and marks the roster short.
- Why: Stops a kill from emptying the roster mid-summon.

### MonstersSectionViewModelTests.cs
- Covers: ViewModels/GameData/Edit/MonsterMdbInfoBuilder, Tables/MonstersSectionViewModel.ComputeAttackAccuracy
- Checks: Lair-tag id match; Accuracy column; summon detection, context, targets and outgoing labels.
- Why: The Game Data Monsters tab columns. The file name doesn't match its content (mostly MonsterMdbInfoBuilder).

### MovePlayerHandlerStartReplyTests.cs
- Covers: Game/Remote/MovePlayerHandler
- Checks: @loop, @lair and @goto answer "started" only when they started; otherwise the engine's reason; a goto answered after the in-flight move settles.
- Why: False "looping" / "walking" replies.

### MovePlayerHandlerStopGotoTests.cs
- Covers: Game/Remote/MovePlayerHandler, MovementController
- Checks: @goto after @stop walks; @stop announces once per hold; @stop then @rego resumes.
- Why: Live report that "@stop then @goto won't move".

### MovementControllerTests.cs
- Covers: Game/Map/MovementController (+ AutoWalk, LoopRunner, AutoLair, Coordinator)
- Checks: Coalesced run state; pause, resume and stop routing; Auto-All suspend; an errand held by Stop and the resume question; pause by typed move; puzzle solvers.
- Why: The @stop / Auto-All / errand regressions.

### MovementCoordinatorTests.cs
- Covers: Game/Map/MovementCoordinator
- Checks: Gate assert/clear and pause transitions; the GatesChanged event; the history ring buffer; [Gate] log lines.
- Why: The "why are we paused" label and the bug-report history.

### MovementFilterTests.cs
- Covers: Services/MovementFilter (+ BfsMapper integration)
- Checks: Avoided and stash sets with profile lifecycle; every exit gate (level, class, race, alignment, toll, fare, item, key and plain doors, hazards, boats); the acquirable-gate suspend; route-scoped warm-ups.
- Why: Routes around gates the party can't pass; stock-20260908-103628, paradigm-20261004-204354.

### MovementRefusalDetectorTests.cs
- Covers: Game/Map/MovementRefusalDetector
- Checks: Refusal, typing-limit, confusion, hold and exit-gate lines revert a pending move; door open/close flags; fall lines.
- Why: Stops a Pending move from stranding (#478, paradigm-20261006-221209, paradigm-20260928-110224).

### MovementSpeedCalculatorTests.cs
- Covers: Game/Calculators/MovementSpeedCalculator
- Checks: 1100 ms base; encumbrance, slowness and quickness terms; cap state and quickness to cap.
- Why: The Calculators tab and the Paradigm cost model.

### MpFileImporterTests.cs
- Covers: Game/Map/MpFile/MpFileImporter, LoopExpander.ResolveLegRoomKeys
- Checks: Step-aligned translation with blank rows and hash resync; dead reckoning; rooms set by hand; default waypoints; anchor candidates, hints and Rooms.md.
- Why: #243, importing MegaMUD .mp loops.

### MpFileParserTests.cs
- Covers: Game/Map/MpFile/MpFileParser
- Checks: Single and dual headers; goto path vs loop; problems reported rather than thrown; Ctrl-Z; path details and step flags; CRLF.
- Why: #243, stock .mp files.

### MpImportReviewOtherGameDataTests.cs
- Covers: ViewModels/Navigation/MpImportReviewViewModel
- Checks: Accept, Verify and Rooms.md are refused under other game data; a start room picked away waits, then rebuilds; edits lost; a stash tick is not saved.
- Why: Saves landing in the wrong set's Loops folder.

### MpImportReviewViewModelTests.cs
- Covers: ViewModels/Navigation/MpImportReviewViewModel
- Checks: Remove, insert and move rows; the shape survives a retranslation.
- Why: Reshaping .mp imports.

### MultiActionExitParseTests.cs
- Covers: Game/Map/MultiActionExitData
- Checks: Modifier count and order; action-cell shape, remote room and required item; RequiresUnheldItem.
- Why: Multi-action exits such as levers and talismans.

### NameEntryTests.cs
- Covers: Services/ProfileService, BbsProfileStore, GameDataSetManager, RealmCatalog, PathGuard, StartupOptions, ViewModels/Profile name dialog, NavFolders
- Checks: Unusable names are refused consistently; dialog vs store agreement; --profile token; odd names from older clients; loop files stay inside their folder.
- Why: Path-traversal and name safety.

### NameGuardGapTests.cs
- Covers: AppPaths.RefusedName listing, GameDataCache.UsableSet, NavFolders, PathGuard
- Checks: The refused-names folder is never listed; set-name usability; nav folders stay inside the root.
- Why: Names that slipped past the guard.

### NavActivityTests.cs
- Covers: ViewModels/Navigation/NavActivity, NavHoldChipStrip
- Checks: Every gate has a label; activity text and kind; hold chips and their linger.
- Why: "Waiting — AutoAll" raw gate names; paradigm-20261003-162514.

### NavFlatListTests.cs
- Covers: ViewModels/Navigation/NavTreeBuilder.Flatten, NavFlatList
- Checks: Flat projection; expand and collapse splice; depth; child state restored.
- Why: Uniform-height virtualized nav tree.

### NavLineStylesTests.cs
- Covers: Models/Settings/NavLineStyles
- Checks: An unset field resolves to the default; IsEmpty.
- Why: Delta-only settings storage.

### NavSeedBootstrapperTests.cs
- Covers: Services/NavSeedBootstrapper
- Checks: Seed ledger that adds new items and respects deletions; legacy-marker migration; revised loops with backup; Restore defaults.
- Why: Never resurrecting loops or favourites the user deleted.

### NavTreeBuilderTests.cs
- Covers: ViewModels/Navigation/NavTreeBuilder
- Checks: Default expand state; a user override survives Sync; filter cycle restores the resting state.
- Why: Folder state across rebuilds and filters.

### NeedsRegistryTests.cs
- Covers: Services/NeedsRegistry
- Checks: Post with dedup and event; resolve; clear.
- Why: The light and path-item demand pipeline.

### NegatedCastLineTests.cs
- Covers: Game/Spells/NegatedCastLine, BenignChatterMatcher
- Checks: Negated/resisted cast lines in Paradigm and Stock forms; chat quotes rejected; own-negate only on the caster line.
- Why: paradigm-20261011-165726; user 2026-10-17 ("a resist is not a negate").

### NonCasterAttackLineTests.cs
- Covers: Game/Combat/NonCasterAttackLine
- Checks: An attack-shaped line by a known non-caster is suppressed; casters and unknown actors are kept.
- Why: Unrecognized-line capture noise.

### NullNameLearningTests.cs
- Covers: Game/Map/Room, RoomTracker, RoomGraphManager
- Checks: Null-name placeholder; landing on and learning a null-name neighbour; later found by name.
- Why: MDB rooms with a null Name.

### NullableIntConverterTests.cs
- Covers: ViewModels/GameData/Tables/NullableIntConverter
- Checks: Negative, blank and invalid round-trip.
- Why: Typing "-50" into range filters.

### NumericAwareCellComparerTests.cs
- Covers: Views/GameData/Tables/NumericAwareCellComparer
- Checks: Sorts by leading number; text cells; empties first.
- Why: HP Regen and Accuracy columns sorting lexically.

### OffRoundDamageLinesTests.cs
- Covers: Game/Combat/OffRoundDamageLines, TickEngine, TickTimingLog
- Checks: Room-spell damage is off the round, monster attack spells are on it; on-hit effects.
- Why: paradigm-20261009-120757.

### OtherLevelsTests.cs
- Covers: Game/Map/OtherLevels
- Checks: Placement of other floors, overlap rules, level limit, side modes, choice of chain.
- Why: paradigm-20261004-204610.

### OtherSettingsDefaultsTests.cs
- Covers: Models/Profile/OtherSettings
- Checks: the defaults a profile with no Other entry runs on: the suicide-lives threshold 5, trap disarming on, 5 disarm attempts.
- Why: what a fresh character's suicide block and @trap auto-disarm do.

### OtherSettingsTests.cs
- Covers: Models/Profile/OtherSettings
- Checks: PrintMonsterHpOnLook reads the old ShowMonsterHpLookup key.
- Why: Settings rename compatibility.

### OutboundAttackObserverTests.cs
- Covers: Game/Combat/OutboundAttackObserver
- Checks: Typed attack verbs and targets; bash at a direction counts as a door; a room's text exit counts as a move.
- Why: The user attack override; jump exits.

### OutboundBreakObserverTests.cs
- Covers: Game/Combat/OutboundBreakObserver, AttackCommandWords
- Checks: Attack and break word table; forwarding with the asker; doors and text exits excluded.
- Why: GAME_MECHANICS "Command words and abbreviations".

### OutboundCastObserverTests.cs
- Covers: Game/Combat/OutboundCastObserver
- Checks: A typed cast code arms the resume with its target; non-casts are silent.
- Why: Between-round manual casts.

### OutboundGearObserverTests.cs
- Covers: Game/Combat/OutboundGearObserver, Services/EngineSendGate
- Checks: Gear command forms; the client's own sends vs the user's; macro steps.
- Why: paradigm-20261009-122342; re-attack after a typed gear change.

### OutboundMovementObserverTests.cs
- Covers: Game/Map/OutboundMovementObserver, RoomTracker
- Checks: Look/peek suppression; text-exit and cardinal announcements; debounce; realm-entry suppression; aux claims.
- Why: paradigm-20260716-005420, -20260722-111523, -20260911-100708.

### PacedReplySenderTests.cs
- Covers: Game/Remote/PacedReplySender
- Checks: Drains one line per beat; pacing; backoff and resend after a clobber.
- Why: Rate-limit clobbers on loop and roomba shares.

### PanicResponderTests.cs
- Covers: Game/Conditions/PanicResponder
- Checks: A partymate's @panic on say triggers a response, subject to the ignore setting, stranger and echo gates.
- Why: Inbound @panic.

### ParadigmMovementCostModelTests.cs
- Covers: Game/Map/ParadigmMovementCostModel
- Checks: Speed formula; cap clamp; latency; live inventory snapshot.
- Why: Travel-time estimates.

### ParadigmPositionResolverTests.cs
- Covers: Game/Map/ParadigmPositionResolver
- Checks: Realm gating of `rm`; coalescing; throttling; timeout; one-shot callbacks; in-flight move handling.
- Why: paradigm-20260924-053941.

### ParadigmRoomSpellSeedTests.cs
- Covers: the Paradigm message seed, Game/MessageCandidateWatcher
- Checks: Room-spell flavour wordings and shipped Ids; recognised, not staged.
- Why: paradigm-20261010-145330; unrecognized-line floods.

### PartyAilmentTrackerTests.cs
- Covers: Game/Conditions/PartyAilmentTracker
- Checks: Ailment chips set by @announce or @status; cleared by a witnessed cure; witnessed apply and duration; held pauses.
- Why: Party ailment sync.

### PartyBroadcasterTests.cs
- Covers: Game/Remote/PartyBroadcaster
- Checks: @Reset broadcast gating; skips self, blank names and invitees.
- Why: Party telepath fan-out.

### PartyComebackManagerTests.cs
- Covers: Game/Remote/PartyComebackManager (+ walker, loop, lair, PartyManager)
- Checks: @comeback and @where recovery; full-party and distance gates; backtrack; loop and lair gates; train-trip roll; Stock join vs invite; @forget.
- Why: Many reports (paradigm-20260926-195517, -20260928-074527, -20261007-*).

### PartyDeathRosterCleanupTests.cs
- Covers: Game/PartyDeathRosterCleanup
- Checks: A dead member who then shows as invited is uninvited once combat clears; scoped to leader, movement and roster.
- Why: Loop stalls on PartyInviteGate.

### PartyDisconnectDeathTests.cs
- Covers: Game/PartyManager
- Checks: Disconnect, hang-up, left-realm and par-drop grace windows; auto-invite on re-entry; leader drop dissolves the party; custom logoff line; member death.
- Why: Re-invite of lost members; stock-20260801-002423.

### PartyDisconnectMovementGateTests.cs
- Covers: Game/PartyDisconnectMovementGate
- Checks: Holds while a follower is dropped; released by re-follow or grace expiry; composes with other gates.
- Why: Not walking off from dropped followers.

### PartyEssentialHandlersTests.cs
- Covers: Game/Remote/PartyEssentialHandlers
- Checks: @version, @health, @status, @where, @who, @path, @party relay, @wait/@ok/@waiting, @lives, @invite, @join (including mortally wounded).
- Why: The remote-command reply policy.

### PartyFollowerMovementGateTests.cs
- Covers: Game/PartyFollowerMovementGate
- Checks: Follower gate asserted and cleared by role.
- Why: Followers don't drive movement.

### PartyHpEstimatorTests.cs
- Covers: Game/PartyManager.AdjustMemberHp, Game/PartyHpEstimator
- Checks: Absolute HP estimate; clamp; par correction; damage, heals and drains seen on members.
- Why: Party HP between par polls.

### PartyInventoryProbeTests.cs
- Covers: Game/Remote/PartyInventoryProbe (+ PartyPathItemGate)
- Checks: @have broadcast; reply parsing including brace-wrapped and count-only; window; unanswered members; invitees.
- Why: Party path-item counts.

### PartyLevelBoundsTests.cs
- Covers: Game/Map/PartyLevelBounds
- Checks: Folds exact and title-band levels into the (Low, High) window.
- Why: Party level gates.

### PartyLevelEstimateTests.cs
- Covers: Game/Map/PartyLevelEstimate
- Checks: Exact level vs title band precedence.
- Why: A stale exact level below the band floor.

### PartyLevelProbeTests.cs
- Covers: Game/Remote/PartyLevelProbe
- Checks: @level broadcast; reply parsing; persistence of known levels including late replies.
- Why: Narrow level gates.

### PartyLevelTrackerTests.cs
- Covers: Game/Remote/PartyLevelTracker, PartyLevelProbe parsers
- Checks: Bounds gating and fold; MegaMUD and @exp reply shapes; WarmStaleLevels.
- Why: Party level window.

### PartyManagerTests.cs
- Covers: Game/PartyManager, Game/PartyMember display props
- Checks: Follow, join and leave lines; par parsing (rank, poison, kai, invited); reconciliation; self row; rank lines; Stock "listed with" state.
- Why: Roster correctness; stock-20260801-002423.

### PartyPathItemGateTests.cs
- Covers: Game/Map/PartyPathItemGate, PartyHandOverMemory
- Checks: Follower borrowing and leader provisioning; substitutes; walk hold and release; hold caps; later legs; hand-over memory.
- Why: paradigm-20261009-011133; user 2026-10-09.

### PartyPollerTests.cs
- Covers: Game/PartyPoller
- Checks: par cadence and gates; round triggers; on-join @health and nags; @health reply parsing; invited rows.
- Why: stock-20260731-004105; @health spam.

### PartyProbeManagerTests.cs
- Covers: Game/Remote/PartyProbeManager
- Checks: Daily @level and @version probe; class change resets; version-reply capture rules.
- Why: stock-20260924-133615.

### PartyReformCoordinatorTests.cs
- Covers: Game/Remote/PartyReformCoordinator
- Checks: Leader reconnect snapshot and reform; grace rebase; one-shot.
- Why: Leader-side reconnect.

### PartyRejoinCoordinatorTests.cs
- Covers: Game/Remote/PartyRejoinCoordinator
- Checks: Remembered leader; @comeback on reconnect, with the room when confirmed; window; kill switch.
- Why: paradigm-20260928-074527; dark-room reconnect.

### PartyRestSyncTests.cs
- Covers: Game/PartyRestSync
- Checks: a follower's @wait / @ok to the leader by given name (never solo or as leader); one @wait per held reason set and an @ok only when the last reason clears; reason notes (bare health unless a pool is named, explicit notes win); Resend; HeldReasons; a stuck reason suppresses later oks; the gang heal request; IsPaused and its Changed event.
- Why: the party pause protocol: the leader must hold for every follower reason and resume only once all clear.

### PartySettingsDefaultsTests.cs
- Covers: Models/Profile/PartySettings, BuffSlot
- Checks: the defaults a profile with no Party entry runs on (par poll 5 s on the timer, auto-invite on, rank Mid, 90 s leading wait, vitals gate off, party bless gates off, both nags on); a new party buff slot's defaults.
- Why: every party engine falls through to these for a character with no saved Party section.

### PartyTollGateTests.cs
- Covers: Game/Map/PartyTollGate (+ walker, loop runner, filter, wealth tracker and probe)
- Checks: Judging the party at tolls, fares and sailings; funding hand-overs and change making; keep-back; closed for the trip; hidden members; FundingNeededFor.
- Why: User 2026-10-10 party-toll rules.

### PartyTrainPlanningTests.cs
- Covers: Game/Train/* (status codec, quorum, funding, itinerary), PartyTrainCoordinator helpers, TrainerWalkManager.PartyCeiling, CurrencyHoldings.PlanCover
- Checks: The planning core of party auto-train.
- Why: Party auto-train.

### PartyTrainTripRollTests.cs
- Covers: Game/Train/PartyTrainTripRoll
- Checks: @ptrain trip on/off to capable clients; roll handed on at close.
- Why: Fetching members a trip left behind.

### PartyViewModelTests.cs
- Covers: ViewModels/PartyViewModel
- Checks: Header text and live HP; LocalRank follows the loaded character's Party rank as it's edited; uninvite command.
- Why: PartyWindow header showing the leader instead of us.

### PartyVitalsWatcherTests.cs
- Covers: Game/PartyVitalsWatcher
- Checks: Gate held while another member is below the HP threshold; self and unobserved members excluded.
- Why: "Wait if member below %".

### PartyWaitMovementGateTests.cs
- Covers: Game/PartyWaitMovementGate
- Checks: Inbound @wait holds movement; last @ok clears it; wait timer; master-switch parking.
- Why: @wait actually holding the engine.

### PartyWealthProbeTests.cs
- Covers: Game/Remote/PartyWealthProbe
- Checks: @wealth broadcast; MudPlay and foreign reply parsing; record seam; loop and event names not misread as wealth.
- Why: The party toll check.

### PartyWealthTrackerTests.cs
- Covers: Game/Remote/PartyWealthTracker, PartyWealthProbe
- Checks: MinWealth stands down (not in party, not leading, own wallet unknown); folds self plus fresh confirmed follower purses and skips unknown or stale ones; the route-scoped Probe is debounced to one @wealth round per window; a reply lands in the reading.
- Why: a cache read from BFS must never touch the wire; an unknown wallet never refuses a walk.

### PartyWindowConverterTests.cs
- Covers: ViewModels/RankChipConverter, GreaterThanZeroConverter
- Checks: F/M/B rank chips with Mid as the fallback; the MA-row visibility test (> 0).
- Why: PartyWindow binding safety.

### PasswordProtectorTests.cs
- Covers: Services/PasswordProtector
- Checks: AES-GCM round trip, a fresh nonce per call, garbage or a wrong key gives null, the same key file decrypts across instances.
- Why: stored BBS credentials and suicide passwords.

### PathGuardTests.cs
- Covers: Services/PathGuard, Services/AppPaths path helpers
- Checks: the plain-name rule and its reasons; containment (Child, IsInside, Usable); DeleteTree only deletes strictly inside; a refusal is logged once; no AppPaths helper can leave its folder.
- Why: a hostile or odd name must not escape the data root or delete outside it.

### PathItemDemandTrackerTests.cs
- Covers: Game/Map/PathItemDemandTracker
- Checks: posts a PathItem need per missing gate item (dedup, quantity, carried); resolves on inventory change; the search-demand gate is separate from posting; deterministic sources don't arm `sea`; outstanding needs are re-offered.
- Why: reports paradigm-20260911-100708 and -103025.

### PathItemFloorCollectorTests.cs
- Covers: Game/Map/PathItemFloorCollector
- Checks: `get`s an outstanding PathItem need the moment it is seen on the floor; nothing for other need kinds or an empty floor.
- Why: a gate item that was searched up must be collected without Auto-Get.

### PathItemGiveRouterTests.cs
- Covers: Game/Map/PathItemGiveRouter
- Checks: detours to the fewest-added-steps giver, asks verbatim, then `i`; resumes on the item or on timeout; a refusal line ends the wait; declined gives aren't retried on the same trip; trades only when agreed; the user taking over abandons the detour.
- Why: reports paradigm-20260911-103025, -20261006-095806 and -20261008-175938.

### PathItemGiveSourcesTests.cs
- Covers: Services/PathItemGiveSources, GatePickSources
- Checks: chooses the trader even when the destination is behind the key door; trades only when agreed or for a key; trade notes; a free give wins over a trade.
- Why: report paradigm-20261008-175938.

### PathItemShopRouterTests.cs
- Covers: Game/Map/PathItemShopRouter
- Checks: the shop detour (fewest steps, gating, unreachable shops skipped); buys the shortfall count; resumes on inventory or timeout; bank leg (withdraw, top-up for the party's tolls, poor bank halts the detour).
- Why: route items bought on the way; a party leader withdraws for tolls (user, 2026-10-10).

### PathItemSubstitutesTests.cs
- Covers: Game/Map/PathItemSubstitutes
- Checks: boat substitute groups intersect per route, apply on commit, are dropped unless kept, and clear.
- Why: a canoe covers a raft gate only on routes where it is valid.

### PathItemSummonRouterTests.cs
- Covers: Game/Map/PathItemSummonRouter
- Checks: summon detour, then a bare-CR redisplay; a kill (no identity) triggers a capped re-survey; resumes on the drop or on timeout; stands down for a sibling detour, a cheaper source, feature off or an engine walk.
- Why: report paradigm-20260911-112005; deaths carry no identity.

### PathReplyParserTests.cs
- Covers: Game/Remote/PathReplyParser
- Checks: @path reply shapes (walk, loop, auto-lair, step clamp) and @goto destinations; @where and prose don't parse.
- Why: followers read the leader's route.

### PerformanceMonitorTests.cs
- Covers: Services/PerformanceMonitor
- Checks: timings only while collecting; the per-minute summary (count, p50, p95, max) resets; stall lines and their summary; measured scopes are summarised.
- Why: the performance log's content.
- Test entry point kept: PerformanceMonitor.CollectTo and its internal constructor: the real output is a file written from a watcher thread off the dispatcher.

### PeriodicDamageRoomSpellsTests.cs
- Covers: Services/RoomSpellDamageIndex, ViewModels/Settings/PeriodicDamageRoomSpellsSectionViewModel
- Checks: classifies damaging room spells and their rooms; whether a spell bars resting by default or by the user's choice; the tab stores only deltas, keeps other sets' choices, logs saves, handles empty states and game-data or profile changes, the wear-counter box, and the rooms-by-area list.
- Why: user, 2026-10-10 (an on-roll spell doesn't bar resting).

### PlayerDatabaseTests.cs
- Covers: Services/PlayerDatabase, PlayerObservation
- Checks: observation merge keyed on the given name (family renames); customisation layer; PurgeStale exemptions; RecordLevel, RecordVersion, RecordPartied and RecordStatedClass; relationship and PvP response kept at realm tier; Add/Remove/Find.
- Why: player identity survives family renames; relationships persist.

### PlayerDeathMovementHaltTests.cs
- Covers: Game/PlayerDeathMovementHalt
- Checks: death stops engines and clears the UserGate only; graveyard-resync CR; dispose unhooks.
- Why: report stock-20260730-194053.

### PlayerDroppedGateTests.cs
- Covers: Game/PlayerDroppedGate
- Checks: HP ≤ 0 (with real prompt data) holds the send gate and movement and clears the party; recovery releases; composes with the suicide-password hold; a single edge; dispose releases.
- Why: a mortally wounded character must not act.

### PlayerIlluminationTests.cs
- Covers: Game/Light/PlayerIllumination
- Checks: worn illumination plus the readied light's projected strength; unknown lights count zero; reads the snapshot live.
- Why: dark-room light decisions.

### PlayerLookManagerTests.cs
- Covers: Game/PlayerLookManager
- Checks: look-back and look-on-arrival toggles; skips self (and party members on arrival); once per local day across both paths; looks by given name.
- Why: a look-back loop between two clients.

### PlayerSightingTrackerTests.cs
- Covers: Game/PlayerSightingTracker, PartyState.HasMember
- Checks: sightings keyed by given name; counted once per visit; an arrival suppresses the redisplay count; self, party members, non-players and synthetic re-fires skipped.
- Why: report paradigm-20261013-173152.

### PlayersSectionViewModelTests.cs
- Covers: ViewModels/GameData/Tables/PlayersSectionViewModel
- Checks: own row hidden (by given name, case-insensitive, the live name preferred); refreshes on profile swap and close; Toggles column; dispose detaches.
- Why: report stock-20260828-104653.

### PlayersSeenViewModelTests.cs
- Covers: ViewModels/PlayersSeenViewModel
- Checks: double-click shows the sighting room, or nothing when the room is unknown.
- Why: Players Seen → map link.

### PostDeathInventoryRefreshTests.cs
- Covers: Game/Inventory/PostDeathInventoryRefresh
- Checks: death marks the inventory stale; reread once a room is known, once per death; a profile swap drops the pending read.
- Why: report paradigm-20261009-105244.

### ProfileBackupTests.cs
- Covers: Services/ProfileService.Save (backup)
- Checks: with backup off no .bak is written; with it on the file as it stood is copied to profile.json.bak before the new one is written; with no earlier file there is nothing to back up.
- Why: Settings → General "Backup profile when making changes".

### ProfileManagementTests.cs
- Covers: Services/ProfileService (create, delete, copy, move, rename events)
- Checks: file operations and clash errors; copy drops game-read state and marks it unverified; ProfileRenamed, StoredProfileMoved, ProfileDeleted and BbsRenamed semantics.
- Why: profile management must not resurrect or corrupt profiles.

### ProfileMigrationsTests.cs
- Covers: Services/ProfileMigrations
- Checks: each schema step v1→v10 (keybind and toolbar reset, per-buff bless conditions, spell priority, quest tick mark, swing-tally reset); current schema is a no-op.
- Why: old profiles load correctly.

### ProfilePickerDialogViewModelTests.cs
- Covers: ViewModels/Profile/ProfilePickerDialogViewModel
- Checks: sorted by BBS then name, case-insensitive.
- Why: picker order.

### ProfileRealmSyncTests.cs
- Covers: Services/ProfileService (Save, AssignRealm)
- Checks: a save adopts a realm another client assigned (one re-pin); our own later change wins.
- Why: a stale realm used to overwrite the reassigned one.

### ProfileServiceTests.cs
- Covers: Services/ProfileService, SpellDisplayModes
- Checks: healed character name; case-insensitive credential keys; nav enums saved by name; the by-teleport overlay is kept out of the key older clients read; ListAll skips a BBS with no profiles folder.
- Why: report stock-20260828-104653; older-client compatibility.

### ProfileStateVerifierTests.cs
- Covers: Game/ProfileStateVerifier
- Checks: a copied profile is verified by `stat` then `i`, or by the login refresh, throttled; nothing sent while busy or when not marked.
- Why: copied profiles start unverified.

### PromptParserTests.cs
- Covers: Game/PromptParser
- Checks: HP, MA and KAI parse; position; max high-water mark; stat-screen max is authoritative (no phantom MaxMa for no-mana classes); equipment delta; reset on profile swap; a gossip-quoted prompt is ignored.
- Why: reports paradigm-20260824-010304, stock-20260730-150957.

### PvpAttackWatcherTests.cs
- Covers: Game/Pvp/PvpAttackWatcher
- Checks: announce, damage and room-attack lines mark the attacker Enemy (friend flip on request); ignores party members, self, monsters and others' targets; party-split grace; recent list capped.
- Why: PvP response triggers.

### PvpFightTests.cs
- Covers: Game/Pvp/PvpFight
- Checks: engage, refusal and @kill routing; PvP spells (between-round recast, combat spell, mana floor, max casts); evil-warning opt-in and its delayed restore; end conditions; the chase (heading, guesses, doors, tracking, room limit).
- Why: the PvP engine's behaviour.

### PvpFleeWalkTests.cs
- Covers: Game/Pvp/PvpFleeWalk
- Checks: a flee and the walk back from it count as this engine's walk where they end.
- Why: other engines mustn't take them for the user's.

### PvpResponderTests.cs
- Covers: Game/Pvp/PvpResponder
- Checks: master switch (hang-up only); what draws a response and how often; PvP actions win over hangup and flee monsters; hang-up/reconnect, flee and flee-then-hang-up variants; followers never flee; gang notify throttles.
- Why: user, 2026-10-09 ("pvp actions win") and 2026-10-10.

### PvpRoomSafetyTests.cs
- Covers: Game/Pvp/PvpRoomSafety
- Checks: who holds our room attacks (non-party, party-split dropouts, possible strangers); when to leave a room (Stock guess from class and monsters, Paradigm poised line, a resident's room attack); arrivals since our move.
- Why: avoid PvP-flagging room attacks.

### PvpStrangerLookupTests.cs
- Covers: Game/Pvp/PvpStrangerLookup
- Checks: one `who` per unknown name (gap, retry); MayBePlayer until the `who` is read; realm-entry lookups; names that don't look like players skipped.
- Why: unknown players count as players while unconfirmed.

### PyramidPreflightTests.cs
- Covers: Game/Map/PyramidPreflight
- Checks: per-move pace (hop formula, 1 s floor, plus 10% lag).
- Why: report paradigm-20260827-133835.

### PyramidRooms.cs
- Test infrastructure.
- Covers: test fixture (Great Pyramid rooms)
- Checks: n/a (helper for the pyramid tests).
- Why: lets the pyramid tests walk the real data layout.

### PyramidScriptTests.cs
- Covers: Game/Map/PyramidScript
- Checks: floor and scatter detection; per-floor step shapes; scripts walk the data's exits; gates walk back to their block; F3 door classes; sphinx rooms.
- Why: GAME_MECHANICS Great Pyramid.

### PyramidSolverTests.cs
- Covers: Game/Map/PyramidSolver
- Checks: preflight; a full climb against the simulated game (dark or lit); one move in flight at a time; gate rewind; refusal and no-answer handling (Paradigm `rm`); sphinx retry; holds and run-through; F3 doors and the key; floor-1 clock and the route display.
- Why: reports paradigm-20261007-234656 and others.

### QuestAvailabilityAnnouncerTests.cs
- Covers: Game/Quests/QuestAvailabilityAnnouncer
- Checks: announces each quest whose level is crossed (multi-level, once); login dump only in the realm; the toggle suppresses without a backlog.
- Why: report paradigm-20260909-172633.

### QuestCrawlerTests.cs
- Covers: Game/Quests/QuestCrawler
- Checks: quest discovery from giveability; level bands and tiers; class, race and alignment guards; rewards, keeper items, exp; value ladders; complete values; helper flags dropped.
- Why: reports paradigm-20260925-123004, -20261011-230844; user, 2026-10-17.

### QuestEditRowViewModelTests.cs
- Covers: ViewModels/CharacterWorkshop/QuestEditRowViewModel
- Checks: ShowInJournal mapping for eligible and ineligible quests; class-restrict options persist; complete-value prefill and delta.
- Why: Quest editor eligibility controls.

### QuestEligibilityResolverTests.cs
- Covers: Game/Quests/QuestEligibilityResolver
- Checks: crawl class guard, class-restrict override, alignment checkboxes.
- Why: Magebane/Tarl leaks.

### QuestFlagIndexTests.cs
- Covers: Game/Quests/QuestFlagIndex
- Checks: relation per verb; source attribution through Called-From chains; names; sorted; set swap clears; thread-safe first build.
- Why: report Crash-20260913-120450.

### QuestFlagSyncGateTests.cs
- Covers: Game/Quests/QuestFlagSyncManager.AlreadyCheckedToday
- Checks: once-per-day gate.
- Why: limits the abil sync.

### QuestFlagSyncTests.cs
- Covers: Game/Quests/QuestFlagProbe, QuestFlagCompletion
- Checks: Stock bulk and Paradigm line parsers; newly-complete resolution (thresholds, tiers, one-way); flags to query; progress values.
- Why: report paradigm-20260925-122911.

### QuestFlagWalkthroughTests.cs
- Covers: Game/Quests/QuestScriptLine, QuestFlagIndex, QuestFlagWalkthroughBuilder
- Checks: script-line parsing (command, roll band, level); table columns; steps in words (settled versus raw); ordering; a check after the line's own change; drawn and random tables; NPC keywords.
- Why: Quest Flag Steps window correctness.

### QuestFlagsSectionViewModelTests.cs
- Covers: ViewModels/GameData/Tables/QuestFlagsSectionViewModel
- Checks: row requirement columns; filter by exact flag number or name, otherwise any column.
- Why: the Quest Flags table search.

### QuestItemGatheringTests.cs
- Covers: ViewModels/CharacterWorkshop/QuestTextFormatter.StepLines/StepEntries/PriorTierLine, QuestTickMigration
- Checks: drafted checklists add kill and fetch lines for turn-ins; reissue left out; drop versus give versus roll ranking; tier boundaries; ticks migrate.
- Why: report paradigm-20261011-230844; user, 2026-10-17.

### QuestQueryTests.cs
- Covers: Game/Quests/QuestNameResolver, QuestQueryReport
- Checks: name, alias and number resolution; labels; marked ordinals; @quest reply wording.
- Why: @quest replies.

### QuestStepGraphTests.cs
- Covers: Game/Quests/QuestStepGraph
- Checks: step drafting (command or guard led, turn-in items, give-step or ability-value order, dedup, terminal flag); exact flag match.
- Why: the followable quest checklist.

### QuestStoreTests.cs
- Covers: Services/QuestStore
- Checks: resolve precedence (overlay > seed > blank); delta-only save; manual and blocked quests; class restrict; realm-file migration.
- Why: the user's quest guides persist correctly.

### QuestTextFormatterTests.cs
- Covers: ViewModels/CharacterWorkshop/QuestTextFormatter
- Checks: fallback titles; checkbox parse; link and command splitting (backtick and quote forms); seed guides use backticks; step rendering (kill, obtain, items, rooms).
- Why: report paradigm-20261011-230844.

### QuestTickMigrationTests.cs
- Covers: ViewModels/CharacterWorkshop/QuestTickMigration, QuestTextFormatter.CheckboxMoves
- Checks: ticks follow their steps after kill lines were inserted; unchanged and user-written lists untouched; runs once; waits for game data.
- Why: v9 tick migration.

### RankingStampTests.cs
- Covers: Game/Simulation/RankingWorld, RankingWorldWatch, Services/RankingStampReader, MonsterOverlaySeedStore.StampOf, RankingCharacter, RankingCheck, RankingReach
- Checks: world differences; tables stamped as read with mid-load rereads making the world unfit; seed and game-edit stamps; character change groups; reach hash.
- Why: kept rankings must match the world and character they were made for.

### RateTextTests.cs
- Covers: Game/Combat/RateText
- Checks: compact k/M rates; percent to a tenth, never falsely 0% or 100%.
- Why: report paradigm-20260930-213857.

### RealmRegenProfileTests.cs
- Covers: Game/RealmRegenProfile
- Checks: For(realm) picks the matching cadence profile.
- Why: report paradigm-20261004-024314.

### RecentLogFileTests.cs
- Covers: Services/RecentLogFile
- Checks: two-file rolling time and size bounds; writes off the logging thread; paused under Auto-collect; prune across processes; a stalled disk drops the oldest and says so; failure notes; disk error ends the file.
- Why: the always-on last-minutes log for hang reports.
- Test entry point kept: RecentLogFile.WhenWrittenAsync: the writer is an async channel; the barrier is the only way to know a line reached the file.

### RecoveryLookSweepTests.cs
- Covers: Game/Map/RecoveryLookSweep
- Checks: `look <dir>` per exit in order, each after the last render; a timed-out arm is skipped; cancel.
- Why: move-free position recovery.
- Test entry point kept: RecoveryLookSweep's useTimer constructor switch and FireLookTimeoutForTests: held with the room-tracking PR (#1091), to be replaced once it lands.

### RegenDiagnosticsRecorderTests.cs
- Covers: Game/RegenDiagnosticsRecorder
- Checks: Debug-channel tick lines (amount, position, interval) only with debug on; uneven amounts kept verbatim.
- Why: Issue #11 (measuring Paradigm regen).

### RegenSpellClassifierTests.cs
- Covers: Game/Spells/RegenSpellClassifier, ManaRegenReroller.IsRollSpell
- Checks: which spells are mana-regen roll spells by ability code (Nature Tap and Mana Flux are, a roll among other abilities still is; Chaos Surge, Rapid Healing, a fixed bonus, a damage spell and an empty formula aren't), and ManaRegenReroller.IsRollSpell agrees.
- Why: roll spells are found by code, not name.

### RegenTrackerTests.cs
- Covers: Game/RegenTracker, RegenStat, RegenCycle
- Checks: which cycle claims a gain; artifact grace; countdown cadence per realm (round and mana grid anchoring, rest and meditate from the command); heals and HoT gains left out; Paradigm capture replay; a Paradigm gain while meditating, when the mana pass is due, is the pass (ClaimIfDue).
- Why: report paradigm-20261004-024314, stock-20261004-150847.

### RelogHandlerTests.cs
- Covers: Game/Remote/RelogHandler
- Checks: @relog permission gate; sends the exit command; penalty line asked for; signal armed; dispose unhooks.
- Why: remote relog without a hang-up penalty.

### RelogSignalTests.cs
- Covers: Services/RelogSignal
- Checks: one-shot consume, re-arm.
- Why: Disconnected handler classification.

### RemoteActionPathExpanderTests.cs
- Covers: Game/Map/RemoteActionPathExpander
- Checks: move steps; doors at runtime; cross-room lever detours (anchored at the nearest approach, redundant levers, nested, cyclic, depth cap, one-way lever rooms); ReachesDestination.
- Why: report paradigm-20261003-132757 and lever routes.

### RemoteCommandCatalogTests.cs
- Covers: Game/Remote/RemoteCommandCatalog, PlayerEditDialogViewModel tips
- Checks: command → permission category; removed commands; bang stripping; help coverage; tooltips list commands.
- Why: the permissions grid must match handlers.

### RemoteCommandCheckOrderTests.cs
- Covers: Game/Remote/RemoteCommandManager
- Checks: permission check runs before reasoned replies (suicide, `@do suicide`); suicide matched as an exact word.
- Why: denied senders learn nothing.

### RemoteCommandEmphasisTests.cs
- Covers: Game/Remote/RemoteCommandManager, RemoteCommandCatalog.WithoutEmphasis, Services/Api/LocalApiActions
- Checks: `@stop!` is `@stop` on every path; relay keeps the bang.
- Why: emphatic command forms.

### RemoteCommandHandlerCoverageTests.cs
- Covers: Game/Remote handlers vs RemoteCommandCatalog (IL scan)
- Checks: every catalog command has a registered handler and vice versa.
- Why: catalog/handler drift.

### RemoteCommandHelpTextTests.cs
- Covers: Game/Remote/RemoteCommandCatalog.Help
- Checks: the `@help <command>` wording that goes out on the wire ("Syntax - Description") for the commands whose behaviour it describes; every line fits one telepath.
- Why: @help accuracy is user-visible; the game truncates long telepaths.

### RemoteCommandLocalInvokeTests.cs
- Covers: Game/Remote/RemoteCommandManager.TryInvokeLocal
- Checks: local invocation skips the permission check but honours the master switch and hard-blocks; unknown commands reported; handler failures surfaced; sender attributed to the API.
- Why: local API safety.

### RemoteCommandManagerTests.cs
- Covers: Game/Remote/RemoteCommandManager
- Checks: registration and prefix handlers; permission and party fallbacks (@party, @reset, @health); hard-blocks and the suicide policy; channel scope and reply routing; ignored tokens; Talk settings knobs; self-echo; `&@` relay.
- Why: CLAUDE.md remote-command reply policy.

### RemoteCommandStealthChannelTests.cs
- Covers: Game/Remote/RemoteCommandManager
- Checks: while sneaking, replies, relays and refusals to a say go by telepath.
- Why: a reply said aloud would break the sneak.

### RemoteReplyPolicyTests.cs
- Covers: Game/Remote/RemoteCommandManager plus the real handlers
- Checks: warn-off failures are silent and warn-on answers on the inbound channel (ASCII only); a failure sends nothing to the game; client-read replies always go; empty results are answers; only the engine reads WarnOnDenial (IL scan).
- Why: CLAUDE.md remote-command reply policy.

### RemoteReplyWordingTests.cs
- Covers: Game/Remote/InventoryActionHandler, ProfileSwapHandler, PartyEssentialHandlers
- Checks: sweep holdback wording; roster in a single pair of braces; ASCII swap report; full-party singular wording.
- Why: reply readability.

### RestThresholdsTests.cs
- Covers: Game/Health/RestThresholds
- Checks: anchored to the default-set max, capped at the real and live max; absolute mode; stable across a pre-rest swap.
- Why: reports paradigm-20260902-052036, -20260903-110346, -20260909-095419.

### RollingLogFileTests.cs
- Covers: Services/RollingLogFile
- Checks: last-N cap, snapshot immediate, reopen continues, trims on open, SetMaxLines, Truncate, append after close ignored.
- Why: talk.log and transactions.log persistence.

### RoomAwareMonsterResolverTests.cs
- Covers: Game/Combat/RoomAwareMonsterResolver
- Checks: a shared name resolves to this room's lair member, NPC or summoned minion; null when absent; case and flavor tolerance.
- Why: HP lookups and spell overrides picked the wrong same-named record.

### RoomBlacklistStoreTests.cs
- Covers: Services/RoomBlacklistStore
- Checks: add/remove/replace and Changed firing; load and persist in the realm folder, nothing persisted without one; malformed file; the CannotBeReached flag.
- Why: blacklist and unreachable rooms prune routing.

### RoomBuffStripIndexTests.cs
- Covers: Services/RoomBuffStripIndex
- Checks: RemovesSpell (122) and DispellMagic (73) room spells, including through EndCast chains; only room spells are candidates; clears on set change.
- Why: don't buff in rooms that strip buffs.

### RoomCommandSourceParserTests.cs
- Covers: Game/GameData/RoomCommandSourceParser
- Checks: "Room(cmd) rooms" entries (chance forms, synonyms, ranges, cut cells); entries split.
- Why: Obtained From room-command sources.

### RoomDepartureWatcherTests.cs
- Covers: Game/Combat/RoomDepartureWatcher
- Checks: departure lines (walks out, exits, self-flee) remove the monster and refire; article retry; departed players removed and reported.
- Why: reports paradigm-20260712-220516, -20260713-193205.

### RoomDisplayParserTests.cs
- Covers: Game/Map/RoomDisplayParser
- Checks: room name and exits recovered (compact or verbose; echo, par and prompt boundaries; colour anchor; bank lobby blank line; raw capture); door and gate modifiers.
- Why: report paradigm-20260829-154032; bank-lobby capture of 2026-08-20.

### RoomEntityClassifierTests.cs
- Covers: Game/Combat/RoomEntityClassifier
- Checks: Also-here parse (monster, flavor prefix, player, unknown); Monsters-table fallbacks; list forms; dead and departed removal; room-change wipe versus fresh keep; room-aware Pass 0; dark-room advance; peek suppression.
- Why: reports stock-20260708-000606, paradigm-20260915-130624.

### RoomEntryWatcherTests.cs
- Covers: Game/Combat/RoomEntryWatcher
- Checks: arrival forms (articles, in/into, greet text, directions); sneak notice is a player; colour hints; flavor resolution; colour-scan custom spawns; client notices ignored.
- Why: report paradigm-20260928-230226.

### RoomExitParseTests.cs
- Covers: Game/Map/RoomExit.TryParseWire, FormatLevelGate, CastsOnWalk
- Checks: every exit modifier (door, key, text, item, ticket, toll, hidden, trap, class, race, level and alignment gates, cast).
- Why: the routing data model.

### RoomExitSetTests.cs
- Covers: Game/Map/RoomExitSet
- Checks: the compact set reads as a dictionary (order, every field kept, With, From, Builder reset, invalid directions, re-enumeration).
- Why: memory-compact exits must be lossless.

### RoomFloorItemIndexTests.cs
- Covers: Services/RoomFloorItemIndex
- Checks: roomitem attribution through Called-From chains; Placed column; set swap; warm-up then read; concurrent warm-up.
- Why: report-driven floor items (bogwood box).

### RoomGraphManagerTests.cs
- Covers: Game/Map/RoomGraphManager, RoomKey and RoomExit parse, BfsMapper routing
- Checks: seeding, reloads and eviction; name and candidate indexes; unreachable pruning; door-tolerant re-anchor; Cmd/Item teleport promotion; lever, guard and greet promotions; synthesised teleport and gateway edges; Arena block; cast pockets; string sharing; circular textblocks.
- Why: reports paradigm-20260714-091000, -20260728-180730, -20260729-221044.

### RoomHazardIndexTests.cs
- Covers: Services/RoomHazardIndex (RoomHazard, BuffCounter)
- Checks: Which room-entry spells are protectable hazards, for each protection shape: NegateSpell damage, textblock failitem guarding harm (including MinBase-encoded TBs), and checkspell/failspell buff gates with duration, lapse spell and immunity items. Also EndCast chains, layered requirement groups, survivability, worn-vs-held countering (phoenix feather, sunstone wristband, raft), route-time usability, mandatory items, and lakes that teleport counter holders.
- Why: Reports `paradigm-20260825-125954`, `paradigm-20260810-202239`, `paradigm-20261008-202210`, `paradigm-20260829-203409`, `paradigm-20261009-123349` / `-135049` / `-135314`; user, 2026-10-09/10; GAME_MECHANICS "Room-spell hazard shape 1".

### RoomLightSpellResolverTests.cs
- Covers: Game/Light/RoomLightSpellResolver (with LightItemIndex)
- Checks: The room-light spell's illumination comes from Illu/RoomIllu MinBase, or from the light item a textblock spell gives (TB in MinBase or AbilVal). Unknown, non-light and non-light-item spells give 0.
- Why: Auto-light's estimate of a cast light spell's illumination on both realms.

### RoomRedisplayCoordinatorTests.cs
- Covers: Game/Inventory/RoomRedisplayCoordinator
- Checks: The first request sends; a second within the 750 ms window coalesces; a later one sends again.
- Why: The post-kill drop re-look and the cash re-display would otherwise render the room twice.

### RoomRenameRelearnTests.cs
- Covers: Game/Map/RoomTracker + RoomGraphManager (learned room names)
- Checks: A data-unnamed house renamed by its gang, whole or one room at a time, is walked and every room re-learned. A data-named room is never re-learned. Learning needs a move in flight that predicts the room, an echo, or a follow drag. Re-looks of the room being left, look-peeks, held displays with no echo, and teleporting exits must not name the room ahead.
- Why: Gang houses can be renamed per room, so the stored name is no proof against the walk; this was confirming the tracker in the wrong room.

### RoomSearchRankingTests.cs
- Covers: Services/RoomSearchService.MatchRank
- Checks: A whole word ranks 0, a word prefix 1 and a buried substring 2; multi-token matching is order-independent and takes the worst token.
- Why: "aged" should surface "aged titan" before "Ravaged Farm".

### RoomSearchServiceParseTests.cs
- Covers: Services/RoomSearchService statics
- Checks: Acronym building; coordinate and coordinate-list parsing; token-subset name matching.
- Why: @goto, @loop and @lair resolution and the rail search.

### RoomSpellAttributorTests.cs
- Covers: Game/Map/RoomSpellAttributor.LikelySource
- Checks: The Unrecognized Lines hint names the room's own spell for ambience lines and the named monster's spells (on-hit, between rounds) when the line names one. The longest monster name wins; slots that never fire are ignored; with nothing to go on the hint is blank.
- Why: A real export showed the same proc hint on all 39 lines, which made the column useless.

### RoomSpellCounterWearTests.cs
- Covers: Game/Inventory/RoomSpellCounterWear over the real EquipmentManager and MovementCoordinator
- Checks: A carried negating item goes on next to or in a hazard room, with a backstop hold before an engine step, and comes off afterwards. Item choice (owner order, a free place before an eviction, least armour lost). Refusals, unanswered wears and gate-dropped wears. Location-rule slots, sneaking (empty-room timing, kept sneaks), one-step plan look-ahead, giving the slot back to the right piece, two-place slots, settings and the master switch.
- Why: Lava and freezing-cold rooms burn unless a negator is worn (GAME_MECHANICS room-spell hazards); phoenix feather vs magma amulet.

### RoomSpellDamageClassifierTests.cs
- Covers: Game/Map/RoomSpellDamageClassifier, RoomSpellDamageText, Services/RoomSpellDamageIndex.BarsRestingByDefault
- Checks: Whether a room's own spell damages every tick, on a roll, only under a condition, after a timer, or not at all, through EndCasts, textblocks, roll tables, skill tests and buff checks. Also the settings-tab text for damage range and how, and missing-block gaps.
- Why: "No rest is started in a room whose own spell does damage". User, 2026-10-10 (set damage of chaos storm and drowning); report `paradigm-20260728-201619` (Paradigm desert).

### RoomSpellTeleportClassifierTests.cs
- Covers: Game/Map/RoomSpellTeleportClassifier
- Checks: A room spell's teleport is Sudden (own ability, behind a roll, a skill test or EndCast%), Conditional (item, class, alignment, buff or empty-room gates), None, or Unknown (missing spell or textblock, LinkTo continuation, chain past the cap). Covers dead table bands, loops, and named-area teleports.
- Why: The Navigation map's by-teleport room-spell overlay (red/yellow).

### RoomSpellTeleportIndexTests.cs
- Covers: Services/RoomSpellTeleportIndex
- Checks: Only spells that rooms carry are classed. Spells the data can't answer for are Unknown and logged with the gap. The index starts over on a set change and raises StoreReloaded.
- Why: Feeds the map overlay and route planning per game-data set.

### RoomSummonParserTests.cs
- Covers: Game/Map/RoomSummonParser.Resolve
- Checks: TBInfo d100 summon tables give a summon chance and expected exp per roll. The nomonsters gate is tracked per line; lines needing a room item or summoning a one-at-a-time boss are left out; denominators other than 100 work; a table whose only summon is a boss resolves to null.
- Why: The route exp estimator's room-spell summon credit (crypt summon, graveyard, bone dock, farnholme portal, dinosaur forest); user, 2026-10-10.

### RoomTokensTests.cs
- Covers: Services/RoomTokens.TryNext
- Checks: Finds the same `map/room` references, in the same order, as the `(\d+)/(\d+)` + int.TryParse it replaced, including fuzzed text, non-ASCII digits and overflow, and resumes after each match.
- Why: The parity guard for the regex-free scanner used by every "Summoned By" index.

### RoomTooltipBuilderTests.cs
- Covers: Game/Map/RoomTooltipBuilder
- Checks: Lair tag parsing and max-regen text by realm; the map tooltip's lines (shop, room spell with teleport class, lair/placed/assigned monsters minus unobtainable ones, floor items, light, exits); exit hints (items, traps with disarm odds, doors with pick chance, keys including unresolvable ids, level and race gates, multi-action unlocks with TBInfo and NPC-ask fallbacks); room commands (teleports, prices, summons, casts, effects, deduplication); light summaries; the exit-row label.
- Why: Reports `paradigm-20260911-010954`, `paradigm-20261007-194642`; user, 2026-09-30 (pick chance). The map tooltip and the Room Info panel both build from this.

### RoomTrackerTests.cs
- Covers: Game/Map/RoomTracker
- Checks: The confidence state machine: first observation, predicted moves, subset exits, blocked moves, silent desync, manual locate, graph reload, StateChanged. Also: the Suspect strike ladder; door-tolerant re-anchor; go-path text exits; dark-room and blind-move advance; the echo-claim and peek windows; manual-move detection; reconnect with a move in flight; hydrate, replay and persist of the profile anchor; death capture; fear moves.
- Why: Reports `paradigm-20260921-145800`, `paradigm-20260813-201720`, `paradigm-20260811-122610`, `paradigm-20260811-104042`, `paradigm-20260805-224603`, and the 002347 / 141605 / 002430 / 195229 cases.

### RoombaHouse.cs
- Test infrastructure.
- Covers: test harness for Game/Map/GhSweepManager (used by GhSweep* tests)
- Checks: n/a (simulated gang house).
- Why: drives the Roomba sweep end to end.

### RoombaMasterListViewModelTests.cs
- Covers: ViewModels/CharacterWorkshop/RoombaMasterListViewModel
- Checks: market cross-reference excludes gang-house shops; one row per room; room names; unresolved items; live refresh; export text.
- Why: the Roomba master list.

### RoombaQueryHandlerTests.cs
- Covers: Game/Remote/RoombaQueryHandler
- Checks: consolidated @roomba reply (total, rooms with counts, timestamp, caps, multiple items); grant gate; sync encoding and sentinel; sync channels and refusal wording.
- Why: reports 20260825-172400, -174300, 20260827.

### RoombaSyncReceiverTests.cs
- Covers: Game/Remote/RoombaSyncReceiver
- Checks: lines merge independently inside the request window; channel gate; not adopted unless requested; malformed lines safe; dispose unhooks.
- Why: a sync must survive dropped lines.

### RoundDamageTrackerTests.cs
- Covers: Game/Combat/RoundDamageTracker, RoundTotalsFormatter, DamageLineAttributor
- Checks: Attribution of each damage line to dealer and taker: swings, monster hits, party spells, room spells spread over every foe, shield and spine reflects, weapon procs, caster-eye spell lines, and casterless or unknown damage. Damage capped at monster HP, with both cap choices kept per round. Round boundaries (tick, settle timer, Combat Off, room clearing, fight-round numbering), Reset, the recent-round ring, and the terminal table and line formats.
- Why: Reports `paradigm-20260929-063213`, `paradigm-20261006-051627`, `paradigm-20260929-161536`, `paradigm-20261009-023430`, `paradigm-20260929-003750`, `paradigm-20261009-120757`, `paradigm-20260929-043055`, `paradigm-20260928-231225`, `paradigm-20260928-232024`; user, 2026-09-29 (HP cap).

### RoundTotalsBoardTests.cs
- Covers: Game/Combat/RoundTotalsBoard, RoundTotalsFormatter
- Checks: The Round Totals window's rows follow its own show options and cap choice. Row slots and name width grow at once and shrink only after the remembered rounds age out. Clear resets them. Rows agree with the terminal table.
- Why: A steady Round Totals window size and options independent of the terminal setting.

### RouteBuyAffordabilityTests.cs
- Covers: Game/Map/RouteBuyAffordabilityCalculator
- Checks: Where a route buy's money comes from: purse, configured bank (preferred), another bank, the party's pooled cash, or nobody (with the shortfall).
- Why: Decides whether a gate or hazard-counter buy runs automatically or walks to the shop and pauses.

### RouteChoiceDialogViewModelTests.cs
- Covers: ViewModels/Navigation/RouteChoiceDialogViewModel, RouteChoicePrompt (RouteSubstitutes, RouteACardWalks)
- Checks: - The requirement line's source tail (ask > buy > dropped-by, only for kinds a walk auto-sources) and the select-to-preview / Go / Cancel flow. - Card wording and visibility per fork: send-it, sole hazard (survivable or grave, obtainable or not), teleport, trap-avoid, avoid-override, boss room, blocked, shortcut, and lake crossings. Also ETAs, the calculating-then-Populate state, and teleport notes per card.
- Why: Reports `paradigm-20261007-192215`, `paradigm-20261007-215302`, `paradigm-20260914-201123`, `paradigm-20261008-173911` / `-174023`.

### RouteChoicePlannerTests.cs
- Covers: Services/RouteChoicePlanner, with Game/Map/BfsMapper, Services/MovementFilter, PathItemShopRouter, LoopExpander, AutoWalkManager (lake crossings)
- Checks: - When the free-vs-gated route card is offered (item, ticket, door key, multi-action and hazard gates; the savings floor) and how each requirement is classified. A party short of an item closes the gate, and sole routes have optional shortcuts and closed-gate items. - Blocked-route planning (level gates named before doors), and the teleport, trap-avoid, avoid-override and lever-detour forks with their deferral to an obtainable route. Also which gate items to source. - Crystal Lake: never routed into or across except a place only the lake reaches at level 50+ with a boat; loops and destinations kept out of it.
- Why: Reports `paradigm-20261007-183903`, `paradigm-20260913-100733`, `paradigm-20261008-173911` / `-174023`, `paradigm-20260911-010954`, `paradigm-20260914-201123`, `paradigm-20260825-125954`, `paradigm-20260915-182554`, `paradigm-20260907-212758`, `paradigm-20260911-095404`, `paradigm-20261003-132224`, `paradigm-20261009-123349` / `-135049` / `-135314`; user, 2026-10-09/10.

### RouteDetailsColoringTests.cs
- Covers: ViewModels/Navigation/RouteDetailsDialogViewModel (monster hit-% colouring)
- Checks: Off keeps the alignment tint; on gives green/amber/red/muted bands by Hits-You-%. Toggling and moving a threshold repaint live, and changes persist rounded.
- Why: The route Details window's monster colouring setting, saved per character.

### RouteEtaEstimatorTests.cs
- Covers: Game/Map/RouteEtaEstimator
- Checks: Hop travel time plus a per-monster dwell for each lair stepped into (only with auto-combat), skipping the current room. A missing or zero Max counts as one monster; unresolvable rooms add nothing; lairs that won't be fought add nothing.
- Why: Report `paradigm-20260909-004947` (phantom ETA on routes past friendly guards).

### RouteExpResolverSimLapTests.cs
- Covers: Game/Map/RouteExpResolver.ResolveSimLap, ViewModels/Navigation/ExpEstimatorSessionViewModel
- Checks: A waypoint command's delay is charged in the room it runs in, including the closing leg; a loaded loop keeps its commands for the simulator until it is edited.
- Why: The loop simulator's lap timing.

### RouteExpResolverSummonTests.cs
- Covers: Game/Map/RouteExpResolver (room-spell summon credit), with RoomSummonParser
- Checks: Reads the textblock number from MinBase or the ability slot, and handles a block-less spell. Leaves out bosses and room-item lines but counts GameLimit monsters with no regen wait. The simulator lap carries the same tables, and one log line names the summoning spells and what was left out.
- Why: Exp/hr estimates for summon rooms; user, 2026-10-10 (tyrannosaurs count).

### RouteLightScannerTests.cs
- Covers: Game/Light/RouteLightScanner
- Checks: The darkest room on a route sets the light strength needed after worn illumination; unresolved rooms are skipped; a route already seeable needs none.
- Why: Auto-light decides whether a route needs a light source, and how strong.

### RouteStepListTests.cs
- Covers: ViewModels/Navigation/RouteStepList.Build
- Checks: Numbered "map/room name < command" rows built from walk steps (moves and lever commands), with an acquire row before a gated crossing, announced once. Each row carries the room the command is sent from; unknown names fall back to bare map/room.
- Why: The route picker's "Show steps" list and the Details view's per-room lookup.

### ScratchRoot.cs
- Test infrastructure.
- Covers: test helper (Services/AppPaths data root guard), used by 8 test files
- Checks: Fails a test that would delete folders unless MUDPLAY_DATA_ROOT names a scratch folder that is not, inside, or around the installed data folder (symlinks resolved). Also provides the "Names as paths" collection name and a Holds(path) check. Its guard is tested in ScratchRootTests.
- Why: Keeps tests from deleting the user's real data.

### ScratchRootTests.cs
- Test infrastructure.
- Covers: MudPlay.Tests/ScratchRoot (the scratch-root guard)
- Checks: a scratch root that is a symbolic link to the live data folder, or one inside it, beside it or around it behind a link, is refused; an unrelated folder is accepted.
- Why: the guard is what keeps a test run from deleting the user's live data.

### ScreenClearScrollbackCaptureTests.cs
- Covers: Terminal/TerminalScreen (ClearAll, ClearRowsInclusive, ClearRow)
- Checks: An ED clear captures rows up to the last non-blank one into scrollback; a blank screen adds nothing; a single-row clear (EL) never captures; successive 2J frames all survive.
- Why: BBS banners drawn and cleared with 2J were missing from the backscroll export.

### ScrollbackBufferResizeTests.cs
- Covers: Terminal/ScrollbackBuffer.SetCapacity
- Checks: Growing keeps all rows; shrinking drops the oldest first, including after a wrap-around; appends work after a resize; same value is a no-op; non-positive throws.
- Why: The runtime scrollback setting resizes the live buffer.

### SearchMatchTests.cs
- Covers: Services/SearchMatch
- Checks: Split marks every case-insensitive hit and trims the query; Matches is a trimmed, case-insensitive single phrase; blank or null input never matches.
- Why: Shared by the Help and Settings search boxes.

### SeedDeltaTests.cs
- Covers: Services/SeedDelta (MessageStore.Delta, MonsterMessageStore.Delta), SpellsSectionViewModel.SeedStatesBySpell, MessageEditDialogViewModel
- Checks: Saves store only the user's delta, so seed fixes reach untouched records while user text and flag edits, deletions and additions survive. Shared seed ids are addressed by links. Legacy snapshot migration with backup. The shipped seeds produce an empty delta. Compare and Revert with seed. Spell-tab seed states. A flag-only edit keeps a stale seed id.
- Why: Seed fixes (e.g. arcane assault's text) must keep reaching users' message catalogues.

### SelfAilmentChipResponderTests.cs
- Covers: Game/Conditions/SelfAilmentChipResponder (with ConditionTracker, PartyManager)
- Checks: Our own poison, blindness and disease light the self party chip, per flag, and clear on wear-off. They also work solo (lone self row) and the chip carries over when a party forms.
- Why: The self party-window row never showed our own poison (PartyAilmentTracker only mirrors other members).

### SelfConfusionResponderTests.cs
- Covers: Game/Conditions/SelfConfusionResponder
- Checks: Confusion lights the self chip and asserts ConfusionGate; clearing it releases both. Ignore Confusion suppresses only the gate, and Reevaluate handles a mid-confusion toggle. Records sharing one applied line clear as a group; ClearAll and Dispose release the gate.
- Why: A stuck navigation pause when a second confusion source had its own wear-off line; leader/solo confusion eating @wait.

### SelfFearMovementGateTests.cs
- Covers: Game/Conditions/SelfFearMovementGate
- Checks: FearGate is held from "You are afraid!" until the fear wear-off line.
- Why: Fear runs us through random exits, so our own moves only fight it.

### SelfHeldResponderTests.cs
- Covers: Game/Conditions/SelfHeldResponder
- Checks: A knockdown lights the Held chip and holds HeldGate until "You get back on your feet.". Death (ClearAll) clears both; it works solo; repeated knockdown lines assert the gate once; Dispose releases it.
- Why: Report `paradigm-20260809-114444` (walker stuck "Paused by: Held" after dying while knocked down).

### SellDetourManagerTests.cs
- Covers: Game/Inventory/SellDetourManager (with the real AutoWalkManager, LoopRunner, AutoSellManager)
- Checks: An item over its detour count turns a walk or loop aside to a shop, sells, and resumes; the loop returns to its nearest room. No detour when the walk ends at the shop, the shop is ahead on the lap, or the detour is blocked. Shop retry and refusal memory. Hand-off to a bank run, and selling ahead of a due bank run.
- Why: Reports `paradigm-20260930-182854`, `paradigm-20260929-060520`, `paradigm-20260930-182949`, `paradigm-20261005-214728`.

### SentCommandTrailTests.cs
- Covers: Services/SentCommandTrail, Services/EngineSendGate, Game/SuicidePasswordTracker, Services/BugReportSentCommands
- Checks: Each sent command is recorded once with its origin, sender and outcome (sent, dropped by a hold, held for later). Typed lines are rebuilt key by key. Passwords are never kept: the cursor-row prompt rule (including a custom statline), the suicide-tracker cover, and lines too long to keep. The ring is capped. The bug-report section's formatting censors chat only.
- Why: The bug report's "Commands sent" section must never leak a login or suicide password.

### SentExitCommandTests.cs
- Covers: Services/SentExitCommand
- Checks: `;o` reads as relog and `=x` as stay-down, each only when it is the whole line. It is consumed once, a command too old to have caused the drop is ignored, and the later of two log-offs wins.
- Why: Deciding whether a disconnect was the user's own log-off (relog vs stay down).

### ServerFeed.cs
- Test infrastructure.
- Covers: test helper: a TerminalEmulator + LineExtractor pair fed CP437 bytes (Line, Rows, Prompt, Coloured, Raw)
- Checks: n/a (helper).
- Why: game output reaches a component through the client's own terminal and line extractor, never a test-only feed.

### SessionActivityTrackerTests.cs
- Covers: Game/Combat/SessionActivityTracker, SessionActivityStats
- Checks: Kill, exp, currency, item and sale counters ignore non-positive input. Per-hour rates and cumulative sparkline series end at the headline figure. The rate window is capped at 4 hours. Sneak/disarm percentages, walk latency, and the Reset / ResetTotals / ResetMovement scopes.
- Why: Session Statistics window and status-bar figures; the 4-hour trailing window.

### SessionLogRoundTripTests.cs
- Covers: Services/SessionLogService (FormatChatLine/TryParseChatLine, FormatTxnLine/TryParseTxnLine)
- Checks: Chat lines (speaker or not, colon in the body, a yell's direction, legacy time-only stamps) and transaction lines (location, keep mark) read back the way they were written; garbage is rejected.
- Why: Restoring prior-session chat and transaction history on reconnect.

### SessionResetHandlerTests.cs
- Covers: Game/Remote/SessionResetHandler (via RemoteCommandManager)
- Checks: `@reset` from a sender with AlterSettings zeroes the activity counters, restarts the Time Analysis clock, and replies with an ack. Without permission nothing changes.
- Why: The remote `@reset` command and its AlterSettings permission gate.

### SessionStatsLayoutTests.cs
- Covers: Services/SessionStatsLayoutStore.Resolve
- Checks: A null layout gives the default order with every panel visible. A saved order is honoured; stale or duplicate ids are dropped and missing panels appended; hidden ids flip visibility.
- Why: Mapping a saved (possibly stale) Session Stats layout onto the live panel set.

### SessionStatsLogTests.cs
- Covers: Services/SessionStatsLog, Services/SessionStatsLogFormatter, LogDiagnosticState
- Checks: The log is off by default with no file. When on, it writes one block per interval to a single file, only while in the game, plus one last block on leaving. The interval is clamped. Switching off closes the file, and a failed write stops the log. The block's header and sections match the window.
- Why: Program Log → "Log session statistics".

### SetIndexTimingsTests.cs
- Covers: Services/SetIndexTimings
- Checks: One "Per-set indexes" log line per set load, naming each rebuild once. A load cut short by a throwing rebuild leaves nothing behind for the next load's line.
- Why: Program-log diagnostics of per-set index rebuild time.

### SettingsExitSaveTests.cs
- Covers: ViewModels/Settings/SettingsWindowViewModel.ApplyDirty
- Checks: On exit, changed sections are saved and untouched ones left alone. A changed section with a SaveWarning is held on exit, but applied on an answered OK/Apply.
- Why: CLAUDE.md "Edit windows": client exit saves pending settings without prompting (user, 2026-10-08).
- Test entry point kept: the internal SettingsWindowViewModel(profile, log, sections) constructor: the public one builds every section from AppServices.Current.

### SettingsSearchTests.cs
- Covers: ViewModels/Settings/SettingsWindowViewModel (search)
- Checks: The search highlights matches without hiding sections, as one phrase (trimmed, case-insensitive), over the views' shown text including tooltips. A blank search never reads the views; shown text re-runs a search already typed; the search survives section changes; the count uses Help's wording.
- Why: Settings search has to behave like the Help window's search.
- Test entry point kept: the internal SettingsWindowViewModel(profile, log, sections) constructor: the public one builds every section from AppServices.Current.

### SetupGuideTests.cs
- Covers: ViewModels/Guides/GuideViewModel, GuideCatalogue, GuideProbes
- Checks: Setup-tour steps are built from missing prerequisites, with Prev/Next, finish and dismiss persisted. The demo mode starts at the first missing step and doesn't persist. Action lines tick and move the highlight; Refresh auto-advances; the character step ends when the character is loaded.
- Why: First-run guide; the user, 2026-10-14 (a new player kept playing on the unnamed default profile).

### SharedRealmFilesTests.cs
- Covers: Services/PlayerDatabase, GhItemLocationStore, LeaderboardSnapshotStore, MessageCandidateStore (realm files)
- Checks: Several clients on one realm merge rather than overwrite (players, Roomba sightings, leaderboard captures, unrecognised lines with removals). Legacy duplicate player rows collapse; a realm with no lines file copies its game-data set's file once.
- Why: The last client to save used to throw away the others' records.

### SharedSettingsFilesTests.cs
- Covers: Services/JsonThreeWayMerge, BbsProfileStore, RoomBlacklistStore, EmoteStore
- Checks: The three-way JSON merge applies only local changes. A BBS save keeps another holder's changes, and an unchanged copy doesn't overwrite a newer file. Blacklist entries from two clients merge; each BBS gets its own emote library, seeded once from the shared one.
- Why: Settings windows and multiple clients clobbering each other's saves.

### ShopListParserTests.cs
- Covers: Game/Inventory/ShopListParser
- Checks: Fixed-width `list` rows are parsed from the header's offsets, keeping multi-word names and the price text with its usability tag. Separators and pre-header noise are skipped and a non-numeric quantity rejected. Wide spell-shop prices are read, along with the CantUse/TooPowerful flags.
- Why: Report `paradigm-20261005-091552` (scrolls of 100+ gold dropped).

### ShopPriceCalculatorTests.cs
- Covers: Game/Calculators/ShopPriceCalculator
- Checks: Currency-to-copper conversion, buy price (markup + charm), sell price by realm, trainer fee per level, and friendly denomination formatting.
- Why: Shop price math matching the reference client's figures.

### ShopRoomParserTests.cs
- Covers: Game/GameData/ShopRoomParser.TryParseFirstRoom
- Checks: Takes the first `Room map/room` of an Assigned To field; null, blank, free-text and malformed values are rejected.
- Why: The Shops browser's jump-to-room.

### ShopSpellErrandTests.cs
- Covers: Game/Train/ShopSpellErrand
- Checks: The train trip's shop leg: `sp`, walk, `list`, buy, read, in order. Carried scrolls are read first and a vanished one is skipped. Out-of-stock, refused, CantUse/TooPowerful and truncated names are handled. Buy and read timeouts, unreachable shops and user stops never leave the run standing in a shop.
- Why: User, 2026-10-05 (shop tags). A reply the client doesn't know must not strand the run.

### ShopSpellPlannerTests.cs
- Covers: Game/Train/ShopSpellPlanner
- Checks: Wants every unlearned spell up to the level, minus skipped ones. Gated and one-way shops are left out. Picks the shop that adds the fewest steps or is already on the trip. The budget buys lowest level first and names what it left out; stops go nearest first and visited shops are excluded.
- Why: Which scrolls a train trip buys and where.

### ShopStockIndexTests.cs
- Covers: Services/ShopStockIndex
- Checks: Item → shops that stock it (several shops per item), the empty-slot 0 sentinel skipped, and the index cleared when the set goes null.
- Why: Auto-sell shop choice, sell detours and buy sources.

### ShortcutSourceCoordinatorTests.cs
- Covers: Game/Map/ShortcutSourceCoordinator
- Checks: The route picker's "get the shortcut item" flow: walk to the source, wait for the room to clear, `get`, then a live walk to the destination. Falls back to the long route (hook first) when the item doesn't turn up or the source can't be reached; stands down on a user redirect or Stop.
- Why: Shortcut item not held when the shortcut route is picked.

### SimLevelProjectionTests.cs
- Covers: Game/Simulation/SimLevelProjection
- Checks: HP and mana shift by the class formulas between levels; a Mystic's kai never lands in max mana; an unknown class keeps today's pools.
- Why: The level the live check simulates at.

### SimProcTests.cs
- Covers: Game/Simulation/SimProc
- Checks: A monster hit spell is read at base values with no level; which timed damage spells burn on Stock vs Paradigm.
- Why: The sim's monster hit-spell damage over time, by realm.

### SimRegenBuildTests.cs
- Covers: Game/Simulation/SimCharacterBuilder.BuildRegen, CharacterCalculator.ParadigmHpRegenThird
- Checks: Paradigm standing and rest-tick HP against captured wire values, with and without a regen-bonus ring; Stock's idle and full rest tick; the regen-third rounding.
- Why: Reports `paradigm-20261004-024314`, `paradigm-20261004-140923`, `paradigm-20261004-142359`.

### SimulatorVersionPinTests.cs
- Covers: Game/Simulation/LoopSimulator, RankingCharacter, RankingLoops
- Checks: Golden figures for two fixed simulated runs (melee, spells and lairs; sneak, heal, buff, meditation and a hit spell), plus pinned group hashes and loop signature, all tied to `SimulatorVersion`.
- Why: Forces a `SimulatorVersion` bump whenever simulation output changes, so stale saved rankings are dropped.

### SimulatorViewModelTests.cs
- Covers: ViewModels/Navigation/SimulatorViewModel, Game/Simulation/AreaRankingStore, AreaRankingFiles
- Checks: The sketch route is offered only while it holds rooms. Picking a ranking puts it on the map. Saving a ranked area as a loop (no overwrite, already-saved detection, walkability, a long tour). Rankings are keyed per game-data set and character, survive renames and set switches, are dropped when a table changes or the set's data is replaced, and are read back from disk, marked when the character, level or loops changed since. Bug-report snapshot fields.
- Why: Area rankings must belong to the game data and character they were made on.

### SingleWriterInvariantTests.cs
- Covers: Services/OwnerAttribute fields across the MudPlay assembly (Mono.Cecil IL scan)
- Checks: No type other than a field's declaring type or its declared owner emits `stfld` to an `[Owner]` field.
- Why: CLAUDE.md single-writer invariant.

### SneakGuardTests.cs
- Covers: Game/Stealth/SneakGuard, SneakBreakingCommands, Game/Inventory/EquipmentManager (held gear)
- Checks: The hold kinds (until backstab, until clear room, move in flight, until rested by shadow rest) and when none applies. Held commands queue and release, and are kept while the master switch is off. Which commands end a sneak or may wait. A held gear swap re-runs on release.
- Why: User, 2026-09-28/30; reports `paradigm-20260927-121050`, `paradigm-20260930-193005`.

### SoldiersQuartersGridLostRepro.cs
- Covers: Game/Map/RoomTracker (on the real lostwaysrealm2 Soldier's Quarters grid)
- Checks: The echo gate stops an un-echoed re-display from phantom-advancing, with a timing fallback when there is no echo statline. A move dropped by the rate limiter is un-counted. The passive footprint re-localiser converges with no engine, stands down with one, and won't guess when the walk stays ambiguous.
- Why: Issue #478 (LostWays Lockers going Lost).

### SoundCueTests.cs
- Covers: Game/Sounds/SoundCueEngine, SoundCues, SoundTones, SoundBossWatcher
- Checks: Cues start off; volume is master × cue; burst throttling; settings cached until invalidated; kill and loop milestones; named sounds; preview; the catalogue's ids and default tones; WAV rendering. Boss window and ready cues once per kill, the Stock 87.5% window, stale moments silent, and silence when not in the game.
- Why: The sound-cue feature's settings and boss-timer alerts.

### SpecialExitDispatchTests.cs
- Covers: Game/Map/SpecialExitDispatch.TrySendSynchronous
- Checks: A party-splitting teleport exit relays `.@party <kw>` and asks for a re-form; a spell that moves the whole party does neither.
- Why: Leaders crossing teleports with followers.

### SpellAilmentIndexTests.cs
- Covers: Game/GameData/SpellAilmentIndex (AilmentTokens)
- Checks: Maps each ailment ability code to its token (poison/confuse/blind/hold); multi-code spells yield every token; non-ailment codes yield none.
- Why: Guards the spell→ailment mapping that feeds ailment detection and cure selection.

### SpellAttackTypeIndexTests.cs
- Covers: Game/Combat/SpellAttackTypeIndex
- Checks: Looks up a cast code's AttType case-insensitively. Fails open to -1 for unknown codes, blank codes, or a missing AttType column.
- Why: The resist guard (CombatSpellGates) reads it; a wrong element would skip or waste attack spells.

### SpellBookCategoryClassifierTests.cs
- Covers: Game/Spells/SpellBookCategoryClassifier
- Checks: The All / Heals (Heal or CurePoison code) / Buffs / Attacks (EnergyCost > 0) / PartyOrAoe (by Targets scope) tab membership. A whole-party buff matches both Buffs and PartyOrAoe.
- Why: Spell Book category tabs; real Paradigm spell shapes (god's wrath, cure poison, chant).

### SpellBookRowViewModelTests.cs
- Covers: ViewModels/SpellBookRowViewModel (via Game/Spells/SpellEffectFormatter, SpellCastChance)
- Checks: The effect column's text: damage/heal ranges, durations, stat affects, RemovesSpell / DispellMagic / NegateAbility / TextBlock rendering, folded textblock casts, and hidden message slots. Also the formula tooltip and the success %/tooltip, including the cap.
- Why: Spell Book row display parity with the game data. Guards against bogus ranges on flag abilities (NonMagicalSpell) and against doubled durations.

### SpellBookViewModelTests.cs
- Covers: ViewModels/SpellBookViewModel, ViewModels/SpellBookItemRowViewModel, Game/Spells/SpellbookState (cast items, teach levels)
- Checks: Row filtering (alignment boxes, level gate / Show all, search, obtained-only, category tabs) and the header and status text. The cast-on-use item section (class-specific only, equippable or carried-retained, procs excluded, level ordering, charges, mana, inline effect). Deck-of-cards draw outcomes and redraw, teaching item/NPC lookup, trainer-gated unlock level.
- Why: Reports `paradigm-20261003-215442` and `paradigm-20261002-114043` (deck of cards); the Stock vs Paradigm redraw difference.

### SpellCalculatorTests.cs
- Covers: Game/Spells/SpellCalculator
- Checks: Base/slope with Fix truncation, level clamps (Cap/ReqLevel), the per-round energy multiplier and its 143 gate, flat overrides, heal/damage/drain slot gating, durations, mana cost, chained EndCast (per-round and single-cast, loop guard), and the spell-damage bonus by realm.
- Why: Every damage/heal/duration figure in the sim, the Spell Book and the buff timers. Report `paradigm-20260816-222917` (the wall-clock round).

### SpellCastChanceTests.cs
- Covers: Game/Spells/SpellCastChance
- Checks: clamp(Spellcasting + Diff), with a cap of 98 on Stock and 100 for Paradigm/Kai. Diff >= 200 always succeeds; Spellcasting <= 0 gives null.
- Why: The cast-success % shown in the Spell Book and used by the sim.

### SpellCasterIndexTests.cs
- Covers: Game/GameData/SpellCasterIndex (with TBInfoStore)
- Checks: Who can cast a spell. Monsters by slot, rooms by their own spell, and EndCast chains. Player-learnable hostile spells count as PlayersAtFoes. Any caster not in view (items, textblocks, exits, traps, enchantments, death spells, truncated lists) keeps the spell open. Also: rebuilding on set reimport/switch, log/bug-report Describe text, and a build fault logged at Warn.
- Why: Report `paradigm-20261011-165726` (freezing family). It feeds CastReach, which decides whether a room spell/ailment could have been cast here.

### SpellDamageCalculatorTests.cs
- Covers: Game/Spells/SpellDamageCalculator
- Checks: The scaled range, the code-17 MR partial cut and low-MR amplification, elemental cuts and vulnerabilities, the spell-damage bonus by code and realm, and Stock's step order. Player-side MR %, elemental resist by realm, and full-resist chance out of 99 on Stock.
- Why: The realm damage/resist math behind matchup, sim and Monster Intel figures.

### SpellEffectFormatterTests.cs
- Covers: Game/Spells/SpellEffectFormatter
- Checks: Inline EndCast expansion (the % prefix, nested chains, the cycle guard, the #N fallback), summon monster names, and the NonMagicalSpell friendly phrase.
- Why: A weapon's chained on-hit spells (Nexus Spear) were dropped from effect text.

### SpellEffectTreeDecoderTests.cs
- Covers: ViewModels/GameData/Edit/SpellInfoRowsBuilder.BuildEffectTree
- Checks: Room-spell textblock trees: conditional branches with tone, weighted outcomes, a collapsed teleport sweep with links, summons collapsed ×N, a mid-sequence `nomonsters` gate scoping, and an empty result for no textblock.
- Why: The game-data spell record's effect tree (sea-crossing shape).

### SpellGrowthFormatterTests.cs
- Covers: Game/Spells/SpellGrowthFormatter
- Checks: Magnitude range at the display level, growth formula strings (GCD reduction, fractions, negatives, flat IncLVLs=0), magnitude labels (including the NonMagical rewrite), display level, and duration seconds.
- Why: The growth rows on the Game Data spell record.

### SpellInfoRowsBuilderTests.cs
- Covers: ViewModels/GameData/Edit/SpellInfoRowsBuilder.Build
- Checks: The "Negated by" and "Cast By" link rows, friendly labels, DR shown to the tenth, scaling affect ranges, the collapsed Removes row, dropped message abilities, the energy-cost fire rate, and a NonMagical flag never shown as a range.
- Why: The Game Data spell record rows.

### SpellListParserTests.cs
- Covers: Game/Spells/SpellListParser
- Checks: How `spells`/`pow` blocks commit the obtained set: header variants (padded, Kai), block end on prompt, blank line or non-row; a header with no rows never wipes the set; "You have no spells." clears it; snapshot replace; noise before the header ignored; multi-word names. Also IsSpellListLine recognition.
- Why: A report that `sp` didn't update the book because of the padded column header; the learned-spell list feeding casting.

### SpellReqLevelIndexTests.cs
- Covers: Game/Combat/SpellReqLevelIndex
- Checks: Cast code → ReqLevel lookup, case-insensitive; a missing column gives 0; unknown fails open to -1; on a duplicate cast code the learnable row wins regardless of order.
- Why: Report `paradigm-20260819-195419` (blood skeleton wrongly read as Unkillable via SpellImmu).

### SpellShortIndexTests.cs
- Covers: Game/Combat/SpellShortIndex
- Checks: Spells.Number ↔ Short cast-code in both directions; case/trim; unknown, blank or non-positive → null; shared cast-codes round-trip to the same code
- Why: per-monster override spell slots and the Monster editor's Override Attack (report paradigm-20260813-070249)

### SpellTargetTypeIndexTests.cs
- Covers: Game/Combat/SpellTargetTypeIndex
- Checks: cast-code → UndeadOnly / AnimalsOnly / LivingOnly from abilities 23/80/108 in any Abil slot; case-insensitive; unknown or blank → Any (fails open)
- Why: the attack-spell cascade skips spells the target's type makes useless

### SpellTextBlockTests.cs
- Covers: Game/Spells/SpellTextBlock
- Checks: the TBInfo number comes from the ability value, then MinBase/MaxBase; the TextBlock slot is found in any position; damage bases aren't read as a block
- Why: route exp resolution (RouteExpResolver) reading spells that run textblocks

### SpellbookStateTests.cs
- Covers: Game/Spells/SpellbookState, Game/Spells/KnownSpellCatalog
- Checks: The class list ignores the level gate; alignment gating with learned spells surviving drift; word-spell siblings ruled out once one is learned. The obtained set is name-backed and survives a set renumber (Reseed) and an early profile load (SeedObtainedNames). Also AvailablePicks with the Learned flag, and Changed events.
- Why: Reports `paradigm-20261003-215442` (alignment lag) and `paradigm-20260820-055007` (learned spells lost on upgrade); the set-swap renumber bug.

### SpellsGameDataTabTests.cs
- Covers: ViewModels/GameData/Edit/MessageEditDialogViewModel (InitialTabIndex); ViewModels/GameData/Tables/SpellsSectionViewModel → ViewModels/GameData/Edit/SpellInfoRowsBuilder
- Checks: which tab a spell dialog opens on (player-castable → User Definitions, room/item/monster spell → Game Data); a spell's Game Data rows: damage rows left to the calculator, textblock item gates ("Avoided by carrying"), summons through random chains, room tokens dropped from Cast By, Cast-in-rooms 20 inline + overflow
- Why: the "silver river" / Blackwood forest spell shapes; keeps the dialog from showing raw TextBlock numbers

### SpellsSettingsTests.cs
- Covers: Models/Profile/SpellsSettings
- Checks: default priority order 1..10 and empty spell slots; bless slots serialise sparsely and keep a slot out of the realm's range.
- Why: CastingDirector's between-round order (user, 2026-10-06)

### StackedPackEntryTests.cs
- Covers: Game/Inventory/InventoryManager + InventorySnapshot (PackCount, PackNames, IsCarriedOrWorn), Game/Inventory/EquipmentManager
- Checks: a pile is one counted entry from an `i` read and from gets; a weapon, off-hand, set piece, backstab armour or location item held as a pile is still equipped; a different item's pile doesn't match
- Why: GAME_MECHANICS stacked pack entries (a piece carried twice read as not held)

### StandInCensor.cs
- Test infrastructure.
- Covers: test helper: a BugReportChatCensor built from made-up stand-in words hashed as the shipped table's are
- Checks: n/a (helper).
- Why: no test spells out a real censored word.

### StandardListContradictionsTests.cs
- Covers: Game/Recovery/StandardListContradictions
- Checks: one contradicting death is logged, the second marks the board; the count belongs to the data set; a legacy count with no set counts for nothing; a failed save is logged once and keeps the count; no board/set → nothing counted
- Why: telling a Stock board with edited rooms from standard data

### StartupOptionsTests.cs
- Covers: Services/StartupOptions
- Checks: --profile parsing (comma list, repeated flag, = form, internal spaces kept, empties dropped); token resolution by BBS/Name or a unique bare name, with error text for a miss or an ambiguous name
- Why: multi-instance CLI launch

### StashBalanceStoreTests.cs
- Covers: Services/StashBalanceStore + Game/Cash/StashLedger
- Checks: stash balances shared per realm across characters and live clients; a profile's carried balances are adopted once; no realm → nothing written; a realm switch reloads
- Why: realm-tier stash tally

### StashLedgerTests.cs
- Covers: Game/Cash/StashLedger
- Checks: hidden/recovered arithmetic with a zero floor; Reconcile writes off a robbed stash or raises a belief; NonEmpty; Snapshot/Hydrate replace and drop non-positive rows
- Why: funding walks plan on this belief; the clamps stop drift

### StashRoomManagerTests.cs
- Covers: Game/Cash/StashRoomManager
- Checks: coin `hide N <coin>` lowest-first, limited by StashCoinCutoff; flagged items/keys hidden (Paradigm counted, Min. to keep honoured, a number in an item's name isn't a count); gates (Auto-get-cash, room match, nothing left on a re-visit); StashExecuted event
- Why: GAME_MECHANICS stash/stacking rules; auto-stash Min. to keep (user, 2026-10-08)

### StashTransferProgressTests.cs
- Covers: Game/Cash/StashTransferRunner.Progress / StashTransferProgress, ViewModels/Navigation/NavHoldChipStrip
- Checks: chip tooltip before the first search (ledger belief) and after a load (left by coin, trips to go, ETA from the timed round); per-load took/left reports; ProgressChanged; the chip refreshes its tooltip text
- Why: Stash Transfer chip tooltip

### StashTransferRunnerTests.cs
- Covers: Game/Cash/StashTransferRunner, Game/GameData/BankCatalog.ByDistance
- Checks: the stash → bank shuttle (search, take, deposit, repeat until empty), keep-on-hand, a loaded purse banked first, coin picked up on the way, pickup held only at the stash, ending when stopped / when nothing can be taken / when the bank refuses, party @get-stash / @deposit-all with timeouts, the outcome event, banks nearest-first
- Why: reports paradigm-20261002-101410, -111650, -114620, paradigm-20261004-224242

### StatBreakpointsTests.cs
- Covers: Game/Calculators/StatBreakpoints
- Checks: every breakpoint column adds back up to its calculator (dodge, accuracy, crit, stealth, damage, HP/mana regen, max HP, carry weight, swing energy); thief/mana columns shown only for the right class/realm
- Why: hand-written formula tables must not drift from the calculators

### StatEffectsTests.cs
- Covers: Game/Calculators/StatEffects (+ CharacterCalculator crit/stealth/spellcasting)
- Checks: realm split on accuracy, stealth rounding, melee damage, crit bonus and cap; delegation to the calculators; CP-tab tooltip contents per stat/class; the advertised "next at" breakpoint really ticks
- Why: CP tab tooltips and Level Projection columns

### StatParserPersistenceTests.cs
- Covers: Game/StatParser (Snapshot / Hydrate / ScreenParsed)
- Checks: snapshot mirrors the parsed stats; ScreenParsed fires once per capture window and never for an empty one; Hydrate restores or resets fields and HasParsed
- Why: last-known stats survive reconnect / profile switch

### StatParserTests.cs
- Covers: Game/StatParser
- Checks: stat-screen field capture; outbound stat/exp/health prefix gates and their windows; always-on lives / exp / CP / train lines; prompt-close and settle-close; chat-line guards; self-arm on the header; IsStatScreenLine; LastCaptureRead* flags
- Why: reports paradigm-20260906-090057, -20260916-081052, -20260924-132158, -20260928-223148, -20260928-231447

### StatScreenMarksTests.cs
- Covers: Game/StatParser (modified-stat marks, effect list)
- Checks: Paradigm red = modified, cyan = not; Stock `*` mark, including right after the colon; effect lines with the countdown cut off; marks taken down mid-screen; an exp line leaves marks alone; marks survive Snapshot/Hydrate
- Why: report paradigm-20260930-160602

### StatlinePromptRegexBuilderTests.cs
- Covers: Services/StatlinePromptRegexBuilder via Services/WirePromptScanner
- Checks: default regex shared for null/full; custom statlines capture HP/mana/position, negative HP, 4-digit values, unlabelled mana, colour codes, %n/%c/%x, chained prompts, negative exp-to-level
- Why: reports paradigm-20260929-122409; GAME_MECHANICS "Custom statline tokens"

### StatlineReconcilerTests.cs
- Covers: Game/StatlineReconciler
- Checks: `set statline` resent after 3 mismatches once in game; cooldown, retry cap, give-up flag and notice; a match syncs/clears; arm/disarm/re-arm; plain-ASCII notice text
- Why: report stock-20260929-111956

### StatlineSyntaxTests.cs
- Covers: Game/StatlineSyntax.MissingEngineFields
- Checks: a custom statline missing %h / %m / %r is named; no-mana classes and an unknown pool; any mana label allowed; numbers placed side by side flagged
- Why: the Statline editor's warnings for fields the engines read

### StatusBarTests.cs
- Covers: Models/Settings/StatusBarSettings, Services/StatusBarItemCatalogue, ViewModels/StatusBar/StatusBarMarquee, ViewModels/Settings/StatusBarEditorViewModel (+ zone/row editors)
- Checks: default layout and that it isn't stored; round trip through global settings; fallback on bad JSON; catalogue ids/groups/tokens; custom-text expansion; marquee frames; editor add/move/remove/row limits/reset/unknown items
- Why: configurable status bar

### StealthManagerTests.cs
- Covers: Game/Stealth/StealthManager
- Checks: sneak/hide state machine from server lines; silent loss on a room change; sneak entries; auto-sneak/auto-hide sends and gates (combat, party, NPC, carried penalty, rest); movement holds (settle, cooldown, cast); followers; manual-play re-sneak in place; hide dwell
- Why: reports stock-20260730-163044, paradigm-20260926-233357, -20260927-003304/-011624/-013820/-014032/-014325, -20260928-165844, -20260929-185544, -20260930-184343, -20261002-004148, -20261004-180252, -20261007-213809

### StrayMonsterRuleTests.cs
- Covers: Game/GameData/StrayMonsterRule
- Checks: a Group-only monster spawning into another NPC's room is out of play; anything else (or unreadable) stays in play; the In Game flag counts with or without a room graph
- Why: Cygani #543 under Aiken's Magic Shoppe

### SuicideHandlerTests.cs
- Covers: Game/Remote/SuicideHandler, with RemoteCommandManager, TelepathPacer and EngineSendGate
- Checks: `@suicide` sends only `suicide`, then the password only when the password prompt is the whole row (statline-aware, never inside chat or an echo). It locks the send gate and holds the pacer until the password is sent; a refusal, timeout, another sent line or dispose releases both. With no stored password it runs `pro` first. Failure replies obey WarnOnDenial. Permission and low-lives blocks apply.
- Why: a password typed in clear onto the statline, or taken by a stray telepath; the CLAUDE.md remote-command reply policy.

### SuicidePasswordTrackerTests.cs
- Covers: Game/SuicidePasswordTracker (with EngineSendGate)
- Checks: `set suicide` arms capture of the new password, including byte-at-a-time input, backspace/DEL and CR/LF endings. It commits on "Password changed" and disarms on the invalid-password variants or "NOT changed". A not-set line or a reroll wipes the stored password. Chat lines don't match. `PasswordEntryLikely` tracks the flow.
- Why: keep the stored password in step with the game's; hold engine sends during password entry.

### SummonOnDeathSettleTests.cs
- Covers: Game/Combat/SummonOnDeathSettle (with MonsterDeathWatcher, RoomEntityClassifier, MovementCoordinator)
- Checks: killing a summoner holds the walker through the kill's own roster removal and other synthetic re-fires. The hold is released by a re-display, an empty display or the window running out. Room-spell kills hold only when a summoner is listed. A hand-fought kill sends no CR.
- Why: report `paradigm-20260729-211336` (the walker dragged the summon along).

### SwapScriptExecutionTests.cs
- Covers: Services/Update/SwapScriptBuilder (the POSIX script, actually executed)
- Checks: the relaunch arguments (`--profile`, `--reconnect`) survive shell quoting. A failed swap restores the install and leaves a failure log next to the executable.
- Why: a broken swap leaves the user without a profile, or without a working client.

### SysRoomStatusParserTests.cs
- Covers: Game/Map/SysRoomStatusParser
- Checks: the parse of a live `sys st` capture (room, items split across wrapped lines, hidden items, stack counts, monsters text). It arms only on outbound `sys st` and its abbreviations. Chat lines and unmodelled lines are ignored; batched blocks; a header right after a prompt; window expiry.
- Why: the parser writes the player's location, so unsolicited text must never relocate them.

### SysStatusProbeTests.cs
- Covers: Game/Map/SysStatusProbe
- Checks: the query resolves to the parsed room. A timeout backs off for a while, then retries. Once the probe has worked, or after a hand-typed `sys st`, a timeout no longer turns it off. A profile reset clears that. Concurrent queries share one probe. The parser's window outlasts the probe's timeout.
- Why: a ~7-hour loss of position recovery from one late reply.

### SysopGodLifeRecoveryTests.cs
- Covers: Game/SysopGodLifeRecovery
- Checks: `sys god <name> add life` goes out on a death only when the power is enabled and a name is known; the name is trimmed; nothing is asked back after an arena death.
- Why: an arena death spends no life (GAME_MECHANICS "Death threshold & consequences").

### SysopGotoManagerTests.cs
- Covers: Game/SysopGotoManager
- Checks: the gate order (combat → break, table membership, level), the exact keyword sent, the forced room display, and the name-matched landing resync. Typed-line interception. The wimpy escape skips the level gate and sends `break` first.
- Why: the risk of jumping mid-fight or below the level a location requires, or of mis-anchoring after the jump.

### SysopGotoRoutePlannerTests.cs
- Covers: Game/Map/SysopGotoRoutePlanner
- Checks: it picks the shortest walk from landing room to goal, applies the router level gate (an unknown level excludes a gated location), and skips landings that are off the graph or are the current room.
- Why: route-shortcut correctness.

### SysopPositionResolverTests.cs
- Covers: Game/Map/SysopPositionResolver (with SysStatusProbe, RoomTracker)
- Checks: `sys st` locates the tracker; going Lost triggers a locate on its own. A timeout, or a room outside the graph, fails without locating. The capability and suppression gates hold. A locate waits behind unconfirmed movement unless it comes from recovery (after death it always waits). Throttling and sharing one in-flight probe.
- Why: keeps sysop-powered location recovery an add-on and stops a resync loop.

### TBInfoActionDecoderTests.cs
- Covers: Game/Map/TBInfoActionDecoder
- Checks: decoding of greet text blocks: synonyms grouped with OR, following LinkTo, the directive table (item/addexp/cast/cost/teleport), random-table percentages, and checkspell recursion with loop detection.
- Why: monster greet info shown in Game Data.

### TBInfoActionResolverTests.cs
- Covers: Game/Map/TBInfoActionResolver
- Checks: room-action keywords (mine/gather) vs remoteaction keywords; command requirements (one price, tiered bribe, fare plus minimum level, minimum level alone); detection of commands that need an empty room.
- Why: room tooltips and refusal reasons.

### TBInfoCastTeleportResolverTests.cs
- Covers: Game/Map/TBInfoCastTeleportResolver
- Checks: cast-teleport commands expand their random landing range; fixed and source-map destinations; non-teleport and unknown spells are skipped; minimum level; the whole-party flag (Targets 13); landings reached through a text block, or null when they can't be predicted.
- Why: map edges for cast teleports.

### TBInfoStoreTests.cs
- Covers: Services/TBInfoStore
- Checks: loading and lookup by number; the NUL action sentinel becomes null; a null set clears the store; enumeration of entries.
- Why: the base for every TBInfo resolver.

### TBInfoTeleportResolverTests.cs
- Covers: Game/Map/TBInfoTeleportResolver
- Checks: a destination resolves to its keyword (first match wins); misses return null; the minimum-level directive; destinations reached through LinkTo, while the keyword resolver ignores them; no loop on a self-link.
- Why: walker teleport edges, and the sea-captain dock.

### TalkSectionViewModelTests.cs
- Covers: Models/Profile/TalkSettings (despite the name)
- Checks: the defaults (permissive, warn-on-denial on, failure message without braces); the broadcast channel is disallowed by default and for profiles saved before the setting existed.
- Why: the CLAUDE.md remote-command reply policy; user decision 2026-10-11 on the broadcast channel.

### TelepathPacerTests.cs
- Covers: Game/Remote/TelepathPacer
- Checks: telepaths are spaced by the gap while other lines pass straight through; refused telepaths are resent in order; giving up after the maximum attempts (one GaveUp event); `LastWrittenTelepath` clearing; acks are anchored and abbreviated names match.
- Why: the server's telepath throttle ("Telepath Not Sent").

### TeleportMazeIndexTests.cs
- Covers: Game/Map/TeleportMazeIndex
- Checks: detection of random-teleport pocket members, the one-way entrance, reshuffle directions, telling twin rooms apart by a 1x2 signature, ignoring deterministic pockets, and the Paradigm asylum lever.
- Why: Warped Asylum navigation (Paradigm lever bug).

### TeleportMazeSolverTests.cs
- Covers: Game/Map/TeleportMazeSolver (with AutoWalkManager, RoomTracker)
- Checks: relocating by a look sweep, then a fast walk with no relocation between steps; opening a closed door before looking, and skipping it if it won't open; reshuffling (preferring a spell whose landings can reach the goal); entering from outside; Paradigm locating by `rm` only, with retries; dead-end goals; user pause, resume and cancel; its own moves are never read as hand-typed; the CanSolve gate.
- Why: the asylum reports (an 80-try loop; walking out of the old-man cell; report 152718).

### TeleportSettingsTests.cs
- Covers: ViewModels/Settings/TeleportsSectionViewModel.ReadOrDefault, Game/Map/TeleportCatalog
- Checks: with no stored entry, automatic walks may use no teleports; stored teleports read back as the same places.
- Why: automatic walks must not take a teleport the user hasn't allowed.

### TelnetClientTimeoutTests.cs
- Covers: Net/TelnetClient.DeadConnectionCapMs
- Checks: the TCP_USER_TIMEOUT calculation (idle time plus a 30 s tail, a 60 s fallback, a clamp to the maximum uint).
- Why: a lost connection must not take the OS default of about 13 minutes to surface.

### TempDeathBurstTests.cs
- Covers: Game/Combat/TempDeathBurst
- Checks: one response per room-spell round for a kind that has a temporary death spell; single-target kills are answered every time; a new round needs the room re-read with that kind still listed.
- Why: user, 2026-10-10 (a room holds one temporary-death monster at a time).

### TempDeathResponseTests.cs
- Covers: Game/Combat/TempDeathResponse
- Checks: whole-word matching of "temp" in spell names; `^M` expands to CR; the stall time is the spell's rounds plus a margin; the default is two CRs.
- Why: report `paradigm-20261007-111929` (Grab All sent too early).

### TerminalColorsEditorViewModelTests.cs
- Covers: ViewModels/Settings/TerminalColorsEditorViewModel
- Checks: standard colours save nothing; picking a scheme shows and saves its name; custom colours start from the shown scheme and can be edited; the user's own colours survive switching schemes; reset and restore.
- Why: colour-blind schemes and keeping the user's custom colours.

### TerminalInputRoutingTests.cs
- Covers: Controls/DialogKeyboardFallthrough.ShouldForwardKey, Services/TerminalInputRouter
- Checks: which keys a focused dialog forwards to the terminal; the router's enabled/registered gates and that it passes back the terminal's own result.
- Why: typing reaches the terminal while modeless windows are focused.

### TerminalPositionedRowTests.cs
- Covers: Terminal/LineExtractor (with TerminalEmulator)
- Checks: rows painted by cursor positioning are emitted without a line feed, in order, with no repeats; erased rows and rows cleared by a screen clear are dropped; an escape sequence cut off by a line break.
- Why: reports `paradigm-20261002-140153` and `paradigm-20261002-140334` (the card-name row was never parsed).

### TestProfiles.cs
- Test infrastructure.
- Covers: test helper: a ProfileService as the client builds one, with its default-profile template and reset marker on unused paths under the data root
- Checks: n/a (helper).
- Why: profiles start the way the client starts them, without tests reading each other's default profile.

### TestRows.cs
- Test infrastructure.
- Covers: test helper (builds GameTable/GameRow from inline JSON through the real reader)
- Checks: n/a (helper, used by about 10 test files)
- Why: hands rows straight to the code under test.

### TestSessionCleanup.cs
- Test infrastructure.
- Covers: test infrastructure (module initializer that sweeps stale `test-*` folders)
- Checks: n/a; it removes test-* folders under the game-data and BBS roots that haven't been touched for an hour.
- Why: crashed test runs leaving folders behind in the install's data directory.

### TickEngineTests.cs
- Covers: Game/TickEngine
- Checks: damage lines start a round, but room heat doesn't (it raises DamageOffTheRound); the timer fallback round; the regen grid (mana sets it, HP only fine-tunes it); the round length is measured from regen passes; the "placed" flag; the combat-tick anchor; dispose.
- Why: reports `paradigm-20260904-214056`, `paradigm-20261007-141844` and `paradigm-20261009-120757`.

### TickTimingLogTests.cs
- Covers: Game/TickTimingLog (with RegenTracker)
- Checks: each gain's size, gap, offset from the last round and offset from the start of a rest; damage outside the round is recorded but is not a round; changes of posture; the heal-window mark; the log keeps only its capacity.
- Why: report `paradigm-20261009-120757`; tick timing in the bug report.

### TimeAnalysisTrackerTests.cs
- Covers: Game/Combat/TimeAnalysisTracker
- Checks: activity split (waiting, moving window, attacking, resting HP or mana); affliction time tracked alongside, separately; the activity buckets sum to time on; counting starts on entering the game, pauses on suspend, and reset works; the Changed event.
- Why: the Time Analysis panel (MegaMUD parity).

### TimeZoneAbbreviationTests.cs
- Covers: Services/TimeZoneAbbreviation
- Checks: MST/MDT follow daylight saving; Arizona is always MST; zones outside the table fall back to the numeric offset.
- Why: correct time-zone labels.

### TnlCountdownTests.cs
- Covers: Game/Calculators/TnlCountdown
- Checks: the countdown runs down through small swings in the estimate, re-anchors on a real change or on reaching zero, and handles null or zero.
- Why: a steady TNL on the status bar, Session Stats and Party window.

### TokenCatalogTests.cs
- Covers: Game/Tokens/TokenCatalog
- Checks: place extraction and normalisation, the uses-remaining parse, matching the look name line, de-duplicating held tokens.
- Why: token routing and charge tracking.

### TokenQueryHandlerTests.cs
- Covers: Game/Remote/TokenQueryHandler (through RemoteCommandManager)
- Checks: a bare `@token` with no charges read is an answer whatever the WarnOnDenial setting; a named token with no count is refused, and silent when WarnOnDenial is off.
- Why: the CLAUDE.md remote-command reply policy.

### TokenRouteCoordinatorTests.cs
- Covers: Game/Tokens/TokenRouteCoordinator
- Checks: solo token use (clear room vs walk overland to a monster-free room, "the Lost City" article); party leader sends `.@party use token` first, retries by room check, and tokens LAST; follower declines; new walk / Cancel abandons; fail-out vs use-anyway when a member is stranded.
- Why: a leader who tokens before the party strands members; a user retarget or Stop must not resume the old route.

### TokenRouteEvaluatorTests.cs
- Covers: Game/Tokens/TokenRouteEvaluator
- Checks: picks the held token saving the most rooms; min-rooms-shorter floor; skips depleted (0) and unreachable tokens and keeps not-yet-looked (null) ones; defers to buy-and-cross when that is as short.
- Why: report `paradigm-20260917-233549` (token teleported away from a shorter raft crossing).

### TokenTeleportReaderTests.cs
- Covers: Game/Tokens/TokenTeleportReader
- Checks: item → CastsSp (43) → spell → TextBlock (148) → TBInfo `minlevel` / `price` / `teleport` chain; "the Lost City" keys as "lost city"; non-token items ignored.
- Why: token destinations, costs and level gates come only from game data.

### TollPurseDoubtTests.cs
- Covers: Services/MovementFilter, Game/Map/MovementRefusalDetector, Game/Map/AutoWalkManager, Game/Inventory/OwedPurseRead, Game/Inventory/PostDeathInventoryRefresh, Game/Recovery/ArenaDeathRooms, Game/DeathDetector, Game/Map/RoomTracker, RouteChoicePlanner
- Checks: after a death, tolls / fares / sailings are closed until the inventory is read again (arena deaths excepted, Paradigm by room, Stock by the colliseum line); a game-refused toll or fare closes the crossing and asks for one `i`; the walk re-plans round it, or stops and names the toll and purse; the owed `i` waits on the master switch / send gate; the walk's failure text.
- Why: report `paradigm-20261010-145529` (walk refused six times at a toll on pre-death coin); user rulings 2026-10-10.

### TooHeavyWaitSignalTests.cs
- Covers: Game/TooHeavyWaitSignal (with Game/PartyRestSync, Game/Map/MovementCoordinator)
- Checks: "too heavy to move" sends `@wait (too heavy to move)`, holds our movement and reads `i`; capacity debuff applied / ended reads `i`; release and `@ok` only on a fresh `i` showing weight under max.
- Why: reports `paradigm-20260926-195517`, `paradigm-20261003-201253`.

### ToolbarRowViewModelTests.cs
- Covers: ViewModels/Settings/ToolbarRowViewModel (in ToolbarSectionViewModel.cs)
- Checks: catalogue actions are toolbar-eligible with icon/label; catalogue-less BuiltInActions (Quit…) are keybind-only with a friendly label; separators and unknown ids carry no action.
- Why: keeps the Shortcuts list from showing "(unknown action)" rows or promoting keybind-only actions onto the toolbar.

### ToolbarSettingsTests.cs
- Covers: Models/Profile/ToolbarSettings, Services/ToolbarDefaults, Services/ToolbarConfig, Services/ToolbarItemCatalogue
- Checks: default layout order and separators, every default button resolves in the catalogue, one-shot actions excluded; ToolbarConfig.ApplyFrom takes the DTO's layout, falls back to defaults and propagates Visible/Position; Show* flags per edge.
- Why: a dead default button or a toolbar on the wrong edge.

### ToolbarSplitButtonTests.cs
- Covers: Services/ToolbarButtonItem (split buttons)
- Checks: a click runs the first option or the saved pick; Choose sets and saves without running; an unknown saved pick falls back to the first; the tooltip names the pick.
- Why: a ▾ mis-pick must not fire a sweep like Drop everything.

### TrainBudgetCalculatorTests.cs
- Covers: Game/Calculators/TrainBudgetCalculator
- Checks: bankable levels (inclusive threshold, cap, no chart/level); fractional banked levels; LevelsToTrain reserve and "do not train above" ceiling; WithinCeiling; FormatBankableLevels rounds down.
- Why: report `paradigm-20260930-203138` (an unreached level shown as banked); the off-by-one the train loop depends on.

### TrainFundingHandoffTests.cs
- Covers: Game/Train/TrainFundingHandoffRule
- Checks: only a Collecting answer leaves the run waiting; Short abandons before setting out and stops short mid-run; Funded proceeds.
- Why: a run parked on a short answer stayed busy until Reset States.

### TrainFundingRouterTests.cs
- Covers: Game/Train/TrainFundingRouter
- Checks: the collection errand: robbed stash re-prices to the bank; stash read first and only the shortfall taken; short stash asks the bank first; tolls and the party's toll share drawn with the fee; keep-on-hand floor; fresh `i` / `bank` before pricing; Auto-Get Cash borrowed and restored; walk failure / stop / cancel.
- Why: reports `paradigm-20261001-222332`, `paradigm-20260930-204041`, `paradigm-20260927-105726`, `paradigm-20260927-105932`; user rulings 2026-09-30, 2026-10-02, 2026-10-10.

### TrainFundingSourceFilterTests.cs
- Covers: Game/Train/TrainFundingSourceFilter
- Checks: "When short on cash" modes (both / bank only / stash only / none) and a picked bank room or stash narrowing the sources; UsesBanks.
- Why: Settings → Auto-Trainer funding choices must limit what a short run draws on.

### TrainFundingTests.cs
- Covers: Game/Train/TrainItineraryPlanner, Game/Train/TrainFundingPlanner, Game/Train/TrainFundingForecast, Game/Calculators/TrainBudgetCalculator.ShouldFire
- Checks: trainer chaining at band ceilings and the chain re-target guards; funding plans (stash before bank, bank nearest the trainer, combining, shortfall, speculative stash coin, leg cap); fire threshold vs reserve; shortfall forecast and retry back-off.
- Why: walking to a trainer with an empty purse; re-pricing the run on every exp gain.

### TrainLearnParserTests.cs
- Covers: Game/Spells/TrainLearnParser
- Checks: "You learn the following Kai abilities:" marks each listed power obtained, additively; the block closes on a prompt or a blank line.
- Why: powers learned from `train` must show as obtained without a full `pow` snapshot.

### TrainLinePatternTests.cs
- Covers: Services/Patterns/KnownPatterns (train lines) via MessageRouter
- Checks: stock "attain level N" captures the level; Paradigm "train to the next level!" matches; the two wordings don't cross; "progressed too far", "money required" and "stats are unnaturally altered" refusals match.
- Why: the auto-train coordinator and cash tracker key off these exact engine lines.

### TrainRefusalGateTests.cs
- Covers: Game/Train/TrainRefusalGate
- Checks: one funding recovery per run on "no money"; a second refusal gives up; Reset restores it; nothing to recover with doesn't spend it; RecoveryClaimed.
- Why: the walk-to-trainer-and-back loop on repeated refusals.

### TrainStatsScreenTests.cs
- Covers: Game/Train/TrainStatsScreen, Game/Train/CpApplyCheck
- Checks: reading the six stats and CP Left off the cursor-painted form (not a half-drawn one); applied / partial / nothing-spent judged from the form, or from CP via `stat`.
- Why: report `paradigm-20260930-160602` (form took no keystrokes yet the row was counted applied).

### TrainerCatalogSelectTests.cs
- Covers: Game/GameData/TrainerCatalog (TrainerShop)
- Checks: SelectNearest by level band / class / disabled / reachability / distance, cheaper markup on a tie; SelectNearestForStats ignores the band; CheapestMarkup; RankCandidates ordering and DescribeCandidates log text.
- Why: report `paradigm-20260927-030929` (trainer choice); walking to a trainer that can't train the level.

### TrainerMenuTrackerTests.cs
- Covers: Game/TrainerMenuTracker
- Checks: marker entry gated on an outbound `train`/`train stats` within the window (char-creation row bypasses it); input-menu arm on `train stats`, echo-prompt skip, exit on prompt / room display / live prompt; MenuOwnsKeyboard without a marker; ObserveScreen creation box; roster snapshot; ForceExit.
- Why: report `paradigm-20260906-090057` (keyboard stuck in char mode); report `paradigm-20260815-072308` (ForceExit); a `par` typed into the Family Name field.

### TrainerScreenGateTests.cs
- Covers: Game/TrainerScreenGate, Services/EngineSendGate
- Checks: the engine send gate holds while the trainer / creation form owns the keyboard (with or without the marker), wrapped engine sends no-op then resume, holds compose with others, Dispose releases.
- Why: background sends leaking into the `train stats` form.

### TransactionHistoryTrackerTests.cs
- Covers: Game/Cash/TransactionHistoryTracker
- Checks: bank / stash / sale / purchase row text; one coin row per stash room with Last / Avg / Total and worth; withdrawals; one sale row per shop visit; keep marks; Hydrate folds older logs without re-firing EntryAdded; cap eviction; Changed signal.
- Why: the Transaction History window and the saved session log must agree, and must not double rows on reconnect.

### TranscriptSnapshotTests.cs
- Covers: Terminal/TranscriptSnapshot, Terminal/TerminalScreen (row stamps, soft-wrap)
- Checks: scrollback-then-live ordering with per-row write times; blank interior rows unstamped; a scrolled-off row keeps its write time; stamps shift with ScrollUp; maxLines cap; soft-wrap flags carried and cleared on overwrite; capture window `only`.
- Why: a bug-report / death-log transcript that ran backward at the scrollback/live boundary.

### TransferChannelsTests.cs
- Covers: Game/Remote/TransferChannels, Game/Remote/RemoteCommandManager (broadcast channel), Game/Remote/BroadcastAnswerBudget, Game/ChatRouter (own-request attribution)
- Checks: - Which chat channels each transfer (loop send, roomba sync, timer sync) may use, and the refusal text. - Telling the user's own request apart from one the client relayed. - Broadcast-channel gates: box, master switch, grants, silent blocks, relay-back, sneak routing, and the per-minute answer budget with its bug-report counts.
- Why: no client-to-client data taken from a request the user didn't send; the broadcast channel is public, so it must never flood.

### TrapDelegationManagerTests.cs
- Covers: Game/TrapDelegationManager
- Checks: - Whether a party member can disarm, from class first and race second, never counting self. - The race `look` when a member joins, skipped while a reform settles. - The `.@trap` say, the accept window, and which replies end the wait (say or telepath, multiple disarmers, cancel, dispose).
- Why: a walker blocked at a trap waits on the party; a misread reply would hang or wrongly resume the walk.

### TrapDisarmManagerTests.cs
- Covers: Game/TrapDisarmManager, TrapDisarmOdds
- Checks: - Disarm skill and odds from Traps and gear; the skill gate with class/race inference; direction normalising. - The disarm state machine: success, triggered retry, Stock "failed any", no effect, already disarmed, the re-arm clock. - The queue, StopAll, dispose, and holding the retry through a rest.
- Why: GAME_MECHANICS "Exit traps — search and disarm"; reports 132150, paradigm-20261002-191037, paradigm-20261003-111800.

### TrapHandlerTests.cs
- Covers: Game/Remote/TrapHandler
- Checks: the `@trap` command's permission, skill and channel gates (silent on say), the reply that it's attempting, `@trap stop`, Warn-off silence, and unregistering on dispose.
- Why: the remote-command reply policy, and stopping a room-wide chorus of "can't disarm".

### TrapReplyTests.cs
- Covers: Game/TrapReply
- Checks: each worded reply reads back as its outcome, the acceptance isn't an ending, braces are read through, and anything else is not a trap reply.
- Why: the wording and its reading must not drift apart between the disarming client and the asking one.

### TrialGearFinderTests.cs
- Covers: Game/Inventory/TrialGearFinder, ItemDamageModel (backstab)
- Checks: - Best item per slot, held slots, paired ring/wrist slots, the equip gate. - The weight budget: weapon chosen first, best combination checked against an exhaustive search. - Search order, and the best backstab set on Paradigm vs Stock.
- Why: report 20260827 (Find Best expansion, weight target); user 2026-10-07 (weapon BS-min counts).

### TriggerEngineTests.cs
- Covers: Services/TriggerEngine
- Checks: turning a literal pattern into a regex (wildcards, numbered captures), interpolating variables into the response, capture-and-reply end to end, the master switch skipping triggers, and the wildcard store and its events.
- Why: numbered-wildcard bug; master-switch rule (user, 2026-10-09).

### TriggerOwnershipTests.cs
- Covers: Services/TriggerEngine (profile load/save), Services/ProfileService
- Checks: an older profile takes a one-time copy of the game-data set's legacy triggers; a new character doesn't; edits save on the character.
- Why: triggers moved from the game-data set to the character.

### UnknownEntityFixDialogViewModelTests.cs
- Covers: ViewModels/UnknownEntityFixDialogViewModel
- Checks: the constructor keeps its inputs; each of the three buttons and Cancel raises CloseRequested with the right action.
- Why: the App-level handler branches on the action.

### UnmodifiedStatResolverTests.cs
- Covers: Game/Calculators/UnmodifiedStatResolver, Game/Spells/ListedEffectReader
- Checks: working back trained stats from `stat` marks, gear (Paradigm vs Stock) and listed effects; unexplained or ambiguous cases; matching effect lines to the catalogue.
- Why: a reported screen with "way of the bear"; a CP plan must not be built on buffed stats.

### UnobtainableSectionTests.cs
- Covers: ViewModels/GameData/Tables/UnobtainableSectionViewModel, MonstersSectionViewModel, Game/GameData/InGameFlag
- Checks: "In Game" = 0 rows are listed as Unobtainable and left out of Monsters; each monster sits in exactly one of the two tables; how the flag is parsed.
- Why: the Monsters table must show only what really spawns.

### UpdateNoticeTests.cs
- Covers: Services/Update/UpdateSchedule, ViewModels UpdateTitleMarquee, Models/Settings/GlobalSettings
- Checks: the twice-daily re-check slots; the legacy settings key `CheckForUpdatesOnStartup` still loads and saves; the title-bar crawl's frames.
- Why: an older settings file must not silently switch checking back on.

### UpdateServiceTests.cs
- Covers: Services/Update (ReleaseParser, UpdatePlatform, SwapScriptBuilder, ChangelogExtractor)
- Checks: - Reading the release JSON, the SHA256 file, and comparing versions. - Matching the download to this platform. - The posix/Windows swap scripts: exe swapped by rename, ACL carried across, failure log, rollback. - Cleaning up old exes; changelog notes between versions.
- Why: a broken self-update leaves the user with no working install.

### UseEndsSneakTests.cs
- Covers: Game/Stealth (SneakBreakingCommands, ItemUseStealthRule, ItemUseFactsIndex, StealthManager)
- Checks: - An item command ends sneak/hide only when its spell is cast. - The rules for hiding and leaving a hide; the sneak still under a hide. - Refused `hid` attempts; one re-sneak per item cast or teleport. - Walker and loop-runner order use → sn → move on a hazard step.
- Why: GAME_MECHANICS "What ends a sneak".

### UtilitySkillCalculatorTests.cs
- Covers: Game/Calculators/CharacterCalculator, StatEffects, LevelProjectionCalculator
- Checks: Perception / Thievery / Traps / Picklocks / Tracking formulas and how they round, the level term halving at 16, and projected backstab accuracy by realm and gear.
- Why: a drift of a point or two would silently mis-advise a CP plan.

### VisualGuidesTests.cs
- Covers: ViewModels/Guides (GuideViewModel, GuidePickerViewModel, GuidePageActions, GuideCatalogue)
- Checks: how guide lines tick (action key, probe, Next), saved progress and finishing, the picker's rows, and that every catalogue action key is one the code actually raises.
- Why: a key nothing raises would strand a guide on that step for good.

### WalkReachTests.cs
- Covers: Game/Map/WalkReach, RoomGraphManager.IsExitCommandSomewhere
- Checks: outward reach through every exit type; following room commands only when they stay on the same map; the land of a map and its knots; a 6,400-room case without recursion; exit-command lookup.
- Why: Paradigm Albion arrival rooms (user, 2026-10-10).

### WhereReplyParserTests.cs
- Covers: Game/Remote/WhereReplyParser
- Checks: reads `(map N, room M)` only from a braced reply; loop-send and event replies naming a room are not location replies; malformed input is rejected.
- Why: never mistake prose or a loop/event name for someone's location.

### WhereReplyTrackerTests.cs
- Covers: Game/Remote/WhereReplyTracker
- Checks: a telepathed @where reply raises TargetLocated; an ordinary telepath does not.
- Why: the nav map highlight.

### WhoListParserTests.cs
- Covers: Game/WhoListParser
- Checks: fantasy and technical `who` styles, Paradigm's free-form guilds and ALL-CAPS alignments, EDITED tails, role markers, skipping one bad row, refreshing records.
- Why: report paradigm-20260827-103227 (a FIEND row cut the list short).

### WinchManagerTests.cs
- Covers: Game/Map/WinchManager
- Checks: pull → turn → poll the gate, retrying "does not budge" up to a cap, enemies-present hands the step back, the drawbridge line short-circuits, pull-only mode, holding through a rest, detecting winch exits.
- Why: GAME_MECHANICS "Winch gates".

### WindowLayoutStoreOpenWindowsTests.cs
- Covers: Services/WindowLayoutStore.PlanOpenWindows
- Checks: which windows to open and close to match the saved set; unmanaged windows are left alone.
- Why: a profile reopens exactly the windows it had open.

### WindowLayoutStoreTests.cs
- Covers: Services/WindowLayoutStore.BoundsToPersist
- Checks: which window geometry is saved, across normal, minimized, maximized and collapsed sizes.
- Why: report paradigm-20260827-081318 (minimized windows at -32000).

### WindowSnapManagerTests.cs
- Covers: Services/WindowSnapManager
- Checks: edge-snap search, never snapping a title bar off the working area, flush adjacency, the cluster BFS.
- Why: a panel snapped behind the taskbar.

### WindowTitleTailTests.cs
- Covers: Controls/WindowTitleTail.Compose
- Checks: the window's own title joined with the profile/BBS tail.
- Why: the window-title rule in CLAUDE.md (user, 2026-10-06).

### WireFormatterTests.cs
- Covers: Services/WireFormatter
- Checks: the raw view (^M / ^H markers) vs the stripped view (CR dropped, backspace overstrike collapsed, CSI removed).
- Why: Wire Inspector and bug-report readability.

### WirePromptScannerTests.cs
- Covers: Services/WirePromptScanner
- Checks: - Prompts read straight off the wire: overwritten rows, SGR/CSI, prompts split across reads, negative HP, rest flags. - Prompts quoted in gossip are rejected. - The custom/default shape-mismatch signal; the `[MAJORMUD]:` realm-left prompt; the burst guard. - A disconnect drops the prompt the link cut off.
- Why: reports paradigm-20260824-010304, stock-20260929-111956, paradigm-20261010-145330.

### WireTextTests.cs
- Covers: Services/WireText, RemoteCommandManager reply path
- Checks: non-ASCII is spelled out in ASCII; every reply, @help line and named answer is ASCII.
- Why: a long dash arrived on the wire as `?`.

### WorkshopGroupSectionTests.cs
- Covers: ViewModels/CharacterWorkshop/WorkshopGroupSectionViewModel, LazyWorkshopSection
- Checks: a group tab stands in for its selected sub-tab (Leaf, size, layout changes); a lazy sub-tab is built only on demand and disposed with the group.
- Why: Workshop window sizing and deep links.
