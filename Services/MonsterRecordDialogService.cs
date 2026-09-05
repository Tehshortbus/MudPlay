using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.Services;

// Opens the monster record (edit) dialog by monster Number, modelessly, from anywhere —
// today the Navigation Room Info panel's monster links. Mirrors the browser Monsters tab's
// open flow (MonstersSectionViewModel.OpenEditAsync) but keyed on a Number instead of a
// browser row, and shares the heavy "Other Info" assembly via MonsterMdbInfoBuilder. The
// small orchestration is duplicated on purpose: the browser path is coupled to its
// GameDataRow + Reload, this one resolves name/tier from the Number and has no grid to
// reload. Single-instance: re-opening the shown record is a no-op; another swaps.
public sealed class MonsterRecordDialogService
{
    private readonly GameDataCache _cache;
    private readonly SettingsResolver _resolver;
    private readonly DialogService _dialogs;
    private readonly MonsterOverlaySeedStore _overlaySeed;
    private readonly RoomGraphManager _roomGraph;
    private readonly TBInfoStore _tb;
    private readonly SpellShortIndex _spellShort;

    private MonsterEditDialogViewModel? _openVm;

    public MonsterRecordDialogService(
        GameDataCache cache, SettingsResolver resolver, DialogService dialogs,
        MonsterOverlaySeedStore overlaySeed, RoomGraphManager roomGraph, TBInfoStore tb,
        SpellShortIndex spellShort)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(overlaySeed);
        ArgumentNullException.ThrowIfNull(roomGraph);
        ArgumentNullException.ThrowIfNull(tb);
        ArgumentNullException.ThrowIfNull(spellShort);
        _cache = cache;
        _resolver = resolver;
        _dialogs = dialogs;
        _overlaySeed = overlaySeed;
        _roomGraph = roomGraph;
        _tb = tb;
        _spellShort = spellShort;
    }

    public async Task OpenAsync(int monsterNumber)
    {
        if (monsterNumber <= 0) return;
        string wcc = monsterNumber.ToString(CultureInfo.InvariantCulture);

        // Re-opening the record already showing is a no-op — don't tear down edits.
        if (_openVm is not null && string.Equals(_openVm.WccNoStr, wcc, StringComparison.Ordinal))
            return;

        // The right-pane "Other Info" assembly, and the 4-tier merged overlay over the
        // realm-flavoured Defaults seed + the tier it resolves from (drives the tier picker).
        IReadOnlyList<MdbInfoRow> mdbInfo =
            new MonsterMdbInfoBuilder(_cache, _roomGraph, _tb, _dialogs).Build(wcc);
        MonsterOverlay seedDefaults = _overlaySeed.GetOverlay(monsterNumber);
        MonsterOverlay existing =
            _resolver.ResolveGameData<MonsterOverlay>("Monsters", wcc, seedDefaults) ?? seedDefaults;
        SettingsTier currentTier = _resolver.GetGameDataSourceTier("Monsters", wcc);

        MonsterEditDialogViewModel vm = new(
            wccNoStr:           wcc,
            mdbName:            _cache.FindNameByNumber("Monsters", monsterNumber) ?? string.Empty,
            existing:           existing,
            currentTier:        currentTier,
            mdbInfo:            mdbInfo,
            writableTiers:      _resolver.WritableTiers(),
            installedDefaults:  seedDefaults,
            resolveSpellShort:  _spellShort.NumberByShort,
            resolveSpellNumber: _spellShort.ShortByNumber,
            spellSuggestions:   AppServices.Current.Spellbook.AvailablePicks,
            manaModePercentage: AppServices.Current.CombatSpellManaModeIsPercentage,
            liveMaxMa:          AppServices.Current.PlayerState.MaxMa);

        MonsterEditDialogViewModel? previous = _openVm;
        _openVm = vm;
        previous?.CancelCommand.Execute(null);

        MonsterEditResult? result;
        try
        {
            result = await _dialogs.OpenWindowAsync<MonsterEditDialogViewModel, MonsterEditResult>(vm);
        }
        finally
        {
            if (ReferenceEquals(_openVm, vm)) _openVm = null;
        }
        if (result is null) return;

        // Installed-defaults reset / redundant-override cleanup / normal write —
        // shared with the browser's Monsters tab.
        await GameDataOverrideApplier.ApplyAsync(
            _resolver, AppServices.Current.Confirm, "Monsters", result.WccNoStr,
            result.Tier, result.Overlay, result.EqualsInstalledDefaults);
    }
}
