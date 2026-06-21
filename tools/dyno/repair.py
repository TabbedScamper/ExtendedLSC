#!/usr/bin/env python3
"""Re-pair painted<->chrome for every B-Rims category using the reflection-robust
descriptor (material mask + morphological closing), reusing the already-saved rim
captures. Rewrites each items.json with grouped 'Style N (Painted/Chrome)' labels."""
import os, json
from PIL import Image, ImageFilter

TMP = r'C:\Users\mwalt\AppData\Local\Temp'
WHEELS = r'C:/Program Files/Rockstar Games/Grand Theft Auto V Legacy/scripts/ExtendedLSC/Universal/Wheels'

# cat, prefix, wheelType, lo, hi, price, filename-pattern
CATS = [
    ('SUV',      'Vossen',   3, 38, 122, 10000, lambda i: os.path.join(TMP, 'rim_%d.png' % i)),
    ('Sport',    'Sport',    0, 50, 122,  5000, lambda i: os.path.join(TMP, 'rim_Sport_%d.png' % i)),
    ('Muscle',   'Muscle',   1, 36,  83,  6500, lambda i: os.path.join(TMP, 'rim_Muscle_%d.png' % i)),
    ('Tuner',    'Tuner',    5, 48, 111,  5000, lambda i: os.path.join(TMP, 'rim_Tuner_%d.png' % i)),
    ('High End', 'High End', 7, 40,  83, 12000, lambda i: os.path.join(TMP, 'rim_High End_%d.png' % i)),
]

def blue_pct(im):
    w, h = im.size
    c = im.crop((int(w*0.28), int(h*0.28), int(w*0.72), int(h*0.72)))
    px = list(c.getdata())
    return 100.0*sum(1 for r, g, b in px if b > 90 and b > r+25 and b > g+10)/len(px)

def desc(im):
    w, h = im.size
    im = im.crop((int(w*0.16), int(h*0.16), int(w*0.84), int(h*0.84))).resize((96, 96))
    md = []
    for r, g, b in im.getdata():
        mx = max(r, g, b)/255.0; mn = min(r, g, b)/255.0; s = 0 if mx == 0 else (mx-mn)/mx
        md.append(255 if (mx > 0.32 or s > 0.4) else 0)
    m = Image.new('L', (96, 96)); m.putdata(md)
    m = m.filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.MinFilter(5))
    return [1 if x > 127 else 0 for x in m.resize((48, 48)).getdata()]

def process(cat, prefix, wt, lo, hi, price, fn):
    finish, D = {}, {}
    for i in range(lo, hi+1):
        im = Image.open(fn(i)).convert('RGB')
        finish[i] = 'Chrome' if blue_pct(im) <= 20 else 'Painted'
        D[i] = desc(im)
    sim = lambda a, b: sum(1 for x, y in zip(D[a], D[b]) if x == y)/2304.0
    chro = [i for i in range(lo, hi+1) if finish[i] == 'Chrome']
    pai = [i for i in range(lo, hi+1) if finish[i] == 'Painted']
    groups = []
    if chro and pai:
        cand = sorted([(sim(c, p), c, p) for c in chro for p in pai], reverse=True)
        uc, up = set(), set()
        for sc, c, p in cand:
            if c in uc or p in up: continue
            groups.append(sorted([c, p])); uc.add(c); up.add(p)
        for i in range(lo, hi+1):
            if i not in uc and i not in up: groups.append([i])
    else:
        groups = [[i] for i in range(lo, hi+1)]
    groups.sort(key=lambda g: g[0])
    style = {}
    for n, g in enumerate(groups, 1):
        for i in g: style[i] = n
    items = []
    for i in range(lo, hi+1):
        nm = '%s Style %d (%s)' % (prefix, style[i], finish[i]) if (chro and pai) else '%s %d' % (prefix, style[i])
        items.append({'Name': nm, 'Description': None, 'Price': price, 'Value': i, 'Type': None,
                      'SourceModType': -1, 'WheelType': wt, 'R': None, 'G': None, 'B': None})
    folder = os.path.join(WHEELS, 'B-RIMS %s' % cat); os.makedirs(folder, exist_ok=True)
    blob = {'MenuTitle': 'B-RIMS %s' % cat, 'Description': 'B-Rims %s wheels.' % cat, 'Items': items}
    json.dump(blob, open(os.path.join(folder, 'items.json'), 'w', encoding='utf-8'), indent=2)
    return len(pai), len(chro), len(groups)

if __name__ == '__main__':
    for cat, prefix, wt, lo, hi, price, fn in CATS:
        p, c, n = process(cat, prefix, wt, lo, hi, price, fn)
        print('%-10s %d painted %d chrome -> %d styles' % (cat, p, c, n))
