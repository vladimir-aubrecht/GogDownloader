# Scheduled backups with Docker Compose

[Project overview](../README.md) · [CLI guide](cli.md)

The image runs the same unified CLI as a local installation. Its default command is a
scheduler: sync the configured stores, optionally refresh DRM reports, download the selected
games, then wait before repeating. Jobs run sequentially, so slow downloads never overlap
the next cycle. Linux containers can archive Windows, Linux and supported macOS content;
they do not execute games. Local DRM launch tests still require Windows.

## Start with Compose

The image is `ghcr.io/vladimir-aubrecht/gamearchiver:latest`, built for Linux amd64 and ARM64.
Use the files in [deploy](../deploy) from a checkout of this repository:

```sh
cd deploy
cp .env.example .env
cp schedule.example.json schedule.json
mkdir -p state staging archives
```

Edit `.env` before starting. Set `ARCHIVER_UID` and `ARCHIVER_GID` to the owner of those
directories (`id -u` / `id -g` on Linux). They default to `1000:1000`; the standalone image
defaults to the .NET image's non-root `app` user. The container does not change ownership
of existing files. On Docker Desktop, choose host paths shared with Docker.
`ARCHIVER_UMASK` defaults to `022`, so other users can read new archives; use `002` to allow
group writes or `077` for private backups. Saved token files keep their restricted permissions.

| Host setting in `.env` | Container path | Contents |
| --- | --- | --- |
| `ARCHIVER_STATE_PATH` | `/state` | Saved logins, library snapshots, DRM reports and scheduler lock |
| `ARCHIVER_STAGING_PATH` | `/staging` | Resumable downloads and temporary ZIPs; use a local disk |
| `ARCHIVER_ARCHIVE_PATH` | `/archives` | Completed game folders; can be a mounted NAS |
| `./schedule.json` | `/config/schedule.json` | Read-only job configuration |

Paths in `.env` belong to the Docker host. Relative paths are relative to `deploy/`.
Paths in `schedule.json` belong to the container. Create host directories before starting;
Compose refuses missing bind paths instead of silently creating root-owned directories.
Keep state and staging across container recreation. Staging needs room for both the
unpacked game and its ZIP, independently of free space on the archive volume.

Fetch the image, then log in to each source used by your jobs:

```sh
docker compose pull
docker compose run --rm archiver auth login --source gog
docker compose run --rm archiver auth login --source steam
```

GOG prints a browser login URL; open it on your desktop and paste the returned URL/code
into the terminal. Steam prompts for your account, password and Steam Guard code/mobile
approval. Passwords are not saved; the resulting tokens live in the mounted state directory.
The normal background service needs no password environment variables or interactive input.
If a saved login expires or is revoked, repeat the corresponding login command.

Remove jobs for sources you do not want to use. Preview the command plan, optionally run one
complete cycle, then start the background service:

```sh
docker compose run --rm archiver schedule --config /config/schedule.json --dry-run
docker compose run --rm archiver schedule --config /config/schedule.json --once
docker compose up -d
docker compose logs -f archiver
```

The schedule preview validates configuration and prints argument arrays; it does not log in,
sync or resolve which games match. To preview actual selections after syncing, use the regular
`download --all ... --dry-run` command. Restart after changing `schedule.json`:

```sh
docker compose restart archiver
```

## Choose what each source downloads

[schedule.example.json](../deploy/schedule.example.json) contains all Windows GOG games and
Windows Steam games smaller than 500 MB. Each job must explicitly choose **either** `all: true`
or a nonempty `products` list. Unknown properties and invalid combinations fail at startup.

For example, replace the jobs with these to download small DRM-free Steam games and only
one selected GOG product:

```json
{
  "intervalMinutes": 360,
  "stagingDirectory": "/staging",
  "jobs": [
    {
      "name": "Small DRM-free Steam games",
      "source": "steam",
      "output": "/archives/steam",
      "all": true,
      "maxSize": "500MB",
      "drm": "free",
      "drmSource": "steam-curator"
    },
    {
      "name": "Selected GOG games",
      "source": "gog",
      "output": "/archives/gog",
      "products": ["the_witcher_3_wild_hunt"]
    }
  ]
}
```

Replace example slugs/IDs with products you own. List them with
`docker compose run --rm archiver library list --source gog` after a successful sync.
For the same output tree as the examples, `output` is `/archives/gog` or `/archives/steam`
with the default `flat` layout. Alternatively use `output: "/archives"` and
`layout: "provider"` for each job.

| Setting | Default / meaning |
| --- | --- |
| `intervalMinutes` | Global: 360; 1–10080 minutes **after the previous cycle finishes**. First cycle starts immediately. |
| `snapshot` | Global: `/state/config/gamearchive/library.json` in the image. An override must be an absolute path. |
| `stagingDirectory` | Global: set `/staging` to use the Compose mount, as in the examples. |
| `name` | Required unique job name for logs. |
| `source` | Required `gog` or `steam`. Add separate jobs for different sources/selections. |
| `output` | Required absolute container path for archives. |
| `all` | Default false. Set true to select matching games from the primary catalog. |
| `products` | Explicit IDs, qualified IDs (`steam:2270`) or exact slugs; cannot be combined with `all`. |
| `search` | Optional title/slug search within `all`, passed as one literal argument. |
| `maxSize` | Optional size limit such as `500MB` or `1GiB`; requires `all`. See [size semantics](cli.md#download-a-subset-by-size). |
| `drm` | Optional `free`, `protected`, `unknown` or `any`; requires `all`. Unknown never matches free. |
| `drmSource` | Optional `pcgamingwiki` or `steam-curator`; refresh community reports before downloading. Omit to use saved reports. |
| `includeNonGames` | Default false; include supported applications/demos in `all`. |
| `platform` / `language` | `windows` / `en`. Platforms: `windows`, `linux`, `mac`. |
| `architecture` / `branch` | `64` / `public`. Architectures: `32`, `64`, `arm64`; this selects game content, not the container CPU. |
| `layout` | `flat` or `provider`. |
| `providerDirectories` | Optional mapping such as `{"gog":"drm_free"}`; requires provider layout. |
| `connections` / `compressionThreads` | 5 / available CPUs. Positive integers. |
| `limitRate` | `0` (unlimited); values such as `5M` use the existing CLI bandwidth limit. |

Search/size/DRM filters and `includeNonGames` apply to `all`, not an explicit products list.
Explicit products may include supported non-game inventory entries. All jobs retain the usual
verification and resume behavior; already current archives are skipped. Scheduled downloads
do not force rebuilds, clean staging or disable integrity checks.

Steam sync records sizes for its default English/public/64-bit selection. With other selections,
size filtering may exclude games whose saved estimate does not match. Community reports are
also scoped by OS/branch. See [DRM reports](drm.md) for evidence, overrides and PCGamingWiki login;
the latter works through `docker compose run --rm archiver auth login --source pcgamingwiki`.

## Errors, stopping and recovery

Each source is synced once per cycle. A fresh snapshot with product-level failures can still
download available games; those failures remain visible in logs and the cycle status. A
whole-source failure, missing snapshot or failed snapshot write skips that source's downloads.
Other jobs continue, and the next cycle retries. If a job explicitly requests a DRM refresh
and that refresh fails, its downloads are skipped until a later successful refresh.

`--once` returns 0 for a successful cycle, 1 for errors (including an empty download selection),
and 130 for interruption. Continuous mode logs errors and waits the configured interval.
It does not restart failed downloads in a tight loop. Job configuration is loaded at startup.

`docker compose stop` sends SIGTERM, which cancels the current operation and retains resumable
download data. Compose allows two minutes for cleanup before forcing termination. Completed
archives remain recognized on restart. Use `docker compose down` to remove containers;
the bind-mounted directories remain on the host.

One scheduler holds a lock in the snapshot directory. Use one service for multiple sources;
separate services/accounts need separate state and staging directories. Stop the service before
running one-off commands that update snapshots or annotations, or before running `--once`.

## Build and publish

[The container workflow](../.github/workflows/container.yml) runs the repository checks and
container startup/preview tests. Pull requests build both CPU architectures without publishing.
Pushes to `main` publish `latest` and `sha-<full-commit>` to GHCR; version tags publish their
semantic version. Manual workflow runs are also supported. Images include `innoextract` for
Windows GOG installer backups. See [dependency licenses](../THIRD-PARTY-NOTICES.md).

Publishing uses the workflow's `GITHUB_TOKEN` with `packages: write`, without a separate
registry password secret. After the first publication, a new GHCR package may be private:
make the `gamearchiver` package public in its GitHub package settings for anonymous pulls,
or authenticate Docker with a token allowed to read that package.

To build locally instead of pulling, run `docker compose build` from `deploy/`. To update a
deployed image, use `docker compose pull && docker compose up -d`. Pin `ARCHIVER_IMAGE` to a
`sha-...` tag or digest when you want upgrades to be explicit.
