namespace FujinTerm.Models.Settings;

/// <summary>
/// Root DTO for <c>Data/BBS/{bbs-name}.json</c> — the BBS tier of the
/// settings hierarchy. Connection info plus deltas the user pinned to "only
/// for this BBS." Per-character credentials are stored separately under each
/// <c>CharacterProfile</c>; this file describes the BBS itself.
/// </summary>
public sealed class BbsProfile
{
    /// <summary>JSON schema version (see <c>GlobalSettings.SchemaVersion</c> for the contract).</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Display name + filename key for this BBS.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Hostname or IP address the Telnet client connects to.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>TCP port; defaults to the Telnet well-known port.</summary>
    public int Port { get; set; } = 23;
}
