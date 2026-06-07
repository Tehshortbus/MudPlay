using System.ComponentModel;
using System.Text;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using FujinTerm.Services.Patterns;

namespace FujinTerm.Game.PvP;

/// <summary>
/// Phase 9 PR 9.G — reactive PvP engine. Subscribes to
/// <see cref="KnownPatterns.PlayerHitsYou"/> and
/// <see cref="KnownPatterns.PlayerMissesYou"/>, classifies the
/// attacker, and fires the user's configured
/// <see cref="PvPSettings.DefaultAction"/> (Ignore / Flee / Hangup).
/// </summary>
/// <remarks>
/// <para>
/// v1 scope is intentionally narrow — global default action per
/// character, single-shot flee per encounter, log-and-warn before
/// Hangup. The Attack / Chase actions are reserved for a follow-up
/// that integrates the walker + persistent target selection. Per-
/// player whitelists (sourcing FriendOrFoe from the Phase 5 Players
/// tab) also land later.
/// </para>
/// <para>
/// Single-shot per encounter: once <see cref="Action.Flee"/> fires,
/// we set <see cref="EncounterActive"/> and don't re-issue until
/// <see cref="PlayerState.InCombat"/> flips false (encounter ended).
/// Same gate for Hangup, though in practice the disconnect happens
/// fast enough that the question never comes up.
/// </para>
/// </remarks>
public sealed class PvPManager : IDisposable
{
    /// <summary>LogService category — appears as <c>[PvP]</c> rows per
    /// detection + action emit.</summary>
    public const string LogCategory = "PvP";

    private readonly PlayerState _state;
    private readonly Func<PvPSettings> _readSettings;
    private readonly Func<bool> _isEnabled;
    private readonly LogService? _log;
    private readonly IDisposable _hitsSub;
    private readonly IDisposable _missesSub;

    private Action<byte[]>? _wireSender;
    private bool _encounterActive;
    private string? _lastAttacker;
    private bool _disposed;

    /// <summary>Event payload — carries the attacker name + the
    /// settings-resolved action chosen.</summary>
    public event Action<string, PvPSettings.Action>? PvPDetected;

    /// <summary>True between a detected PvP attack and the next
    /// transition of <see cref="PlayerState.InCombat"/> to false.
    /// Single-shot gate on reactive actions.</summary>
    public bool EncounterActive => _encounterActive;

    /// <summary>Name of the attacker that triggered the active
    /// encounter, or <c>null</c> when no encounter is open.</summary>
    public string? LastAttacker => _lastAttacker;

    public PvPManager(
        MessageRouter router,
        PlayerState state,
        Func<PvPSettings> readSettings,
        Func<bool> isEnabled,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _state = state;
        _readSettings = readSettings;
        _isEnabled = isEnabled;
        _log = log;

        _hitsSub   = router.Subscribe(KnownPatterns.PlayerHitsYou,   OnPlayerAttack);
        _missesSub = router.Subscribe(KnownPatterns.PlayerMissesYou, OnPlayerAttack);
        _state.PropertyChanged += OnStateChanged;
    }

    /// <summary>Bind the wire sender — typically the gate-wrapped
    /// engine pipeline from <c>MainWindowViewModel</c>. Until set,
    /// reactive actions log decisions but don't send commands.</summary>
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    private void OnPlayerAttack(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        string attacker = m.Groups[0].Trim();
        if (attacker.Length == 0) return;

        // Defensive: our own name showing up in a PlayerHitsYou match
        // means the regex over-matched (e.g. an emote we didn't filter).
        // Skip — never self-react.
        // (We don't have a direct read of own name here without an
        // extra constructor dep; defer to the encounter-active gate
        // for v1 — duplicate fires are no-ops once _encounterActive
        // is true.)

        if (!_isEnabled())
        {
            _log?.Debug(LogCategory, $"PvP detected attacker={attacker} — engine off");
            return;
        }

        if (_encounterActive)
        {
            // Same encounter — log but don't re-fire the action.
            return;
        }

        PvPSettings settings = _readSettings();
        _encounterActive = true;
        _lastAttacker = attacker;

        _log?.Info(LogCategory,
            $"PvP detected attacker={attacker} action={settings.DefaultAction}");
        PvPDetected?.Invoke(attacker, settings.DefaultAction);

        switch (settings.DefaultAction)
        {
            case PvPSettings.Action.Ignore:
                // Already logged; nothing else to do.
                break;
            case PvPSettings.Action.Flee:
                SendFlee(settings);
                break;
            case PvPSettings.Action.Hangup:
                SendHangup(settings);
                break;
            case PvPSettings.Action.Attack:
            case PvPSettings.Action.Chase:
                _log?.Warn(LogCategory,
                    $"action={settings.DefaultAction} reserved — unwired in v1");
                break;
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerState.InCombat)) return;
        if (_state.InCombat) return;
        // Combat ended — re-arm.
        if (_encounterActive)
        {
            _log?.Debug(LogCategory,
                $"encounter ended (combat cleared) — re-arming action gate");
        }
        _encounterActive = false;
        _lastAttacker = null;
    }

    private void SendFlee(PvPSettings settings)
    {
        string cmd = string.IsNullOrWhiteSpace(settings.FleeDirection)
            ? "flee"
            : $"run {settings.FleeDirection.Trim()}";
        _log?.Info(LogCategory, $"flee cmd={cmd}");
        Send(cmd);
    }

    private void SendHangup(PvPSettings settings)
    {
        string cmd = string.IsNullOrWhiteSpace(settings.HangupCommand)
            ? "/q"
            : settings.HangupCommand.Trim();
        _log?.Warn(LogCategory,
            $"HANGUP sent cmd='{cmd}' attacker={_lastAttacker} — destructive action, " +
            "configured per-character in Settings -> PvP");
        Send(cmd);
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hitsSub.Dispose();
        _missesSub.Dispose();
        _state.PropertyChanged -= OnStateChanged;
    }
}
