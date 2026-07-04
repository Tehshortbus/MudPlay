using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using FujinTerm.Services;
using FujinTerm.ViewModels.GameData;

namespace FujinTerm.Views.GameData;

public partial class SpellCoverageReportWindow : Window
{
    public SpellCoverageReportWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        Closed += OnClosed;
        // Double-click a row → open a new MessageEditDialog pre-filled
        // with that spell's Link, so the user can drill from "this
        // spell has no anchor" to authoring the matching record in
        // one motion. Bubble-stage handler so the DataGrid's own row
        // click handling doesn't swallow it.
        AddHandler(InputElement.DoubleTappedEvent, OnRowDoubleTapped, RoutingStrategies.Bubble);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is SpellCoverageReportViewModel vm) vm.Detach();
    }

    // Walk up the visual tree from the tapped point looking for a row whose
    // DataContext is an UnanchoredSpell; dispatch via the VM's
    // CreateMessageForSpell command when found. A double-tap on the header /
    // scroll bar / empty area falls through to no-op.
    private void OnRowDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Visual src) return;
        Visual? cur = src;
        while (cur is not null)
        {
            if (cur is Control c && c.DataContext is UnanchoredSpell spell)
            {
                if (DataContext is SpellCoverageReportViewModel vm)
                    vm.CreateMessageForSpellCommand.Execute(spell);
                return;
            }
            cur = cur.GetVisualParent();
        }
    }
}
