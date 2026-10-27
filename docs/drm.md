# DRM reports, filters and manual corrections

[Project overview](../README.md)

The aggregate library records DRM per store and OS as `Free`, `Protected` or `Unknown`, with
the source, evidence URL, check time and notes. GOG defaults to its DRM-free distribution policy;
Steam defaults to unknown. Steam ownership, Steamworks DLLs and the absence of a store warning
are not evidence that a game is DRM-free. The classification describes the reported DRM status,
not verified portability, offline play or availability of multiplayer/services.

## Fetch community reports

Fetch community reports separately from the normal library sync (no Steam login required).
For authenticated PCGamingWiki access, create a [bot password](https://www.pcgamingwiki.com/wiki/Special:BotPasswords)
with the `Create, query and delete data through the Cargo extension` grant, then log in with
the generated `Account@BotName` username and bot password:

```sh
dotnet run --project src/GameArchiver.Cli -- auth login --source pcgamingwiki
dotnet run --project src/GameArchiver.Cli -- library drm sync --source steam
# Public alternative when PCGamingWiki denies automated API access:
dotnet run --project src/GameArchiver.Cli -- library drm sync --source steam --drm-source steam-curator
dotnet run --project src/GameArchiver.Cli -- library list --source steam --drm any --platform windows --details
dotnet run --project src/GameArchiver.Cli -- download --all --source steam --drm free --max-size 100MB --dry-run
```

The password prompt hides input. For noninteractive login, supply `GAMEARCHIVE_PCGW_USERNAME`
and `GAMEARCHIVE_PCGW_BOT_PASSWORD` in the environment; `--username` can also select the bot
username. No command-line password option is provided. Only session cookies are saved, never
the bot password, in the user configuration directory's `gamearchive/pcgamingwiki-session.json`.
The file is not encrypted; access is restricted to its owner on Unix and the current user on
Windows. Use `--pcgw-session FILE` on both auth and DRM sync to select a different location.
`auth status --source pcgamingwiki` reports the saved account without a network request;
`auth logout --source pcgamingwiki` deletes the local session. Revoke the bot password on the
wiki to revoke all its sessions. When a saved session expires, log in again.

`library drm sync` automatically uses the saved PCGamingWiki session. It resolves exact Steam
AppIDs through authenticated Cargo queries, fetches page wikitext and checks matching Steam
availability rows. Without a saved session, it retains the anonymous AppID-resolver flow.
An invalid or expired session never silently falls back to anonymous access. Reports are
cached in `library.json` for seven days; `--refresh` bypasses that cache.
Missing, conditional, conflicting or unrecognized evidence stays unknown. Unscoped reports
are applied only to Windows; native Linux/macOS ports need explicit OS evidence or an override.
If PCGamingWiki is unavailable (including HTTP 403), enrichment reports one failure, stops
querying and leaves unrefreshed classifications unknown. Normal library sync/download remains
independent. Filters use saved metadata without network access. The download summary separates
free, protected and unknown counts before applying the size filter; `--details` shows excluded
products and their evidence. Unknown does not mean DRM was detected. A normal download does
not run a DRM test; use the [local test command](local-drm-tests.md) or record a manual correction below.
Login addresses API authentication requirements, but cannot bypass a Cloudflare access block.
Cloudflare refusal, rejected credentials, expired sessions and missing Cargo permissions have
separate diagnostics. The live authenticated flow requires a PCGamingWiki account; offline
HTTP probes cover login, cookie persistence and authenticated enrichment.

## Alternative community source

`--drm-source steam-curator` reads the public [DRM-Free Complete List](https://store.steampowered.com/curator/45614612/)
instead, without a PCGamingWiki account or Steam login. It fetches the complete paginated list
once per refresh and matches exact AppIDs. Only an unqualified DRM-free report for an entry
with Windows support becomes `Free`; absent, conditional and native Linux/macOS reports stay
`Unknown`. Absence never means `Protected`. This secondary source does not reproduce all
PCGamingWiki notes and may be incomplete or outdated. Its source, link and description are saved.
Selecting a different community source replaces automatic reports for the selected products;
manual overrides still win. Both sources use the seven-day snapshot cache. See
[curator data and validation](steam-curator-drm.md).

## Manual corrections and filters

Manual corrections take precedence and survive subsequent store and DRM syncs. They apply to
the selected store, AppID, OS and branch (public by default); `--note` records the evidence:

```sh
dotnet run --project src/GameArchiver.Cli -- library drm set steam:12345 --platform windows --drm free --note "Tested this Windows release without Steam"
dotnet run --project src/GameArchiver.Cli -- library drm clear steam:12345 --platform windows
```

Replace the example ID with a product in your snapshot. Setting `unknown` explicitly overrides
a community claim. Clearing the override reveals a matching local test, or the community/default value. `--drm` accepts
`free`, `protected`, `unknown` and `any` for `library list` and `download --all`; unknown never
passes the free filter. A filter applies to the resolved base game, not a DLC's standalone size
or DRM label. New archives record the selected report in `Platforms.{OS}.Drm`, including GOG's
legacy metadata. Existing completed archives are not rewritten by DRM enrichment or overrides.
These annotations do not affect archive freshness or remove DRM.

### Use the current game's metadata

Inside a downloaded game's folder, omit `SOURCE:ID` to read its identity from `gamearchiver.json`
or either legacy metadata filename. A single archived platform and its branch are inferred too:

```sh
# Run from the game's folder; use an absolute project path outside the checkout.
dotnet run --project /path/to/GameArchiver/src/GameArchiver.Cli -- library drm set --drm free --note "Tested without Steam"
dotnet run --project /path/to/GameArchiver/src/GameArchiver.Cli -- library drm clear
```

Multiple archived platforms require `--platform`. Explicit `--platform` and `--branch` override
the local selection; a conflicting `--source` is rejected. Providing `SOURCE:ID` uses explicit
selection without reading the current folder. Corrections are saved in the library snapshot
(`--snapshot` can override its path); local metadata and ZIPs are only read, not rewritten.
The selected product and platform must already exist in that snapshot.

## What a DRM report means

DRM status is separate from a verified requirement to run the Steam client. A `Free` report
describes an edition reported as DRM-free, not a test of this archived build. `Protected` can
refer to Steam DRM or another system. Steam login/ownership is still required to obtain Steam
downloads, and online features may depend on Steam even if the game itself runs without it.

See [the PCGamingWiki adapter and its limits](pcgamingwiki.md).

## Automatic local tests

On Windows, [test downloaded games locally](local-drm-tests.md) to classify the archived build
by observing its process tree and Steam. Tests run autonomously and resume after interruption.
Precedence is **manual override > local test for matching content > community report**.
Local testing is a process heuristic, not proof that the game will work on a clean machine.
