using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using FujinTerm.ViewModels.Settings;

namespace FujinTerm.Views.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // The Window has to be Focusable for ClearFocusGlobal()'s Focus()
        // call to actually take effect — otherwise it's a silent no-op
        // and the TextBox keeps its caret.
        Focusable = true;
        FujinTerm.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "settings");
        // Re-subscribe whenever the view-model is swapped; first attach happens
        // after construction when the host assigns DataContext.
        DataContextChanged += (_, _) => HookCloseRequested();

        // Title-bar X / Alt-F4 / parent-window-close: treat as Cancel
        // (discard pending edits) so the window can't sneak past the
        // user's explicit commit decision. Routed through DiscardChanges
        // rather than DiscardAndClose to avoid re-entering Close.
        Closing += (_, _) =>
        {
            if (DataContext is SettingsWindowViewModel vm && !vm.IsCommitted)
            {
                vm.DiscardChanges();
            }
        };

        // Click-outside-to-unfocus + Enter-to-commit: when the user
        // clicks anywhere that isn't an interactive control, or presses
        // Enter inside a single-line TextBox / NumericUpDown, drop
        // focus off the current input. handledEventsToo on the pointer
        // route catches the case where TextBox's own PointerPressed
        // logic marks the event handled before we see it.
        AddHandler(PointerPressedEvent, OnAnyPointerPressed,
                   RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnAnyKeyDown,
                   RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control source) return;

        // Walk from the click target up to this window. If any ancestor
        // is an input control, the click landed on something that wants
        // focus — leave it alone. Otherwise the click was on chrome /
        // label / panel: clear focus globally so the active TextBox
        // releases its caret.
        Control? walk = source;
        while (walk is not null && walk != this)
        {
            if (IsFocusableInput(walk)) return;
            walk = walk.Parent as Control;
        }

        ClearFocusGlobal();
    }

    private void OnAnyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        if (e.Source is not Control source) return;

        // Find the nearest TextBox ancestor (TextBox can be wrapped by
        // NumericUpDown / ComboBox / AutoCompleteBox so the raw source
        // may be the inner TextBox, but the user-visible control is the
        // wrapper). Multi-line TextBoxes (AcceptsReturn=true) own
        // Enter for newline insertion — leave them alone.
        Control? walk = source;
        TextBox? textBox = null;
        while (walk is not null && walk != this)
        {
            if (walk is TextBox tb) { textBox = tb; break; }
            walk = walk.Parent as Control;
        }
        if (textBox is null) return;
        if (textBox.AcceptsReturn) return;

        ClearFocusGlobal();
        e.Handled = true;
    }

    private void ClearFocusGlobal()
    {
        // Avalonia 12 doesn't expose a ClearFocus() on IFocusManager —
        // the documented way to release the active element is to focus
        // something else. The window itself works as long as we set
        // Focusable = true on it (done in the ctor). Calling Focus()
        // here transfers keyboard focus away from whatever TextBox /
        // NumericUpDown currently holds the caret.
        Focus();
    }

    private static bool IsFocusableInput(Control c) => c is
        TextBox or
        NumericUpDown or
        ComboBox or
        CheckBox or
        ToggleButton or
        RadioButton or
        Button or
        ListBox or
        Slider or
        AutoCompleteBox;

    private SettingsWindowViewModel? _hooked;

    private void HookCloseRequested()
    {
        if (_hooked is not null)
        {
            _hooked.CloseRequested -= Close;
            _hooked = null;
        }
        if (DataContext is SettingsWindowViewModel vm)
        {
            vm.CloseRequested += Close;
            _hooked = vm;
        }
    }
}
