using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

// Modeless Roomba master inventory list opened from the Workshop's
// Roomba tab. Code-behind disposes the VM on close (unsubscribing it from
// GhItemLocationStore) and routes a row double-click to the item's record —
// everything else is XAML.
public partial class RoombaMasterListWindow : Window
{
    public RoombaMasterListWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "roombamasterlist");
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is RoombaMasterListViewModel vm) vm.Dispose();
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is RoombaMasterListViewModel vm
            && sender is DataGrid { SelectedItem: RoombaMasterListRowViewModel row })
            _ = vm.OpenItemRecordAsync(row);
    }

    // Export the whole logged list to a text file, room-grouped. The VM builds the
    // text; the window owns the save picker + file write (same shape as
    // WireInspectorWindow's export).
    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RoombaMasterListViewModel vm) return;
        string text = vm.BuildExportText();

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Roomba master list",
            SuggestedFileName = $"roomba-master-list-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Plain text (.txt)") { Patterns = ["*.txt"] }],
        });
        if (file is null) return;

        await using System.IO.Stream stream = await file.OpenWriteAsync();
        await using System.IO.StreamWriter writer = new(stream);
        await writer.WriteAsync(text);
    }
}
