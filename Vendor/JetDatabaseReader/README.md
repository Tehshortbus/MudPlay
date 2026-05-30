# Vendored: JetDatabaseReader

Source: <https://github.com/diegoripera/JetDatabaseReader>
Version: **2.2.0**
License: MIT (see upstream `LICENSE`)

## Why vendored

We need a pure-managed Jet (.mdb / .accdb) reader on Linux / macOS without
shelling out to `mdbtools` or requiring a Wine prefix. JetDatabaseReader is
the most mature candidate on NuGet — the only blocker is one false-positive
encryption check that rejects MajorMUD-distribution MDBs.

Vendoring (rather than depending on the NuGet) lets us patch the one line
without forking the project on GitHub or waiting for an upstream release.
When upstream fixes the issue we drop this folder and re-add the NuGet
package reference.

## Local patches

Search for `[FUJINTERM PATCH]` in the source to find every modification.

1. **`Core/AccessReader.cs`** — disable the `hdr[0x62] & 0x03` encryption
   flag check. Jet4 page-0 bytes 0x18–0x97 are XOR-obfuscated against a
   per-file salt; reading byte 0x62 raw misinterprets ordinary data as an
   encryption flag and rejects MDBs that any other Jet reader opens
   cleanly. Truly password-protected MDBs still fail downstream when the
   catalog read produces garbled bytes — strictly better than the current
   false-positive lockout.

## Compile setup

- Every file is prefixed with `#nullable disable` + `#pragma warning disable`
  so the vendored source (authored for `Nullable=disable` / `LangVersion=7.3`)
  compiles cleanly under FujinTerm's `Nullable=enable` /
  `TreatWarningsAsErrors=true` settings without diff-noise from inserting
  modern annotations.
- Files auto-include via the .NET SDK's default Compile glob — no explicit
  `<Compile Include>` needed in `FujinTerm.csproj`.
- Adds one transitive package: `System.Text.Encoding.CodePages` (registered
  for code-page resolution in `AccessReader`'s static ctor).

## How to refresh from upstream

1. `git clone --depth 1 https://github.com/diegoripera/JetDatabaseReader.git /tmp/jdr`
2. Diff `/tmp/jdr/JetDatabaseReader/**/*.cs` against this folder.
3. Re-apply the `[FUJINTERM PATCH]` blocks above.
4. Bump the version above.
