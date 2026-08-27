"""Functional tests for the dflog Python module against the SITL corpus.

Run via rust/crates/dflog-py/run-tests.bat (builds the module, stages it as
dflog.pyd on sys.path, runs this file). Plain asserts - no pytest dependency.
"""
import sys
from pathlib import Path

import numpy as np

import dflog

CORPUS = Path(__file__).resolve().parents[3] / "testdata"


def test_open_and_types():
    log = dflog.LogFile(str(CORPUS / "copter.bin"))
    assert len(log) == 31867, len(log)  # phase-0 golden count
    assert log.types["ATT"] > 0
    assert "GPS" in log.types
    repr(log)


def test_format_metadata():
    log = dflog.LogFile(str(CORPUS / "copter.bin"))
    fmt = log.format("GPS")
    assert fmt["name"] == "GPS"
    fields = {f["name"]: f for f in fmt["fields"]}
    lat = fields["Lat"]
    assert lat["type"] == "L", lat
    assert lat["unit"] == "deglatitude"
    assert lat["multiplier"] == np.float32(1e-7)  # logged through a float cast
    assert log.formats()["ATT"]["name"] == "ATT"
    try:
        log.format("NOPE")
        raise AssertionError("expected KeyError")
    except KeyError:
        pass


def test_columns_numpy():
    log = dflog.LogFile(str(CORPUS / "copter.bin"))
    cols = log.columns("ATT", ["TimeUS", "Roll", "Pitch"])
    assert set(cols) == {"lineno", "TimeUS", "Roll", "Pitch"}
    n = len(cols["lineno"])
    assert n == log.types["ATT"]
    assert cols["lineno"].dtype == np.int64
    for name in ("TimeUS", "Roll", "Pitch"):
        assert cols[name].dtype == np.float64
        assert len(cols[name]) == n
    assert np.all(np.diff(cols["TimeUS"]) >= 0)  # board time is monotonic
    try:
        log.columns("ATT", ["NoSuchField"])
        raise AssertionError("expected KeyError")
    except KeyError:
        pass


def test_array_column():
    log = dflog.LogFile(str(CORPUS / "copter-isbd.bin"))
    linenos, samples = log.array_column("ISBD", "x")
    assert samples.dtype == np.int16
    assert samples.shape == (len(linenos), 32), samples.shape
    assert len(linenos) > 0


def test_messages_iteration():
    log = dflog.LogFile(str(CORPUS / "copter.bin"))
    cols = log.columns("ATT", ["Roll"])

    rolls = []
    for m in log.messages(types=["ATT"]):
        assert m.type == "ATT"
        assert m.time_us is not None
        assert "Roll" in m
        rolls.append(m["Roll"])
    assert rolls == list(cols["Roll"])  # iterator agrees with columnar path

    first = next(iter(log.messages(types=["MSG"])))
    assert isinstance(first["Message"], str) and first["Message"].startswith("ArduCopter")
    assert sorted(first.keys()) == sorted(first.to_dict().keys())
    try:
        first["NoSuchField"]
        raise AssertionError("expected KeyError")
    except KeyError:
        pass
    repr(first)


def test_time_base():
    log = dflog.LogFile(str(CORPUS / "copter.bin"))
    base = log.time_base()
    assert base is not None
    assert base.gps_start_unix_ms > 1_577_836_800_000  # after 2020-01-01 UTC
    assert base.wall_clock_unix_ms(base.ms_offset) == base.gps_start_unix_ms
    repr(base)


def test_from_bytes_and_empty():
    data = (CORPUS / "copter.bin").read_bytes()
    log = dflog.LogFile.from_bytes(data)
    assert len(log) == 31867

    empty = dflog.LogFile.from_bytes(b"")
    assert len(empty) == 0
    assert empty.types == {}
    assert empty.time_base() is None

    try:
        dflog.LogFile(str(CORPUS / "no-such-file.bin"))
        raise AssertionError("expected OSError")
    except OSError:
        pass


def main():
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    for test in tests:
        test()
        print(f"PASS {test.__name__}")
    print(f"{len(tests)} tests passed (dflog {dflog.__version__})")


if __name__ == "__main__":
    sys.exit(main())
