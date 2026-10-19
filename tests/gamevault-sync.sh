#!/usr/bin/env sh
# Exercise the real sync script with controlled curl responses; no GameVault is contacted.
set -eu
cd "$(dirname "$0")/.."
command -v jq >/dev/null || { echo "jq is required for the GameVault sync probes" >&2; exit 1; }
SYNC_FIXTURE=$(mktemp -d)
export SYNC_FIXTURE
trap 'rm -rf "$SYNC_FIXTURE"' EXIT
mkdir -p "$SYNC_FIXTURE/bin" "$SYNC_FIXTURE/backups/game__1"
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
        > "$BACKUP_DIR/game__1/.gogdl.json"
}
write_game() {
    jq -n --arg e "$1" --arg p "$2" \
        '{data: [{id: 1, file_path: "/files/game__1/Game.zip", user_metadata: {launch_executable: $e, launch_parameters: $p}}]}' \
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
