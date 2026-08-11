# Blender headless: Meta'nin el FBX'leri ile bizim FP_Hands iskeletini karsilastir.
# Cikti: kemik listesi, hiyerarsi, mesh boyutu, olcek - hizalama planini kurmak icin.
import bpy, sys, mathutils

FILES = sys.argv[sys.argv.index("--") + 1:]


def rapor(path):
    print("=" * 70)
    print("DOSYA:", path)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    try:
        bpy.ops.import_scene.fbx(filepath=path)
    except Exception as e:
        print("IMPORT HATASI:", e)
        return

    for ob in bpy.data.objects:
        if ob.type == 'ARMATURE':
            arm = ob
            print("  ARMATURE:", ob.name, "scale", tuple(round(v, 4) for v in ob.scale))
            bones = arm.data.bones
            print("  kemik sayisi:", len(bones))
            for b in bones:
                par = b.parent.name if b.parent else "-"
                h = b.head_local
                print("    %-28s <- %-24s head=(%.4f, %.4f, %.4f) len=%.4f"
                      % (b.name, par, h.x, h.y, h.z, b.length))
        elif ob.type == 'MESH':
            me = ob.data
            print("  MESH:", ob.name, "vert", len(me.vertices), "poly", len(me.polygons))
            print("       scale", tuple(round(v, 4) for v in ob.scale))
            co = [ob.matrix_world @ v.co for v in me.vertices]
            mn = mathutils.Vector((min(c.x for c in co), min(c.y for c in co), min(c.z for c in co)))
            mx = mathutils.Vector((max(c.x for c in co), max(c.y for c in co), max(c.z for c in co)))
            print("       bbox min (%.4f, %.4f, %.4f)  max (%.4f, %.4f, %.4f)  boyut (%.4f, %.4f, %.4f)"
                  % (mn.x, mn.y, mn.z, mx.x, mx.y, mx.z, mx.x - mn.x, mx.y - mn.y, mx.z - mn.z))
            print("       vertex grup sayisi:", len(ob.vertex_groups))
            print("       UV katman:", [l.name for l in me.uv_layers])
            print("       malzeme:", [m.name if m else None for m in me.materials])


for f in FILES:
    rapor(f)
print("BITTI")
