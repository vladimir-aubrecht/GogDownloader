# Development and API references

[Project overview](../README.md)

## Build and verify

All projects target .NET 9. From the repository root:

```sh
dotnet build -c Release
scripts/check.sh
```

`scripts/check.sh` builds the solution, runs the console probes, checks CLI fixture behavior
and exercises the GameVault sync script with mocked HTTP calls. The full suite needs `jq`
and `innoextract` on `PATH`. No store credentials are needed for the tests.

There is no installer. Run a CLI with `dotnet run`, or use `dotnet publish` to build a
self-contained binary. `dotnet pack src/GameArchiver.Cli` creates the `gamearchiver` .NET tool.
Rebuild the matching configuration after updating code before using `--no-build`.
The [container workflow](../.github/workflows/container.yml) also builds amd64/ARM64 images;
see [Docker Compose](docker.md) for scheduling and deployment configuration.

## Repository layout

- `src/GameArchiver.Core` — shared contracts, metadata, storage, ZIP, links and verification.
- `src/GogArchiver.Core` — independent GOG client, content system and installers.
- `src/SteamArchiver.Core` — independent Steam client using SteamKit2.
- `src/GameArchiver` — source-qualified library, download, diff and verification API.
- `src/GameArchiver.Cli` — unified CLI; `dotnet pack` creates the `gamearchiver` .NET tool.
- `src/GogArchiver.Cli` — the original GOG-only command-line front end.
- `tests/GogArchiver.Probes` — a console probe suite without a test framework; `scripts/check.sh` runs it
  and the GameVault sync fixture tests (which require `jq`, with HTTP calls mocked).
- `docs/gog-api.md`, `docs/gamevault.md` — verified references for the two undocumented APIs.
- `scripts/` — `check.sh`, `release.sh`, `vendor-ref.sh`, and the [GameVault sync script](gamevault-sync.md).

## Project rename and compatibility

The rename changes project, assembly and namespace names, plus the unified tool command to
`gamearchiver`. Existing configuration/staging directories (`gogdownloader` and `gamearchive`),
environment variables (`GOGDL_SNAPSHOT` and `GAMEARCHIVE_STEAM_*`) and shared lock filenames
keep their original names. Existing logins, snapshots and interrupted
downloads therefore remain usable without migration. C# consumers must update project/package
references and `using` directives, and use `GameArchiverClient` for the aggregate API.
Archive metadata filenames have a separate, backward-compatible migration to `gamearchiver.json`.

## Integration verification

The native Steam path is exercised with controlled session/CDN fixtures. A live login, licensed
depot download and execution on ARM64 still need verification on a real account/device. No
Steam credentials are required to build or run the test suite.

Windows process/job behavior and real game launches require Windows; controlled runtime
fixtures run on other hosts. See [local DRM tests](local-drm-tests.md) for the testing limits.

## API and protocol references

- [C# API](api.md): independent providers, aggregation, snapshots and embedding examples.
- [GOG API](gog-api.md): verified content and ownership behavior.
- [SteamKit2](steamkit2.md): connection, authentication, licensing and depot selection.
- [GameVault](gamevault.md): archive naming and launch-metadata integration.
- [PCGamingWiki](pcgamingwiki.md): authentication, queries and DRM evidence parsing.
- [Steam curator DRM data](steam-curator-drm.md): alternative source and validation rules.
