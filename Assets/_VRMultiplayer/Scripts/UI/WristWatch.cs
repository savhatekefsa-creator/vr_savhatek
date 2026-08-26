using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// BIRINCI SAHIS KOL SAATI — sahibin SOL bilegine, elin SIRTINA takilir.
    ///
    /// NEDEN YENI BIR YOL: saat ekrani bugune kadar AVATARIN sag el kemigine bagliydi
    /// (NetworkPlayer/Avatar/.../Right_Hand/Watch Screen). Ama sahibin gordugu el artik
    /// avatardan gelmiyor — avatarin BUTUN renderer'lari sahibe kapatiliyor
    /// (bkz. <see cref="NetworkVRPlayer.ApplyVisibility"/>), cunku kol IK'si kumandaya
    /// yetisemedigi icin avatarin parcalari goruste geride kaliyordu. Yani o saat
    /// yalnizca KARSIDAKILERIN gordugu bir prop; sahibi onu hic goremiyor.
    ///
    /// Bu bilesen saati sahibin GERCEKTEN gordugu ele — birinci sahis el modeline —
    /// takar. El kumanda tasiyicisina kaynakli oldugu icin saat de her zaman gercek
    /// bilegin uzerindedir; kolun erisim siniri diye bir sorun yoktur.
    ///
    /// YERLESIM TURETILIYOR, GOMULMUYOR: konum ve aci rig'in kendi kemiklerinden
    /// hesaplanir (parmak yonu + avuc normali, <see cref="FingerCurlMath.PalmFrame"/>).
    /// Model degisirse ya da yeniden import edilirse saat kendiliginde dogru yerde kalir —
    /// projedeki namlu/mentese eksenlerinde izlenen kuralin aynisi.
    ///
    /// TEZGAHTA TAKILMAZ: atolyenin sahte elleri de ayni el kurulumunu kullaniyor
    /// (<c>WeaponWorkshop.MakeHand</c>) ama orada avatar YOKTUR; saat yalnizca gercek
    /// oyuncu baglaminda kurulur (bkz. <see cref="Attach"/> cagrisi).
    /// </summary>
    public static class WristWatch
    {
        public const string ObjectName = "WristWatch";

        // ---------------------------------------------------------------------------------
        // ASAGIDAKI UC SAYI EL MODELININ OLCULMESIYLE SECILDI, TAHMINLE DEGIL.
        // Meta el modeli SADECE EL: on kol yok, mesh bilekten ~2.5 cm sonra bitiyor.
        // Parmak ekseninde olculen kesitler (0 = bilek kemigi, negatif = dirsege dogru):
        //
        //   kesit    vertex   sirt yuzeyi   enine genislik
        //   -3.0 cm     0        —              —          <- MESH BURADA YOK
        //   -2.5 cm    10      -1.49 cm       2.85 cm      <- mansetin agzi
        //   -2.0 cm    14       0.41 cm       5.72 cm
        //   -1.5 cm    15       1.61 cm       5.61 cm      <- saglam bolge
        //   -1.0 cm    18       2.10 cm       5.66 cm
        //    0.0 cm    14       1.95 cm       5.71 cm
        //
        // Ilk denemede saat 3.2 cm geriye konmustu ve mesh'in BITTIGI yerde, deriden
        // 3.1 cm yukarida bosluga asili kaliyordu. Kullanilabilir bant -2.0 .. 0.0 cm.
        // ---------------------------------------------------------------------------------

        /// <summary>Bilek kemiginden DIRSEGE dogru kayma (m). Kasa boyu ~2.9 cm oldugu icin
        /// 1.3 cm'de saat -2.8 .. +0.2 cm arasini kaplar; mansetin agzini 3 mm asar, ki
        /// gercekte de saat mansetten biraz tasar.</summary>
        const float TowardElbow = 0.013f;

        /// <summary>Elin SIRTINDAN yukari yukselti (m). Saatin oturdugu kesitte OLCULEN deri
        /// yuzeyi 2.1 cm; 2.3 cm kasayi derinin 2 mm uzerine oturtur. 2.0 cm denendi ve
        /// olculdu: ekran 1 mm ETE GOMULUYORDU. Dusurme; yukseltirsen bilekten kopar.</summary>
        const float LiftFromSkin = 0.023f;

        /// <summary>
        /// Ekran icerigi <see cref="WatchScreenUI"/> tarafindan 1.50 x 1.19 birimlik bir alana
        /// ciziliyor; cerceve 1.57 x 1.256. Bu sayi onu FIZIKSEL boyuta cevirir.
        ///
        /// ARTIK ELLE SECILMIYOR — kadranin OLCULEN uzunlugundan turetilir (bkz. DialFace ve
        /// FrameAlongUnits). Bu sabit yalnizca kasa mesh'i yoksa ya da kadran yuzeyi
        /// secilemezse devreye girer.
        ///
        /// TARIHCE, ayni hataya donmemek icin: bir ara "saatin kendi kotu goruntusu arayuzun
        /// altindan gorunuyor" denince deger 0.032'den 0.040'a CIKARILMISTI. Ortme cabasiydi
        /// ama olculdu ki kadran 2.8 x 3.5 cm, ekran ise 6.3 x 4.1 cm — yani kadranin iki
        /// katindan genis, kasanin her yonunde disina tasiyor. Cihazda gorulen "kayma" da
        /// buydu. Cozum ekrani buyutmek degil, kadrani OLCUP oraya oturtmakti.
        /// </summary>
        // ARTIK YEDEK: olcek normalde kadranin OLCULEN uzunlugundan turetilir (bkz. DialFace
        // ve FrameAlongUnits). Bu sayi yalnizca kasa mesh'i yoksa ya da kadran yuzeyi
        // secilemezse kullanilir.
        const float FaceScale = 0.040f;

        /// <summary>Ekranin kendi normali etrafinda dondurulmesi (derece).
        ///
        /// -90 = icerigin UZUN ekseni KOL BOYUNCA uzanir (saatin dik durusu). Varsayilan 0'da
        /// uzun eksen bilegin ENINE gidiyordu ve cihazda "yatay duruyor" diye reddedildi.
        ///
        /// BEDELI OLCULDU: -90'da ekran kol ekseninde 5.5 cm yer kaplar, oysa Meta el modeli
        /// bilekten yalnizca 2.5 cm geriye uzanir (on kol YOK). Yani ekranin dirsek tarafi
        /// mesh'in bittigi yerin otesine tasar. Saat kasasi da orada oldugu icin bosluk
        /// dolu gorunur; yine de rahatsiz ederse tek dokunus noktasi burasi.</summary>
        const float FaceRollDegrees = -90f;

        // FaceTiltDegrees KALDIRILDI. Ekrani kendi dikey ekseninde 7 derece egen bir yamaydi;
        // amaci "arayuzun sol kismi saatle ic ice geciyor" sikayetini kapatmakti. Gercek sebep
        // egim eksikligi degildi: yon KABA bir ortalamadan (butun ust yuzeyin normali)
        // geliyordu, oysa artik GERCEK kadran duzleminden geliyor (bkz. DialFace). Dogru
        // duzleme oturunca egmeye gerek kalmadi.

        /// <summary>Kadran yuzeyi secimi: kaba normale bu aciya kadar yakin yuzler sayilir.</summary>
        const float DialFaceAngle = 20f;

        /// <summary>...ve yalnizca yuzeyin en ust bu derinligindekiler (m, mesh-yerel).
        /// Pahlar ve kasa omuzlari boylece disarida kalir.</summary>
        const float DialFaceDepth = 0.004f;

        /// <summary>Cerceve quad'inin UZUN ekseni, birim cinsinden (WatchScreenUI: 1.57 x 1.256).
        /// <see cref="FaceRollDegrees"/> bu ekseni KOL BOYUNCA cevirdigi icin ekran olcegi
        /// kadranin kol yonundeki uzunlugundan turetilir.</summary>
        const float FrameAlongUnits = 1.57f;

        // ---- Saat kasasi (Watch_FP) --------------------------------------------------
        // Projedeki TEK saat modeli FP_Hands.fbx icindeki Watch_FP; askerin SAG eline
        // skinli oldugu icin dogrudan kullanilamaz. Menu ile bilek merkezli statik bir
        // mesh'e pisirildi (Resources/FPHands/Watch_FP_Baked): orijin bilek, +Z parmaklar,
        // +Y elin sirti. Boylece her iki rig'de de ayni anatomik cerceveye oturur.
        const string CaseMeshPath = "FPHands/Watch_FP_Baked";
        const string CaseMaterialPath = "FPHands/M_FP_Watch";

        /// <summary>Kasanin bilek ENINE gore hedef genisligi. Pismis mesh 10.6 cm genisliginde
        /// (askerin eli buyuk ve mesh mansetle birlikte geliyor); Meta bilegi 5.65 cm, yani
        /// olcek buradan TURETILIR — sabit bir carpan yazmak rig degisince bozulurdu.</summary>
        const float CaseTargetWidth = 0.058f;

        /// <summary>Ekranin kasanin UST yuzeyinden yukselti payi (m). Sifirda z-fighting,
        /// fazlasinda ekran kasadan kopuk durur. 2 -> 1.2 mm: cihazda "arayuzu saate yakin
        /// yap" dendi; ic ice gecmemesi icin sifirlanmadi.
        ///
        /// NOT: olcu kasanin SINIR KUTUSUNUN tepesinden aliniyor, kadranin kendisinden degil.
        /// Kayis kadrandan yuksekse ekran gorunurde biraz havada kalir — o durumda cozum bu
        /// sayiyi kucultmek degil, mesh'i degistirmek.</summary>
        const float ScreenClearance = 0.0012f;

        /// <summary>Kasanin kol ekseninde OTURACAGI merkez (m; negatif = dirsege dogru).
        /// Cihazda "saat bilege yapismak zorunda degil, biraz gerisinde durabilir" dendi:
        /// -0.5 cm'den -2.2 cm'ye cekildi, yani kasa artik el sirtina hic tasmiyor, tamamen
        /// bilegin gerisinde. Sifir yaparsan saat elin sirtina kayar.</summary>
        const float CaseCenterAlongArm = -0.022f;

        /// <summary>
        /// Kasanin KADRAN normali (mesh-yerel). Ust yuzeyi olusturan ucgenlerin ALAN
        /// AGIRLIKLI ortalamasi — kucuk pahlar ve kayis parcalari sonucu bozmasin diye
        /// yalnizca sinir kutusunun ust kismindaki ve disari bakan yuzler sayilir.
        /// Olculdu (Watch_FP_Baked): (0.130, 0.956, 0.262), yani elin sirtindan 17 derece egik.
        /// </summary>
        static Vector3 DialNormal(Mesh m)
        {
            var v = m.vertices; var tri = m.triangles; var b = m.bounds;
            Vector3 sum = Vector3.zero; float area = 0f;
            for (int i = 0; i + 2 < tri.Length; i += 3)
            {
                Vector3 p0 = v[tri[i]], p1 = v[tri[i + 1]], p2 = v[tri[i + 2]];
                Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
                float a = n.magnitude * 0.5f;
                if (a < 1e-9f) continue;
                n /= (a * 2f);
                Vector3 c = (p0 + p1 + p2) / 3f;
                if (c.y <= b.center.y + b.extents.y * 0.4f) continue;   // ust kisim degil
                if (Vector3.Dot(n, Vector3.up) <= 0.3f) continue;       // disari bakmiyor
                sum += n * a; area += a;
            }
            return area > 0f ? (sum / area).normalized : Vector3.up;
        }

        /// <summary>
        /// KADRANIN DUZ UST YUZEYI: merkezi, normali, kol ekseni ve iki olcusu — hepsi
        /// mesh-yerel cercevede (+X enine, +Y sirt, +Z parmaklar). Yuzey bulunamazsa false.
        ///
        /// NEDEN <see cref="DialNormal"/> YETMIYOR: o, sinir kutusunun ust yarisindaki disari
        /// bakan TUM yuzlerin ortalamasi — pahlar, kasa omuzlari ve kayis parcalari dahil.
        /// Ekrani hizalamak icin fazla kaba: olculdu, gercek kadran duzleminden 5.8 derece
        /// sapiyordu ve bu sapma daha once elle bir egim sabitiyle (FaceTiltDegrees) kapatilmaya
        /// calisilmisti. Burada o yon yalnizca KABA FILTRE olarak kullanilir; gercek kadran
        /// yuzeyi ondan turetilir.
        ///
        /// SECIM: normali kaba yone <see cref="DialFaceAngle"/> dereceden yakin VE yuzeyin en
        /// ust <see cref="DialFaceDepth"/> derinligindeki yuzler. Olculdu: 325 ucgenin 29'u,
        /// 2.8 x 3.5 cm.
        ///
        /// MERKEZ, AGIRLIK MERKEZI DEGIL: secilen yuzlerin kapladigi DIKDORTGENIN ortasi
        /// aliniyor. Ekran bir dikdortgen ve kadrana ortalanmali; agirlik merkezi ucgenlerin
        /// dagilimina gore kayardi (kadranin bir kosesi daha yogun uclenmisse oraya cekerdi).
        /// Normal yonunde ise ORTA degil TEPE noktasi verilir — ekran yuzeyin uzerine biner.
        /// </summary>
        static bool DialFace(Mesh m, out Vector3 center, out Vector3 normal,
                             out Vector3 alongArm, out float across, out float along)
        {
            center = Vector3.zero;
            normal = Vector3.up;
            alongArm = Vector3.forward;
            across = along = 0f;
            if (m == null) return false;

            Vector3 coarse = DialNormal(m).normalized;
            var v = m.vertices;
            var tri = m.triangles;
            if (v.Length == 0 || tri.Length == 0) return false;

            float top = float.NegativeInfinity;
            for (int i = 0; i < v.Length; i++)
            {
                float p = Vector3.Dot(v[i], coarse);
                if (p > top) top = p;
            }

            // Alan agirlikli normal: buyuk kadran ucgenleri kucuk kenar ucgenlerini bastirir.
            var picked = new List<int>();
            Vector3 nSum = Vector3.zero;
            for (int t = 0; t < tri.Length; t += 3)
            {
                Vector3 a = v[tri[t]], b = v[tri[t + 1]], c = v[tri[t + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                float area2 = n.magnitude;
                if (area2 < 1e-12f) continue;
                n /= area2;
                if (Vector3.Angle(n, coarse) > DialFaceAngle) continue;
                float h = (Vector3.Dot(a, coarse) + Vector3.Dot(b, coarse) + Vector3.Dot(c, coarse)) / 3f;
                if (h < top - DialFaceDepth) continue;
                nSum += n * area2;
                picked.Add(t);
            }
            if (picked.Count == 0 || nSum.sqrMagnitude < 1e-12f) return false;

            normal = nSum.normalized;
            alongArm = Vector3.ProjectOnPlane(Vector3.forward, normal);   // mesh +Z = parmaklar
            if (alongArm.sqrMagnitude < 1e-8f) return false;
            alongArm.Normalize();
            Vector3 side = Vector3.Cross(alongArm, normal).normalized;

            float minS = float.PositiveInfinity, maxS = float.NegativeInfinity;
            float minA = float.PositiveInfinity, maxA = float.NegativeInfinity;
            float maxN = float.NegativeInfinity;
            for (int i = 0; i < picked.Count; i++)
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = v[tri[picked[i] + k]];
                    float s = Vector3.Dot(p, side);
                    float aa = Vector3.Dot(p, alongArm);
                    float nn = Vector3.Dot(p, normal);
                    if (s < minS) minS = s;
                    if (s > maxS) maxS = s;
                    if (aa < minA) minA = aa;
                    if (aa > maxA) maxA = aa;
                    if (nn > maxN) maxN = nn;
                }

            across = maxS - minS;
            along = maxA - minA;
            center = side * ((minS + maxS) * 0.5f)
                   + alongArm * ((minA + maxA) * 0.5f)
                   + normal * maxN;
            return true;
        }

        /// <summary>
        /// Saati bu elin modeline takar. <paramref name="handModel"/> = Meta el modelinin
        /// koku ("Hand"); kemikler onun altinda aranir.
        /// Sessizce vazgecer: kemik bulunamazsa saat kurulmaz, el normal calisir.
        /// </summary>
        public static Transform Attach(Transform handModel, bool left)
        {
            if (handModel == null) return null;

            string p = left ? "b_l_" : "b_r_";
            Transform wrist = null, mid = null, idx = null, pky = null;
            foreach (var t in handModel.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == p + "wrist") wrist = t;
                else if (t.name == p + "middle1") mid = t;
                else if (t.name == p + "index1") idx = t;
                else if (t.name == p + "pinky1") pky = t;
            }
            if (wrist == null || mid == null || idx == null || pky == null)
            {
                Debug.LogWarning("[WristWatch] El kemikleri bulunamadi (" + p + "*) — saat takilmadi.");
                return null;
            }
            if (wrist.Find(ObjectName) != null) return null;   // ikinci kez takilmasin

            // Anatomik cerceve ORTAK yardimciyla: parmak kivriminin kullandigi tanimin
            // ta kendisi, yani saat ile parmaklar ayni "avuc" fikrini paylasiyor.
            FingerCurlMath.PalmFrame(wrist, idx, pky, mid, left,
                                     out _, out Vector3 curlPlane, out _);
            // curlPlane AVUC tarafini gosterir (parmaklar oraya kivriliyor); saat elin
            // SIRTINDA durur. Olculdu: iki elde de sirt = -curlPlane.
            Vector3 back = -curlPlane.normalized;
            Vector3 fingers = (mid.position - wrist.position).normalized;

            // ---- KASA: bilek merkezli, elin kendi anatomik cercevesinde ----
            // Kok dogrudan bilege oturur (+Z parmak, +Y sirt) — pismis mesh tam bu
            // cerceveye gore uretildi, yani ek bir hizalama gerekmiyor.
            var caseGo = new GameObject(ObjectName);
            var ct = caseGo.transform;
            ct.SetParent(wrist, false);
            ct.SetPositionAndRotation(wrist.position,
                Quaternion.LookRotation(fingers, Vector3.ProjectOnPlane(back, fingers)));

            // Ekranin nereye oturacagi KASADAN turetilir (asagida). Kasa yoksa bu degerler
            // yedege duser: bilekten TowardElbow geride, deriden LiftFromSkin yukarida.
            float screenAlongArm = -TowardElbow;
            float screenAcross = 0f;
            float screenLift = LiftFromSkin;

            var caseMesh = Resources.Load<Mesh>(CaseMeshPath);
            if (caseMesh != null)
            {
                float w = caseMesh.bounds.size.x;
                float k = w > 1e-4f ? CaseTargetWidth / w : 1f;
                ct.localScale = Vector3.one * k;

                // KASAYI BILEGE ORTALA. Pismis mesh kaynagindaki yerini tasiyor ve olculdu:
                // kol ekseninde -1.5 .. +3.8 cm, yani agirligi ELIN SIRTINDA kaliyordu
                // (Watch_FP askerin eldiven mansetiyle birlikte modellenmis, saf saat degil).
                // Merkezini istedigimiz noktaya tasiyoruz; ekran da kasadan turedigi icin
                // kendiliginden onunla gelir.
                Bounds cb0 = caseMesh.bounds;
                ct.position = wrist.position
                            + fingers * (CaseCenterAlongArm - cb0.center.z * k)
                            - Vector3.Cross(back, fingers).normalized * (cb0.center.x * k);

                var mf = caseGo.AddComponent<MeshFilter>();
                mf.sharedMesh = caseMesh;
                var mr = caseGo.AddComponent<MeshRenderer>();
                var mat = Resources.Load<Material>(CaseMaterialPath);
                if (mat != null) mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;

                // EKRAN KASANIN TAM UZERINE. Mesh bilek-merkezli cerceveye pisirildi
                // (+X enine, +Y sirt, +Z parmak), yani sinirlarini dogrudan o eksenlerde
                // okuyabiliyoruz — ayrica bir donusum gerekmiyor.
                //
                // Ilk surumde ekran bagimsiz sabitlerle konuluyordu ve olculdu: kasanin
                // ustu 3.3 cm'deyken ekran 2.3 cm'de, yani KASANIN ICINDE kaliyordu;
                // ustelik kol ekseninde 2.6 cm kaymisti. Turetince ikisi de kendiliginden
                // dogru olur ve kasa olcegi degisince ekran onu takip eder.
                // Kasa yukarida TASINDI; ekran onun YENI merkezine hizalanmali, mesh'in
                // ham merkezine degil.
                screenAcross = 0f;
                screenAlongArm = CaseCenterAlongArm;
                screenLift = caseMesh.bounds.max.y * k + ScreenClearance;
            }
            else Debug.LogWarning("[WristWatch] Kasa mesh'i yok: Resources/" + CaseMeshPath);

            // ---- EKRAN: kasanin cocugu DEGIL, kardesi ----
            // Kasa "bilek enine sigsin" diye olcekleniyor; ekran o olcekten ETKILENMEMELI,
            // yoksa kasa carpani ekranin fiziksel boyutunu da degistirir ve iki ayar
            // birbirine baglanir.
            var go = new GameObject(ObjectName + "_Screen");
            var t2 = go.transform;
            t2.SetParent(wrist, false);
            t2.position = wrist.position
                        + fingers * screenAlongArm
                        + back * screenLift
                        + Vector3.Cross(back, fingers).normalized * screenAcross;

            // EKRANIN YONU KASANIN KENDI KADRANINDAN TURETILIR, bilekten DEGIL.
            //
            // Ilk surumde ekran dogrudan elin sirtina (back) dikti ve cihazda "yamuk duruyor"
            // dendi. Olculdu: Watch_FP'nin kadran yuzeyi kendi cercevesinde (0.130, 0.956,
            // 0.262) yonune bakiyor — yani elin sirtindan 17 DERECE egik. Kasa askerin
            // bilegine o acida modellenmis. Ekrani bilege dikince kasayla arasinda tam o
            // 17 derece kaliyor ve iki parca birbirine gore egri gorunuyordu.
            //
            // Kadran normali mesh'ten HER SEFERINDE hesaplanir (325 ucgen, bir kez): mesh
            // yeniden pisirilirse hizalama kendiliginde dogru kalir, sabit bir aci gommeye
            // gerek yok.
            // EKRANIN YERI, YONU VE BOYUTU KADRANIN KENDI YUZEYINDEN TURETILIR.
            //
            // OLCULDU (Watch_FP_Baked, kasa olcegi 0.549): kadranin duz ust yuzeyi 29 ucgen
            // ve fiziksel olarak 2.8 x 3.5 cm. Ekran ise elle secilmis sabitlerle
            // 6.3 x 4.1 cm cikiyordu — kadranin IKI KATINDAN genis. Kasanin her yonunde
            // disina tasiyor, merkezi 3 mm kaciyor ve 5.8 derece egik duruyordu; cihazda
            // gorulen "kayma" buydu. Nasil buyudugu de kayitli: "saatin kendi kotu
            // goruntusu arayuzun altindan gorunuyor" denince 0.032 -> 0.040 buyutulmustu,
            // yani ortme cabasi tasmayi artirmisti.
            //
            // Sabitleri kovalamak yerine yuzeyi olcup oraya oturtuyoruz — projenin kurali:
            // yerlesim TURETILIR, gomulmez. Mesh yeniden pisirilirse ekran kendiliginden
            // dogru kalir.
            float faceScale = FaceScale;
            Quaternion caseRot = ct.rotation;
            Vector3 dial = back;
            Vector3 dialCenter = t2.position;
            Vector3 alongArm = fingers;

            Vector3 dcL, dnL, daL; float dAcross, dAlong;
            if (caseMesh != null && DialFace(caseMesh, out dcL, out dnL, out daL,
                                             out dAcross, out dAlong))
            {
                float k = ct.localScale.x;
                dial = (caseRot * dnL).normalized;
                alongArm = (caseRot * daL).normalized;
                dialCenter = ct.TransformPoint(dcL);

                // YUKSEKLIGE SIGDIR. Cerceve FrameAlongUnits birim uzunlugunda ve
                // FaceRollDegrees onu kol boyunca ceviriyor, yani kadranin KOL YONUNDEKI
                // uzunluguna oturtuyoruz. Genislige sigdirsaydik ekran kol ekseninde
                // ~9 mm tasardi; boyle kadranin iki yaninda ~3 mm ciplak kalir ve o da
                // kasanin kendi cercevesi gibi okunur.
                faceScale = (dAlong * k) / FrameAlongUnits;
            }
            else if (caseMesh != null)
                dial = (caseRot * DialNormal(caseMesh)).normalized;

            Vector3 up = Vector3.ProjectOnPlane(alongArm, dial);
            if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(back, dial);
            t2.rotation = Quaternion.LookRotation(dial, up)
                        * Quaternion.AngleAxis(FaceRollDegrees, Vector3.forward);
            t2.localScale = Vector3.one;
            t2.position = dialCenter + dial * ScreenClearance;

            var ui = go.AddComponent<WatchScreenUI>();
            ui.faceScale = faceScale;
            ui.faceStretch = Vector2.one;   // icerik kendi oraninda; esnetme gerekmiyor
            // Quad'in on yuzu -Z'ye bakar; yuzu 180 cevirerek ekrani disari donduruyoruz.
            // (Avatardaki eski ekran da tam olarak bu duzeltmeyi tasiyor.)
            ui.faceLocalEuler = new Vector3(0f, 180f, 0f);
            ui.faceLocalPosition = new Vector3(0f, 0f, -0.001f);
            return t2;
        }
    }
}
