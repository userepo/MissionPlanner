//! Text rendering of a dataflash log, byte-compatible with the C#
//! `BinaryLog.ConvertBin` (phase C of docs/dflog-rust-core-plan.md).
//!
//! The reference is `BinaryLog.ReadMessage` running headless (no
//! `onFlightMode` subscriber, so `M` fields render as the raw mode number -
//! inside the full app the legacy path substitutes mode names, a documented
//! divergence). Every quirk here is intentional parity:
//!
//! - one pass with the same resync state machine as the index scan, learning
//!   FMT progressively; records of a type whose FMT has not been seen yet
//!   produce no output and are rescanned mid-payload
//! - numbers format like .NET's `IConvertible.ToString(InvariantCulture)`:
//!   integers plainly; float via legacy "G7", double (and the c/C/e/E/L
//!   scaled fields) via legacy "G15" - fixed notation unless the decimal
//!   exponent is < -4 or >= the precision, then `d.dddE+XX`
//! - `n`/`N` strings map non-ASCII bytes to '?' (ASCIIEncoding) and trim NULs
//! - `Z` additionally escapes backslash, \n, \r, \t and control chars as \xHH
//! - `a` renders as "[s0 s1 ... s31]"
//! - an unknown format char contributes an empty field and advances zero
//!   bytes, exactly like the C# `(null, 0)` decoder result

use std::io::{self, Write};

use crate::{FMT_PAYLOAD_LEN, FMT_TYPE, HEAD_BYTE1, HEAD_BYTE2};

pub struct RenderStats {
    pub records: u64,
    pub dropped: u64,
}

fn field_size(code: u8) -> usize {
    match code {
        b'b' | b'B' | b'M' => 1,
        b'h' | b'H' | b'c' | b'C' | b'g' => 2,
        b'i' | b'I' | b'e' | b'E' | b'L' | b'f' => 4,
        b'q' | b'Q' | b'd' => 8,
        b'n' => 4,
        b'N' => 16,
        b'Z' => 64,
        b'a' => 64,
        _ => 0,
    }
}

/// legacy .NET Framework "G" format with `prec` significant digits (G7 for
/// float, G15 for double), invariant culture. The Framework rounds the exact
/// decimal expansion half AWAY FROM ZERO at the cutoff - unlike a shortest
/// correctly-rounded conversion, which rounds ties to even. Exact decimal
/// midpoints such as the float 94502.125 therefore format as 94502.13. The
/// 60-digit intermediate below leaves enough margin that a simple >=5 test
/// on the cutoff digit is exact for every double (a non-tie double cannot
/// sit within 1e-40 of a decimal midpoint).
pub fn dotnet_g(value: f64, prec: usize) -> String {
    if value.is_nan() {
        return "NaN".into();
    }
    if value.is_infinite() {
        return if value > 0.0 { "Infinity".into() } else { "-Infinity".into() };
    }
    if value == 0.0 {
        return "0".into();
    }

    // 60 significant digits, correctly rounded
    let formatted = format!("{:.59e}", value);
    let (mantissa, exp_str) = formatted.split_once('e').expect("exponent");
    let mut exponent: i32 = exp_str.parse().expect("exponent value");

    let negative = mantissa.starts_with('-');
    let mut digits: Vec<u8> = mantissa
        .bytes()
        .filter(|b| b.is_ascii_digit())
        .map(|b| b - b'0')
        .collect();

    // round the digit string to `prec` digits, half away from zero
    if digits.len() > prec {
        let round_up = digits[prec] >= 5;
        digits.truncate(prec);
        if round_up {
            let mut i = prec;
            loop {
                if i == 0 {
                    digits.insert(0, 1);
                    digits.truncate(prec);
                    exponent += 1;
                    break;
                }
                i -= 1;
                if digits[i] == 9 {
                    digits[i] = 0;
                } else {
                    digits[i] += 1;
                    break;
                }
            }
        }
    }

    while digits.len() > 1 && *digits.last().unwrap() == 0 {
        digits.pop();
    }

    let digits: String = digits.iter().map(|&d| (d + b'0') as char).collect();

    let mut out = String::new();
    if negative {
        out.push('-');
    }

    if exponent < -4 || exponent >= prec as i32 {
        // scientific: d.dddE+XX (at least two exponent digits)
        out.push_str(&digits[..1]);
        if digits.len() > 1 {
            out.push('.');
            out.push_str(&digits[1..]);
        }
        out.push('E');
        if exponent < 0 {
            out.push('-');
        } else {
            out.push('+');
        }
        out.push_str(&format!("{:02}", exponent.abs()));
    } else if exponent >= 0 {
        let int_len = exponent as usize + 1;
        if digits.len() <= int_len {
            out.push_str(&digits);
            out.push_str(&"0".repeat(int_len - digits.len()));
        } else {
            out.push_str(&digits[..int_len]);
            out.push('.');
            out.push_str(&digits[int_len..]);
        }
    } else {
        out.push_str("0.");
        out.push_str(&"0".repeat((-exponent - 1) as usize));
        out.push_str(&digits);
    }

    out
}

/// the C# float path: widen to double (exact), then legacy G7
fn dotnet_g7_f32(value: f32) -> String {
    dotnet_g(value as f64, 7)
}

/// ASCIIEncoding semantics: bytes > 0x7F become '?', then trim NULs
fn ascii_lossy_trim(bytes: &[u8]) -> String {
    let mapped: String = bytes
        .iter()
        .map(|&b| if b > 0x7F { '?' } else { b as char })
        .collect();
    mapped.trim_matches('\0').to_string()
}

/// None when the trimmed string is empty: the C# escape chain ends in a
/// seedless LINQ Aggregate, which throws on an empty sequence and drops the
/// whole record line
fn escape_z(bytes: &[u8]) -> Option<String> {
    let s = ascii_lossy_trim(bytes);
    let s = s.replace('\\', "\\\\");
    let s = s.replace('\n', "\\n");
    let s = s.replace('\r', "\\r");
    let s = s.replace('\t', "\\t");
    if s.is_empty() {
        return None;
    }
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        if (c as u32) < 32 || (c as u32) > 127 {
            out.push_str(&format!("\\x{:02X}", c as u32 as u8));
        } else {
            out.push(c);
        }
    }
    Some(out)
}

fn zero_padded(payload: &[u8], at: usize, len: usize) -> Vec<u8> {
    let mut buf = vec![0u8; len];
    if at < payload.len() {
        let take = len.min(payload.len() - at);
        buf[..take].copy_from_slice(&payload[at..at + take]);
    }
    buf
}

/// false = the whole record line must be dropped (empty-Z quirk)
fn render_field(code: u8, payload: &[u8], at: usize, out: &mut String) -> bool {
    let b = zero_padded(payload, at, field_size(code).max(1).min(64));
    match code {
        b'b' => out.push_str(&(b[0] as i8).to_string()),
        b'B' | b'M' => out.push_str(&b[0].to_string()),
        b'h' => out.push_str(&i16::from_le_bytes([b[0], b[1]]).to_string()),
        b'H' => out.push_str(&u16::from_le_bytes([b[0], b[1]]).to_string()),
        b'i' => out.push_str(&i32::from_le_bytes([b[0], b[1], b[2], b[3]]).to_string()),
        b'I' => out.push_str(&u32::from_le_bytes([b[0], b[1], b[2], b[3]]).to_string()),
        b'q' => out.push_str(&i64::from_le_bytes(b[..8].try_into().unwrap()).to_string()),
        b'Q' => out.push_str(&u64::from_le_bytes(b[..8].try_into().unwrap()).to_string()),
        b'f' => out.push_str(&dotnet_g7_f32(f32::from_le_bytes([b[0], b[1], b[2], b[3]]))),
        b'd' => out.push_str(&dotnet_g(f64::from_le_bytes(b[..8].try_into().unwrap()), 15)),
        b'g' => out.push_str(&dotnet_g7_f32(crate::columns::half_to_f32(u16::from_le_bytes([b[0], b[1]])))),
        b'c' => out.push_str(&dotnet_g(i16::from_le_bytes([b[0], b[1]]) as f64 / 100.0, 15)),
        b'C' => out.push_str(&dotnet_g(u16::from_le_bytes([b[0], b[1]]) as f64 / 100.0, 15)),
        b'e' => out.push_str(&dotnet_g(i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 100.0, 15)),
        b'E' => out.push_str(&dotnet_g(u32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 100.0, 15)),
        b'L' => out.push_str(&dotnet_g(i32::from_le_bytes([b[0], b[1], b[2], b[3]]) as f64 / 10000000.0, 15)),
        b'n' => out.push_str(&ascii_lossy_trim(&zero_padded(payload, at, 4))),
        b'N' => out.push_str(&ascii_lossy_trim(&zero_padded(payload, at, 16))),
        b'Z' => match escape_z(&zero_padded(payload, at, 64)) {
            Some(s) => out.push_str(&s),
            None => return false,
        },
        b'a' => {
            out.push('[');
            let bytes = zero_padded(payload, at, 64);
            for (i, pair) in bytes.chunks_exact(2).enumerate() {
                if i > 0 {
                    out.push(' ');
                }
                out.push_str(&i16::from_le_bytes([pair[0], pair[1]]).to_string());
            }
            out.push(']');
        }
        _ => {} // unknown code: empty field, zero bytes consumed
    }

    true
}

#[derive(Clone, Default)]
struct FmtEntry {
    length: usize,
    name: String,
    format: Vec<u8>,
}

/// One-pass conversion of a binary log to the ConvertBin text form.
pub fn convert<W: Write>(data: &[u8], out: &mut W) -> io::Result<RenderStats> {
    let mut fmts: Vec<FmtEntry> = vec![FmtEntry::default(); 256];
    let mut stats = RenderStats { records: 0, dropped: 0 };
    let len = data.len();
    let mut pos = 0usize;
    let mut line = String::new();

    let mut step = 0u8;
    while pos < len {
        let b = data[pos];
        pos += 1;
        match step {
            0 => {
                if b == HEAD_BYTE1 {
                    step = 1;
                }
            }
            1 => {
                step = if b == HEAD_BYTE2 { 2 } else { 0 };
            }
            _ => {
                step = 0;
                if b == FMT_TYPE {
                    let take = FMT_PAYLOAD_LEN.min(len - pos);
                    let mut payload = [0u8; FMT_PAYLOAD_LEN];
                    payload[..take].copy_from_slice(&data[pos..pos + take]);
                    pos += take;

                    let entry = FmtEntry {
                        length: payload[1] as usize,
                        name: ascii_lossy_trim(&payload[2..6]),
                        format: ascii_lossy_trim(&payload[6..22]).into_bytes(),
                    };
                    fmts[payload[0] as usize] = entry;

                    line.clear();
                    line.push_str("FMT, ");
                    line.push_str(&payload[0].to_string());
                    line.push_str(", ");
                    line.push_str(&payload[1].to_string());
                    line.push_str(", ");
                    line.push_str(&ascii_lossy_trim(&payload[2..6]));
                    line.push_str(", ");
                    line.push_str(&ascii_lossy_trim(&payload[6..22]));
                    line.push_str(", ");
                    line.push_str(&ascii_lossy_trim(&payload[22..86]));
                    line.push_str("\r\n");
                    out.write_all(line.as_bytes())?;
                    stats.records += 1;
                } else {
                    let fmt = &fmts[b as usize];
                    if fmt.length == 0 {
                        // unknown type: no output, rescan inside its payload
                        stats.dropped += 1;
                        continue;
                    }
                    if fmt.length < 3 {
                        // C# throws on new byte[size - 3]: record dropped
                        stats.dropped += 1;
                        continue;
                    }

                    let size = fmt.length - 3;
                    let take = size.min(len - pos);
                    let payload = &data[pos..pos + take];
                    pos += take;

                    line.clear();
                    line.push_str(&fmt.name);
                    let mut at = 0usize;
                    let mut ok = true;
                    for &code in &fmt.format {
                        line.push_str(", ");
                        if !render_field(code, payload, at, &mut line) {
                            ok = false;
                            break;
                        }
                        at += field_size(code);
                    }

                    if ok {
                        line.push_str("\r\n");
                        out.write_all(line.as_bytes())?;
                        stats.records += 1;
                    } else {
                        stats.dropped += 1;
                    }
                }
            }
        }
    }

    Ok(stats)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn g_format_basics() {
        assert_eq!(dotnet_g(0.0, 15), "0");
        assert_eq!(dotnet_g(1.0, 15), "1");
        assert_eq!(dotnet_g(-12.34, 15), "-12.34");
        assert_eq!(dotnet_g(0.1, 15), "0.1");
        assert_eq!(dotnet_g(1234567890123456.0, 15), "1.23456789012346E+15");
        assert_eq!(dotnet_g(0.00001, 15), "1E-05");
        assert_eq!(dotnet_g(47.3566, 15), "47.3566");
        assert_eq!(dotnet_g(f64::NAN, 15), "NaN");
        assert_eq!(dotnet_g(-0.0, 15), "0");
    }

    #[test]
    fn g7_float_basics() {
        assert_eq!(dotnet_g7_f32(0.0), "0");
        assert_eq!(dotnet_g7_f32(1.5), "1.5");
        assert_eq!(dotnet_g7_f32(1.2345678), "1.234568");
        assert_eq!(dotnet_g7_f32(12345678.0), "1.234568E+07");
        assert_eq!(dotnet_g7_f32(-0.001), "-0.001");
        assert_eq!(dotnet_g7_f32(0.00001), "1E-05");
        // exact decimal midpoint: legacy rounds half away from zero
        assert_eq!(dotnet_g7_f32(94502.125), "94502.13");
        assert_eq!(dotnet_g7_f32(-94502.125), "-94502.13");
    }

    #[test]
    fn z_escaping() {
        let mut bytes = [0u8; 64];
        bytes[..7].copy_from_slice(b"a\\b\nc\td");
        assert_eq!(escape_z(&bytes).as_deref(), Some("a\\\\b\\nc\\td"));
        let mut ctrl = [0u8; 64];
        ctrl[0] = b'x';
        ctrl[1] = 0x1B;
        ctrl[2] = b'y';
        assert_eq!(escape_z(&ctrl).as_deref(), Some("x\\x1By"));
        // empty after trim: the record line is dropped (C# Aggregate quirk)
        assert_eq!(escape_z(&[0u8; 64]), None);
    }
}
