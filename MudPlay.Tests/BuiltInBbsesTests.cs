using System.IO;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The boards MudPlay comes with: added to the BBS list once per install, never on
// top of the same board under another name. The logon setup goes by the realm's
// game data, and no setup carries anyone's account or password.
public sealed class BuiltInBbsesTests : IDisposable
{
    private readonly string _tag = Path.GetRandomFileName().Replace(".", "");
    private readonly BbsProfileStore _store = new();
    private readonly List<string> _made = [];

    public BuiltInBbsesTests() => ScratchRoot.Require();

    public void Dispose()
    {
        foreach (string name in _made) _store.Delete(name);
        foreach (string set in _sets)
        {
            try { Directory.Delete(AppPaths.GameDataSetDir(set), recursive: true); }
            catch (IOException) { /* best-effort cleanup of a scratch folder */ }
        }
    }

    private readonly List<string> _sets = [];

    private string WriteSet(string kind, string custom)
    {
        string set = $"!mudplay-test-{kind}-{_tag}";
        _sets.Add(set);
        string dir = AppPaths.GameDataSetDir(set);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.json"), $$"""[ { "Custom": "{{custom}}", "Legit": 2 } ]""");
        return set;
    }

    private BuiltInBbses.Preset Preset(string name)
    {
        _made.Add(name);
        return new BuiltInBbses.Preset(name, $"{name}.example.org", 2323, null,
            [new MenuStep { WaitForPattern = "login:", Send = "{username}" }]);
    }

    [Fact]
    public void APreset_IsAddedOnce_AndStaysRemovedOnceTheUserRemovesIt()
    {
        BuiltInBbses.Preset preset = Preset($"builtin-{_tag}");
        List<string> added = [];

        Assert.Equal([preset.Name], BuiltInBbses.AddMissing(_store, added, [preset]));

        BbsProfile bbs = _store.Get(preset.Name)!;
        Assert.Equal(preset.Host, bbs.Host);
        Assert.Equal(2323, bbs.Port);
        Assert.Equal(preset.Name, Assert.Single(bbs.Realms).Name);
        Assert.Contains(preset.Name, added);

        _store.Delete(preset.Name);
        Assert.Empty(BuiltInBbses.AddMissing(_store, added, [preset]));
        Assert.False(_store.Exists(preset.Name));
    }

    [Fact]
    public void ABoardAlreadyListedUnderAnotherName_IsNotAddedTwice()
    {
        BuiltInBbses.Preset preset = Preset($"builtin-{_tag}");
        string mine = $"mine-{_tag}";
        _made.Add(mine);
        _store.Save(new BbsProfile { Name = mine, Host = preset.Host!.ToUpperInvariant() + " ", Port = preset.Port });
        List<string> added = [];

        Assert.Empty(BuiltInBbses.AddMissing(_store, added, [preset]));
        Assert.False(_store.Exists(preset.Name));
        Assert.Contains(preset.Name, added);
    }

    // The setup goes by the realm's game data, the board its Info names, never the
    // host and port, which a board can change. Anything else is Stock.
    [Fact]
    public void ASetsBoardName_PicksTheSetup_AndAnythingElseIsStock()
    {
        string anomaly = WriteSet("anomaly", "Anomaly");
        string paradigm = WriteSet("paradigm", "Paradigm");
        string stock = WriteSet("stock", "Default");

        Assert.Equal("Anomaly", BuiltInBbses.ForGameDataSet(anomaly).Name);
        Assert.Equal("Paradigm", BuiltInBbses.ForGameDataSet(paradigm).Name);
        Assert.Same(BuiltInBbses.Stock, BuiltInBbses.ForGameDataSet(stock));
        Assert.Same(BuiltInBbses.Stock, BuiltInBbses.ForGameDataSet($"not-installed-{_tag}"));
        Assert.Same(BuiltInBbses.Stock, BuiltInBbses.ForGameDataSet(null));
        // A realm on the global default goes by the global set.
        Assert.Equal("Paradigm", BuiltInBbses.ForRealm(new RealmProfile(), paradigm).Name);
        Assert.Equal("Anomaly", BuiltInBbses.ForRealm(new RealmProfile { ActiveGameDataSet = anomaly }, paradigm).Name);

        List<MenuStep> steps = BuiltInBbses.Stock.CopyLogonSteps();
        Assert.Equal(["Otherwise type \"new\":", "Enter your password:", "Main System Menu (TOP)"],
            steps.Select(s => s.WaitForPattern));
        Assert.Equal(["{username}", "{password}", "m"], steps.Select(s => s.Send));
        steps[0].Send = "changed";
        Assert.Equal("{username}", BuiltInBbses.Stock.LogonSteps[0].Send);
    }

    // What connect walks. With no steps saved for the board, the setup the realm's
    // data calls for. Saved steps the client filled in, still unedited, give way to
    // the setup installed data calls for the connecting character's realm, and the
    // board keeps its own; with no data to go by they stay, and steps the player
    // applied or edited are never replaced.
    [Fact]
    public void ConnectReplacesOnlyStepsTheClientFilledIn_WhenInstalledDataCallsForAnother()
    {
        string anomaly = WriteSet("anomaly", "Anomaly");
        RealmProfile onAnomaly = new() { ActiveGameDataSet = anomaly };

        (IReadOnlyList<MenuStep> none, string? noneNote) = BuiltInBbses.LogonFor(new BbsProfile(), new RealmProfile(), null);
        Assert.True(BuiltInBbses.Stock.HasSteps(none));
        Assert.NotNull(noneNote);

        BbsProfile filled = new() { LogonSteps = BuiltInBbses.Stock.CopyLogonSteps(), AutoLogonSetup = "Stock" };
        Assert.Null(BuiltInBbses.LogonFor(filled, new RealmProfile(), null).Note);

        (IReadOnlyList<MenuStep> swapped, string? swapNote) = BuiltInBbses.LogonFor(filled, onAnomaly, null);
        Assert.NotNull(swapNote);
        Assert.True(BuiltInBbses.All[0].HasSteps(swapped));
        Assert.True(BuiltInBbses.Stock.HasSteps(filled.LogonSteps!));
        Assert.Equal("Stock", filled.AutoLogonSetup);

        BbsProfile noData = new() { LogonSteps = BuiltInBbses.All[0].CopyLogonSteps(), AutoLogonSetup = "Anomaly" };
        Assert.Null(BuiltInBbses.LogonFor(noData, new RealmProfile { ActiveGameDataSet = $"not-installed-{_tag}" }, null).Note);

        BbsProfile applied = new() { LogonSteps = BuiltInBbses.Stock.CopyLogonSteps() };
        Assert.True(BuiltInBbses.Stock.HasSteps(BuiltInBbses.LogonFor(applied, onAnomaly, null).Steps));

        BbsProfile edited = new() { LogonSteps = BuiltInBbses.Stock.CopyLogonSteps(), AutoLogonSetup = "Stock" };
        edited.LogonSteps[2].Send = "g";
        Assert.Equal("g", BuiltInBbses.LogonFor(edited, onAnomaly, null).Steps[2].Send);
    }

    [Fact]
    public void TheShippedLogonSteps_SendOnlyCredentialTokensAndMenuKeys()
    {
        Assert.Equal(["Anomaly", "Paradigm", "Stock"], BuiltInBbses.Setups.Select(p => p.Name));
        Assert.Null(BuiltInBbses.Stock.Host);
        foreach (MenuStep step in BuiltInBbses.Setups.SelectMany(p => p.LogonSteps))
            Assert.True(step.Send is "{username}" or "{password}" || step.Send.Length == 1,
                $"'{step.Send}' is neither a credential token nor a menu key");
    }
}
