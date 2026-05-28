using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

/// <summary>
/// Two-pane modeless debug window: Raw (with non-printables shown) on the
/// left, Stripped (CSI sequences removed) on the right. Bound to
/// <see cref="WireInspectorViewModel"/>. Code-behind handles ScrollViewer
/// sync (XAML can't easily express two-way scroll-offset binding) and
/// Find-next (a one-shot scroll-to-match — no live highlight in v1).
/// </summary>
public partial class WireInspectorWindow : Window
{
    private ScrollViewer? _rawScroll;
    private ScrollViewer? _strippedScroll;
    private bool _syncingScroll;

    public WireInspectorWindow()
    {
        InitializeComponent();
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
