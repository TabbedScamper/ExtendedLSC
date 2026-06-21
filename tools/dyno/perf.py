#!/usr/bin/env python3
"""Bridge-driven frame-time profiler for ELSC.

Samples GET_FRAME_TIME densely over a window and reports the frame-time
distribution (mean/median/p95/p99/max in ms) + FPS, so we can compare
states (menu closed/open, walk-around, MT on/off) and catch periodic
spikes from the throttled world sweeps.
"""
import socket, json, struct, time, statistics, sys

HOST, PORT = '127.0.0.1', 27015


def _req(sock, cmd, params):
    msg = json.dumps({'command': cmd, 'params': params or {}}).encode()
    sock.sendall(struct.pack('<I', len(msg)) + msg)
    ln = struct.unpack('<I', sock.recv(4))[0]
    data = b''
    while len(data) < ln:
        data += sock.recv(ln - len(data))
    return json.loads(data)


def _ft(sock):
    r = _req(sock, 'call_native_by_name',
             {'name': 'GET_FRAME_TIME', 'args': [], 'return_type': 'float'})
    return r.get('result')


def _persistent_ok():
    """Does the bridge allow >1 request per connection?"""
    try:
        s = socket.socket(); s.settimeout(5); s.connect((HOST, PORT))
        _ft(s); _ft(s); s.close()
        return True
    except Exception:
        return False


def sample(label, dur=4.0):
    persistent = _persistent_ok()
    ms = []
    t0 = time.time()
    s = None
    if persistent:
        s = socket.socket(); s.settimeout(8); s.connect((HOST, PORT))
    while time.time() - t0 < dur:
        try:
            if persistent:
                ft = _ft(s)
            else:
                s2 = socket.socket(); s2.settimeout(8); s2.connect((HOST, PORT))
                ft = _ft(s2); s2.close()
            if isinstance(ft, (int, float)) and 0 < ft < 1.0:
                ms.append(ft * 1000.0)
        except Exception:
            time.sleep(0.02)
    if s:
        s.close()
    if not ms:
        print(f"{label}: NO SAMPLES")
        return None
    ms.sort()
    n = len(ms)
    def pct(p): return ms[min(n - 1, int(p * n))]
    mean = statistics.mean(ms)
    out = (f"{label:<22} n={n:4d} conn={'reuse' if persistent else 'percall'} | "
           f"ms mean={mean:5.2f} med={statistics.median(ms):5.2f} "
           f"p95={pct(0.95):5.2f} p99={pct(0.99):5.2f} max={ms[-1]:6.2f} | "
           f"FPS mean={1000/mean:5.1f} low1%={1000/pct(0.99):5.1f}")
    print(out)
    return {'label': label, 'n': n, 'mean': mean, 'p95': pct(0.95),
            'p99': pct(0.99), 'max': ms[-1]}


if __name__ == '__main__':
    label = sys.argv[1] if len(sys.argv) > 1 else 'state'
    dur = float(sys.argv[2]) if len(sys.argv) > 2 else 4.0
    sample(label, dur)
