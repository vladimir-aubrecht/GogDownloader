# SteamKit2 integration reference

Verified against SteamRE/SteamKit tag `3.4.0`, commit
`1c7bc9c` (the dependency pinned by `SteamArchiver.Core.csproj`). Reference source was read
from the upstream repository, not copied into the application. SteamKit2 is LGPL-2.1-only;
see [third-party notices](../THIRD-PARTY-NOTICES.md).

## Connection and authentication

- Create `SteamClient`, `CallbackManager`, `SteamUser`, `SteamApps`, `SteamContent`.
- Pump callbacks for the session lifetime, including while awaiting `AsyncJob` results.
- Wait for `SteamClient.ConnectedCallback` before authentication.
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
`AuthSession.cs`, `AuthPollResult.cs`, and the `SteamUser` handlers.

## Library and appinfo

`LicenseListCallback` supplies package IDs, flags and package access tokens. Ask PICS for package
info with those tokens and read package `appids`. Do not invent ownership by treating public
appinfo as an ownership grant. Expired/cancelled/not-activated licenses are excluded.

Request app access tokens with `PICSGetAccessTokens`, then app info with `PICSGetProductInfo`.
`AsyncJobMultiple.ResultSet.Complete` and `Failed` must be checked; partial results are not a
complete library. Large appinfo can carry `UseHttp` and `HttpUri`; that body must be read and
parsed instead of silently accepting empty KeyValues. Per-app failures are recorded separately.

Appinfo fields used by this provider:

| Path | Meaning |
|---|---|
| `common/name`, `common/type`, `common/oslist` | Name, product type and OS availability |
| `common/parent` | Parent AppID for a DLC product |
| `extended/listofdlc` | Candidate DLC AppIDs; intersect with licenses |
| `depots/branches/{branch}/buildid` | Branch build identifier |
| `depots/{depot}/config/oslist`, `osarch`, `language` | Depot selection constraints |
| `depots/{depot}/dlcappid` | DLC-specific content, gated by license |
| `depots/{depot}/depotfromapp` | Shared depot; resolve appinfo recursively with cycle checks |
| `depots/{depot}/manifests/{branch}/gid` | Unsigned 64-bit manifest ID |
| `config/launch/*` | Executable/arguments and OS constraint |

Main content precedes DLC content when merging paths. The archive's identity includes the final
depot/manifest selection, not just the top-level build number. Private/encrypted branch manifests
are rejected rather than silently replaced by another branch.

Primary sources: `Steam/Handlers/SteamApps/SteamApps.cs`, `Callbacks.cs`, `Types/KeyValue.cs`.

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
They also parse representative PICS KeyValues with SteamKit2. Compilation verifies the pinned
library's API signatures. This does not establish that a specific user's account, title, network
or ARM64 device will authenticate and transfer successfully; that remains a live integration test.
