using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// MessageEditExporter diffs the active catalogue against the shipped seed and reports
// what a user changed, keyed by the spell / item a record recognizes — the "Upload edits"
// export the dev ingests to update the seed. Covers: a changed line, a brand-new record,
// an unchanged record (nothing reported), and that Render surfaces the spell number.
public sealed class MessageEditExporterTests
{
    private static MessageRecord Spell(int number, string name, string caster,
        string target = "", string witness = "", string applied = "", string wears = "") =>
        new(Id: "", Name: name, Flags: MessageFlags.None, RawFlagsHex: 0,
            CasterMessage: caster, TargetMessage: target, WitnessMessage: witness,
            AppliedMessage: applied, AppliedEndsWith: wears,
            Links: new[] { new GameDataLink("Spells", number) });

    [Fact]
    public void ChangedLine_IsReportedWithSeedAndNewValue()
    {
        var baseline = new[] { Spell(114, "weapon major bless", "", applied: "Your weapon is blessed!") };
        var current  = new[] { Spell(114, "weapon major bless",
            "You raise your weapon into the air, summoning its power!", applied: "Your weapon is blessed!") };

        IReadOnlyList<MessageEditExporter.RecordEdit> edits =
            MessageEditExporter.Diff(current, baseline);

        MessageEditExporter.RecordEdit e = Assert.Single(edits);
        Assert.Equal("changed", e.Kind);
        Assert.Equal(114, e.SpellNumber);
        MessageEditExporter.FieldEdit caster = Assert.Single(e.Fields, f => f.Field == "Caster");
        Assert.Equal("", caster.Seed);
        Assert.Equal("You raise your weapon into the air, summoning its power!", caster.New);
    }

    [Fact]
    public void NewRecord_NotInSeed_IsReportedAsAdded()
    {
        var baseline = Array.Empty<MessageRecord>();
        var current  = new[] { Spell(500, "custom buff", "You glow.", applied: "You are glowing!") };

        MessageEditExporter.RecordEdit e = Assert.Single(MessageEditExporter.Diff(current, baseline));
        Assert.Equal("added", e.Kind);
        Assert.Equal(500, e.SpellNumber);
        Assert.Contains(e.Fields, f => f.Field == "Caster" && f.Seed is null && f.New == "You glow.");
        Assert.Contains(e.Fields, f => f.Field == "Applied" && f.New == "You are glowing!");
    }

    [Fact]
    public void UnchangedRecord_YieldsNoEdit()
    {
        var rec = Spell(1, "magic missile", "You fire a {spellname} at {target} for {damage} damage!");
        Assert.Empty(MessageEditExporter.Diff(new[] { rec }, new[] { rec }));
    }

    [Fact]
    public void Render_NamesSpellNumber_AndCountsEdits()
    {
        var edits = MessageEditExporter.Diff(
            new[] { Spell(114, "weapon major bless", "raise!", applied: "blessed!") },
            new[] { Spell(114, "weapon major bless", "", applied: "blessed!") });

        string md = MessageEditExporter.Render(edits, "paradigm", "data-Paradigm-1.9.1", new DateTime(2026, 9, 4, 13, 0, 0));

        Assert.Contains("Spell #114", md);
        Assert.Contains("weapon major bless", md);
        Assert.Contains("paradigm", md);
        Assert.Contains("```json", md);              // machine-ingestable block present
        Assert.Contains("\"spell\": 114", md);
    }

    [Fact]
    public void Render_NoEdits_SaysSo()
    {
        string md = MessageEditExporter.Render(
            Array.Empty<MessageEditExporter.RecordEdit>(), "stock", "data-v1.11p", new DateTime(2026, 9, 4, 13, 0, 0));
        Assert.Contains("No message edits", md);
    }
}
