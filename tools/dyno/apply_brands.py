#!/usr/bin/env python3
"""Apply confident brand names + prestige-tier pricing to the B-Rims category jsons.
Painted/Chrome/Graffiti of one design share a brand+number; ambiguous designs keep a
clean category label at the category-tier price. Graffiti adds +$3000."""
import os, json, re

WH = 'C:/Program Files/Rockstar Games/Grand Theft Auto V Legacy/scripts/ExtendedLSC/Universal/Wheels'

BRANDS = {
    'SUV': {'Vossen': list(range(1, 47))},
    'Sport': {'Rotiform': list(range(1, 12)), 'Stance': [12, 13], 'RAYS': [16, 17, 18, 19],
              'Vorsteiner': list(range(20, 39)), 'Equip': [41, 42], 'Gnosis': [43, 44], 'Seeker': [45, 46]},
    'Muscle': {'Rotiform': [1, 2, 3, 4, 5, 22, 23], 'Vorsteiner': [9, 10, 11, 12, 13, 24, 25],
               'Gnosis': [15, 16, 17, 19], 'Varianza': [18], 'Meister': [26, 27]},
    'Tuner': {'3SDM': [1, 2], 'ADV.1': [3, 4], 'Alpina': [5], 'Avant Garde': [6, 7], 'BBS': [8, 9],
              'Brada': [10, 11], 'Hochtech': [12], 'ISS': [39], 'Lumarai': [40]},
    'High End': {'ADV.1': [1, 2, 3, 4, 5, 6], 'Avant Garde': [7, 8], 'Racing R': [11],
                 'Incurve': [25], 'ISS': [26], 'Lumarai': [27]},
    'Off-Road': {'Schwert': [6, 7], 'Rotiform': list(range(8, 25)), 'Asanti': [31, 32, 33, 34],
                 'Equip': [40, 41, 42, 43, 44]},
}
TIER = {}
for b in ['HRE', 'Vossen', 'ADV.1', 'Vorsteiner', 'BBS', 'RAYS']: TIER[b] = 14000
for b in ['Asanti', 'Rotiform', 'Kranze', 'Meister', 'Avant Garde', 'Brada']: TIER[b] = 10000
for b in ['Equip', 'XXR', '3SDM', 'Stance', 'Incurve', 'Lumarai', 'Seeker', 'Maverick',
          'Gnosis', 'Varianza', 'Schwert', 'ISS', 'Alpina', 'Hochtech', 'Racing R']: TIER[b] = 7000
CATPRICE = {'SUV': 14000, 'Sport': 5000, 'Muscle': 6500, 'Tuner': 5000, 'High End': 12000, 'Off-Road': 8000}

def style_to_brand(cat):
    out = {}
    for brand, styles in BRANDS.get(cat, {}).items():
        for s in styles: out[s] = brand
    return out

summary = {}
for cat in ['SUV', 'Sport', 'Muscle', 'Tuner', 'High End', 'Off-Road']:
    path = os.path.join(WH, 'B-RIMS %s' % cat, 'items.json')
    d = json.load(open(path, encoding='utf-8'))
    s2b = style_to_brand(cat)
    # per-wheel: parse style + finish
    parsed = []
    for it in d['Items']:
        m = re.search(r'Style (\d+) \((\w+)\)', it['Name']) or re.search(r' (\d+) \((\w+)\)', it['Name'])
        s = int(m.group(1)); fin = m.group(2)
        parsed.append((it, s, fin))
    # number designs within each label (brand or category), in style order
    labels = {}  # label -> {style: num}
    seen_styles = sorted({s for _, s, _ in parsed})
    counters = {}
    stylenum = {}
    for s in seen_styles:
        lab = s2b.get(s, cat)
        counters[lab] = counters.get(lab, 0) + 1
        stylenum[s] = (lab, counters[lab])
    branded = 0
    for it, s, fin in parsed:
        lab, num = stylenum[s]
        it['Name'] = '%s %d (%s)' % (lab, num, fin)
        price = TIER.get(lab, CATPRICE[cat])
        if fin == 'Graffiti': price += 3000
        it['Price'] = price
        if lab != cat: branded += 1
    json.dump(d, open(path, 'w', encoding='utf-8'), indent=2)
    summary[cat] = (len(parsed), branded)

for cat, (tot, br) in summary.items():
    print('%-9s %d wheels, %d branded, %d generic' % (cat, tot, br, tot-br))
