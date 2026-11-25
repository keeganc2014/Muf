#!/usr/bin/env python3
"""
Adds the surface kinds that pack_assets.py does not know to Assets/Resources/Map/map.bytes (in place):

  metal  - the city's corrugated / teal metal (found by where on the shared texture atlas each triangle points; the atlas is
           material 17 = texture m10), plus the brushed, diamond-plate and rusty metal textures and the painted posts
  ice    - the dark and the light blue of the mountains (textures m68 and m64)   -> slippery (SurfaceInfo.IceSlip)
  wood   - the stump and the ramp down from it (chunks map_-3_0_-3 and map_-2_0_-3): they use the ground's moss texture (m1),
           which counts as grass everywhere else

Run it ONCE after every `python Tools/pack_assets.py map ...` (it refuses to run twice on the same file).
Needs only numpy.   python Tools/fix_map_surfaces.py
"""
import os, sys, json, struct, copy
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, 'Assets', 'Resources', 'Map', 'map.bytes')

# material indices of the packed map (see the "materials" list in its header; texture mN is not always material N)
ICE_MATS = (79, 83)                           # m64 light blue, m68 dark blue
METAL_MATS = (57, 72, 73, 62, 63, 64, 20, 70, 84)   # m42 / m57 brushed, m58 diamond plate, m47-49 rust, m12 / m55 / m69 painted posts
MOSS_MAT = 1                                  # m1: ground / bark texture (grass), used by the stump too
STUMP_CHUNKS = ('map_-3_0_-3', 'map_-2_0_-3')
ATLAS_MAT = 17                                # the city's shared atlas


def load(path):
    b = open(path, 'rb').read()
    assert b[:4] == b'GTP1'
    n = struct.unpack('<I', b[4:8])[0]
    h = json.loads(b[8:8 + n])
    blob = b[8 + n:]
    chunks = []
    for c in h['chunks']:
        N = c['n']; o = c['off']
        raw_vert = bytes(blob[o:o + N * 6 + N * 3 + N * 4]); o += N * 13
        subs = []
        for s in c['subs']:
            subs.append((s['mat'], np.frombuffer(blob, '<i4', s['count'], o).copy())); o += s['count'] * 4
        uq = np.frombuffer(raw_vert, '<u2', N * 2, N * 9)
        umin = np.array(c['uvmin']); umax = np.array(c['uvmax'])
        chunks.append(dict(info=c, raw=raw_vert, uv=umin + uq.reshape(N, 2) / 65535.0 * (umax - umin), subs=subs))
    return h, chunks


def save(path, h, chunks):
    blob = bytearray()
    for ch in chunks:
        c = ch['info']; c['off'] = len(blob); blob += ch['raw']; c['subs'] = []
        for m, idx in ch['subs']:
            blob += np.asarray(idx, '<i4').tobytes(); c['subs'].append({'mat': int(m), 'count': int(len(idx))})
    h['chunks'] = [ch['info'] for ch in chunks]
    js = json.dumps(h, separators=(',', ':')).encode()
    with open(path, 'wb') as f:
        f.write(b'GTP1' + struct.pack('<I', len(js)) + js + bytes(blob))


def atlas_metal(uv, tris):
    """corrugated strip (u<0.25), fine corrugated (0.25-0.5) and teal metal (0.5-0.75) of the 512 px atlas; the rest is brick / concrete"""
    u = uv[tris, 0].mean(1); fy = 1 - uv[tris, 1].mean(1)
    return ((u < 0.25) & (fy > 0.10)) | ((u >= 0.25) & (u < 0.5) & (fy > 0.77)) | ((u >= 0.5) & (u < 0.75) & (fy > 0.77))


def main():
    h, ch = load(PATH)
    mats = h['materials']
    if any(m.get('surf') in ('metal', 'ice') for m in mats):
        sys.exit('map.bytes already has metal / ice surfaces - run pack_assets.py map first if you want to redo it.')

    def new_mat(src, surf):
        m = copy.deepcopy(mats[src]); m['surf'] = surf; mats.append(m); return len(mats) - 1

    for i in ICE_MATS: mats[i]['surf'] = 'ice'
    for i in METAL_MATS: mats[i]['surf'] = 'metal'

    bark = new_mat(MOSS_MAT, 'wood')
    for c in ch:
        if c['info']['name'] in STUMP_CHUNKS:
            c['subs'] = [(bark if m == MOSS_MAT else m, idx) for m, idx in c['subs']]

    metal_atlas = new_mat(ATLAS_MAT, 'metal')
    nm_tot = 0
    for c in ch:
        new = []
        for m, idx in c['subs']:
            if m != ATLAS_MAT: new.append((m, idx)); continue
            T = idx.reshape(-1, 3)
            mk = atlas_metal(c['uv'], T)
            nm = int(mk.sum()); nc = len(T) - nm
            if nm == 0 or (nm < 50 and nc >= 50): new.append((m, idx))
            elif nc < 50: new.append((metal_atlas, idx)); nm_tot += len(T)
            else: new.append((m, T[~mk].ravel())); new.append((metal_atlas, T[mk].ravel())); nm_tot += nm
        c['subs'] = new
    save(PATH, h, ch)
    print('done: %d city metal triangles, ice and stump wood set; %d materials' % (nm_tot, len(mats)))


if __name__ == '__main__':
    main()
