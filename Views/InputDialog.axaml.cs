using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FujinTerm.Views;

/// <summary>
/// Generic single-line input prompt used for things like
/// "name the new profile" / "save profile as…". Modeless per the global
/// rule — terminal stays interactive while it's open — but blocks the
/// calling workflow via the <see cref="ResultTask"/> TaskCompletionSource
/// so callers can await a user answer.
/// </summary>
public partial class InputDialog : Window
{
    private readonly TaskCompletionSource<string?> _result = new();

    public InputDialog()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (!_result.Task.IsCompleted) _result.SetResult(null);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Resolves with the entered text, or <c>null</c> on Cancel / close.</summary>
    public Task<string?> ResultTask => _result.Task;

    /// <summary>Populate headline, description, and pre-filled value before <c>Show</c>.</summary>
    public void Configure(string headline, string description, string initialValue = "")
    {
        Title = headline;
        ((TextBlock)this.FindControl<TextBlock>("HeadlineText")!).Text = headline;
        ((TextBlock)this.FindControl<TextBlock>("DescriptionText")!).Text = description;
        TextBox box = this.FindControl<TextBox>("InputBox")!;
        box.Text = initialValue;
        Opened += (_, _) => box.Focus();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        string text = this.FindControl<TextBox>("InputBox")?.Text ?? string.Empty;
        _result.TrySetResult(text);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(null);
        Close();
    }
}
