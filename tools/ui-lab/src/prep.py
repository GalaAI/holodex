import json, os, sys, struct, shutil, re
src, dst = sys.argv[1], sys.argv[2]
names = sorted(os.listdir(src))
ok = re.compile(r'^[a-z0-9-]+\.(png|json)$')
sizes = {}; maps = []; comps = {}; toks = {}; n_el = 0; keys=set(); issues=[]
for n in names:
    if not ok.match(n): issues.append(('badname', n)); continue
    p = os.path.join(src, n)
    if os.path.islink(p) or not os.path.isfile(p): issues.append(('notfile', n)); continue
    if n.endswith('.png'):
        b = open(p,'rb').read(32)
        if b[:8] != b'\x89PNG\r\n\x1a\n': issues.append(('notpng', n)); continue
        w,h = struct.unpack('>II', b[16:24]); sizes[n[:-4]] = [w,h]
        shutil.copyfile(p, os.path.join(dst, n))
    else:
        d = json.load(open(p, encoding='utf-8-sig'))
        els = d.get('elements', [])
        keys |= set(d.keys())
        for e in els:
            keys |= {'el.'+k for k in e}
            comps[e.get('component')] = comps.get(e.get('component'),0)+1
            for t in e.get('tokens') or []: toks[t]=toks.get(t,0)+1
        n_el += len(els); maps.append(n[:-5])
        json.dump(d, open(os.path.join(dst, n),'w'), separators=(',',':'))
print('png', len(sizes), 'maps', len(maps), 'elements', n_el, 'issues', issues)
print('keys', sorted(keys)); print('components', comps); print('tokens', toks)
print('maps', maps)
json.dump(sizes, open(sys.argv[3],'w')); json.dump(maps, open(sys.argv[4],'w'))
