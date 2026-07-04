using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels.Navigation;

// One flat "Use Teleport → {room}" context-menu entry for a CMD/teleport
// room that leads to more than one distinct destination. Label is the full
// menu header (already includes the "Use Teleport → " prefix, destination
// name, and key); Command recenters the map on that destination.
// Single-destination teleports skip this and use the flat "Use Teleport"
// command instead.
public sealed record TeleportDestinationItem(string Label, IRelayCommand Command);
