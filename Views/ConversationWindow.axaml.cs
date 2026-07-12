using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

// Modeless Conversation panel. Bound to ConversationViewModel;
// code-behind handles Enter-to-send in the input field and scroll-to-newest
// when AutoScroll is on.
public partial class ConversationWindow : Window
{
    private ListBox? _rowsList;
    private ScrollViewer? _rowsScroll;

    public ConversationWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "conversation");
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        _rowsList = this.FindControl<ListBox>("RowsList");
        if (DataContext is ConversationViewModel vm)
        {
            vm.ScrollToRowRequested += OnScrollToRow;
            // Land on the freshest row.
            if (vm.Rows.Count > 0) OnScrollToRow(vm.Rows[^1]);
            this.FindControl<TextBox>("InputBox")?.Focus();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is ConversationViewModel vm)
        {
            vm.ScrollToRowRequested -= OnScrollToRow;
            vm.Dispose();
        }
    }

    private void OnRecallSelected(object? sender, SelectionChangedEventArgs e)
    {
        // Dropdown pick fills the box, then hands focus back to the input
        // with the caret at the end so the user can edit / Enter at once.
        if (sender is not ListBox { SelectedItem: string command } list) return;
        if (DataContext is ConversationViewModel vm) vm.InputText = command;
        // Clear the selection so the same entry can be re-picked next open.
        list.SelectedIndex = -1;
        this.FindControl<Button>("RecallButton")?.Flyout?.Hide();
        if (this.FindControl<TextBox>("InputBox") is { } box)
        {
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        }
    }

    private void OnScrollToRow(ConversationRowViewModel row)
    {
        if (_rowsList is null) return;
        if (DataContext is not ConversationViewModel { AutoScroll: true }) return;
        // Defer the scroll. Calling ScrollIntoView synchronously while the
        // virtualizing panel is mid-update (a chat line arriving as the row is
        // added, or the panel still materialising on open) re-enters the layout
        // pass before the new container is measured and throws "Invalid Arrange
        // rectangle" — the same crash that hit the log pane. Posting lets the
        // panel finish its layout first.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _rowsList?.ScrollIntoView(row);
            // ScrollIntoView only scrolls far enough to reveal the row, leaving
            // the ListBox's bottom padding (and any extent growth from a line
            // that landed mid-layout) between the last message and the viewport
            // edge — so "auto-scroll" visibly stopped short of the true bottom.
            // Pin the inner viewport to its end to close that gap.
            ResolveRowsScroll()?.ScrollToEnd();
        });
    }

    private ScrollViewer? ResolveRowsScroll()
        => _rowsScroll ??= _rowsList?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Macro lookup first — same dispatch path the terminal canvas
        // uses, so a user-bound chord (F1, numpad direction, Ctrl+letter)
        // fires its command at the wire instead of typing characters
        // into this input field.
        if (FujinTerm.Services.AppServices.Current.MacroDispatcher
                .TryHandleKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        if (DataContext is not ConversationViewModel vm) return;

        // Up / Down recall previously-sent commands into the box, same as
        // the terminal canvas. The input is single-line, so the arrows
        // have no native job here to clobber.
        if (e.Key == Key.Up || e.Key == Key.Down)
        {
            if (e.Key == Key.Up) vm.RecallPrevious();
            else vm.RecallNext();
            if (sender is TextBox tb) tb.CaretIndex = tb.Text?.Length ?? 0;
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        if (vm.SendInputCommand.CanExecute(null))
        {
            vm.SendInputCommand.Execute(null);
            e.Handled = true;
        }
    }
}
