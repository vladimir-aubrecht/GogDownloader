# CLI guide

[Project overview](../README.md)

Run these commands from the repository root. See the [quick start](../README.md#quick-start) for prerequisites.

## Common commands

```sh
# Authenticate each store separately. Steam passwords are read without echo.
dotnet run --project src/GameArchiver.Cli -- auth login --source gog
dotnet run --project src/GameArchiver.Cli -- auth login --source steam

# Collect both libraries, list products, and download a source-qualified product.
dotnet run --project src/GameArchiver.Cli -- library sync
dotnet run --project src/GameArchiver.Cli -- library list --source steam
dotnet run --project src/GameArchiver.Cli -- download steam:427520 --out /backups

dotnet run --project src/GameArchiver.Cli -- download --all --platform windows --out /backups
dotnet run --project src/GameArchiver.Cli -- diff --out /backups
dotnet run --project src/GameArchiver.Cli -- verify --deep --out /backups
```

## Sources and snapshots

`--source gog|steam|all` defaults to `all`; authentication requires one store. The new snapshot
lives under the application's `gamearchive` configuration directory, separately from the original
GOG snapshot. Use `--snapshot` for the aggregate snapshot and `--gog-snapshot` for the GOG one.
The new CLI can list an existing GOG snapshot, but refuses to overwrite it with the aggregate
format. `diff` is offline by default; `--refresh` asks the stores for the selected version.
`--keys` prints unambiguous `source:ID` identifiers for scripts. A bare ID or slug matching more
than one product is rejected.

`download --all` selects games by default. Applications such as GizmoVR, demos and other
non-game products are skipped. Add `--include-non-games` to include supported non-game products,
or use an explicit `download steam:APPID`. Owned DLC still belongs in its base game's archive;
standalone GOG packs containing games remain eligible. The override does not make unsupported
products downloadable.

## Output folders

A GOG and Steam copy can share one backup root: an existing `factorio/` stays in place and the
second store gets `factorio_steam/` (or `factorio_gog/`, depending on which was first). No numeric
ID is appended. Further collisions require an explicit `--directory NAME`; unidentified folders
are never adopted. Existing directories are found by their metadata identity even after a manual
rename. Games with the same slug within one store remain separate products; choose distinct
folder names explicitly.

To keep each provider in its own folder, add `--layout provider`:

```sh
# /backups/gog/<slug>/ and /backups/steam/<slug>/
dotnet run --project src/GameArchiver.Cli -- download --all --source all --out /backups --layout provider
dotnet run --project src/GameArchiver.Cli -- diff --out /backups --layout provider
dotnet run --project src/GameArchiver.Cli -- verify --deep --out /backups --layout provider

# Only GOG, in /backups/drm_free/<slug>/; provider-dir implies provider layout.
dotnet run --project src/GameArchiver.Cli -- download --all --source gog --out /backups --provider-dir gog=drm_free
```

`--source gog|steam|all` applies to library, download, diff and verify commands. The default
`--layout flat` keeps all game folders directly under `--out`. Repeat `--provider-dir` to
customize multiple provider folder names, for example `--provider-dir gog=drm_free
--provider-dir steam=steam_library`. Unmapped providers use their source names. Names are single
portable directory names, not paths; they must be distinct even ignoring case. `--directory`
still selects one game's folder inside its provider folder.

Use the same layout and folder settings for download, diff and verify. These settings select
the destination; they do not move existing backups. To switch layouts without downloading
again, stop active downloads and move each complete game folder, including its metadata,
into the chosen provider folder. Existing backups there are recognized by identity.

## Steam login and content selection

Steam currently supports public and other unencrypted branches for which all selected depots
have accessible manifests. Use `--branch`, `--language`, and `--arch 32|64|arm64` to select content.
Steam Guard supports email/device codes and mobile approval. For noninteractive login use
`GAMEARCHIVE_STEAM_USERNAME`, `GAMEARCHIVE_STEAM_PASSWORD` and, when needed,
`GAMEARCHIVE_STEAM_GUARD_CODE`. Passwords are never stored. Remembered refresh/guard tokens are
stored separately from GOG, with mode 0600 from creation on Unix. `--steam-tokens` and
`--gog-tokens` override their paths.

Steam makes up to three initial connection attempts within one operation timeout.
Disconnection errors identify whether the connection failed before authentication, during
credentials/Steam Guard, or after logon. After updating the code, rebuild before using
`--no-build` (for example, `dotnet build -c Release`); otherwise the CLI runs the old binary.

## Download a subset by size

Use `--max-size` with `download --all` to select games whose recorded full download estimate
is **below** the limit. When Steam's transfer estimate is missing or predates the corrected
size calculation, the filter uses its recorded installed size instead. Run sync to populate
sizes, then preview:

```sh
dotnet run --project src/GameArchiver.Cli -- library sync --source steam
dotnet run --project src/GameArchiver.Cli -- download --all --source steam --max-size 100MB --dry-run
dotnet run --project src/GameArchiver.Cli -- download --all --source steam --max-size 100MB --out /backups
```

The filter also works with GOG or all providers and with a title/slug search. It uses the last
snapshot without network requests. Steam sizes include the selected OS, language, architecture,
branch and owned DLC, counting a shared depot once. GOG installer estimates use the selected
installer's total size. A DLC selection is resolved to its base game before applying the limit.

Unknown sizes, retained failed-sync metadata and sizes for another language/branch/architecture
are skipped. The summary counts transfer estimates and installed-size fallbacks separately,
and distinguishes outdated estimates, missing sizes, failed syncs and selection mismatches.
`--details` explains each candidate. A new sync populates the default English, public,
64-bit Steam selection. Old Steam transfer estimates are not trusted: some nonempty depots
report a zero download estimate, which must not make a multi-gigabyte game look small. Old
snapshots can still use their independently recorded installed sizes; if those are missing too,
run `library sync --source steam` with the rebuilt version. Some sizes may remain unavailable.
Before transferring game content, Steam checks the actual selected manifest chunk sizes against
the limit; an oversized selection returns `SkippedSize`. GOG rechecks its current estimate.
The limit describes a full transfer, independent of resume/reuse; it is neither the resulting
ZIP size nor a hard network-byte cap (metadata and retries also use bandwidth). An installed-size
fallback is an approximation for selecting small games, not a measured compressed transfer size;
Steam's manifest check still enforces the transfer selection before game content is downloaded.

`100MB` means 100,000,000 bytes; `100MiB` or `100M` means 104,857,600 bytes. Bare numbers mean
bytes. KB/MB/GB/TB use powers of 1000; KiB/MiB/GiB/TiB and K/M/G/T use powers of 1024.
Fractional units such as `0.1GiB` are accepted and rounded down to whole bytes.

## Staging, interruption and recovery

Work happens in a staging directory; only an atomic move puts the zip into place, and the
`gamearchiver.json` manifest records platform completion last — until that entry exists, the platform
counts as not downloaded. An initial manifest with no platforms only reserves the folder for
its `GameId`, so an interruption cannot leave a corrupt "done" state. Native Steam backups use
the same completion rule and filename, with `(Source, ProductId)` ownership. The
original GOG CLI asks what to do after Ctrl+C; the shared CLI keeps partial downloads for a
rerun, with `--clean` available to start over. Free space is checked before downloading.

Staging needs space for both the unpacked game and the ZIP being built. `--out` on a large NAS
does not provide space for the default local staging directory; use `--staging DIR` to choose
another disk. On Linux, the check uses that directory's filesystem, and transfers to a different
mount use an interruptible copy followed by an atomic rename at the destination.

## More commands

For the complete option list, run:

```sh
dotnet run --project src/GameArchiver.Cli -- --help
```

See [library sync and comparison](library.md) for product counts and diagnostics,
[DRM filtering](drm.md) for community reports and manual corrections, and
[archive maintenance](archives.md) for ZIP edits and metadata migration.
The [original GOG CLI](gog-cli.md) remains supported.
