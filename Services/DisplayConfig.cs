using CommunityToolkit.Mvvm.ComponentModel;

namespace FujinTerm.Services;

// Live, observable channel for the terminal's display state. ScrollbackLines,
// TerminalCols and TerminalRows mirror the BBS-tier settings; FontFamily,
// FontSize and ScaleToWindow mirror the char-tier General settings. The
// settings sections write into this for live effect; the main window subscribes
// to PropertyChanged and re-applies side effects: font rebind on FontFamily /
// FontSize, scrollback ring resize on ScrollbackLines, emulator screen resize +
// Telnet NAWS re-advertise on TerminalCols / TerminalRows, terminal re-fit on
// ScaleToWindow. AppServices re-resolves these from the active profile / BBS on
// ProfileLoaded / ProfileMutated.
public sealed partial class DisplayConfig : ObservableObject
{
    // The bundled MX437 CP437 bitmap font the TerminalControl renders by
    // default — kept in sync with TerminalControl.FontFamilyProperty's default
    // and used as the fallback whenever the char-tier font choice is unset.
    public const string DefaultFontFamily =
        "avares://FujinTerm/Assets/Fonts/Mx437_IBM_VGA_8x16.ttf#Mx437 IBM VGA 8x16";

    public const double DefaultFontSize = 16.0;

    [ObservableProperty] private double _fontSize = DefaultFontSize;
    [ObservableProperty] private int _scrollbackLines = 4_000;
    [ObservableProperty] private int _terminalCols = 80;
    [ObservableProperty] private int _terminalRows = 25;

    // Terminal canvas font family, as an avares:// URI. Sourced from the
    // char-tier GeneralSettings.TerminalFontFamily; MainWindowViewModel wraps it
    // into a FontFamily the TerminalControl binds to.
    [ObservableProperty] private string _fontFamily = DefaultFontFamily;

    // Auto-fit the terminal font to the window (keeping the fixed cell grid).
    // Sourced from the char-tier GeneralSettings.ScaleTerminalToWindow.
    [ObservableProperty] private bool _scaleToWindow;
}
