#!/bin/bash
# Fuzz soak: runs the TARGETS (default: all four) IN PARALLEL for TIME
# seconds each. The original phase-D exit criterion ran scan/convert/columns
# at TIME=14400 (~4h wall clock); the `access` target covers the general
# access layer, time correlation, units metadata and instance filtering,
# which postdate that campaign.
#
# Run from the rust/ directory under WSL (pipe through sed to strip CRLF):
#   cd /mnt/c/<repo>/rust && sed 's/\r$//' fuzz/soak.sh | TIME=14400 bash
#   cd /mnt/c/<repo>/rust && sed 's/\r$//' fuzz/soak.sh | TARGETS="access columns" TIME=7200 bash
#
# Logs land in ~/dflog-soak-<target>.log; crashing inputs (if any) in
# fuzz/artifacts/<target>/ - distill every finding into
# crates/dflog-core/tests/fuzz_smoke.rs.
set -u
source ~/.cargo/env
export CARGO_TARGET_DIR=~/dflog-fuzz-target

TIME="${TIME:-14400}"
TARGETS="${TARGETS:-scan convert columns access}"

for t in $TARGETS; do
    mkdir -p "fuzz/corpus/$t"
    head -c 8192 testdata/copter.bin > "fuzz/corpus/$t/seed1"
    tail -c 8192 testdata/copter-isbd.bin > "fuzz/corpus/$t/seed2"
done

echo "soak: $TIME seconds per target ($TARGETS), parallel; logs in ~/dflog-soak-<target>.log"
for t in $TARGETS; do
    cargo +nightly fuzz run "$t" -- -max_total_time="$TIME" -max_len=65536 -print_final_stats=1 \
        > ~/dflog-soak-"$t".log 2>&1 &
done
wait

echo "=== summary ==="
for t in $TARGETS; do
    echo "--- $t ---"
    grep -E "DONE|Done [0-9]+ runs|stat::number_of_executed_units" ~/dflog-soak-"$t".log | tail -3
done

crashes=$(find fuzz/artifacts -type f 2>/dev/null | wc -l)
echo "crash artifacts: $crashes"
find fuzz/artifacts -type f 2>/dev/null
exit "$((crashes > 0))"
