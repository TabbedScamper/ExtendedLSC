#!/usr/bin/env python3
"""Finalize the Off-Road B-Rims category: chrome (blue test), Graffiti custom-lip
(hue-diversity verified), painted otherwise; pair painted<->chrome by structure."""
import os, json
from PIL import Image, ImageFilter

TMP = 'C:/Users/mwalt/AppData/Local/Temp'
WHEELS = 'C:/Program Files/Rockstar Games/Grand Theft Auto V Legacy/scripts/ExtendedLSC/Universal/Wheels'
LO, HI, WT, PRICE, PREFIX = 35, 88, 4, 8000, 'Off-Road'
CHROME = {37, 43, 46, 52, 58, 64, 67, 73, 79, 85}
GRAFFITI = {55, 61, 70, 76, 82, 88}

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

finish, D = {}, {}
for i in range(LO, HI+1):
    im = Image.open(os.path.join(TMP, 'rim_OffRoad_%d.png' % i)).convert('RGB')
    finish[i] = 'Graffiti' if i in GRAFFITI else ('Chrome' if i in CHROME else 'Painted')
    D[i] = desc(im)
sim = lambda a, b: sum(1 for x, y in zip(D[a], D[b]) if x == y)/2304.0
chro = [i for i in range(LO, HI+1) if finish[i] == 'Chrome']
pai = [i for i in range(LO, HI+1) if finish[i] == 'Painted']
groups, uc, up = [], set(), set()
for sc, c, p in sorted([(sim(c, p), c, p) for c in chro for p in pai], reverse=True):
    if c in uc or p in up: continue
    groups.append(sorted([c, p])); uc.add(c); up.add(p)
for i in range(LO, HI+1):
    if i not in uc and i not in up: groups.append([i])
groups.sort(key=lambda g: g[0])
style = {}
for n, g in enumerate(groups, 1):
    for i in g: style[i] = n
items = [{'Name': '%s Style %d (%s)' % (PREFIX, style[i], finish[i]), 'Description': None,
          'Price': PRICE, 'Value': i, 'Type': None, 'SourceModType': -1, 'WheelType': WT,
          'R': None, 'G': None, 'B': None} for i in range(LO, HI+1)]
folder = os.path.join(WHEELS, 'B-RIMS Off-Road'); os.makedirs(folder, exist_ok=True)
json.dump({'MenuTitle': 'B-RIMS Off-Road', 'Description': 'B-Rims off-road wheels (incl. Graffiti custom-lip).',
           'Items': items}, open(os.path.join(folder, 'items.json'), 'w', encoding='utf-8'), indent=2)
print('Off-Road: %d painted, %d chrome, %d graffiti -> %d styles' % (len(pai), len(chro), len(GRAFFITI), len(groups)))
