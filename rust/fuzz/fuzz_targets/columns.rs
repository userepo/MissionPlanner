#![no_main]

use dflog_core::{columns, LogFile};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Ok(log) = LogFile::open_bytes(data) {
        let names: Vec<String> = log.name_to_id.keys().cloned().collect();
        for name in names.iter().take(8) {
            if let Some(fmt) = log.name_to_id.get(name).and_then(|id| log.fmts.get(id)) {
                let labels: Vec<&str> = fmt.labels.iter().map(|s| s.as_str()).take(3).collect();
                if !labels.is_empty() {
                    let _ = columns::get_columns(&log, name, &labels);
                    let _ = columns::get_array_column(&log, name, labels[0]);
                }
            }
        }
    }
});
