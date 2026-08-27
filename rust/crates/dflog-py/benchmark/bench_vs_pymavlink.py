"""Benchmark dflog against pymavlink's DFReader on the same logs.

Usage: python bench_vs_pymavlink.py <log.bin> [runs]

Measures, with `runs` repetitions each (report the median; single samples
mislead):
  open      - construct the reader (index/format scan)
  iterate   - decode every message start to finish
  columns   - extract ATT TimeUS/Roll/Pitch as numpy arrays; for pymavlink
              that is the idiomatic recv_match loop + np.array, for dflog
              the columnar API

Needs: numpy, pymavlink, and the dflog extension module on sys.path
(rust/crates/dflog-py/run-tests.bat stages it as %TEMP%\\dflog-py-test\\dflog.pyd).
"""
import statistics
import sys
import time

import numpy as np
from pymavlink import DFReader

import dflog


def timed(fn, runs):
    samples = []
    result = None
    for _ in range(runs):
        t0 = time.perf_counter()
        result = fn()
        samples.append(time.perf_counter() - t0)
    return statistics.median(samples), samples, result


def pymavlink_open(path):
    return DFReader.DFReader_binary(path)


def pymavlink_iterate(path):
    log = DFReader.DFReader_binary(path)
    n = 0
    while log.recv_msg() is not None:
        n += 1
    return n


def pymavlink_columns(path):
    log = DFReader.DFReader_binary(path)
    time_us, roll, pitch = [], [], []
    while True:
        m = log.recv_match(type="ATT")
        if m is None:
            break
        time_us.append(m.TimeUS)
        roll.append(m.Roll)
        pitch.append(m.Pitch)
    return np.array(time_us), np.array(roll), np.array(pitch)


def dflog_open(path):
    return dflog.LogFile(path)


def dflog_iterate(path):
    log = dflog.LogFile(path)
    n = 0
    for _ in log.messages():
        n += 1
    return n


def dflog_columns(path):
    log = dflog.LogFile(path)
    cols = log.columns("ATT", ["TimeUS", "Roll", "Pitch"])
    return cols["TimeUS"], cols["Roll"], cols["Pitch"]


def report(name, med_py, med_rs, extra=""):
    speedup = med_py / med_rs if med_rs > 0 else float("inf")
    print(f"{name:<10} pymavlink {med_py:8.3f}s   dflog {med_rs:8.3f}s   {speedup:6.1f}x  {extra}")


def main():
    path = sys.argv[1]
    runs = int(sys.argv[2]) if len(sys.argv) > 2 else 3
    print(f"log: {path}  runs: {runs} (median reported)")

    med_py, all_py, _ = timed(lambda: pymavlink_open(path), runs)
    med_rs, all_rs, log = timed(lambda: dflog_open(path), runs)
    report("open", med_py, med_rs, f"({len(log)} records)")

    med_py, all_py, n_py = timed(lambda: pymavlink_iterate(path), runs)
    med_rs, all_rs, n_rs = timed(lambda: dflog_iterate(path), runs)
    report("iterate", med_py, med_rs, f"(msgs: pymavlink {n_py}, dflog {n_rs})")

    med_py, all_py, cols_py = timed(lambda: pymavlink_columns(path), runs)
    med_rs, all_rs, cols_rs = timed(lambda: dflog_columns(path), runs)
    rows = len(cols_rs[0])
    match = all(np.array_equal(a, b) for a, b in zip(cols_py, cols_rs))
    report("columns", med_py, med_rs, f"(ATT rows {rows}, values equal: {match})")


if __name__ == "__main__":
    main()
