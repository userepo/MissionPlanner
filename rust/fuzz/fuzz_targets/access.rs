#![no_main]

// The general access layer and everything built on it: typed record
// iteration, GPS time correlation, UNIT/MULT/FMTU metadata, and the
// instance-filtered column path (offset arithmetic driven by untrusted
// FMTU '#' positions). These surfaces postdate the phase-D campaign.

use dflog_core::{columns, LogFile};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Ok(log) = LogFile::open_bytes(data) {
        for record in log.records().take(256) {
            let _ = record.values();
        }
        let _ = log.record_at(data.len());
        let _ = log.time_base();
        let units = log.units();

        let names: Vec<String> = log.name_to_id.keys().cloned().collect();
        for name in names.iter().take(8) {
            let _ = log.instance_field(name);
            if let Some(fmt) = log.name_to_id.get(name).and_then(|id| log.fmts.get(id)) {
                if let Some(index) = units.instance_field_index(fmt.id) {
                    let _ = units.field_meta(fmt.id, index);
                }
                let labels: Vec<&str> = fmt.labels.iter().map(|s| s.as_str()).take(3).collect();
                if !labels.is_empty() {
                    for instance in [0i64, 1, -1] {
                        let _ = columns::get_columns_filtered(&log, name, &labels, Some(instance));
                        let _ = columns::get_array_column_filtered(&log, name, labels[0], Some(instance));
                    }
                }
            }
        }
    }
});
