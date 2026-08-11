using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Attaches the first-person hands model (FP_Hands: glove + watch cut at the wrist,
    /// skin weights locked to the hand/finger bones) onto a spawned avatar and rebinds its
    /// SkinnedMeshRenderers to the avatar's LIVE skeleton by bone name. The FP meshes then
    /// follow everything that already drives those bones — arm IK, ProceduralFingerPoser,
    /// WeaponHandWeld — with zero extra per-frame code.
    ///
    /// The model is loaded from Resources (same convention as the weapon grip profiles),
    /// so no scene or prefab wiring is needed. If the asset is missing or the skeletons
    /// don't match, <see cref="TryAttach"/> returns null and the caller keeps the old
    /// glove|watch visibility path.
    /// </summary>
    public static class FirstPersonHands
    {
        const string ResourcePath = "FPHands/FP_Hands";

        // FBX'in Glove_FP mesh'i parmak bogumlarinda TEK kemige katili bagli (vertexlerin
        // %55'i) — parmak kivrilinca bogumlarda keskin kirilmanin sebebi. Duzeltilmis
        // agirliklarla TURETILMIS kopya (uretimi: GripOlcum/DEVAM.md'deki reweight adimi);
        // FBX'e dokunmamak icin degisim burada, kurulum sirasinda yapiliyor. Asset yoksa
        // orijinal mesh aynen kalir.
        const string ReweightedGlovePath = "FPHands/Glove_FP_Reweighted";

        // Eldivenin kendisi de degisti: Meta XR Core SDK'nin el mesh'i bizim iskelete
        // eklem eklem oturtuldu (uretimi: GripOlcum/blender_meta_hand_retarget.py).
        // Eski eldiven 59 ayri ada oldugu icin hicbir agirlik semasi yumrukta duzgun
        // kapanmiyordu; bu mesh tek parca ve agirliklari Meta'nin kendi sanatcilarindan
        // geliyor. Meta'nin UV'si bizim eldiven dokusuna uymaz - kendi malzemesiyle
        // gelir. Asset yoksa yukaridaki eski eldivene duser.
        const string MetaHandMeshPath = "FPHands/Glove_FP_MetaHand";
        const string MetaHandMaterialPath = "FPHands/M_FP_MetaHand";

        /// <summary>
        /// Instantiates FP_Hands under <paramref name="avatarRoot"/> and retargets its
        /// renderers onto the avatar's skeleton. Returns the instance, or null on any
        /// mismatch (caller should fall back to the current visibility behaviour).
        /// </summary>
        public static GameObject TryAttach(GameObject avatarRoot)
        {
            if (avatarRoot == null)
                return null;

            var prefab = Resources.Load<GameObject>(ResourcePath);
            if (prefab == null)
                return null;

            // Avatar transform lookup by name. Bone names are unique in this rig; first
            // hit wins so stray same-named helpers can't shadow the real bones.
            var map = new Dictionary<string, Transform>();
            foreach (var t in avatarRoot.GetComponentsInChildren<Transform>(true))
                if (!map.ContainsKey(t.name))
                    map.Add(t.name, t);

            var instance = Object.Instantiate(prefab, avatarRoot.transform, false);
            instance.name = "FP_Hands";

            var metaHand = Resources.Load<Mesh>(MetaHandMeshPath);
            var metaHandMaterial = Resources.Load<Material>(MetaHandMaterialPath);
            var reweighted = Resources.Load<Mesh>(ReweightedGlovePath);

            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                // Bindpose'lar kopyada birebir ayni oldugu icin kemik yeniden baglamadan
                // once/sonra atamak farksiz — burada, dongunun en basinda yapiliyor.
                if (smr.name == "Glove_FP")
                {
                    if (metaHand != null)
                    {
                        smr.sharedMesh = metaHand;
                        // Tek alt-mesh: malzeme dizisi de tek olmali, yoksa ikinci
                        // malzeme (FP_GloveCap) bosa asili kalir.
                        if (metaHandMaterial != null)
                            smr.sharedMaterials = new[] { metaHandMaterial };
                    }
                    else if (reweighted != null)
                    {
                        smr.sharedMesh = reweighted;
                    }
                }

                var src = smr.bones;
                var dst = new Transform[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    if (src[i] == null || !map.TryGetValue(src[i].name, out dst[i]))
                    {
                        Debug.LogWarning(
                            $"[FirstPersonHands] Bone '{(src[i] != null ? src[i].name : "<null>")}' " +
                            "not found on avatar skeleton - falling back to default hands.");
                        Object.Destroy(instance);
                        return null;
                    }
                }

                if (smr.rootBone == null || !map.TryGetValue(smr.rootBone.name, out var root))
                {
                    Debug.LogWarning("[FirstPersonHands] Root bone not found on avatar skeleton - falling back.");
                    Object.Destroy(instance);
                    return null;
                }

                smr.bones = dst;
                smr.rootBone = root;

                // Same fixes the avatar gloves get today: live bounds so raised hands
                // don't get frustum-culled, and double-sided faces so the mesh never
                // reads as a hole when clipped into.
                smr.updateWhenOffscreen = true;
                MaterialDoubleSided.Apply(smr);
            }

            // The instance's own armature is dead weight once the renderers point at the
            // avatar's bones - drop it so nothing ever drives or poses it by accident.
            var ownSkeleton = instance.transform.Find("Skeleton");
            if (ownSkeleton != null)
                Object.Destroy(ownSkeleton.gameObject);

            return instance;
        }
    }
}
