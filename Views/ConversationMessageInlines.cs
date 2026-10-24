using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MudPlay.Controls;
using MudPlay.Game.Emotes;
using MudPlay.Services;

namespace MudPlay.Views;

// Attached behaviour that renders a conversation message into a TextBlock's inline
// runs with clickable web links: plain spans stay text Runs (so the line still
// wraps) and http/https URLs become
// underlined LinkRuns that open in the OS default browser via ShellLaunch.OpenUrl.
// Bind MessageProperty in XAML instead of Text; the handler rebuilds the inlines
// whenever the text changes. Mirrors the CharacterWorkshop QuestStepInlines pattern.
public static class ConversationMessageInlines
{
    public static readonly AttachedProperty<string?> MessageProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>(
            "Message", typeof(ConversationMessageInlines));

    public static void SetMessage(TextBlock target, string? value) =>
        target.SetValue(MessageProperty, value);

    public static string? GetMessage(TextBlock target) =>
        target.GetValue(MessageProperty);

    // Whether to substitute emoji / emote shortcodes (bound to the Talk setting). When
    // off, the message renders as plain text + links exactly as before. Default true so
    // a host that doesn't set it still gets emotes.
    public static readonly AttachedProperty<bool> EmotesEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>(
            "EmotesEnabled", typeof(ConversationMessageInlines), defaultValue: true);

    public static void SetEmotesEnabled(TextBlock target, bool value) =>
        target.SetValue(EmotesEnabledProperty, value);

    public static bool GetEmotesEnabled(TextBlock target) =>
        target.GetValue(EmotesEnabledProperty);

    // Drop a cached emote bitmap so a replaced / re-imported user image reloads.
    // Wired to EmoteStore.ConversationImageInvalidate by MainWindowViewModel.
    public static void InvalidateEmoteImage(string path) => EmoteImages.Invalidate(path);

    // Load (and cache) an emote bitmap by avares:// URI or file path — used by the
    // emote picker for suggestion thumbnails, sharing the renderer's cache.
    public static Bitmap? LoadEmoteBitmap(string path) => EmoteImages.Get(path);

    // http(s):// run of non-space characters. Trailing sentence punctuation is
    // trimmed off the match below so "see https://x.org." doesn't eat the period.
    private static readonly Regex s_url = new(
        @"https?://\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Trailing characters that are almost always sentence punctuation wrapping the
    // link rather than part of it. Peeled off the end and rendered as plain text.
    private const string TrailingTrim = ".,;:!?)]}>\"'";

    static ConversationMessageInlines()
    {
        MessageProperty.Changed.AddClassHandler<TextBlock>((t, _) => Rebuild(t));
        EmotesEnabledProperty.Changed.AddClassHandler<TextBlock>((t, _) => Rebuild(t));
        // A picture emote is sized from the row's font and line height, and both are
        // bound after the message is (and change with the font-size setting), so a
        // row that carries a message is laid out again when either arrives.
        TextBlock.LineHeightProperty.Changed.AddClassHandler<TextBlock>((t, _) => RebuildIfHosted(t));
        TextBlock.FontSizeProperty.Changed.AddClassHandler<TextBlock>((t, _) => RebuildIfHosted(t));
    }

    // Only a TextBlock this behaviour was given a message for; the class handlers
    // above see every TextBlock in the app.
    private static void RebuildIfHosted(TextBlock target)
    {
        if (target.IsSet(MessageProperty)) Rebuild(target);
    }

    // Rebuild the inline runs: URLs become clickable links; the remaining text is
    // emote-scanned (when enabled) so shortcodes / emoticons render as emoji runs and
    // image emotes as inline pictures. Links are split first so a smiley inside a URL is
    // never substituted.
    private static void Rebuild(TextBlock target)
    {
        string message = GetMessage(target) ?? string.Empty;
        bool emotes = GetEmotesEnabled(target);

        var inlines = new InlineCollection();
        foreach ((string text, bool isLink) in Segment(message))
        {
            if (isLink) { inlines.Add(LinkInline(text)); continue; }
            if (!emotes) { inlines.Add(new Run(text)); continue; }
            foreach (EmoteSegment seg in EmoteRuntime.Scanner.Scan(text))
                inlines.Add(EmoteInline(seg, target));
        }
        target.Inlines = inlines;
    }

    // One emote segment → an inline: a text/emoji Run, or an inline Image for an image
    // emote (falling back to the literal shortcode text when the asset can't load).
    private static Inline EmoteInline(EmoteSegment seg, TextBlock host)
    {
        if (seg.Kind == EmoteSegmentKind.Image && seg.Payload is { } uri
            && EmoteImages.Get(uri) is { } bmp)
        {
            double h = EmoteImageSize(host.FontSize, host.LineHeight);
            var img = new Image
            {
                Source = bmp,
                Width = h,
                Height = h,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                [ToolTip.TipProperty] = seg.Shortcode,
            };
            return new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Center };
        }
        // Emoji run, or an image emote whose asset was missing → its literal text.
        return new Run(seg.Text);
    }

    // The side of a picture emote's square box. It reads best a little taller than
    // the text, but a TextBlock with a set LineHeight gives every line exactly that
    // height and does not grow one for an inline picture: a taller picture is drawn
    // over the lines and rows above and below it. So the line height, when set, is
    // the most a picture may take; with none set the line grows to fit it.
    internal static double EmoteImageSize(double fontSize, double lineHeight)
    {
        double wanted = fontSize > 0 ? fontSize * 1.75 : 22;
        return lineHeight > 0 && !double.IsNaN(lineHeight) && !double.IsInfinity(lineHeight)
            ? Math.Min(wanted, lineHeight)
            : wanted;
    }

    // Loads + caches emote bitmaps by avares:// URI or file path so a repeated
    // emote reuses one decode. A missing / unreadable asset caches null so the renderer falls back to
    // literal text without retrying every row.
    private static class EmoteImages
    {
        private static readonly Dictionary<string, Bitmap?> _cache = new();

        public static Bitmap? Get(string path)
        {
            if (_cache.TryGetValue(path, out Bitmap? cached)) return cached;
            Bitmap? bmp = null;
            try
            {
                // Built-in emotes are avares:// resources; user emotes are absolute
                // file paths (the shared or a BBS's emote folder).
                bmp = path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
                    ? new Bitmap(AssetLoader.Open(new Uri(path)))
                    : new Bitmap(path);
            }
            catch { /* missing / bad asset → null, rendered as literal text */ }
            _cache[path] = bmp;
            return bmp;
        }

        // Drop a cached bitmap so a re-imported / replaced user emote reloads.
        public static void Invalidate(string path) => _cache.Remove(path);
    }

    // Split a message into ordered (text, isLink) segments: http/https URLs become
    // link segments, everything else (including punctuation peeled off a link's
    // tail) stays plain text. Pure + testable; the view handler just maps segments
    // to Runs / link inlines.
    internal static IReadOnlyList<(string Text, bool IsLink)> Segment(string text)
    {
        var segments = new List<(string, bool)>();
        if (string.IsNullOrEmpty(text)) return segments;

        int pos = 0;
        foreach (Match match in s_url.Matches(text))
        {
            if (match.Index > pos)
                segments.Add((text.Substring(pos, match.Index - pos), false));

            string url = match.Value;
            int keep = url.Length;
            while (keep > 0 && TrailingTrim.IndexOf(url[keep - 1]) >= 0) keep--;

            if (keep > 0) segments.Add((url.Substring(0, keep), true));
            if (keep < url.Length) segments.Add((url.Substring(keep), false));

            pos = match.Index + match.Length;
        }

        if (pos < text.Length)
            segments.Add((text.Substring(pos), false));

        return segments;
    }

    private static LinkRun LinkInline(string url)
    {
        // Font is left to inherit from the host TextBlock so the link
        // tracks the user's message font size / family; only colour + underline
        // come from the ChatLinkText style.
        var link = new LinkRun(url, () => ShellLaunch.OpenUrl(url), tip: url);
        link.Classes.Add("ChatLinkText");
        return link;
    }
}
