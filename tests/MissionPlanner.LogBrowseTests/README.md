# MissionPlanner.LogBrowseTests

Manual GUI verification harness for the LogBrowse typed-column fast path
(docs/dflog-rust-core-plan.md phase B). Not part of any automated run - it
opens the real `LogBrowse` WinForms window on screen.

It loads a dataflash log into the actual `LogBrowse` form, invokes the real
`GraphItem` path for `ATT.Roll` and `IMU[0].GyrX` (the instanced case),
waits for the curves, then saves the rendered graph as `<prefix>.png` and
every plotted point as `<prefix>.csv`. When run with `DFLOG_NATIVE=1` it
also asserts the Rust scanner actually ran (`nativescan=True` in the OK
line).

## Usage

Build (also builds the whole app), then from `bin/Debug/net472`:

```
set DFLOG_NATIVE=1
MissionPlanner.LogBrowseTests.exe graph ..\..\..\..\MissionPlanner.Utilities.Tests\testdata\copter.bin native

set DFLOG_NATIVE=
MissionPlanner.LogBrowseTests.exe graph ..\..\..\..\MissionPlanner.Utilities.Tests\testdata\copter.bin managed

MissionPlanner.LogBrowseTests.exe compare native managed
```

`compare` sorts plotted points by curve label + x (curve add-order differs
between modes: the native path adds curves synchronously in request order,
the legacy threadpool path in completion order) and reports the row count,
the maximum y delta, and the number of differing pixels between the PNGs.

Set `LOGBROWSE_TIMEAXIS=1` to graph with the time x-axis (the GPS-corrected
XDate values) instead of line numbers.

## Reference results (2026-08-26/27, corpus copter.bin)

Line-number axis and time axis produce the same numbers:

```
COMPARE rows=686 maxYdelta=4.997E-009 diffpixels=0 of 1022528
```

- x values (line numbers) identical in both modes
- max y delta ~5e-9: the documented divergence - the legacy path rounds
  floats through 7-significant-digit strings, the native path does not
- rendered graphs pixel-identical

## Gotchas

- The process must be 64-bit (`PlatformTarget=x64` here): `dflog_ffi.dll`
  is x64 and a 32-bit host silently falls back to the managed scanner.
  The real MissionPlanner.exe is true AnyCPU and runs 64-bit.
- The harness drives private members of `LogBrowse` (`logdata`, `zg1`,
  `GraphItem`, `chk_time`) via reflection; renames there will surface as
  a "FAIL reflection" line.
- `Loading.ShowLoading`/`Close` skip the splash when `MainV2.instance` is
  null - that null-guard is what makes hosting the form outside the full
  app possible.
