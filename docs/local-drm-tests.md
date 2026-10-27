# Local DRM launch tests

`library drm test [search] --out DIR` tests completed, metadata-referenced Windows archives
on Windows. It does not log in to any store or download content. The same command works for
GOG and Steam, legacy metadata, flat/provider layouts and custom provider folder names.
Without `--out`, it reads the current game's metadata. A search matches a source:ID, title
or slug. By default known non-game products are omitted; `--include-non-games` includes them.

## Observations versus confirmation

The tool samples Steam processes every 200 ms while the launch runs (60 seconds by default),
including while the user checks gameplay at the confirmation prompt. After an early process
exit it watches for another two seconds for a deferred Steam launch. The outcomes are:

| Outcome | Meaning |
| --- | --- |
| RunningWithoutSteam | The test process tree was still running; no Steam process was observed. |
| SteamObserved | A Steam client, web helper or crash reporter was observed. |
| Exited | The process tree exited before the observation period ended. |
| Unavailable | Missing/unsupported launcher, damaged ZIP, extraction or launch error. |

None automatically means `Free` or `Protected`. A process can be displaying an error or a
launcher; Steam can open for optional features. Polling can miss a short-lived Steam process.
An already running Steam client blocks testing rather than contaminating the result. The
installed Steam background service alone does not count as a running client. The tool does
not stop unrelated Steam processes; after observing Steam it checkpoints the result and stops
the batch. Close Steam and rerun to continue with other games.

The interactive prompt accepts `y` for confirmed gameplay without Steam, `s` for a confirmed
requirement for Steam, or Enter for unknown. A detected Steam process always prevents a free
confirmation, even if it starts while the prompt is open. `--no-prompt` saves observations
without classification and is required when input is redirected. A game merely starting
without Steam does not establish offline play: cached authorization, another client, delayed
checks and machine-specific dependencies may still matter. No network isolation or clean
Windows environment is created. No DRM is modified or removed.

## Persistence, precedence and resuming

`ArchiveLibrary.DrmTests` stores identity, version, launch recipe, machine, timestamp, elapsed
observation time, processes seen, outcome and optional confirmation. `library drm tests
[search] [--source steam|gog|all] [--details]` shows these saved records. Each complete result is
atomically checkpointed, including inconclusive/unavailable attempts. Ctrl+C leaves the
current attempt unrecorded; repeat the command to retry it and continue with remaining games.
Interrupted extraction is removed and repeated; it is not retained as a trusted game install.

Resume uses a fingerprint of metadata/version, archive name/size/modification time, launch
recipe, machine and observation duration. It is a change detector, not a cryptographic digest
of every ZIP byte. A changed fingerprint gets a new test automatically; `--retest` replaces
the result for the same fingerprint. Fix missing dependencies/launch settings and use
`--retest` to repeat an otherwise unchanged unsuccessful test.

Manual overrides remain first. A confirmed local result then outranks community evidence
only for a matching version and selection; an unconfirmed observation does not replace a
community classification. Store/community syncs retain local test records. Locally edited or
outdated archives can be tested, but their results do not classify the provider's original
content. Download publication checks `DrmInfo.TestedVersion` against the actual live version
so a newer release cannot inherit a previous build's local confirmation. Existing ZIPs and
`gamearchiver.json` are never rewritten just to annotate a test.

## Launching and extraction

Use `--staging DIR` for scratch space on a local disk. Each attempt extracts into a fresh
unique subdirectory of `STAGING/drm-tests`, checks entry sizes/CRCs, rejects traversal,
case collisions, device paths, links and special entries, and checks free space first.
The archive lease remains held during testing to serialize against downloads and ZIP edits.
Native test batches also hold a per-user lock, preventing concurrent batches on one machine.

The launcher comes from `LaunchExecutable`/`LaunchParameters`; no executable is guessed.
Supported launchers are `.exe`, `.bat` and `.cmd`. The working directory defaults to the
extraction root because existing archive metadata does not record a separate working
directory. Missing launchers and unsuitable working directories are inconclusive. Select
exactly one archived game to override the launch recipe, for example:

```powershell
dotnet run --project src/GameArchiver.Cli -- library drm test wolfenstein_3d --out Z:\games\steam --executable dosbox\dosbox.exe --arguments "-conf base\wolf3d.conf" --working-directory base
```

Paths are relative to the extracted root. Overrides apply only to this invocation; use the
same arguments when resuming. Existing archives are not modified. Batch launcher arguments
are interpreted by `cmd.exe`; use only trusted game archives and launch settings.

The Windows runtime creates the initial process suspended, assigns it to a kill-on-close job,
then resumes it. Job accounting includes children after a launcher exits. Completion, errors
and cancellation terminate contained processes before cleanup; closing/crashing the test
host also closes the job. This is process cleanup, not a security sandbox: games can write
outside the extraction under your account, and services/shell brokers can create processes
outside the job. The tool never deliberately kills unrelated processes. Cleanup failures
stop the batch and identify the scratch directory for manual inspection.

The deterministic probes cover resuming, CRC/path failures, confirmation precedence, version
invalidation and Steam starting during confirmation. Windows-only probes exercise actual
child process containment. Development on Linux cannot validate real game launches, GUI
behavior or the native Windows runtime; these require a Windows machine.
