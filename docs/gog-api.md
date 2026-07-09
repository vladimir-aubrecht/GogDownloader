# GOG API — verified reference

GOG has no public API documentation. This document is a **cache of verified facts** so the
endpoints do not have to be looked up again. Verified 2026-07-09 against [heroic-gogdl][gogdl]
(commit `4928e46` on `main`), the downloader that powers Heroic Games Launcher.

Refresh: `scripts/refresh-gog-api-ref.sh` (clones the sources and prints the current endpoints).

[gogdl]: https://github.com/Heroic-Games-Launcher/heroic-gogdl

## Hosts

| Constant | Value |
|---|---|
| `GOG_AUTH` | `https://auth.gog.com` |
| `GOG_EMBED` | `https://embed.gog.com` |
| `GOG_API` | `https://api.gog.com` |
| `GOG_CONTENT_SYSTEM` | `https://content-system.gog.com` |
| `GOG_CDN` | `https://gog-cdn-fastly.gog.com` |
| `GOG_CLOUDSTORAGE` | `https://cloudstorage.gog.com` |

> Careful: the CDN host is `gog-cdn-fastly.gog.com`, **not** `cdn.gog.com`.

## Authentication (OAuth2)

Public credentials of the Galaxy client (they live in gogdl's open source):

- `client_id` = `46899977096215655`
- `client_secret` = `9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9`
- `redirect_uri` = `https://embed.gog.com/on_login_success?origin=client`

Exchanging a code for a token:

```
GET {GOG_AUTH}/token?client_id=…&client_secret=…&grant_type=authorization_code
    &redirect_uri=…&code={code}
```

Refreshing a token:

```
GET {GOG_AUTH}/token?client_id=…&client_secret=…&grant_type=refresh_token
    &refresh_token={refresh_token}
```

The access token is short-lived (roughly an hour), the refresh token is long-lived.

## Library and metadata

| Purpose | Endpoint |
|---|---|
| List of owned `gameId`s | `GET {GOG_EMBED}/user/data/games` |
| Game details + offline installers | `GET {GOG_EMBED}/account/gameDetails/{id}.json` |
| Product data | `GET {GOG_API}/products/{id}` (`?expand=…`) |

## Content system (Galaxy depots) — Path A

Builds for a platform. `build_platform` is one of `windows`, `osx`, `linux`:

```
GET {GOG_CONTENT_SYSTEM}/products/{gameId}/os/{build_platform}/builds?&generation={1|2}
```

> This endpoint was originally documented here without the `/os/{platform}/` segment —
> **that was wrong.**

The response has `items[]`; each entry carries `branch` and `repository_manifest` (a direct
URL). When `items` is empty the game has no content system support, so the depot path is
impossible.

Manifests are **zlib-compressed JSON** (`zlib.decompress(data, 15)`).

### galaxy_path

A manifest hash maps onto a CDN path by inserting two directory levels:
`abcdef…` becomes `ab/cd/abcdef…` (unless the hash already contains a slash).

| Purpose | URL |
|---|---|
| Depot manifest (v2) | `{GOG_CDN}/content-system/v2/meta/{galaxy_path(manifest)}` |
| Patch manifest (v2) | `{GOG_CDN}/content-system/v2/patches/meta/{galaxy_path(manifest)}` |
| Depot manifest (v1) | `{GOG_CDN}/content-system/v1/manifests/{gameId}/{platform}/{timestamp}/{manifest}` |

Generation 1 is the older format — its manifest is **uncompressed JSON** and the path contains
a `timestamp` from the product data. A non-trivial difference from v2; expect both.

### Schema of v2 manifests

The repository manifest (the URL in `build.repository_manifest`) after zlib decompression:

| Field | Meaning |
|---|---|
| `baseProductId` | id of the base game |
| `buildId` | needed by the patch endpoint |
| `installDirectory` | name of the install directory (**do not use it for our zip**) |
| `dependencies[]` | ids of redistributables |
| `products[]` | `productId` + `name` of the base game **and all its DLC** |
| `depots[]` | see below |

The DLC list therefore never has to be fetched separately: take `products[]` from the
manifest, drop `baseProductId`, and intersect the rest with the owned ids from
`{GOG_EMBED}/user/data/games`.

A depot has `manifest` (hash), `productId`, `languages[]`, `osBitness`, `size` and
`compressedSize`. A depot belongs in the download when `productId` is the base game or an
owned DLC, **and** `languages` contains `*` or the target language.

The depot manifest (`{GOG_CDN}/content-system/v2/meta/{galaxy_path(depot.manifest)}`, zlib
again) has `depot.items[]`, each with a `type`:

- `DepotFile` — `path`, `chunks[]`, optionally `md5`, `sha256`, `flags[]`
- `DepotDirectory` — just a `path`, a directory to create
- `DepotLink` — `path` + `target`, a symlink whose target is relative to the install root

`flags` may contain `support` (the file belongs in `gog-support`, not in the game) and
`executable`. Paths in `path` use `\` as the separator and must be translated on Linux.

A chunk has `compressedMd5`, `md5`, `size` and `compressedSize`. A file's identity is `md5`,
or `chunks[0].md5` when that is missing (which only holds for single-chunk files).

### Secure links (signed URLs for chunks)

```
generation 2:  GET {GOG_CONTENT_SYSTEM}/products/{gameId}/secure_link?_version=2&generation=2&path=/
generation 1:  GET {GOG_CONTENT_SYSTEM}/products/{gameId}/secure_link?_version=2&type=depot&path=/
```

Returns an array of `urls`. A secure link is obtained **separately for every owned DLC
product**, not just for the base game. (Optionally `&root=/patches/store` for patches.)

Every element of `urls[]` has `url_format` and `parameters`. A chunk URL is assembled like
this — the order of the steps matters:

1. `parameters["path"] += "/" + galaxy_path(chunk.compressedMd5)`
2. replace every `{key}` in `url_format` with `parameters[key]`

The chunk body is **zlib-compressed**. Its MD5 is checked against `compressedMd5`, i.e. over
the *downloaded* bytes before inflating, not after.

A `401` on a chunk means the secure link expired; it is not a download failure.

### Dependencies / redistributables

| Purpose | Endpoint |
|---|---|
| Dependency repository | `GET {GOG_CONTENT_SYSTEM}/dependencies/repository?generation=2` |
| Store link | `GET {GOG_CONTENT_SYSTEM}/open_link?generation=2&_version=2&path=/dependencies/store/` |

## Linux — a separate path

**Galaxy depots do not exist for Linux.** For `platform == "linux"` gogdl bypasses the content
system entirely and takes the offline MojoSetup installers from `gameDetails` (filtering
`installer["os"] == "linux"` and selecting by `installer["language"]`; English installers tend
to be multilanguage).

The essential trick gogdl uses: a MojoSetup installer is a shell script with a ZIP payload, and
a ZIP keeps its central directory at the end. **HTTP Range requests** therefore make it
possible to read the central directory and download only the entries that are needed — **the
installer is never downloaded in full.** (Source: a comment in `gogdl/dl/managers/linux.py`:
"new zip method based on Range headers".)

That satisfies the requirement of never storing installers.

Curiosity: `linux.py` requests `os/windows/builds` even for a Linux game. I did not investigate
why — probably to determine the version or whether the title exists. Do not copy this blindly.

## What gogdl cannot do

Games with no content system (an empty `builds.items`) are **unsupported** by gogdl on
Windows/macOS — it errors out. So even the reference implementation gives up on the Inno Setup
installer path. For such titles we are on our own (innoextract / Wine).

`library stats` over the real library reports **24** such Windows/macOS titles out of 256 games.
