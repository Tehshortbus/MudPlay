using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// The realm-wide and BBS-wide lists of players' remote settings, and which list a
// character uses. A choice reaches only the characters it names, so each records when
// it was made and a character follows the latest one naming it. That lets a wider
// choice override characters that aren't running without writing to their profiles.
// The BBS's file holds the numbering and the BBS list, each realm's file its list, and
// each profile its own choice. Every client on the BBS reads the same files, so they
// are followed the way the realm's other shared files are.
public sealed class SharedPlayerGrants
{
    private sealed class GrantsFile
    {
        // The one choice older versions kept for the whole BBS. It stands for any list
        // not chosen since.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PlayerPermissionScope? Scope { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? Id { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long LastPick { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Picked { get; set; }

        // Null until the list is first used, which is what lets the first use start
        // from the table on show without a later switch undoing a revoke.
        public Dictionary<string, PlayerGrant>? Players { get; set; }
    }

    public static string Describe(PlayerPermissionScope scope) => scope switch
    {
        PlayerPermissionScope.Realm => "on this realm's shared list",
        PlayerPermissionScope.Bbs   => "on this BBS's shared list",
        _                           => "for this character only",
    };

    private readonly SharedFileStamp _bbsStamp = new();
    private readonly SharedFileStamp _realmStamp = new();
    private string? _bbsFile;
    private string? _realmFile;
    private Dictionary<string, PlayerGrant>? _bbsGrants;
    private Dictionary<string, PlayerGrant>? _realmGrants;
    private PlayerPermissionScope? _olderScope;
    private Guid? _bbsId;
    private long _lastPick;
    private long? _bbsPicked;
    private long? _realmPicked;

    public PlayerPermissionPick? OwnPick { get; private set; }

    private long BbsPick => _bbsPicked ?? (_olderScope == PlayerPermissionScope.Bbs ? 1 : 0);

    // An older realm-wide choice left realms nobody had granted on with an empty list.
    // Those characters keep their own rather than being held to nothing.
    private long RealmPick => _realmPicked
        ?? (_olderScope == PlayerPermissionScope.Realm && _realmGrants is not null ? 1 : 0);

    private long OwnPickNumber => OwnPick is { } own && own.Bbs == _bbsId ? own.Number : 0;

    public PlayerPermissionScope Scope
    {
        get
        {
            if (!Available) return PlayerPermissionScope.Character;
            if (BbsPick > RealmPick && BbsPick > OwnPickNumber) return PlayerPermissionScope.Bbs;
            return RealmPick > OwnPickNumber ? PlayerPermissionScope.Realm : PlayerPermissionScope.Character;
        }
    }

    // Higher is later; the character follows the highest.
    public string PickSummary => $"BBS {BbsPick}, this realm {RealmPick}, this character {OwnPickNumber}";

    // False with no BBS and realm resolved: there is nothing to share across.
    public bool Available => _bbsFile is not null && _realmFile is not null;

    public bool IsShared => Scope != PlayerPermissionScope.Character;

    private Dictionary<string, PlayerGrant>? ActiveList =>
        Scope == PlayerPermissionScope.Bbs ? _bbsGrants : _realmGrants;

    public void Attach(string? bbsFolder, string? realmFolder, PlayerPermissionPick? ownPick)
    {
        bool usable = !string.IsNullOrEmpty(bbsFolder) && !string.IsNullOrEmpty(realmFolder);
        _bbsFile   = usable ? AppPaths.PlayerPermissionsFile(bbsFolder!) : null;
        _realmFile = usable ? AppPaths.PlayerPermissionsFile(realmFolder!) : null;
        OwnPick = ownPick;
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
        bool bbsList = Scope == PlayerPermissionScope.Bbs;
        Dictionary<string, PlayerGrant> list = ActiveList ?? NewList();
        if (grant.IsEmpty) list.Remove(given);
        else list[given] = grant;
        if (bbsList) { _bbsGrants = list; SaveBbs(); }
        else { _realmGrants = list; SaveRealm(); }
    }

    // A list used for the first time starts from what the table shows, so widening
    // keeps the grants on screen. One used before keeps its own contents.
    public void ChangeScope(PlayerPermissionScope scope, IEnumerable<KeyValuePair<string, PlayerGrant>> characterGrants)
    {
        if (!Available || scope == Scope || !Enum.IsDefined(scope)) return;
        IEnumerable<KeyValuePair<string, PlayerGrant>> shown =
            IsShared ? ActiveList ?? NewList() : characterGrants;

        long pick = new[] { _lastPick, BbsPick, RealmPick, OwnPickNumber }.Max() + 1;
        _lastPick = pick;
        _bbsId ??= Guid.NewGuid();

        if (scope == PlayerPermissionScope.Bbs)
        {
            _bbsGrants ??= Copy(shown);
            _bbsPicked = pick;
        }
        // The numbering is written first: a number a crash left unused is harmless,
        // one issued twice could tie two choices.
        SaveBbs();
        if (scope == PlayerPermissionScope.Realm)
        {
            _realmGrants ??= Copy(shown);
            _realmPicked = pick;
            SaveRealm();
        }
        else if (scope == PlayerPermissionScope.Character)
        {
            OwnPick = new PlayerPermissionPick(_bbsId.Value, pick);
        }
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
        _olderScope = file?.Scope is { } scope && Enum.IsDefined(scope) ? scope : null;
        _bbsId = file?.Id;
        // Past the 1 an older choice counts as, so a new choice always outranks it.
        _lastPick = Math.Max(file?.LastPick ?? 0, _olderScope is null ? 0 : 1);
        _bbsPicked = file?.Picked;
        _bbsGrants = file?.Players is { } players ? Copy(players) : null;
    }

    private void LoadRealm()
    {
        DateTime seen = _realmStamp.Look(_realmFile);
        GrantsFile? file = _realmFile is null ? null : JsonStore.Load<GrantsFile>(_realmFile);
        _realmStamp.MarkAs(seen);
        _realmPicked = file?.Picked;
        _realmGrants = file?.Players is { } players ? Copy(players) : null;
    }

    private void SaveBbs()
    {
        Save(_bbsFile!, new GrantsFile
        {
            Scope = _olderScope, Id = _bbsId, LastPick = _lastPick,
            Picked = _bbsPicked, Players = Sorted(_bbsGrants),
        });
        _bbsStamp.Mark(_bbsFile);
    }

    private void SaveRealm()
    {
        Save(_realmFile!, new GrantsFile { Picked = _realmPicked, Players = Sorted(_realmGrants) });
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
