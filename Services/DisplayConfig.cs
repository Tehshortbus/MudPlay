using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.Services;

// Live, observable mirror of the BBS-tier display settings (FontSize,
// ScrollbackLines, TerminalCols, TerminalRows). The Settings → BBS section
// writes into this for live-preview; the main window subscribes to
// PropertyChanged and re-applies side effects: font rebind on FontSize,
// scrollback ring resize on ScrollbackLines, emulator screen resize + Telnet
// NAWS re-advertise on TerminalCols / TerminalRows. AppServices re-resolves
// these from the active BBS on ProfileLoaded / ProfileMutated.
public sealed partial class DisplayConfig : ObservableObject
{
    [ObservableProperty] private double _fontSize = 16.0;
    [ObservableProperty] private int _scrollbackLines = 4_000;
    [ObservableProperty] private int _terminalCols = 80;
    [ObservableProperty] private int _terminalRows = 25;
}
