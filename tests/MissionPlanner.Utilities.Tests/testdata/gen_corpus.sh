#!/bin/bash
# Generate small SITL dataflash logs for the phase-0 characterization corpus.
set -u
cd ~/sitl
printf "LOG_DISARMED 1\n" > extra.parm

drain_client() {
    python3 - <<'PY'
import socket, time
time.sleep(3)
try:
    s = socket.create_connection(("127.0.0.1", 5760), timeout=15)
    s.settimeout(20)
    end = time.time() + 14
    while time.time() < end:
        if not s.recv(4096):
            break
except Exception as e:
    print("client:", e)
PY
}

gen() {
    local name="$1" bin="$2" model="$3" parm="$4"
    echo "=== $name ==="
    mkdir -p ~/sitl/corpus/"$name"
    cd ~/sitl/corpus/"$name"
    rm -rf logs eeprom.bin
    drain_client &
    timeout 25 ~/sitl/"$bin" --model "$model" --speedup 1 -w --defaults "$parm" > boot.log 2>&1
    echo "sitl exit: $?"
    wait
    ls -la logs/ 2>/dev/null | tail -2 || tail -3 boot.log
    cd ~/sitl
}

gen copter arducopter +     /home/ivanc/sitl/copter.parm,/home/ivanc/sitl/extra.parm
gen plane  arduplane  plane /home/ivanc/sitl/extra.parm
gen rover  ardurover  rover /home/ivanc/sitl/rover.parm,/home/ivanc/sitl/extra.parm
