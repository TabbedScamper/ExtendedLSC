#!/usr/bin/env python3
"""ELSC runway dyno automation.

Usage:
  python dyno.py current [secs] [label]      # dyno the car the player is already in
  python dyno.py <model> [secs] [label]      # spawn <model>, seat player, dyno it

Triggers the in-game autorun harness (writes autorun.txt), waits for run_telemetry.csv
to be rewritten, then parses it into a summary and saves a labeled copy under results/.
"""
import socket, json, struct, time, sys, os, math, glob

HOST, PORT = '127.0.0.1', 27015
GAME_DIR = r'C:/Program Files/Rockstar Games/Grand Theft Auto V Legacy/scripts/ExtendedLSC'
CSV = os.path.join(GAME_DIR, 'run_telemetry.csv')
TRIGGER = os.path.join(GAME_DIR, 'autorun.txt')
HERE = os.path.dirname(os.path.abspath(__file__))
RESULTS = os.path.join(HERE, 'results')
os.makedirs(RESULTS, exist_ok=True)

START = (1719.2906494140625, 3254.599853515625, 40.75857925415039, 104.86547088623047)
MS2MPH = 2.2369362921


def call(cmd, params=None, timeout=8):
    s = socket.socket(); s.settimeout(timeout); s.connect((HOST, PORT))
    msg = json.dumps({'command': cmd, 'params': params or {}}).encode()
    s.sendall(struct.pack('<I', len(msg)) + msg)
    ln = struct.unpack('<I', s.recv(4))[0]; data = b''
    while len(data) < ln:
        data += s.recv(ln - len(data))
    s.close(); return json.loads(data)


def nat(name, args, rt='void'):
    r = call('call_native_by_name', {'name': name, 'args': args, 'return_type': rt})
    return r.get('result', r)


def ped():
    return nat('PLAYER_PED_ID', [], 'int')


def cur_veh():
    v = nat('GET_VEHICLE_PED_IS_IN', [ped(), False], 'int')
    return v if isinstance(v, int) and v != 0 else 0


def spawn(model):
    """Spawn at the player's CURRENT position (collision already streamed) and seat them.
    Never uses pointer-arg natives (DELETE_VEHICLE / *_AS_NO_LONGER_NEEDED) — passing a
    handle where the bridge expects a pointer corrupts memory and crashes the game.
    Old cars are left to despawn naturally."""
    p = ped()
    pos = nat('GET_ENTITY_COORDS', [p, True], 'vector3')
    h = nat('GET_HASH_KEY', [model], 'int')
    nat('REQUEST_MODEL', [h])
    loaded = False
    for _ in range(60):
        if nat('HAS_MODEL_LOADED', [h], 'bool') is True:
            loaded = True
            break
        time.sleep(0.1)
    if not loaded:
        print(f'WARN: model {model} not loaded');
    veh = nat('CREATE_VEHICLE', [h, pos[0], pos[1], pos[2] + 2.0, START[3], False, False, False], 'int')
    nat('SET_VEHICLE_ON_GROUND_PROPERLY', [veh])
    nat('SET_VEHICLE_ENGINE_ON', [veh, True, True, False])
    nat('SET_PED_INTO_VEHICLE', [p, veh, -1])
    nat('SET_MODEL_AS_NO_LONGER_NEEDED', [h])   # takes Hash by value — safe
    time.sleep(0.4)
    return veh


def trigger_run(secs, mode='auto'):
    before = os.path.getmtime(CSV) if os.path.exists(CSV) else 0
    with open(TRIGGER, 'w') as f:
        f.write(f'{secs};{mode}')
    # wait for the harness to rewrite the CSV (teleport+settle+run+complete)
    deadline = time.time() + secs + 18
    while time.time() < deadline:
        time.sleep(1.0)
        if os.path.exists(CSV) and os.path.getmtime(CSV) > before:
            time.sleep(0.5)
            return True
    return False


def parse(path):
    rows = []
    with open(path) as f:
        next(f)
        for line in f:
            p = line.strip().split(',')
            if len(p) < 6:
                continue
            rows.append({'ms': int(p[0]), 'spd': float(p[1]), 'rpm': float(p[2]),
                         'gear': int(p[3]), 'thr': float(p[4]), 'mult': float(p[5])})
    return rows


def summarize(rows, label):
    if not rows:
        return {'label': label, 'error': 'empty'}
    top = max(r['spd'] for r in rows)
    # accel times
    def t_at(target):
        for r in rows:
            if r['spd'] >= target:
                return r['ms'] / 1000.0
        return None
    # distance via trapezoid
    dist = 0.0
    for a, b in zip(rows, rows[1:]):
        dt = (b['ms'] - a['ms']) / 1000.0
        dist += (a['spd'] + b['spd']) / 2 * dt
    # shift points
    shifts = []
    for a, b in zip(rows, rows[1:]):
        if b['gear'] > a['gear']:
            shifts.append({'gear': f"{a['gear']}->{b['gear']}", 'ms': b['ms'],
                           'mph': round(b['spd'] * MS2MPH, 1), 'rpm': round(a['rpm'], 3)})
    return {
        'label': label,
        'topspeed_mph': round(top * MS2MPH, 1),
        'topspeed_ms': round(top, 2),
        '0-60mph_s': t_at(26.8224),
        '0-100mph_s': t_at(44.704),
        'dist_m': round(dist, 1),
        'final_gear': rows[-1]['gear'],
        'top_gear_seen': max(r['gear'] for r in rows),
        'duration_s': round(rows[-1]['ms'] / 1000.0, 2),
        'shifts': shifts,
        'samples': len(rows),
    }


def main():
    if len(sys.argv) < 2:
        print('usage: python dyno.py <current|model> [secs] [label]'); return
    target = sys.argv[1]
    secs = int(sys.argv[2]) if len(sys.argv) > 2 else 25
    label = sys.argv[3] if len(sys.argv) > 3 else target

    if target != 'current':
        veh = spawn(target)
        print(f'spawned {target} -> handle {veh}')
        time.sleep(0.5)

    print(f'triggering dyno: {secs}s ...')
    ok = trigger_run(secs)
    if not ok:
        print('TIMEOUT: no telemetry written'); return
    rows = parse(CSV)
    summ = summarize(rows, label)
    # capture the gearbox dump written at launch
    rp = os.path.join(GAME_DIR, 'last_ratios.txt')
    if os.path.exists(rp):
        try:
            raw = open(rp).read().strip().split(';')
            summ['maxFlatVel'] = float(raw[0])
            summ['topGear'] = int(raw[1])
            summ['driveGears'] = int(raw[2])
            summ['ratios'] = [float(x) for x in raw[3].split(',')]
        except Exception as e:
            summ['ratios_err'] = str(e)
    # save labeled copies
    import shutil
    shutil.copy(CSV, os.path.join(RESULTS, f'{label}.csv'))
    with open(os.path.join(RESULTS, f'{label}.json'), 'w') as f:
        json.dump(summ, f, indent=2)
    print(json.dumps(summ, indent=2))


if __name__ == '__main__':
    main()
