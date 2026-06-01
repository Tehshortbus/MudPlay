using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels;

namespace FujinTerm.Views;

/// <summary>
/// Modeless Conversation panel. Bound to <see cref="ConversationViewModel"/>;
/// code-behind handles Enter-to-send in the input field and scroll-to-newest
/// when AutoScroll is on.
/// </summary>
public partial class ConversationWindow : Window
{
    private ListBox? _rowsList;

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

    private void OnScrollToRow(ConversationRowViewModel row)
    {
        if (_rowsList is null) return;
        if (DataContext is not ConversationViewModel { AutoScroll: true }) return;
        _rowsList.ScrollIntoView(row);
    }

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

        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        if (DataContext is not ConversationViewModel vm) return;
        if (vm.SendInputCommand.CanExecute(null))
        {
            vm.SendInputCommand.Execute(null);
            e.Handled = true;
        }
    }
}
