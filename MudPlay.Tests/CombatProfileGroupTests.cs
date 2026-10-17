using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// "Include in combat profile" groups: each Settings header a combat profile can
// carry can be shared across every profile instead.
public sealed class CombatProfileGroupTests
{
    // ----- completeness: every profile field belongs to exactly one group -----

    // Flatten a profile to path → JSON value, descending into the nested settings
    // objects a profile carries.
    private static Dictionary<string, string> Flatten(CombatSpellProfile p)
    {
        var map = new Dictionary<string, string>();
        void Walk(object obj, string prefix)
        {
            foreach (PropertyInfo prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
                object? v = prop.GetValue(obj);
                string path = prefix + prop.Name;
                if (v is HealthSettings or CombatProfileSpells or CombatProfileParty)
                    Walk(v, path + ".");
                else
                    map[path] = JsonSerializer.Serialize(v);
            }
        }
        Walk(p, "");
        map.Remove("Id");
        map.Remove("Name");
        return map;
    }

    // A profile whose every field differs from a fresh one.
    private static CombatSpellProfile Distinct()
    {
        var p = new CombatSpellProfile();
        void Fill(object obj)
        {
            foreach (PropertyInfo prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite || prop.Name is "Id" or "Name") continue;
                Type t = prop.PropertyType;
                object? cur = prop.GetValue(obj);
                if (cur is HealthSettings or CombatProfileSpells or CombatProfileParty) { Fill(cur); continue; }
                if (cur is CombatSpellSlot slot) { slot.SpellName = "x" + prop.Name; continue; }
                if (t == typeof(bool)) prop.SetValue(obj, !(bool)cur!);
                else if (t == typeof(int)) prop.SetValue(obj, (int)cur! + 7);
                else if (t == typeof(string)) prop.SetValue(obj, "x" + prop.Name);
                else if (t.IsEnum)
                {
                    Array values = Enum.GetValues(t);
                    prop.SetValue(obj, values.Cast<object>().First(v => !v.Equals(cur)));
                }
            }
        }
        Fill(p);
        return p;
    }

    [Fact]
    public void EveryProfileField_BelongsToExactlyOneGroup()
    {
        CombatSpellProfile src = Distinct();
        Dictionary<string, string> fresh = Flatten(new CombatSpellProfile());
        Dictionary<string, string> full = Flatten(src);
        HashSet<string> needed = full.Where(kv => fresh[kv.Key] != kv.Value).Select(kv => kv.Key).ToHashSet();
        Assert.Equal(fresh.Count, needed.Count);   // the fixture really changed every field

        var owner = new Dictionary<string, CombatProfileGroup>();
        foreach (CombatProfileGroup g in Enum.GetValues<CombatProfileGroup>())
        {
            var target = new CombatSpellProfile();
            CombatProfileGroupCopy.Copy(g, src, target);
            foreach (var kv in Flatten(target).Where(kv => fresh[kv.Key] != kv.Value))
            {
                Assert.False(owner.ContainsKey(kv.Key), $"{kv.Key} is copied by both {owner.GetValueOrDefault(kv.Key)} and {g}");
                owner[kv.Key] = g;
            }
        }
        Assert.Empty(needed.Except(owner.Keys));   // a field no group copies would never share
    }

    // ----- manager: a live switch -----

    private static (CharacterProfile Profile, CombatProfileManager Mgr, Func<CombatSettings> Live, Func<PartySettings> LiveParty)
        TwoProfiles()
    {
        CombatSettings combat = new();
        HealthSettings health = new();
        SpellsSettings spells = new();
        PartySettings party = new();
        CharacterProfile profile = new()
        {
            CombatProfiles = new CombatProfileSettings
            {
                Profiles =
                {
                    new CombatSpellProfile { Name = "melee", DoBackstab = true, KillAllEngaged = true },
                    new CombatSpellProfile { Name = "caster", DoBackstab = false, KillAllEngaged = false },
                },
                SchemaVersion = CombatProfileSettings.PerProfileRunBlessPartyVersion,
            },
        };
        profile.CombatProfiles.Profiles[0].Party.AoeMinMembers = 2;
        profile.CombatProfiles.Profiles[1].Party.AoeMinMembers = 4;
        profile.CombatProfiles.ActiveId = profile.CombatProfiles.Profiles[0].Id;
        CombatProfileManager mgr = new(
            profile: () => profile,
            readCombat: () => combat, writeCombat: c => combat = c,
            readHealth: () => health, writeHealth: h => health = h,
            readSpells: () => spells, writeSpells: s => spells = s,
            equipment: () => null, save: () => { },
            readParty: () => party, writeParty: p => party = p);
        mgr.SwitchToIndex(0);
        return (profile, mgr, () => combat, () => party);
    }

    [Fact]
    public void Switch_SwapsIncludedGroups()
    {
        var (_, mgr, live, party) = TwoProfiles();
        mgr.SwitchToIndex(1);
        Assert.False(live().DoBackstab);
        Assert.Equal(4, party().AoeMinMembers);
    }

    // What a Settings commit stores for a shared group: the flag, and the active
    // profile's values copied into every profile.
    private static void Share(CharacterProfile profile, params CombatProfileGroup[] groups)
    {
        CombatProfileSettings s = profile.CombatProfiles!;
        s.SharedGroups = groups.ToList();
        CombatProfileGroupCopy.SyncShared(groups, s.Profiles[0], s.Profiles);
    }

    [Fact]
    public void SharedGroup_StaysPutAcrossASwitch()
    {
        var (profile, mgr, live, party) = TwoProfiles();
        Share(profile, CombatProfileGroup.Backstab, CombatProfileGroup.PartyHealing);

        mgr.SwitchToIndex(1);
        Assert.True(live().DoBackstab);                 // shared: unchanged
        Assert.Equal(2, party().AoeMinMembers);         // shared: unchanged
        Assert.False(live().KillAllEngaged);            // Room thresholds still per profile
    }

    [Fact]
    public void SharedGroup_EditedLive_FollowsTheSwitch()
    {
        // An edit made on the Party tab while a shared group is live carries over.
        var (profile, mgr, _, party) = TwoProfiles();
        Share(profile, CombatProfileGroup.PartyHealing);
        party().AoeMinMembers = 5;

        mgr.SwitchToIndex(1);
        Assert.Equal(5, party().AoeMinMembers);
    }

    // ----- Settings-window staging -----

    [Fact]
    public void Staging_SharedFlagsSurviveASave()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        var (store, mgr, _, _) = TwoProfiles();
        profile.Current!.CombatProfiles = store.CombatProfiles;
        CombatProfileManager bound = new(
            profile: () => profile.Current,
            readCombat: () => new CombatSettings(), writeCombat: _ => { },
            readHealth: () => new HealthSettings(), writeHealth: _ => { },
            readSpells: () => new SpellsSettings(), writeSpells: _ => { },
            equipment: () => null, save: () => { });
        using CombatProfileStagingSession session = new(bound, profile, () => null);

        session.SetShared(CombatProfileGroup.Backstab, shared: true);
        session.CommitIfDirty();

        Assert.True(profile.Current.CombatProfiles!.SharedGroups?.Contains(CombatProfileGroup.Backstab) == true);
        Assert.All(profile.Current.CombatProfiles.Profiles, p => Assert.True(p.DoBackstab));
    }

    [Fact]
    public void Staging_ReIncluding_StartsEveryProfileFromTheSharedValue()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        var (store, _, _, _) = TwoProfiles();
        profile.Current!.CombatProfiles = store.CombatProfiles;
        CombatProfileManager bound = new(
            profile: () => profile.Current,
            readCombat: () => new CombatSettings(), writeCombat: _ => { },
            readHealth: () => new HealthSettings(), writeHealth: _ => { },
            readSpells: () => new SpellsSettings(), writeSpells: _ => { },
            equipment: () => null, save: () => { });
        using CombatProfileStagingSession session = new(bound, profile, () => null);

        session.SetShared(CombatProfileGroup.Backstab, shared: true);
        session.SetShared(CombatProfileGroup.Backstab, shared: false);
        session.CommitIfDirty();

        Assert.False(profile.Current.CombatProfiles!.SharedGroups?.Contains(CombatProfileGroup.Backstab) == true);
        Assert.All(profile.Current.CombatProfiles.Profiles, p => Assert.True(p.DoBackstab));
    }

    [Fact]
    public void Staging_ChipSwitch_CarriesSharedGroups()
    {
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        var (store, _, _, _) = TwoProfiles();
        profile.Current!.CombatProfiles = store.CombatProfiles;
        CombatProfileManager bound = new(
            profile: () => profile.Current,
            readCombat: () => new CombatSettings(), writeCombat: _ => { },
            readHealth: () => new HealthSettings(), writeHealth: _ => { },
            readSpells: () => new SpellsSettings(), writeSpells: _ => { },
            equipment: () => null, save: () => { });
        using CombatProfileStagingSession session = new(bound, profile, () => null);

        session.SetShared(CombatProfileGroup.Backstab, shared: true);
        session.Active.DoBackstab = false;              // edited on screen after sharing
        session.SwitchTo(1);

        Assert.False(session.Active.DoBackstab);        // the shared value followed the switch
        Assert.False(session.Active.KillAllEngaged);    // per-profile stays the caster's
    }

    [Fact]
    public void Staging_PartyTabFollowsTheChips()
    {
        // The Party tab stages its party healing like the other tabs: an edit made
        // before a chip switch stays with that profile, the switch loads the other
        // profile's value into the tab, and the commit makes the active one live.
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        var (store, _, _, _) = TwoProfiles();
        profile.Current!.CombatProfiles = store.CombatProfiles;
        PartySettings live = new() { AoeMinMembers = 2 };
        CombatProfileManager bound = new(
            profile: () => profile.Current,
            readCombat: () => new CombatSettings(), writeCombat: _ => { },
            readHealth: () => new HealthSettings(), writeHealth: _ => { },
            readSpells: () => new SpellsSettings(), writeSpells: _ => { },
            equipment: () => null, save: () => { },
            readParty: () => live, writeParty: p => live = p);
        using CombatProfileStagingSession session = new(bound, profile, () => null);
        int partyBox = live.AoeMinMembers;                        // the tab's box, loaded from live
        session.CaptureRequested += () => session.Active.Party.AoeMinMembers = partyBox;
        session.LoadRequested += () => partyBox = session.Active.Party.AoeMinMembers;

        partyBox = 3;                                             // edited on profile 1
        session.SwitchTo(1);
        Assert.Equal(4, partyBox);                                // the tab now shows profile 2's

        session.CommitIfDirty();
        Assert.Equal(3, profile.Current.CombatProfiles!.Profiles[0].Party.AoeMinMembers);
        Assert.Equal(4, live.AoeMinMembers);
    }

    [Fact]
    public void Staging_StartsTheActiveProfileFromTheLiveParty()
    {
        // A live Party-tab value the profile copy hasn't caught up with is what the
        // tab shows, so the working copy starts from it.
        ProfileService profile = TestProfiles.Service();
        profile.LoadDefaultProfile();
        var (store, _, _, _) = TwoProfiles();
        profile.Current!.CombatProfiles = store.CombatProfiles;
        PartySettings live = new() { AoeMinMembers = 5 };
        CombatProfileManager bound = new(
            profile: () => profile.Current,
            readCombat: () => new CombatSettings(), writeCombat: _ => { },
            readHealth: () => new HealthSettings(), writeHealth: _ => { },
            readSpells: () => new SpellsSettings(), writeSpells: _ => { },
            equipment: () => null, save: () => { },
            readParty: () => live, writeParty: p => live = p);
        using CombatProfileStagingSession session = new(bound, profile, () => null);

        Assert.Equal(5, session.Active.Party.AoeMinMembers);
    }
}
