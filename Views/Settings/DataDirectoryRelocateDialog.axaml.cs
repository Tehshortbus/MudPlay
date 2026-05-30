using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FujinTerm.Services;
using FujinTerm.ViewModels.Settings;

namespace FujinTerm.Views.Settings;

public partial class DataDirectoryRelocateDialog : Window
{
    public DataDirectoryRelocateDialog()
    {
        InitializeComponent();
    }

    private async void OnBrowseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not DataDirectoryRelocateDialogViewModel vm) return;

        IStorageFolder? start = await StorageProvider.TryGetFolderFromPathAsync(AppPaths.DataRoot);
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                 = "Pick a new data folder",
            AllowMultiple         = false,
            SuggestedStartLocation = start,
        });

        if (picked.Count == 0) return;
        string? path = picked[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        vm.SetDestination(path);
    }
}
