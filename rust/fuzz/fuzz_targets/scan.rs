#![no_main]

use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    let index = dflog_core::scan(data);
    assert_eq!(index.offsets.len(), index.types.len());
});
