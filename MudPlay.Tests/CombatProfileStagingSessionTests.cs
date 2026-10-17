using System.Linq;
using MudPlay.Game.Combat;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

public sealed class CombatProfileStagingSessionTests
{
    private static (ProfileService Profile, CombatProfileManager Mgr) Setup()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        CharacterProfile p = profile.Current!;
        p.CombatProfiles = new CombatProfileSettings
        {
            Profiles =
            {
                new CombatSpellProfile { Name = "melee", Health = new HealthSettings { RestMaxHp = 90 } },
                new CombatSpellProfile { Name = "caster", Health = new HealthSettings { RestMaxHp = 60 } },
            },
            SchemaVersion = CombatProfileSettings.PerProfileRunBlessPartyVersion,
        };
        p.CombatProfiles.ActiveId = p.CombatProfiles.Profiles[0].Id;
        CombatProfileManager mgr = new(
            profile: () => profile.Current,
            readCombat: () => new CombatSettings(), writeCombat: _ => { },
            readHealth: () => new HealthSettings { RestMaxHp = 90 }, writeHealth: _ => { },
            readSpells: () => new SpellsSettings(), writeSpells: _ => { },
            equipment: () => null, save: () => { });
        return (profile, mgr);
    }

    // A Settings save rebuilt the CombatProfiles blob without its SchemaVersion, so
    // the next profile load re-ran the one-time back-fills and overwrote every
    // profile's Health / spells / weapons / action order / backstab with the active
    // one's.
    [Fact]
    public void Commit_KeepsTheSchemaVersion_SoTheBackfillNeverReRuns()
    {
        (ProfileService profile, CombatProfileManager mgr) = Setup();
        using CombatProfileStagingSession session = new(mgr, profile, () => null);

        session.MarkDirty();
        session.CommitIfDirty();
        mgr.EnsureSeeded();      // what the next ProfileLoaded does

        CombatProfileSettings store = profile.Current!.CombatProfiles!;
        Assert.Equal(CombatProfileSettings.PerProfileRunBlessPartyVersion, store.SchemaVersion);
        Assert.Equal(60, store.Profiles.Single(x => x.Name == "caster").Health.RestMaxHp);
    }
}
