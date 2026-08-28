//! Round-trip verification of the parquet exporter against the access and
//! columns layers (runs only with `--features parquet`).
#![cfg(feature = "parquet")]

#[path = "../src/parquet_export.rs"]
mod parquet_export;

use std::fs::File;
use std::path::PathBuf;

use arrow_array::{
    Array, FixedSizeListArray, Float64Array, Int16Array, StringArray, TimestampMicrosecondArray,
    UInt64Array,
};
use parquet::arrow::arrow_reader::ParquetRecordBatchReaderBuilder;

use dflog_core::{columns, LogFile};

fn testdata(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../testdata")
        .join(name)
}

fn export_dir(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("dflog-parquet-test-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    dir
}

fn read_all(path: &PathBuf) -> Vec<arrow_array::RecordBatch> {
    let file = File::open(path).expect("parquet file");
    ParquetRecordBatchReaderBuilder::try_new(file)
        .expect("reader")
        .build()
        .expect("build")
        .collect::<Result<Vec<_>, _>>()
        .expect("batches")
}

#[test]
fn att_columns_round_trip_bitwise() {
    let dir = export_dir("att");
    parquet_export::export(&testdata("copter.bin"), &dir, Some("ATT"), false).expect("export");

    let log = LogFile::open(&testdata("copter.bin")).unwrap();
    let expected = columns::get_columns(&log, "ATT", &["TimeUS", "Roll", "Pitch"]).unwrap();
    let rows = expected.rows as usize;

    let batches = read_all(&dir.join("ATT.parquet"));
    let total: usize = batches.iter().map(|b| b.num_rows()).sum();
    assert_eq!(total, rows);

    let mut row = 0usize;
    for batch in &batches {
        let lineno = batch
            .column_by_name("lineno")
            .unwrap()
            .as_any()
            .downcast_ref::<UInt64Array>()
            .unwrap();
        let time_us = batch
            .column_by_name("TimeUS")
            .unwrap()
            .as_any()
            .downcast_ref::<UInt64Array>()
            .unwrap();
        let roll = batch
            .column_by_name("Roll")
            .unwrap()
            .as_any()
            .downcast_ref::<Float64Array>()
            .unwrap();
        for i in 0..batch.num_rows() {
            assert_eq!(lineno.value(i), expected.linenos[row]);
            assert_eq!(time_us.value(i) as f64, expected.values[row]);
            assert_eq!(roll.value(i), expected.values[rows + row], "row {row}");
            row += 1;
        }
    }

    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn time_utc_matches_time_base() {
    let dir = export_dir("time");
    parquet_export::export(&testdata("copter.bin"), &dir, Some("ATT"), false).expect("export");

    let log = LogFile::open(&testdata("copter.bin")).unwrap();
    let base = log.time_base().expect("corpus log has a time base");

    let batches = read_all(&dir.join("ATT.parquet"));
    let batch = &batches[0];
    let time_utc = batch
        .column_by_name("time_utc")
        .expect("time_utc column present")
        .as_any()
        .downcast_ref::<TimestampMicrosecondArray>()
        .unwrap();
    let time_us = batch
        .column_by_name("TimeUS")
        .unwrap()
        .as_any()
        .downcast_ref::<UInt64Array>()
        .unwrap();

    for i in 0..batch.num_rows().min(50) {
        let expected =
            base.gps_start_unix_ms * 1000 + (time_us.value(i) as i64 - base.ms_offset * 1000);
        assert_eq!(time_utc.value(i), expected, "row {i}");
    }

    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn strings_and_arrays_round_trip() {
    let dir = export_dir("mixed");
    parquet_export::export(&testdata("copter-isbd.bin"), &dir, Some("MSG,ISBD"), false)
        .expect("export");

    let log = LogFile::open(&testdata("copter-isbd.bin")).unwrap();

    // MSG.Message survives as utf8
    let expected_first = log
        .records_of(&["MSG"])
        .next()
        .and_then(|r| r.value("Message"))
        .and_then(|v| v.as_str().map(str::to_string))
        .unwrap();
    let batches = read_all(&dir.join("MSG.parquet"));
    let message = batches[0]
        .column_by_name("Message")
        .unwrap()
        .as_any()
        .downcast_ref::<StringArray>()
        .unwrap()
        .value(0)
        .to_string();
    assert_eq!(message, expected_first);

    // ISBD.x survives as fixed-size-list<int16, 32>, bitwise
    let expected = columns::get_array_column(&log, "ISBD", "x").unwrap();
    let batches = read_all(&dir.join("ISBD.parquet"));
    let total: usize = batches.iter().map(|b| b.num_rows()).sum();
    assert_eq!(total as u64, expected.rows);

    let mut row = 0usize;
    for batch in &batches {
        let x = batch
            .column_by_name("x")
            .unwrap()
            .as_any()
            .downcast_ref::<FixedSizeListArray>()
            .unwrap();
        for i in 0..batch.num_rows() {
            let cell = x.value(i);
            let shorts = cell.as_any().downcast_ref::<Int16Array>().unwrap();
            assert_eq!(shorts.len(), 32);
            for k in 0..32 {
                assert_eq!(
                    shorts.value(k),
                    expected.values[row * 32 + k],
                    "row {row}[{k}]"
                );
            }
            row += 1;
        }
    }

    let _ = std::fs::remove_dir_all(&dir);
}

/// --split-instances writes one file per IMU instance whose rows match the
/// instance-filtered columnar extraction exactly
#[test]
fn split_instances_partitions_imu() {
    let dir = export_dir("split");
    parquet_export::export(&testdata("copter.bin"), &dir, Some("IMU"), true).expect("export");

    let log = LogFile::open(&testdata("copter.bin")).unwrap();
    let all = columns::get_columns(&log, "IMU", &["GyrX"]).unwrap();

    assert!(
        !dir.join("IMU.parquet").exists(),
        "split export must not also write the interleaved file"
    );

    let mut total = 0u64;
    for instance in [0i64, 1] {
        let expected =
            columns::get_columns_filtered(&log, "IMU", &["GyrX"], Some(instance)).unwrap();
        assert!(
            expected.rows > 0,
            "corpus IMU should have instance {instance}"
        );

        let batches = read_all(&dir.join(format!("IMU_{instance}.parquet")));
        let rows: usize = batches.iter().map(|b| b.num_rows()).sum();
        assert_eq!(rows as u64, expected.rows, "instance {instance}");

        let mut row = 0usize;
        for batch in &batches {
            let lineno = batch
                .column_by_name("lineno")
                .unwrap()
                .as_any()
                .downcast_ref::<UInt64Array>()
                .unwrap();
            let gyrx = batch
                .column_by_name("GyrX")
                .unwrap()
                .as_any()
                .downcast_ref::<Float64Array>()
                .unwrap();
            for i in 0..batch.num_rows() {
                assert_eq!(lineno.value(i), expected.linenos[row]);
                assert_eq!(gyrx.value(i), expected.values[row]);
                row += 1;
            }
        }
        total += expected.rows;
    }

    assert_eq!(total, all.rows, "instances must partition the rows");

    let _ = std::fs::remove_dir_all(&dir);
}

/// every corpus log exports every type without error, with row counts
/// matching the record index
#[test]
fn full_export_row_counts_match_index() {
    for name in ["copter.bin", "plane.bin", "rover.bin", "copter-isbd.bin"] {
        let dir = export_dir(name);
        parquet_export::export(&testdata(name), &dir, None, false).expect(name);

        let log = LogFile::open(&testdata(name)).unwrap();
        let mut expected = 0u64;
        for record in log.records() {
            if log.name_to_id.get(&record.fmt.name) == Some(&record.fmt.id) {
                expected += 1;
            }
        }

        let mut exported = 0u64;
        for entry in std::fs::read_dir(&dir).unwrap() {
            let path = entry.unwrap().path();
            exported += read_all(&path)
                .iter()
                .map(|b| b.num_rows() as u64)
                .sum::<u64>();
        }
        assert_eq!(exported, expected, "{name}");

        let _ = std::fs::remove_dir_all(&dir);
    }
}
