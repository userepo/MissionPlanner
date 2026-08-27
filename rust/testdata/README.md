# rust/testdata

SITL corpus logs used by the Rust crates' tests (dflog-core, dflog-cli
with `--features parquet`, dflog-py, and the fuzz seeds). These are
byte-for-byte copies of the canonical corpus in
`tests/MissionPlanner.Utilities.Tests/testdata`, kept locally so the Rust
workspace is self-contained.

If the canonical corpus is ever regenerated, refresh these copies too -
several Rust tests pin exact values from them (record counts, GPS.Lat
units, MSG text), the same way the C# characterization goldens do.
