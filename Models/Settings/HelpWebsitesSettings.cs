namespace FujinTerm.Models.Settings;

// Global-tier DTO for the user-editable Help-menu website list. Persisted in
// GlobalSettings.Settings["HelpWebsites"] and surfaced two ways: the Help menu
// composes one launch item per link, and Settings → Toolbar + Shortcuts edits
// the list (add / remove / rename / reorder / reset-to-default). Global scope —
// the same set of reference links serves every character on every BBS. The
// active BBS's own website is a separate, per-BBS value (BbsProfile.WebsiteUrl)
// and is NOT stored here.
public sealed class HelpWebsitesSettings
{
    public List<HelpWebsite> Links { get; set; } = DefaultLinks();

    // The seed set restored by the editor's "Reset to default" button. These
    // are the community reference links every MajorMUD player wants at hand.
    public static List<HelpWebsite> DefaultLinks() => new()
    {
        new HelpWebsite { Label = "MajorMUD wiki",           Url = "https://kyau.net/wiki/MajorMUD" },
        new HelpWebsite { Label = "MajorMUD subreddit",      Url = "https://www.reddit.com/r/majormud/" },
        new HelpWebsite { Label = "MudInfo.net",             Url = "https://www.mudinfo.net/" },
        new HelpWebsite { Label = "MajorMUD Facebook Group", Url = "https://www.facebook.com/groups/4826389426" },
    };
}

// One entry in the Help-menu website list: a display label and the URL it opens.
public sealed class HelpWebsite
{
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
