# Shift-It

A .NET 10 Worker Service that keeps **hot** storage clean by relocating aged
files into a parallel **archive** location — recreating the source folder
structure under the archive root as it goes. Archive reffers to alternate
storage location not any kind of compressed/packed "folder".

Each file is copied to its mirrored path under the archive root, verified, 
and the original deleted. The archive is a 1:1 structural mirror of what left 
the hot area.

## How it works

On a schedule, for each configured **hot → archive** pair the service:

1. Recursively scans the hot root for files whose **last-modified time**
   (`LastWriteTimeUtc`) is older than `MinAgeDays`.
2. Computes each file's path relative to the hot root and mirrors it under the
   archive root, creating directories as needed.
3. Moves the file safely (see below).
4. Optionally removes folders left empty in the hot tree (never the hot root).

### Safe move (crash- and cross-volume-proof)

Each file is moved with a copy → verify → rename → delete sequence:

1. Copy the source to a temporary `*.archtmp` file on the **archive** volume.
2. Verify the copy: byte-length always, plus **SHA-256** when `VerifyWithHash`
   is enabled.
3. Atomically rename the temp file into its final name (same-volume rename).
4. Delete the source **last**, only after the archived copy is durable.

Because the source is removed last, an interrupted run leaves the original
intact and is safe to re-run. If a file already exists at the destination, the
move is **skipped with a warning** and the source is left untouched (the service
never overwrites an existing archived file).

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned via `global.json`)
- Windows to run as a Windows Service; the worker itself is cross-platform.

## Configuration

All settings live under the `Archive` section of
[`appsettings.json`](src/ShiftIt/appsettings.json):

```jsonc
{
  "Archive": {
    "ScanIntervalMinutes": 60,   // how often a sweep runs
    "MinAgeDays": 30,            // archive files older than this (by LastWriteTimeUtc); 0 = everything
    "RemoveEmptyHotFolders": true,
    "VerifyWithHash": false,     // false = length check; true = also SHA-256
    "MaxRetries": 3,             // retries for transient errors (network blips, file locks)
    "RetryDelaySeconds": 2,      // base backoff; doubles each attempt (2s, 4s, 8s...)
    "MaxParallelMoves": 1,       // concurrent moves per pair; raise for SMB/high-latency targets
    "LogDirectory": "logs",      // rolling file log location (relative to the app base dir)
    "LogRetentionDays": 14,      // delete daily log files older than this
    "Pairs": [
      { "Name": "Reports", "HotRoot": "D:\\Hot\\Reports", "ArchiveRoot": "E:\\Archive\\Reports" },
      { "Name": "Logs",    "HotRoot": "D:\\Hot\\Logs",    "ArchiveRoot": "\\\\nas\\archive\\Logs" }
    ]
  }
}
```

Settings are validated on startup. A pair is rejected if its roots are not
absolute paths, are equal, or if the archive root is nested **inside** its own
hot root (which would re-scan archived files forever). Configuration is bound
via `IOptionsMonitor`, so edits are picked up without a restart.

| Setting | Meaning |
|---|---|
| `ScanIntervalMinutes` | Minutes between sweeps. A sweep also runs immediately on start. |
| `MinAgeDays` | Minimum file age (by `LastWriteTimeUtc`) to be archived. `0` archives everything present. |
| `RemoveEmptyHotFolders` | Prune folders emptied by a sweep (hot roots are never removed). |
| `VerifyWithHash` | Verify copies with SHA-256 in addition to byte length (slower — see Benchmarks). |
| `MaxRetries` | Retries after a transient failure (sharing violation, momentary network drop) before giving up. |
| `RetryDelaySeconds` | Base delay between retries; grows exponentially per attempt. |
| `MaxParallelMoves` | Files moved concurrently within a pair (1 = sequential). Raise to hide per-file latency on SMB/network targets. |
| `LogDirectory` | Folder for the detailed rolling file log. Relative paths resolve against the app base directory. |
| `LogRetentionDays` | Daily log files older than this are deleted automatically. |
| `Pairs[]` | One or more `{ Name, HotRoot, ArchiveRoot }` mappings. |

## Running

### Development (console)

```bash
dotnet run --project src/ShiftIt
```

Runs in the foreground with console logging; `Ctrl+C` to stop.

### As a Windows Service

Publish, then register with the Service Control Manager:

```powershell
dotnet publish src/ShiftIt -c Release -o C:\Services\ShiftIt
New-Service -Name ShiftIt -BinaryPathName C:\Services\ShiftIt\ShiftIt.exe -StartupType Automatic
Start-Service ShiftIt
```

The host detects the service context automatically (`AddWindowsService`).
Remove with `sc.exe delete ShiftIt`.

## Logging

Two destinations, deliberately scoped so each stays useful:

- **Windows Event Log** (when running as a service) — high-signal only: service
  start/stop, one summary line per pair per sweep, and warnings/errors such as a
  missing hot root, an aborted pair, or a sweep that finished with failures.
  Per-file activity is **never** written here, so the Event Log stays readable.
- **Rolling file log** (`LogDirectory`, daily file `shiftit-YYYYMMDD.log`) —
  detailed: every move, skip, retry, and empty-folder cleanup at `Debug`, plus
  everything the Event Log gets. Files older than `LogRetentionDays` are pruned
  automatically (powered by [Serilog](https://serilog.net/)).

When run as a console app (development), output goes to the console and the file
log; the Event Log is left untouched.

## Resilience

The mover is built to survive an imperfect environment without losing data or
flooding the logs:

- **Transient errors** — a sharing violation, a file briefly locked, or a
  momentary network drop is retried up to `MaxRetries` times with exponential
  backoff (`RetryDelaySeconds`, doubling each attempt).
- **Destination full** — the affected pair is stopped for the sweep with a
  single clear warning rather than failing every remaining file; other pairs
  continue. It resumes next sweep once space is available.
- **Source/destination becomes inaccessible** — retried; if still unreachable
  after the retries, the pair is aborted (one warning) instead of churning.
- **Insufficient permissions** — that single file is skipped and logged; the
  sweep moves on.

In every case the source file is only deleted after the archived copy is
verified and durable, so nothing is ever lost to an interrupted or failed move.

## Tests

```bash
dotnet test
```

48 xUnit tests in [`tests/ShiftIt.Tests`](tests/ShiftIt.Tests) cover the move
logic (mirroring, conflict skip, hash verification, no temp leftovers), the
scanner (age filtering, empty-folder pruning, missing hot root, `MinAge=0`,
pair abort, parallelism), error classification, the retry policy, and
configuration validation. Bad-situation coverage includes a missing source, an
exclusively locked source, and an unreachable archive drive.

**End-to-end tests** drive the whole pipeline over a realistic multi-nested tree
of mixed file sizes and assert exact structure mirroring and byte-for-byte
content — sequential and parallel, with and without hashing.

The move, scan, and end-to-end tests run against **both a local destination and
the SMB share** (`S:\The Archive\temp`); the SMB cases skip automatically when
the share is offline. Tests use real directories and clean up after themselves.

## Benchmarks

```bash
dotnet run -c Release --project benchmarks/ShiftIt.Benchmarks -- --filter '*'
```

### Single move (`FileMoveBenchmarks`)

Measures `FileMover.MoveAsync` across file sizes, with hash verification off/on,
to **both a local disk and an SMB share** (`S:\The Archive\temp`). The source is
always local, so the SMB rows measure a real local→network move. Mean times
(Ryzen 7 3700X, .NET 10, gigabit SMB, warm OS cache):

| File size | Local | Local + SHA-256 | SMB | SMB + SHA-256 |
|---|--:|--:|--:|--:|
| 4 KB | 1.6 ms | 3.3 ms | 8.0 ms | 12.6 ms |
| 256 KB | 5.5 ms | 15.3 ms | 25 ms | 24.8 ms |
| 4 MB | 4.7 ms | 20.4 ms | 74 ms | 159 ms |
| 64 MB | 33.6 ms | 130 ms | 871 ms | 1,897 ms |

- **SMB adds a large fixed latency** plus network-bound throughput. Size
  `ScanIntervalMinutes` and expectations for a network archive accordingly.
- **Hashing during the copy** means the source is read once, not twice; small and
  mid-size hashed moves over SMB roughly halved versus the naive approach. Very
  large hashed files over SMB are a touch slower (streamed write vs `File.Copy`),
  and hashing is off by default regardless.
- The non-hash path is allocation-free (~2–5 KB/move regardless of size).
- SMB numbers carry real network jitter (wide error bars); treat them as
  ballpark, not precise.

### Full-process workloads (`WorkloadBenchmarks`)

Drives a whole `ArchiveScanner.RunSweepAsync` over realistic, multi-nested trees
to a local and an SMB destination, sequential (`1`) vs parallel (`8`). Median
sweep time (high run-to-run variance — see note):

| Workload | Local ×1 | Local ×8 | SMB ×1 | SMB ×8 |
|---|--:|--:|--:|--:|
| **ManySmall** — 250 × 8 KB, nested | 358 ms | 153 ms | 2.70 s | 0.99 s |
| **Mixed** — 100 files (~12 MB), nested | 239 ms | 61 ms | 1.43 s | 0.55 s |
| **FewLarge** — 3 × 20 MB | 41 ms | 23 ms | 0.85 s | 0.87 s |

Throughput and takeaways:

- **Small-file workloads are latency-bound**, so parallelism is the big lever:
  over SMB, ManySmall goes from ~95 to ~250 files/s (×8) and Mixed from ~70 to
  ~180 files/s. Locally, 2–4×.
- **Large-file workloads are bandwidth-bound**: FewLarge over SMB saturates the
  link (~70 MB/s) and parallelism doesn't help — extra concurrency just shares
  the same pipe (and there are only 3 files to spread).
- So tune `MaxParallelMoves` to the data: raise it for many-small-file trees on a
  network target; leave it low when big files dominate.
- Numbers are warm-cache with real network/GC jitter; medians are shown because
  the per-iteration spread is wide. Treat them as relative, not precise.

## Project layout

```
ShiftIt.sln
├── src/ShiftIt/                 Worker Service
│   ├── Program.cs               Host, DI, options validation, Windows Service
│   ├── Worker.cs                PeriodicTimer sweep loop
│   ├── Configuration/           ArchiveOptions + validator
│   └── Services/                ArchiveScanner, FileMover, FileErrors, Resilience
├── tests/ShiftIt.Tests/         xUnit tests
└── benchmarks/ShiftIt.Benchmarks/  BenchmarkDotNet suite
```
