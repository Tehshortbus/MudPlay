using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// The realm-wide and BBS-wide lists of players' remote settings, and which of them (if
// either) is in force. The BBS's file holds the scope and the BBS list; each realm's
// file holds that realm's list. Every client on the BBS reads the same files, so
// they are followed the way the realm's other shared files are.
public sealed class SharedPlayerGrants
{
    private sealed class GrantsFile
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PlayerPermissionScope? Scope { get; set; }

        // Null until the list is first used, which is what lets the first use start
        // from the table on show without a later switch undoing a revoke.
        public Dictionary<string, PlayerGrant>? Players { get; set; }
    }

    public static string Describe(PlayerPermissionScope scope) => scope switch
    {
        PlayerPermissionScope.Realm => "for every character on this realm",
        PlayerPermissionScope.Bbs   => "for every character on this BBS",
        _                           => "per character",
    };

    private readonly SharedFileStamp _bbsStamp = new();
    private readonly SharedFileStamp _realmStamp = new();
    private string? _bbsFile;
    private string? _realmFile;
    private Dictionary<string, PlayerGrant>? _bbsGrants;
    private Dictionary<string, PlayerGrant>? _realmGrants;

    public PlayerPermissionScope Scope { get; private set; } = PlayerPermissionScope.Character;

    // False with no BBS and realm resolved: there is nothing to share across.
    public bool Available => _bbsFile is not null && _realmFile is not null;

    public bool IsShared => Available && Scope != PlayerPermissionScope.Character;

    private Dictionary<string, PlayerGrant>? ActiveList =>
        Scope == PlayerPermissionScope.Bbs ? _bbsGrants : _realmGrants;

    public void Attach(string? bbsFolder, string? realmFolder)
    {
        bool usable = !string.IsNullOrEmpty(bbsFolder) && !string.IsNullOrEmpty(realmFolder);
        _bbsFile   = usable ? AppPaths.PlayerPermissionsFile(bbsFolder!) : null;
        _realmFile = usable ? AppPaths.PlayerPermissionsFile(realmFolder!) : null;
        LoadBbs();
        LoadRealm();
    }

    public bool TakeInOutsideChanges()
    {
        if (!Available) return false;
        bool bbs = _bbsStamp.ChangedOutside(_bbsFile);
        bool realm = _realmStamp.ChangedOutside(_realmFile);
        if (bbs) LoadBbs();
        if (realm) LoadRealm();
        return bbs || realm;
    }

    public PlayerGrant Get(string given) =>
        ActiveList is { } list && list.TryGetValue(given, out PlayerGrant grant) ? grant : default;

    public void Set(string given, PlayerGrant grant)
    {
        if (!IsShared) return;
        Dictionary<string, PlayerGrant> list = ActiveList ?? NewList();
        if (grant.IsEmpty) list.Remove(given);
        else list[given] = grant;
        if (Scope == PlayerPermissionScope.Bbs) { _bbsGrants = list; SaveBbs(); }
        else { _realmGrants = list; SaveRealm(); }
    }

    // A list used for the first time starts from what the table shows, so widening
    // keeps the grants on screen. One used before keeps its own contents.
    public void ChangeScope(PlayerPermissionScope scope, IEnumerable<KeyValuePair<string, PlayerGrant>> characterGrants)
    {
        if (!Available || scope == Scope || !Enum.IsDefined(scope)) return;
        IEnumerable<KeyValuePair<string, PlayerGrant>> shown =
            IsShared ? ActiveList ?? NewList() : characterGrants;

        if (scope == PlayerPermissionScope.Realm && _realmGrants is null)
        {
            _realmGrants = Copy(shown);
            SaveRealm();
        }
        else if (scope == PlayerPermissionScope.Bbs && _bbsGrants is null)
        {
            _bbsGrants = Copy(shown);
        }
        Scope = scope;
        SaveBbs();
    }

    private static Dictionary<string, PlayerGrant> NewList() => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, PlayerGrant> Copy(IEnumerable<KeyValuePair<string, PlayerGrant>> grants)
    {
        Dictionary<string, PlayerGrant> list = NewList();
        foreach ((string given, PlayerGrant grant) in grants)
        {
            // A hand-edited file can't hold a permission the client doesn't know.
            PlayerGrant known = grant with { RemoteControls = grant.RemoteControls & PlayerRemoteControls.All };
            if (!string.IsNullOrEmpty(given) && !known.IsEmpty) list[given] = known;
        }
        return list;
    }

    private void LoadBbs()
    {
        DateTime seen = _bbsStamp.Look(_bbsFile);
        GrantsFile? file = _bbsFile is null ? null : JsonStore.Load<GrantsFile>(_bbsFile);
        _bbsStamp.MarkAs(seen);
        Scope = file?.Scope is { } scope && Enum.IsDefined(scope) ? scope : PlayerPermissionScope.Character;
        _bbsGrants = file?.Players is { } players ? Copy(players) : null;
    }

    private void LoadRealm()
    {
        DateTime seen = _realmStamp.Look(_realmFile);
        GrantsFile? file = _realmFile is null ? null : JsonStore.Load<GrantsFile>(_realmFile);
        _realmStamp.MarkAs(seen);
        _realmGrants = file?.Players is { } players ? Copy(players) : null;
    }

    private void SaveBbs()
    {
        Save(_bbsFile!, new GrantsFile { Scope = Scope, Players = Sorted(_bbsGrants) });
        _bbsStamp.Mark(_bbsFile);
    }

    private void SaveRealm()
    {
        Save(_realmFile!, new GrantsFile { Players = Sorted(_realmGrants) });
        _realmStamp.Mark(_realmFile);
    }

    private static Dictionary<string, PlayerGrant>? Sorted(Dictionary<string, PlayerGrant>? list) =>
        list?.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(p => p.Key, p => p.Value);

    private static void Save(string path, GrantsFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonStore.Save(path, file);
    }
}
