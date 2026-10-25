#!/usr/bin/env sh
# Push the launch executable of each backup into GameVault.
#
# GogArchiver deliberately knows nothing about GameVault. It records what GOG says the launcher
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
#   BACKUP_DIR           the --out directory GogArchiver wrote to      (default /files)
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

# Basic auth only covers the login endpoint, which issues a JWT; every other route wants
# "Authorization: Bearer". The access token expires after minutes (AUTH_ACCESS_TOKEN_EXPIRES_IN,
# default 5m), so each pass logs in afresh rather than caching one.
login() {
    if ! pair=$(curl -fsSL -u "$AUTH" "$API/auth/basic/login"); then
        echo "login at $API/auth/basic/login failed (curl's error above says which kind):" \
             "401 means GAMEVAULT_USER/GAMEVAULT_PASSWORD are wrong or the user is not activated;" \
             "5xx or a connection failure means GAMEVAULT_URL — when GameVault runs in the same" \
             "compose file, point it at the service directly (http://<service>:8080)," \
             "not at a public URL that hops through a reverse proxy" >&2
        return 1
    fi
    token=$(printf '%s' "$pair" | jq -r '.access_token // empty')
    [ -n "$token" ] || { echo "the login response carried no access_token" >&2; return 1; }
    printf '%s' "$token"
}

# GameVault's game list, one JSON object per game, including both launch fields.
# Following pagination to the end so a library larger than one page still matches.
list_games() {
    page=1
    while :; do
        # A failed request must abort the pass. Without this, an unreachable GameVault leaves
        # $body empty, the pagination check trips on a non-number ("sh: out of range"), and the
        # loop hammers the same URL forever.
        if ! body=$(curl -fsSL -H "Authorization: Bearer $TOKEN" "$API/games?limit=100&page=$page"); then
            echo "cannot list games from $API even though the login worked — see curl's error above" >&2
            return 1
        fi
        count=$(printf '%s' "$body" | jq '.data | length')
        printf '%s' "$body" | jq -c '
            .data[] | {
                id,
                path: (.file_path // ""),
                archive: (.file_path // "" | sub(".*/"; "")),
                executable: (.user_metadata.launch_executable // ""),
                parameters: (.user_metadata.launch_parameters // "")
            }'
        [ "$count" -lt 100 ] && break
        page=$((page + 1))
    done
}

# One line of tally per action, so the summary survives the pipe subshells that would otherwise
# swallow counter variables.
sync_once() {
    TOKEN=$(login) || return 1

    tally=$(mktemp)

    # Match by filename. An unreachable GameVault fails the pass
    # cleanly here instead of mislabelling every archive as "not in GameVault yet".
    games=$(list_games) || { rm -f "$tally"; return 1; }

    # GOG keeps its legacy metadata; native Steam archives use the shared version-2 format.
    # Other producers may use .gogdl.json with another schema, which must stay untouched.
    # Support both root/game and root/provider/game layouts without following links.
    find "$BACKUP_DIR" -maxdepth 3 \( -name '.gogdl.json' -o -name '.gamebackup.json' \) -type f 2>/dev/null | while IFS= read -r manifest; do
        format=${manifest##*/}
        directory=${manifest%/*}
        legacy="$directory/.gogdl.json"
        modern="$directory/.gamebackup.json"
        if [ -f "$legacy" ] && [ -f "$modern" ] && jq -e '
            with_entries(.key |= ascii_downcase)
            | if has("source") then (.source | strings | ascii_downcase) == "gog" else true end' "$legacy" >/dev/null; then
            # The original CLI can update its metadata independently. Use it only when both
            # envelopes agree on the owner; a conflict must never change GameVault metadata.
            [ "$format" = ".gogdl.json" ] || continue
            if ! jq -se 'map(with_entries(.key |= ascii_downcase))
                | (.[1].source | strings | ascii_downcase) == "gog"
                  and (.[0].gameid | tostring) == .[1].productid' "$legacy" "$modern" >/dev/null; then
                echo "FAILED    conflicting metadata owners in $directory" >&2
                echo failed >>"$tally"; continue
            fi
        fi
        jq -c --arg format "$format" '
            select(with_entries(.key |= ascii_downcase)
                | if $format == ".gamebackup.json" then
                    .schemaversion == 2 and ((.source | strings | ascii_downcase) | . == "gog" or . == "steam")
                  elif has("source") then (.source | strings | ascii_downcase) == "gog"
                  else true end)
            | .Platforms | to_entries[] | .value
            | select(.LaunchExecutable != null and .LaunchExecutable != "")
            | {archive: .ArchiveName, executable: .LaunchExecutable,
               parameters: (.LaunchParameters // "")}' "$manifest" \
        | while IFS= read -r backup; do
            archive=$(printf '%s' "$backup" | jq -r '.archive')
            executable=$(printf '%s' "$backup" | jq -r '.executable')
            relative=${directory#"${BACKUP_DIR%/}/"}
            matches=$(printf '%s\n' "$games" | jq -sc --arg n "$archive" 'map(select(.archive == $n))')
            if [ "$(printf '%s' "$matches" | jq 'length')" -gt 1 ]; then
                matches=$(printf '%s' "$matches" | jq -c --arg suffix "/$relative/$archive" 'map(select(.path | endswith($suffix)))')
                if [ "$(printf '%s' "$matches" | jq 'length')" -ne 1 ]; then
                    echo "FAILED    ambiguous GameVault archive: $archive" >&2
                    echo failed >>"$tally"; continue
                fi
            fi
            row=$(printf '%s' "$matches" | jq -c '.[0] // empty')

            if [ -z "$row" ]; then
                echo "skipped   $archive (not in GameVault yet)"
                echo unmatched >>"$tally"; continue
            fi

            id=$(printf '%s' "$row" | jq -r '.id')

            if printf '%s' "$row" | jq -e --argjson want "$backup" \
                    '.executable == $want.executable and .parameters == $want.parameters' >/dev/null; then
                echo current >>"$tally"; continue
            fi

            if [ "${DRY_RUN:-0}" = "1" ]; then
                echo "would set $archive -> $executable"
                echo would >>"$tally"; continue
            fi

            # Keep paths and arguments as JSON until the request, preserving backslashes,
            # quotes and whitespace that a TSV/read round-trip would change.
            payload=$(printf '%s' "$backup" | jq -c \
                '{user_metadata: {launch_executable: .executable,
                  launch_parameters: (if .parameters == "" then null else .parameters end)}}')

            if curl -fsSL -H "Authorization: Bearer $TOKEN" -X PUT -H 'Content-Type: application/json' \
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
