# rust/

Cargo workspace for Mission Planner's Rust components - one workspace, one
toolchain, one CI surface (`rust/**`). Layout follows the mixed-language
monorepo convention (cf. Signal's libsignal): later components slot in as
additional members under `crates/`.

Current members (see docs/dflog-rust-core-plan.md):

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
  verified against the corpus by tests/MissionPlanner.Utilities.Tests).

Build:

```bash
cd rust
cargo build --release   # produces target/release/dflog_ffi.dll
cargo test
```

The test csproj copies `dflog_ffi.dll` to its output when present. At
runtime the native scanner is opt-in via `DFLogBuffer.UseNativeScan`
(env var `DFLOG_NATIVE=1`); the managed scanner remains the fallback
whenever the library is missing or a scan fails.
