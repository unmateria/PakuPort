# dosfs_test — DOS file system: current directory, 8.3 names, findfirst with a path, drives

## What it measures

22 cases, each GREEN/RED, no human eye involved. It mounts a temporary directory as `C:` (and
another as `D:` via `PAKU_DISCO_D`) and talks to `Int21hHandler` **through INT 21h**, like a game.

| Case | Checks |
|---|---|
| **a1/a2** | `findfirst *.*` with `CX=0x10` returns the 3 folders **and** the 4 files; with `CX=0`, only the 4 files. Previously `*.*` became the regex `^.*\..*$`, which **does not match a name without extension**: folders `S` and `I` were missing. |
| **b/b2** | DTA names are real upper-case 8.3: `SKAVEN~1.S3M`, `LONGFI~1.S3M`, `A~1.TXT`, `NORMAL.MOD` untouched. |
| **c** | `open` of the SHORT name `SKAVEN~1.S3M` opens the real long-named file and returns its bytes. |
| **d1..d5** | `chdir S` -> `getcwd` = `S`; inside, `findfirst *.S3M` and `open ARMANI.S3M` work; `chdir NOEXISTE` gives CF=1 AX=3 **and leaves the cwd unchanged**; `chdir ..` returns to the root. |
| **e1/e2** | `findfirst S\*.S3M` and `\S\*.*` with `CX=0x10` (which also returns `.` and `..`). |
| **f** | Two INTERLEAVED searches with different DTAs (`AH=1A` between calls) do not clobber each other — a pattern real programs use. |
| **g1/g2** | `*.S3M` returns neither `NORMAL.MOD` nor folders; `*` without a dot matches only names WITHOUT extension. |
| **h1..h7** | Extra drives (`PAKU_DISCO_D`): `AH=0E`/`AH=19`, listing of D:, `chdir` with a per-drive cwd, `open D:\X\Y.S3M` from C: without switching drive, `D:*.MOD`, an unmounted letter, and `AH=36` on an unmounted drive. |

## How to run (from the repo root)

```
dotnet run -c Release --project tools\dosfs_test
```

Exit code 0 if everything is GREEN, 1 if any case is RED.

## Negative control (no judge goes green without having been seen red)

With the code before the fix **19 of 22 are RED**. The three that are not: `d5` (`chdir ..`
"worked" because the cwd was always the root), `h6` (`AH=0E` never changed anything, so "does
not change" was free) and `c`, which was green **for the wrong reason**: NTFS generates its own
8.3 alias and `File.Exists("SKAVEN~1.S3M")` resolved it behind the scenes. The short<->long map
is really measured by `d3`/`e1`, which were red.

## Where each rule comes from

All ported from DOSBox-X, with file and line cited in `src/PakuPort.Core/Platform/DosFileSystem.cs`:

- `src/dos/dos_files.cpp` — `DOS_MakeName`, `DOS_ChangeDir`, `DOS_GetCurrentDir`, `DOS_FindFirst`, `DOS_FindNext`
- `src/dos/drives.cpp` — `WildFileCmp`
- `src/dos/drive_cache.cpp` — `CreateShortName`, `CreateShortNameID`, `CompareShortname`, `SortByName`
- `src/dos/drive_fat.cpp` — `filename_not_8x3`
- `src/dos/drive_local.cpp` — `localDrive::FindFirst` / `FindNext`, attributes, volume label
- `src/dos/dos_classes.cpp` — `DOS_DTA::SetupSearch` / `GetSearchParams` / `SetResult`

## Why it exists

A DOS music tracker exposed it: its load dialog did not show folders `S` or `I`, and a module
appeared as `SKAVEN-NOT_F`, a name that is not 8.3 and then cannot be opened. It is a class
problem: any game with saves in a subfolder, or that calls `findfirst` with a path in front, was
affected the same way.
