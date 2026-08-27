# rust/

Cargo workspace for Mission Planner's Rust components - one workspace, one
toolchain, one CI surface (`rust/**`). Layout follows the mixed-language
monorepo convention (cf. Signal's libsignal): later components slot in as
additional members under `crates/`.

Current members:

- `crates/dflog-core` - ArduPilot dataflash (`.bin`) log parsing/indexing.
  Behaviorally a byte-exact port of the C# `BinaryLog` scanner; the phase-0
  characterization goldens in `tests/MissionPlanner.Utilities.Tests` are the
  contract.
- `crates/dflog-ffi` - `cdylib` C ABI consumed by
  `ExtLibs/Utilities/DFLogNative.cs` over P/Invoke. Panics never cross the
  boundary; errors are codes plus a per-thread `dflog_last_error` message.
- `crates/dflog-cli` - the standalone `dflog` binary: `info` (record/type
  summary), `dump` (numeric columns as CSV), `convert` (text conversion,
  byte-compatible with Mission Planner's BinaryLog.ConvertBin run headless;
  verified against the corpus by tests/MissionPlanner.Utilities.Tests), and
  `parquet` (one file per message type with lineno + UTC timestamp columns;
  opt-in via `--features parquet`, which pulls the arrow dependency tree).

Build:

```bash
cd rust
cargo build --release   # produces target/release/dflog_ffi.dll
cargo test              # includes the deterministic mutation-fuzz smoke test
```

Fuzzing (phase D): `fuzz/` holds cargo-fuzz targets (`scan`, `convert`,
`columns`) - excluded from the workspace since libFuzzer needs nightly and
prefers Linux; run under WSL:

```bash
rustup toolchain install nightly && cargo install cargo-fuzz
cd rust
mkdir -p fuzz/corpus/scan
head -c 8192 ../tests/MissionPlanner.Utilities.Tests/testdata/copter.bin > fuzz/corpus/scan/seed
cargo +nightly fuzz run scan -- -max_total_time=300 -max_len=65536
```

Crashing inputs land in `fuzz/artifacts/<target>/`; distill every finding
into `crates/dflog-core/tests/fuzz_smoke.rs` as a permanent regression case.

The test csproj copies `dflog_ffi.dll` to its output when present. At
runtime the native path is controlled by `DFLogBuffer.UseNativeScan`
(explicit set > `DFLOG_NATIVE` env var > the `dflog_native` setting); the
managed path remains the fallback whenever the library is missing or a
call fails.

Packaging: the app ships the checked-in copy at
`ExtLibs/Utilities/dflog_ffi.dll` (Windows x64; other platforms use the
managed fallback). After any change under `rust/`, run `update-dll.bat`
and commit the refreshed DLL - the `CheckedInDllMatchesExpectedAbi` unit
test fails when the checked-in copy lags the sources' ABI.
