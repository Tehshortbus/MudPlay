using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.GameData;

namespace FujinTerm.ViewModels.Settings;

/// <summary>
/// One discovered trainer room in the Settings → Auto-Trainer table: a
/// room+shop label, host map/room, served level range, and the per-row Use?
/// toggle that decides whether auto-train may route to it. Carries the raw
/// <see cref="TrainerShop"/> so the section's level filter can evaluate it.
/// Flipping <see cref="Allowed"/> marks the parent section dirty via the
/// supplied callback.
/// </summary>
public sealed partial class AutoTrainerRowViewModel : ObservableObject
{
    private readonly TrainerShop _shop;
    private readonly Action _onAllowedChanged;

    /// <summary>Stable per-row key (<c>shop/map/room</c>) — the persistence key for the disabled set.</summary>
    public string RowKey => _shop.RowKey;

    /// <summary>
    /// Display label: <c>"&lt;room name&gt; - &lt;shop name&gt;"</c>. Always pairs the
    /// host room with the trainer shop so a multi-room shop's rows read
    /// consistently (e.g. the Druid trainer's "Druid Training Room - Druid
    /// Training Room" and "Druid's Training Room - Druid Training Room" both show
    /// the shop), even when the room and shop happen to share a name. Falls back
    /// to the shop name alone only when the room name didn't resolve.
    /// </summary>
    public string TrainerLabel
    {
        get
        {
            string room = _shop.RoomName.Trim();
            string shop = _shop.Name.Trim();
            return room.Length == 0 ? shop : $"{room} - {shop}";
        }
    }

    /// <summary>Host room as <c>"map/room"</c>, or "—" when the shop has no resolvable room.</summary>
    public string MapRoom { get; }
    /// <summary>Served level range as <c>"min–max"</c>.</summary>
    public string LevelRange { get; }

    [ObservableProperty] private bool _allowed;

    public AutoTrainerRowViewModel(TrainerShop shop, string mapRoom, string levelRange,
                                   bool allowed, Action onAllowedChanged)
    {
        ArgumentNullException.ThrowIfNull(onAllowedChanged);
        _shop = shop;
        MapRoom = mapRoom;
        LevelRange = levelRange;
        _allowed = allowed;
        _onAllowedChanged = onAllowedChanged;
    }

    /// <summary>True when this trainer serves a character at <paramref name="level"/>.</summary>
    public bool ServesLevel(int level) => _shop.ServesLevel(level);

    /// <summary>
    /// True when this trainer serves <paramref name="classNumber"/> — either the
    /// universal Training Room (<c>ClassRest == 0</c>) or the matching class
    /// guild. Used to hide other classes' guild trainers from the list.
    /// </summary>
    public bool ServesClass(int classNumber) => _shop.ServesClass(classNumber);

    partial void OnAllowedChanged(bool value) => _onAllowedChanged();
}
