using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// View-model for the Game Data Browser → Monsters tab's per-record edit dialog.
// Surfaces the editable overlay fields (Use-tier, Name, Relationship, Priority, override
// spell slots, DontBackstab) plus the per-monster combat-message bundle
// (9 perspective lines + flavor prefixes) the combat manager will use to recognise lines
// this monster produces in play.
//
// The Messages section binds to a MonsterMessageRecord looked up by monster Number. Each
// of the 9 line fields is a multi-line textbox where every non-empty line is one variant
// pattern (matches the wcc seed shape — e.g. guardsman has 2 hit variants "slash" +
// "all-out slash"; dragon has 4 body-part hit variants). FlavorPrefixes is a single
// comma-separated textbox where the literal token (no prefix) represents the
// AllowNoPrefix flag.
public sealed partial class MonsterEditDialogViewModel : ObservableObject, IDialogViewModel<MonsterEditResult>
{
    public event Action<MonsterEditResult?>? CloseRequested;

    // Sentinel string used in the FlavorPrefixes CSV to represent the AllowNoPrefix flag.
    public const string NoPrefixSentinel = "(no prefix)";

    public string WccNoStr { get; }
    public int    MonsterNumber { get; }

    private readonly MonsterMessageRecord? _originalMessages;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private SettingsTier _useTier = SettingsTier.Character;

    [ObservableProperty] private MonsterRelationship _relationship = MonsterRelationship.Enemy;
    [ObservableProperty] private MonsterAttackPriority _priority = MonsterAttackPriority.Normal;

    [ObservableProperty] private string _preAttackSpellId = string.Empty;
    [ObservableProperty] private string _preAttackCount = string.Empty;
    // "Override Attack" holds a Spell.Number, OR a spell cast-code that resolves
    // to one (both land on the mana-gated spell rung, needs Max), OR a raw verb
    // like "attack"/"bash" that doesn't resolve to any spell (sent as-is, no
    // gating). See ParseAttackOverride.
    [ObservableProperty] private string _attackOverride = string.Empty;
    [ObservableProperty] private string _attackCount = string.Empty;

    [ObservableProperty] private bool _dontBackstab;

    // ----- Messages section (9 line slots + flavor) -----
    // Each line field is one variant per line in a multi-line textbox.
    [ObservableProperty] private string _hitYouText           = string.Empty;
    [ObservableProperty] private string _hitOtherText         = string.Empty;
    [ObservableProperty] private string _deathLineText        = string.Empty;
    [ObservableProperty] private string _armorBlockYouText    = string.Empty;
    [ObservableProperty] private string _armorBlockOtherText  = string.Empty;
    [ObservableProperty] private string _dodgeYouText         = string.Empty;
    [ObservableProperty] private string _dodgeOtherText       = string.Empty;
    [ObservableProperty] private string _missYouText          = string.Empty;
    [ObservableProperty] private string _missOtherText        = string.Empty;

    // Comma-separated flavor list. Literal token (no prefix) (case-insensitive) in the
    // list represents the AllowNoPrefix flag — e.g. giant rat renders as
    // "nasty, angry, large, fat, thin, big, small, (no prefix)".
    [ObservableProperty] private string _flavorPrefixesCsv = string.Empty;

    public IReadOnlyList<MdbInfoRow> MdbInfo { get; }

    public IReadOnlyList<MonsterRelationship> AvailableRelationships { get; } =
        Enum.GetValues<MonsterRelationship>().ToArray();

    public IReadOnlyList<MonsterAttackPriority> AvailablePriorities { get; } =
        Enum.GetValues<MonsterAttackPriority>().ToArray();

    // Tiers the picker offers. Restricted to tiers the resolver can actually write to in
    // the current session (Global always; BBS only with an active BBS; Character only with
    // a loaded profile) so Save can't land on a tier whose scope is unresolvable.
    // Read-only Defaults is excluded — the MDB is its source.
    public IReadOnlyList<SettingsTier> AvailableTiers { get; }

    public string Title => $"Monster — {(Name.Length > 0 ? Name : $"#{WccNoStr}")}";

    // Resolves a typed cast-code (e.g. "turn") to its Spells.Number, or null
    // when the text doesn't match a known spell — see ParseAttackOverride.
    // Optional so the dialog still works (falling back to numeric-only
    // detection) wherever game data isn't wired, e.g. tests.
    private readonly Func<string, int?>? _resolveSpellShort;

    public MonsterEditDialogViewModel(
        string wccNoStr,
        string mdbName,
        MonsterOverlay? existing,
        SettingsTier currentTier,
        IReadOnlyList<MdbInfoRow> mdbInfo,
        MonsterMessageRecord? messages = null,
        IReadOnlyList<SettingsTier>? writableTiers = null,
        Func<string, int?>? resolveSpellShort = null)
    {
        _resolveSpellShort = resolveSpellShort;
        WccNoStr      = wccNoStr;
        MonsterNumber = int.TryParse(wccNoStr, out int n) ? n : 0;
        Name          = existing?.Name ?? mdbName;
        AvailableTiers = writableTiers is { Count: > 0 }
            ? writableTiers
            : Enum.GetValues<SettingsTier>().ToArray();
        // Default to the tier the existing override lives at when it's writable;
        // otherwise the most-specific writable tier (first in the list). Stops
        // the picker defaulting to read-only Defaults or an unloaded Character,
        // which made Save throw "no named profile loaded" and crash the app.
        UseTier       = AvailableTiers.Contains(currentTier) ? currentTier : AvailableTiers[0];
        MdbInfo       = mdbInfo;
        _originalMessages = messages;

        Relationship = existing?.Relationship ?? MonsterRelationship.Enemy;
        Priority     = existing?.Priority     ?? MonsterAttackPriority.Normal;

        PreAttackSpellId = (existing?.OverridePreAttackSpellId is { } pi) ? pi.ToString() : string.Empty;
        PreAttackCount   = (existing?.OverridePreAttackCount   is { } pc) ? pc.ToString() : string.Empty;
        // A command override wins the box display; else fall back to the spell id.
        AttackOverride   = existing?.OverrideAttackCommand is { Length: > 0 } cmd
            ? cmd
            : (existing?.OverrideAttackSpellId is { } ai ? ai.ToString() : string.Empty);
        AttackCount      = (existing?.OverrideAttackCount      is { } ac) ? ac.ToString() : string.Empty;

        DontBackstab = existing?.DontBackstab ?? false;

        // Hydrate the Messages section.
        if (messages is not null)
        {
            HitYouText          = JoinLines(messages.HitYou);
            HitOtherText        = JoinLines(messages.HitOther);
            DeathLineText       = JoinLines(messages.DeathLine);
            ArmorBlockYouText   = JoinLines(messages.ArmorBlockYou);
            ArmorBlockOtherText = JoinLines(messages.ArmorBlockOther);
            DodgeYouText        = JoinLines(messages.DodgeYou);
            DodgeOtherText      = JoinLines(messages.DodgeOther);
            MissYouText         = JoinLines(messages.MissYou);
            MissOtherText       = JoinLines(messages.MissOther);
            FlavorPrefixesCsv   = BuildFlavorCsv(messages.FlavorPrefixes, messages.AllowNoPrefix);
        }
    }

    [RelayCommand]
    private void Save()
    {
        (int? attackSpellId, string? attackCommand) = ParseAttackOverride(AttackOverride, _resolveSpellShort);
        MonsterOverlay overlay = new()
        {
            Name                     = string.IsNullOrWhiteSpace(Name) ? null : Name,
            Relationship             = Relationship,
            Priority                 = Priority,
            OverridePreAttackSpellId = ParseNullableInt(PreAttackSpellId),
            OverridePreAttackCount   = ParseNullableInt(PreAttackCount),
            OverrideAttackSpellId    = attackSpellId,
            OverrideAttackCount      = ParseNullableInt(AttackCount),
            OverrideAttackCommand    = attackCommand,
            DontBackstab             = DontBackstab,
        };

        // Build the message record. Even when there were no original
        // messages (user is authoring from scratch for a never-seeded
        // monster), produce a record IF the user filled in any field
        // OR any flavor prefix. An entirely-blank section returns
        // null so the consumer can skip the upsert.
        MonsterMessageRecord? updatedMessages = BuildUpdatedMessages();

        CloseRequested?.Invoke(new MonsterEditResult(WccNoStr, overlay, UseTier, updatedMessages, _originalMessages));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private MonsterMessageRecord? BuildUpdatedMessages()
    {
        IReadOnlyList<string> hitYou        = SplitLines(HitYouText);
        IReadOnlyList<string> hitOther      = SplitLines(HitOtherText);
        IReadOnlyList<string> deathLine     = SplitLines(DeathLineText);
        IReadOnlyList<string> armorYou      = SplitLines(ArmorBlockYouText);
        IReadOnlyList<string> armorOther    = SplitLines(ArmorBlockOtherText);
        IReadOnlyList<string> dodgeYou      = SplitLines(DodgeYouText);
        IReadOnlyList<string> dodgeOther    = SplitLines(DodgeOtherText);
        IReadOnlyList<string> missYou       = SplitLines(MissYouText);
        IReadOnlyList<string> missOther     = SplitLines(MissOtherText);
        (IReadOnlyList<string> prefixes, bool allowNoPrefix) = ParseFlavorCsv(FlavorPrefixesCsv);

        // Drop the record entirely when every field is empty AND there
        // was no original — saves a no-op write to the per-set file.
        bool anyContent =
            hitYou.Count + hitOther.Count + deathLine.Count +
            armorYou.Count + armorOther.Count +
            dodgeYou.Count + dodgeOther.Count +
            missYou.Count + missOther.Count +
            prefixes.Count > 0 || allowNoPrefix;

        if (!anyContent && _originalMessages is null) return null;

        return new MonsterMessageRecord(
            Id:               ComputeId(Name, hitYou, hitOther, deathLine, armorYou, armorOther,
                                        dodgeYou, dodgeOther, missYou, missOther,
                                        prefixes, allowNoPrefix),
            Name:             Name,
            HitYou:           hitYou,
            HitOther:         hitOther,
            DeathLine:        deathLine,
            ArmorBlockYou:    armorYou,
            ArmorBlockOther:  armorOther,
            DodgeYou:         dodgeYou,
            DodgeOther:       dodgeOther,
            MissYou:          missYou,
            MissOther:        missOther,
            FlavorPrefixes:   prefixes,
            AllowNoPrefix:    allowNoPrefix,
            Links:            new[] { new GameDataLink("Monsters", MonsterNumber) });
    }

    // SHA1 of all editable content joined; matches the offline generator's id rule.
    private static string ComputeId(
        string name,
        IReadOnlyList<string> hitYou,    IReadOnlyList<string> hitOther,    IReadOnlyList<string> death,
        IReadOnlyList<string> armorYou,  IReadOnlyList<string> armorOther,
        IReadOnlyList<string> dodgeYou,  IReadOnlyList<string> dodgeOther,
        IReadOnlyList<string> missYou,   IReadOnlyList<string> missOther,
        IReadOnlyList<string> prefixes,  bool allowNoPrefix)
    {
        IEnumerable<string> all =
            hitYou.Concat(hitOther).Concat(death)
                .Concat(armorYou).Concat(armorOther)
                .Concat(dodgeYou).Concat(dodgeOther)
                .Concat(missYou).Concat(missOther)
                .Concat(prefixes)
                .Append(allowNoPrefix ? "1" : "0");
        string blob = name + "|" + string.Join("|", all);
        byte[] hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(blob));
        System.Text.StringBuilder sb = new(16);
        for (int i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // Multi-line text → list of non-empty trimmed lines.
    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    // List of lines → newline-joined string for the textbox.
    public static string JoinLines(IReadOnlyList<string> lines)
        => lines is { Count: > 0 } ? string.Join("\n", lines) : string.Empty;

    // CSV "nasty, angry, (no prefix)" → (["nasty","angry"], true). The NoPrefixSentinel
    // token (case-insensitive) sets the AllowNoPrefix flag and is dropped from the prefix
    // list. Empty entries skipped; duplicates removed (first-wins).
    public static (IReadOnlyList<string> Prefixes, bool AllowNoPrefix) ParseFlavorCsv(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return (Array.Empty<string>(), false);
        List<string> prefixes = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        bool allowNoPrefix = false;
        foreach (string part in csv.Split(','))
        {
            string token = part.Trim();
            if (token.Length == 0) continue;
            if (string.Equals(token, NoPrefixSentinel, StringComparison.OrdinalIgnoreCase))
            {
                allowNoPrefix = true;
                continue;
            }
            if (seen.Add(token)) prefixes.Add(token);
        }
        return (prefixes, allowNoPrefix);
    }

    // List of prefixes + AllowNoPrefix flag → display CSV with the sentinel appended when the flag is set.
    public static string BuildFlavorCsv(IReadOnlyList<string> prefixes, bool allowNoPrefix)
    {
        IEnumerable<string> parts = prefixes ?? Array.Empty<string>();
        if (allowNoPrefix) parts = parts.Concat(new[] { NoPrefixSentinel });
        return string.Join(", ", parts);
    }

    private static int? ParseNullableInt(string? text)
        => int.TryParse(text, out int n) ? n : null;

    // The "Override Attack" box holds EITHER a Spell.Number (routed through the
    // mana-gated attack-spell rung — needs a Max cast count) OR a raw command /
    // verb like "attack" or "bash" (sent verbatim, no gating). A positive
    // integer reads as a spell id directly; blank is no override.
    //
    // For any other text, resolveSpellShort (when supplied) gets first look: a
    // typed cast-code that matches a known spell (e.g. "turn") resolves to that
    // spell's Number and lands on the SAME mana-gated, cascading rung as typing
    // the number directly — someone reasonably types the code they'd actually
    // cast in-game, not an internal database id they have no way to know,
    // and shouldn't silently lose mana/cap gating for it (report
    // paradigm-20260813-070249: "it just means you use a different spell",
    // not "ignore combat settings completely"). Only text that resolves to no
    // known spell falls through to the raw command path — this is also why
    // typing "attack" persists as a command instead of being silently dropped
    // by an int-only parse (report paradigm-20260809-131642).
    //
    // Exactly one of the pair is set (or both null) — the two are kept mutually
    // exclusive so a species never carries both an id and a command.
    public static (int? SpellId, string? Command) ParseAttackOverride(
        string? text, Func<string, int?>? resolveSpellShort = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        string trimmed = text.Trim();
        if (int.TryParse(trimmed, out int n) && n > 0) return (n, null);
        if (resolveSpellShort?.Invoke(trimmed) is { } resolved) return (resolved, null);
        return (null, trimmed);
    }
}

// Returned by MonsterEditDialogViewModel on Save. WccNoStr is the monster's WCC No as a
// string — primary key for the overlay write; Overlay is the user's edited overlay
// payload; Tier is the tier the overlay should be written at. UpdatedMessages is the
// user's edited monster-message record, or null when the Messages section was entirely
// blank and no original record existed — the consumer upserts via
// MonsterMessageStore.Replace using OriginalMessages?.Id as the replace key.
// OriginalMessages is the record loaded at dialog-open time, or null if no record existed
// for this monster; carried in the result so the consumer can do an Id-keyed replace even
// when the user's edits flipped the projected Id.
public sealed record MonsterEditResult(
    string                WccNoStr,
    MonsterOverlay        Overlay,
    SettingsTier          Tier,
    MonsterMessageRecord? UpdatedMessages = null,
    MonsterMessageRecord? OriginalMessages = null);
