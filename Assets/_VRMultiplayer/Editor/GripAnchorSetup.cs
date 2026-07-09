using UnityEditor;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// Adds a grip anchor ("GripPoint") to the selected weapon so the hand holds it by the KABZA
    /// instead of snapping the pivot into the palm. A first estimate is placed (a bit behind the
    /// center, below the barrel line, +Z pointing down the barrel); drag it in the Scene view onto
    /// the real handle and re-grab in game.
    ///   Tools ▸ VR Multiplayer ▸ 22. Kabza Noktasi Olustur
    /// </summary>
    public static class GripAnchorSetup
    {
        [MenuItem("Tools/VR Multiplayer/22. Kabza Noktasi Olustur (secili silah)")]
        static void CreateGripAnchor()
        {
            var sel = Selection.activeGameObject;
            var grab = sel != null ? sel.GetComponentInParent<GrabbableObject>() : null;
            if (grab == null)
            {
                EditorUtility.DisplayDialog("Kabza Noktasi",
                    "Once sahnedeki silahi sec. Uzerinde GrabbableObject olmali (menu 15 ya da 10 ile eklenir).",
                    "Tamam");
                return;
            }

            Transform anchor = grab.gripAnchor;
            if (anchor == null)
            {
                var existing = grab.transform.Find("GripPoint");
                if (existing != null) anchor = existing;
                else
                {
                    anchor = new GameObject("GripPoint").transform;
                    anchor.SetParent(grab.transform, false);
                    Undo.RegisterCreatedObjectUndo(anchor.gameObject, "Create GripPoint");
                }
            }

            EstimatePose(grab, anchor);

            grab.snapToHand = true;
            grab.gripAnchor = anchor;
            EditorUtility.SetDirty(grab);
            EditorUtility.SetDirty(anchor.gameObject);
            Selection.activeTransform = anchor;

            Debug.Log("[Kabza] GripPoint olusturuldu/guncellendi. Scene view'da tam kabzaya surukle, " +
                      "mavi Z okunu namlu yonune cevir, sonra oyunda birak-tekrar tut.");
            EditorUtility.DisplayDialog("Kabza Noktasi",
                "GripPoint eklendi ve secildi.\n\n" +
                "1) Scene view'da GripPoint'i tam KABZAYA surukle (W tusu = tasima).\n" +
                "2) Mavi Z okunu namlu yonune cevir (E tusu = dondurme).\n" +
                "3) Oyunda silahi birak-tekrar tut.\n\n" +
                "Not: Kalici olsun istiyorsan Inspector'da prefab Overrides > Apply All.",
                "Tamam");
        }

        // First guess: biggest mesh = gun body. Barrel = its longest axis; grip sits a bit behind
        // the center and half a height below the barrel line, oriented barrel-forward.
        static void EstimatePose(GrabbableObject grab, Transform anchor)
        {
            MeshFilter biggest = null;
            float big = 0f;
            foreach (var mf in grab.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                float s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).sqrMagnitude;
                if (s > big) { big = s; biggest = mf; }
            }
            if (biggest == null)
            {
                anchor.localPosition = Vector3.zero;
                anchor.localRotation = Quaternion.identity;
                return;
            }

            Bounds mb = biggest.sharedMesh.bounds;
            Vector3 centerW = biggest.transform.TransformPoint(mb.center);
            Vector3 sizeW = Vector3.Scale(mb.size, biggest.transform.lossyScale);

            Vector3[] ax = { biggest.transform.right, biggest.transform.up, biggest.transform.forward };
            float[] ln = { Mathf.Abs(sizeW.x), Mathf.Abs(sizeW.y), Mathf.Abs(sizeW.z) };
            int bi = 0;
            if (ln[1] > ln[bi]) bi = 1;
            if (ln[2] > ln[bi]) bi = 2;

            // Muzzle side = where the mesh bulk lies relative to the pivot.
            float sign = Mathf.Sign(Vector3.Dot(centerW - grab.transform.position, ax[bi]));
            if (sign == 0f) sign = 1f;
            Vector3 muzzle = (ax[bi] * sign).normalized;
            float length = ln[bi];

            // Height ~ the non-barrel local axis most aligned with world up.
            float height = 0f;
            for (int i = 0; i < 3; i++)
            {
                if (i == bi) continue;
                if (Mathf.Abs(Vector3.Dot(ax[i].normalized, Vector3.up)) > 0.5f) height = ln[i];
            }
            if (height <= 0f) height = Mathf.Min(ln[(bi + 1) % 3], ln[(bi + 2) % 3]);

            Vector3 up = Mathf.Abs(Vector3.Dot(muzzle, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            anchor.position = centerW - muzzle * (length * 0.12f) + Vector3.down * (height * 0.5f);
            anchor.rotation = Quaternion.LookRotation(muzzle, up);
        }
    }
}
