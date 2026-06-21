#!/usr/bin/env python3
"""Batch +gears verification dyno.

For each vehicle (chosen for a wide top-speed spread) it spawns a FRESH stock car
and dynos it at gearDelta = 0, +1, +2. A fresh spawn each run guarantees stock gears
so deltas never accumulate. The harness applies the delta via the EXACT in-game fix
path (VehicleMemory.SetTopGear + ELSCTransmission.RefreshGears) at the launch point.

autorun.txt format extended to: "secs;mode;gearDelta"

Pass/fail per run:
  - dump topGear == stock+delta            (live write took)
  - telemetry top_gear_seen == stock+delta (the harness actually shifted INTO the new gear -> it engages)
  - new ratio slot non-zero                (gear has a real ratio, won't bog)

Usage: python dyno_gears.py [secs] [vehicle1 vehicle2 ...]
"""
import sys, time, json, os
import dyno  # reuse call/nat/spawn/parse/summarize from dyno.py

SECS_DEFAULT = 16
DELTAS = [0, 1, 2]

# Wide top-speed spread (slow economy -> hypercar)
DEFAULT_FLEET = ['blista', 'sultan', 'dominator', 'banshee', 'comet2', 'adder']


def trigger_run_gd(secs, delta, mode='auto'):
    before = os.path.getmtime(dyno.CSV) if os.path.exists(dyno.CSV) else 0
    with open(dyno.TRIGGER, 'w') as f:
        f.write(f'{secs};{mode};{delta}')
    deadline = time.time() + secs + 22
    while time.time() < deadline:
        time.sleep(1.0)
        if os.path.exists(dyno.CSV) and os.path.getmtime(dyno.CSV) > before:
            time.sleep(0.6)
            return True
    return False


def read_dump():
    rp = os.path.join(dyno.GAME_DIR, 'last_ratios.txt')
    try:
        raw = open(rp).read().strip().split(';')
        return {'maxFlatVel': float(raw[0]), 'topGear': int(raw[1]),
                'driveGears': int(raw[2]), 'ratios': [float(x) for x in raw[3].split(',')]}
    except Exception as e:
        return {'err': str(e)}


def run_one(model, secs, delta):
    dyno.spawn(model)          # fresh stock car every run
    time.sleep(0.6)
    ok = trigger_run_gd(secs, delta)
    if not ok:
        return {'model': model, 'delta': delta, 'error': 'timeout'}
    rows = dyno.parse(dyno.CSV)
    summ = dyno.summarize(rows, f'{model}+{delta}')
    summ['model'] = model
    summ['delta'] = delta
    summ.update(read_dump())
    return summ


def main():
    args = sys.argv[1:]
    secs = SECS_DEFAULT
    if args and args[0].isdigit():
        secs = int(args[0]); args = args[1:]
    fleet = args if args else DEFAULT_FLEET

    all_results = []
    for model in fleet:
        print(f'\n===== {model} =====')
        stock_top = None
        for d in DELTAS:
            r = run_one(model, secs, d)
            all_results.append(r)
            if 'error' in r:
                print(f'  delta {d}: ERROR {r["error"]}'); continue
            if d == 0:
                stock_top = r.get('topGear')
            exp = (stock_top + d) if stock_top is not None else '?'
            tg = r.get('topGear'); seen = r.get('top_gear_seen')
            new_ratio = None
            ratios = r.get('ratios') or []
            # gear g uses ratio index g+1 (idx0 unused, idx1 reverse). top gear ratio:
            if tg and tg + 1 < len(ratios):
                new_ratio = ratios[tg + 1]
            verdict = 'OK' if (tg == exp and seen == exp) else 'CHECK'
            print(f'  delta +{d}: top={r.get("topspeed_mph")}mph  dumpTopGear={tg} (exp {exp})  '
                  f'gearSeen={seen}  topRatio={new_ratio}  shifts={len(r.get("shifts",[]))}  [{verdict}]')

    out = os.path.join(dyno.RESULTS, 'gears_batch.json')
    with open(out, 'w') as f:
        json.dump(all_results, f, indent=2)
    print(f'\nSaved {len(all_results)} runs -> {out}')

    # compact comparison table
    print('\n=== SUMMARY (topspeed mph / top gear seen) ===')
    by_model = {}
    for r in all_results:
        by_model.setdefault(r['model'], {})[r.get('delta')] = r
    print(f'{"model":12} {"stock":>16} {"+1":>16} {"+2":>16}')
    for m, ds in by_model.items():
        cells = []
        for d in DELTAS:
            r = ds.get(d, {})
            if 'error' in r:
                cells.append('ERR')
            else:
                cells.append(f'{r.get("topspeed_mph","?")}/{r.get("top_gear_seen","?")}')
        print(f'{m:12} {cells[0]:>16} {cells[1]:>16} {cells[2]:>16}')


if __name__ == '__main__':
    main()
