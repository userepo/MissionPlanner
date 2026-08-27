//! Python bindings for dflog-core: a fast, columnar-first reader for
//! ArduPilot dataflash `.bin` logs, with a DFReader-style message iterator
//! for migration. Values decode exactly as the general access layer does
//! (legacy scaling on c/C/e/E/L, plain trimmed strings, raw mode numbers);
//! units and multipliers are exposed as metadata, never applied.

use std::collections::HashMap;
use std::sync::OnceLock;

use pyo3::exceptions::{PyIOError, PyKeyError, PyValueError};
use pyo3::prelude::*;
use pyo3::types::PyDict;
use pyo3::IntoPyObjectExt;

use numpy::prelude::*;

use dflog_core::access::Value;
use dflog_core::columns::{self, ColumnError};
use dflog_core::units::UnitsTable;

fn column_err(e: ColumnError) -> PyErr {
    match &e {
        ColumnError::UnknownType(_) | ColumnError::UnknownField { .. } => {
            PyKeyError::new_err(e.to_string())
        }
        _ => PyValueError::new_err(e.to_string()),
    }
}

fn value_to_py(py: Python<'_>, value: &Value) -> PyResult<Py<PyAny>> {
    match value {
        Value::I64(v) => v.into_py_any(py),
        Value::U64(v) => v.into_py_any(py),
        Value::F64(v) => v.into_py_any(py),
        Value::Str(v) => v.into_py_any(py),
        Value::Shorts(v) => v.clone().into_py_any(py),
    }
}

/// (lineno array, rows x 32 samples array)
type ArrayColumnResult<'py> = (
    Bound<'py, numpy::PyArray1<i64>>,
    Bound<'py, numpy::PyArray2<i16>>,
);

/// An opened, indexed dataflash log.
#[pyclass(frozen, module = "dflog")]
struct LogFile {
    inner: dflog_core::LogFile,
    units: OnceLock<UnitsTable>,
}

impl LogFile {
    fn units_table(&self) -> &UnitsTable {
        self.units.get_or_init(|| self.inner.units())
    }

    fn format_dict<'py>(&self, py: Python<'py>, name: &str) -> PyResult<Bound<'py, PyDict>> {
        let &id = self
            .inner
            .name_to_id
            .get(name)
            .ok_or_else(|| PyKeyError::new_err(format!("unknown message type: {name}")))?;
        let fmt = &self.inner.fmts[&id];
        let units = self.units_table();

        let fields = pyo3::types::PyList::empty(py);
        let codes = fmt.format.as_bytes();
        for (i, label) in fmt.labels.iter().enumerate() {
            let meta = units.field_meta(id, i);
            let field = PyDict::new(py);
            field.set_item("name", label)?;
            field.set_item("type", codes.get(i).map(|&c| (c as char).to_string()))?;
            field.set_item("unit", meta.unit)?;
            field.set_item("multiplier", meta.multiplier)?;
            fields.append(field)?;
        }

        let d = PyDict::new(py);
        d.set_item("name", &fmt.name)?;
        d.set_item("id", id)?;
        d.set_item("length", fmt.length)?;
        d.set_item("format", &fmt.format)?;
        d.set_item("fields", fields)?;
        Ok(d)
    }
}

#[pymethods]
impl LogFile {
    #[new]
    fn new(path: std::path::PathBuf) -> PyResult<Self> {
        let inner = dflog_core::LogFile::open(&path)
            .map_err(|e| PyIOError::new_err(format!("{}: {e}", path.display())))?;
        Ok(LogFile {
            inner,
            units: OnceLock::new(),
        })
    }

    /// Open a log already held in memory.
    #[staticmethod]
    fn from_bytes(data: Vec<u8>) -> PyResult<Self> {
        let inner = dflog_core::LogFile::open_bytes(&data)
            .map_err(|e| PyIOError::new_err(e.to_string()))?;
        Ok(LogFile {
            inner,
            units: OnceLock::new(),
        })
    }

    /// Message counts by type name, e.g. {"ATT": 196, "GPS": 62}.
    #[getter]
    fn types(&self) -> HashMap<String, u64> {
        let mut counts = [0u64; 256];
        for &t in &self.inner.index.types {
            counts[t as usize] += 1;
        }
        let mut out = HashMap::new();
        for (id, fmt) in &self.inner.fmts {
            let n = counts[*id as usize];
            if n > 0 {
                *out.entry(fmt.name.clone()).or_insert(0) += n;
            }
        }
        out
    }

    /// Format description of one message type: fields with format chars
    /// and units/multiplier metadata.
    fn format<'py>(&self, py: Python<'py>, name: &str) -> PyResult<Bound<'py, PyDict>> {
        self.format_dict(py, name)
    }

    /// All format descriptions, keyed by message name.
    fn formats<'py>(&self, py: Python<'py>) -> PyResult<Bound<'py, PyDict>> {
        let d = PyDict::new(py);
        for name in self.inner.name_to_id.keys() {
            d.set_item(name, self.format_dict(py, name)?)?;
        }
        Ok(d)
    }

    /// Columnar decode: numpy float64 array per requested field, plus the
    /// "lineno" int64 array of global record indexes.
    fn columns<'py>(
        &self,
        py: Python<'py>,
        type_name: &str,
        fields: Vec<String>,
    ) -> PyResult<Bound<'py, PyDict>> {
        let refs: Vec<&str> = fields.iter().map(String::as_str).collect();
        let cols = columns::get_columns(&self.inner, type_name, &refs).map_err(column_err)?;

        let rows = cols.rows as usize;
        let out = PyDict::new(py);
        let linenos: Vec<i64> = cols.linenos.iter().map(|&v| v as i64).collect();
        out.set_item("lineno", numpy::PyArray1::from_vec(py, linenos))?;
        for (i, field) in fields.iter().enumerate() {
            let column = &cols.values[i * rows..(i + 1) * rows];
            out.set_item(field, numpy::PyArray1::from_slice(py, column))?;
        }
        Ok(out)
    }

    /// Decode an `a` (int16[32]) field into a rows x 32 numpy int16 array;
    /// returns (lineno array, samples array).
    fn array_column<'py>(
        &self,
        py: Python<'py>,
        type_name: &str,
        field: &str,
    ) -> PyResult<ArrayColumnResult<'py>> {
        let col = columns::get_array_column(&self.inner, type_name, field).map_err(column_err)?;
        let rows = col.rows as usize;
        let elems = col.values.len().checked_div(rows).unwrap_or(32);

        let linenos: Vec<i64> = col.linenos.iter().map(|&v| v as i64).collect();
        let samples = numpy::PyArray1::from_vec(py, col.values)
            .reshape([rows, elems])
            .map_err(|e| PyValueError::new_err(e.to_string()))?;
        Ok((numpy::PyArray1::from_vec(py, linenos), samples))
    }

    /// Iterate messages in log order, optionally limited to some types.
    #[pyo3(signature = (types=None))]
    fn messages(slf: Py<Self>, types: Option<Vec<String>>) -> MessagesIter {
        let filter = types.map(|names| {
            let log = slf.get();
            let mut filter = [false; 256];
            for name in names {
                if let Some(&id) = log.inner.name_to_id.get(&name) {
                    filter[id as usize] = true;
                }
            }
            filter
        });
        MessagesIter {
            log: slf,
            pos: 0,
            filter,
        }
    }

    /// GPS wall-clock correlation, or None when the log has no usable fix.
    fn time_base(&self) -> Option<TimeBase> {
        self.inner.time_base().map(|b| TimeBase {
            gps_start_unix_ms: b.gps_start_unix_ms,
            ms_offset: b.ms_offset,
        })
    }

    /// Number of indexed records (including types without a FMT).
    fn __len__(&self) -> usize {
        self.inner.index.len()
    }

    fn __repr__(&self) -> String {
        format!("LogFile({} records)", self.inner.index.len())
    }
}

/// Iterator over decoded messages; each item owns its data.
#[pyclass(module = "dflog")]
struct MessagesIter {
    log: Py<LogFile>,
    pos: usize,
    filter: Option<[bool; 256]>,
}

#[pymethods]
impl MessagesIter {
    fn __iter__(slf: PyRef<'_, Self>) -> PyRef<'_, Self> {
        slf
    }

    fn __next__(&mut self, py: Python<'_>) -> PyResult<Option<Message>> {
        let log = self.log.get();
        while self.pos < log.inner.index.types.len() {
            let i = self.pos;
            self.pos += 1;
            if let Some(filter) = &self.filter {
                if !filter[log.inner.index.types[i] as usize] {
                    continue;
                }
            }
            let Some(record) = log.inner.record_at(i) else {
                continue;
            };

            let data = PyDict::new(py);
            let mut time_us = None;
            for (label, value) in record.values() {
                if label == "TimeUS" {
                    time_us = value.as_f64();
                }
                data.set_item(label, value_to_py(py, &value)?)?;
            }
            return Ok(Some(Message {
                type_name: record.type_name().to_string(),
                lineno: record.lineno,
                time_us,
                data: data.unbind(),
            }));
        }
        Ok(None)
    }
}

/// One decoded log message: field access by name, plus type/lineno/time_us.
#[pyclass(frozen, module = "dflog")]
struct Message {
    type_name: String,
    #[pyo3(get)]
    lineno: u64,
    /// TimeUS in microseconds, when the message has that field
    #[pyo3(get)]
    time_us: Option<f64>,
    data: Py<PyDict>,
}

#[pymethods]
impl Message {
    #[getter]
    #[pyo3(name = "type")]
    fn type_name(&self) -> &str {
        &self.type_name
    }

    fn __getitem__(&self, py: Python<'_>, key: &str) -> PyResult<Py<PyAny>> {
        match self.data.bind(py).get_item(key)? {
            Some(v) => Ok(v.unbind()),
            None => Err(PyKeyError::new_err(key.to_string())),
        }
    }

    fn __contains__(&self, py: Python<'_>, key: &str) -> PyResult<bool> {
        self.data.bind(py).contains(key)
    }

    fn keys(&self, py: Python<'_>) -> PyResult<Py<PyAny>> {
        self.data.bind(py).keys().into_py_any(py)
    }

    /// Field values as a plain dict.
    fn to_dict(&self, py: Python<'_>) -> Py<PyDict> {
        self.data.clone_ref(py)
    }

    fn __repr__(&self, py: Python<'_>) -> String {
        format!(
            "Message({}, lineno={}, {})",
            self.type_name,
            self.lineno,
            self.data.bind(py)
        )
    }
}

/// Wall-clock correlation from the log's first valid GPS fix.
#[pyclass(frozen, module = "dflog")]
struct TimeBase {
    /// UTC unix time in milliseconds at `ms_offset` board time
    #[pyo3(get)]
    gps_start_unix_ms: i64,
    /// board time (ms) the GPS start corresponds to
    #[pyo3(get)]
    ms_offset: i64,
}

#[pymethods]
impl TimeBase {
    /// Map a board time in milliseconds to UTC unix time in milliseconds.
    fn wall_clock_unix_ms(&self, board_ms: f64) -> f64 {
        self.gps_start_unix_ms as f64 + (board_ms - self.ms_offset as f64)
    }

    fn __repr__(&self) -> String {
        format!(
            "TimeBase(gps_start_unix_ms={}, ms_offset={})",
            self.gps_start_unix_ms, self.ms_offset
        )
    }
}

#[pymodule]
fn dflog(m: &Bound<'_, PyModule>) -> PyResult<()> {
    m.add_class::<LogFile>()?;
    m.add_class::<MessagesIter>()?;
    m.add_class::<Message>()?;
    m.add_class::<TimeBase>()?;
    m.add("__version__", env!("CARGO_PKG_VERSION"))?;
    Ok(())
}
