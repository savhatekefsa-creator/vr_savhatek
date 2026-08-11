# Blender headless: FP_Hands eldiveninin parmak agirliklarini topoloji-farkinda yumusat.
# Calistirma: blender --background --python glove_reweight.py
#
# Neden bu yol: Unity icindeki geometrik harmanlama (mesafe tabanli) topolojiyi bilmiyor
# ve cihazda yetersiz kaldi. Blender'in vertex_group_smooth'u agirliklari BAGLI komsular
# uzerinden ortalar — bogum kirilmasinin dogru ilaci.
#
# Cikti: FP_Hands_Blender.fbx (ayni klasore). Orijinal FBX'e DOKUNULMAZ.
import bpy, re, sys

SRC = r"C:\Users\BCE\savhateks\Assets\_VRMultiplayer\Resources\FPHands\FP_Hands.fbx"
DST = r"C:\Users\BCE\savhateks\Assets\_VRMultiplayer\Resources\FPHands\FP_Hands_Blender.fbx"

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=SRC)

print("OBJELER:", [(o.name, o.type) for o in bpy.data.objects])

glove = None
for ob in bpy.data.objects:
    if ob.type == 'MESH' and 'Glove' in ob.name:
        glove = ob
        break
if glove is None:
    print("HATA: Glove mesh bulunamadi")
    sys.exit(1)

finger_rx = re.compile(r'(Thumb|Index|Middle|Ring|Pinky)(Proximal|Intermediate|Distal)$')
finger_groups = {i for i, g in enumerate(glove.vertex_groups) if finger_rx.search(g.name)}
print("PARMAK VERTEX GRUBU:", len(finger_groups), "(beklenen 30)")
if len(finger_groups) < 20:
    print("HATA: grup adlari beklenen kaliba uymuyor")
    print("GRUPLAR:", [g.name for g in glove.vertex_groups])
    sys.exit(1)

# Parmak bolgesi vertexlerini sec (baskin etkisi parmak kemigi olanlar).
bpy.context.view_layer.objects.active = glove
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='DESELECT')
bpy.ops.object.mode_set(mode='OBJECT')
sel = 0
for v in glove.data.vertices:
    for ge in v.groups:
        if ge.group in finger_groups and ge.weight > 0.2:
            v.select = True
            sel += 1
            break
print("SECILEN VERTEX:", sel)
if sel == 0:
    print("HATA: hic parmak vertexi secilmedi")
    sys.exit(1)

# Secim maskeli topolojik yumusatma: parmak disi bolgeye dokunulmaz.
bpy.ops.object.mode_set(mode='WEIGHT_PAINT')
glove.data.use_paint_mask_vertex = True
bpy.ops.object.vertex_group_smooth(group_select_mode='ALL', factor=0.5, repeat=6, expand=0.15)

# Smooth sonrasi toplamlar 1'den sapabilir; normalize oranlari korur, toplami 1 yapar.
bpy.ops.object.vertex_group_normalize_all(group_select_mode='ALL', lock_active=False)
bpy.ops.object.mode_set(mode='OBJECT')

# add_leaf_bones=False SART: yoksa her kemige "_end" cocugu eklenir ve Unity'de
# isimle yeniden baglama fazladan kemiklere takilir.
bpy.ops.export_scene.fbx(
    filepath=DST,
    add_leaf_bones=False,
    apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_ALL',
    object_types={'ARMATURE', 'MESH'},
)
print("EXPORT OK:", DST)
