using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Birinci sahis eli. Avatar iskeletinden TAMAMEN ayri: el gorseli dogrudan
    /// kumanda tasiyicisinin ALTINA parent'lanir.
    ///
    /// Sart: kumanda neredeyse el ORADADIR. Sinir yok - kumanda 2 m otede yerdeyse
    /// el de oradadir. Bu garanti her karede konum yazan bir koddan degil,
    /// parent-child iliskisinin kendisinden gelir; yani bir kare geride kalma,
    /// IK yakinsama hatasi ve govde yaw olu bolgesi gibi hata siniflarinin tamami
    /// yapisal olarak imkansiz. Bu yuzden burada Update/LateUpdate YOK.
    ///
    /// Kol IK'si (uzak oyuncularin gordugu) bundan bagimsiz calismaya devam eder;
    /// orada kural terstir - kol uzayamaz, sinirinda dumduz kalir
    /// (bkz. <see cref="ArmReach"/>).
    ///
    /// Gorsel su an YER TUTUCU: "el dogru yerde mi, dogru mu donuyor" sorusunu
    /// cevaplamak icin kod ile uretilen basit bloklar. Gercek el modeli ayri is.
    /// </summary>
    public static class FirstPersonHandView
    {
        public const string ObjectName = "FP_HandView";

        // OpenXR grip pose zaten avucun icinde oturur, o yuzden tasiyiciya gore
        // kaydirma sifir. Faz 3 olcumu (el <-> silah kabzasi mesafesi) burayi
        // ayarlamak icin tek dokunus noktasi.
        static readonly Vector3 PalmLocalOffset = Vector3.zero;

        /// <summary>
        /// Iki kumanda tasiyicisinin altina el gorselini kurar. Yalnizca SAHIP
        /// icin cagrilir; aga hic girmez, uzak istemcilerde hic yaratilmaz.
        /// </summary>
        public static void Attach(Transform leftCarrier, Transform rightCarrier)
        {
            Build(leftCarrier, true);
            Build(rightCarrier, false);
        }

        static void Build(Transform carrier, bool left)
        {
            if (carrier == null) return;

            // ApplyVisibility birden fazla kez calisabilir - ikinci el takilmasin.
            var existing = carrier.Find(ObjectName);
            if (existing != null) return;

            var root = new GameObject(ObjectName);
            root.transform.SetParent(carrier, false);
            root.transform.localPosition = PalmLocalOffset;
            root.transform.localRotation = Quaternion.identity;

            // Tasiyicilar eski "basit el kupu"ndan kalma DUZGUN OLMAYAN bir olcek
            // tasiyor (0.08, 0.045, 0.13). Altina konan her sey hem kuculur hem
            // carpilir. Olcegi burada tersine cevirip gorseli gercek dunya
            // olcusune dondururuz. Tasiyicinin olcegini prefabta duzeltmek
            // caziptir ama YAPILMAMALI: HandGrabber silah tutus offsetlerini
            // anchor.InverseTransformPoint ile cozuyor, yani olcek tutus
            // kalibrasyonunun icinde.
            //
            // DIKKAT: duzgun olmayan bir olcegin tersi ancak cocuklar EKSEN
            // HIZALI ise dogru sonuc verir. Asagidaki parcalarin donusu bilerek
            // identity - yeni bir parca eklerken de oyle kalmali, yoksa mesh
            // makaslanir.
            Vector3 s = carrier.lossyScale;
            root.transform.localScale = new Vector3(
                Mathf.Approximately(s.x, 0f) ? 1f : 1f / s.x,
                Mathf.Approximately(s.y, 0f) ? 1f : 1f / s.y,
                Mathf.Approximately(s.z, 0f) ? 1f : 1f / s.z);

            // Yer tutucu: avuc + basparmak + parmak yonu. Uc parcanin rengi ayri,
            // boylece elin hangi yone baktigi duz isikta bile okunuyor.
            // Basparmak hangi tarafta: eli duz tut, avuc asagi, parmaklar ileri (+z),
            // elin sirti yukari (+y). SAG elin basparmagi o zaman -x'te (govde
            // ortasina dogru), sol elinki +x'te kalir.
            float side = left ? 1f : -1f;
            var body = new Color(0.62f, 0.60f, 0.58f);
            var thumb = new Color(0.85f, 0.45f, 0.15f);
            var fingers = new Color(0.25f, 0.65f, 0.80f);

            //            ad          konum                                  olcek                          renk
            Piece(root, "Palm", new Vector3(0f, 0f, 0.01f), new Vector3(0.075f, 0.028f, 0.095f), body);
            Piece(root, "Thumb", new Vector3(side * 0.042f, 0.004f, 0.028f), new Vector3(0.024f, 0.022f, 0.052f), thumb);
            Piece(root, "Fingers", new Vector3(0f, -0.002f, 0.082f), new Vector3(0.068f, 0.022f, 0.062f), fingers);
        }

        static void Piece(GameObject parent, string name, Vector3 localPos, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;

            // CreatePrimitive collider'la geliyor. Elin uzerinde collider kalirsa
            // silah yakalama ve fizik bundan etkilenir - kaldiriliyor.
            // Object.Destroy oyun modu DISINDA ertelenir ve hic calismaz (editor
            // olcum kosumunda collider hayatta kaliyordu), o yuzden moda gore secim.
            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                if (Application.isPlaying) Object.Destroy(col);
                else Object.DestroyImmediate(col);
            }

            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = scale;

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = MakeMaterial(color);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        static Material MakeMaterial(Color color)
        {
            // Projedeki konvansiyon: URP shader'i bulunamazsa yerlesik olana dus,
            // boylece build'de shader elenirse el gorunmez olmaz.
            var sh = Shader.Find("Universal Render Pipeline/Lit")
                  ?? Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Unlit/Color")
                  ?? Shader.Find("Sprites/Default");
            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.15f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            return m;
        }
    }
}
