# dflog (Python bindings)

Fast reader for ArduPilot dataflash `.bin` logs, backed by the Rust
`dflog-core` crate. Columnar-first — field data comes back as numpy
arrays — with a DFReader-style message iterator for migration.

```python
import dflog

log = dflog.LogFile("flight.bin")
log.types                                # {"ATT": 196, "GPS": 62, ...}
log.format("GPS")                        # fields, format chars, units, multipliers

cols = log.columns("ATT", ["TimeUS", "Roll"])   # dict of numpy float64 arrays
linenos, samples = log.array_column("ISBD", "x") # int16 sample blocks, rows x 32

for m in log.messages(types=["ATT", "GPS"]):     # DFReader-ish records
    m["Roll"], m.type, m.lineno, m.time_us

base = log.time_base()                   # GPS wall-clock correlation, or None
base.wall_clock_unix_ms(m.time_us / 1000)
```

Values decode exactly as the core access layer does: legacy scaling on
`c/C/e/E/L` fields, plain trimmed strings, raw mode numbers. Units and
multipliers from the log's own UNIT/MULT/FMTU records are exposed as
metadata (`format()`), never applied — multiplier factors are exactly what
the autopilot logged (it writes them through a float cast, so expect
`np.float32(1e-7)`, not `1e-7`).

## Building and testing

Local test run (no pip install needed; needs Python 3.9+ with numpy):

```
run-tests.bat
```

It builds the abi3 cdylib with cargo, stages it as `dflog.pyd` on
`sys.path`, and runs `tests/test_dflog.py` against the SITL corpus in
`tests/MissionPlanner.Utilities.Tests/testdata`.

Wheels are built with [maturin](https://github.com/PyO3/maturin) from this
directory (`maturin build --release`); the abi3-py39 target produces one
wheel per platform covering CPython 3.9+. The crate is not in the
workspace's `default-members`, so plain `cargo build`/`cargo test` at the
workspace root never require a Python toolchain.

## Benchmark vs pymavlink DFReader

`benchmark/bench_vs_pymavlink.py <log.bin> [runs]` times both libraries on
the same log (median of several runs; single samples mislead): `open`
(construct the reader), `iterate` (decode every message), and `columns`
(log file to ATT TimeUS/Roll/Pitch numpy arrays, end to end - the
idiomatic recv_match loop for pymavlink, the columnar API for dflog).
Column values are asserted bit-identical between the two.

Benchmark logs are the SITL corpus log concatenated to size. Cut each
copy at a record boundary first - copter.bin ends in a truncated record,
and a partial record at a seam desyncs DFReader for ~73 bytes per seam
(dflog resyncs silently; the byte-wise "bad header" walk also skews
DFReader's numbers).

Reference results (2026-08-28, Python 3.14.3, numpy 2.5.2,
pymavlink 2.4.49, warm file cache, median of 3):

```
248 MiB / 6.37M records          pymavlink      dflog
open                                2.20s      0.12s    17.7x
iterate (all messages)             20.60s      4.33s     4.8x
columns (ATT, 39200 rows)           3.99s      0.13s    29.7x

62 MiB / 1.59M records
open                                0.54s      0.03s    17.5x
iterate (all messages)              5.13s      1.01s     5.1x
columns (ATT, 9800 rows)            0.98s      0.03s    32.8x
```

The message iterator is ~5x faster; the columnar path - the reason to
use this library - turns a quarter-gigabyte log into numpy arrays in
about a tenth of a second, ~30x faster than iterating with DFReader.
