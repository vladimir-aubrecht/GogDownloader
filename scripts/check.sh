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
dotnet run --project tests/GogArchiver.Probes -v q --nologo

echo
echo "== cli against tests/fixtures/library.json =="
# --snapshot keeps the real library in the user's profile untouched.
gogarchiver() { dotnet run --project src/GogArchiver.Cli -- "$@" --snapshot tests/fixtures/library.json; }

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

expect "list finds every game"        "5 games"                    gogarchiver library list
expect "list searches by title"       "the_witcher_3_wild_hunt"    gogarchiver library list witcher
expect "list shows owned DLC"         "Blood and Wine"             gogarchiver library list witcher
expect "list shows sizes"             "30 GiB"                     gogarchiver library list witcher
expect "list filters by DLC"          "1 game,"                    gogarchiver library list --dlc
expect "list marks installer-only"    "(osx)"                      gogarchiver library list ultima
expect "list rejects bad platform"    "Unknown platform"           gogarchiver library list --platform amiga
expect "list reports no matches"      "Nothing matches"            gogarchiver library list zzzznope
expect "list --slugs prints bare slugs" "faster_than_light"        gogarchiver library list --slugs --depot --platform windows
expect "stats counts inno titles"     "needing innoextract: 1"     gogarchiver library stats
expect "stats counts unsupported"     "cannot be backed up at all: 0"  gogarchiver library stats
expect "stats counts gen1 depots"     "gen1:    1"                 gogarchiver library stats
expect "stats pluralises correctly"   "1 title "                   gogarchiver library stats
# A pack of games the library owns separately is never downloaded, so it must not be counted.
expect "stats names duplicate packs"  "Packs skipped as duplicates (their games are owned separately): 1"  gogarchiver library stats
expect "stats leaves packs out of the totals" "Windows     4 titles" gogarchiver library stats
expect "download needs a game"        "Missing game"               gogarchiver download
# A flag's value must never be mistaken for the game name.
expect "download ignores flag values" "Missing game"               gogarchiver download --platform windows
expect "download rejects unknown game" "is not in the library"     gogarchiver download nonexistent
expect "download refuses no depot"    "The library has nothing"    gogarchiver download ultima_1_2_3 --platform linux
# macOS installers are neither a depot nor Inno Setup, so they stay unsupported.
expect "macOS installers stay unsupported" "not Inno Setup"        gogarchiver download ultima_1_2_3 --platform mac
expect "list marks inno titles"       "win:inno"                   gogarchiver library list ultima


# Linux has no depots at all; its games come out of the installer.
expect "linux games are backupable"   "1 game for linux"           gogarchiver download --all --dry-run --platform linux
expect "list marks the linux installer path" "lin:installer"       gogarchiver library list faster_than_light --platform linux

# Batch mode must select exactly what `library list` shows.
expect "download --all includes gen1 and inno" "4 games for windows"  gogarchiver download --all --dry-run --platform windows
expect "download --all lists the gen1 title" "Ancient Bundle"        gogarchiver download --all --dry-run --platform windows
expect "list marks gen1 depots"          "win:gen1"                  gogarchiver library list ancient
expect "download --all honours the search"     "1 game for windows"   gogarchiver download --all witcher --dry-run --platform windows
expect "download --all honours --dlc"          "1 game for windows"   gogarchiver download --all --dlc --dry-run --platform windows
expect "download --all reports no matches"     "Nothing matches"      gogarchiver download --all zzzznope --dry-run
expect "download --all rejects bad platform"   "Unknown platform"     gogarchiver download --all --dry-run --platform amiga

# A pack is a container GOG never installs; its games are separate products, already backed up.
expect "download skips a duplicate pack"  "a pack of games the library has on its own"  gogarchiver download prehistorik_12
expect "a pack names what covers it"      "FTL: Faster Than Light, Ultima 1+2+3"        gogarchiver download prehistorik_12
expect "a lone pack leaves nothing to do" "Nothing left to download"                    gogarchiver download prehistorik_12
expect "a batch drops the pack too"       "Skipping Prehistorik 1+2"                    gogarchiver download --all --dry-run --platform windows

echo
echo "== aggregate CLI compatibility =="
gamearchiver() { dotnet run --project src/GameArchiver.Cli -- "$@"; }
expect "aggregate CLI describes both sources" "GOG and Steam" gamearchiver --help
expect "aggregate CLI can read the original GOG snapshot" "gog:1207658930" \
    gamearchiver library list --source gog --snapshot tests/fixtures/library.json
expect "aggregate CLI accepts qualified Steam IDs without a snapshot" "steam:427520" \
    gamearchiver download steam:427520 --dry-run --snapshot tests/fixtures/nonexistent.json
expect "aggregate CLI refuses unknown sources" "Unknown source" gamearchiver library list --source nonexistent


echo
echo "== diff against a fixture backup directory =="
BACKUP=$(mktemp -d)
trap 'rm -rf "$BACKUP"' EXIT

mkdir -p "$BACKUP/the_witcher_3_wild_hunt" "$BACKUP/faster_than_light__1207666073" "$BACKUP/stranger__42"

# Only an identified GOG backup is an orphan; other stores can share this directory.
cat > "$BACKUP/stranger__42/.gogdl.json" <<'JSON'
{ "Source":"gog", "GameId":42, "Slug":"stranger", "Title":"Stranger", "Platforms":{} }
JSON

# Up to date: build b1 matches the snapshot.
cat > "$BACKUP/the_witcher_3_wild_hunt/.gogdl.json" <<'JSON'
{ "SchemaVersion":1, "GameId":1207658930, "Slug":"the_witcher_3_wild_hunt", "Title":"The Witcher 3",
  "Platforms": { "Windows": { "ContentRevision":1, "BuildId":"b1", "VersionName":"1.32", "Language":"en",
    "ArchiveName":"w3.zip", "ArchiveSize":1, "InstalledSize":1,
    "CompletedAt":"2026-07-09T10:00:00+00:00", "Dlcs":["1640424747","1495134380"], "SkippedDependencies":[], "Files":{} } } }
JSON

# Outdated: the snapshot has b3.
cat > "$BACKUP/faster_than_light__1207666073/.gogdl.json" <<'JSON'
{ "SchemaVersion":1, "GameId":1207666073, "Slug":"faster_than_light", "Title":"FTL",
  "Platforms": { "Windows": { "BuildId":"b0", "VersionName":"1.5", "Language":"en",
    "ArchiveName":"ftl.zip", "ArchiveSize":1, "InstalledSize":1,
    "CompletedAt":"2026-07-09T10:00:00+00:00", "Dlcs":[], "SkippedDependencies":[], "Files":{} } } }
JSON

diffcmd() { dotnet run --project src/GogArchiver.Cli -- diff "$@" --out "$BACKUP" --snapshot tests/fixtures/library.json; }

# A completion manifest alone is not a backup. These fixture archives have the recorded size.
printf x > "$BACKUP/the_witcher_3_wild_hunt/w3.zip"
printf x > "$BACKUP/faster_than_light__1207666073/ftl.zip"

expect "diff finds an outdated build"   "have 1.5 (b0), GOG has 1.6 (b3)"  diffcmd --platform windows
expect "diff counts what is current"    "1 up to date"                     diffcmd --platform windows
expect "diff reports orphan directories" "stranger__42"                    diffcmd --platform windows
expect "diff totals what is missing"    "to download"                      diffcmd --platform windows
expect "diff counts the gen1 game as missing" "2 missing, 1 outdated"       diffcmd --platform windows
expect "diff --slugs feeds a download"  "faster_than_light"                diffcmd --slugs --platform windows
# macOS installers are neither a depot nor Inno Setup, so diff must say why.
expect "diff explains the macOS gap"   "not extractable"                  diffcmd --platform mac
# A duplicate pack is not missing, or diff would ask for it forever.
expect "diff parks packs of their own"  "Packs, skipped as duplicates"    diffcmd --platform windows
expect "diff counts duplicate packs"    "1 duplicate pack"                diffcmd --platform windows

echo
echo "== verify against the fixture backup directory =="
rm "$BACKUP/the_witcher_3_wild_hunt/w3.zip" "$BACKUP/faster_than_light__1207666073/ftl.zip"
verifycmd() { dotnet run --project src/GogArchiver.Cli -- verify --out "$BACKUP" "$@"; }

# Both manifests name an archive nobody ever wrote. Saying "sound" about a file that is not
# there would be the one answer worse than an error.
expect "verify notices an archive is gone"  "the archive is gone"          verifycmd
expect "verify counts what it checked"      "0 sound, 0 broken, 2 missing" verifycmd
expect "verify says how to download again" "--force --platform windows"   verifycmd
expect "verify --slugs feeds a download"    "faster_than_light"            verifycmd --slugs
expect "verify searches by title"           "1 archive"                    verifycmd witcher
expect "verify needs a backup directory"    "No such backup directory"     \
    dotnet run --project src/GogArchiver.Cli -- verify --out "$BACKUP/nope"
# --fix mends headers; an archive that is not there is not header damage.
expect "--fix cannot conjure a missing archive" "0 repaired, 0 broken, 2 missing"  verifycmd --fix

echo
echo "== GameVault sync against a fixture API =="
sh tests/gamevault-sync.sh

echo
echo "All checks passed."
