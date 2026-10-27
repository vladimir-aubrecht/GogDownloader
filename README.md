# GameArchiver

Back up games from **GOG and Steam** into verified ZIP archives, using a unified CLI or
independent C# libraries. Built with .NET 9 for Windows, Linux and macOS; Steam uses SteamKit2 directly.

- Resume interrupted downloads and reuse verified content when updating backups.
- Keep both stores in one library, with optional folders per provider.
- Filter downloads by size or reported DRM status; test downloaded games locally on Windows.
- Use archive names and launch metadata compatible with [GameVault](https://gamevau.lt/).

GOG archives contain the installed DRM-free game. Steam archives contain the selected game
files and may still require Steam or other DRM to run. Existing GOG backups and logins remain supported.

## Quick start

Install the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and run these commands
from the repository root. Some older GOG Windows games also need
[`innoextract`](https://constexpr.org/innoextract/); see [GOG download methods](docs/gog-cli.md#how-each-game-is-fetched).

Build, log in and fetch your library:

```sh
dotnet build -c Release
dotnet run --project src/GameArchiver.Cli -c Release --no-build -- auth login --source gog
dotnet run --project src/GameArchiver.Cli -c Release --no-build -- library sync --source gog
dotnet run --project src/GameArchiver.Cli -c Release --no-build -- library list --source gog
```

For Steam, replace `--source gog` with `--source steam`. Steam Guard codes and mobile approval
are supported. To use both stores, log in to each separately, then use `--source all` for sync
and downloads. Rebuild after updating the code before using `--no-build`.

Preview the games to download, then start the backup:

```sh
dotnet run --project src/GameArchiver.Cli -c Release --no-build -- download --all --source gog --out ./backups --layout provider --dry-run
dotnet run --project src/GameArchiver.Cli -c Release --no-build -- download --all --source gog --out ./backups --layout provider
```

`--all` selects games by default; add `--include-non-games` for applications and demos.
To download one product, use its title/slug or qualified ID, for example `download steam:427520`.
Add `--max-size 500MB` to a batch download to select smaller games.

## Working with backups

The commands above write to `backups/gog/<slug>/` (or `backups/steam/<slug>/`). Each game folder
contains its ZIP and a visible `gamearchiver.json` with the store, product ID and archive metadata.
Keep that file beside the ZIP. Omit `--layout provider` to put game folders directly under `--out`.

Use the same output and layout settings when checking your backups:

```sh
dotnet run --project src/GameArchiver.Cli -c Release --no-build -- diff --source gog --out ./backups --layout provider
dotnet run --project src/GameArchiver.Cli -c Release --no-build -- verify --source gog --out ./backups --layout provider --deep
```

`diff` compares against the saved library; sync first for current store metadata. `verify --deep`
checks ZIP contents. Interrupted downloads can be resumed by repeating the download command.
Staging needs local space for the unpacked game and ZIP; use `--staging DIR` to select another disk,
especially when the backup destination is a NAS.

## Documentation

| Task | Guide |
| --- | --- |
| Log in, select providers, configure folders or filter by size | [CLI guide](docs/cli.md) |
| Understand product counts, sync failures and `library compare` | [Library and diagnostics](docs/library.md) |
| Fetch community DRM reports, filter downloads or correct a result | [DRM reports and filters](docs/drm.md) |
| Run autonomous DRM tests and resume an interrupted batch | [Local DRM tests](docs/local-drm-tests.md) |
| Add/replace files inside ZIPs or migrate old metadata | [Archive maintenance](docs/archives.md) |
| Use the original GOG CLI or upgrade existing GOG backups | [GOG compatibility](docs/gog-cli.md) |
| Synchronize launch settings with GameVault | [GameVault setup](docs/gamevault-sync.md) |
| Embed individual providers or the aggregate C# API | [C# API](docs/api.md) |
| Build, run tests or inspect protocol references | [Development](docs/development.md) |

For all CLI options, run `dotnet run --project src/GameArchiver.Cli -- --help`.

## License

[MIT](LICENSE). SteamKit2 and the optional external `innoextract` tool retain their own licenses;
see [third-party notices](THIRD-PARTY-NOTICES.md).
