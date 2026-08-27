# MissionPlanner.Utilities.DFLogBench

Manual performance harness for the dataflash log core
(docs/dflog-rust-core-plan.md): native (rust/) vs managed paths. Not part of
any automated run - results are wall-clock measurements on whatever machine
runs them; always take several runs (single samples mislead - one outlier
during phase-C work read 55% slow).

## Preparing a benchmark log

Concatenate a corpus log until the target size (records stay valid across
the seams; both scanners agree on the seam records):

```bash
for i in $(seq 1 260); do cat tests/MissionPlanner.Utilities.Tests/testdata/copter.bin >> big320.bin; done
```

260 copies = ~322 MiB / 8.29M records, above the 300 MiB BinaryFormatter
cache threshold; 200 copies = ~248 MiB, below it.

## Modes

```
MissionPlanner.Utilities.DFLogBench.exe <log.bin>              open + scan + column benchmarks
MissionPlanner.Utilities.DFLogBench.exe <log.bin> convert <out.log>   time BinaryLog.ConvertBin
MissionPlanner.Utilities.DFLogBench.exe <log.bin> cachebench   native fresh scan vs managed cache path (needs >300MiB log)
```

`cachebench` deletes any existing index cache for the log, then measures:
managed cold open (scan + cache save), managed warm open (cache load, twice),
native open (cache skipped, twice), asserting via the LastScanNative /
LastLoadFromCache diagnostics that each run took the intended path.

## Reference results (2026-08-27, warm file cache)

322 MiB / 8.29M records, two full cachebench runs:

```
managed cold (scan+savecache): 9.47s / 9.30s   (cache file 44 MiB)
managed warm (cacheload):      1.52-1.60s
native (cache skipped):        1.37-1.45s
```

Native fresh scan beats even a warm cache hit; the cache save alone is
~7-8s of the cold open. 248 MiB log, other modes: scan-only 0.15s native vs
0.35s managed; ATT.Roll column 39k rows ~equal; SIM2 column 1.57M rows
0.03s native vs 3.14s legacy enumerator path; ConvertBin ~16.4s vs
`dflog convert` ~10.5s.
