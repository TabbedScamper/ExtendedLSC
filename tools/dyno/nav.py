#!/usr/bin/env python3
"""Reliable blind ELSC menu navigation via the menustate file the mod writes
(scripts/ExtendedLSC_menustate.json) when DebugLogging is on."""
import time, json, os
import dyno

MS = r"C:/Program Files/Rockstar Games/Grand Theft Auto V Legacy/scripts/ExtendedLSC_menustate.json"


def state():
    try:
        with open(MS) as f:
            return json.load(f)
    except Exception:
        return {}


def key(k, wait=0.12):
    dyno.call('send_keys', {'keys': k})
    time.sleep(wait)


def to_index(target, maxsteps=40):
    """Send Down/Up until selectedIndex == target."""
    for _ in range(maxsteps):
        s = state()
        cur = s.get('selectedIndex', 0)
        if cur == target:
            return True
        key('Down' if cur < target else 'Up')
    return state().get('selectedIndex') == target


def to_title(title, maxsteps=40):
    for _ in range(maxsteps):
        s = state()
        items = s.get('items', [])
        if title in items:
            return to_index(items.index(title))
        key('Down')
    return False


def dump():
    s = state()
    print('menu:', s.get('menu'), '| sel:', s.get('selectedIndex'), '| count:', s.get('count'))
    for i, t in enumerate(s.get('items', [])):
        print(('>>' if i == s.get('selectedIndex') else '  ') + f'{i:2d} {t}')


if __name__ == '__main__':
    import sys
    if len(sys.argv) > 1 and sys.argv[1] == 'enter':
        title = sys.argv[2]
        if to_title(title):
            key('Enter', 0.3)
            dump()
    else:
        dump()
