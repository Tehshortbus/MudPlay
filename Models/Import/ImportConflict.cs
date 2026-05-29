using System.Collections.Generic;

namespace FujinTerm.Models.Import;

/// <summary>
/// One row-level collision between an incoming import and the existing
/// store. Carries enough context for the
/// <see cref="ViewModels.Import.ImportConflictViewModel"/> to show a
/// per-field diff without having to know what kind of import is
/// running — importers (MDB tables, MegaMUD spell messages,
/// favourites, MegaMUD <c>.mp</c> loops) all describe their conflicts
/// using this shape.
/// </summary>
/// <param name="Category">
/// What kind of thing this conflict is about — usually a table or
/// collection name (e.g. <c>"Monsters"</c>, <c>"Spell Messages"</c>,
/// <c>"Favourites / Cities"</c>). Surfaced as a group header in the
/// dialog's left rail.
/// </param>
/// <param name="Identifier">
/// Stable row key — the importer's natural primary key (e.g. monster
/// id, spell name, favourite path). Shown in the dialog's list rail
/// and reused on <see cref="ImportResolution"/>.
/// </param>
/// <param name="Existing">
/// Field name → human-readable value of the record currently in the
/// store. <c>null</c> values represent missing-field-on-existing
/// (rare; usually means the existing row predates a schema add).
/// </param>
/// <param name="Incoming">
/// Field name → human-readable value of the row being imported. Same
/// schema as <see cref="Existing"/>; the dialog diffs row-by-row.
/// </param>
public sealed record ImportConflict(
    string Category,
    string Identifier,
    IReadOnlyDictionary<string, string?> Existing,
    IReadOnlyDictionary<string, string?> Incoming);

/// <summary>What the user picked for one <see cref="ImportConflict"/>.</summary>
public enum ImportAction
{
    /// <summary>Drop the incoming row; keep the existing one untouched.</summary>
    Skip,

    /// <summary>Replace the existing row wholesale with the incoming one.</summary>
    Overwrite,

    /// <summary>
    /// Combine the two — incoming fills any field the existing row has
    /// as <c>null</c> / empty, the existing row keeps every non-null
    /// field. The importer applies the actual merge; this enum only
    /// records the user's intent.
    /// </summary>
    Merge,

    /// <summary>
    /// Keep the existing row AND keep the incoming row, but rename the
    /// incoming one to <see cref="ImportResolution.RenameTo"/> so the
    /// two coexist.
    /// </summary>
    Rename,
}

/// <summary>
/// The user's decision for one conflict. Returned from the dialog as
/// part of <see cref="ImportConflictResult"/>; the importer reads
/// <see cref="Action"/> + <see cref="RenameTo"/> and applies them.
/// </summary>
/// <param name="Conflict">The conflict this resolution belongs to (same instance the dialog was handed).</param>
/// <param name="Action">Which of the four operations to perform.</param>
/// <param name="RenameTo">
/// New identifier when <see cref="Action"/> is <see cref="ImportAction.Rename"/>;
/// <c>null</c> otherwise. The importer must validate uniqueness before
/// applying; the dialog only captures the typed value.
/// </param>
public sealed record ImportResolution(
    ImportConflict Conflict,
    ImportAction Action,
    string? RenameTo);

/// <summary>
/// Aggregate result returned from <c>ImportConflictDialog</c>. The
/// dialog returns one resolution per input conflict (preserved input
/// order) on commit; <c>null</c> task result on cancel.
/// </summary>
/// <param name="Resolutions">Per-conflict decisions, in the same order the conflicts were supplied.</param>
public sealed record ImportConflictResult(
    IReadOnlyList<ImportResolution> Resolutions);
