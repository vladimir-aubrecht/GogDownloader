#!/usr/bin/env bash
# Refreshes the source material behind docs/gog-api.md.
#
# Why a script: looking endpoints up one file at a time through the web is slow and expensive.
# A shallow clone plus a single grep gives the whole picture at once.
#
# Usage: scripts/refresh-gog-api-ref.sh [outdir]
set -euo pipefail

REPO=https://github.com/Heroic-Games-Launcher/heroic-gogdl
OUT="${1:-$(mktemp -d)}"

if [ ! -d "$OUT/.git" ]; then
    git clone --depth 1 --quiet "$REPO" "$OUT"
else
    git -C "$OUT" pull --quiet --ff-only
fi

echo "# heroic-gogdl @ $(git -C "$OUT" rev-parse --short HEAD)  ($(git -C "$OUT" log -1 --format=%cs))"
echo

echo "## Host / URL constants"
grep -rhnE '^[A-Z_]+ *= *"https?://' "$OUT/gogdl" | sed 's/^/  /' || true
echo

echo "## Assembled endpoints (f-strings over the constants)"
grep -rnE 'f"[^"]*\{constants\.GOG_[A-Z_]+\}[^"]*"' "$OUT/gogdl" \
    | sed -E "s|^$OUT/||" | sed 's/^/  /' || true
echo

echo "## OAuth (client_id / secret / redirect_uri)"
grep -rnE 'client_id|client_secret|redirect_uri' "$OUT/gogdl/auth.py" \
    | sed -E "s|^$OUT/||" | sed 's/^/  /' || true
echo

echo "## Platform-specific branching (where Linux differs)"
grep -rnE 'platform *== *"(linux|osx|windows)"' "$OUT/gogdl" \
    | sed -E "s|^$OUT/||" | sed 's/^/  /' || true
echo

echo "## v2 manifest and chunk handling (schema source)"
grep -rnE 'galaxy_path|url_format|compressedMd5|DepotFile|DepotLink|products' \
    "$OUT/gogdl/dl/objects/v2.py" "$OUT/gogdl/dl/dl_utils.py" \
    | sed -E "s|^$OUT/||" | sed 's/^/  /' || true
echo

echo "Sources left in: $OUT"
