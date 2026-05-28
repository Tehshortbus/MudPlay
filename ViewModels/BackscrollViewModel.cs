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
    /// Replace the rows at indices <c>[_scrollbackCount..end)</c> with
    /// fresh snapshots of every screen row. Adjusts the live-tail size if
    /// the screen was resized.
    /// </summary>
    private void RefreshLiveTail()
    {
        TerminalScreen screen = _emulator.Screen;
        int desired = screen.Rows;
        DateTimeOffset now = DateTimeOffset.Now;

        int currentLive = Rows.Count - _scrollbackCount;

        // Replace existing live rows in-place; cheaper than clear + re-add.
        int common = Math.Min(currentLive, desired);
        for (int y = 0; y < common; y++)
        {
            Cell[] cells = screen.Row(y).ToArray();
            Rows[_scrollbackCount + y] = new BackscrollRowViewModel(
                new ScrollbackBuffer.Row(now, cells));
        }

        // Add any extra rows the screen grew into.
        for (int y = common; y < desired; y++)
        {
            Cell[] cells = screen.Row(y).ToArray();
            Rows.Add(new BackscrollRowViewModel(new ScrollbackBuffer.Row(now, cells)));
        }

        // Remove tail if the screen shrank.
        while (Rows.Count - _scrollbackCount > desired)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        OnPropertyChanged(nameof(StatusText));
    }

    [RelayCommand]
    private void GoToLive() => GoToLiveRequested?.Invoke();

    [RelayCommand]
    private void FindNext()
    {
        if (string.IsNullOrEmpty(SearchText)) return;
        int hits = 0;
        int firstMatch = -1;
        for (int i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].PlainText.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            {
                hits++;
                if (firstMatch < 0) firstMatch = i;
            }
        }
        MatchCount = hits;
        OnPropertyChanged(nameof(StatusText));
        if (firstMatch >= 0) ScrollToRowRequested?.Invoke(firstMatch);
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
