# GameVault launcher synchronization

[Project overview](../README.md)

GameArchiver records the launch executable and parameters in each backup's `gamearchiver.json`.
GOG launch settings come from the game's `goggame-{id}.info`; Steam archives use the selected
app's launch metadata. GameVault keeps those settings in its database.

The optional [gamevault-sync.sh](../scripts/gamevault-sync.sh) reads the metadata and updates
GameVault through its API, matching the archive filename and, when necessary, its relative
folder. It changes only values that differ. Both providers, legacy metadata and flat/provider
folder layouts are supported. The archiver itself does not call GameVault.

## As a docker compose sidecar

[gamevault-sync.compose.yml](../scripts/gamevault-sync.compose.yml) is a ready service. Drop it beside your GameVault
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
      BACKUP_DIR: /files                             # where the gamearchiver.json files are
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
   `gamearchiver.json` sits next to each zip in that same tree, mounting it read-only gives the script
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

## Try it once first

Before letting it write, run a single dry-run pass — anywhere with `curl` and `jq`:

```sh
DRY_RUN=1 GAMEVAULT_URL=http://localhost:8080 \
  GAMEVAULT_USER=... GAMEVAULT_PASSWORD=... \
  BACKUP_DIR=/path/to/backups \
  scripts/gamevault-sync.sh
```

It prints one line per game (`would set`, `current`, or `skipped (not in GameVault yet)`) plus a
summary, and touches nothing. Drop `DRY_RUN=1` to apply.

## Configuration

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

## As a cron job instead

If you would rather not run a container, put `curl` and `jq` on the host and schedule a single
pass (leave `SYNC_INTERVAL` unset):

```cron
0 * * * * GAMEVAULT_URL=http://localhost:8080 GAMEVAULT_USER=svc GAMEVAULT_PASSWORD=secret \
          BACKUP_DIR=/srv/gamevault/files /usr/local/bin/gamevault-sync.sh >> /var/log/gamevault-sync.log 2>&1
```

See the [GameVault reference](gamevault.md#setting-launch_executable-without-coupling-the-tool) for matching, metadata precedence and API details.
