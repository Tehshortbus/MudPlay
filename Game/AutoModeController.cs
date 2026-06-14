using System.Text.Json;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game;

/// <summary>
/// Master kill-switch for every wired auto-engine. One press flips all
/// currently-engaged engines off (after snapshotting which were on); the
/// next press restores that snapshot. Shared by the "Auto-All" toolbar
/// button / Action-menu item and the <c>@auto-all</c> remote command so
/// both drive the exact same session-only snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Operates over the nine engines that actually have runtime wiring —
/// <see cref="AutoActionDefaults.AutoSearch"/> is intentionally excluded
/// until its engine ships, so a kill / restore never strands a flag the
/// runtime can't honour.
/// </para>
/// <para>
/// The snapshot is per-session and per-character: it is captured on the
/// kill press and cleared on profile load (a freshly loaded character
/// must not inherit the previous one's remembered state). Profile-load
/// seeding of AutoMode from Settings → General is owned elsewhere; this
/// controller never applies General defaults — restore always replays the
/// remembered snapshot (no snapshot → no-op).
/// </para>
/// </remarks>
public sealed class AutoModeController
{
    private const string TabKey = "General";
    private const string LogCategory = "AutoMode";

    /// <summary>
    /// The wired engines, in stable order. AutoSearch is excluded — it has
    /// no engine yet, so the kill-switch must not touch it.
    /// </summary>
    private static readonly (Func<AutoActionDefaults, bool> Get,
                             Action<AutoActionDefaults, bool> Set)[] Wired =
    {
        (d => d.AutoCombat,   (d, v) => d.AutoCombat   = v),
        (d => d.AutoNuke,     (d, v) => d.AutoNuke     = v),
        (d => d.AutoHealRest, (d, v) => d.AutoHealRest = v),
        (d => d.AutoBless,    (d, v) => d.AutoBless    = v),
        (d => d.AutoLight,    (d, v) => d.AutoLight    = v),
        (d => d.AutoGetItems, (d, v) => d.AutoGetItems = v),
        (d => d.AutoGetCash,  (d, v) => d.AutoGetCash  = v),
        (d => d.AutoSneak,    (d, v) => d.AutoSneak    = v),
        (d => d.AutoHide,     (d, v) => d.AutoHide     = v),
    };

    private readonly ProfileService _profile;
    private readonly LogService? _log;

    /// <summary>
    /// Remembered on/off state of the wired engines captured by the last
    /// kill press. <c>null</c> when nothing has been killed yet this
    /// session (so a restore would be a no-op).
    /// </summary>
    private bool[]? _snapshot;

    public AutoModeController(ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _log = log;
    }

    /// <summary>True when every wired engine is currently off.</summary>
    public bool AllWiredOff
    {
        get
        {
            if (_profile.Current is not { } profile) return true;
            AutoActionDefaults am = ReadGeneral(profile).AutoMode;
            foreach ((Func<AutoActionDefaults, bool> get, _) in Wired)
                if (get(am)) return false;
            return true;
        }
    }

    /// <summary>
    /// Drop the remembered snapshot. Called on profile load so a newly
    /// loaded character can't restore the previous character's state.
    /// </summary>
    public void ResetSnapshot() => _snapshot = null;

    /// <summary>
    /// Toggle the master switch. If any wired engine is on, snapshot the
    /// current states and turn them all off. If all are already off,
    /// restore the snapshot (no-op when none was captured). Returns the
    /// resulting <see cref="AllWiredOff"/> value (true = everything is now
    /// off).
    /// </summary>
    public bool ToggleAll()
    {
        if (_profile.Current is not { } profile) return true;

        GeneralSettings general = ReadGeneral(profile);
        AutoActionDefaults am = general.AutoMode;

        bool anyOn = false;
        foreach ((Func<AutoActionDefaults, bool> get, _) in Wired)
            if (get(am)) { anyOn = true; break; }

        if (anyOn)
        {
            // Kill: remember current states, then clear every wired flag.
            _snapshot = new bool[Wired.Length];
            for (int i = 0; i < Wired.Length; i++)
            {
                _snapshot[i] = Wired[i].Get(am);
                Wired[i].Set(am, false);
            }
            WriteGeneral(profile, general);
            _log?.Log(LogSeverity.Info, LogCategory, "Auto-All: every engine off (snapshot saved)");
            return true;
        }

        // Restore: replay the remembered snapshot if we have one.
        if (_snapshot is { } snap)
        {
            for (int i = 0; i < Wired.Length && i < snap.Length; i++)
                Wired[i].Set(am, snap[i]);
            WriteGeneral(profile, general);
            _log?.Log(LogSeverity.Info, LogCategory, "Auto-All: restored snapshot");
        }
        return AllWiredOff;
    }

    private static GeneralSettings ReadGeneral(CharacterProfile profile)
    {
        if (profile.Settings is null) return new GeneralSettings();
        if (!profile.Settings.TryGetValue(TabKey, out JsonElement json))
            return new GeneralSettings();
        try
        {
            return JsonSerializer.Deserialize<GeneralSettings>(json.GetRawText())
                   ?? new GeneralSettings();
        }
        catch
        {
            return new GeneralSettings();
        }
    }

    private void WriteGeneral(CharacterProfile profile, GeneralSettings general)
    {
        profile.Settings ??= new();
        profile.Settings[TabKey] = JsonSerializer.SerializeToElement(general);
        _profile.Save();
    }
}
