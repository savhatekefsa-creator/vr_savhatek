# Blender headless: Glove_FP icin SIFIRDAN otomatik agirlik (bone heat).
# Neden: mesh 59 ayri ada (panel/yama yapisi). Topolojik yumusatma adalar arasinda
# calismaz; bone heat UZAMSAL oldugu icin komsu adalara tutarli agirlik verir.
import bpy, sys

SRC = r"C:\Users\BCE\savhateks\Assets\_VRMultiplayer\Resources\FPHands\FP_Hands.fbx"
DST = r"C:\Users\BCE\savhateks\Assets\_VRMultiplayer\Resources\FPHands\FP_Hands_Auto.fbx"

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=SRC)

glove = None
arm = None
for ob in bpy.data.objects:
    if ob.type == 'MESH' and 'Glove' in ob.name:
        glove = ob
    if ob.type == 'ARMATURE':
        arm = ob
if glove is None or arm is None:
    print("HATA: glove/armature yok"); sys.exit(1)

# Eski gruplar tamamen gitsin - kismi kalinti karisikligi olmasin.
glove.vertex_groups.clear()

bpy.ops.object.select_all(action='DESELECT')
glove.select_set(True)
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.parent_set(type='ARMATURE_AUTO')
print("OTOMATIK AGIRLIK BITTI; grup sayisi:", len(glove.vertex_groups))
if len(glove.vertex_groups) < 10:
    print("HATA: cok az grup - bone heat basarisiz olabilir"); sys.exit(1)

bpy.ops.export_scene.fbx(
    filepath=DST,
    add_leaf_bones=False,
    apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_ALL',
    object_types={'ARMATURE', 'MESH'},
)
print("EXPORT OK:", DST)
