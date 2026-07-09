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
expect "stats counts gen1 depots"     "gen1:    1"                 gogdl library stats
expect "stats pluralises correctly"   "1 title "                   gogdl library stats
expect "download needs a game"        "Missing game"               gogdl download
# A flag's value must never be mistaken for the game name.
expect "download ignores flag values" "Missing game"               gogdl download --platform windows
expect "download rejects unknown game" "is not in the library"     gogdl download nonexistent
expect "download refuses generation 1" "only 2 is supported"       gogdl download ancient_bundle
expect "download refuses no depot"    "No Galaxy depot"            gogdl download ultima_1_2_3 --platform linux

# Batch mode must select exactly what `library list` shows.
expect "download --all lists every depot game" "2 games for windows"  gogdl download --all --dry-run --platform windows
# Generation 1 has a depot but cannot be fetched; it must be excluded loudly, not silently.
expect "download --all excludes gen1"    "Skipping 1 title"          gogdl download --all --dry-run --platform windows
expect "download --all drops the gen1 title" "Ancient Bundle"        gogdl library list ancient
expect "list marks gen1 depots"          "win:gen1"                  gogdl library list ancient
expect "download --all honours the search"     "1 game for windows"   gogdl download --all witcher --dry-run --platform windows
expect "download --all honours --dlc"          "1 game for windows"   gogdl download --all --dlc --dry-run --platform windows
expect "download --all reports no matches"     "Nothing matches"      gogdl download --all zzzznope --dry-run
expect "download --all rejects bad platform"   "Unknown platform"     gogdl download --all --dry-run --platform amiga


echo
echo "== diff against a fixture backup directory =="
BACKUP=$(mktemp -d)
trap 'rm -rf "$BACKUP"' EXIT

mkdir -p "$BACKUP/the_witcher_3_wild_hunt__1207658930" "$BACKUP/faster_than_light__1207666073" "$BACKUP/stranger__42"

# Up to date: build b1 matches the snapshot.
cat > "$BACKUP/the_witcher_3_wild_hunt__1207658930/.gogdl.json" <<'JSON'
{ "SchemaVersion":1, "GameId":1207658930, "Slug":"the_witcher_3_wild_hunt", "Title":"The Witcher 3",
  "Platforms": { "Windows": { "BuildId":"b1", "VersionName":"1.32", "Language":"en",
    "ArchiveName":"w3.zip", "ArchiveSize":1, "InstalledSize":1,
    "CompletedAt":"2026-07-09T10:00:00+00:00", "Dlcs":[], "SkippedDependencies":[], "Files":{} } } }
JSON

# Outdated: the snapshot has b3.
cat > "$BACKUP/faster_than_light__1207666073/.gogdl.json" <<'JSON'
{ "SchemaVersion":1, "GameId":1207666073, "Slug":"faster_than_light", "Title":"FTL",
  "Platforms": { "Windows": { "BuildId":"b0", "VersionName":"1.5", "Language":"en",
    "ArchiveName":"ftl.zip", "ArchiveSize":1, "InstalledSize":1,
    "CompletedAt":"2026-07-09T10:00:00+00:00", "Dlcs":[], "SkippedDependencies":[], "Files":{} } } }
JSON

diffcmd() { dotnet run --project src/GogDownloader.Cli -- diff "$@" --out "$BACKUP" --snapshot tests/fixtures/library.json; }

expect "diff finds an outdated build"   "have 1.5 (b0), GOG has 1.6 (b3)"  diffcmd --platform windows
expect "diff counts what is current"    "1 up to date"                     diffcmd --platform windows
expect "diff flags a gen1 depot"        "generation 1 depot"               diffcmd --platform windows
expect "diff flags a depotless game"    "no depot"                         diffcmd --platform windows
expect "diff reports orphan directories" "stranger__42"                    diffcmd --platform windows
expect "diff totals what is missing"    "170 MiB to download"              diffcmd --platform windows
expect "diff --slugs feeds a download"  "faster_than_light"                diffcmd --slugs --platform windows

echo
echo "All checks passed."
