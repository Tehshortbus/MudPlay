using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Inventory;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Drives MajorMUD's `train stats` screen to apply the character's saved CP
// plan. On TrainNow it sends `train stats`, waits for the trainer screen to
// come up, replays the Enter-driven keystroke sequence from
// AutoTrainSequenceBuilder with a short delay between strokes, and the form's
// SAVE-default Exit commits on the final Enter. Today it's driven by the CP
// Allocation tab's manual Train Now; the armed loop/auto-lair auto-fire (gated
// by the Settings → Auto-Trainer toggles + trainer allow-list) lands with the
// trainer-navigation engine.
//
// Menu-open detection is realm-independent. Stock realms scroll the trainer's
// "Point Cost Chart" marker as an inline line, so TrainerMenuTracker.MenuEntered
// fires and drives the replay immediately. Paradigm draws the stat box with
// cursor positioning — the marker row never completes until teardown, so
// MenuEntered never fires mid-session. For that path we fall back to the
// command-driven signal TrainerMenuTracker already arms for the terminal's
// character-mode switch: after a short render delay, if the input menu is still
// active (full-screen menu owns the keyboard, no in-game prompt returned) we
// begin the replay; if instead the in-game prompt came back (InputMenuExited —
// we weren't at a guild) we abort cleanly. MenuExited is likewise marker-driven
// and never fires on Paradigm, so InputMenuExited is also our exit signal.
//
// The plan targets are recomputed against live unspent CP + race bounds via
// CpPlanCalculator.ClampRowToBudget, so the engine never tries to overspend,
// and only the current level's planned raises are typed (absolute values —
// self-correcting against the field's starting value). Sessions are id-tagged
// so a late timeout / exit-watchdog from a finished run can't disturb a newer
// one. The manager never touches Family Name / appearance fields, and the
// form's QUIT option means a misfire that bails leaves stats unchanged.
public sealed class AutoTrainManager : IDisposable
{
    private enum Phase { Idle, AwaitingMenu, Replaying }

    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private readonly ProfileService _profile;
    private readonly TrainerMenuTracker _trainer;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    private Phase _phase = Phase.Idle;
    private int _sessionId;
    private int _lastLevel;
    private IReadOnlyList<string> _sequence = Array.Empty<string>();

    // Delay between keystrokes so the server's form redraw keeps pace.
    public int KeystrokeDelayMs { get; } = 200;
    // Grace after `train stats` for the stat box to render before we check the
    // command-driven input-menu signal and begin the replay (the realm-
    // independent fallback for menus whose marker row never scrolls).
    public TimeSpan MenuRenderDelay { get; } = TimeSpan.FromMilliseconds(1200);
    // How long to wait for the trainer screen after sending `train stats`.
    public TimeSpan MenuEntryTimeout { get; } = TimeSpan.FromSeconds(6);
    // Grace after the final keystroke before force-releasing the latch if no
    // exit prompt arrives.
    public TimeSpan ExitGrace { get; } = TimeSpan.FromSeconds(4);

    // Raised when CanTrainNow / IsBusy may have changed.
    public event Action? StateChanged;

    // Raised once the CP keystroke replay has finished sending — the plan's
    // raises and the SAVE that commits them are on the wire. Fires before the
    // menu-exit prompt arrives and before the ExitGrace latch releases, so
    // subscribers that only need "the CP is committed" (e.g. clearing fulfilled
    // plan rows) can react immediately instead of waiting for the server
    // round-trip.
    public event Action? PlanCommitted;

    public AutoTrainManager(PlayerStats stats, GameDataCache gameData, InventoryManager inventory,
                            ProfileService profile, TrainerMenuTracker trainer, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(trainer);
        _stats = stats;
        _gameData = gameData;
        _inventory = inventory;
        _profile = profile;
        _trainer = trainer;
        _log = log;
        _lastLevel = stats.Level;

        _stats.PropertyChanged += OnStatsChanged;
        _trainer.MenuEntered += OnMenuEntered;
        _trainer.MenuExited += OnMenuExited;
        _trainer.InputMenuExited += OnInputMenuExited;
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // True while a train session (our send → exit) is in flight.
    public bool IsBusy => _phase != Phase.Idle;

    // True when the current level has a planned, affordable raise to apply.
    public bool CanTrainNow => TryResolveTargets(out _, out _);

    // Begin a train run for the current level's plan. No-op when already busy, no
    // wire is bound, or there's nothing affordable to raise. Drives the screen
    // asynchronously; subscribe to StateChanged for progress.
    public void TrainNow()
    {
        if (_phase != Phase.Idle || !_wire.IsBound) return;
        if (!TryResolveTargets(out int[] current, out int[] target)) return;

        _sequence = AutoTrainSequenceBuilder.Build(current, target);
        int session = ++_sessionId;
        _phase = Phase.AwaitingMenu;
        _log?.Info("AutoTrain", "Sent `train stats` — awaiting trainer screen.");
        _wire.Send("train stats");
        StateChanged?.Invoke();
        _ = AwaitMenuTimeoutAsync(session);
        _ = AwaitRenderThenReplayAsync(session);
    }

    private async Task AwaitMenuTimeoutAsync(int session)
    {
        await Task.Delay(MenuEntryTimeout);
        if (_sessionId == session && _phase == Phase.AwaitingMenu)
        {
            _phase = Phase.Idle;
            _log?.Info("AutoTrain", "Trainer screen never opened (not at a guild?) — aborted.");
            StateChanged?.Invoke();
        }
    }

    // Realm-independent fallback for menus whose "Point Cost Chart" marker never
    // scrolls (Paradigm's cursor-positioned box), so MenuEntered never fires. If
    // the stock inline marker already drove the replay this is a no-op (phase has
    // left AwaitingMenu); otherwise, once the box has had time to render, the
    // command-driven input-menu flag distinguishes "menu is up" (still active →
    // replay) from "command bounced" (the InputMenuExited handler already aborted).
    private async Task AwaitRenderThenReplayAsync(int session)
    {
        await Task.Delay(MenuRenderDelay);
        if (_sessionId != session || _phase != Phase.AwaitingMenu) return;
        if (!_trainer.IsInputMenuActive) return;   // prompt returned / not armed — let the abort paths handle it
        _log?.Info("AutoTrain", "Trainer screen up (input-menu signal) — replaying plan.");
        StartReplay();
    }

    private void OnMenuEntered()
    {
        if (_phase != Phase.AwaitingMenu) return;   // user opened the trainer themselves — ignore
        StartReplay();
    }

    private void StartReplay()
    {
        _phase = Phase.Replaying;
        StateChanged?.Invoke();
        _ = ReplayAsync(_sessionId);
    }

    // The in-game prompt returned after `train stats`. Realm-independent (it's
    // the command-driven signal, not the marker), so it's both our "not at a
    // guild — abort" signal while awaiting the menu and our menu-exit signal
    // after the replay, on realms where the marker-driven MenuExited never fires.
    private void OnInputMenuExited()
    {
        if (_phase == Phase.AwaitingMenu)
        {
            _phase = Phase.Idle;
            _log?.Info("AutoTrain", "Trainer screen didn't open (not at a guild?) — aborted.");
            StateChanged?.Invoke();
        }
        else if (_phase == Phase.Replaying)
        {
            _phase = Phase.Idle;
            StateChanged?.Invoke();
        }
    }

    private async Task ReplayAsync(int session)
    {
        foreach (string payload in _sequence)
        {
            if (_sessionId != session || _phase != Phase.Replaying) return;
            _wire.Send(payload);
            await Task.Delay(KeystrokeDelayMs);
        }
        _log?.Info("AutoTrain", "Applied plan; saved + exited trainer.");
        // CP raises + SAVE are on the wire — let "plan committed" subscribers
        // (the plan-grid cleanup) react now, ahead of the menu-exit round-trip.
        PlanCommitted?.Invoke();

        // Safety: if the exit prompt never fires, release the latch after a grace.
        await Task.Delay(ExitGrace);
        if (_sessionId == session && _phase == Phase.Replaying)
        {
            _phase = Phase.Idle;
            StateChanged?.Invoke();
        }
    }

    private void OnMenuExited()
    {
        if (_phase == Phase.Idle) return;
        _phase = Phase.Idle;
        StateChanged?.Invoke();
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Track level for the (future) loop/auto-lair auto-fire; refresh
        // CanTrainNow for the CP Allocation tab's manual Train Now.
        if (_stats.Level != _lastLevel) _lastLevel = _stats.Level;
        StateChanged?.Invoke();
    }

    // Resolve the current level's affordable target stats from the saved plan.
    // current/target are length-6 (STR/INT/WIL/AGL/HEA/CHM); false when there's
    // no character, no plan row for this level, or no affordable raise.
    private bool TryResolveTargets(out int[] current, out int[] target)
    {
        current = Array.Empty<int>();
        target = Array.Empty<int>();

        CharacterPlanContext ctx = CharacterPlanContext.Resolve(_stats, _gameData, _inventory);
        if (!ctx.HasCharacter) return false;
        if (_profile.Current?.CharacterPlan is not { } plan) return false;

        CpPlanEntry? row = null;
        foreach (CpPlanEntry e in plan)
            if (e.Level == _stats.Level) { row = e; break; }
        if (row is null) return false;

        int[] prev = ToArray(ctx.Baseline);
        int[] clamped = CpPlanCalculator.ClampRowToBudget(
            prev, ToArray(row), ToArray(ctx.RaceMin), ToArray(ctx.RaceMax), _stats.Cp, ctx.Realm, null, out _);
        if (!AutoTrainSequenceBuilder.HasRaise(prev, clamped)) return false;

        current = prev;
        target = clamped;
        return true;
    }

    private static int[] ToArray(CpPlanEntry e) =>
        new[] { e.Strength, e.Intellect, e.Willpower, e.Agility, e.Health, e.Charm };

    public void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _trainer.MenuEntered -= OnMenuEntered;
        _trainer.MenuExited -= OnMenuExited;
        _trainer.InputMenuExited -= OnInputMenuExited;
    }
}
