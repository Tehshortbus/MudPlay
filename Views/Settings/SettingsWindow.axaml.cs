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

        // Click-outside-to-unfocus: when the user clicks anywhere that
        // isn't an interactive control, drop focus off the current
        // TextBox / NumericUpDown / etc. so they don't have to navigate
        // to another control just to commit a half-typed value or stop
        // capturing keystrokes. Bubble routing — by the time we run,
        // any input ancestor along the route has already had a chance
        // to handle / claim focus.
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Bubble);
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control source) return;

        // Walk from the click target up to this window. If any ancestor
        // is an input control, the click landed on something that wants
        // focus — leave it alone. Otherwise the click was on a chrome
        // background / label / panel, so push focus off the current
        // element and onto the window root.
        Control? walk = source;
        while (walk is not null && walk != this)
        {
            if (IsFocusableInput(walk)) return;
            walk = walk.Parent as Control;
        }

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
