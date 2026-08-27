# MissionPlanner.Utilities.Tests

Characterization suite for the dataflash log core (`BinaryLog` /
`DFLog` / `DFLogBuffer`) - the safety net for the Rust implementation
under `rust/`.

`DFLogCharacterizationTests` snapshots the current parser's observable
behavior (counts, FMT/FMTU/unit tables, sample rows, per-type record counts,
first records, unit lookups, instance indices) over the corpus into golden
files under `testdata/goldens/`. Any replacement implementation must produce
identical reports. Where current behavior is a bug (see below), the golden
documents it until a deliberate decision changes it - regenerate with
`DFLOG_UPDATE_GOLDEN=1` and review the golden diff like code.

## Corpus

`testdata/*.bin` are ArduPilot SITL (Software In The Loop) dataflash logs,
generated 2026-08-26 with the stable prebuilt SITL binaries from
firmware.ardupilot.org (`LOG_DISARMED=1`, ~15 s runs, simulation killed by
timeout so the logs have realistic truncated tails - like a power-pull):

| file | vehicle | size | records |
|---|---|---|---|
| copter.bin | ArduCopter (quad) | 1.24 MiB | 31,867 |
| plane.bin | ArduPlane | 828 KiB | 20,885 |
| rover.bin | ArduRover | 1.14 MiB | 26,335 |

Regenerate with `testdata/gen_corpus.sh` (WSL; expects the SITL binaries in
`~/sitl` - setup recipe in
[tests/MissionPlanner.ArduPilot.SitlTests/README.md](../MissionPlanner.ArduPilot.SitlTests/README.md)).
Regenerating the corpus changes the logs, so the goldens must be regenerated
with it.

Corrupt variants (truncation, garbage prefix, flipped FMT byte, empty file)
are derived deterministically from `copter.bin` at test time - no extra
fixtures.

## Notable characterized behaviors (current parser, not aspirations)

- 128 bytes of garbage before the first record loses the *entire* log: no
  FMT is found, `seen=` is empty, and the file degrades to garbage rows
  (`copter-garbage-prefix.golden.txt`). A resyncing parser should recover
  everything after the prefix.
- Truncated tails parse silently up to the cut; no error is surfaced.
- Goldens are generated under `CultureInfo.InvariantCulture`; row formatting
  is culture-sensitive in the current parser, which is itself a documented
  quirk.

## Running

xUnit v3 - run the built exe (`bin/Debug/net472/MissionPlanner.Utilities.Tests.exe`)
or `dotnet test`. Suite runs in about a second.
