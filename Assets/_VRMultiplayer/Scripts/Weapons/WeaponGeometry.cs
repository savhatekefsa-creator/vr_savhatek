using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Silah geometrisi sozlesmesinin TEK kaynagi: "en buyuk mesh'in en uzun OLCEKLI ekseni =
    /// namlu dogrusu; kutlenin pivot'a gore topladigi taraf = namlu agzi tarafi". Ayni kural
    /// 6 yerde elle kopyaliydi (NetworkWeapon, HandGrabber + 4 editor araci) ve kopyalar
    /// birbirinden saparsa nisan ile atis yonu ayrisir. Kopyalardan biri duzelirse hepsi
    /// buradan duzelir.
    /// </summary>
    public static class WeaponGeometry
    {
        /// <summary>Kok altindaki en buyuk (olcekli bounds buyuklugune gore) MeshFilter;
        /// mesh yoksa null.</summary>
        public static MeshFilter FindBiggestMesh(Transform root)
        {
            MeshFilter biggest = null;
            float biggestSize = 0f;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                float s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).sqrMagnitude;
                if (s > biggestSize) { biggestSize = s; biggest = mf; }
            }
            return biggest;
        }

        /// <summary>Bounds'un en uzun OLCEKLI ana ekseni (mesh-lokal) ve o eksendeki extent.</summary>
        public static Vector3 LongestLocalAxis(Bounds mb, Vector3 lossyScale, out float extent)
        {
            Vector3 size = Vector3.Scale(mb.size, lossyScale);
            Vector3 axis = Vector3.right;
            extent = mb.extents.x;
            float len = Mathf.Abs(size.x);
            if (Mathf.Abs(size.y) > len) { axis = Vector3.up; extent = mb.extents.y; len = Mathf.Abs(size.y); }
            if (Mathf.Abs(size.z) > len) { axis = Vector3.forward; extent = mb.extents.z; }
            return axis;
        }

        /// <summary>Namlu agzi tarafinin isareti: mesh kutlesinin pivot'a (kabzaya) gore
        /// hangi tarafta toplandigi. Sifirsa +1.</summary>
        public static float BulkSign(Bounds mb, Vector3 axis)
        {
            float sign = Mathf.Sign(Vector3.Dot(mb.center, axis));
            return sign == 0f ? 1f : sign;
        }

        /// <summary>Verilen mesh-lokal referans yonune EN YAKIN ana ekseni secer (profil namlu
        /// yonu tahmini ezerken kullanilir); isaret referans yonden alinir.</summary>
        public static Vector3 AxisClosestTo(Vector3 refDirMeshLocal, Bounds mb, out float extent, out float sign)
        {
            Vector3 axis = Vector3.right;
            extent = mb.extents.x;
            float a = Mathf.Abs(refDirMeshLocal.x);
            if (Mathf.Abs(refDirMeshLocal.y) > a) { axis = Vector3.up; extent = mb.extents.y; a = Mathf.Abs(refDirMeshLocal.y); }
            if (Mathf.Abs(refDirMeshLocal.z) > a) { axis = Vector3.forward; extent = mb.extents.z; }
            sign = Mathf.Sign(Vector3.Dot(refDirMeshLocal, axis));
            if (sign == 0f) sign = 1f;
            return axis;
        }
    }
}
