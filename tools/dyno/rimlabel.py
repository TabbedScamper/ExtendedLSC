#!/usr/bin/env python3
"""Capture every rim of a wheel type via a locked in-game camera, classify painted
(takes the blue wheel color) vs chrome (stays silver), structurally pair painted<->chrome
of the same design, and write the ELSC universal-wheel items.json with grouped labels.

Usage:
  python rimlabel.py setup                      # lock camera on the front-left rim
  python rimlabel.py probe  <wt> <lo> <hi>      # sample ~12 indices, print blue% (is there chrome?)
  python rimlabel.py run <cat> <wt> <lo> <hi> <price> [--prefix NAME]   # full sweep + write json
  python rimlabel.py release <cam>              # stop script cam
Wheel types: 0 Sport,1 Muscle,3 SUV,4 OffRoad,5 Tuner,7 HighEnd
"""
import sys, os, json, math, time, ctypes
from ctypes import wintypes
from PIL import Image
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dyno

TMP = r'C:\Users\mwalt\AppData\Local\Temp'
WHEELS = r'C:/Program Files/Rockstar Games/Grand Theft Auto V Legacy/scripts/ExtendedLSC/Universal/Wheels'
user32 = ctypes.windll.user32; gdi32 = ctypes.windll.gdi32

class BMIH(ctypes.Structure):
    _fields_ = [('biSize',wintypes.DWORD),('biWidth',wintypes.LONG),('biHeight',wintypes.LONG),
                ('biPlanes',wintypes.WORD),('biBitCount',wintypes.WORD),('biCompression',wintypes.DWORD),
                ('biSizeImage',wintypes.DWORD),('biXPelsPerMeter',wintypes.LONG),('biYPelsPerMeter',wintypes.LONG),
                ('biClrUsed',wintypes.DWORD),('biClrImportant',wintypes.DWORD)]

def cap_window():
    hwnd = user32.FindWindowW('grcWindow', None) or user32.FindWindowW(None, 'Grand Theft Auto V')
    rect = wintypes.RECT(); user32.GetClientRect(hwnd, ctypes.byref(rect))
    w, h = rect.right-rect.left, rect.bottom-rect.top
    hdc = user32.GetDC(hwnd); memdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h); gdi32.SelectObject(memdc, bmp)
    user32.PrintWindow(hwnd, memdc, 2)  # PW_RENDERFULLCONTENT
    bmi = BMIH(); bmi.biSize = ctypes.sizeof(bmi); bmi.biWidth = w; bmi.biHeight = -h
    bmi.biPlanes = 1; bmi.biBitCount = 32; bmi.biCompression = 0
    buf = ctypes.create_string_buffer(w*h*4)
    gdi32.GetDIBits(memdc, bmp, 0, h, buf, ctypes.byref(bmi), 0)
    img = Image.frombuffer('RGB', (w, h), buf, 'raw', 'BGRX', 0, 1)
    gdi32.DeleteObject(bmp); gdi32.DeleteDC(memdc); user32.ReleaseDC(hwnd, hdc)
    return img

def setup_camera():
    v = dyno.cur_veh()
    bone = dyno.nat('GET_ENTITY_BONE_INDEX_BY_NAME', [v, 'wheel_lf'], 'int'); side = 'lf'
    if not isinstance(bone, int) or bone < 0:
        bone = dyno.nat('GET_ENTITY_BONE_INDEX_BY_NAME', [v, 'wheel_rf'], 'int'); side = 'rf'
    pos = dyno.nat('GET_WORLD_POSITION_OF_ENTITY_BONE', [v, bone], 'vector3')
    hd = dyno.nat('GET_ENTITY_HEADING', [v], 'float'); rad = math.radians(hd)
    dx, dy = (-math.cos(rad), -math.sin(rad)) if side == 'lf' else (math.cos(rad), math.sin(rad))
    d = 2.0; cam = dyno.nat('CREATE_CAM', ['DEFAULT_SCRIPTED_CAMERA', True], 'int')
    dyno.nat('SET_CAM_COORD', [cam, pos[0]+dx*d, pos[1]+dy*d, pos[2]+0.05])
    dyno.nat('POINT_CAM_AT_COORD', [cam, pos[0], pos[1], pos[2]])
    dyno.nat('SET_CAM_FOV', [cam, 28.0]); dyno.nat('SET_CAM_ACTIVE', [cam, True])
    dyno.nat('RENDER_SCRIPT_CAMS', [True, False, 0, True, False])
    return cam

def analyze(im):
    w, h = im.size
    c = im.crop((int(w*0.28), int(h*0.28), int(w*0.72), int(h*0.72)))
    px = list(c.getdata()); n = len(px)
    blue = sum(1 for r, g, b in px if b > 90 and b > r+25 and b > g+10)
    m = im.crop((int(w*0.14), int(h*0.14), int(w*0.86), int(h*0.86))).resize((64, 64))
    mask = []
    for r, g, b in m.getdata():
        mx = max(r, g, b)/255.0; mn = min(r, g, b)/255.0
        s = 0 if mx == 0 else (mx-mn)/mx
        mask.append(1 if (mx > 0.5 or s > 0.45) else 0)
    return 100.0*blue/n, mask

def sweep(cat, wt, lo, hi):
    v = dyno.cur_veh()
    dyno.nat('SET_VEHICLE_MOD_KIT', [v, 0]); dyno.nat('SET_VEHICLE_WHEEL_TYPE', [v, wt])
    out = {}
    for idx in range(lo, hi+1):
        dyno.nat('SET_VEHICLE_MOD', [v, 23, idx, False]); dyno.nat('SET_VEHICLE_MOD', [v, 24, idx, False])
        time.sleep(0.45)
        im = cap_window(); im.save(os.path.join(TMP, 'rim_%s_%d.png' % (cat, idx)))
        bp, mask = analyze(im)
        out[idx] = {'blue': bp, 'mask': mask}
    return out

def pair_and_label(cat, prefix, lo, hi, price, data):
    sim = lambda a, b: sum(1 for x, y in zip(data[a]['mask'], data[b]['mask']) if x == y)/4096.0
    chrome = [i for i in range(lo, hi+1) if data[i]['blue'] <= 20]
    paint = [i for i in range(lo, hi+1) if data[i]['blue'] > 20]
    style = {}; finish = {}
    for i in range(lo, hi+1):
        finish[i] = 'Chrome' if i in chrome else 'Painted'
    if not chrome or not paint:
        # single finish -> simple sequential, no finish suffix
        for n, i in enumerate(range(lo, hi+1), 1): style[i] = (n, None)
    else:
        cand = sorted([(sim(c, p), c, p) for c in chrome for p in paint], reverse=True)
        uc, up, match = set(), set(), {}
        for sc, c, p in cand:
            if c in uc or p in up: continue
            match[c] = p; uc.add(c); up.add(p)
        # build design groups: each matched pair shares a style; singles get their own
        groups = []
        for c, p in match.items(): groups.append(sorted([c, p]))
        for i in range(lo, hi+1):
            if i not in uc and i not in up: groups.append([i])
        groups.sort(key=lambda g: g[0])
        for n, g in enumerate(groups, 1):
            for i in g: style[i] = (n, finish[i])
    items = []
    for i in range(lo, hi+1):
        s, f = style[i]
        name = '%s Style %d (%s)' % (prefix, s, f) if f else '%s %d' % (prefix, s)
        items.append({'Name': name, 'Description': None, 'Price': price, 'Value': i,
                      'Type': None, 'SourceModType': -1, 'WheelType': WT[cat], 'R': None, 'G': None, 'B': None})
    folder = os.path.join(WHEELS, 'B-RIMS %s' % cat)
    os.makedirs(folder, exist_ok=True)
    blob = {'MenuTitle': 'B-RIMS %s' % cat,
            'Description': 'B-Rims %s wheels.' % cat, 'Items': items}
    json.dump(blob, open(os.path.join(folder, 'items.json'), 'w', encoding='utf-8'), indent=2)
    return len(paint), len(chrome), max(s for s, _ in style.values())

WT = {'Sport': 0, 'Muscle': 1, 'SUV': 3, 'Off-Road': 4, 'Tuner': 5, 'High End': 7}

if __name__ == '__main__':
    cmd = sys.argv[1]
    if cmd == 'setup':
        print('CAM=%d' % setup_camera())
    elif cmd == 'release':
        dyno.nat('RENDER_SCRIPT_CAMS', [False, False, 0, True, False])
        dyno.nat('DESTROY_CAM', [int(sys.argv[2]), False]); print('released')
    elif cmd == 'probe':
        wt, lo, hi = int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
        v = dyno.cur_veh(); dyno.nat('SET_VEHICLE_MOD_KIT', [v, 0]); dyno.nat('SET_VEHICLE_WHEEL_TYPE', [v, wt])
        idxs = [int(lo + (hi-lo)*k/11) for k in range(12)]
        for idx in idxs:
            dyno.nat('SET_VEHICLE_MOD', [v, 23, idx, False]); dyno.nat('SET_VEHICLE_MOD', [v, 24, idx, False])
            time.sleep(0.45); bp, _ = analyze(cap_window())
            print('idx %3d  blue%%=%.1f  %s' % (idx, bp, 'Painted' if bp > 20 else 'CHROME'))
    elif cmd == 'run':
        cat, wt, lo, hi, price = sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5]), int(sys.argv[6])
        prefix = sys.argv[8] if len(sys.argv) > 8 and sys.argv[7] == '--prefix' else cat
        data = sweep(cat, wt, lo, hi)
        p, c, ns = pair_and_label(cat, prefix, lo, hi, price, data)
        print('%s: %d painted, %d chrome, %d design-styles -> items.json written' % (cat, p, c, ns))
