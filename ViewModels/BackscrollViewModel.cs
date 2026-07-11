using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Terminal;

namespace FujinTerm.ViewModels;

// View-model behind Views.BackscrollWindow. A FROZEN snapshot of everything the
// user had seen at the instant the window opened: the ScrollbackBuffer rows that
// physically scrolled off the top of the screen, followed by a one-time capture
// of the then-current terminal screen (including the active prompt row).
//
// The window deliberately does NOT track the live terminal. Output keeps
// accumulating in the emulator's ScrollbackBuffer regardless — it's owned by the
// emulator, not this window — so closing and reopening rehydrates a fresh
// snapshot covering everything since; nothing is missed. Freezing is what fixes
// the lag: a live line-stream (e.g. following a fast-moving party leader) forced
// a full transcript rebuild on every screen update while the window was open.
//
// Rows are laid out as:
//   [ 0 .. ScrollbackCount )      historical rows (ScrollbackBuffer)
//   [ ScrollbackCount .. end )    the screen as it looked when the window opened
public sealed partial class BackscrollViewModel : ObservableObject
{
    private int _scrollbackCount;
    private int _lastMatchIndex = -1;
    private string _lastMatchSearchText = string.Empty;

    public ObservableCollection<BackscrollRowViewModel> Rows { get; } = new();

    // Count of historical (scrollback) rows at the front of Rows — the window
    // reads this to draw the history/screen divider at the boundary index.
    public int ScrollbackCount => _scrollbackCount;

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _matchCount;

    public string StatusText
        => $"{_scrollbackCount:N0} scrollback  •  {Rows.Count - _scrollbackCount:N0} screen  •  {MatchCount:N0} matches";

    // Fired when Find Next lands on a match. Payload: (rowIndex,
    // columnOffsetWithinRowText, matchLength). The window translates it into a
    // character selection in the single transcript block and scrolls it into
    // view.
    public event Action<int, int, int>? FindMatchRequested;

    // Fired when the user requests Jump to end (scroll to the newest row).
    public event Action? JumpToEndRequested;

    public BackscrollViewModel(TerminalEmulator emulator)
    {
        ScrollbackBuffer buffer = emulator.Screen.Scrollback;
        foreach (ScrollbackBuffer.Row row in buffer.Enumerate())
        {
            Rows.Add(new BackscrollRowViewModel(row));
        }
        _scrollbackCount = Rows.Count;
        AppendScreenSnapshot(emulator.Screen);
    }

    // Append a one-time snapshot of every screen row up to the last non-blank
    // row. Trailing blank rows below the content are dropped — they're just
    // unused screen padding (a freshly-launched terminal has 25 of them).
    // Mid-content blank rows are kept since the server may have intentionally
    // written them for spacing.
    private void AppendScreenSnapshot(TerminalScreen screen)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        int lastNonBlank = -1;
        for (int y = 0; y < screen.Rows; y++)
        {
            if (!IsScreenRowBlank(screen, y)) lastNonBlank = y;
        }
        for (int y = 0; y <= lastNonBlank; y++)
        {
            Cell[] cells = screen.Row(y).ToArray();
            Rows.Add(new BackscrollRowViewModel(new ScrollbackBuffer.Row(now, cells)));
        }
    }

    private static bool IsScreenRowBlank(TerminalScreen screen, int y)
    {
        for (int x = 0; x < screen.Cols; x++)
        {
            if (screen[x, y].Char != ' ') return false;
        }
        return true;
    }

    [RelayCommand]
    private void JumpToEnd() => JumpToEndRequested?.Invoke();

    [RelayCommand]
    private void FindNext()
    {
        if (string.IsNullOrEmpty(SearchText)) return;

        // Reset the cursor whenever the search text changes — otherwise the
        // user retyping a fresh query would resume from wherever the last
        // search left off.
        if (!string.Equals(_lastMatchSearchText, SearchText, StringComparison.Ordinal))
        {
            _lastMatchIndex = -1;
            _lastMatchSearchText = SearchText;
        }

        // Tally total hits in the corpus and find the next match strictly
        // AFTER the cursor, wrapping back to 0 if we hit the end.
        int hits = 0;
        for (int i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].PlainText.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                hits++;
        }

        int startFrom = _lastMatchIndex + 1;
        if (startFrom >= Rows.Count) startFrom = 0;

        int next = -1;
        for (int offset = 0; offset < Rows.Count; offset++)
        {
            int i = (startFrom + offset) % Rows.Count;
            if (Rows[i].PlainText.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            {
                next = i;
                break;
            }
        }

        MatchCount = hits;
        OnPropertyChanged(nameof(StatusText));

        if (next >= 0)
        {
            _lastMatchIndex = next;
            string text = Rows[next].PlainText;
            int col = text.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase);
            if (col < 0) col = 0;
            FindMatchRequested?.Invoke(next, col, SearchText.Length);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return;

        IStorageFile? file = await main.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export backscroll",
            SuggestedFileName = $"backscroll-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType("Plain text (.txt)") { Patterns = ["*.txt"] },
            ],
        });

        if (file is null) return;

        StringBuilder sb = new(capacity: Rows.Count * 88);
        foreach (BackscrollRowViewModel row in Rows)
        {
            sb.Append('[').Append(row.TimestampText).Append("] ").AppendLine(row.PlainText);
        }

        await using Stream stream = await file.OpenWriteAsync();
        await using StreamWriter writer = new(stream);
        await writer.WriteAsync(sb.ToString()).ConfigureAwait(false);
    }
}
