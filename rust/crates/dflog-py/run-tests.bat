@echo off
rem Build the dflog Python extension module and run its functional tests.
rem Needs a Python 3.9+ with numpy on PATH (or set DFLOG_PY=path\to\python).
rem The abi3 cdylib is staged as dflog.pyd in a scratch dir on sys.path -
rem no pip install, no maturin needed for local testing.
setlocal
cd /d "%~dp0"
if "%DFLOG_PY%"=="" set DFLOG_PY=python

cargo build --release -p dflog-py || exit /b 1
if not exist "%TEMP%\dflog-py-test" mkdir "%TEMP%\dflog-py-test"
copy /y ..\..\target\release\dflog.dll "%TEMP%\dflog-py-test\dflog.pyd" >nul || exit /b 1

set PYTHONPATH=%TEMP%\dflog-py-test
"%DFLOG_PY%" tests\test_dflog.py
