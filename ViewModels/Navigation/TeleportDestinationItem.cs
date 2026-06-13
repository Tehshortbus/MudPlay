using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// One flat "Use Teleport → {room}" context-menu entry for a CMD/teleport
/// room that leads to more than one distinct destination. <see cref="Label"/>
/// is the full menu header (already includes the "Use Teleport → " prefix,
/// destination name, and key); <see cref="Command"/> recenters the map on
/// that destination. Single-destination teleports skip this and use the flat
/// "Use Teleport" command instead.
/// </summary>
public sealed record TeleportDestinationItem(string Label, IRelayCommand Command);
