//! C ABI over dflog-core for Mission Planner's P/Invoke bindings
//! (ExtLibs/Utilities/DFLogNative.cs).
//!
//! Contract:
//! - Every function returns 0 on success or a negative error code; call
//!   `dflog_last_error` for a UTF-8 message describing the last failure on
//!   the calling thread.
//! - `dflog_scan_file` allocates a `DflogIndex`; release it with
//!   `dflog_index_free`. The `offsets`/`types` pointers stay valid until then.
//! - Panics never cross the boundary: they convert to `DFLOG_ERR_PANIC`.

use std::cell::RefCell;
use std::ffi::CStr;
use std::os::raw::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::PathBuf;
use std::ptr;

pub const DFLOG_OK: i32 = 0;
pub const DFLOG_ERR_BAD_ARGUMENT: i32 = -1;
pub const DFLOG_ERR_IO: i32 = -2;
pub const DFLOG_ERR_PANIC: i32 = -3;

/// Bumped when the ABI changes shape; checked by the C# side.
pub const DFLOG_ABI_VERSION: u32 = 1;

thread_local! {
    static LAST_ERROR: RefCell<String> = RefCell::new(String::new());
}

fn set_last_error(message: String) {
    LAST_ERROR.with(|slot| *slot.borrow_mut() = message);
}

#[repr(C)]
pub struct DflogIndex {
    pub count: u64,
    pub offsets: *const u64,
    pub types: *const u8,
    // owned storage the raw pointers refer to; not part of the C layout
    // contract - the C side must treat this struct as opaque beyond `types`
    offsets_vec: Vec<u64>,
    types_vec: Vec<u8>,
}

#[no_mangle]
pub extern "C" fn dflog_abi_version() -> u32 {
    DFLOG_ABI_VERSION
}

/// Scan the dataflash log at `path_utf8` and return its record index.
///
/// # Safety
/// `path_utf8` must be a valid NUL-terminated UTF-8 string and `out` a valid
/// pointer to receive the index.
#[no_mangle]
pub unsafe extern "C" fn dflog_scan_file(path_utf8: *const c_char, out: *mut *mut DflogIndex) -> i32 {
    if path_utf8.is_null() || out.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }
    *out = ptr::null_mut();

    let path = match CStr::from_ptr(path_utf8).to_str() {
        Ok(s) => PathBuf::from(s),
        Err(_) => {
            set_last_error("path is not valid UTF-8".into());
            return DFLOG_ERR_BAD_ARGUMENT;
        }
    };

    let result = catch_unwind(AssertUnwindSafe(|| dflog_core::scan_file(&path)));

    match result {
        Ok(Ok(index)) => {
            let mut boxed = Box::new(DflogIndex {
                count: index.offsets.len() as u64,
                offsets: ptr::null(),
                types: ptr::null(),
                offsets_vec: index.offsets,
                types_vec: index.types,
            });
            boxed.offsets = boxed.offsets_vec.as_ptr();
            boxed.types = boxed.types_vec.as_ptr();
            *out = Box::into_raw(boxed);
            DFLOG_OK
        }
        Ok(Err(err)) => {
            set_last_error(format!("{}: {}", path.display(), err));
            DFLOG_ERR_IO
        }
        Err(_) => {
            set_last_error("panic in dflog_scan_file".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Release an index returned by `dflog_scan_file`.
///
/// # Safety
/// `index` must be a pointer previously returned via `dflog_scan_file`, and
/// must not be used after this call. Null is ignored.
#[no_mangle]
pub unsafe extern "C" fn dflog_index_free(index: *mut DflogIndex) {
    if !index.is_null() {
        drop(Box::from_raw(index));
    }
}

/// Copy the calling thread's last error message (UTF-8, NUL-terminated) into
/// `buf`. Returns the number of bytes written excluding the NUL, or the
/// required capacity as a negative number when `cap` is too small.
///
/// # Safety
/// `buf` must point to at least `cap` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn dflog_last_error(buf: *mut c_char, cap: usize) -> i32 {
    if buf.is_null() || cap == 0 {
        return DFLOG_ERR_BAD_ARGUMENT;
    }

    LAST_ERROR.with(|slot| {
        let message = slot.borrow();
        let bytes = message.as_bytes();
        if bytes.len() + 1 > cap {
            return -(bytes.len() as i32 + 1);
        }
        ptr::copy_nonoverlapping(bytes.as_ptr(), buf as *mut u8, bytes.len());
        *buf.add(bytes.len()) = 0;
        bytes.len() as i32
    })
}
