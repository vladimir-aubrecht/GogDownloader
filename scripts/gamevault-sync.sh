#!/usr/bin/env sh
# Push the launch executable of each backup into GameVault.
#
# GogDownloader deliberately knows nothing about GameVault. It records what GOG says the launcher
# is into each backup's .gogdl.json; this script is the glue that turns that into GameVault API
# calls. It lives here, not in the tool, so the dependency stays on your side.
#
# Meant to run beside GameVault — a cron job, or a compose sidecar with SYNC_INTERVAL set. Needs
# only curl and jq (e.g. alpine + 'apk add curl jq').
#
# Configuration (flags override environment):
#   GAMEVAULT_URL        e.g. http://gamevault-backend:8080
#   GAMEVAULT_USER       a user with the EDITOR role
#   GAMEVAULT_PASSWORD
#   BACKUP_DIR           the --out directory GogDownloader wrote to      (default /files)
#   SYNC_INTERVAL        seconds between passes; unset means one pass and exit
#   DRY_RUN=1            report what would change without changing it
set -eu

BACKUP_DIR="${BACKUP_DIR:-/files}"

usage() {
    cat >&2 <<EOF
usage: gamevault-sync.sh [--url URL] [--user USER] [--password PASS] [--backup-dir DIR] [--dry-run]
       configuration also read from GAMEVAULT_URL, GAMEVAULT_USER, GAMEVAULT_PASSWORD,
       BACKUP_DIR, SYNC_INTERVAL, DRY_RUN.
EOF
    exit 2
}

while [ $# -gt 0 ]; do
    case "$1" in
        --url) GAMEVAULT_URL="$2"; shift 2 ;;
        --user) GAMEVAULT_USER="$2"; shift 2 ;;
        --password) GAMEVAULT_PASSWORD="$2"; shift 2 ;;
        --backup-dir) BACKUP_DIR="$2"; shift 2 ;;
        --dry-run) DRY_RUN=1; shift ;;
        -h|--help) usage ;;
        *) echo "unknown argument: $1" >&2; usage ;;
    esac
done

: "${GAMEVAULT_URL:?set GAMEVAULT_URL or pass --url}"
: "${GAMEVAULT_USER:?set GAMEVAULT_USER or pass --user}"
: "${GAMEVAULT_PASSWORD:?set GAMEVAULT_PASSWORD or pass --password}"
command -v jq >/dev/null || { echo "jq is required" >&2; exit 3; }

API="${GAMEVAULT_URL%/}/api"
AUTH="$GAMEVAULT_USER:$GAMEVAULT_PASSWORD"

# GameVault's game list, one row per game: "id<TAB>filename<TAB>current_launch_executable".
# Following pagination to the end so a library larger than one page still matches.
list_games() {
    page=1
    while :; do
        # A failed request must abort the pass. Without this, an unreachable or misconfigured
        # GameVault leaves $body empty, the pagination check trips on a non-number ("sh: out of
        # range"), and the loop hammers the same 401 forever.
        if ! body=$(curl -fsSL -u "$AUTH" "$API/games?limit=100&page=$page"); then
            echo "cannot list games from $API (curl's error above says which kind):" \
                 "401 means GAMEVAULT_USER/GAMEVAULT_PASSWORD are wrong or the user is not activated;" \
                 "5xx or a connection failure means GAMEVAULT_URL — when GameVault runs in the same" \
                 "compose file, point it at the service directly (http://<service>:8080)," \
                 "not at a public URL that hops through a reverse proxy" >&2
            return 1
        fi
        count=$(printf '%s' "$body" | jq '.data | length')
        printf '%s' "$body" | jq -r '
            .data[] | [
                .id,
                (.file_path // "" | sub(".*/"; "")),
                (.user_metadata.launch_executable // "")
            ] | @tsv'
        [ "$count" -lt 100 ] && break
        page=$((page + 1))
    done
}

# One line of tally per action, so the summary survives the pipe subshells that would otherwise
# swallow counter variables.
sync_once() {
    tally=$(mktemp)

    # filename -> "id<TAB>current_executable". An unreachable GameVault fails the pass
    # cleanly here instead of mislabelling every archive as "not in GameVault yet".
    games=$(list_games) || { rm -f "$tally"; return 1; }

    # Each backup's .gogdl.json can describe several platforms, each its own archive.
    find "$BACKUP_DIR" -maxdepth 2 -name '.gogdl.json' -type f 2>/dev/null | while read -r manifest; do
        jq -r '
            .Platforms | to_entries[] | .value
            | select(.LaunchExecutable != null and .LaunchExecutable != "")
            | [.ArchiveName, .LaunchExecutable, (.LaunchParameters // "")] | @tsv' "$manifest" \
        | while IFS="$(printf '\t')" read -r archive executable parameters; do
            row=$(printf '%s\n' "$games" | awk -F'\t' -v n="$archive" '$2 == n { print; exit }')

            if [ -z "$row" ]; then
                echo "skipped   $archive (not in GameVault yet)"
                echo unmatched >>"$tally"; continue
            fi

            id=$(printf '%s' "$row" | cut -f1)
            have=$(printf '%s' "$row" | cut -f3)

            if [ "$have" = "$executable" ]; then
                echo current >>"$tally"; continue
            fi

            if [ "${DRY_RUN:-0}" = "1" ]; then
                echo "would set $archive -> $executable"
                echo would >>"$tally"; continue
            fi

            payload=$(jq -n --arg e "$executable" --arg p "$parameters" \
                '{user_metadata: {launch_executable: $e, launch_parameters: (if $p == "" then null else $p end)}}')

            if curl -fsSL -u "$AUTH" -X PUT -H 'Content-Type: application/json' \
                    -d "$payload" "$API/games/$id" >/dev/null; then
                echo "updated   $archive -> $executable"
                echo updated >>"$tally"
            else
                echo "FAILED    $archive" >&2
                echo failed >>"$tally"
            fi
        done
    done

    # grep -c prints 0 and also exits non-zero on no match, which would both double the output
    # and trip set -e; capture it and default cleanly.
    count() { c=$(grep -c "^$1\$" "$tally" 2>/dev/null || true); echo "${c:-0}"; }
    echo "done: $(count updated) updated, $(count current) current, $(count would) would change, $(count unmatched) not in GameVault, $(count failed) failed."

    failures=$(count failed)
    rm -f "$tally"
    [ "$failures" = "0" ]
}

if [ -n "${SYNC_INTERVAL:-}" ]; then
    echo "gamevault-sync: every ${SYNC_INTERVAL}s"
    while :; do
        sync_once || echo "pass failed, will retry" >&2
        sleep "$SYNC_INTERVAL"
    done
else
    sync_once
fi
