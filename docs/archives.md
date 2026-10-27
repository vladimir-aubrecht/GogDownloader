# Archive files and metadata

[Project overview](../README.md)

Run commands from the repository root, with the [output layout](cli.md#output-folders) matching your backups.

## Add, find or replace files in existing ZIPs

The unified CLI can edit all completed archives in a backup directory without logging into a
store or loading a library snapshot. It discovers ZIPs through `gamearchiver.json` and the old
`.gogdl.json` / `.gamebackup.json` names;
unmanaged ZIPs and unreferenced leftover archives are not touched. Source selection and output
layout work exactly as for `verify`. All platforms are included unless `--platform` is supplied.

```sh
# Add readme.txt at the root of every GOG archive; preview first.
dotnet run --project src/GameArchiver.Cli -- archive add --file ./readme.txt --out /backups/gog --source gog --dry-run
dotnet run --project src/GameArchiver.Cli -- archive add --file ./readme.txt --out /backups/gog --source gog

# Choose a different destination inside each ZIP.
dotnet run --project src/GameArchiver.Cli -- archive add --file ./readme.txt --entry extras/info.txt --out /backups --layout provider

# Find by filename anywhere in the ZIP, or by its exact relative path.
dotnet run --project src/GameArchiver.Cli -- archive find --name readme.txt --out /backups --layout provider
dotnet run --project src/GameArchiver.Cli -- archive find --path docs/readme.txt --out /backups --layout provider

# Replace every matching file, retaining each original next to it.
dotnet run --project src/GameArchiver.Cli -- archive replace --name readme.txt --file ./replacement.txt --out /backups --layout provider --dry-run
dotnet run --project src/GameArchiver.Cli -- archive replace --path docs/readme.txt --file ./replacement.txt --out /backups --layout provider
```

`add` defaults to the source filename at the ZIP root. If that entry already exists, its
original content becomes `readme.txt.backup`. Existing backups are never overwritten: the next
available name is `readme.txt.backup.backup`, and so on. Case-only collisions during `add`
replace the existing entry while retaining its spelling. `replace` changes only matched files;
it does not add anything to archives with no match. Names/paths are literal (no glob patterns),
case-sensitive unless `--ignore-case` is supplied. A positional search after `add`, `find` or
`replace` filters games by title/slug. Exit status is nonzero on any failure or when nothing matches.

Each ZIP is rewritten using bounded buffers and existing compressed payloads; games are never
unpacked or recompressed in full. The output volume needs space for a second ZIP while that
archive is being edited. Source compression uses local `--staging` and is shared across the batch.
Inserted content is verified, and archive/installed sizes and file hashes are updated in metadata;
`ModifiedAt` records the local edit while the downloaded version and `CompletedAt` remain unchanged.
To verify existing content as well, run `verify --deep` separately.

Publication adds a `(local-<unique-id>)` marker before `.zip` (ignored by GameVault's title parser).
Long filenames are shortened to fit 255 UTF-8 bytes, preserving Unicode characters and GameVault
type tags; exceptionally long version labels are shortened too. Temporary ZIPs use independent
short names, and preview applies the same filename rules.
The new ZIP is published before metadata switches to it, then the old ZIP is removed. This keeps
the previous backup usable on failure; a process crash may leave an unreferenced ZIP or `.tmp`
file, but cannot switch metadata to a partial archive. Each archive commits independently, so
already completed edits remain after cancellation. Successive edits replace the filename marker.
These are local edits, not persistent download rules: a future update or forced download rebuilds
the game from store content, so custom additions/replacements need to be applied again.

## Visible archive metadata

Both providers write **`gamearchiver.json`** beside each game's ZIP, with `Source` identifying
`gog` or `steam`. The filename has no leading dot, and writes/migration clear the Windows Hidden
attribute. GOG retains its schema-1 payload (`GameId`, build IDs and MD5 inventory); Steam uses
schema 2 (`ProductId`, depot selection and SHA1 inventory).

Existing `.gogdl.json` and `.gamebackup.json` files remain readable. The next download migrates
them even if the archive is already current; ZIP edits migrate them too. No game content is
downloaded or rewritten just to rename metadata. `library sync`, `diff`, `verify` and previews
do not rename these files. To migrate an existing library without downloading:

```sh
dotnet run --project src/GameArchiver.Cli -- archive migrate --source steam --out /backups/steam --dry-run
dotnet run --project src/GameArchiver.Cli -- archive migrate --source steam --out /backups/steam
# Or both providers in /backups/gog and /backups/steam:
dotnet run --project src/GameArchiver.Cli -- archive migrate --out /backups --layout provider
```

Use the output/layout settings matching your existing folders. Migration preserves metadata
fields, adds the missing GOG `Source`, and removes recognized old files after committing the new
one. It can be retried after interruption. Conflicting ownership fails without merging games.
The canonical file wins if a same-owner legacy copy remains. Use updated CLIs and the updated
GameVault sync script with migrated backups; old binaries do not recognize the new filename.
