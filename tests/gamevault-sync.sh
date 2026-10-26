#!/usr/bin/env sh
# Exercise the real sync script with controlled curl responses; no GameVault is contacted.
set -eu
cd "$(dirname "$0")/.."
command -v jq >/dev/null || { echo "jq is required for the GameVault sync probes" >&2; exit 1; }
SYNC_FIXTURE=$(mktemp -d)
export SYNC_FIXTURE
trap 'rm -rf "$SYNC_FIXTURE"' EXIT
mkdir -p "$SYNC_FIXTURE/bin" "$SYNC_FIXTURE/backups/game"
cat > "$SYNC_FIXTURE/bin/curl" <<'SH'
#!/usr/bin/env sh
set -eu
payload=''
while [ "$#" -gt 0 ]; do
    case "$1" in
        -d) payload="$2"; shift 2 ;;
        *) url="$1"; shift ;;
    esac
done

case "$url" in
    */auth/basic/login) printf '{"access_token":"fixture"}' ;;
    */games\?*) cat "$SYNC_FIXTURE/games.json" ;;
    */games/1) printf '%s' "$payload" >> "$SYNC_FIXTURE/requests.jsonl"; printf '\n' >> "$SYNC_FIXTURE/requests.jsonl" ;;
    *) echo "Unexpected fixture URL: $url" >&2; exit 1 ;;
esac
SH
chmod +x "$SYNC_FIXTURE/bin/curl"
PATH="$SYNC_FIXTURE/bin:$PATH"
export PATH
export GAMEVAULT_URL='https://fixture.invalid' GAMEVAULT_USER='fixture' GAMEVAULT_PASSWORD='fixture'
export BACKUP_DIR="$SYNC_FIXTURE/backups"
unset SYNC_INTERVAL DRY_RUN

check() {
    description="$1"; shift
    if "$@" >/dev/null; then echo "  ok   $description"; else echo "  FAIL $description" >&2; exit 1; fi
}

# JSON stays intact through the real script, including backslashes, tabs, newlines and quotes.
desired_executable='bin\Game.exe'
desired_parameters=$(printf '%s\t%s\n%s' '--profile "A B"' 'C:\Users\Player' '--fullscreen')
write_backup() {
    jq -n --arg e "$desired_executable" --arg p "$desired_parameters" \
        '{Platforms: {Windows: {ArchiveName: "Game.zip", LaunchExecutable: $e, LaunchParameters: $p}}}' \
        > "$BACKUP_DIR/game/.gogdl.json"
}
write_game() {
    jq -n --arg e "$1" --arg p "$2" \
        '{data: [{id: 1, file_path: "/files/game/Game.zip", user_metadata: {launch_executable: $e, launch_parameters: $p}}]}' \
        > "$SYNC_FIXTURE/games.json"
}
run_sync() {
    : > "$SYNC_FIXTURE/requests.jsonl"
    result=$(sh scripts/gamevault-sync.sh "$@")
}

write_backup
write_game "$desired_executable" '--old'
run_sync
check "sync updates arguments when the executable is unchanged" \
    jq -e --arg e "$desired_executable" --arg p "$desired_parameters" \
        '.user_metadata == {launch_executable: $e, launch_parameters: $p}' "$SYNC_FIXTURE/requests.jsonl"
check "an argument-only change counts as updated" test "$(printf '%s' "$result" | tail -n 1)" = \
    'done: 1 updated, 0 current, 0 would change, 0 not in GameVault, 0 failed.'

write_game "$desired_executable" "$desired_parameters"
run_sync
check "matching executable and arguments make no request" test ! -s "$SYNC_FIXTURE/requests.jsonl"
check "matching arguments count as current" test "$(printf '%s' "$result" | tail -n 1)" = \
    'done: 0 updated, 1 current, 0 would change, 0 not in GameVault, 0 failed.'

desired_parameters=''
write_backup
run_sync
check "removing launch arguments writes null to GameVault" \
    jq -e '.user_metadata.launch_parameters == null' "$SYNC_FIXTURE/requests.jsonl"

jq '.data[0].user_metadata.launch_parameters = null' "$SYNC_FIXTURE/games.json" > "$SYNC_FIXTURE/null.json"
mv "$SYNC_FIXTURE/null.json" "$SYNC_FIXTURE/games.json"
run_sync
check "null and empty arguments compare equally" test ! -s "$SYNC_FIXTURE/requests.jsonl"

desired_parameters='--new'
write_backup
run_sync --dry-run
check "dry-run detects an argument-only change" test "$(printf '%s' "$result" | tail -n 1)" = \
    'done: 0 updated, 0 current, 1 would change, 0 not in GameVault, 0 failed.'
check "dry-run makes no update request" test ! -s "$SYNC_FIXTURE/requests.jsonl"

desired_executable='other\Launcher.exe'
write_backup
run_sync
check "an executable change still updates both fields" \
    jq -e --arg e "$desired_executable" --arg p "$desired_parameters" \
        '.user_metadata == {launch_executable: $e, launch_parameters: $p}' "$SYNC_FIXTURE/requests.jsonl"

mkdir -p "$BACKUP_DIR/foreign"
printf '%s\n' '{"Source":"steam","Platforms":"a different schema"}' > "$BACKUP_DIR/foreign/.gogdl.json"
jq '.Source = "GOG"' "$BACKUP_DIR/game/.gogdl.json" > "$SYNC_FIXTURE/source.json"
mv "$SYNC_FIXTURE/source.json" "$BACKUP_DIR/game/.gogdl.json"
run_sync
check "sync accepts explicit GOG sources and ignores foreign schemas" test "$(printf '%s' "$result" | tail -n 1)" = \
    'done: 1 updated, 0 current, 0 would change, 0 not in GameVault, 0 failed.'

for source in '"Steam"' '"manual"' 'null' '""' '42'; do
    jq --argjson source "$source" 'del(.Source) | .source = $source' \
        "$BACKUP_DIR/game/.gogdl.json" > "$SYNC_FIXTURE/source.json"
    mv "$SYNC_FIXTURE/source.json" "$BACKUP_DIR/game/.gogdl.json"
    run_sync
    check "sync never writes launch metadata from source $source" test ! -s "$SYNC_FIXTURE/requests.jsonl"
done

# Native Steam metadata shares the launch fields, but lives in its own versioned envelope.
write_backup
jq '. + {SchemaVersion: 2, Source: "steam", ProductId: "1", Slug: "game", Title: "Game"}' \
    "$BACKUP_DIR/game/.gogdl.json" > "$BACKUP_DIR/game/.gamebackup.json"
rm "$BACKUP_DIR/game/.gogdl.json"
run_sync
check "sync reads launch metadata from native Steam archives" \
    jq -e --arg e "$desired_executable" '.user_metadata.launch_executable == $e' "$SYNC_FIXTURE/requests.jsonl"
jq '.Source = "epic"' "$BACKUP_DIR/game/.gamebackup.json" > "$SYNC_FIXTURE/source.json"
mv "$SYNC_FIXTURE/source.json" "$BACKUP_DIR/game/.gamebackup.json"
run_sync
check "unsupported modern sources remain untouched" test ! -s "$SYNC_FIXTURE/requests.jsonl"

jq '.Source = "steam"' "$BACKUP_DIR/game/.gamebackup.json" > "$SYNC_FIXTURE/source.json"
mv "$SYNC_FIXTURE/source.json" "$BACKUP_DIR/game/.gamebackup.json"
write_game 'old.exe' '--old'
jq '.data = [(.data[0] + {id: 2, file_path: "/files/other/Game.zip"}), .data[0]]' \
    "$SYNC_FIXTURE/games.json" > "$SYNC_FIXTURE/matches.json"
mv "$SYNC_FIXTURE/matches.json" "$SYNC_FIXTURE/games.json"
run_sync
check "duplicate archive basenames are matched by their backup folder" test "$(wc -l < "$SYNC_FIXTURE/requests.jsonl")" -eq 1
jq '.data[1].file_path = "/files/elsewhere/Game.zip"' "$SYNC_FIXTURE/games.json" > "$SYNC_FIXTURE/matches.json"
mv "$SYNC_FIXTURE/matches.json" "$SYNC_FIXTURE/games.json"
: > "$SYNC_FIXTURE/requests.jsonl"
failed=0
result=$(sh scripts/gamevault-sync.sh 2>&1) || failed=$?
check "an ambiguous archive match fails without updating another game's launcher" test "$failed" -eq 1
check "ambiguous matches make no update request" test ! -s "$SYNC_FIXTURE/requests.jsonl"

write_game 'old.exe' '--old'
write_backup
jq '. + {GameId: 1, Source: "gog"}' "$BACKUP_DIR/game/.gogdl.json" > "$SYNC_FIXTURE/source.json"
mv "$SYNC_FIXTURE/source.json" "$BACKUP_DIR/game/.gogdl.json"
jq '.Source = "gog" | .Platforms.Windows.LaunchExecutable = "stale.exe"' \
    "$BACKUP_DIR/game/.gamebackup.json" > "$SYNC_FIXTURE/source.json"
mv "$SYNC_FIXTURE/source.json" "$BACKUP_DIR/game/.gamebackup.json"
run_sync
check "matching dual metadata envelopes use the authoritative GOG launcher once" \
    test "$(wc -l < "$SYNC_FIXTURE/requests.jsonl")" -eq 1
check "stale native GOG launch data cannot override current legacy metadata" \
    jq -e --arg e "$desired_executable" '.user_metadata.launch_executable == $e' "$SYNC_FIXTURE/requests.jsonl"
jq '.Source = "steam"' "$BACKUP_DIR/game/.gamebackup.json" > "$SYNC_FIXTURE/source.json"
mv "$SYNC_FIXTURE/source.json" "$BACKUP_DIR/game/.gamebackup.json"
: > "$SYNC_FIXTURE/requests.jsonl"
failed=0
result=$(sh scripts/gamevault-sync.sh 2>&1) || failed=$?
check "conflicting metadata owners fail without a GameVault update" test "$failed" -eq 1
check "conflicting owners make no update request" test ! -s "$SYNC_FIXTURE/requests.jsonl"

# Provider folders add one level; identical basenames still match the full relative path.
rm "$BACKUP_DIR/game/.gamebackup.json"
mkdir -p "$BACKUP_DIR/gog"
mv "$BACKUP_DIR/game" "$BACKUP_DIR/gog/game"
write_game 'old.exe' '--old'
jq '.data = [(.data[0] + {id: 2, file_path: "/files/steam/game/Game.zip"}),
    (.data[0] + {file_path: "/files/gog/game/Game.zip"})]' \
    "$SYNC_FIXTURE/games.json" > "$SYNC_FIXTURE/matches.json"
mv "$SYNC_FIXTURE/matches.json" "$SYNC_FIXTURE/games.json"
run_sync
check "provider layout finds GOG metadata and disambiguates the provider folder" \
    test "$(wc -l < "$SYNC_FIXTURE/requests.jsonl")" -eq 1
jq '. + {SchemaVersion: 2, Source: "steam", ProductId: "1"}' "$BACKUP_DIR/gog/game/.gogdl.json" \
    > "$BACKUP_DIR/gog/game/.gamebackup.json"
rm "$BACKUP_DIR/gog/game/.gogdl.json"
mkdir -p "$BACKUP_DIR/steam_library"
mv "$BACKUP_DIR/gog/game" "$BACKUP_DIR/steam_library/game"
jq '.data[0].file_path = "/files/gog/game/Game.zip" | .data[1].file_path = "/files/steam_library/game/Game.zip"' \
    "$SYNC_FIXTURE/games.json" > "$SYNC_FIXTURE/matches.json"
mv "$SYNC_FIXTURE/matches.json" "$SYNC_FIXTURE/games.json"
run_sync
check "custom provider folders support native Steam launch metadata" \
    test "$(wc -l < "$SYNC_FIXTURE/requests.jsonl")" -eq 1

# The visible filename works for both provider schemas and wins over matching old files.
cp "$BACKUP_DIR/steam_library/game/.gamebackup.json" "$BACKUP_DIR/steam_library/game/gamearchiver.json"
run_sync
check "canonical Steam metadata is processed once alongside its old filename" \
    test "$(wc -l < "$SYNC_FIXTURE/requests.jsonl")" -eq 1
rm "$BACKUP_DIR/steam_library/game/.gamebackup.json"
jq '.Source = "gog" | .SchemaVersion = 1 | .GameId = 1 | del(.ProductId)' \
    "$BACKUP_DIR/steam_library/game/gamearchiver.json" > "$SYNC_FIXTURE/canonical.json"
mv "$SYNC_FIXTURE/canonical.json" "$BACKUP_DIR/steam_library/game/gamearchiver.json"
run_sync
check "canonical GOG metadata supports the same launch synchronization" \
    test "$(wc -l < "$SYNC_FIXTURE/requests.jsonl")" -eq 1
jq '.Platforms.Windows.LaunchExecutable = "stale.exe"' \
    "$BACKUP_DIR/steam_library/game/gamearchiver.json" > "$BACKUP_DIR/steam_library/game/.gogdl.json"
run_sync
check "canonical GOG launch metadata wins over a stale legacy copy" \
    jq -e '.user_metadata.launch_executable != "stale.exe"' "$SYNC_FIXTURE/requests.jsonl"
jq '.GameId = 999' "$BACKUP_DIR/steam_library/game/.gogdl.json" > "$SYNC_FIXTURE/conflict.json"
mv "$SYNC_FIXTURE/conflict.json" "$BACKUP_DIR/steam_library/game/.gogdl.json"
: > "$SYNC_FIXTURE/requests.jsonl"
failed=0
result=$(sh scripts/gamevault-sync.sh 2>&1) || failed=$?
check "canonical/legacy ownership conflicts still prevent GameVault writes" test "$failed" -eq 1
check "canonical/legacy conflict sends no update" test ! -s "$SYNC_FIXTURE/requests.jsonl"
