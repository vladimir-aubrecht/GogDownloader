#!/usr/bin/env bash
# Runs without network access or store credentials against a locally built image.
set -euo pipefail
cd "$(dirname "$0")/.."
image="${1:?usage: tests/container-smoke.sh IMAGE}"
scratch="$(mktemp -d)"
container=""
cleanup() {
    if [ -n "$container" ]; then docker rm -f "$container" >/dev/null; fi
    rm -rf "$scratch"
}
trap cleanup EXIT

docker run --rm --network none "$image" --version
docker run --rm --network none --entrypoint innoextract "$image" --version
cp deploy/schedule.example.json "$scratch/example.json"
chmod 644 "$scratch/example.json"
docker run --rm --network none -v "$scratch/example.json:/config/schedule.json:ro" \
    "$image" schedule --config /config/schedule.json --dry-run

mkdir -p "$scratch/state" "$scratch/staging" "$scratch/archives"
cat > "$scratch/schedule.json" <<'JSON'
{"intervalMinutes":360,"stagingDirectory":"/staging","jobs":[{"name":"Smoke","source":"gog","output":"/archives/gog","all":true}]}
JSON
container="$(docker run -d --network none --user "$(id -u):$(id -g)" \
    -v "$scratch/schedule.json:/config/schedule.json:ro" \
    -v "$scratch/state:/state" -v "$scratch/staging:/staging" -v "$scratch/archives:/archives" "$image")"
for attempt in {1..50}; do
    if docker logs "$container" 2>&1 | grep -q 'Next cycle in'; then break; fi
    sleep 0.2
done
docker logs "$container" > "$scratch/log" 2>&1
if ! grep -q 'Next cycle in' "$scratch/log"; then cat "$scratch/log"; exit 1; fi
grep -q 'Skipping downloads' "$scratch/log"
test -f "$scratch/state/config/gamearchive/library.json"
docker stop --time 30 "$container" >/dev/null
test "$(docker inspect --format '{{.State.ExitCode}}' "$container")" = 130
docker logs "$container" 2>&1 | grep 'Scheduler stopped'
test -f "$scratch/state/config/gamearchive/library.json"
echo 'Container startup, persisted state, failed-source isolation and SIGTERM checks passed.'
