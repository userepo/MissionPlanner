#![no_main]

use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    let mut sink = std::io::sink();
    let _ = dflog_core::render::convert(data, &mut sink);
});
