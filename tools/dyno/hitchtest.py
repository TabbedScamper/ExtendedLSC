#!/usr/bin/env python3
"""Bridge-driven hitch hunter: trigger ELSC actions and watch for frame-time spikes.

A real freeze blocks the game thread, so a GET_FRAME_TIME sampled right after an
action returns the freeze duration. We burst-sample after each action and report
the worst frames + how many crossed hitch thresholds.
"""
import time, socket, json, struct
import dyno

HOST, PORT = '127.0.0.1', 27015


def _ft():
    s = socket.socket(); s.settimeout(10); s.connect((HOST, PORT))
    try:
        msg = json.dumps({'command': 'call_native_by_name',
                          'params': {'name': 'GET_FRAME_TIME', 'args': [], 'return_type': 'float'}}).encode()
        s.sendall(struct.pack('<I', len(msg)) + msg)
        ln = struct.unpack('<I', s.recv(4))[0]; d = b''
        while len(d) < ln:
            d += s.recv(ln - len(d))
        return json.loads(d).get('result')
    finally:
        s.close()


def burst(dur):
    ms = []
    t0 = time.time()
    while time.time() - t0 < dur:
        ft = _ft()
        if isinstance(ft, (int, float)) and 0 < ft < 2.0:
            ms.append(ft * 1000.0)
    return ms


def report(label, ms):
    if not ms:
        print(f"{label:<28} NO SAMPLES"); return
    ms2 = sorted(ms)
    n = len(ms2)
    mean = sum(ms2) / n
    over25 = sum(1 for x in ms2 if x > 25)
    over50 = sum(1 for x in ms2 if x > 50)
    over100 = sum(1 for x in ms2 if x > 100)
    worst = ", ".join(f"{x:.0f}" for x in ms2[-3:])
    flag = "  <== HITCH" if over50 else ("  <- minor" if over25 else "")
    print(f"{label:<28} n={n:3d} mean={mean:5.2f} max={ms2[-1]:6.1f} "
          f">25ms:{over25} >50:{over50} >100:{over100} worst[{worst}]{flag}")


def act(label, fn, dur=3.0, settle=0.2):
    fn()
    time.sleep(settle)
    report(label, burst(dur))


print("=== ELSC hitch hunt ===")
report("idle baseline", burst(3.0))
act("OPEN menu (F5)",        lambda: dyno.call('send_keys', {'keys': 'F5'}))
act("nav down x12",          lambda: [dyno.call('send_keys', {'keys': 'Down'}) or time.sleep(0.12) for _ in range(12)] and None, dur=2.5)
act("enter category",        lambda: dyno.call('send_keys', {'keys': 'Enter'}))
act("nav in submenu x8",     lambda: [dyno.call('send_keys', {'keys': 'Down'}) or time.sleep(0.12) for _ in range(8)] and None, dur=2.0)
act("back out",              lambda: dyno.call('send_keys', {'keys': 'Back'}))
act("CLOSE menu (F5)",       lambda: dyno.call('send_keys', {'keys': 'F5'}))
print("done.")
