using System.Text;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Spells;

// Low-level spell-send layer. Builds the <cast-code> [target] wire command (the
// 4-letter cast-code is typed directly — NOT prefixed with the c cast verb), gates
// on a recent-cast cooldown + a "block until next combat tick" latch (set by
// server failure messages), and emits CastSent / CastFailed events so
// CastingDirector can sequence decisions on top.
//
// Three gates compose the "can I cast right now?" check:
//   - Recent-cast cooldown — one cast per combat round (5.5s default, matches
//     MajorMUD's between-round cap). Cleared by OnCombatTick so the very next round
//     can cast immediately. Tracked on TWO independent clocks — _lastAttackSentAt
//     for CombatManager's attack-slot dispatches, _lastBetweenRoundSentAt for
//     CastingDirector's heals/buffs/cures/debuffs — since the two occupy
//     independent slots server-side and neither may block the other (see
//     _lastAttackSentAt's comment for the report this fixed).
//   - Cast-blocked latch — set on server failure lines (fizzle, no-mana,
//     already-cast-this-round, interrupted). Cleared by OnCombatTick OR by the
//     CastBlockExpiry timeout (safety net: out of combat no tick will fire, so we'd
//     be stuck otherwise). Shared across both slots — a genuine server rejection is
//     a real "something's wrong" signal regardless of which slot drew it.
//   - Min recast interval — short sub-cooldown between two consecutive TryCast
//     attempts on the SAME clock (500ms) to absorb burst calls when CastingDirector
//     evaluates multiple candidates in the same frame.
//
// This is a foundation layer — it does NOT decide what to cast; CastingDirector
// picks the spell and target and calls TryCast. A between-round command sent
// outside TryCast (an item-cast buff's use, a user-typed cast) must call
// NotifyExternalCastSent so the cooldown is honoured.
public sealed class CastCoordinator : IDisposable
{
    // LogService category — appears as [Cast] rows per send + failure detection +
    // block-expire.
    public const string LogCategory = "Cast";

    // One cast per combat round (5.5s — slightly longer than the 5s tick to
    // account for server-side rounding). Reset by OnCombatTick.
    public static readonly TimeSpan CastCommandCooldown = TimeSpan.FromMilliseconds(5500);

    // Burst-absorb gap between two TryCast attempts. Keeps a single decision frame
    // from queuing two casts when only the first should land.
    public static readonly TimeSpan MinRecastInterval = TimeSpan.FromMilliseconds(500);

    // How long the cast-blocked latch lives without a combat tick clearing it. Out
    // of combat the tick never fires, so the latch must auto-expire or buffs would
    // never recast. Must match (or exceed) CastCommandCooldown — both gate the same
    // once-per-round cast slot, just from opposite directions (this is "you got
    // rejected, wait"; that one is "don't even try yet"). A shorter value here
    // self-clears the latch before the server's own slot has actually refreshed, so
    // the very next retry gets rejected again — report paradigm-20260831-091839
    // ("WHY DOES IT KEEP BUFFING ITSELF"): a self-buff spammed a reject/retry loop
    // every ~3-5s indefinitely while out of combat because this used to be a bare
    // 3s, racing the confirmed ~5.04s combat tick (GAME_MECHANICS.md) instead of
    // this file's own already-correct 5.5s cooldown for the identical constraint.
    public static readonly TimeSpan CastBlockExpiry = CastCommandCooldown;

    private readonly LogService? _log;
    private readonly IDisposable _fizzleSub;
    private readonly IDisposable _noManaSub;
    private readonly IDisposable _alreadySub;
    private readonly IDisposable _interruptSub;
    private readonly IDisposable _noTargetsSub;

    private Action<byte[]>? _wireSender;
    // Attack-spell dispatches (CombatManager, always bypassRoundCooldown) and
    // between-round dispatches (CastingDirector's heals/buffs/cures/debuffs, never
    // bypassRoundCooldown) occupy independent slots server-side — confirmed
    // GAME_MECHANICS.md: a 0-energy between-round spell "rides between the round's
    // main action, so one is free each round on top of your attack." A single
    // shared clock let one slot's send block the other's: a fresh-engage attack
    // (which legitimately bypasses its own cooldown to fire instantly) stamped the
    // same clock CastingDirector's un-bypassed emergency-heal check reads, so the
    // heal lost its round to an unrelated attack and never got a window to fire —
    // the character died waiting (report paradigm-20260916-113009). Tracked
    // separately per slot so an attack firing can never block a heal, or vice
    // versa. bypassRoundCooldown is always true for attack-slot calls and always
    // false for between-round calls (see class doc), so it doubles as the
    // discriminator between the two clocks.
    private DateTimeOffset _lastAttackSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBetweenRoundSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _castBlockedSince = DateTimeOffset.MinValue;
    private bool _castBlocked;
    // Which slot the most recent send used — true = attack slot (bypassRoundCooldown),
    // false = between-round slot. Lets an "already cast this round" rejection block only the
    // slot it actually applies to (the two are independent server-side).
    private bool _lastCastWasAttackSlot;
    private bool _disposed;

    // Where the two cast clocks stand, for a bug report's "why didn't it cast".
    public string DescribeState()
    {
        static string At(DateTimeOffset t) =>
            t == DateTimeOffset.MinValue ? "never" : t.ToLocalTime().ToString("HH:mm:ss.fff");
        return $"last attack-slot cast {At(_lastAttackSentAt)}, last between-round cast {At(_lastBetweenRoundSentAt)}, "
            + $"last spell sent {_lastSpellSent ?? "(none)"} ({(_lastCastWasAttackSlot ? "attack slot" : "between-round slot")}); "
            + (_castBlocked
                ? $"block latched at {At(_castBlockedSince)} when the game refused a cast (it is lifted at the next cast attempt, so an old time here is a latch nobody has tested since, not a cast still refused)"
                : "no block latched");
    }

    // Fires after a cast command was successfully written to the wire. Carries the
    // literal command line (no trailing CR).
    public event Action<string>? CastSent;

    // Fires whenever a cast attempt was rejected — either by our local gates or by
    // a server failure line. Reason carries the classification; the second arg is a
    // free-text detail (spell name from the fizzle regex, "cooldown" for local
    // gating, etc.); the third is the cast code the rejection applies to — the
    // spell just attempted for a local-gate block, or the last spell actually sent
    // to the wire for a server-line rejection (null if nothing's gone out yet).
    // MUST be checked against a pending cast's own code before reacting: multiple
    // callers (CastingDirector's between-round casts, CombatManager's attack-spell
    // cascade) share this coordinator, and the server's "already cast this round"
    // line never says which spell it's rejecting — treating every rejection as
    // "my pending cast failed" regardless of which cast it actually was drops a
    // landed buff's timer on an unrelated collision (report paradigm-20260824-233439:
    // an attack-spell resume racing the round slot repeatedly killed vlwa's just-armed
    // timer, forcing an immediate spurious recast every few seconds).
    public event Action<CastFailureReason, string, string?>? CastFailed;

    // The cast code most recently written to the wire via TryCast — what a
    // server-line rejection (fizzle / no-mana / already-cast-this-round /
    // interrupted) is presumably about, since those arrive asynchronously with no
    // spell identity of their own. Not updated by NotifyExternalCastSent (callers
    // outside TryCast don't report a spell code); a rejection landing while this is
    // stale from an external send is the same ambiguity the server's own message
    // already carries, not something this coordinator can resolve further.
    private string? _lastSpellSent;
    // Read for the command echoed ahead of a refusal that names no spell.
    private readonly MessageRouter _router;
    private readonly Func<DateTimeOffset> _now;

    public CastCoordinator(MessageRouter router, LogService? log = null, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
        _log = log;
        _now = now ?? (static () => DateTimeOffset.Now);
        _fizzleSub    = router.Subscribe(KnownPatterns.CastFizzled,          OnFizzle);
        _noManaSub    = router.Subscribe(KnownPatterns.CastNoMana,           OnNoMana);
        _alreadySub   = router.Subscribe(KnownPatterns.CastAlreadyThisRound, OnAlreadyThisRound);
        _interruptSub = router.Subscribe(KnownPatterns.CastInterrupted,      OnInterrupted);
        _noTargetsSub = router.Subscribe(KnownPatterns.SpellNoEffectInRoom,  OnNoTargets);
    }

    // Bind the wire sender — typically the TelnetClient.SendAsync wrapper exposed
    // by MainWindowViewModel. Until set, TryCast short-circuits to false.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Drop a stale block latch that no combat tick ever cleared (out of combat the
    // tick never fires, so the latch must self-expire or buffs would never recast).
    private void ExpireStaleBlockLatch()
    {
        if (_castBlocked && _now() - _castBlockedSince >= CastBlockExpiry)
        {
            _castBlocked = false;
            _log?.Debug(LogCategory, "cast-block latch expired (no combat tick received)");
        }
    }

    // True while the failure latch or the recent-cast cooldown would reject a cast.
    // Auto-clears the block latch on read once CastBlockExpiry elapses. Sole caller
    // is CastingDirector's between-round decision, so this reads the between-round
    // clock — an attack dispatch elsewhere never makes this true.
    public bool IsCastBlocked
    {
        get
        {
            ExpireStaleBlockLatch();
            if (_castBlocked) return true;
            return _now() - _lastBetweenRoundSentAt < CastCommandCooldown;
        }
    }

    // Attempt to send <cast-code> [target] (the configured 4-letter cast-code typed
    // directly — not prefixed with c). Returns true only if the command actually
    // went to the wire. Burst-absorbs back-to-back attempts via MinRecastInterval +
    // checks IsCastBlocked. spellName is the configured spell command-name (e.g.
    // "heal", "freedom"); whitespace-only is a no-op false return. target is an
    // optional explicit target — "self", a party member's name, or a monster name;
    // omit for self-cast spells where the server defaults the target to the caster.
    // bypassRoundCooldown skips the once-per-round CastCommandCooldown so a combat
    // engage / resume casts at the monster instantly — mirroring a weapon attack,
    // which has no cooldown gate at all. The failure latch still applies, so it can't
    // retry a fizzle. Per-round heartbeat re-casts leave it false.
    // bypassRecastInterval additionally skips the 500ms MinRecastInterval burst guard.
    // That guard exists to absorb CastingDirector's OWN back-to-back survival re-casts;
    // the combat engine sends legitimate back-to-back attack-slot casts (a pre-attack
    // debuff then the attack, a primary then its alternate after a "no effect") that
    // the 500ms window would otherwise defer to the next tick. The combat dispatch is
    // already paced once-per-round by ResumePacing / the round cooldown, so it can't
    // burst; CastingDirector's casts leave this false.
    public bool TryCast(string spellName, string? target = null,
                        bool bypassRoundCooldown = false, bool bypassRecastInterval = false)
    {
        if (string.IsNullOrWhiteSpace(spellName)) return false;
        if (_wireSender is null) return false;

        // Item-casts (#-prefixed buff-slot tokens) never go through the raw
        // cast path — they need an equip → use → re-equip sequence owned by the
        // item-cast engine. Reject here so a misrouted token can't be typed to
        // the wire as a bogus cast command.
        if (ItemCastToken.IsToken(spellName))
        {
            _log?.Debug(LogCategory, $"ignored item-cast token via TryCast: {spellName.Trim()}");
            CastFailed?.Invoke(CastFailureReason.Blocked, "item-cast-token", spellName.Trim());
            return false;
        }

        DateTimeOffset now = _now();
        ExpireStaleBlockLatch();

        // The failure latch (fizzle / no-mana / interrupt) blocks regardless of the
        // bypass — the last cast didn't take, so an instant retry would just re-fail.
        if (_castBlocked)
        {
            CastFailed?.Invoke(CastFailureReason.Blocked, "cast-blocked", spellName.Trim());
            return false;
        }
        // bypassRoundCooldown is always true for attack-slot calls (CombatManager)
        // and always false for between-round calls (CastingDirector) — see the
        // class doc and _lastAttackSentAt's comment — so it doubles as which
        // independent clock this attempt is paced against.
        DateTimeOffset lastSentAt = bypassRoundCooldown ? _lastAttackSentAt : _lastBetweenRoundSentAt;
        // The once-per-round cooldown gates re-casts; the initial engage bypasses it.
        if (!bypassRoundCooldown && now - lastSentAt < CastCommandCooldown)
        {
            CastFailed?.Invoke(CastFailureReason.Blocked, "cast-blocked", spellName.Trim());
            return false;
        }
        if (!bypassRecastInterval && now - lastSentAt < MinRecastInterval)
        {
            CastFailed?.Invoke(CastFailureReason.Blocked, "recast-interval", spellName.Trim());
            return false;
        }

        // The configured spell value is already MajorMUD's cast-code (the
        // 4-letter abbreviation from game data), which is typed directly to
        // cast — NOT a spell name fed to the "c" (cast) command. Prefixing
        // "c " would make the server look up a spell literally named e.g.
        // "shce". Send the code as-is; append the target when given.
        string spell = spellName.Trim();
        string line = string.IsNullOrWhiteSpace(target)
            ? spell
            : $"{spell} {target.Trim()}";
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
        if (bypassRoundCooldown) _lastAttackSentAt = now; else _lastBetweenRoundSentAt = now;
        _lastCastWasAttackSlot = bypassRoundCooldown;
        _lastSpellSent = spell;
        _log?.Info(LogCategory, $"cast spell={spell} target={target ?? "<self>"}");
        CastSent?.Invoke(line);
        return true;
    }

    // External-cast notification. Engines that issue between-round spell commands
    // outside TryCast (an item-cast buff's equip→use→re-equip sequence, a
    // user-typed `c X` line, etc.) must call this so the between-round cooldown
    // blocks subsequent TryCast attempts for this round.
    public void NotifyExternalCastSent()
    {
        _lastBetweenRoundSentAt = _now();
        _lastCastWasAttackSlot = false;   // external casts are between-round (item-cast buff, `c X`)
        _log?.Debug(LogCategory, "external cast noted — cooldown started");
    }

    // Release the between-round cooldown immediately, bypassing the normal
    // CastCommandCooldown wait and without needing a combat tick. For when a
    // between-round send is discovered to have collided with a condition that
    // applies AFTER the send but within the same round's line burst — a stun /
    // petrify / bind landing a line later than the cast it swallowed. Those
    // ailment lines aren't recognized cast-failure patterns (most of the time
    // nothing is even mid-cast when one lands, so treating every occurrence as a
    // failure would misfire), so the caller (CastingDirector, which knows both
    // when it just sent and what just got flagged AttackPrevented) calls this
    // narrowly instead. Leaving the cooldown stamped would strand the round's one
    // cast slot for the full cooldown on a cast that never actually landed.
    public void ReleaseBetweenRoundCooldown()
    {
        if (_lastBetweenRoundSentAt == DateTimeOffset.MinValue) return;
        _lastBetweenRoundSentAt = DateTimeOffset.MinValue;
        _log?.Debug(LogCategory, "between-round cooldown released — send collided with an unrelated block");
    }

    // Hook the combat-tick boundary. Clears the block latch + resets both
    // recent-cast clocks so the next round can cast immediately on either slot.
    // Subscribe by wiring TickEngine.CombatTickElapsed to this method in
    // AppServices.
    //
    // roundPlaced false is a tick the client couldn't place against the game's
    // rounds (TickEngine.LastCombatTickWasPlaced): only its own timer counting, long
    // after anything was seen. The game gives the between-round cast back at its own
    // round, wherever that is, so such a tick leaves that clock running and the cast
    // waits out CastCommandCooldown, which is longer than any round and so can't put
    // two casts in one (report paradigm-20261007-141844).
    public void OnCombatTick(bool roundPlaced = true)
    {
        if (_castBlocked)
        {
            _castBlocked = false;
            _log?.Debug(LogCategory, "cast-block latch cleared on combat tick");
        }
        _lastAttackSentAt = DateTimeOffset.MinValue;
        if (roundPlaced) _lastBetweenRoundSentAt = DateTimeOffset.MinValue;
    }

    // ----- failure handlers ------------------------------------------

    private void OnFizzle(MatchResult m)
    {
        string spell = m.Groups.Count > 0 ? m.Groups[0] : "<unknown>";
        BlockAndLog(CastFailureReason.Fizzled, $"spell={spell}");
    }

    private void OnNoMana(MatchResult _) =>
        BlockAndLog(CastFailureReason.NotEnoughMana, "insufficient-mana");

    // "You have already cast a spell this round!" is SLOT-SPECIFIC. The attack slot and the
    // between-round slot are independent server-side (confirmed — see the class doc), so a
    // rejected BETWEEN-ROUND cast (e.g. an area-debuff colliding with a self-buff that already
    // took the round's between-round slot) does NOT spend the attack slot. Blocking the shared
    // latch there stranded the round's combat attack behind the debuff's retry — report
    // paradigm-20260923-103506: "ISTO fired before the spell round was up, and it waited until
    // it could retry ISTO before casting HSTO". Block only when the rejected cast was on the
    // ATTACK slot; a between-round rejection still fires CastFailed (so the debuff rolls back and
    // re-offers next round) but leaves the attack slot free to fire this round.
    //
    // The line names no spell either, so it used to be pinned on the cast code sent
    // last. The command echoed just ahead of it says what it really answers: when that
    // is something else (the user typing `use coin bag` two seconds after a flux of
    // ours had landed), the round's slot was spent but no cast of ours was refused, and
    // the failure carries that command in place of our spell so nobody rolls anything
    // back (a mana flux that had rolled 87 was recast into a 16 this way).
    private void OnAlreadyThisRound(MatchResult _)
    {
        if (AnswersAnotherCommand() is { } other)
        {
            _log?.Info(LogCategory,
                $"cast failed reason=AlreadyCastThisRound for '{other}', not our last cast "
                + $"({_lastSpellSent}); the round's slot is spent and nothing of ours is taken back");
            CastFailed?.Invoke(CastFailureReason.AlreadyCastThisRound, "already-cast-this-round-other-command", other);
            return;
        }
        if (_lastCastWasAttackSlot)
        {
            BlockAndLog(CastFailureReason.AlreadyCastThisRound, "already-cast-this-round");
            return;
        }
        _log?.Info(LogCategory,
            $"cast failed reason=AlreadyCastThisRound (between-round slot; attack slot unaffected) "
            + $"spell={_lastSpellSent ?? "<unknown>"}");
        CastFailed?.Invoke(CastFailureReason.AlreadyCastThisRound, "already-cast-this-round-between", _lastSpellSent);
    }

    // The command the refusal being dispatched answers, when one was echoed ahead of it
    // and it is not the cast code we sent last. Null when no echo was read (a statline
    // the extractor can't split) or nothing was sent yet: the refusal then keeps its old
    // reading as ours.
    private string? AnswersAnotherCommand()
    {
        if (_lastSpellSent is not { Length: > 0 } ours) return null;
        if (_router.CommandEchoedBeforeLine?.Trim() is not { Length: > 0 } echoed) return null;
        int space = echoed.IndexOf(' ');
        string word = space < 0 ? echoed : echoed[..space];
        return word.Equals(ours, StringComparison.OrdinalIgnoreCase) ? null : echoed;
    }

    private void OnInterrupted(MatchResult _) =>
        BlockAndLog(CastFailureReason.Interrupted, "concentration-lost");

    // The room-wide refusal comes back before the engine does anything with the
    // cast, and a room attack's own round prints it too when the room has emptied, so
    // it isn't proof a cast slot was spent: report it without latching the block.
    private void OnNoTargets(MatchResult _)
    {
        _log?.Info(LogCategory,
            $"cast failed reason=NoTargets (nothing in the room to act on) spell={_lastSpellSent ?? "<unknown>"}");
        CastFailed?.Invoke(CastFailureReason.NoTargets, "no-targets-in-room", _lastSpellSent);
    }

    private void BlockAndLog(CastFailureReason reason, string detail)
    {
        _castBlocked = true;
        _castBlockedSince = _now();
        _log?.Info(LogCategory, $"cast failed reason={reason} {detail} spell={_lastSpellSent ?? "<unknown>"}");
        CastFailed?.Invoke(reason, detail, _lastSpellSent);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fizzleSub.Dispose();
        _noManaSub.Dispose();
        _alreadySub.Dispose();
        _interruptSub.Dispose();
        _noTargetsSub.Dispose();
    }
}
