namespace FujinTerm.Game.Cash;

// Runtime source-of-truth for the per-BBS runic-currency name. Some realms
// relabel the top ("runic") denomination to a board-specific word — which
// changes both the coin wording the server sends AND the bare keyword the
// client keys currency commands on (get/drop/hide/give/deposit). Only the
// runic token varies; the "coin"/"coins" noun-suffix and the other four
// denominations are stable, so the noun alone still identifies the
// denomination — this service only resolves the leading word.
//
// The name is read live from BbsProfile.RunicCurrencyName via an injected
// accessor and re-read on ProfileLoaded / BbsPinApplied through Refresh().
// Consumers read RunicName on demand rather than subscribing to a change
// event, so a rename takes effect on the next line/command with no rewiring.
public sealed class CurrencyNaming
{
    public const string DefaultRunicName = "runic";

    private readonly Func<string?> _read;

    // Lowercased, trimmed active runic name; blank/whitespace falls back to
    // "runic". Kept in the same normalized shape the parsers compare against.
    public string RunicName { get; private set; } = DefaultRunicName;

    public CurrencyNaming(Func<string?>? read = null)
    {
        _read = read ?? (static () => null);
        RunicName = Normalize(_read());
    }

    // Re-read the active BBS's runic name (call after a profile / BBS swap).
    public void Refresh() => RunicName = Normalize(_read());

    // True if word names the runic denomination — either the active board's
    // renamed word or the stock "runic" (so mid-session renames and captures
    // that predate a rename both resolve). Case-insensitive.
    public bool IsRunic(string word) =>
        string.Equals(word, RunicName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(word, DefaultRunicName, StringComparison.OrdinalIgnoreCase);

    // Collapse a captured currency word to its canonical denomination key:
    // the runic word (renamed or stock) becomes "runic"; every other word is
    // returned lowercased/trimmed unchanged. Feed this into the value ladder
    // and denomination switches so they never see a board-specific token.
    public string Canonicalize(string word) =>
        IsRunic(word) ? DefaultRunicName : word.Trim().ToLowerInvariant();

    private static string Normalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? DefaultRunicName : raw.Trim().ToLowerInvariant();
}
