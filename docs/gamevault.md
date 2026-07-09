# GameVault — verified reference

Backups are named so [GameVault](https://gamevau.lt/) can index them. This is a **cache of
verified facts**, because the published docs leave open the two questions that decide our
layout: whether subfolders are indexed, and what happens to non-game files.

Verified 2026-07-09 against [gamevault-backend][backend] commit `f95cc9f` (2026-02-15),
`src/modules/games/files.service.ts` and `src/configuration.ts`.

Refresh: `scripts/vendor-ref.sh gamevault`.

[backend]: https://github.com/Phalcode/gamevault-backend

## Naming convention

```
Title (Version) (EarlyAccess) (GameType) (NoCache) (ReleaseYear).zip
```

Case sensitive; every flag is optional. Implemented by `GameVaultName`.

| Flag | Literal | Source |
|---|---|---|
| Version | `(v1.2.3)` | regex `\((v[^)]+)\)` — must start with lowercase `v` |
| Early access | `(EA)` | regex `\(EA\)` |
| Game type | `(W_P)` `(W_S)` `(W_SW)` `(L_P)` `(L_SW)` | literal match, overrides autodetection |
| No cache | `(NC)` | skips external metadata lookup |
| Release year | `(2016)` | regex `\((\d{4})\)` |

Game types: `W_P` Windows portable, `W_S` Windows needs a setup step, `W_SW` Windows
software (not a game), `L_P` Linux portable, `L_SW` Linux software. **There is no macOS
type**, so macOS archives stay "undetected".

## The two things the docs do not say

**Subfolders are indexed.** `readAllFiles()` calls `readdirp` with
`depth: configuration.GAMES.SEARCH_RECURSIVE ? undefined : 0`, and `SEARCH_RECURSIVE`
defaults to `true` in `configuration.ts`. So a `{slug}__{gameId}/` directory per game is fine.

**Non-game files are ignored, not errors.** `isValidFilePath()` rejects anything whose
extension is not in `SUPPORTED_FILE_FORMATS`, logging at debug level. `.gogdl.json` next to
the archive is therefore invisible to the indexer.

## How the title is derived — the part that bites

```js
path.basename(filePath, path.extname(filePath))
    .replace(/\([^)]*\)/g, "")   // every round-bracket group is stripped
    .replace(/\s+/g, " ")
    .trim();
```

So **any round brackets in a real title are destroyed**. `Fallout 2 (GOTY)` would index as
`Fallout 2`. Use square brackets instead.

Filenames containing `/ < > : " \ | ? *` are rejected outright by `isValidFilePath()`
(regex `/[/<>:"\\|?*]/`). A colon is common in GOG titles, so it becomes ` -`.

## Release year: do not use GOG's

GOG's `release_date` from `api.gog.com/products/{id}` is **not** the year the game came out.
Sampled 2026-07-09:

| `release_date` | Title | Actually released |
|---|---|---|
| `1991-12-24` | The Witcher 2 | 2011 |
| `2014-12-24` | Akalabeth: World of Doom | 1979 |
| `2016-08-31` | Titan Quest Anniversary Edition | 2016 |
| `null` | Blackwell Legacy | — |
| `2015-05-19` | The Witcher 3: Wild Hunt | 2015 |

GameVault feeds the year to its metadata matcher, so a wrong year points it at a different
game. We omit the flag entirely.

`in_development.active` from the same endpoint is reliable and becomes `(EA)`.

## Supported archive extensions

Whatever 7zip 23.01 reads: `.7z .xz .bz2 .gz .tar .zip .wim .ar .arj .cab .chm .cpio .cramfs
.dmg .ext .fat .gpt .hfs .ihex .iso .lzh .lzma .mbr .msi .nsis .ntfs .qcow2 .rar .rpm
.squashfs .udf .uefi .vdi .vhd .vmdk .xar .z`

We write `.zip`. Chained extensions like `.tar.gz` are not supported — only the last one counts.
