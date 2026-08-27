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
