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

/// <summary>
/// View-model behind <see cref="Views.BackscrollWindow"/>. Shows the
/// <see cref="ScrollbackBuffer"/> contents (rows that physically scrolled
/// off the top of the screen) followed by a live mirror of the
/// currently-visible terminal screen — so the Backscroll is a true
/// chronological transcript of everything the user has ever seen,
/// including the active prompt row.
/// </summary>
/// <remarks>
/// Rows are laid out as:
/// <code>
/// [ 0 .. _scrollbackCount )           historical rows (ScrollbackBuffer)
/// [ _scrollbackCount .. end )         live mirror of current screen
/// </code>
/// On <see cref="ScrollbackBuffer.RowAdded"/> we insert at the boundary.
/// On <see cref="TerminalEmulator.ScreenUpdated"/> we replace the tail with
/// the current screen rows.
/// </remarks>
public sealed partial class BackscrollViewModel : ObservableObject, IDisposable
{
    private readonly TerminalEmulator _emulator;
    private readonly ScrollbackBuffer _buffer;
    private int _scrollbackCount;
    private bool _disposed;
    private int _lastMatchIndex = -1;
    private string _lastMatchSearchText = string.Empty;

    public ObservableCollection<BackscrollRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _autoFollow = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _matchCount;

    public string StatusText
        => $"{_scrollbackCount:N0} scrollback  •  {Rows.Count - _scrollbackCount:N0} live  •  {MatchCount:N0} matches";

    /// <summary>Fired when the user requests Find Next. The window scrolls.</summary>
    public event Action<int>? ScrollToRowRequested;

    /// <summary>Fired when the user requests Go to live (scroll to bottom).</summary>
    public event Action? GoToLiveRequested;

    public bool FocusSearchOnOpen { get; set; }

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
        Dispatcher.UIThread.Post(RefreshLiveTail);
    }

    /// <summary>
    /// Replace the rows at indices <c>[_scrollbackCount..end)</c> with a
    /// fresh snapshot of every screen row up to the last non-blank row.
    /// Trailing blank rows below the content are dropped — they're just
    /// unused screen padding (a freshly-launched terminal has 25 of them).
    /// Mid-content blank rows are kept since the server may have intentionally
    /// written them for spacing.
    /// </summary>
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
    private void GoToLive() => GoToLiveRequested?.Invoke();

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

        // Clear the previous find-match tint before applying the new one so
        // only one row at a time shows the "current hit" highlight.
        if (_previousMatchRow is not null) _previousMatchRow.IsFindMatch = false;
        _previousMatchRow = null;

        if (next >= 0)
        {
            _lastMatchIndex = next;
            Rows[next].IsFindMatch = true;
            _previousMatchRow = Rows[next];
            ScrollToRowRequested?.Invoke(next);
        }
    }

    private BackscrollRowViewModel? _previousMatchRow;

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
