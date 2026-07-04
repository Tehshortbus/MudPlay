using System.Collections.Generic;

namespace FujinTerm.Models.Import;

// One row-level collision between an incoming import and the existing
// store. Carries enough context for ImportConflictViewModel to show a
// per-field diff without having to know what kind of import is running —
// importers (MDB tables, MegaMUD spell messages, favourites, MegaMUD .mp
// loops) all describe their conflicts using this shape.
//
// Category is what kind of thing this conflict is about — usually a table
// or collection name (e.g. "Monsters", "Spell Messages",
// "Favourites / Cities"). Surfaced as a group header in the dialog's left
// rail. Identifier is the stable row key — the importer's natural primary
// key (e.g. monster id, spell name, favourite path), shown in the dialog's
// list rail and reused on ImportResolution. Existing maps field name →
// human-readable value of the record currently in the store (null values
// represent missing-field-on-existing — rare; usually means the existing
// row predates a schema add). Incoming is the same schema for the row being
// imported; the dialog diffs row-by-row.
public sealed record ImportConflict(
    string Category,
    string Identifier,
    IReadOnlyDictionary<string, string?> Existing,
    IReadOnlyDictionary<string, string?> Incoming);

// What the user picked for one ImportConflict.
public enum ImportAction
{
    // Drop the incoming row; keep the existing one untouched.
    Skip,

    // Replace the existing row wholesale with the incoming one.
    Overwrite,

    // Combine the two — incoming fills any field the existing row has as
    // null / empty, the existing row keeps every non-null field. The
    // importer applies the actual merge; this enum only records the user's
    // intent.
    Merge,

    // Keep the existing row AND keep the incoming row, but rename the
    // incoming one to ImportResolution.RenameTo so the two coexist.
    Rename,
}

// The user's decision for one conflict. Returned from the dialog as part
// of ImportConflictResult; the importer reads Action + RenameTo and applies
// them. Conflict is the same instance the dialog was handed. RenameTo is
// the new identifier when Action is Rename, null otherwise — the importer
// must validate uniqueness before applying; the dialog only captures the
// typed value.
public sealed record ImportResolution(
    ImportConflict Conflict,
    ImportAction Action,
    string? RenameTo);

// Aggregate result returned from ImportConflictDialog. The dialog returns
// one resolution per input conflict (preserved input order) on commit;
// null task result on cancel. Resolutions are the per-conflict decisions,
// in the same order the conflicts were supplied.
public sealed record ImportConflictResult(
    IReadOnlyList<ImportResolution> Resolutions);
