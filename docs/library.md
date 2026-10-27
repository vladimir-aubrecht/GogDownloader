# Library sync, counts and comparison

[Project overview](../README.md)

Run commands from the repository root. Authentication and output settings are described
in the [CLI guide](cli.md).

## Sync and inspect the library

```sh
dotnet run --project src/GameArchiver.Cli -- library sync --source steam
dotnet run --project src/GameArchiver.Cli -- library list --source steam
dotnet run --project src/GameArchiver.Cli -- library stats --source steam
```

Replace `steam` with `gog`, or use `all` after authenticating both stores. The local snapshot
also supports offline listing, size/DRM filters and `diff`.

## Product counts and Steam's primary catalog

`Saved N products` counts unique `(source, product ID)` entries in the saved snapshot, including
DLC, applications, demos and tools. It is neither a license count nor the Steam client's game
count. Sync also prints a per-source breakdown by product type; `library stats --source steam --inventory`
shows the complete inventory breakdown offline. A source-filtered sync retains the other stores' entries and
older entries for failed products, so the saved total can include both.

Steam sync now reads the full validated `GetOwnedGames` list once to establish the **primary
catalog**, separately from the package-license inventory. The primary catalog controls normal
`library list` / `library stats`, `download --all` and `diff`. Use `library list --source steam
--inventory` to inspect all known Steam products, including DLC, tools and games outside that
catalog. Sync prints the primary-catalog count separately, including listed products whose
metadata could not be loaded. Missing license grants remain failures even for catalog entries.

The inventory still supplies owned DLC and shared-depot references. Searching a DLC during a
batch download resolves to its catalog parent. An explicit `download steam:APPID` remains
available outside the catalog and checks live licenses and content access. Existing archives
are not moved or removed; inventory products are not treated as orphaned just because they
are outside the primary catalog. Old snapshots retain their previous selection behavior until
a successful Steam sync adds catalog membership. GOG selection behavior is unchanged.

If the complete catalog or package inventory cannot be read, the previous source entries,
catalog membership and failure diagnostics are retained, with a source failure. An explicitly
empty catalog is valid and never falls back to selecting every licensed product.

## Sync failures

If Steam reports missing access tokens for individual apps, sync retries each affected app
once with a fresh individual token request. Persistent failures include the token response
states and owning package IDs, with no token values. Confirmed access denials have a shorter
default display; `--details` on `library sync` or `library failures` shows the complete diagnostic.
They remain recorded failures with a nonzero sync exit status; a name
alone does not make an app downloadable. Failures show a title from Steam or an earlier snapshot
when available, otherwise `Name unavailable (steam:ID)`.

Multiple active licenses granting the same AppID are combined before appinfo is requested.
A different accessible app in the same package does not prove that the denied app is available,
even if their titles match. Such failures are not suppressed.

Inspect the saved failures without logging in or repeating sync:

```sh
dotnet run --project src/GameArchiver.Cli -- library failures --source steam
dotnet run --project src/GameArchiver.Cli -- library failures "title or AppID" --source steam
dotnet run --project src/GameArchiver.Cli -- library failures --source steam --details
```

Confirmed failures show `Blocks <title> (steam:APPID)` with the platform/language/architecture/
branch and required AppID/depot. Searches also match blocked games' names. Inventory metadata
failures without a confirmed blocking link are labelled separately; that does **not** establish
they are harmless for other selections. They remain saved failures and keep sync's exit status
nonzero. Local shared manifests can make a parent's selection succeed without reading the
source app's metadata, but they do not resolve the source app's own metadata failure.
If a game leaves the primary catalog while remaining licensed, sync refreshes its inventory
metadata without checking its download selection. A previous selection failure stays saved
and is labelled `Previously blocked (not rechecked)` until a later selection check resolves it.
Successful appinfo alone clears only the app's metadata failure, not its unchecked selection.
Returning to the catalog also preserves this evidence if appinfo fails before selection starts.

## Compare with live ownership

### Steam

To investigate differences from Steam's game count, compare the saved snapshot with the
full `GetOwnedGames` list using the remembered Steam login (no Web API key is needed):

```sh
dotnet run --project src/GameArchiver.Cli -- library compare --source steam
```

`SnapshotGameOnly` identifies snapshot products tagged `game` but absent from that API list;
`GetOwnedGamesOnly` identifies API entries absent from the snapshot. `OtherListedProduct`
shows listed products classified as software, music, DLC or other types in the snapshot.
`SyncFailure` shows whether each failed AppID appears in the live list, using its returned
name when available; `--details` prints the full saved error. A soundtrack is not reclassified
based on its title. The comparison matches AppIDs, never names.

The command makes one `GetOwnedGames` request, asking for free games including unplayed
titles, free packages and unvetted apps. This API list is not necessarily the Steam client's
displayed count or a complete inventory of licensed products. Steam can omit entries depending
on service filters and visibility; absence does not erase a licensed product from the inventory
or prevent an explicit download. The next successful sync uses this list for default catalog selection.
It does not refresh PICS, download content or change the snapshot. Being listed does not
establish depot access or clear a sync failure. Exit 0 means the comparison completed, even
when it finds differences; inaccessible, incomplete or invalid responses cause exit 1.
Use `--snapshot FILE` to select a saved library. In PowerShell, append
`| Tee-Object steam-comparison.txt` to save the report.

### GOG

GOG supports the same diagnostic command with its saved login:

```sh
dotnet run --project src/GameArchiver.Cli -- library compare --source gog
```

It compares **all GOG product IDs**, including DLC and packs, with the live
`/user/data/games` ownership list, the same endpoint used by GOG sync. `SnapshotOnly` shows
saved products no longer listed as owned; `OwnedOnly` shows owned products absent from the
snapshot. `SyncFailure` retains each saved GOG failure and says whether that ID is currently
owned; `--details` prints its full error. Names come from the snapshot or saved failure names:
this endpoint returns only IDs, so newly owned products may show `Name unavailable`.
Unlike the Steam diagnostic, this is a fresh check of the sync's ownership source, not a
comparison against a second catalog API. It does not check build versions or download access.

Use `--snapshot FILE` for either an aggregate library or an original GOG library snapshot.
The command only reads ownership, leaving both snapshots, their failures and archives unchanged.
It does not fetch product details, builds or depots. Differences and saved failures still mean
exit 0 if comparison completed; authentication, HTTP and malformed-response errors mean exit 1.
Neither provider's comparison changes the selected download targets or resolves sync failures.

## Snapshot fields for API consumers

The aggregate snapshot adds optional `PrimaryCatalogs` (provider to product-ID set) and
`FailureImpacts` (failure key to affected selections, with `IsStale` for unchecked evidence).
Provider results identify metadata-only refreshes through `StoreLibraryResult.MetadataOnlyProducts`.
`GameVersion.Dependencies` records
required DLC metadata, inspected shared-app metadata and local shared-manifest references;
`MetadataRequired: false` distinguishes a reference that needs no appinfo lookup. These
fields also support C# consumers. Dependency provenance does not invalidate an otherwise
identical archive; actual depot/manifest selection still determines content identity.

See the [C# API guide](api.md) for embedding and snapshot access.
