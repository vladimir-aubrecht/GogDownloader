# SteamKit2 integration reference

Verified against SteamRE/SteamKit tag `3.4.0`, commit
`1c7bc9c` (the dependency pinned by `SteamArchiver.Core.csproj`). Reference source was read
from the upstream repository, not copied into the application. SteamKit2 is LGPL-2.1-only;
see [third-party notices](../THIRD-PARTY-NOTICES.md).

## Connection and authentication

- Create `SteamClient`, `CallbackManager`, `SteamUser`, `SteamApps`, `SteamContent`.
- Pump callbacks for the session lifetime, including while awaiting `AsyncJob` results.
- Wait for `SteamClient.ConnectedCallback` before authentication.
- Select the HTTP handler by `HttpClientPurpose`. `CMWebSocket` must use the underlying
  transport directly: .NET's `ClientWebSocket` reads the upgraded response synchronously and
  requires a duplex stream. The asynchronous, read-only CDN body wrapper breaks that handshake
  before credentials or Steam Guard are reached. SteamKit reports it as a disconnect.
- SteamKit's `Connect` does not retry automatically. A failed connection marks its CM endpoint
  bad; reuse the client to try another. The adapter allows three initial attempts with bounded
  backoff, sharing one `OperationTimeout`. Once connected, a disconnect terminates the session
  and reports its phase; it never silently repeats credentials or Steam Guard interaction.
- Credentials use `Authentication.BeginAuthSessionViaCredentialsAsync(AuthSessionDetails)`.
  Supply `IAuthenticator`; do not use SteamKit2's console authenticator in a library.
- `IAuthenticator` supports email codes, device codes and mobile approval. Poll with
  `PollingWaitForResultAsync(cancellationToken)`.
- `SteamUser.LogOnDetails.AccessToken` takes the **refresh token**, not the short-lived access
  token. `IsPersistentSession` and `ShouldRememberPassword` must agree.
- Save remembered credentials after `LoggedOnCallback.Result == EResult.OK`. Passwords do not
  belong in the saved session. New guard data is optional.
- Wait for `LicenseListCallback`; disconnection must stop trusting a logged-on state.

Primary sources: `Samples/000_Authentication/Program.cs`, `Steam/Authentication/IAuthenticator.cs`,
`AuthSession.cs`, `AuthPollResult.cs`, the `SteamUser` handlers, `Steam/CMClient.cs`,
`Networking/Steam3/WebSocketContext.cs`, and `Steam/SteamClient/Configuration`.

## Library and appinfo

`LicenseListCallback` supplies package IDs, flags and package access tokens. Ask PICS for package
info with those tokens and read package `appids`. Do not invent ownership by treating public
appinfo as an ownership grant. Expired/cancelled/not-activated licenses are excluded.
Union AppIDs from all active packages before fetching appinfo: duplicate licenses do not add
duplicate library entries. A package can contain games, DLC, software, demos and tools, so
the saved product count is not a license count or the Steam client's game count. Display the
product types separately, using `common/type`.

Sync separately fetches the complete validated `GetOwnedGames` catalog before processing the
license inventory. Catalog membership controls default archive roots, while active packages
still authorize roots and DLC. Query failure or an incomplete response fails the source and
preserves its prior products, catalog and unresolved failure evidence. A listed AppID without
an active package license is recorded as a catalog failure, not granted content access.
Keep all successful inventory appinfo and retain old entries for failed products; do not trim
the saved product collection to the API list. Only catalog roots receive default content
planning during sync. Non-catalog inventory appinfo does not trigger unrelated depot planning.

Request app access tokens with `PICSGetAccessTokens`, then app info with `PICSGetProductInfo`.
`AsyncJobMultiple.ResultSet.Complete` and `Failed` must be checked; partial results are not a
complete library. Large appinfo can carry `UseHttp` and `HttpUri`; that body must be read and
parsed instead of silently accepting empty KeyValues. Per-app failures are recorded separately.

`PICSTokensCallback.AppTokensDenied` is an explicit denial; an absent dictionary entry is not
the same response. A returned zero token is valid for public appinfo, while a nonzero token
must be passed through unchanged. If product info reports `MissingToken`, re-request the token
and product info once for that AppID alone. Cache the final result for the current sync so
references from several games cannot trigger repeated retries. A fresh sync clears that cache.
Persistent failures include the initial and retry token states and the package IDs that supplied
the AppID. Never include token values or infer that an app was retired from `MissingToken` alone.

Appinfo fields used by this provider:

| Path | Meaning |
|---|---|
| `common/name`, `common/type`, `common/oslist` | Name, product type and OS availability |
| `common/parent` | Parent AppID for a DLC product |
| `extended/listofdlc` | Candidate DLC AppIDs; intersect with licenses |
| `depots/branches/{branch}/buildid` | Branch build identifier |
| `depots/{depot}/config/oslist`, `osarch`, `language` | Depot selection constraints |
| `depots/{depot}/dlcappid` | DLC-specific content, gated by license |
| `depots/{depot}/depotfromapp` | Shared depot; resolve source appinfo only when the selected branch has no local manifest, with cycle checks |
| `depots/{depot}/manifests/{branch}/gid` | Unsigned 64-bit manifest ID |
| `depots/{depot}/manifests/{branch}/download`, `size` | Full compressed/download and unpacked byte estimates for that manifest |
| `config/launch/*` | Executable/arguments and OS constraint |

Main content precedes DLC content when merging paths. The archive's identity includes the final
depot/manifest selection, not just the top-level build number. Private/encrypted branch manifests
are rejected rather than silently replaced by another branch.

Record both sizes for the final selected depot set, after OS/language/architecture/DLC
filtering and shared-depot resolution. Count repeated depot/manifest pairs once. Size data
from another manifest must never be reused after a DLC override. Missing or malformed size
fields make that total unknown (zero in `GameVersion`), rather than summing only known depots.
A known zero-byte depot is valid when other selected depots have content. Size estimates do
not participate in `GameVersion.Matches`, so learning a size does not invalidate a backup.

Primary sources: `Steam/Handlers/SteamApps/SteamApps.cs`, `Callbacks.cs`, `Types/KeyValue.cs`.

### Shared depot manifests and unavailable appinfo

A shared depot can publish its manifest directly in the licensed game's appinfo alongside
`depotfromapp`. Prefer that manifest for the selected branch; the source application's metadata
may be inaccessible and may describe a different manifest. Look up source appinfo only when
the local entry has no manifest for that branch. Authorize depot keys and manifest requests
against the licensed parent game, and retain the shared depot in the archive and completion
metadata. A failed depot download must still prevent completion.

Before each shared-app lookup, check the current depot's known OS, license, architecture and
language constraints. Keep incompatible language/architecture variants in availability checks
so an unavailable requested variant cannot silently produce a partial archive of common files.
When walking multiple sources, carry the accumulated constraints forward instead of merging
every hop against the original unconstrained entry. Retain cycle detection and the preference
for local branch manifests.

Each successful selection records `GameVersion.Dependencies`: DLC metadata reads, shared-app
metadata inspected during resolution, and references backed by local manifests. A local
manifest sets `MetadataRequired: false`; inherited DLC license gates are recorded separately
as `dlc-license`. These are provenance, not an independent authorization or completeness proof.
Dependencies inherit platform from the enclosing version map and language/architecture/branch
from the version. Provenance is excluded from archive-content equality.

Confirmed selection failures record `FailureImpacts` with the affected catalog root, selection,
required AppID and depot when known. Every checked platform keeps its own diagnostic even
when the product-level summary reflects the final failure. Inventory failures without a
confirmed edge stay unresolved, with unknown impact for other selections. Unsupported default
variants and failures that stop discovery early do not prove all dependencies were examined.
Neither classification nor catalog absence clears a failure or changes sync's failing exit code.

Observed on 2026-10-25 in the public appinfo cache at
`https://api.steamcmd.net/v1/info/{appid}`: BioShock Infinite (8870), XCOM 2 (268500), BioShock
Remastered (409710), BioShock 2 Remastered (409720) and XCOM: Chimera Squad (882100) each expose
Windows depot 1523211 with `depotfromapp=1523210` and public manifest 5488617169383930545.
Appinfo for 1523210, 41710 and 223060 contains only identity/public-only fields and
`_missing_token=true`; the user's authenticated PICS requests also reported missing tokens.
These observations motivate the shared-depot regression fixture; the application never calls
this public cache or hardcodes these IDs.

A later authenticated run confirmed explicit `AppTokensDenied` responses on both the initial
and individual request for all 15 remaining AppIDs. The optional name lookup returned no names.
Package membership provides context but does not establish the private app's title: the public
`https://store.steampowered.com/api/packagedetails?packageids={packageid}&l=english` endpoint
maps package 466 to S.T.A.L.K.E.R.: Shadow of Chernobyl (main AppID 4500), package 7254 to Magicka
(42910), and package 2120 to Majesty 2 (25980). The denied extra AppIDs in those packages were
41710, 479520 and 481220 respectively. Do not label these extra apps with the main game's name
or conclude that the main game is missing just because the extra app's metadata is unavailable.

Do not suppress actual PICS access failures or treat empty appinfo as a game. `missing_token`
describes access to an individual application's metadata. The unavailable application remains
a per-app sync failure and an older snapshot entry is retained when present. It must not make
other games fail when their own appinfo already contains the required shared manifests.
An accessible different AppID in the same package, even with the same title, is not evidence
that the denied AppID is available through another license. Keep the failure unless that
same AppID actually succeeds. Do not reclassify these failures as harmless package duplicates.

When both token requests explicitly deny the same AppID, the CLI uses a concise summary and
retains full diagnostics in `Failures`; `--details` on sync or offline failures prints them.
If a game fails on a required app's metadata, its summary names that required AppID. Timeouts,
omitted tokens and other failures keep their diagnostic text instead of claiming a confirmed
access denial. Summaries never alter failure status or the sync exit code.

For diagnostic display names, keep any name present in PICS even if protected appinfo is
unavailable. For failures without names, try `Player.GetOwnedGames#1` through SteamKit2 unified
messages with `include_appinfo`, the current account's SteamID and an AppID filter. This optional
lookup is bounded to ten seconds (or the smaller operation timeout), and its failure must not
discard the sync. Only use returned names as labels, never as ownership, depot or manifest data.
The aggregate snapshot records `FailureNames`; an older game title or failure name is retained
when no fresh name is available. Source outages and source-filtered sync preserve known names.
Unknown names are displayed explicitly instead of guessed. This lookup is best effort and does
not establish that every private application will return a name.

For explicit diagnostics, `library compare --source steam` calls the same service without
`appids_filter`, using the existing authenticated session and current SteamID. Set
`include_appinfo`, `include_played_free_games`, `include_all_free_games`, `include_free_sub` to true and
`skip_unvetted_apps` to false for both full-list diagnostics and filtered name lookups.
The full request uses the configured operation timeout and has no automatic retry. Unlike
optional name lookup, diagnostic failures must propagate: require `EResult.OK`, an explicitly
present `game_count` equal to the returned list length, and positive unique AppIDs. An omitted
count is unavailable data, not proof of an empty library. Preserve nameless entries by AppID.

Compare this result against saved metadata without updating it. Compare game-tagged snapshot
entries with the API list by AppID, show API-only entries against all snapshot product types,
and identify matching non-game types separately. Always retain unresolved sync failures, even
when the API supplies a name for that same AppID. Neither presence in `GetOwnedGames` nor a
soundtrack-like title proves depot access or overrides the product type from PICS. The API's
list and the client's displayed game counter can use different filters.

### Unplayed free games and incomplete diagnostic lists

The user's 2026-10-26 comparison returned 311 entries: 305 snapshot games, five applications
and one demo. Two licensed snapshot games were absent, Burntime Remastered (3269080) and
King's Quest (345390), despite both appearing in the Steam client. The client reported 306
games, one soundtrack, eight software products and 141 tools; the snapshot had 307 games,
one music product, eight applications and 32 tools. These counts do not identify the extra
game or establish that any soundtrack is misclassified. The incomplete original query must
not be used to discard licensed products or reinterpret the tool-count difference as missing games.

The Steam store lists [Burntime as free](https://store.steampowered.com/app/3269080/) and
[King's Quest's first chapter as free](https://store.steampowered.com/app/345390/).
`include_played_free_games` is not a request for all unplayed free titles. Valve's newer
`GetOwnedGames` API description defines `include_all_free_games` for that purpose, subject
to playtime visibility. The user confirmed that neither missing game had ever been launched,
consistent with this filter excluding them. The user then verified the corrected request:
317 entries, including all 307 snapshot games, eight applications and two demos. Neither
snapshot games nor API entries were missing from the other set. All 15 denied AppIDs remained
absent from the list. This validates the free-game fix on this account, but does not identify
the 306/307 client-count discrepancy or prove that the denied apps are unused dependencies.

Verified schema and API description snapshots:
- [webui/service_player.proto, field 10](https://github.com/SteamDatabase/Protobufs/blob/94546e8057dc80a155d4c52f05f6b230a97919e4/webui/service_player.proto)
- [IPlayerService/GetOwnedGames parameters](https://github.com/SteamDatabase/SteamTracking/blob/d3352acf40db92a2ac544d2221fd9ba3af4fb235/API/IPlayerService.json)

SteamKit2 3.4.0's generated request ends at field 8; even the separately maintained
steamclient schema may lag the web UI schema. Use protobuf-net's supported `IExtensible`
mechanism to append bool field 10 to that request. Field 9 is `include_family_licenses` and
must not be confused with field 10. Both full comparison and filtered name lookup share the
builder, so both receive the flag. Wire-format probes decode the serialized request with a
newer schema and check its survival through the older generated type. This does not change download ownership,
PICS retry policy, or the handling of incomplete/denied diagnostic responses.

Primary sources for labels: `Samples/013_UnifiedMessages/Program.cs` and
`Base/Generated/SteamMsgPlayer.cs` (`CPlayer_GetOwnedGames_Request`, response and `Player` service).

## SteamPipe downloads

1. Discover servers with `SteamContent.GetServersForSteamPipe`.
2. Get the depot key with `SteamApps.GetDepotDecryptionKey(depotId, appId)`; check `EResult`.
3. Get the manifest request code with `SteamContent.GetManifestRequestCode`.
4. Download/decrypt the manifest with `SteamKit2.CDN.Client.DownloadManifestAsync`.
5. If a CDN requires authorization, obtain `GetCDNAuthToken` for that host and retry.
6. For each chunk call `DownloadDepotChunkAsync` with a bounded destination byte array and
   the depot key. SteamKit2 decrypts/decompresses content and validates its Adler checksum.
7. The provider additionally validates chunk and whole-file SHA-1 and exact chunk coverage.

Validate manifest identity and decrypted file names. File flags include `Directory` and
`Symlink`; links are dereferenced against the manifest tree, with missing/cyclic/escaping targets
rejected. Nothing outside the selected staging tree may be included.

Version 3.4.0's CDN methods do not accept caller cancellation tokens. The configured HTTP factory
therefore propagates operation and per-transfer cancellation into response-body reads, including
the separate body timeout passed by SteamKit2 to `HttpContent.CopyToAsync`. Merely abandoning the
returned task would leave HTTP work writing into a chunk buffer. The integration awaits aborted
reads and propagates cancellation before any buffer is used or any completion is written.

Rate-limit waiting happens before chunk requests and after manifest responses (whose wire size
is only known after transfer). This keeps intentional pacing outside SteamKit2's network
deadlines. The provider does not alter SteamKit2's process-wide static timeout settings.

Primary sources: `Steam/Handlers/SteamContent/SteamContent.cs`, `Steam/CDN/Client.cs`,
`Steam/CDN/DepotChunk.cs`, `Types/DepotManifest.cs`, `Steam/SteamClient/Configuration`.

## What the offline fixtures establish

The tests validate application planning, reconstruction and recovery with an injected
`ISteamSession` plus a controlled HTTP response stream for the real cancellation adapter.
They also exercise a real .NET WebSocket upgrade and the SteamKit callback pump with a fake HTTP
transport and server directory: the old wrapper fails, the CM transport can send, failed initial
connections retry, and cancellation/timeouts terminate the attempt sequence. No account or
external connection is needed. Tests also parse representative PICS KeyValues. Compilation
verifies the pinned library's API signatures. This does not establish that a specific user's account, title, network
or ARM64 device will authenticate and transfer successfully; that remains a live integration test.
