@echo off
rem Rebuild the Rust workspace and refresh the checked-in native library the
rem app ships (ExtLibs\Utilities\dflog_ffi.dll). Run after any change under
rem rust\ and commit the updated DLL - the AbiVersionMatchesCheckedInDll unit
rem test fails when the checked-in copy lags the sources' ABI.
setlocal
cd /d "%~dp0"
cargo build --release || exit /b 1
copy /y target\release\dflog_ffi.dll ..\ExtLibs\Utilities\dflog_ffi.dll || exit /b 1
echo refreshed ExtLibs\Utilities\dflog_ffi.dll
