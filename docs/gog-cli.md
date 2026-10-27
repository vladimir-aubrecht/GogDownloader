# Original GOG CLI and backup compatibility

[Project overview](../README.md)

The standalone `GogArchiver.Cli` preserves the original GOG workflow. For new installations,
start with the [unified CLI](cli.md), which handles both GOG and Steam. Commands below run
from the repository root and use the original GOG snapshot.

## Quick start

```sh
# 1. Log in through your browser (paste back the URL you land on).
dotnet run --project src/GogArchiver.Cli -- auth login

# 2. Fetch your library and save a local snapshot.
dotnet run --project src/GogArchiver.Cli -- library sync

# 3. See what you own and how each title can be backed up.
dotnet run --project src/GogArchiver.Cli -- library list

# 4. Back one game up...
dotnet run --project src/GogArchiver.Cli -- download the_witcher_3_wild_hunt --platform windows

# 5. ...or every Windows game at once, into a chosen folder.
dotnet run --project src/GogArchiver.Cli -- download --all --platform windows --out /backups
```

Add `--dry-run` to `download --all` to see the selection and total size without downloading
anything.

## Folders and metadata

Backups live in `{slug}/` folders (lowercase), with the GOG ID stored as `GameId` in
`gamearchiver.json`. On the next `download`, existing `{slug}__{id}/` folders are renamed after
checking their metadata; completed archives are reused. `diff` recognizes both layouts
without renaming anything. Interrupted downloads keep using their existing staging data.

Slugs are checked for collisions across the library, including differences only in case.
An existing folder must belong to the same `GameId`; unidentified nonempty folders and
multiple populated folders for one game are reported as conflicts and left untouched.
Resolve the reported conflict before retrying; `--force` does not bypass these checks.

Each GOG `gamearchiver.json` records `"Source": "gog"` at the top level, alongside `GameId`.
The source identifies the store/origin, not the temporary URL of a download. Older manifests
named `.gogdl.json` without `Source` are treated as GOG; the next `download` migrates them even when the backup is
already current, without fetching the archive again.

The same `--out` root can contain games from other sources. Folders without recognized metadata
and manifests explicitly marked with another source (for example `"Source": "steam"`)
are ignored by the GOG `diff` and `verify`. The shared CLI and GameVault sync script recognize
both providers under the canonical name and the old names.
Foreign metadata does not need to follow the GOG schema. A foreign or unidentified
folder occupying a GOG game's slug remains a conflict: it is never adopted, renamed or
overwritten, even with `--force`. Keep those copies in distinct folders.

## DLC and existing backups

Owned DLC is included in the base game's Galaxy backup. When a library also lists that DLC
as a separate product, selecting it (by slug, search, or `--all`) selects the base game instead
and explains the substitution. The base game is downloaded only once. This requires a known
base game with a depot for the selected platform; installer-only backups do not merge DLC.

After upgrading from a version that produced tiny standalone DLC archives, run `library sync`
to refresh the per-platform DLC information, then use `diff` or download the base game again.
A backup missing owned DLC is rebuilt even if its build number is unchanged, reusing verified
files from the previous ZIP. A missing or size-mismatched ZIP is also downloaded again.
Old standalone DLC archives are left untouched; check the completed base backup before removing
them. `verify --deep` checks ZIP integrity, not whether a standalone DLC contains the full game.

DLC files override base-game files at the same path (including the executable in Factorio:
Space Age). Per-platform `ContentRevision` in `gamearchiver.json` records these content validation
rules. Older backups containing DLC, and older Windows installer-based backups, are reported as outdated
by `diff` and rebuilt once on the next download, even at the same build. Depot backups reuse
unchanged verified files from the previous ZIP.

## Resume and content validation

When resuming depot or Linux installer downloads, files without a whole-file content hash are
downloaded again. Staging is reconciled with the current file plan, removing obsolete paths and
case-only duplicates while retaining files whose size and hash match.

Interrupted Windows installer downloads only reuse parts belonging to the exact installer
version, language and file list. Unidentified or mismatched caches are discarded, and `--clean`
also removes the installer cache. An incomplete part never becomes a reusable completed file.

Resuming an interrupted Inno backup replaces staging with a fresh successful extraction,
including nested directories. Cancelling extraction terminates the running extractor's process
tree and cancels output reads, even if a launcher has exited with a descendant holding a pipe.
Build API outages are reported as errors rather than a missing Galaxy depot.

`--limit-rate` applies to depot content and both installer paths, including Linux installer
metadata reads. All transfers share the same bandwidth budget and can be cancelled while waiting.
Archive verification compares content with staging and with the CRC recorded in the ZIP.
`verify --deep --fix` checks all payloads after repairing headers. Default verification also rejects
malformed or incomplete central directories. GOG updates preserve the previous archive until the
new manifest commits, including when the version label is unchanged: a colliding filename gets a
unique `(revision-<id>)` suffix. Failed or cancelled publication removes only the uncommitted ZIP.

Installer build identities include the selected installer's language, version and size. `diff`
uses the backup's requested language when comparing installer versions; older installer identities
are refreshed once. The metadata and archive name follow the live installer actually downloaded.

## Links and Unix permissions

Directory and chained symlinks are copied as ordinary files and directories. Missing targets and
cycles fail before an archive is published. Existing archives produced with missing link content
need a download with `--force`. Generation 1 depot files and Linux installer entries stream into
staging with bounded buffers, including files larger than 2 GiB. Linux installers with more than
65,535 entries use the complete ZIP64 inventory.

Archives preserve ordinary Unix rwx permissions from Linux installers and executable flags from
GOG and Steam depots, even when downloading on Windows. Dereferenced links inherit the target's
permissions; archive edits retain permissions on replaced files and their backups. Older Linux/macOS
backups are rebuilt once on the next download (`ContentRevision: 2`), reusing verified depot files
where possible. Existing Windows revision-1 backups do not need a permissions rebuild.

## Commands

| Command | What it does |
|---|---|
| `auth login` / `status` / `logout` | Log in via browser, check the token, forget it |
| `library sync` | Fetch the library from GOG and save a snapshot |
| `library list [text]` | List games, slugs, sizes and how each can be backed up |
| `library stats` | Breakdown by how titles can be obtained |
| `download <slug\|id>` | Back up one game into a zip |
| `download --all [text]` | Back up every matching game (filters: `--platform`, `--dlc`, `--dry-run`) |
| `diff [text]` | Compare a backup directory against the library: what is missing or outdated |
| `verify [text]` | Check the archives already in a backup directory (`--fix` repairs, `--deep` inflates every entry) |

Useful download flags: `--platform windows\|mac\|linux`, `--language en`, `--out DIR`,
`--staging DIR`, `--connections N`, `--limit-rate 5M`, `--compression-threads N`, `--force`,
`--clean` / `--keep`, `--no-verify`, `--no-reuse`, `--no-launcher`. Any command takes
`--snapshot FILE` to run against a specific library snapshot.

## How each game is fetched

GOG delivers different games in different ways, and the tool picks the right one per platform:

- **Galaxy depots (Windows, macOS)** — the installed files, downloaded in chunks. Both the
  current (generation 2) and legacy (generation 1) depot formats are supported.
- **Linux** — GOG has no Linux depots. The game is read straight out of the MojoSetup installer
  over HTTP Range, so the installer is never downloaded in full.
- **Windows without a depot** — old classics whose only download is an Inno Setup installer.
  These are downloaded, extracted with [`innoextract`](https://constexpr.org/innoextract/), and
  the installer is discarded. This is the only case that needs an external tool.
- **macOS without a depot** — not supported; those installers are `.pkg` inside `.dmg`.

Each archive is named `Title (vVersion) (EA) (GameType).zip` for GameVault, with a `start.bat`
written into the root (from the game's own launch metadata) so it runs the same way everywhere;
Linux backups already carry a `start.sh`.

See [archive metadata migration](archives.md#visible-archive-metadata) and the [GOG API reference](gog-api.md) for more detail.
