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

// View-model behind Views.BackscrollWindow. A FROZEN snapshot of the terminal
// history captured when the window opened: the ScrollbackBuffer rows that
// physically scrolled off the top of the screen. The still-on-screen lines are
// deliberately excluded — the window shows history only, not a mirror of the
// live screen.
//
// The window deliberately does NOT track the live terminal. Output keeps
// accumulating in the emulator's ScrollbackBuffer regardless — it's owned by the
// emulator, not this window — so closing and reopening rehydrates a fresh
// snapshot covering everything since; nothing is missed. Freezing is what fixes
// the lag: a live line-stream (e.g. following a fast-moving party leader) forced
// a full transcript rebuild on every screen update while the window was open.
public sealed partial class BackscrollViewModel : ObservableObject
{
    private int _lastMatchIndex = -1;
    private string _lastMatchSearchText = string.Empty;

    public ObservableCollection<BackscrollRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _matchCount;

    public string StatusText
        => $"{Rows.Count:N0} lines  •  {MatchCount:N0} matches";

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
