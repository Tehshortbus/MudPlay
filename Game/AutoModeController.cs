using System.Text.Json;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Master kill-switch for every wired auto-engine. One press flips all
// currently-engaged engines off (after snapshotting which were on); the next
// press restores that snapshot. Shared by the "Auto-All" toolbar button /
// Action-menu item and the `@auto-all` remote command so both drive the exact
// same session-only snapshot.
//
// Operates over every auto-engine that actually has runtime wiring, so a kill /
// restore never strands a flag the runtime can't honour.
//
// The snapshot is per-session and per-character: it is captured on the kill
// press and cleared on profile load (a freshly loaded character must not
// inherit the previous one's remembered state). Profile-load seeding of
// AutoMode from Settings → General is owned elsewhere; this controller never
// applies General defaults — restore always replays the remembered snapshot
// (no snapshot → no-op).
public sealed class AutoModeController
{
    private const string TabKey = "General";
    private const string LogCategory = "AutoMode";

    // The wired engines, in stable order.
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
        (d => d.AutoSearch,   (d, v) => d.AutoSearch   = v),
    };

    private readonly ProfileService _profile;
    private readonly LogService? _log;

    // Remembered on/off state of the wired engines captured by the last kill
    // press. null when nothing has been killed yet this session (so a restore
    // would be a no-op).
    private bool[]? _snapshot;

    // True while the kill switch is actively engaged — engines were
    // snapshotted off by a kill press and not yet restored. Backs
    // KillSwitchEngaged; cleared on restore and on profile load.
    private bool _killEngaged;

    public AutoModeController(ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _log = log;
    }

    // True when every wired engine is currently off.
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

    // True while the Auto-All kill switch is actively engaged — the user (or an
    // `@auto-all off`) pressed it while engines were on and has not restored
    // them yet. Deliberately distinct from AllWiredOff: a character who simply
    // never enabled any auto-engine has everything off WITHOUT the kill switch
    // engaged. Consumers that mean "did the user actively silence automation"
    // (e.g. the main-menu auto-entry suppression) must read this, not
    // AllWiredOff, so a manual-play character still auto-enters the realm.
    public bool KillSwitchEngaged => _killEngaged;

    // Drop the remembered snapshot and clear the kill-switch flag. Called on
    // profile load so a newly loaded character can't restore the previous
    // character's state or inherit its silenced-automation flag.
    public void ResetSnapshot()
    {
        _snapshot = null;
        _killEngaged = false;
    }

    // Toggle the master switch. If any wired engine is on, snapshot the current
    // states and turn them all off. If all are already off, restore the
    // snapshot (no-op when none was captured). Returns the resulting AllWiredOff
    // value (true = everything is now off).
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
            _killEngaged = true;
            WriteGeneral(profile, general);
            _log?.Log(LogSeverity.Info, LogCategory, "Auto-All: every engine off (snapshot saved)");
            return true;
        }

        // Restore: replay the remembered snapshot if we have one.
        if (_snapshot is { } snap)
        {
            for (int i = 0; i < Wired.Length && i < snap.Length; i++)
                Wired[i].Set(am, snap[i]);
            _killEngaged = false;
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
