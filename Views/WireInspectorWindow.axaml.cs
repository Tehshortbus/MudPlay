using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Two-pane modeless debug window: Raw (with non-printables shown) on the
// left, Stripped (CSI sequences removed) on the right. Bound to
// WireInspectorViewModel. Code-behind handles ScrollViewer sync (XAML can't
// easily express two-way scroll-offset binding) and Find-next (a one-shot
// scroll-to-match — no live highlight in v1).
public partial class WireInspectorWindow : Window
{
    private ScrollViewer? _rawScroll;
    private ScrollViewer? _strippedScroll;
    private bool _syncingScroll;

    public WireInspectorWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "wireinspector");
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        _rawScroll      = this.FindControl<ScrollViewer>("RawScroll");
        _strippedScroll = this.FindControl<ScrollViewer>("StrippedScroll");

        if (_rawScroll is not null)      _rawScroll.ScrollChanged      += OnRawScrolled;
        if (_strippedScroll is not null) _strippedScroll.ScrollChanged += OnStrippedScrolled;

        if (DataContext is WireInspectorViewModel vm)
        {
            vm.RefreshCompleted += OnRefreshCompleted;
            // Initial scroll-to-end so the open lands on the live tail.
            OnRefreshCompleted();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is WireInspectorViewModel vm)
        {
            vm.RefreshCompleted -= OnRefreshCompleted;
            // VM owns a DispatcherTimer; stop it so we don't leak ticks.
            vm.Dispose();
        }
    }

    private void OnRefreshCompleted()
    {
        if (DataContext is not WireInspectorViewModel { AutoScroll: true }) return;
        _syncingScroll = true;   // suppress the sync-scroll handlers below
        try
        {
            _rawScroll?.ScrollToEnd();
            _strippedScroll?.ScrollToEnd();
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void OnRawScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        if (DataContext is not WireInspectorViewModel { SyncScroll: true }) return;
        if (_rawScroll is null || _strippedScroll is null) return;
        _syncingScroll = true;
        _strippedScroll.Offset = new Vector(_strippedScroll.Offset.X, _rawScroll.Offset.Y);
        _syncingScroll = false;
    }

    private void OnStrippedScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        if (DataContext is not WireInspectorViewModel { SyncScroll: true }) return;
        if (_rawScroll is null || _strippedScroll is null) return;
        _syncingScroll = true;
        _rawScroll.Offset = new Vector(_rawScroll.Offset.X, _strippedScroll.Offset.Y);
        _syncingScroll = false;
    }

    private async void OnExportRaw(object? sender, RoutedEventArgs e)
        => await ExportPaneAsync("raw", () => (DataContext as WireInspectorViewModel)?.RawText);

    private async void OnExportStripped(object? sender, RoutedEventArgs e)
        => await ExportPaneAsync("stripped", () => (DataContext as WireInspectorViewModel)?.StrippedText);

    private async System.Threading.Tasks.Task ExportPaneAsync(string kind, Func<string?> body)
    {
        string? text = body();
        if (string.IsNullOrEmpty(text)) return;

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {kind} wire output",
            SuggestedFileName = $"wire-{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Plain text (.txt)") { Patterns = ["*.txt"] }],
        });

        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream);
        await writer.WriteAsync(text);
    }

    private void OnFindNext(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WireInspectorViewModel vm) return;
        string needle = vm.SearchText;
        if (string.IsNullOrEmpty(needle)) return;

        // Search the stripped pane (closer to human-readable text). On match,
        // scroll both panes to the match's line in the stripped view. Without
        // direct character-index → pixel-Y access on TextBlock we approximate
        // by counting newlines before the match and scaling by line height.
        SelectableTextBlock? body = this.FindControl<SelectableTextBlock>("StrippedBody");
        if (body is null || _strippedScroll is null) return;

        int idx = vm.StrippedText.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        int linesAbove = 0;
        for (int i = 0; i < idx; i++)
        {
            if (vm.StrippedText[i] == '\n') linesAbove++;
        }

        double lineHeight = body.FontSize * 1.4;          // matches the TextBlock default cadence.
        double targetY = Math.Max(0, linesAbove * lineHeight - _strippedScroll.Bounds.Height / 2);
        _strippedScroll.Offset = new Vector(_strippedScroll.Offset.X, targetY);
    }
}
