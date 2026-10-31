using System.Collections.Generic;
using System.IO;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Backs the Help → About window: program identity, a clickable repo link, a
// tab per bundled license (loaded verbatim from a resource), and a thank-you
// to the community. Read-only — no persisted state.
// One license tab: the component/license label shown on the tab header and the
// full verbatim license text shown in the body. Top-level so XAML compiled
// bindings can name it as the tab item/content DataType.
public sealed record LicenseEntry(string Name, string Text);

public sealed partial class AboutWindowViewModel : ObservableObject
{
    // Every component whose license ships in this build, each loaded verbatim
    // from a bundled avares:// resource. This list IS the tab set — adding a
    // license is one row here plus the bundled file. Names carry the SPDX-ish
    // license id so the tab is self-describing; the body is the untouched text.
    private static readonly (string Name, string Uri)[] Catalog =
    {
        ("MudPlay — MIT",                "avares://MudPlay/LICENSE"),
        ("Avalonia — MIT",              "avares://MudPlay/Assets/Licenses/Avalonia-LICENSE.txt"),
        ("JetDatabaseReader — MIT",     "avares://MudPlay/Assets/Licenses/JetDatabaseReader-LICENSE.txt"),
        ("ClrMD — MIT",                 "avares://MudPlay/Assets/Licenses/ClrMD-LICENSE.txt"),
        ("JetBrains Mono — SIL OFL 1.1", "avares://MudPlay/Assets/Fonts/JetBrainsMono-LICENSE.txt"),
        ("IBM Plex Sans — SIL OFL 1.1", "avares://MudPlay/Assets/Fonts/IBMPlexSans-LICENSE.txt"),
        ("Px437 / Mx437 — CC BY-SA 4.0", "avares://MudPlay/Assets/Fonts/Px437-LICENSE.txt"),
    };

    public string AppName => AppInfo.DisplayName;
    public string VersionLine => $"Version {AppInfo.Version}";
    public string RepoUrl => AppInfo.RepoUrl;
    public string Tagline => "A modern Avalonia BBS terminal client with MajorMUD-aware features.";

    // Community credit, per the About-page brief. MegaMUD / MajorMUD are the
    // game + reference client this app targets; the others are long-standing
    // community tools worth thanking by name.
    public string Acknowledgements =>
        "Thank you to the MajorMUD community — the players, Guide Makers, and Sysops who "
        + "have kept the game alive for decades. And thank you to those who made the "
        + "tools that shaped the community: MegaMUD, Nightmare Redux, and MajorMUD Explorer.";

    public IReadOnlyList<LicenseEntry> Licenses { get; }

    public AboutWindowViewModel()
    {
        var list = new List<LicenseEntry>(Catalog.Length);
        foreach ((string name, string uri) in Catalog)
            list.Add(new LicenseEntry(name, LoadResourceText(uri)));
        Licenses = list;
    }

    [RelayCommand]
    private void OpenRepo() => ShellLaunch.OpenUrl(RepoUrl);

    private static string LoadResourceText(string uri)
    {
        try
        {
            using Stream s = AssetLoader.Open(new System.Uri(uri));
            using StreamReader r = new(s);
            return r.ReadToEnd().TrimEnd();
        }
        catch (System.Exception)
        {
            // A missing/renamed resource must not blank the whole window — show a
            // visible marker instead of a silently empty tab.
            return $"(license text unavailable — {uri})";
        }
    }
}
