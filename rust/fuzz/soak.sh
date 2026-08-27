#!/bin/bash
# Overnight fuzz soak (phase D exit criterion): runs the scan, convert and
# columns targets IN PARALLEL for TIME seconds each (default 4 hours, so the
# whole soak finishes in ~4h wall clock / 12h CPU across the three targets).
#
# Run from the rust/ directory under WSL (pipe through sed to strip CRLF):
#   cd /mnt/c/<repo>/rust && sed 's/\r$//' fuzz/soak.sh | TIME=14400 bash
#
# Logs land in ~/dflog-soak-<target>.log; crashing inputs (if any) in
# fuzz/artifacts/<target>/ - distill every finding into
# crates/dflog-core/tests/fuzz_smoke.rs.
set -u
source ~/.cargo/env
export CARGO_TARGET_DIR=~/dflog-fuzz-target

TIME="${TIME:-14400}"

for t in scan convert columns; do
    mkdir -p "fuzz/corpus/$t"
    head -c 8192 ../tests/MissionPlanner.Utilities.Tests/testdata/copter.bin > "fuzz/corpus/$t/seed1"
    tail -c 8192 ../tests/MissionPlanner.Utilities.Tests/testdata/copter-isbd.bin > "fuzz/corpus/$t/seed2"
done

echo "soak: $TIME seconds per target, parallel; logs in ~/dflog-soak-<target>.log"
for t in scan convert columns; do
    cargo +nightly fuzz run "$t" -- -max_total_time="$TIME" -max_len=65536 -print_final_stats=1 \
        > ~/dflog-soak-"$t".log 2>&1 &
done
wait

echo "=== summary ==="
for t in scan convert columns; do
    echo "--- $t ---"
    grep -E "DONE|Done [0-9]+ runs|stat::number_of_executed_units" ~/dflog-soak-"$t".log | tail -3
done

crashes=$(find fuzz/artifacts -type f 2>/dev/null | wc -l)
echo "crash artifacts: $crashes"
find fuzz/artifacts -type f 2>/dev/null
exit "$((crashes > 0))"
