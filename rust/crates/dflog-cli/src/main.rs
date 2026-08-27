//! `dflog` - ArduPilot dataflash (.bin) log tool.
//!
//! Commands:
//!   dflog info <log.bin>                     summary: records, types, formats
//!   dflog dump <log.bin> <TYPE> <F1,F2,..>   numeric columns as CSV to stdout
//!   dflog convert <log.bin> <out.log>        text conversion, byte-compatible
//!                                            with Mission Planner's
//!                                            BinaryLog.ConvertBin (headless:
//!                                            M fields as mode numbers)
//!   dflog parquet <log.bin> <out-dir> [TYPES]  one parquet file per message
//!                                            type (builds with
//!                                            --features parquet)

use std::fs::File;
use std::io::{BufWriter, Write};
use std::path::Path;
use std::process::ExitCode;

use dflog_core::{columns, render, LogFile};

#[cfg(feature = "parquet")]
mod parquet_export;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.as_slice() {
        [cmd, log] if cmd == "info" => info(Path::new(log)),
        [cmd, log, type_name, fields] if cmd == "dump" => dump(Path::new(log), type_name, fields),
        [cmd, log, out] if cmd == "convert" => convert(Path::new(log), Path::new(out)),
        [cmd, rest @ ..] if cmd == "parquet" => parquet(rest),
        _ => {
            eprintln!("usage: dflog info <log.bin>");
            eprintln!("       dflog dump <log.bin> <TYPE> <FIELD1,FIELD2,...>");
            eprintln!("       dflog convert <log.bin> <out.log>");
            eprintln!("       dflog parquet <log.bin> <out-dir> [TYPE1,TYPE2,...]");
            return ExitCode::from(2);
        }
    };

    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(message) => {
            eprintln!("dflog: {message}");
            ExitCode::FAILURE
        }
    }
}

fn open(path: &Path) -> Result<LogFile, String> {
    LogFile::open(path).map_err(|e| format!("{}: {e}", path.display()))
}

#[cfg(feature = "parquet")]
fn parquet(args: &[String]) -> Result<(), String> {
    match args {
        [log, out_dir] => parquet_export::export(Path::new(log), Path::new(out_dir), None),
        [log, out_dir, types] => {
            parquet_export::export(Path::new(log), Path::new(out_dir), Some(types))
        }
        _ => Err("usage: dflog parquet <log.bin> <out-dir> [TYPE1,TYPE2,...]".into()),
    }
}

#[cfg(not(feature = "parquet"))]
fn parquet(_args: &[String]) -> Result<(), String> {
    Err("this build has no parquet support (rebuild with --features parquet)".into())
}

fn info(path: &Path) -> Result<(), String> {
    let log = open(path)?;

    let mut counts = [0u64; 256];
    for &t in &log.index.types {
        counts[t as usize] += 1;
    }

    println!("file: {}", path.display());
    println!("records: {}", log.index.len());
    println!();
    println!(
        "{:<6} {:<8} {:<6} {:<18} name",
        "id", "count", "len", "format"
    );

    let mut ids: Vec<u8> = log.fmts.keys().copied().collect();
    ids.sort_unstable();
    for id in ids {
        let fmt = &log.fmts[&id];
        if counts[id as usize] == 0 {
            continue;
        }
        println!(
            "{:<6} {:<8} {:<6} {:<18} {}",
            id, counts[id as usize], fmt.length, fmt.format, fmt.name
        );
    }

    let unknown: u64 = (0u16..256)
        .filter(|&i| !log.fmts.contains_key(&(i as u8)) && i as u8 != 0x80)
        .map(|i| counts[i as usize])
        .sum();
    if unknown > 0 {
        println!();
        println!("records of unknown type: {unknown}");
    }

    Ok(())
}

fn dump(path: &Path, type_name: &str, fields_csv: &str) -> Result<(), String> {
    let log = open(path)?;
    let fields: Vec<&str> = fields_csv.split(',').collect();

    let cols = columns::get_columns(&log, type_name, &fields).map_err(|e| e.to_string())?;

    let stdout = std::io::stdout();
    let mut out = BufWriter::new(stdout.lock());

    writeln!(out, "lineno,{}", fields.join(",")).map_err(|e| e.to_string())?;
    let rows = cols.rows as usize;
    for row in 0..rows {
        let mut line = cols.linenos[row].to_string();
        for col in 0..fields.len() {
            line.push(',');
            line.push_str(&render::format_significant(
                cols.values[col * rows + row],
                15,
            ));
        }
        writeln!(out, "{line}").map_err(|e| e.to_string())?;
    }

    Ok(())
}

fn convert(path: &Path, out_path: &Path) -> Result<(), String> {
    let file = File::open(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let len = file.metadata().map_err(|e| e.to_string())?.len() as usize;
    let data: memmap2::Mmap;
    let empty = [];
    let bytes: &[u8] = if len == 0 {
        &empty
    } else {
        // SAFETY: read-only mapping for the duration of the conversion
        data = unsafe { memmap2::Mmap::map(&file) }.map_err(|e| e.to_string())?;
        &data[..len]
    };

    let out_file = File::create(out_path).map_err(|e| format!("{}: {e}", out_path.display()))?;
    let mut out = BufWriter::new(out_file);
    let stats = render::convert(bytes, &mut out).map_err(|e| e.to_string())?;
    out.flush().map_err(|e| e.to_string())?;

    eprintln!("{} records, {} dropped", stats.records, stats.dropped);
    Ok(())
}
