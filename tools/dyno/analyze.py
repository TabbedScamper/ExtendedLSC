#!/usr/bin/env python3
"""Analyze a run_telemetry.csv for strange post-shift behavior (bog / stall on imperfect shifts)."""
import csv, sys

MS2MPH = 2.2369362921


def load(path):
    rows = []
    for r in csv.DictReader(open(path)):
        rows.append({'ms': int(r['ms']), 'spd': float(r['speed']), 'rpm': float(r['rpm']),
                     'gear': int(r['gear']), 'thr': float(r['throttle']), 'mult': float(r['mult'])})
    return rows


def spd_at(rows, ms):
    best = None
    for r in rows:
        if r['ms'] >= ms:
            return r['spd']
        best = r['spd']
    return best


def rpm_min_after(rows, ms0, ms1):
    vals = [r['rpm'] for r in rows if ms0 <= r['ms'] <= ms1]
    return min(vals) if vals else None


def analyze(path, label):
    rows = load(path)
    if not rows:
        print(f'{label}: empty'); return
    print(f'\n=== {label} ===')
    for a, b in zip(rows, rows[1:]):
        if b['gear'] > a['gear']:
            t = b['ms']
            shift_rpm = a['rpm']
            shift_mph = a['spd'] * MS2MPH
            v0 = b['spd']
            v1 = spd_at(rows, t + 800)
            accel = (v1 - v0) / 0.8 if v1 is not None else 0
            rpm_dip = rpm_min_after(rows, t, t + 600)
            flag = ''
            if accel < 0.6:
                flag += ' <BOG/STALL>'
            if v1 is not None and v1 < v0:
                flag += ' <SPEED-DROP>'
            if rpm_dip is not None and rpm_dip < 0.28:
                flag += ' <RPM-COLLAPSE>'
            print(f"  {a['gear']}->{b['gear']}  shift@{shift_rpm:.2f}rpm {shift_mph:5.1f}mph  "
                  f"post-accel={accel:+.2f}m/s2  rpmDip={rpm_dip:.2f}{flag}")


if __name__ == '__main__':
    for p in sys.argv[1:]:
        import os
        analyze(p, os.path.basename(p))
