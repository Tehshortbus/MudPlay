using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using FujinTerm.ViewModels;

namespace FujinTerm.Controls;

// Non-selectable left-rail companion to SelectableTranscript. Renders one
// timestamp per row in a muted grey, aligned line-by-line with the transcript
// by sharing its font + size + line metrics. Pulled out into its own control
// so the user's drag-select on the transcript can't include the timestamps.
public sealed class TimestampGutter : TextBlock
{
    public static readonly StyledProperty<IList?> RowsProperty =
        AvaloniaProperty.Register<TimestampGutter, IList?>(nameof(Rows));

    public IList? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    private INotifyCollectionChanged? _observed;
    // Coalesces burst CollectionChanged events into a single Rebuild per
    // dispatcher tick — same compounding-on-typing fix as SelectableTranscript.
    private bool _rebuildScheduled;

    static TimestampGutter()
    {
        RowsProperty.Changed.AddClassHandler<TimestampGutter>((c, e) =>
            c.OnRowsChanged(e.NewValue as IList));
    }

    public TimestampGutter()
    {
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        TextWrapping = TextWrapping.NoWrap;
        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
    }

    private void OnRowsChanged(IList? newRows)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
            _observed = null;
        }
        if (newRows is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += OnCollectionChanged;
            _observed = incc;
        }
        Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRebuild();

    private void ScheduleRebuild()
    {
        if (_rebuildScheduled) return;
        _rebuildScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildScheduled = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        InlineCollection inlines = new();
        IList? rows = Rows;
        if (rows is null || rows.Count == 0) { Inlines = inlines; return; }

        for (int r = 0; r < rows.Count; r++)
        {
            if (rows[r] is not BackscrollRowViewModel row) continue;
            inlines.Add(new Run(row.TimestampText));
            if (r < rows.Count - 1) inlines.Add(new LineBreak());
        }
        Inlines = inlines;
    }
}
