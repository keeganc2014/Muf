#!/usr/bin/env python3
"""
Packs the big source models into the small runtime format the game loads (PackedMesh.cs):

    python Tools/pack_assets.py map    the_entire_gtag_map.glb   -> Assets/Resources/Map/map.bytes + Map/Textures/*
    python Tools/pack_assets.py timmy  timmy.glb                 -> Assets/Resources/Creatures/timmy.bytes + textures
    python Tools/pack_assets.py spider "HopeFully Fixed Spidr.fbx" spider_textures_dir
                                                                 -> Assets/Resources/Creatures/spider.bytes + textures
    python Tools/pack_assets.py player gorilla.glb  -> Assets/Resources/Rig/player.bytes (rigged player model) + textures
    python Tools/pack_assets.py monke                            -> tags the parts (body / arms / hands) of monke_mesh.txt

Needs only numpy + Pillow (+ scipy for 'monke'). No Blender, no Unity.
File format: 'GTP1' | u32 jsonLength | json | binary blob. Per chunk: positions u16[3n] (inside the chunk's box),
normals i8[3n], uvs u16[2n] (inside the chunk's uv box), indices i32[...] (one run per sub-mesh).
Coordinates are converted to Unity (x is negated, triangle winding swapped).
"""
import sys, os, re, io, json, struct, collections
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RES = os.path.join(ROOT, 'Assets', 'Resources')
MAX_TEX = 512
CELL = 32.0


# ---------------------------------------------------------------- packed file writer
class Pack:
    def __init__(self):
        self.materials = []
        self.chunks = []
        self.blob = bytearray()
        self.extra = {}

    def add_material(self, **kw):
        self.materials.append(kw)
        return len(self.materials) - 1

    def add_chunk(self, name, group, collide, pos, nrm, uv, subs):
        """pos/nrm (n,3) float, uv (n,2) float (already Unity v), subs = [(material, int32 indices)]"""
        n = len(pos)
        pmin, pmax = pos.min(0), pos.max(0)
        span = np.maximum(pmax - pmin, 1e-6)
        q = np.round((pos - pmin) / span * 65535.0).astype('<u2')
        nq = np.clip(np.round(nrm * 127.0), -127, 127).astype('<i1')
        umin, umax = uv.min(0), uv.max(0)
        uspan = np.maximum(umax - umin, 1e-6)
        uq = np.round((uv - umin) / uspan * 65535.0).astype('<u2')
        off = len(self.blob)
        self.blob += q.tobytes() + nq.tobytes() + uq.tobytes()
        sub_info = []
        for m, idx in subs:
            self.blob += np.asarray(idx, '<i4').tobytes()
            sub_info.append({'mat': int(m), 'count': int(len(idx))})
        self.chunks.append({'name': name, 'group': group, 'collide': bool(collide), 'n': int(n), 'off': int(off),
                            'pmin': [float(v) for v in pmin], 'pmax': [float(v) for v in pmax],
                            'uvmin': [float(v) for v in umin], 'uvmax': [float(v) for v in umax], 'subs': sub_info})

    def save(self, path):
        head = {'materials': self.materials, 'chunks': self.chunks}
        head.update(self.extra)
        js = json.dumps(head, separators=(',', ':')).encode()
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'wb') as f:
            f.write(b'GTP1' + struct.pack('<I', len(js)) + js + bytes(self.blob))
        print('wrote', path, os.path.getsize(path) // 1024, 'KB;', len(self.chunks), 'chunks,',
              sum(c['n'] for c in self.chunks), 'verts,', sum(s['count'] for c in self.chunks for s in c['subs']) // 3, 'tris')


def weld(pos, nrm, uv, idx):
    """merge identical vertices (same position, normal and uv)"""
    key = np.hstack([np.round(pos * 1000), np.round(nrm * 60), np.round(uv * 2000)]).astype(np.int64)
    _, first, inv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    inv = inv.ravel()
    return pos[first], nrm[first], uv[first], inv[idx]


def save_texture(img, name, outdir, alpha):
    img = img.copy()
    if max(img.size) > MAX_TEX:
        s = MAX_TEX / max(img.size)
        img = img.resize((max(1, int(img.width * s)), max(1, int(img.height * s))), Image.LANCZOS)
    os.makedirs(outdir, exist_ok=True)
    if alpha:
        p = os.path.join(outdir, name + '.png')
        img.convert('RGBA').save(p, optimize=True)
    else:
        p = os.path.join(outdir, name + '.jpg')
        img.convert('RGB').save(p, quality=85, optimize=True)
    return p


# ---------------------------------------------------------------- glb reading
CT = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}


class Glb:
    def __init__(self, path):
        data = open(path, 'rb').read()
        off = 12
        self.bin = None
        while off < len(data):
            cl, ct = struct.unpack('<II', data[off:off + 8]); off += 8
            chunk = data[off:off + cl]; off += cl
            if ct == 0x4E4F534A: self.js = json.loads(chunk)
            elif ct == 0x004E4942: self.bin = chunk
        self.acc = self.js['accessors']; self.bv = self.js['bufferViews']; self.nodes = self.js['nodes']

    def read(self, i):
        a = self.acc[i]; v = self.bv[a['bufferView']]
        dt = np.dtype(CT[a['componentType']]); n = NC[a['type']]
        stride = v.get('byteStride', dt.itemsize * n)
        off = v.get('byteOffset', 0) + a.get('byteOffset', 0); cnt = a['count']
        if stride == dt.itemsize * n:
            return np.frombuffer(self.bin, dt, cnt * n, off).reshape(cnt, n).copy()
        raw = np.frombuffer(self.bin, np.uint8, stride * cnt, off).reshape(cnt, stride)
        return np.frombuffer(raw[:, :dt.itemsize * n].tobytes(), dt).reshape(cnt, n).copy()

    def image(self, i):
        im = self.js['images'][i]; v = self.bv[im['bufferView']]
        o = v.get('byteOffset', 0)
        return Image.open(io.BytesIO(self.bin[o:o + v['byteLength']]))

    @staticmethod
    def node_matrix(n):
        if 'matrix' in n: return np.array(n['matrix'], float).reshape(4, 4).T
        T = np.eye(4); R = np.eye(4); S = np.eye(4)
        if 'translation' in n: T[:3, 3] = n['translation']
        if 'rotation' in n:
            x, y, z, w = n['rotation']
            R[:3, :3] = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                         [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                         [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
        if 'scale' in n: S = np.diag(list(n['scale']) + [1])
        return T @ R @ S

    def material_info(self, mi, pack, outdir, resprefix, tex_cache, force_unlit=False):
        """returns the index of the pack material for glTF material mi"""
        key = (mi, force_unlit)
        if key in tex_cache['mats']: return tex_cache['mats'][key]
        m = self.js['materials'][mi] if mi is not None else {}
        pbr = m.get('pbrMetallicRoughness', {})
        color = list(pbr.get('baseColorFactor', [1, 1, 1, 1]))
        emissive = m.get('emissiveFactor')
        tex = ''
        has_alpha = False
        bt = pbr.get('baseColorTexture')
        if bt is not None:
            img_i = self.js['textures'][bt['index']]['source']
            img = self.image(img_i)
            if img.mode in ('RGBA', 'LA', 'P'):
                a = np.array(img.convert('RGBA'))[:, :, 3]
                has_alpha = (a < 250).mean() > 0.01
            blend = m.get('alphaMode') == 'BLEND' or m.get('alphaMode') == 'MASK'
            use_alpha = has_alpha and blend
            name = 'm%d' % img_i
            if name not in tex_cache['imgs']:
                save_texture(img, name, outdir, use_alpha)
                tex_cache['imgs'][name] = True
            tex = resprefix + name
            has_alpha = use_alpha
        if bt is None and emissive and max(emissive) > 0:
            color = [emissive[0], emissive[1], emissive[2], 1.0]
            unlit = True
        else:
            unlit = False
        if m.get('alphaMode') == 'BLEND' and not has_alpha and color[3] < 0.99:
            has_alpha = True
        if has_alpha or force_unlit: unlit = True
        surf = ''
        if bt is not None:
            surf = 'wood' if img_i in WOOD_IMAGES else 'grass' if img_i in GRASS_IMAGES else ''
        idx = pack.add_material(tex=tex, color=[float(c) for c in color], unlit=bool(unlit), alpha=bool(has_alpha), surf=surf)
        tex_cache['mats'][key] = idx
        return idx


# ---------------------------------------------------------------- map
# source image numbers (glTF image index) -> what a hand hit on them sounds like; everything else = concrete/stone
WOOD_IMAGES = {0, 4, 8, 9, 13, 14, 16, 18, 40, 41, 43, 50, 52, 53, 54, 60}   # tree rings, planks, bark, shingles
GRASS_IMAGES = {1, 15, 32, 33, 44, 59, 62, 63}                                  # moss/ground, leaves, bushes, pine branches
NAMED = ['campgroundstructure_1592', 'scoreboard_1590', 'scoreboard__1__2122', 'PhysicalComputer_1744',
         'PhysicalComputer__2__2151', 'mirror_89', 'mirrors2__1_.001_681']


def strip_name(n):
    return re.sub(r'_\d+$', '', n)


def pack_map(path):
    g = Glb(path)
    nodes = g.nodes
    parent = {}
    world = {}

    def walk(i, P):
        W = P @ g.node_matrix(nodes[i]); world[i] = W
        for c in nodes[i].get('children', []):
            parent[c] = i; walk(c, W)
    for r in g.js['scenes'][0]['nodes']: walk(r, np.eye(4))
    by_name = {n.get('name'): i for i, n in enumerate(nodes)}
    named_ids = {by_name[n]: strip_name(n) for n in NAMED if n in by_name}
    cave_id, forest_id = by_name['cave_1571'], by_name['forest_1976']
    far_ids = {by_name[n] for n in ('Sphere_1978', 'Sign_And_Igloo_2184') if n in by_name}
    sky_id = by_name.get('Sphere_1978')

    def classify(i):
        grp = None; reg = 'map'; far = False; sky = False
        j = i
        while j is not None:
            if grp is None and j in named_ids: grp = named_ids[j]
            if j == cave_id: reg = 'cave'
            elif j == forest_id and reg == 'map': reg = 'forest'
            if j in far_ids: far = True
            if j == sky_id: sky = True
            j = parent.get(j)
        return grp, ('far' if far else reg), sky

    pack = Pack()
    outdir = os.path.join(RES, 'Map', 'Textures')
    for f in (os.listdir(outdir) if os.path.isdir(outdir) else []): os.remove(os.path.join(outdir, f))
    cache = {'mats': {}, 'imgs': {}}
    pieces = collections.defaultdict(list)     # key -> list of (mat, pos, nrm, uv, idx)
    board_pieces = []
    bounds_all = [np.full(3, 1e9), np.full(3, -1e9)]
    zone = {'cave': [np.full(3, 1e9), np.full(3, -1e9)], 'forest': [np.full(3, 1e9), np.full(3, -1e9)]}
    for ni, n in enumerate(nodes):
        if 'mesh' not in n: continue
        grp, reg, sky = classify(ni)
        W = world[ni]; M3 = W[:3, :3]
        Nm = np.linalg.inv(M3).T
        flip = np.linalg.det(M3) > 0     # glTF -> Unity mirrors x, which swaps the winding (and a mirrored node swaps it back)
        for p in g.js['meshes'][n['mesh']]['primitives']:
            at = p['attributes']
            P = g.read(at['POSITION']).astype(np.float64); N = g.read(at['NORMAL']).astype(np.float64)
            U = g.read(at['TEXCOORD_0']).astype(np.float64)
            idx = g.read(p['indices']).astype(np.int64).ravel()
            P, N, U, idx = weld(P, N, U, idx.reshape(-1, 3).ravel())
            idx = idx.reshape(-1, 3)
            P = (W @ np.hstack([P, np.ones((len(P), 1))]).T).T[:, :3]
            N = (Nm @ N.T).T
            N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
            P[:, 0] *= -1; N[:, 0] *= -1                       # Unity space
            U = np.stack([U[:, 0], 1.0 - U[:, 1]], 1)
            if sky:                                             # the sky sphere is seen from inside: make its faces point inwards
                c = P.mean(0)
                if (N * (P - c)).sum(1).mean() > 0:
                    N = -N
                    flip = not flip
            if flip: idx = idx[:, [0, 2, 1]]
            mat = g.material_info(p.get('material'), pack, outdir, 'Map/Textures/', cache, force_unlit=sky)
            if grp: key = ('grp', grp, grp)
            elif reg == 'far': key = ('far', 'far', 'far')
            else:
                c = P.mean(0); cell = tuple(int(v) for v in np.floor(c / CELL))
                key = (reg, '', '%s_%d_%d_%d' % ((reg,) + cell))
            pieces[key].append((mat, P, N, U, idx))
            if grp == 'scoreboard': board_pieces.append((P, N))
            if reg != 'far':
                bounds_all[0] = np.minimum(bounds_all[0], P.min(0)); bounds_all[1] = np.maximum(bounds_all[1], P.max(0))
            if reg in zone:
                zone[reg][0] = np.minimum(zone[reg][0], P.min(0)); zone[reg][1] = np.maximum(zone[reg][1], P.max(0))

    # sub-divide far/named groups per material only when they get huge; otherwise one chunk per key
    for key, lst in sorted(pieces.items(), key=lambda kv: kv[0]):
        kind, group, name = key
        pos = np.vstack([p[1] for p in lst]); nrm = np.vstack([p[2] for p in lst]); uv = np.vstack([p[3] for p in lst])
        offs = np.cumsum([0] + [len(p[1]) for p in lst])
        per_mat = collections.OrderedDict()
        for k, p in enumerate(lst):
            per_mat.setdefault(p[0], []).append((p[4] + offs[k]).ravel())
        subs = [(m, np.concatenate(v)) for m, v in per_mat.items()]
        collide = kind != 'far'
        pack.add_chunk(name, group if kind == 'grp' else '', collide, pos, nrm, uv, subs)
    board = find_board(board_pieces)
    if board: pack.extra['board'] = board
    pack.extra['bounds'] = {'min': [float(v) for v in bounds_all[0]], 'max': [float(v) for v in bounds_all[1]]}
    for z, (lo, hi) in zone.items():
        pack.extra[z] = {'min': [float(v) for v in lo], 'max': [float(v) for v in hi]}
    pack.save(os.path.join(RES, 'Map', 'map.bytes'))
    print('bounds', pack.extra['bounds']); print('cave', pack.extra['cave']); print('forest', pack.extra['forest'])


def find_board(pieces):
    """the flat panel of the scoreboard: centre, outward normal, up, width, height, thickness (Unity space)"""
    best = None
    for P, N in pieces:
        c = P.mean(0)
        w, v = np.linalg.eigh(np.cov((P - c).T))
        ext = [np.ptp((P - c) @ v[:, k]) for k in (2, 1, 0)]      # largest first
        if len(P) < 4 or ext[2] > 0.3 * ext[1]: continue
        area = ext[0] * ext[1]
        if best is None or area > best[0]:
            best = (area, c, v, ext, N)
    if best is None: return None
    _, c, v, ext, N = best
    ax = [v[:, 2], v[:, 1], v[:, 0]]                              # in-plane axes (long, medium) and the normal
    nrm = ax[2] if N.mean(0) @ ax[2] >= 0 else -ax[2]
    up_i = 0 if abs(ax[0][1]) >= abs(ax[1][1]) else 1
    up = ax[up_i] if ax[up_i][1] >= 0 else -ax[up_i]
    return {'center': [float(x) for x in c], 'normal': [float(x) for x in nrm], 'up': [float(x) for x in up],
            'w': float(ext[1 - up_i]), 'h': float(ext[up_i]), 't': float(ext[2])}


# ---------------------------------------------------------------- Timmy (skinned glb, baked in its rest pose and stood upright)
def pack_timmy(path):
    g = Glb(path)
    nodes = g.nodes
    world = {}

    def walk(i, P):
        W = P @ g.node_matrix(nodes[i]); world[i] = W
        for c in nodes[i].get('children', []): walk(c, W)
    for r in g.js['scenes'][0]['nodes']: walk(r, np.eye(4))
    skin = g.js['skins'][0]
    ibm = g.read(skin['inverseBindMatrices']).reshape(-1, 4, 4).transpose(0, 2, 1)
    J = np.array([world[j] @ ibm[k] for k, j in enumerate(skin['joints'])])
    pack = Pack()
    outdir = os.path.join(RES, 'Creatures', 'Textures')
    cache = {'mats': {}, 'imgs': {}}
    P_all, N_all, U_all, subs = [], [], [], []
    base = 0
    for n in nodes:
        if 'mesh' not in n: continue
        for p in g.js['meshes'][n['mesh']]['primitives']:
            at = p['attributes']
            P = g.read(at['POSITION']).astype(float); N = g.read(at['NORMAL']).astype(float); U = g.read(at['TEXCOORD_0']).astype(float)
            jj = g.read(at['JOINTS_0']).astype(int); ww = g.read(at['WEIGHTS_0']).astype(float)
            ww /= np.maximum(ww.sum(1, keepdims=True), 1e-9)
            P4 = np.hstack([P, np.ones((len(P), 1))])
            op = np.zeros((len(P), 3)); on = np.zeros((len(P), 3))
            for k in range(4):
                Mk = J[jj[:, k]]
                op += ww[:, k:k + 1] * np.einsum('nij,nj->ni', Mk, P4)[:, :3]
                on += ww[:, k:k + 1] * np.einsum('nij,nj->ni', Mk[:, :3, :3], N)
            idx = g.read(p['indices']).astype(np.int64).ravel()
            mat = g.material_info(p['material'], pack, outdir, 'Creatures/Textures/timmy_', cache)
            P_all.append(op); N_all.append(on); U_all.append(U); subs.append((mat, idx + base)); base += len(P)
    P = np.vstack(P_all); N = np.vstack(N_all); U = np.vstack(U_all)
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
    # the model lies along z (head at -z, arms reaching +z, face on +y): stand it up with the head on top, facing +z
    R = np.array([[1, 0, 0], [0, 0, -1], [0, 1, 0]], float)
    P = P @ R.T; N = N @ R.T
    P[:, 0] *= -1; N[:, 0] *= -1
    U = np.stack([U[:, 0], 1.0 - U[:, 1]], 1)
    P[:, 1] -= P[:, 1].min()                                  # lowest point (the hand tips) on the floor
    subs = [(m, i.reshape(-1, 3)[:, [0, 2, 1]].ravel()) for m, i in subs]
    # centre it on the middle of its body
    P[:, 0] -= (P[:, 0].min() + P[:, 0].max()) / 2
    pack.add_chunk('timmy', '', False, P, N, U, subs)
    pack.extra['height'] = float(P[:, 1].max())
    pack.extra['headY'] = float(P[:, 1].max())
    pack.save(os.path.join(RES, 'Creatures', 'timmy.bytes'))
    print('timmy size', P.min(0), P.max(0))


# ---------------------------------------------------------------- spider (ASCII fbx)
def fbx_parse(path):
    root = []; stack = [root]; arr = None; buf = []
    with open(path, 'r', errors='replace') as f:
        for line in f:
            s = line.strip()
            if arr is not None:
                if s.startswith('a:'): s = s[2:]
                if s == '}':
                    t = ''.join(buf).strip().rstrip(',')
                    stack[-1].append((arr, np.fromstring(t, sep=',') if t else np.zeros(0), []))
                    arr = None; buf = []; continue
                buf.append(s); continue
            if not s or s.startswith(';'): continue
            if s == '}': stack.pop(); continue
            m = re.match(r'^([A-Za-z0-9_]+):\s*(.*)$', s)
            if not m: continue
            name, rest = m.group(1), m.group(2)
            if rest.startswith('*') and rest.rstrip().endswith('{'):
                arr = name; buf = []; continue
            if rest.endswith('{'):
                node = (name, rest[:-1].strip(), []); stack[-1].append(node); stack.append(node[2])
            else:
                stack[-1].append((name, rest, []))
    return root


def pack_spider(fbx, texdir):
    root = fbx_parse(fbx)
    find = lambda ch, nm: [c for c in ch if c[0] == nm]
    obj = find(root, 'Objects')[0][2]
    # the body: the biggest geometry
    geos = sorted(find(obj, 'Geometry'), key=lambda g: -len(find(g[2], 'Vertices')[0][1]))
    geo = geos[0][2]
    V = find(geo, 'Vertices')[0][1].reshape(-1, 3)
    PVI = find(geo, 'PolygonVertexIndex')[0][1].astype(np.int64)
    ends = PVI < 0
    assert ends.sum() * 3 == len(PVI), 'spider body is expected to be all triangles'
    corner_v = np.where(ends, ~PVI, PVI)
    le = find(geo, 'LayerElementNormal')[0][2]
    Nn = find(le, 'Normals')[0][1].reshape(-1, 3)
    lu = find(geo, 'LayerElementUV')[0][2]
    UVv = find(lu, 'UV')[0][1].reshape(-1, 2); UVi = find(lu, 'UVIndex')[0][1].astype(np.int64)
    lm = find(geo, 'LayerElementMaterial')[0][2]
    Mi = find(lm, 'Materials')[0][1].astype(int)
    pos = V[corner_v]; nrm = Nn; uv = UVv[UVi]
    # FBX is right handed with the model lying down (z-up export): stand it up (y = old z), centimetres -> metres
    R = np.array([[1, 0, 0], [0, 0, 1], [0, -1, 0]], float)
    pos = (pos @ R.T) * 0.01; nrm = nrm @ R.T
    pos[:, 0] *= -1; nrm[:, 0] *= -1
    uv = np.stack([uv[:, 0], uv[:, 1]], 1)      # fbx uv origin is bottom left like Unity
    pos[:, 1] -= pos[:, 1].min()
    pack = Pack()
    outdir = os.path.join(RES, 'Creatures', 'Textures')
    os.makedirs(outdir, exist_ok=True)
    fur = Image.open(os.path.join(texdir, 'IMG_7548.png')).convert('RGBA')
    face = Image.open(os.path.join(texdir, 'NO_EYES_CREATURE.png')).convert('RGB')
    fur.convert('RGB').save(os.path.join(outdir, 'spider_fur.png'))
    face.save(os.path.join(outdir, 'spider_face.png'))
    m_fur = pack.add_material(tex='Creatures/Textures/spider_fur', color=[1, 1, 1, 1], unlit=False, alpha=False)
    m_face = pack.add_material(tex='Creatures/Textures/spider_face', color=[1, 1, 1, 1], unlit=False, alpha=False)
    idx = np.arange(len(pos)).reshape(-1, 3)[:, [0, 2, 1]]
    uv[:, 1] = uv[:, 1]
    subs = []
    P2, N2, U2, I2 = weld(pos, nrm, uv, idx.ravel())
    I2 = I2.reshape(-1, 3)
    for mat_id, pm in ((0, m_fur), (1, m_face)):
        sel = I2[Mi == mat_id]
        if len(sel): subs.append((pm, sel.ravel()))
    P2[:, 0] -= (P2[:, 0].min() + P2[:, 0].max()) / 2
    z = (P2[:, 2].min() + P2[:, 2].max()) / 2
    P2[:, 2] -= z
    pack.add_chunk('spider', '', False, P2, N2, U2, subs)
    pack.extra['height'] = float(P2[:, 1].max())
    pack.save(os.path.join(RES, 'Creatures', 'spider.bytes'))
    print('spider size', P2.min(0), P2.max(0))


# ---------------------------------------------------------------- rigged player model (skinned glb -> bones + weights)
def pack_player(path):
    """gorilla.glb -> Resources/Rig/player.bytes ('GTR1': json | blob) with the skeleton (rest pose, Unity space), skin weights and mesh."""
    from scipy.spatial.transform import Rotation
    g = Glb(path)
    nodes = g.nodes
    parent = {}; world = {}

    def walk(i, P):
        W = P @ g.node_matrix(nodes[i]); world[i] = W
        for c in nodes[i].get('children', []):
            parent[c] = i; walk(c, W)
    for r in g.js['scenes'][0]['nodes']: walk(r, np.eye(4))
    skin = g.js['skins'][0]; joints = skin['joints']
    ibm = g.read(skin['inverseBindMatrices']).reshape(-1, 4, 4).transpose(0, 2, 1)
    M = np.diag([-1.0, 1.0, 1.0])
    pos_w, rot_w = [], []
    for j in joints:
        W = world[j]
        R = W[:3, :3] / np.linalg.norm(W[:3, 0])
        pos_w.append(M @ W[:3, 3]); rot_w.append(M @ R @ M)
    bones = []
    for k, j in enumerate(joints):
        par = joints.index(parent[j]) if parent.get(j) in joints else -1
        if par < 0: lp, lr = pos_w[k], rot_w[k]
        else: lp, lr = rot_w[par].T @ (pos_w[k] - pos_w[par]), rot_w[par].T @ rot_w[k]
        q = Rotation.from_matrix(lr).as_quat()
        bones.append({'name': nodes[j]['name'], 'parent': par, 'pos': [float(x) for x in lp], 'rot': [float(x) for x in q]})
    P_all, N_all, U_all, J_all, W_all, subs = [], [], [], [], [], []
    base = 0
    order = []
    for n in nodes:
        if 'mesh' not in n: continue
        for p in g.js['meshes'][n['mesh']]['primitives']: order.append(p)
    order.sort(key=lambda p: 0 if p['material'] == 2 else 1 if p['material'] == 0 else 2)   # primary fur first: it is the one that gets tinted
    outdir = os.path.join(RES, 'Rig')
    tex_of = {0: 'Rig/player_secondary', 1: 'Rig/player_face', 2: ''}
    for p in order:
        at = p['attributes']
        P = g.read(at['POSITION']).astype(float); N = g.read(at['NORMAL']).astype(float); U = g.read(at['TEXCOORD_0']).astype(float)
        J = g.read(at['JOINTS_0']).astype(np.uint8); Wt = g.read(at['WEIGHTS_0']).astype(np.float32)
        Wt /= np.maximum(Wt.sum(1, keepdims=True), 1e-9)
        idx = g.read(p['indices']).astype(np.int64).reshape(-1, 3)[:, [0, 2, 1]].ravel()
        P[:, 0] *= -1; N[:, 0] *= -1
        U = np.stack([U[:, 0], 1.0 - U[:, 1]], 1)
        P_all.append(P); N_all.append(N); U_all.append(U); J_all.append(J); W_all.append(Wt)
        subs.append({'count': int(len(idx)), 'tex': tex_of[p['material']], '_idx': idx + base}); base += len(P)
    P = np.vstack(P_all).astype('<f4'); N = np.vstack(N_all).astype('<f4'); U = np.vstack(U_all).astype('<f4')
    J = np.vstack(J_all).astype('u1'); Wt = np.vstack(W_all).astype('<f4')
    head = [i for i, b in enumerate(bones) if b['name'].startswith('head')][0]
    dom = J[np.arange(len(J)), Wt.argmax(1)]
    hv = P[dom == head]
    head_center = ((hv.min(0) + hv.max(0)) / 2) if len(hv) else np.array([0, 0.5, 0])
    os.makedirs(outdir, exist_ok=True)
    for i, name in ((0, 'player_secondary'), (1, 'player_face')):
        g.image(g.js['textures'][i]['source']).convert('RGB').save(os.path.join(outdir, name + '.png'))
    blob = bytearray(P.tobytes() + N.tobytes() + U.tobytes() + J.tobytes() + Wt.tobytes())
    for sdef in subs:
        blob += np.asarray(sdef.pop('_idx'), '<i4').tobytes()
    head_json = {'bones': bones, 'n': int(len(P)), 'headCenter': [float(x) for x in head_center], 'subs': subs}
    js = json.dumps(head_json, separators=(',', ':')).encode()
    with open(os.path.join(outdir, 'player.bytes'), 'wb') as f:
        f.write(b'GTR1' + struct.pack('<I', len(js)) + js + bytes(blob))
    # sanity: skinning the bind pose must give back the mesh
    chk = np.zeros((len(P), 3))
    for k in range(4):
        j = J[:, k]
        Wm = np.array([world[joints[i]] @ ibm[i] for i in range(len(joints))])
        pp = np.hstack([np.vstack([p_ for p_ in [g.read(pr['attributes']['POSITION']).astype(float) for pr in order]]), np.ones((len(P), 1))])
        chk += Wt[:, k:k + 1] * np.einsum('nij,nj->ni', Wm[j], pp)[:, :3]
    ref = np.vstack([g.read(pr['attributes']['POSITION']).astype(float) for pr in order])
    print('bind pose error', np.abs(chk - ref).max(), '| head centre', head_center, '| bones', len(bones), '| verts', len(P))


# ---------------------------------------------------------------- monke parts
def tag_monke():
    from scipy.sparse import coo_matrix
    from scipy.sparse.csgraph import connected_components
    path = os.path.join(RES, 'Rig', 'monke_mesh.txt')
    rows = [l.split() for l in open(path) if len(l.split()) >= 9]
    T = np.array([[float(x) for x in r[:9]] for r in rows])
    V = T.reshape(-1, 3)
    q = np.round(V / 1e-4).astype(np.int64)
    _, inv = np.unique(q, axis=0, return_inverse=True)
    inv = inv.ravel(); nv = inv.max() + 1
    f = inv.reshape(-1, 3)
    r_ = np.concatenate([f[:, 0], f[:, 1], f[:, 2]]); c_ = np.concatenate([f[:, 1], f[:, 2], f[:, 0]])
    ncomp, lab = connected_components(coo_matrix((np.ones(len(r_)), (r_, c_)), shape=(nv, nv)), directed=False)
    fl = lab[f[:, 0]]
    cen = T.reshape(-1, 3, 3).mean(1)
    info = []
    for c in range(ncomp):
        m = fl == c
        info.append((c, cen[m, 0].mean(), cen[m, 1].min(), cen[m, 1].max(), m.sum()))
    body = min(info, key=lambda t: abs(t[1]) - t[4] * 1e-3)[0]
    part = {body: 0}
    for side, (a, h) in (('L', (1, 2)), ('R', (3, 4))):
        comps = [t for t in info if t[0] != body and ((t[1] < 0) == (side == 'L'))]
        comps.sort(key=lambda t: -(t[3] - t[2]))      # the long one is the upper arm, the short low one is the hand
        part[comps[0][0]] = a; part[comps[1][0]] = h
    with open(path, 'w') as out:
        for k, r in enumerate(rows):
            out.write(' '.join(r[:9]) + ' %d\n' % part[fl[k]])
    print('tagged', collections.Counter(part[c] for c in fl))


if __name__ == '__main__':
    cmd = sys.argv[1]
    if cmd == 'map': pack_map(sys.argv[2])
    elif cmd == 'timmy': pack_timmy(sys.argv[2])
    elif cmd == 'spider': pack_spider(sys.argv[2], sys.argv[3])
    elif cmd == 'player': pack_player(sys.argv[2])
    elif cmd == 'monke': tag_monke()
    else: print(__doc__)
