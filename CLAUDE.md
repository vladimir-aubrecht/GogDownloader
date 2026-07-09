# GogDownloader

Backs up games you own on GOG into portable zips. C# / .NET 9, runs on Linux, Windows and macOS.

## Language

Everything in this repository is **English**: code, comments, XML docs, commit messages,
documentation, scripts and the application's console output. Chat with the maintainer
happens in Czech, but none of that ever lands in a file.

## Before you touch the GOG API

Read `docs/gog-api.md`. It is a verified cache of endpoints — GOG has no public
documentation. **Do not go hunting for endpoints on the web.** Refresh it with
`scripts/refresh-gog-api-ref.sh`.

## Decisions already made

- **Path A (primary):** Galaxy content-system depots for `windows` and `osx`. The installed
  game files are downloaded directly, chunk by chunk. No installer conversion.
- **Path B (Linux):** Galaxy depots do not exist for Linux. Linux builds are MojoSetup
  installers (shell script + ZIP payload). They are read over HTTP Range — the installer is
  never downloaded in full. This path is unavoidable, not speculative.
- **Path C (Inno Setup / Wine):** only for Windows/macOS titles with no depot. **Not
  implemented yet.** `library stats` on the real library reports 24 such titles, mostly old
  bundles and compilations (Ultima, Might and Magic, Commandos 2+3).
- **DLC:** owned DLC belongs in the zip. The DLC list is `products[]` straight from the
  repository manifest, intersected with owned ids — no extra API call.
- **Sizes** live in the repository manifest (`compressedSize` = bytes downloaded, `size` = bytes
  on disk) and are recorded by `library sync`. They are language-dependent, so the snapshot
  records them for `en`, matching the default of `download --language`.
- **Snapshot schema** is versioned. When a sync starts recording something older snapshots
  lack, bump `LibrarySnapshot.CurrentSchema` and warn on read — a stale snapshot silently
  reporting zero is worse than an error.
- **Redistributables** (`dependencies` in the manifest): **not downloaded.** They are
  recorded in `.gogdl.json` as `skippedDependencies` so it stays visible what the game wanted.
- **Archive:** zip everywhere. Symlinks are dereferenced; unix permissions are not preserved —
  the user explicitly agreed and handles that outside this project.
- **Staging goes on a local disk**, never in `--out`. Users back up onto network shares, and
  staging there would push every byte across the wire three times (write the files, read them
  back to zip, write the zip). Override with `--staging`. When staging and target sit on
  different volumes the zip is copied to a sibling `.tmp` and then renamed, so the final name
  still appears atomically.
- **Reliability:** work in a staging directory, only an atomic `File.Move` into the target.
  The `.gogdl.json` manifest is written **as the very last step** — until it exists the game
  counts as not downloaded and a restart redoes it. A corrupted "done" state cannot arise.
- **Idempotence and patches:** the manifest carries per-file hashes. A patch downloads only
  the chunks of changed files and pulls the rest out of the old zip (which acts as a cache).
- **Directory names** are `{slug}__{gameId}`, never derived from the title (`:`, `CON`/`AUX`,
  MAX_PATH, matching across operating systems).
- **The user controls** download speed and the number of concurrent connections.

## A trap you can fall into

`api.gog.com` uses the string `mac` for macOS, but `content-system.gog.com` uses `osx`.
Do not mix them up.

## Working principles for this project

- Do not implement features until it is proven the user's library needs them. When in doubt,
  ask — do not write code just in case.
- Store expensive findings in `docs/`, do not re-derive them every session.
- Take size and count estimates from real data (`library sync`), not from memory.

## Build

```sh
dotnet build
dotnet run --project src/GogDownloader.Cli -- <command>
```

Note: `dotnet run -v q --nologo -- <command>` swallows the arguments. Use the form above.

## Release

**A push is a release.** Every push bumps the version in `Directory.Build.props` (started at
0.1.0) and gets a `vX.Y.Z` tag. Do not push work in progress.
