#!/usr/bin/env bash
# Refreshes the source material behind docs/gog-api.md and docs/gamevault.md.
#
# Why a script: neither project documents what we depend on, and reading their sources one
# file at a time through the web is slow and expensive. A shallow clone plus one grep gives
# the whole picture at once. The docs are the cache; this regenerates the evidence.
#
# Usage:
#   scripts/vendor-ref.sh gog        # heroic-gogdl: GOG endpoints, manifests, chunks
#   scripts/vendor-ref.sh gamevault  # gamevault-backend: indexer rules
#   scripts/vendor-ref.sh all
set -euo pipefail

CACHE="${VENDOR_REF_CACHE:-$(mktemp -d)}"

clone() {
    local repo="$1" dir="$2"
    if [ ! -d "$dir/.git" ]; then
        git clone --depth 1 --quiet "$repo" "$dir"
    else
        git -C "$dir" pull --quiet --ff-only
    fi
    echo "# $(basename "$dir") @ $(git -C "$dir" rev-parse --short HEAD)  ($(git -C "$dir" log -1 --format=%cs))"
    echo
}

section() { echo; echo "## $1"; }
show() { sed -E "s|^$1/||" | sed 's/^/  /' || true; }

gog() {
    local out="$CACHE/heroic-gogdl"
    clone https://github.com/Heroic-Games-Launcher/heroic-gogdl "$out"

    section "Host / URL constants"
    grep -rhnE '^[A-Z_]+ *= *"https?://' "$out/gogdl" | sed 's/^/  /' || true

    section "Assembled endpoints (f-strings over the constants)"
    grep -rnHE 'f"[^"]*\{constants\.GOG_[A-Z_]+\}[^"]*"' "$out/gogdl" | show "$out"

    section "OAuth (client_id / secret / redirect_uri)"
    grep -rnHE 'client_id|client_secret|redirect_uri' "$out/gogdl/auth.py" | show "$out"

    section "Platform-specific branching (where Linux differs)"
    grep -rnHE 'platform *== *"(linux|osx|windows)"' "$out/gogdl" | show "$out"

    section "v2 manifest and chunk handling (schema source)"
    grep -rnHE 'galaxy_path|url_format|compressedMd5|DepotFile|DepotLink|products' \
        "$out/gogdl/dl/objects/v2.py" "$out/gogdl/dl/dl_utils.py" | show "$out"
}

gamevault() {
    local out="$CACHE/gamevault-backend"
    clone https://github.com/Phalcode/gamevault-backend "$out"
    local files="$out/src/modules/games/files.service.ts"

    section "Is indexing recursive? (decides whether our per-game directory is allowed)"
    grep -nHE 'SEARCH_RECURSIVE' "$out/src/configuration.ts" "$files" | show "$out"

    section "Which files are indexed at all? (decides whether .gogdl.json is safe)"
    grep -nHE 'isValidFilePath|supportedFormatsSet|invalidCharacters' "$files" | show "$out"

    section "Filename parsing (title, version, year, early access)"
    grep -nHE 'extractTitle|extractVersion|extractReleaseYear|extractEarlyAccessFlag|replace\(/' "$files" | show "$out"

    section "Game type overrides"
    grep -nHE '\((W_P|W_S|W_SW|L_P|L_SW)\)' "$files" | show "$out"
}

case "${1:-all}" in
    gog) gog ;;
    gamevault) gamevault ;;
    all) gog; echo; gamevault ;;
    *) echo "usage: $0 [gog|gamevault|all]" >&2; exit 2 ;;
esac

echo
echo "Sources left in: $CACHE"
