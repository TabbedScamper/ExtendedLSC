#!/usr/bin/env python3
"""Continuous frame-time watcher. Samples GET_FRAME_TIME as fast as the bridge
allows for DUR seconds and prints any frame over a threshold with a wall-clock
offset, so spikes can be correlated with actions run concurrently."""
import time, socket, json, struct, sys

HOST, PORT = '127.0.0.1', 27015
DUR = float(sys.argv[1]) if len(sys.argv) > 1 else 40.0
THRESH = float(sys.argv[2]) if len(sys.argv) > 2 else 20.0


def _ft():
    s = socket.socket(); s.settimeout(10); s.connect((HOST, PORT))
    try:
        m = json.dumps({'command': 'call_native_by_name',
                        'params': {'name': 'GET_FRAME_TIME', 'args': [], 'return_type': 'float'}}).encode()
        s.sendall(struct.pack('<I', len(m)) + m)
        ln = struct.unpack('<I', s.recv(4))[0]; d = b''
        while len(d) < ln:
            d += s.recv(ln - len(d))
        return json.loads(d).get('result')
    finally:
        s.close()


t0 = time.time()
allms = []
spikes = []
while time.time() - t0 < DUR:
    ft = _ft()
    if isinstance(ft, (int, float)) and 0 < ft < 3.0:
        ms = ft * 1000.0
        allms.append(ms)
        if ms > THRESH:
            spikes.append((time.time() - t0, ms))
            print(f"  SPIKE @ t={time.time()-t0:5.1f}s : {ms:6.1f} ms ({1000/ms:.0f} fps)", flush=True)

allms.sort()
n = len(allms)
print(f"\n=== watch summary: {n} samples over {DUR:.0f}s ===")
if n:
    print(f"mean={sum(allms)/n:.2f}ms  median={allms[n//2]:.2f}  p99={allms[min(n-1,int(.99*n))]:.2f}  max={allms[-1]:.1f}")
    print(f"spikes >{THRESH:.0f}ms: {len(spikes)}  | worst5: {', '.join(f'{x:.0f}' for x in allms[-5:])}")
