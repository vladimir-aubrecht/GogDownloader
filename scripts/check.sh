#!/usr/bin/env bash
# Build, run the probes, and exercise the CLI against a fixture snapshot.
#
# This is what "verified" means in this repo. `scripts/release.sh` refuses to publish
# unless this passes.
set -euo pipefail

cd "$(dirname "$0")/.."

echo "== build =="
dotnet build -v q --nologo

echo
echo "== probes =="
dotnet run --project tests/GogDownloader.Probes -v q --nologo

echo
echo "== cli against tests/fixtures/library.json =="
# --snapshot keeps the real library in the user's profile untouched.
gogdl() { dotnet run --project src/GogDownloader.Cli -- "$@" --snapshot tests/fixtures/library.json; }

expect() {
    local description="$1" expected="$2"; shift 2
    local output
    output="$("$@" 2>&1 || true)"
    if grep -qF -- "$expected" <<<"$output"; then
        echo "  ok   $description"
    else
        echo "  FAIL $description"
        echo "       expected to find: $expected"
        echo "       in:"
        sed 's/^/         /' <<<"$output"
        exit 1
    fi
}

expect "list finds every game"        "4 games"                    gogdl library list
expect "list searches by title"       "the_witcher_3_wild_hunt"    gogdl library list witcher
expect "list shows owned DLC"         "Blood and Wine"             gogdl library list witcher
expect "list shows sizes"             "30 GiB"                     gogdl library list witcher
expect "list filters by DLC"          "1 game,"                    gogdl library list --dlc
expect "list marks installer-only"    "(win)"                      gogdl library list ultima
expect "list rejects bad platform"    "Unknown platform"           gogdl library list --platform amiga
expect "list reports no matches"      "Nothing matches"            gogdl library list zzzznope
expect "list --slugs prints bare slugs" "faster_than_light"        gogdl library list --slugs --depot --platform windows
expect "stats counts depotless games" "without a depot: 1"         gogdl library stats
expect "stats pluralises correctly"   "1 title "                   gogdl library stats
expect "download needs a game"        "Missing game"               gogdl download
# A flag's value must never be mistaken for the game name.
expect "download ignores flag values" "Missing game"               gogdl download --platform windows
expect "download rejects unknown game" "is not in the library"     gogdl download nonexistent
expect "download refuses generation 1" "only 2 is supported"       gogdl download ancient_bundle
expect "download refuses no depot"    "No Galaxy depot"            gogdl download ultima_1_2_3 --platform linux

# Batch mode must select exactly what `library list` shows.
expect "download --all lists every depot game" "3 games for windows"  gogdl download --all --dry-run --platform windows
expect "download --all honours the search"     "1 game for windows"   gogdl download --all witcher --dry-run --platform windows
expect "download --all honours --dlc"          "1 game for windows"   gogdl download --all --dlc --dry-run --platform windows
expect "download --all skips depotless games"  "Ancient Bundle"       gogdl download --all --dry-run --platform windows
expect "download --all reports no matches"     "Nothing matches"      gogdl download --all zzzznope --dry-run
expect "download --all rejects bad platform"   "Unknown platform"     gogdl download --all --dry-run --platform amiga

echo
echo "All checks passed."
