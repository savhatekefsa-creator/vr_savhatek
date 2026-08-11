# Blender headless: Meta XR Core SDK'nin el mesh'ini BIZIM iskelete oturtur.
#
# Yontem (bind-pose yeniden hedefleme):
#   Her kemik icin cerceve GEOMETRIDEN kurulur - Blender'in FBX'te tahmin ettigi
#   "tail" yonune ASLA guvenilmez (iki iskelette farkli tahmin eder, eller burulur).
#     X = normalize(sonraki eklem - bu eklem)      <- iki iskelette ayni anatomik tanim
#     Z = normalize(X x avuc_normali)
#     Y = Z x X
#   Vertexler Meta'nin KENDI agirliklariyla harmanlanarak tasinir:
#     v' = sum_b w_b * ( Translate(o_head) . Ro . s . Rm^T . Translate(-m_head) ) . v
#
#   Gercek karsiligi olmayan kemikler (forearm_stub = mansat, pinky0 = serce
#   metakarpi, *_null = uc) KENDI cercevelerini kullanamaz: karsi iskelette
#   eslesen eklem baska yerdedir. Bunlar en yakin gercek eslesmenin cercevesiyle
#   tasinir, agirlik hedefi ise ayri tutulur. (forearm_stub'i Right_LowerArm'in
#   BASINA tasimak mansati dirsege firlatiyordu - bu yuzden ayrim sart.)
#
# SCALE_MODE:
#   global  - tek kuresel olcek. Parmaklar bizim uzun kemiklerimize gerilir (incelir).
#   perbone - her segment kendi oraniyla. Parmaklar orantili ama kalinlasir.
#   mid     - ikisinin geometrik ortasi.
import bpy, sys, json, struct
import numpy as np
from mathutils import Vector, Matrix

ARGS = sys.argv[sys.argv.index("--") + 1:]
OURS_FBX, META_R_FBX, META_L_FBX, OUT_BASE, SCALE_MODE = ARGS[:5]

# meta kemik -> (cerceve: meta baslangic/bitis eklemi, bizim baslangic/bitis, agirlik hedefi)
def chain(pre, ours_pre):
    d = {}
    m = [pre + "1", pre + "2", pre + "3", pre + "_null"]
    o = [ours_pre + "Proximal", ours_pre + "Intermediate", ours_pre + "Distal", ours_pre + "DistalEnd"]
    for i in range(3):
        d[m[i]] = ((m[i], m[i + 1]), (o[i], o[i + 1]), o[i])
    d[m[3]] = ((m[2], m[3]), (o[2], o[3]), o[3])   # uc: bir onceki cerceve
    return d

WRIST_FRAME = (("wrist", "middle1"), ("{S}_Hand", "{S}_MiddleProximal"))

DEF = {}
DEF["wrist"] = (WRIST_FRAME[0], WRIST_FRAME[1], "{S}_Hand")
DEF["forearm_stub"] = (WRIST_FRAME[0], WRIST_FRAME[1], "{S}_LowerArm")
DEF["pinky0"] = (WRIST_FRAME[0], WRIST_FRAME[1], "{S}_Hand")
# bas parmak: meta thumb0..thumb3(+null) -> bizde 4 eklem
DEF["thumb0"] = (("thumb0", "thumb1"), ("{S}_ThumbProximal", "{S}_ThumbIntermediate"), "{S}_ThumbProximal")
DEF["thumb1"] = (("thumb1", "thumb2"), ("{S}_ThumbIntermediate", "{S}_ThumbDistal"), "{S}_ThumbIntermediate")
DEF["thumb2"] = (("thumb2", "thumb3"), ("{S}_ThumbDistal", "{S}_ThumbDistalEnd"), "{S}_ThumbDistal")
DEF["thumb3"] = (("thumb2", "thumb3"), ("{S}_ThumbDistal", "{S}_ThumbDistalEnd"), "{S}_ThumbDistalEnd")
DEF["thumb_null"] = DEF["thumb3"]
for _p, _o in (("index", "{S}_Index"), ("middle", "{S}_Middle"), ("ring", "{S}_Ring")):
    DEF.update(chain(_p, _o))
# serce: meta pinky1/2/3 -> bizim Proximal/Intermediate/Distal
DEF["pinky1"] = (("pinky1", "pinky2"), ("{S}_PinkyProximal", "{S}_PinkyIntermediate"), "{S}_PinkyProximal")
DEF["pinky2"] = (("pinky2", "pinky3"), ("{S}_PinkyIntermediate", "{S}_PinkyDistal"), "{S}_PinkyIntermediate")
DEF["pinky3"] = (("pinky3", "pinky_null"), ("{S}_PinkyDistal", "{S}_PinkyDistalEnd"), "{S}_PinkyDistal")
DEF["pinky_null"] = (("pinky3", "pinky_null"), ("{S}_PinkyDistal", "{S}_PinkyDistalEnd"), "{S}_PinkyDistalEnd")


def log(*a):
    print("[retarget]", *a)


def meta_key(name):
    return name[4:] if name.startswith("b_r_") or name.startswith("b_l_") else name


def import_new(path):
    before = set(o.name for o in bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    return [o for o in bpy.data.objects if o.name not in before]


def frame_of(a, b, palm_normal):
    x = (b - a)
    x = x.normalized() if x.length > 1e-9 else Vector((1.0, 0.0, 0.0))
    z = x.cross(palm_normal)
    if z.length < 1e-6:
        alt = Vector((0.0, 0.0, 1.0)) if abs(x.z) < 0.9 else Vector((1.0, 0.0, 0.0))
        z = x.cross(alt)
    z = z.normalized()
    y = z.cross(x).normalized()
    m = Matrix().to_3x3()
    m[0][0], m[1][0], m[2][0] = x
    m[0][1], m[1][1], m[2][1] = y
    m[0][2], m[1][2], m[2][2] = z
    return m


bpy.ops.wm.read_factory_settings(use_empty=True)
ours_objs = import_new(OURS_FBX)
ours_arm = next((o for o in ours_objs if o.type == 'ARMATURE'), None)
for o in list(ours_objs):
    if o.type == 'MESH':
        bpy.data.objects.remove(o, do_unlink=True)
log("bizim iskelet:", ours_arm.name, len(ours_arm.data.bones), "kemik | olcek modu:", SCALE_MODE)

out_parts = []
snap_lines = []
for side, fbx in (("R", META_R_FBX), ("L", META_L_FBX)):
    S = "Right" if side == "R" else "Left"
    objs = import_new(fbx)
    m_arm = next((o for o in objs if o.type == 'ARMATURE'), None)
    m_obj = next((o for o in objs if o.type == 'MESH'), None)

    mmw, omw = m_arm.matrix_world, ours_arm.matrix_world
    m_head = {meta_key(b.name): mmw @ b.head_local for b in m_arm.data.bones}
    o_head = {b.name: omw @ b.head_local for b in ours_arm.data.bones}

    m_palm = (m_head["index1"] - m_head["wrist"]).cross(m_head["pinky1"] - m_head["wrist"]).normalized()
    o_palm = (o_head[S + "_IndexProximal"] - o_head[S + "_Hand"]).cross(
              o_head[S + "_PinkyProximal"] - o_head[S + "_Hand"]).normalized()

    # Kuresel olcek AVUCTAN olculur: bilek -> her MCP mesafesi. Parmak boyu
    # oranlarini karistirmak olceği sisiriyor (uc falankslarda Meta iskeleti
    # mesh'in ucundan once bitiyor, oran 2.7'ye kadar yalan soyluyor).
    # Bas parmak DISARIDA: rig'de asimetrik ve fazla disarida yerlestirilmis
    # (oran sagda 1.54, solda 2.27) - ortalamayi sisiriyor.
    mcp = [("index1", "{S}_IndexProximal"), ("middle1", "{S}_MiddleProximal"),
           ("ring1", "{S}_RingProximal"), ("pinky1", "{S}_PinkyProximal")]
    rs = [ (o_head[on.format(S=S)] - o_head[S + "_Hand"]).length /
           (m_head[mn] - m_head["wrist"]).length for mn, on in mcp ]
    g_scale = sum(rs) / len(rs)
    log(side, "avuc olcegi = %.4f  (MCP oranlari: %s)" % (g_scale, ", ".join("%.2f" % r for r in rs)))

    # Parmak basina olcek: MCP -> DIP acikligi (uc eklem disarida, guvenilmez)
    span = {"index": ("index1", "index3", "{S}_IndexProximal", "{S}_IndexDistal"),
            "middle": ("middle1", "middle3", "{S}_MiddleProximal", "{S}_MiddleDistal"),
            "ring": ("ring1", "ring3", "{S}_RingProximal", "{S}_RingDistal"),
            "pinky": ("pinky1", "pinky3", "{S}_PinkyProximal", "{S}_PinkyDistal"),
            "thumb": ("thumb0", "thumb2", "{S}_ThumbProximal", "{S}_ThumbDistal")}
    finger_scale = {}
    for f, (ma, mb, oa, ob) in span.items():
        finger_scale[f] = ((o_head[ob.format(S=S)] - o_head[oa.format(S=S)]).length /
                           (m_head[mb] - m_head[ma]).length)
    if side == "R":
        log("  parmak olcekleri:", ", ".join("%s=%.2f" % (k, v) for k, v in finger_scale.items()))

    xforms, target_of = {}, {}
    ratios = []
    for b in m_arm.data.bones:
        mk = meta_key(b.name)
        if mk not in DEF:
            continue
        mp, op, tgt = DEF[mk]
        of, ot = op[0].format(S=S), op[1].format(S=S)
        mv = m_head[mp[1]] - m_head[mp[0]]
        ov = o_head[ot] - o_head[of]
        r = ov.length / mv.length if mv.length > 1e-9 else g_scale
        # Son segment (uca giden) oranlari YALAN: Meta iskeleti mesh'in ucundan
        # once bitiyor, oran 1.85-2.67 cikiyor. Yalnizca onlari kirp.
        r_len = min(r, 1.5) if mp[1].endswith("_null") else r
        if SCALE_MODE == "perbone":
            s = (r_len, r_len, r_len)
        elif SCALE_MODE == "perfinger":
            fam = next((f for f in finger_scale if mk.startswith(f)), None)
            v = finger_scale[fam] if fam else g_scale
            s = (v, v, v)
        elif SCALE_MODE == "aniso":
            # Uzatma YALNIZ kemik ekseni boyunca; kesit avuc olceginde kalir.
            # Tek-tip olcek basparmagi (oran 1.67-2.09) sisiriyordu.
            s = (r_len, g_scale, g_scale)
        else:
            s = (g_scale, g_scale, g_scale)
        ratios.append((mk, r))
        Rm = frame_of(m_head[mp[0]], m_head[mp[1]], m_palm)
        Ro = frame_of(o_head[of], o_head[ot], o_palm)
        S3 = Matrix.Diagonal(Vector(s))
        M3 = Ro @ S3 @ Rm.transposed()
        xforms[b.name] = Matrix.Translation(o_head[of]) @ M3.to_4x4() @ Matrix.Translation(-m_head[mp[0]])
        target_of[b.name] = tgt.format(S=S)
    if side == "R":
        log("  segment oranlari (bizim/meta):", ", ".join("%s=%.2f" % (k, v) for k, v in ratios))

    # SNAP: mesh'e HIC dokunma - tek kati donusumle (bilek cercevesi + avuc
    # olcegi) yerine kondur, sonra BIZIM parmak kemiklerini Meta'nin eklem
    # konumlarina tasi. Rig duzeldigi icin gerilme/incelme/sisme kalmaz.
    if SCALE_MODE == "snap":
        Rw = frame_of(o_head[S + "_Hand"], o_head[S + "_MiddleProximal"], o_palm) @ \
             (frame_of(m_head["wrist"], m_head["middle1"], m_palm).transposed() * g_scale)
        T_snap = Matrix.Translation(o_head[S + "_Hand"]) @ Rw.to_4x4() @ Matrix.Translation(-m_head["wrist"])
        for b in m_arm.data.bones:
            mk = meta_key(b.name)
            if mk in DEF:
                xforms[b.name] = T_snap
        # duzeltilmis eklem konumlari (bizim kemik adi -> Meta ekleminin yeni yeri)
        SNAP_JOINT = {
            "{S}_ThumbProximal": "thumb0", "{S}_ThumbIntermediate": "thumb1",
            "{S}_ThumbDistal": "thumb2", "{S}_ThumbDistalEnd": "thumb3",
        }
        for _f, _o in (("index", "Index"), ("middle", "Middle"), ("ring", "Ring"), ("pinky", "Pinky")):
            n1 = "1" if _f != "pinky" else "1"
            SNAP_JOINT["{S}_" + _o + "Proximal"] = _f + n1
            SNAP_JOINT["{S}_" + _o + "Intermediate"] = _f + "2"
            SNAP_JOINT["{S}_" + _o + "Distal"] = _f + "3"
            SNAP_JOINT["{S}_" + _o + "DistalEnd"] = _f + "_null"
        for on, mn in SNAP_JOINT.items():
            p = T_snap @ m_head[mn]
            snap_lines.append("%s %.8f %.8f %.8f" % (on.format(S=S), p.x, p.y, p.z))
        log(side, "snap: %d parmak eklemi yeniden konumlandirilacak" % len(SNAP_JOINT))

    me = m_obj.data
    mwv = m_obj.matrix_world
    vg_name = {vg.index: vg.name for vg in m_obj.vertex_groups}
    wrist_bone = [n for n in xforms if meta_key(n) == "wrist"][0]

    new_co, weights_out, unmapped = [], [], set()
    for v in me.vertices:
        acc, wsum = {}, 0.0
        for g in v.groups:
            gn = vg_name.get(g.group)
            if gn is None or g.weight <= 0.0:
                continue
            if gn not in xforms:
                unmapped.add(gn); continue
            acc[gn] = acc.get(gn, 0.0) + g.weight
            wsum += g.weight
        if wsum <= 1e-8:
            acc, wsum = {wrist_bone: 1.0}, 1.0
        p = mwv @ v.co
        out = Vector((0.0, 0.0, 0.0))
        for bn, w in acc.items():
            out += (xforms[bn] @ p) * (w / wsum)
        new_co.append(out)
        merged = {}
        for bn, w in acc.items():
            tn = target_of[bn]
            merged[tn] = merged.get(tn, 0.0) + w / wsum
        items = sorted(merged.items(), key=lambda kv: -kv[1])[:4]
        tot = sum(w for _, w in items) or 1.0
        weights_out.append([[n, w / tot] for n, w in items])
    if unmapped:
        log(side, "UYARI eslenmemis grup:", sorted(unmapped))

    for i, v in enumerate(me.vertices):
        v.co = new_co[i]
    m_obj.matrix_world = Matrix.Identity(4)
    bpy.ops.object.select_all(action='DESELECT')
    m_obj.select_set(True)
    bpy.context.view_layer.objects.active = m_obj
    try:
        bpy.ops.mesh.customdata_custom_splitnormals_clear()
    except Exception:
        pass
    for p in me.polygons:
        p.use_smooth = True
    me.update()
    me.calc_loop_triangles()

    uvl = me.uv_layers.active.data if me.uv_layers.active else None
    corner_n = [tuple(n.vector) for n in me.corner_normals] if hasattr(me, "corner_normals") else None
    vnorm = [tuple(v.normal) for v in me.vertices]
    verts, tris, wts, seen = [], [], [], {}
    for lt in me.loop_triangles:
        for k in range(3):
            vi, li = lt.vertices[k], lt.loops[k]
            uv = tuple(uvl[li].uv) if uvl else (0.0, 0.0)
            nn = corner_n[li] if corner_n else vnorm[vi]
            key = (vi, round(uv[0], 6), round(uv[1], 6), round(nn[0], 4), round(nn[1], 4), round(nn[2], 4))
            j = seen.get(key)
            if j is None:
                j = len(verts); seen[key] = j
                co = me.vertices[vi].co
                verts.append([co.x, co.y, co.z, nn[0], nn[1], nn[2], uv[0], uv[1]])
                wts.append(weights_out[vi])
            tris.append(j)
    log(side, "cikti: %d vertex, %d ucgen" % (len(verts), len(tris) // 3))
    out_parts.append({"verts": verts, "tris": tris, "weights": wts})
    for o in objs:
        try:
            bpy.data.objects.remove(o, do_unlink=True)
        except Exception:
            pass

names, name_idx = [], {}
for part in out_parts:
    for wl in part["weights"]:
        for n, _ in wl:
            if n not in name_idx:
                name_idx[n] = len(names); names.append(n)
buf = bytearray(b"MHND")
buf += struct.pack("<i", len(out_parts)) + struct.pack("<i", len(names))
for n in names:
    b = n.encode("utf-8"); buf += struct.pack("<i", len(b)) + b
for part in out_parts:
    buf += struct.pack("<i", len(part["verts"]))
    for row in part["verts"]:
        buf += struct.pack("<8f", *row)
    buf += struct.pack("<i", len(part["tris"]))
    buf += struct.pack("<%di" % len(part["tris"]), *part["tris"])
    for wl in part["weights"]:
        buf += struct.pack("<B", len(wl))
        for n, w in wl:
            buf += struct.pack("<if", name_idx[n], w)
out = OUT_BASE + "_" + SCALE_MODE + ".bin"
with open(out, "wb") as f:
    f.write(buf)
log("BIN yazildi:", out, len(buf), "bayt")
if snap_lines:
    jf = OUT_BASE + "_snap_joints.txt"
    with open(jf, "w") as f:
        f.write("\n".join(snap_lines))
    log("EKLEM TABLOSU yazildi:", jf, len(snap_lines), "satir (Blender uzayi)")
log("BITTI")
