using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Terminal;

namespace FujinTerm.ViewModels;

// View-model behind Views.BackscrollWindow. Shows the ScrollbackBuffer
// contents (rows that physically scrolled off the top of the screen)
// followed by a live mirror of the currently-visible terminal screen — so
// the Backscroll is a true chronological transcript of everything the user
// has ever seen, including the active prompt row.
//
// Rows are laid out as:
//   [ 0 .. _scrollbackCount )      historical rows (ScrollbackBuffer)
//   [ _scrollbackCount .. end )    live mirror of current screen
// On ScrollbackBuffer.RowAdded we insert at the boundary. On
// TerminalEmulator.ScreenUpdated we replace the tail with the current
// screen rows.
public sealed partial class BackscrollViewModel : ObservableObject, IDisposable
{
    private readonly TerminalEmulator _emulator;
    private readonly ScrollbackBuffer _buffer;
    private int _scrollbackCount;
    private bool _disposed;
    private int _lastMatchIndex = -1;
    private string _lastMatchSearchText = string.Empty;
    // Coalesces many ScreenUpdated events into a single RefreshLiveTail per
    // dispatcher tick — without this, every byte echoed during typing fires
    // a full live-tail refresh (which replaces ~25 rows in Rows). The
    // virtualizing ListBox only re-renders the realised tail rows, but the
    // coalescing still spares the collection churn on keystroke bursts.
    private bool _refreshScheduled;
    // Set when a ScreenUpdated arrived while mirroring was suspended
    // (see AutoFollow) so we can catch the tail up on resume.
    private bool _liveTailDeferred;

    public ObservableCollection<BackscrollRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;

    // Whether the live screen mirror tracks the terminal in real time.
    // Driven by Views.BackscrollWindow: true only while the window is focused
    // AND scrolled to the bottom. When false, live-tail refreshes are deferred
    // so a user reading back through history isn't repeatedly yanked down to
    // the tail as new screen output streams in.
    [ObservableProperty] private bool _autoFollow = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _matchCount;

    public string StatusText
        => $"{_scrollbackCount:N0} scrollback  •  {Rows.Count - _scrollbackCount:N0} live  •  {MatchCount:N0} matches";

    // Fired when Find Next lands on a match. Payload: (rowIndex,
    // columnOffsetWithinRowText, matchLength). The window selects the matched
    // row in the ListBox and scrolls it into view (row-granular — the column
    // and length are reported for completeness but unused by the window).
    public event Action<int, int, int>? FindMatchRequested;

    // Fired when the user requests Go to live (scroll to bottom).
    public event Action? GoToLiveRequested;

    public BackscrollViewModel(TerminalEmulator emulator)
    {
        _emulator = emulator;
        _buffer = emulator.Screen.Scrollback;

        Hydrate();

        _buffer.RowAdded += OnScrollbackRowAdded;
        _emulator.ScreenUpdated += OnScreenUpdated;
    }

    private void Hydrate()
    {
        Rows.Clear();
        foreach (ScrollbackBuffer.Row row in _buffer.Enumerate())
        {
            Rows.Add(new BackscrollRowViewModel(row));
        }
        _scrollbackCount = Rows.Count;
        RefreshLiveTail();
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnScrollbackRowAdded(ScrollbackBuffer.Row row)
    {
        // Producer thread is the emulator's UI-thread Feed path in
        // production; Post-to-UI defensively in case a future off-UI
        // capture (replay tooling, future paste-snapshot, etc.) appears.
        Dispatcher.UIThread.Post(() =>
        {
            Rows.Insert(_scrollbackCount, new BackscrollRowViewModel(row));
            _scrollbackCount++;
            OnPropertyChanged(nameof(StatusText));
        });
    }

    private void OnScreenUpdated()
    {
        // Suspended: the user isn't watching the live tail (window unfocused or
        // scrolled up into history). Record that we fell behind and catch up in
        // OnAutoFollowChanged once the user returns to the tail, rather than
        // scrolling them down mid-review.
        if (!AutoFollow) { _liveTailDeferred = true; return; }
        if (_refreshScheduled) return;
        _refreshScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshScheduled = false;
            if (AutoFollow) RefreshLiveTail();
            else _liveTailDeferred = true;
        });
    }

    partial void OnAutoFollowChanged(bool value)
    {
        // Back at the live tail: flush the screen state we skipped while suspended.
        if (value && _liveTailDeferred)
        {
            _liveTailDeferred = false;
            RefreshLiveTail();
        }
    }

    // Replace the rows at indices [_scrollbackCount..end) with a fresh
    // snapshot of every screen row up to the last non-blank row. Trailing
    // blank rows below the content are dropped — they're just unused screen
    // padding (a freshly-launched terminal has 25 of them). Mid-content blank
    // rows are kept since the server may have intentionally written them for
    // spacing.
    private void RefreshLiveTail()
    {
        TerminalScreen screen = _emulator.Screen;
        DateTimeOffset now = DateTimeOffset.Now;

        List<BackscrollRowViewModel> liveRows = new();
        int lastNonBlank = -1;
        for (int y = 0; y < screen.Rows; y++)
        {
            if (!IsScreenRowBlank(screen, y))
            {
                lastNonBlank = y;
            }
        }
        for (int y = 0; y <= lastNonBlank; y++)
        {
            Cell[] cells = screen.Row(y).ToArray();
            liveRows.Add(new BackscrollRowViewModel(new ScrollbackBuffer.Row(now, cells)));
        }

        int currentLive = Rows.Count - _scrollbackCount;

        // Replace existing live rows in-place; cheaper than clear + re-add.
        int common = Math.Min(currentLive, liveRows.Count);
        for (int y = 0; y < common; y++)
        {
            Rows[_scrollbackCount + y] = liveRows[y];
        }

        // Add any extra rows beyond what was already there.
        for (int y = common; y < liveRows.Count; y++)
        {
            Rows.Add(liveRows[y]);
        }

        // Remove tail if the new snapshot is shorter than the previous one.
        while (Rows.Count - _scrollbackCount > liveRows.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        OnPropertyChanged(nameof(StatusText));
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
    private void GoToLive()
    {
        // Resume mirroring (flushes any deferred tail) before scrolling down.
        AutoFollow = true;
        GoToLiveRequested?.Invoke();
    }

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffer.RowAdded -= OnScrollbackRowAdded;
        _emulator.ScreenUpdated -= OnScreenUpdated;
    }
}
