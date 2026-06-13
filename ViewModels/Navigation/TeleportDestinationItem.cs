using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels.Navigation;

/// <summary>
/// One "Use Teleport" submenu entry for a CMD/teleport room that leads
/// to more than one distinct destination. <see cref="Label"/> is the
/// destination room name + key; <see cref="Command"/> recenters the map
/// on that room. Single-destination teleports skip this and use the flat
/// "Use Teleport" command instead.
/// </summary>
public sealed record TeleportDestinationItem(string Label, IRelayCommand Command);
