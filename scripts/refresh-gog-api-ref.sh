#!/usr/bin/env bash
# Obnoví podklady pro docs/gog-api.md.
#
# Proč skript: dohledávání endpointů po jednom souboru přes web je pomalé a drahé.
# Shallow clone + jeden grep dá kompletní přehled naráz.
#
# Použití: scripts/refresh-gog-api-ref.sh [outdir]
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

echo "## Host / URL konstanty"
grep -rhnE '^[A-Z_]+ *= *"https?://' "$OUT/gogdl" | sed 's/^/  /' || true
echo

echo "## Sestavované endpointy (f-stringy nad konstantami)"
grep -rnE 'f"[^"]*\{constants\.GOG_[A-Z_]+\}[^"]*"' "$OUT/gogdl" \
    | sed -E "s|^$OUT/||" | sed 's/^/  /' || true
echo

echo "## OAuth (client_id / secret / redirect_uri)"
grep -rnE 'client_id|client_secret|redirect_uri' "$OUT/gogdl/auth.py" \
    | sed -E "s|^$OUT/||" | sed 's/^/  /' || true
echo

echo "## Platform-specific větvení (kde se Linux liší)"
grep -rnE 'platform *== *"(linux|osx|windows)"' "$OUT/gogdl" \
    | sed -E "s|^$OUT/||" | sed 's/^/  /' || true
echo

echo "Zdroje ponechány v: $OUT"
