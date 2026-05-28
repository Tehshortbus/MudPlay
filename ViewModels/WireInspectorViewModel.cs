using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

/// <summary>
/// View-model behind <see cref="Views.WireInspectorWindow"/>. Polls the shared
/// <see cref="WireBuffer"/> on a low-frequency UI tick (200 ms) and exposes
/// the rendered Raw + Stripped text the two panes bind to.
/// </summary>
/// <remarks>
/// <para>
/// We poll rather than subscribe to <see cref="WireBuffer.BufferChanged"/>
/// because incoming bytes arrive in tiny chunks (one per Telnet read) and
/// repainting the entire 64 KB pane on every byte would melt the UI. 200 ms
/// is responsive enough for an at-a-glance debugger and keeps the
/// dispatcher idle the rest of the time.
/// </para>
/// <para>
/// The ViewModel implements <see cref="IDisposable"/> — the hosting window
/// disposes it on close so the timer stops cleanly.
/// </para>
/// </remarks>
public sealed partial class WireInspectorViewModel : ObservableObject, IDisposable
{
    private readonly WireBuffer _buffer;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private string _rawText = string.Empty;

    [ObservableProperty]
    private string _strippedText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    private bool _isPaused;

    [ObservableProperty]
    private bool _syncScroll = true;

    /// <summary>
    /// When true (default), each refresh tick scrolls both panes to the
    /// bottom so the freshest bytes are always visible. The window's
    /// code-behind does the actual <c>ScrollToEnd</c> call in response to
    /// <see cref="RefreshCompleted"/>.
    /// </summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>Fires after each non-paused <see cref="Refresh"/> on the UI thread.</summary>
    public event Action? RefreshCompleted;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusText = "Listening…";

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    public WireInspectorViewModel(WireBuffer buffer)
    {
        _buffer = buffer;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    private void Refresh()
    {
        if (IsPaused) return;

        byte[] snapshot = _buffer.Snapshot();
        RawText = WireFormatter.RenderRaw(snapshot);
        StrippedText = WireFormatter.RenderStripped(snapshot);
        StatusText = $"{snapshot.Length:N0} / {_buffer.Capacity:N0} bytes  •  {_buffer.TotalBytes:N0} total";

        RefreshCompleted?.Invoke();
    }

    /// <summary>Toggle the live refresh. When paused the buffer keeps growing.</summary>
    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused) Refresh();
    }

    /// <summary>Drop every byte from the buffer and wipe both panes.</summary>
    [RelayCommand]
    private void Clear()
    {
        _buffer.Clear();
        RawText = string.Empty;
        StrippedText = string.Empty;
    }

    public void Dispose() => _timer.Stop();
}
