namespace MudPlay.Services;

// One place the Game Data edit dialogs (Monsters, Items) route a saved override
// through, so the tier semantics are identical everywhere the Use picker appears.
//
// Three outcomes, keyed off the tier the user chose and whether the edit ended up
// equal to the installed defaults:
//   * Installed defaults tier  → a confirm, then ResetGameDataRecord wipes the
//     record's Global / BBS / Character overrides so it falls back to the seed.
//   * edit == installed defaults → the chosen tier's override is redundant, so
//     ClearGameDataAt removes it (an edit dragged back to the seeded value shifts
//     the row back toward Def instead of writing a no-op override).
//   * otherwise → WriteGameDataAt stores the delta at the chosen tier.
//
// Returns true when something changed so the caller reloads its table.
public static class GameDataOverrideApplier
{
    public static async Task<bool> ApplyAsync<T>(
        SettingsResolver resolver,
        ConfirmService confirm,
        string table,
        string recordId,
        SettingsTier tier,
        T overlay,
        bool equalsInstalledDefaults) where T : class
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(overlay);

        if (tier == SettingsTier.Defaults)
        {
            bool ok = await confirm.ConfirmAsync(
                "Reset to installed defaults",
                "This resets the record to its seeded installed defaults, removing your "
                + "Global, BBS, and Character edits for this one record. This can't be undone.\n\n"
                + "Continue?",
                "Reset");
            if (!ok) return false;
            resolver.ResetGameDataRecord(table, recordId);
            return true;
        }

        // A tier whose scope isn't active can't be written (Character with no profile
        // loaded, BBS with no active BBS) — fall back to the most-specific writable
        // tier so Save never throws. Defaults never reaches here.
        if (!resolver.CanWriteAt(tier)) tier = resolver.WritableTiers()[0];

        if (equalsInstalledDefaults)
            resolver.ClearGameDataAt(tier, table, recordId);
        else
            resolver.WriteGameDataAt(tier, table, recordId, overlay);
        return true;
    }
}
