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
/// View-model behind <see cref="Views.BackscrollWindow"/>. Subscribes to the
/// active terminal's <see cref="ScrollbackBuffer.RowAdded"/> event and
/// maintains an <see cref="ObservableCollection{T}"/> of row VMs the
/// virtualizing list binds to.
/// </summary>
/// <remarks>
/// Sized at the buffer's capacity — 10 000 rows by default. The
/// ListBox virtualizes so the on-screen cost is bounded; off-screen rows
/// are pure data.
/// </remarks>
public sealed partial class BackscrollViewModel : ObservableObject, IDisposable
{
    private readonly ScrollbackBuffer _buffer;
    private bool _disposed;

    public ObservableCollection<BackscrollRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _autoFollow = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _matchCount;

    public string StatusText => $"{Rows.Count:N0} rows  •  {MatchCount:N0} matches";

    /// <summary>Fired when the user requests Find Next. The window scrolls.</summary>
    public event Action<int>? ScrollToRowRequested;

    /// <summary>Fired when the user requests Go to live (scroll to bottom).</summary>
    public event Action? GoToLiveRequested;

    public BackscrollViewModel(ScrollbackBuffer buffer)
    {
        _buffer = buffer;
        Hydrate();
        _buffer.RowAdded += OnRowAdded;
    }

    private void Hydrate()
    {
        Rows.Clear();
        foreach (ScrollbackBuffer.Row row in _buffer.Enumerate())
        {
            Rows.Add(new BackscrollRowViewModel(row));
        }
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnRowAdded(ScrollbackBuffer.Row row)
    {
        // Producer is the emulator on the UI thread already, but Post anyway
        // for robustness against future off-UI captures (replay, paste, etc.).
        Dispatcher.UIThread.Post(() =>
        {
            Rows.Add(new BackscrollRowViewModel(row));
            OnPropertyChanged(nameof(StatusText));
        });
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
        _buffer.RowAdded -= OnRowAdded;
    }
}
