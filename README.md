# GogDownloader

Back up the games you own on GOG into portable, self-contained zips. C# / .NET 9, runs on
Linux, Windows and macOS.

It downloads the *installed* game — not the installer — directly from GOG's Galaxy content
system, chunk by chunk, and assembles it into a single zip named so a library manager like
[GameVault](https://gamevau.lt/) can index it. Downloads resume after an interruption, a new
build reuses the previous backup instead of re-downloading it, and every archive is verified
before it is considered done.

## Build

```sh
dotnet build
dotnet run --project src/GogDownloader.Cli -- <command>
```

There is no installer; run it through `dotnet run`, or `dotnet publish` a self-contained binary.
No NuGet dependencies. The one optional external tool is `innoextract`, needed only for old
Windows titles that ship without a Galaxy depot (see below).

## Getting started

```sh
# 1. Log in through your browser (paste back the URL you land on).
dotnet run --project src/GogDownloader.Cli -- auth login

# 2. Fetch your library and save a local snapshot.
dotnet run --project src/GogDownloader.Cli -- library sync

# 3. See what you own and how each title can be backed up.
dotnet run --project src/GogDownloader.Cli -- library list

# 4. Back one game up...
dotnet run --project src/GogDownloader.Cli -- download the_witcher_3_wild_hunt --platform windows

# 5. ...or every Windows game at once, into a chosen folder.
dotnet run --project src/GogDownloader.Cli -- download --all --platform windows --out /backups
```

Add `--dry-run` to `download --all` to see the selection and total size without downloading
anything.

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

## Keeping GameVault's launch executables in sync

GogDownloader does not talk to GameVault. When it backs up a game it records the launcher GOG
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
| `BACKUP_DIR` | `--backup-dir` | Directory holding the `{slug}__{id}/` backups (default `/files`) |
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
`.gogdl.json` manifest is written last — until it exists, the game counts as not downloaded, so a
crash never leaves a corrupt "done" state. Ctrl+C asks what to do with a partial download and the
next run resumes it. Free space is checked before downloading, not discovered an hour in.

Staging needs space for both the unpacked game and the ZIP being built. `--out` on a large NAS
does not provide space for the default local staging directory; use `--staging DIR` to choose
another disk. On Linux, the check uses that directory's filesystem, and transfers to a different
mount use an interruptible copy followed by an atomic rename at the destination.

## Repository layout

- `src/GogDownloader.Core` — the library: GOG API, content system, download, zip writer.
- `src/GogDownloader.Cli` — the command-line front end.
- `tests/GogDownloader.Probes` — a dependency-free probe suite; `scripts/check.sh` runs it.
- `docs/gog-api.md`, `docs/gamevault.md` — verified references for the two undocumented APIs.
- `scripts/` — `check.sh`, `release.sh`, `vendor-ref.sh`, and the GameVault sync script above.

## License

[MIT](LICENSE). `innoextract`, which path C shells out to, is GPLv2 and stays a separate
program — installing it is up to you, and it is not part of this project.
