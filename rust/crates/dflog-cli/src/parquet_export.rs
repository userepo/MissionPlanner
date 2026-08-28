//! `dflog parquet` - one parquet file per message type, columns typed from
//! the format chars via the general access layer (legacy scaling on
//! c/C/e/E/L, plain trimmed strings, raw mode numbers; `a` fields as
//! fixed-size-list<int16, 32>). Every file carries `lineno` (the global
//! record index) and, when the log has a GPS time base and the type has a
//! TimeUS field, `time_utc` as a microsecond UTC timestamp.

use std::collections::hash_map::Entry;
use std::collections::HashMap;
use std::fs::File;
use std::path::Path;
use std::sync::Arc;

use arrow_array::builder::{
    FixedSizeListBuilder, Float64Builder, Int16Builder, Int64Builder, StringBuilder,
    TimestampMicrosecondBuilder, UInt64Builder,
};
use arrow_array::{ArrayRef, RecordBatch};
use arrow_schema::{DataType, Field, Schema, TimeUnit};
use parquet::arrow::ArrowWriter;

use dflog_core::access::Value;
use dflog_core::LogFile;

const BATCH_ROWS: usize = 65_536;
const SHORTS_LEN: i32 = 32;

enum ColBuilder {
    I64(Int64Builder),
    U64(UInt64Builder),
    F64(Float64Builder),
    Str(StringBuilder),
    Shorts(FixedSizeListBuilder<Int16Builder>),
}

impl ColBuilder {
    fn for_value(value: &Value) -> ColBuilder {
        match value {
            Value::I64(_) => ColBuilder::I64(Int64Builder::new()),
            Value::U64(_) => ColBuilder::U64(UInt64Builder::new()),
            Value::F64(_) => ColBuilder::F64(Float64Builder::new()),
            Value::Str(_) => ColBuilder::Str(StringBuilder::new()),
            Value::Shorts(_) => {
                ColBuilder::Shorts(FixedSizeListBuilder::new(Int16Builder::new(), SHORTS_LEN))
            }
        }
    }

    fn data_type(&self) -> DataType {
        match self {
            ColBuilder::I64(_) => DataType::Int64,
            ColBuilder::U64(_) => DataType::UInt64,
            ColBuilder::F64(_) => DataType::Float64,
            ColBuilder::Str(_) => DataType::Utf8,
            ColBuilder::Shorts(_) => DataType::FixedSizeList(
                Arc::new(Field::new("item", DataType::Int16, true)),
                SHORTS_LEN,
            ),
        }
    }

    fn append(&mut self, value: &Value) {
        match (self, value) {
            (ColBuilder::I64(b), Value::I64(v)) => b.append_value(*v),
            (ColBuilder::U64(b), Value::U64(v)) => b.append_value(*v),
            (ColBuilder::F64(b), Value::F64(v)) => b.append_value(*v),
            (ColBuilder::Str(b), Value::Str(v)) => b.append_value(v),
            (ColBuilder::Shorts(b), Value::Shorts(v)) => {
                b.values().append_slice(v);
                b.append(true);
            }
            // decode variants are fixed per format char, so this is
            // unreachable for well-typed input; null keeps rows aligned
            (b, _) => b.append_null(),
        }
    }

    fn append_null(&mut self) {
        match self {
            ColBuilder::I64(b) => b.append_null(),
            ColBuilder::U64(b) => b.append_null(),
            ColBuilder::F64(b) => b.append_null(),
            ColBuilder::Str(b) => b.append_null(),
            ColBuilder::Shorts(b) => {
                for _ in 0..SHORTS_LEN {
                    b.values().append_null();
                }
                b.append(false);
            }
        }
    }

    fn finish(&mut self) -> ArrayRef {
        match self {
            ColBuilder::I64(b) => Arc::new(b.finish()),
            ColBuilder::U64(b) => Arc::new(b.finish()),
            ColBuilder::F64(b) => Arc::new(b.finish()),
            ColBuilder::Str(b) => Arc::new(b.finish()),
            ColBuilder::Shorts(b) => Arc::new(b.finish()),
        }
    }
}

struct TypeWriter {
    writer: ArrowWriter<File>,
    schema: Arc<Schema>,
    lineno: UInt64Builder,
    /// present when the log has a time base and the type a TimeUS field
    time_utc: Option<TimestampMicrosecondBuilder>,
    /// (field name, builder), aligned with the kept `values()` positions
    fields: Vec<(String, ColBuilder)>,
    /// positions in the record's `values()` output that map to `fields`
    kept: Vec<usize>,
    time_us_position: Option<usize>,
    rows_in_batch: usize,
    rows_total: u64,
}

impl TypeWriter {
    /// Column layout from the first record of the type: `values()` output
    /// positions are stable per format string, so the first record fixes
    /// the schema. Duplicate and reserved names keep the first occurrence
    /// (matching the access layer's first-label-match semantics).
    fn create(
        out_dir: &Path,
        file_stem: &str,
        values: &[(&str, Value)],
        has_time_base: bool,
    ) -> Result<TypeWriter, String> {
        let mut fields = Vec::new();
        let mut kept = Vec::new();
        let mut time_us_position = None;
        let mut seen = vec!["lineno".to_string(), "time_utc".to_string()];

        for (position, (label, value)) in values.iter().enumerate() {
            if *label == "TimeUS" {
                time_us_position = Some(position);
            }
            if label.is_empty() || seen.iter().any(|s| s == label) {
                continue;
            }
            seen.push(label.to_string());
            fields.push((label.to_string(), ColBuilder::for_value(value)));
            kept.push(position);
        }

        let time_utc = (has_time_base && time_us_position.is_some())
            .then(|| TimestampMicrosecondBuilder::new().with_timezone("UTC"));

        let mut schema_fields = vec![Field::new("lineno", DataType::UInt64, false)];
        if time_utc.is_some() {
            schema_fields.push(Field::new(
                "time_utc",
                DataType::Timestamp(TimeUnit::Microsecond, Some("UTC".into())),
                true,
            ));
        }
        for (name, builder) in &fields {
            schema_fields.push(Field::new(name, builder.data_type(), true));
        }
        let schema = Arc::new(Schema::new(schema_fields));

        let out_path = out_dir.join(format!("{file_stem}.parquet"));
        let file = File::create(&out_path).map_err(|e| format!("{}: {e}", out_path.display()))?;
        let writer =
            ArrowWriter::try_new(file, Arc::clone(&schema), None).map_err(|e| e.to_string())?;

        Ok(TypeWriter {
            writer,
            schema,
            lineno: UInt64Builder::new(),
            time_utc,
            fields,
            kept,
            time_us_position,
            rows_in_batch: 0,
            rows_total: 0,
        })
    }

    fn append(
        &mut self,
        lineno: u64,
        values: &[(&str, Value)],
        time_base: Option<&dflog_core::time::TimeBase>,
    ) -> Result<(), String> {
        self.lineno.append_value(lineno);

        if let Some(time_utc) = &mut self.time_utc {
            let stamp = self
                .time_us_position
                .and_then(|p| values.get(p))
                .and_then(|(_, v)| match v {
                    Value::U64(us) => Some(*us as i64),
                    Value::I64(us) => Some(*us),
                    _ => None,
                })
                .and_then(|us| {
                    let base = time_base?;
                    Some(base.gps_start_unix_ms * 1000 + (us - base.ms_offset * 1000))
                });
            match stamp {
                Some(us) => time_utc.append_value(us),
                None => time_utc.append_null(),
            }
        }

        for (i, position) in self.kept.iter().enumerate() {
            match values.get(*position) {
                Some((_, value)) => self.fields[i].1.append(value),
                // shorter values() than the schema row: truncated format
                None => self.fields[i].1.append_null(),
            }
        }

        self.rows_in_batch += 1;
        self.rows_total += 1;
        if self.rows_in_batch >= BATCH_ROWS {
            self.flush_batch()?;
        }
        Ok(())
    }

    fn flush_batch(&mut self) -> Result<(), String> {
        if self.rows_in_batch == 0 {
            return Ok(());
        }
        let mut arrays: Vec<ArrayRef> = vec![Arc::new(self.lineno.finish())];
        if let Some(time_utc) = &mut self.time_utc {
            arrays.push(Arc::new(time_utc.finish()));
        }
        for (_, builder) in &mut self.fields {
            arrays.push(builder.finish());
        }
        let batch =
            RecordBatch::try_new(Arc::clone(&self.schema), arrays).map_err(|e| e.to_string())?;
        self.writer.write(&batch).map_err(|e| e.to_string())?;
        self.rows_in_batch = 0;
        Ok(())
    }

    fn finish(mut self) -> Result<u64, String> {
        self.flush_batch()?;
        self.writer.close().map_err(|e| e.to_string())?;
        Ok(self.rows_total)
    }
}

/// Export one parquet file per message type into `out_dir`; `types` limits
/// the export (comma-separated names, error on unknown), None means every
/// type that appears in the log. With `split_instances`, a type with an
/// instance field ('#' unit id) writes one file per instance value
/// (`IMU_0.parquet`, `IMU_1.parquet`) instead of one interleaved table.
pub fn export(
    path: &Path,
    out_dir: &Path,
    types: Option<&str>,
    split_instances: bool,
) -> Result<(), String> {
    let log = LogFile::open(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let time_base = log.time_base();
    let units = log.units();

    let selected: Option<Vec<u8>> = match types {
        Some(csv) => {
            let mut ids = Vec::new();
            for name in csv.split(',') {
                let &id = log
                    .name_to_id
                    .get(name)
                    .ok_or_else(|| format!("unknown message type: {name}"))?;
                ids.push(id);
            }
            Some(ids)
        }
        None => None,
    };

    std::fs::create_dir_all(out_dir).map_err(|e| format!("{}: {e}", out_dir.display()))?;

    // per type: label of its instance field, resolved once ('#' unit id);
    // matched against `values()` output by name because that output skips
    // undecodable fields and format-order indexes may not line up
    let mut instance_labels: HashMap<u8, Option<String>> = HashMap::new();

    let mut writers: HashMap<(u8, Option<i64>), TypeWriter> = HashMap::new();
    for record in log.records() {
        let id = record.fmt.id;
        if let Some(ids) = &selected {
            if !ids.contains(&id) {
                continue;
            }
        }
        // per-name export: ids orphaned by a later same-name FMT are skipped
        if log.name_to_id.get(&record.fmt.name) != Some(&id) {
            continue;
        }

        let values = record.values();

        let instance = if split_instances {
            let label = instance_labels.entry(id).or_insert_with(|| {
                units
                    .instance_field_index(id)
                    .and_then(|index| record.fmt.labels.get(index).cloned())
            });
            // a record whose instance field failed to decode (truncated
            // payload) lands in the unsuffixed file
            label.as_ref().and_then(|label| {
                values
                    .iter()
                    .find(|(name, _)| name == label)
                    .and_then(|(_, value)| value.as_f64())
                    .map(|v| v as i64)
            })
        } else {
            None
        };

        let writer = match writers.entry((id, instance)) {
            Entry::Occupied(e) => e.into_mut(),
            Entry::Vacant(e) => {
                let file_stem = match instance {
                    Some(instance) => format!("{}_{instance}", record.type_name()),
                    None => record.type_name().to_string(),
                };
                e.insert(TypeWriter::create(
                    out_dir,
                    &file_stem,
                    &values,
                    time_base.is_some(),
                )?)
            }
        };
        writer.append(record.lineno, &values, time_base.as_ref())?;
    }

    let mut names: Vec<(String, u64)> = Vec::new();
    for ((id, instance), writer) in writers {
        let name = match instance {
            Some(instance) => format!("{}_{instance}", log.fmts[&id].name),
            None => log.fmts[&id].name.clone(),
        };
        let rows = writer.finish()?;
        names.push((name, rows));
    }
    names.sort();
    for (name, rows) in &names {
        eprintln!("{name}.parquet: {rows} rows");
    }
    eprintln!("{} files in {}", names.len(), out_dir.display());
    Ok(())
}
