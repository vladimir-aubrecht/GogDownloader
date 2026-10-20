# GameArchiver

Archive games from GOG and Steam through independent C# libraries or a unified API and CLI.
C# / .NET 9, runs on Linux, Windows and macOS. Steam uses SteamKit2 directly.

GOG archives contain the installed DRM-free game. Steam archives contain the selected depot
files; running them may still require Steam or game-specific DRM. Steam portability is not
assumed. Existing GOG commands, snapshots, archives and resumable downloads remain supported.

The GOG provider downloads the *installed* game directly from GOG's Galaxy content
system, chunk by chunk, and assembles it into a single zip named so a library manager like
[GameVault](https://gamevau.lt/) can index it. Downloads resume after an interruption, a new
build reuses the previous backup instead of re-downloading it, and every archive is verified
before it is considered done.

## Build

```sh
dotnet build
dotnet run --project src/GogArchiver.Cli -- <command>
```

The solution is `GameArchiver.sln`. There is no installer; run a CLI through `dotnet run`,
or `dotnet publish` a self-contained binary.
SteamKit2 3.4.0 is the Steam library dependency; GOG and the shared archive engine do not
depend on it. The optional external tool `innoextract` is needed only for old
Windows titles that ship without a Galaxy depot (see below).

## Unified CLI

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

`--source gog|steam|all` defaults to `all`; authentication requires one store. The new snapshot
lives under the application's `gamearchive` configuration directory, separately from the original
GOG snapshot. Use `--snapshot` for the aggregate snapshot and `--gog-snapshot` for the GOG one.
The new CLI can list an existing GOG snapshot, but refuses to overwrite it with the aggregate
format. `diff` is offline by default; `--refresh` asks the stores for the selected version.
`--keys` prints unambiguous `source:ID` identifiers for scripts. A bare ID or slug matching more
than one product is rejected.

A GOG and Steam copy can share one backup root: an existing `factorio/` stays in place and the
second store gets `factorio_steam/` (or `factorio_gog/`, depending on which was first). No numeric
ID is appended. Further collisions require an explicit `--directory NAME`; unidentified folders
are never adopted. Existing directories are found by their metadata identity even after a manual
rename. Games with the same slug within one store remain separate products; choose distinct
folder names explicitly.

Steam currently supports public and other unencrypted branches for which all selected depots
have accessible manifests. Use `--branch`, `--language`, and `--arch 32|64|arm64` to select content.
Steam Guard supports email/device codes and mobile approval. For noninteractive login use
`GAMEARCHIVE_STEAM_USERNAME`, `GAMEARCHIVE_STEAM_PASSWORD` and, when needed,
`GAMEARCHIVE_STEAM_GUARD_CODE`. Passwords are never stored. Remembered refresh/guard tokens are
stored separately from GOG, with mode 0600 from creation on Unix. `--steam-tokens` and
`--gog-tokens` override their paths.

The rename changes project, assembly and namespace names, plus the unified tool command to
`gamearchiver`. Existing configuration/staging directories (`gogdownloader` and `gamearchive`),
environment variables (`GOGDL_SNAPSHOT` and `GAMEARCHIVE_STEAM_*`), metadata filenames and
shared lock filenames keep their original names. Existing logins, snapshots and interrupted
downloads therefore remain usable without migration. C# consumers must update project/package
references and `using` directives, and use `GameArchiverClient` for the aggregate API.

The native Steam path is exercised with controlled session/CDN fixtures. A live login, licensed
depot download and execution on ARM64 still need verification on a real account/device. No
Steam credentials are required to build or run the test suite.

## C# API

Use `GogArchiver.Core` or `SteamArchiver.Core` independently; use `GameArchiver` to combine
them. The providers depend only on `GameArchiver.Core` and their own store protocol. See
[the API guide](docs/api.md) for authentication, ownership, snapshots and embedding examples,
and [the SteamKit2 reference](docs/steamkit2.md) for protocol details and current limits.

## GOG CLI compatibility: getting started

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

Backups live in `{slug}/` folders (lowercase), with the GOG ID stored as `GameId` in
`.gogdl.json`. On the next `download`, existing `{slug}__{id}/` folders are renamed after
checking their metadata; completed archives are reused. `diff` recognizes both layouts
without renaming anything. Interrupted downloads keep using their existing staging data.

Slugs are checked for collisions across the library, including differences only in case.
An existing folder must belong to the same `GameId`; unidentified nonempty folders and
multiple populated folders for one game are reported as conflicts and left untouched.
Resolve the reported conflict before retrying; `--force` does not bypass these checks.

Each legacy `.gogdl.json` also records `"Source": "gog"` at the top level, alongside `GameId`.
The source identifies the store/origin, not the temporary URL of a download. Older manifests
without `Source` are treated as GOG; the next `download` adds it even when the backup is
already current, without fetching the archive again.

The same `--out` root can contain games from other sources. Folders without `.gogdl.json`
and manifests explicitly marked with another source (for example `"Source": "steam"`)
are ignored by the original GOG `diff` and `verify`. The shared CLI also recognizes native
version-2 `.gamebackup.json` archives from supported sources, as does the GameVault sync script.
Foreign metadata does not need to follow the GOG schema. A foreign or unidentified
folder occupying a GOG game's slug remains a conflict: it is never adopted, renamed or
overwritten, even with `--force`. Keep those copies in distinct folders.

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
Space Age). Per-platform `ContentRevision` in `.gogdl.json` records these content validation
rules. Older backups containing DLC, and older Windows installer-based backups, are reported as outdated
by `diff` and rebuilt once on the next download, even at the same build. Depot backups reuse
unchanged verified files from the previous ZIP.

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
`verify --deep --fix` checks all payloads after repairing headers. When an update renames an
archive, the previous archive is kept until the new manifest has been saved successfully.

Installer build identities include the selected installer's language, version and size. `diff`
uses the backup's requested language when comparing installer versions; older installer identities
are refreshed once. The metadata and archive name follow the live installer actually downloaded.

Directory and chained symlinks are copied as ordinary files and directories. Missing targets and
cycles fail before an archive is published. Existing archives produced with missing link content
need a download with `--force`. Generation 1 depot files and Linux installer entries stream into
staging with bounded buffers, including files larger than 2 GiB.

## Original GOG CLI commands

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

## Keeping GameVault's launch executables in sync

GogArchiver does not talk to GameVault. When it backs up a game it records the launcher GOG
declares — from the game's own `goggame-{id}.info` — into that backup's `.gogdl.json`, as neutral
metadata. GameVault stores the launch executable in its own database and cannot read it from an
archive, so without help you would set it by hand in the client for every game.

`scripts/gamevault-sync.sh` is optional glue that reads those manifests and sets each game's
launch executable through the GameVault API, matching a backup to a game by the archive filename
GameVault indexed. It changes only what differs, so it is safe to run on a timer. **The GameVault
dependency lives entirely in that script and your compose file, never in the tool.**

### As a docker compose sidecar

`scripts/gamevault-sync.compose.yml` is a ready service. Drop it beside your GameVault
`docker-compose.yml` (or paste the service into it) and point it at the volume GameVault already
uses for its games:

```yaml
services:
  gamevault-launch-sync:
    image: alpine:3
    restart: unless-stopped
    environment:
      GAMEVAULT_URL: http://gamevault-backend:8080   # the backend service, over the compose network
      GAMEVAULT_USER: ${GAMEVAULT_USER}
      GAMEVAULT_PASSWORD: ${GAMEVAULT_PASSWORD}
      BACKUP_DIR: /files                             # where the .gogdl.json files are
      SYNC_INTERVAL: "3600"                          # seconds between passes; unset = one pass
    volumes:
      - ./gamevault-sync.sh:/gamevault-sync.sh:ro
      - gamevault-files:/files:ro                    # the same volume GameVault indexes
    entrypoint: ["/bin/sh", "-c", "apk add --no-cache curl jq >/dev/null && exec /gamevault-sync.sh"]
```

Three things to line up with your own setup:

1. **`GAMEVAULT_URL`** is the backend as seen from inside the compose network — usually
   `http://<backend-service-name>:<port>`, not `localhost`.
2. **The games volume** (`gamevault-files` above) must be the one GameVault reads. Because the
   `.gogdl.json` sits next to each zip in that same tree, mounting it read-only gives the script
   everything it needs. Adjust the name, or reference it as `external`, to match your setup.
3. **`gamevault-sync.sh`** is bind-mounted from this repo; copy it next to your compose file.

Put the credentials in a `.env` file beside the compose file so they stay out of the YAML:

```
GAMEVAULT_USER=your-editor-user
GAMEVAULT_PASSWORD=your-password
```

Then:

```sh
docker compose up -d gamevault-launch-sync
docker compose logs -f gamevault-launch-sync
```

The GameVault user needs at least the **EDITOR** role, and `curl` + `jq` must be present where
the script runs (the compose entrypoint installs them into `alpine`).

### Try it once first

Before letting it write, run a single dry-run pass — anywhere with `curl` and `jq`:

```sh
DRY_RUN=1 GAMEVAULT_URL=http://localhost:8080 \
  GAMEVAULT_USER=... GAMEVAULT_PASSWORD=... \
  BACKUP_DIR=/path/to/backups \
  scripts/gamevault-sync.sh
```

It prints one line per game (`would set`, `current`, or `skipped (not in GameVault yet)`) plus a
summary, and touches nothing. Drop `DRY_RUN=1` to apply.

### Configuration

Every value can come from an environment variable or a flag; the flag wins.

| Variable | Flag | Meaning |
|---|---|---|
| `GAMEVAULT_URL` | `--url` | Base URL of the backend |
| `GAMEVAULT_USER` | `--user` | A user with the EDITOR role |
| `GAMEVAULT_PASSWORD` | `--password` | That user's password |
| `BACKUP_DIR` | `--backup-dir` | Directory holding the `{slug}/` backups (default `/files`) |
| `SYNC_INTERVAL` | — | Seconds between passes; unset runs one pass and exits |
| `DRY_RUN=1` | `--dry-run` | Report changes without making them |

A game GameVault has not imported yet is reported as `skipped (not in GameVault yet)` and left
alone; the next pass picks it up once GameVault has indexed the zip.

### As a cron job instead

If you would rather not run a container, put `curl` and `jq` on the host and schedule a single
pass (leave `SYNC_INTERVAL` unset):

```cron
0 * * * * GAMEVAULT_URL=http://localhost:8080 GAMEVAULT_USER=svc GAMEVAULT_PASSWORD=secret \
          BACKUP_DIR=/srv/gamevault/files /usr/local/bin/gamevault-sync.sh >> /var/log/gamevault-sync.log 2>&1
```

## Reliability

Work happens in a staging directory; only an atomic move puts the zip into place, and the
`.gogdl.json` manifest records platform completion last — until that entry exists, the platform
counts as not downloaded. An initial manifest with no platforms only reserves the folder for
its `GameId`, so an interruption cannot leave a corrupt "done" state. Native Steam backups use
the same completion rule in `.gamebackup.json`, with `(Source, ProductId)` ownership. The
original GOG CLI asks what to do after Ctrl+C; the shared CLI keeps partial downloads for a
rerun, with `--clean` available to start over. Free space is checked before downloading.

Staging needs space for both the unpacked game and the ZIP being built. `--out` on a large NAS
does not provide space for the default local staging directory; use `--staging DIR` to choose
another disk. On Linux, the check uses that directory's filesystem, and transfers to a different
mount use an interruptible copy followed by an atomic rename at the destination.

## Repository layout

- `src/GameArchiver.Core` — shared contracts, metadata, storage, ZIP, links and verification.
- `src/GogArchiver.Core` — independent GOG client, content system and installers.
- `src/SteamArchiver.Core` — independent Steam client using SteamKit2.
- `src/GameArchiver` — source-qualified library, download, diff and verification API.
- `src/GameArchiver.Cli` — unified CLI; `dotnet pack` creates the `gamearchiver` .NET tool.
- `src/GogArchiver.Cli` — the command-line front end.
- `tests/GogArchiver.Probes` — a console probe suite without a test framework; `scripts/check.sh` runs it
  and the GameVault sync fixture tests (which require `jq`, with HTTP calls mocked).
- `docs/gog-api.md`, `docs/gamevault.md` — verified references for the two undocumented APIs.
- `scripts/` — `check.sh`, `release.sh`, `vendor-ref.sh`, and the GameVault sync script above.

## License

[MIT](LICENSE). `innoextract`, which path C shells out to, is GPLv2 and stays a separate
program — installing it is up to you, and it is not part of this project.
