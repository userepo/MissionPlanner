//! Typed columnar extraction (phase B of docs/dflog-rust-core-plan.md).
//!
//! Decodes all records of one message type into `f64` columns for the
//! requested field labels, plus the global record index ("line number") per
//! row so callers can align with the existing DFLogBuffer numbering.
//!
//! Semantics mirror the C# `BinaryLog.GetObjectFromMessage` decoders:
//! little-endian primitives; `c`/`C`/`e`/`E` scaled by 1/100; `L` by 1e-7;
//! `g` is an IEEE half; `M` is the raw mode byte; a record whose payload runs
//! past end-of-file decodes the missing bytes as zero, exactly like the C#
//! partial `Stream.Read` into a zeroed buffer.
//!
//! Precision note (deliberate, documented in the plan): these are the *raw*
//! decoded values. The legacy graphing path round-trips through 7-significant
//! -digit strings, so it loses float precision that this path keeps.

use crate::LogFile;

#[derive(Debug)]
pub enum ColumnError {
    UnknownType(String),
    UnknownField { field: String, available: String },
    /// field exists but has no numeric decoding (n/N/Z strings, `a` arrays)
    NotNumeric { field: String, code: char },
    /// FMT format/labels column counts disagree; no stable field mapping
    MalformedFormat(String),
}

impl std::fmt::Display for ColumnError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ColumnError::UnknownType(t) => write!(f, "unknown message type {t}"),
            ColumnError::UnknownField { field, available } => {
                write!(f, "unknown field {field}; available: {available}")
            }
            ColumnError::NotNumeric { field, code } => {
                write!(f, "field {field} (format '{code}') is not numeric")
            }
            ColumnError::MalformedFormat(t) => write!(f, "malformed FMT for {t}"),
        }
    }
}

impl std::error::Error for ColumnError {}

/// Column-major result: `values[col * rows + row]`.
pub struct Columns {
    pub rows: u64,
    pub cols: u32,
    pub linenos: Vec<u64>,
    pub values: Vec<f64>,
}

fn field_size(code: char) -> Option<usize> {
    Some(match code {
        'b' | 'B' | 'M' => 1,
        'h' | 'H' | 'c' | 'C' | 'g' => 2,
        'i' | 'I' | 'e' | 'E' | 'L' | 'f' => 4,
        'q' | 'Q' | 'd' => 8,
        'n' => 4,
        'N' => 16,
        'Z' => 64,
        'a' => 64,
        _ => return None,
    })
}

fn half_to_f32(bits: u16) -> f32 {
    // IEEE 754 binary16 -> binary32
    let sign = ((bits >> 15) & 1) as u32;
    let exp = ((bits >> 10) & 0x1F) as u32;
    let frac = (bits & 0x3FF) as u32;
    let out = match exp {
        0 => {
            if frac == 0 {
                sign << 31
            } else {
                // subnormal: normalize
                let mut e = 127 - 15 + 1;
                let mut f = frac;
                while f & 0x400 == 0 {
                    f <<= 1;
                    e -= 1;
                }
                (sign << 31) | ((e as u32) << 23) | ((f & 0x3FF) << 13)
            }
        }
        0x1F => (sign << 31) | 0x7F80_0000 | (frac << 13),
        _ => (sign << 31) | ((exp + 127 - 15) << 23) | (frac << 13),
    };
    f32::from_bits(out)
}

fn read_zero_padded(data: &[u8], start: usize, len: usize) -> [u8; 8] {
    let mut buf = [0u8; 8];
    if start < data.len() {
        let take = len.min(data.len() - start).min(8);
        buf[..take].copy_from_slice(&data[start..start + take]);
    }
    buf
}

fn decode(code: char, data: &[u8], at: usize) -> f64 {
    let b = read_zero_padded(data, at, field_size(code).unwrap_or(0));
    match code {
        'b' => (b[0] as i8) as f64,
        'B' | 'M' => b[0] as f64,
        'h' => i16::from_le_bytes([b[0], b[1]]) as f64,
        'H' => u16::from_le_bytes([b[0], b[1]]) as f64,
        'i' => i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64,
        'I' => u32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64,
        'q' => i64::from_le_bytes(b) as f64,
        'Q' => u64::from_le_bytes(b) as f64,
        'f' => f32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64,
        'd' => f64::from_le_bytes(b),
        'g' => half_to_f32(u16::from_le_bytes([b[0], b[1]])) as f64,
        'c' => i16::from_le_bytes([b[0], b[1]]) as f64 / 100.0,
        'C' => u16::from_le_bytes([b[0], b[1]]) as f64 / 100.0,
        'e' => i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 100.0,
        'E' => u32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 100.0,
        'L' => i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 10000000.0,
        _ => f64::NAN,
    }
}

/// Decode `fields` of every `type_name` record in the log.
pub fn get_columns(log: &LogFile, type_name: &str, fields: &[&str]) -> Result<Columns, ColumnError> {
    let &id = log
        .name_to_id
        .get(type_name)
        .ok_or_else(|| ColumnError::UnknownType(type_name.into()))?;
    let fmt = log
        .fmts
        .get(&id)
        .ok_or_else(|| ColumnError::UnknownType(type_name.into()))?;

    let codes: Vec<char> = fmt.format.chars().collect();
    if codes.len() != fmt.labels.len() {
        return Err(ColumnError::MalformedFormat(type_name.into()));
    }

    // label -> (payload byte offset, format code); first match wins like the
    // C# FindMessageOffset lookup
    let mut offsets = Vec::with_capacity(fields.len());
    for &field in fields {
        let pos = fmt
            .labels
            .iter()
            .position(|l| l == field)
            .ok_or_else(|| ColumnError::UnknownField {
                field: field.into(),
                available: fmt.labels.join(","),
            })?;
        let code = codes[pos];
        if field_size(code).is_none() || matches!(code, 'n' | 'N' | 'Z' | 'a') {
            return Err(ColumnError::NotNumeric { field: field.into(), code });
        }
        let offset: usize = codes[..pos].iter().map(|&c| field_size(c).unwrap_or(0)).sum();
        offsets.push((offset, code));
    }

    let data = log.data();
    let mut linenos = Vec::new();
    for (i, &t) in log.index.types.iter().enumerate() {
        if t == id {
            linenos.push(i as u64);
        }
    }

    let rows = linenos.len();
    let mut values = vec![0f64; rows * fields.len()];
    for (col, &(field_offset, code)) in offsets.iter().enumerate() {
        let out = &mut values[col * rows..(col + 1) * rows];
        for (row, &lineno) in linenos.iter().enumerate() {
            let payload = log.index.offsets[lineno as usize] as usize + 3;
            out[row] = decode(code, data, payload + field_offset);
        }
    }

    Ok(Columns {
        rows: rows as u64,
        cols: fields.len() as u32,
        linenos,
        values,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn half_conversion_basics() {
        assert_eq!(half_to_f32(0x0000), 0.0);
        assert_eq!(half_to_f32(0x3C00), 1.0);
        assert_eq!(half_to_f32(0xC000), -2.0);
        assert_eq!(half_to_f32(0x7BFF), 65504.0);
        assert!(half_to_f32(0x7C00).is_infinite());
    }

    #[test]
    fn decodes_scaled_and_primitive_fields() {
        // synthetic log: one FMT for type 0xAA "TST" format "chL" labels "A,B,C"
        // then one TST record: c=-1234 (=-12.34), h=1000, L=473566000 (=47.3566)
        let mut data = vec![0xA3, 0x95, 0x80];
        let mut fmt = vec![0u8; 86];
        fmt[0] = 0xAA;
        fmt[1] = 3 + 2 + 2 + 4;
        fmt[2..5].copy_from_slice(b"TST");
        fmt[6..9].copy_from_slice(b"chL");
        fmt[22..27].copy_from_slice(b"A,B,C");
        data.extend_from_slice(&fmt);
        data.extend_from_slice(&[0xA3, 0x95, 0xAA]);
        data.extend_from_slice(&(-1234i16).to_le_bytes());
        data.extend_from_slice(&1000i16.to_le_bytes());
        data.extend_from_slice(&473566000i32.to_le_bytes());

        let dir = std::env::temp_dir().join(format!("dflog-col-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("tst.bin");
        std::fs::write(&path, &data).unwrap();

        let log = LogFile::open(&path).unwrap();
        let cols = get_columns(&log, "TST", &["A", "B", "C"]).unwrap();
        assert_eq!(cols.rows, 1);
        assert_eq!(cols.linenos, vec![1]);
        assert_eq!(cols.values, vec![-12.34, 1000.0, 47.3566]);

        drop(log);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
