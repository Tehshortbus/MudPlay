using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FujinTerm.ViewModels.Settings;

// One editable row in the Talk tab's per-channel colour list. Each channel gets
// two slots: the accent ("Label") colour used for the channel tag / speaker /
// filter toggle, and the message-body ("Text") colour. Each slot is a live
// Color the visual ColorPicker edits directly. A slot still equal to its theme
// default persists as "no override" (null), so the ConversationViewModel falls
// back to the theme brush; the per-slot Reset snaps it back to that default.
// Overrides are handed downstream as "#RRGGBB" strings.
public sealed partial class ChannelColorRowViewModel : ObservableObject
{
    private readonly Color _defaultLabel;
    private readonly Color _defaultText;
    private readonly Action _onChanged;

    // Stable override key (matches ConversationViewModel.GroupKey) vs. the
    // display name shown in the row.
    public string Key { get; }
    public string Name { get; }

    [ObservableProperty] private Color _labelColor;
    [ObservableProperty] private Color _textColor;

    // Live preview brushes for the swatch buttons that open each picker flyout.
    public IBrush LabelSwatch => new SolidColorBrush(LabelColor);
    public IBrush TextSwatch => new SolidColorBrush(TextColor);

    public ChannelColorRowViewModel(string key, string name, Color defaultLabel, Color defaultText, Action onChanged)
    {
        Key = key;
        Name = name;
        _defaultLabel = defaultLabel;
        _defaultText = defaultText;
        _onChanged = onChanged;
        _labelColor = defaultLabel;
        _textColor = defaultText;
    }

    // "#RRGGBB" when the slot differs from its theme default, else null — a
    // default-equal slot writes no delta.
    public string? LabelOverride => LabelColor == _defaultLabel ? null : Hex(LabelColor);
    public string? TextOverride  => TextColor  == _defaultText  ? null : Hex(TextColor);

    // Seed the pickers from persisted hex, falling back to the theme default
    // when absent / unparsable. Caller wraps this in its suppress-dirty window.
    public void Load(string? labelHex, string? textHex)
    {
        LabelColor = Parse(labelHex, _defaultLabel);
        TextColor  = Parse(textHex, _defaultText);
    }

    [RelayCommand]
    private void ResetLabel() => LabelColor = _defaultLabel;

    [RelayCommand]
    private void ResetText() => TextColor = _defaultText;

    partial void OnLabelColorChanged(Color value)
    {
        OnPropertyChanged(nameof(LabelSwatch));
        _onChanged();
    }

    partial void OnTextColorChanged(Color value)
    {
        OnPropertyChanged(nameof(TextSwatch));
        _onChanged();
    }

    // Drop alpha — overrides persist as RGB only, matching what the
    // Conversation window renders.
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return Color.Parse(hex); }
        catch { return fallback; }
    }
}
