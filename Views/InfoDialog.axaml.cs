using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views;

// Reusable read-only modeless dialog for "show this text and a Close
// button" affordances — About, License, Keyboard shortcuts, anything that
// doesn't earn a bespoke window. Call Configure after construction; then
// Show(owner).
public partial class InfoDialog : Window
{
    public InfoDialog()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Set the window title and body text.
    public void Configure(string title, string body)
    {
        Title = title;
        SelectableTextBlock body_ = this.FindControl<SelectableTextBlock>("Body")!;
        body_.Text = body;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
