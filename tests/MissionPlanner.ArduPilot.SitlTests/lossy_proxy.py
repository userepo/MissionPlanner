#!/usr/bin/env python3
"""TCP proxy that forwards MAVLink both ways but drops every Nth LOG_DATA
frame (msgid 120) travelling vehicle -> GCS. Listens on LISTEN_PORT and
connects to the SITL vehicle on VEHICLE_PORT."""
import socket
import sys
import threading

LISTEN_PORT = 5770
VEHICLE_PORT = 5760
DROP_EVERY = 20
LOG_DATA = 120


def pump_gcs_to_vehicle(gcs, veh):
    try:
        while True:
            d = gcs.recv(4096)
            if not d:
                break
            veh.sendall(d)
    except OSError:
        pass
    finally:
        try:
            veh.shutdown(socket.SHUT_WR)
        except OSError:
            pass


def pump_vehicle_to_gcs(veh, gcs):
    buf = b""
    seen = 0
    dropped = 0
    try:
        while True:
            d = veh.recv(4096)
            if not d:
                break
            buf += d
            out = b""
            while buf:
                b0 = buf[0]
                if b0 == 0xFD:  # MAVLink2: 10 byte hdr + payload + 2 crc (+13 sig)
                    if len(buf) < 10:
                        break
                    length = 12 + buf[1] + (13 if buf[2] & 0x01 else 0)
                    if len(buf) < length:
                        break
                    msgid = buf[7] | (buf[8] << 8) | (buf[9] << 16)
                elif b0 == 0xFE:  # MAVLink1: 6 byte hdr + payload + 2 crc
                    if len(buf) < 6:
                        break
                    length = 8 + buf[1]
                    if len(buf) < length:
                        break
                    msgid = buf[5]
                else:
                    out += buf[:1]
                    buf = buf[1:]
                    continue

                frame = buf[:length]
                buf = buf[length:]
                if msgid == LOG_DATA:
                    seen += 1
                    if seen % DROP_EVERY == 0:
                        dropped += 1
                        continue
                out += frame
            if out:
                gcs.sendall(out)
    except OSError:
        pass
    finally:
        print(f"[proxy] LOG_DATA seen={seen} dropped={dropped}", flush=True)
        try:
            gcs.shutdown(socket.SHUT_WR)
        except OSError:
            pass


def main():
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("0.0.0.0", LISTEN_PORT))
    srv.listen(1)
    print(f"[proxy] listening on {LISTEN_PORT}, dropping 1/{DROP_EVERY} LOG_DATA", flush=True)
    gcs, addr = srv.accept()
    print(f"[proxy] client {addr}", flush=True)
    veh = socket.create_connection(("127.0.0.1", VEHICLE_PORT))
    gcs.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    veh.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    t = threading.Thread(target=pump_gcs_to_vehicle, args=(gcs, veh), daemon=True)
    t.start()
    pump_vehicle_to_gcs(veh, gcs)
    t.join(timeout=2)


if __name__ == "__main__":
    sys.exit(main())
