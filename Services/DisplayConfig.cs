using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.Services;

// Live, observable channel for the terminal's display state. Most fields
// (FontSize, ScrollbackLines, TerminalCols, TerminalRows) mirror the BBS-tier
// settings; ScaleToWindow mirrors the char-tier General toggle. The settings
// sections write into this for live effect; the main window subscribes to
// PropertyChanged and re-applies side effects: font rebind on FontSize,
// scrollback ring resize on ScrollbackLines, emulator screen resize + Telnet
// NAWS re-advertise on TerminalCols / TerminalRows, terminal re-fit on
// ScaleToWindow. AppServices re-resolves these from the active profile / BBS on
// ProfileLoaded / ProfileMutated.
public sealed partial class DisplayConfig : ObservableObject
{
    [ObservableProperty] private double _fontSize = 16.0;
    [ObservableProperty] private int _scrollbackLines = 4_000;
    [ObservableProperty] private int _terminalCols = 80;
    [ObservableProperty] private int _terminalRows = 25;

    // Auto-fit the terminal font to the window (keeping the fixed cell grid).
    // Sourced from the char-tier GeneralSettings.ScaleTerminalToWindow.
    [ObservableProperty] private bool _scaleToWindow;
}
