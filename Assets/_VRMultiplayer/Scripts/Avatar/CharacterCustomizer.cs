using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Avatarin gorunum SECENEKLERI ve uygulayicisi: kafa (erkek ciltleri + kadin kafasi),
    /// boyun, ceket, pantolon malzeme listeleri + kafa aksesuari (mesh ac/kapa) listesi.
    /// Ayni bilesen iki yerde durur: NetworkPlayer prefabinda (oyun ici gorunum,
    /// <see cref="PlayerAppearance"/> surer) ve karakter ekranindaki mankende (onizleme,
    /// <see cref="UI.CharacterSelectUI"/> surer). Listeleri CharacterSetupTool doldurur
    /// (Tools &gt; Karakter) — elle kurulmaz.
    ///
    /// KAFA SECENEGI MESH DE DEGISTIREBILIR: erkek secenekleri ayni mesh'e farkli cilt
    /// malzemesi giydirir; KADIN secenegi erkek kafayi KAPATIP kadin kafasini acar.
    ///
    /// KADIN KAFASI TEK PARCA: yuz + sac + gozler + boyun ayni mesh'te (Mixamo modelinden
    /// alinip bizim iskeletin boyun dikisine olceklendi — bkz. CharacterSetupTool). Bu
    /// yuzden kadin secilince erkek KAFA, BOYUN ve GOZ mesh'lerinin UCU birden kapanir;
    /// aksi halde iki boyun ve iki cift goz ust uste binerdi.
    ///
    /// 0. INDEKS VARSAYILAN GORUNUM (prefabin kendi malzemesi); aksesuarda 0 = YOK.
    ///
    /// TEPEYI KAPATAN AKSESUAR TOPUZU GIZLER (<see cref="AccessoryOption.hidesHair"/>):
    /// sac tek mesh'e gomulu oldugu icin ayrica gizlenemiyor — bunun yerine topuzu
    /// yassilastirilmis IKINCI bir kafa varyantina gecilir
    /// (<see cref="femaleHeadNoBunPart"/>).
    /// </summary>
    public class CharacterCustomizer : MonoBehaviour
    {
        [System.Serializable]
        public class HeadOption
        {
            [Tooltip("Kafa malzemesi. Kadin secenekte kadin kafa mesh'ine, erkekte erkek " +
                     "kafa mesh'ine uygulanir.")]
            public Material material;

            [Tooltip("Bu kafayla birlikte giyilecek boyun malzemesi (ten uyumu).")]
            public Material neckMaterial;

            [Tooltip("Isaretliyse erkek kafa/boyun/goz kapanir, kadin kafasi acilir.")]
            public bool female;
        }

        [System.Serializable]
        public class AccessoryOption
        {
            [Tooltip("Secim ekraninda gosterilecek ad (or. KASK).")]
            public string label;

            [Tooltip("Acilip kapanacak mesh objesi. Birden fazla secenek AYNI objeyi " +
                     "paylasabilir — ayni kask, farkli desen.")]
            public GameObject part;

            [Tooltip("Secilince parcaya yazilacak malzeme (bos = parcanin kendi malzemesi).")]
            public Material material;

            [Tooltip("Bu parca takiliyken kadin karakterin SAC TOPUZU gizlenir " +
                     "(topuzu kafaya yapistirilmis kafa varyanti kullanilir). Kask/sapka " +
                     "gibi tepeyi kapatan parcalarda true; kar maskesi/maske gibi topuza " +
                     "dokunmayanlarda false.")]
            public bool hidesHair;

            [Tooltip("KADIN kafasi secilince kullanilacak varyant: yukari kaydirilmis ve " +
                     "gerekiyorsa buyutulmus mesh. Kadin saci toplu ve hacimli, sapka gibi " +
                     "kucuk parcalar icine gomuluyor. Parca kemige bagli oldugu icin " +
                     "transform kaydirmak ISE YARAMAZ (SkinnedMeshRenderer kendi " +
                     "transformunu yok sayar) — donusum bind poza islenir. Bos ise normal " +
                     "mesh kullanilir (kask yeterince buyuk, gerekmiyor). " +
                     "Uretimi: CharacterSetupTool.MakeLifted.")]
            public Mesh femaleMesh;

            [Tooltip("Parcanin normal (erkek) mesh'i — kadin varyantindan geri donerken " +
                     "kullanilir. femaleMesh doluysa zorunlu.")]
            public Mesh maleMesh;
        }

        [SerializeField] SkinnedMeshRenderer headRenderer;
        [SerializeField] SkinnedMeshRenderer neckRenderer;
        [SerializeField] SkinnedMeshRenderer jacketRenderer;
        [SerializeField] SkinnedMeshRenderer pantsRenderer;

        [Tooltip("Erkek goz mesh'i — kadin kafasi kendi gozlerini tasidigi icin kapatilir.")]
        [SerializeField] SkinnedMeshRenderer eyeRenderer;

        [Tooltip("Kadin kafa mesh objesi: yuz + sac + goz + boyun TEK PARCA " +
                 "(FemaleHeadKach.fbx'ten aşılanır).")]
        [SerializeField] GameObject femaleHeadPart;

        [Tooltip("Kadin kafanin TOPUZSUZ varyanti: kask/sapka takiliyken bunun yerine " +
                 "bu acilir, boylece topuz baslikin icinden tasmaz.")]
        [SerializeField] GameObject femaleHeadNoBunPart;

        [SerializeField] HeadOption[] headOptions;
        [SerializeField] Material[] jacketMaterials;
        [SerializeField] Material[] pantsMaterials;
        [SerializeField] AccessoryOption[] accessories;

        public int HeadCount   => headOptions     != null ? headOptions.Length     : 0;

        /// <summary>Verilen kafa secenegi KADIN mi? Secim ekrani kafalari cinsiyete gore
        /// iki kategoriye ayirirken bunu sorar (bkz. UI.CharacterSelectUI).</summary>
        public bool IsFemaleOption(int index)
        {
            if (headOptions == null || headOptions.Length == 0) return false;
            var o = headOptions[Mathf.Clamp(index, 0, headOptions.Length - 1)];
            return o != null && o.female;
        }

        /// <summary>Istenen cinsiyetteki ILK kafa secenegi; yoksa -1. Kategori
        /// degistirildiginde o kategorinin varsayilanina atlamak icin.</summary>
        public int FirstOptionOfGender(bool female)
        {
            if (headOptions == null) return -1;
            for (int i = 0; i < headOptions.Length; i++)
                if (headOptions[i] != null && headOptions[i].female == female) return i;
            return -1;
        }

        /// <summary>Bu cinsiyetteki secenek sayisi (sayac gostergesi icin).</summary>
        public int CountOfGender(bool female)
        {
            if (headOptions == null) return 0;
            int n = 0;
            for (int i = 0; i < headOptions.Length; i++)
                if (headOptions[i] != null && headOptions[i].female == female) n++;
            return n;
        }

        /// <summary>Cinsiyet icindeki siradaki/onceki secenegin GENEL indeksi (sarmali).
        /// Kategori disina TASMAZ: erkek kafalarda ilerlerken kadina atlamaz.</summary>
        public int StepWithinGender(int current, int dir, bool female)
        {
            if (headOptions == null || headOptions.Length == 0) return current;
            var list = new System.Collections.Generic.List<int>();
            for (int i = 0; i < headOptions.Length; i++)
                if (headOptions[i] != null && headOptions[i].female == female) list.Add(i);
            if (list.Count == 0) return current;

            int pos = list.IndexOf(current);
            if (pos < 0) return list[0];
            return list[(pos + dir + list.Count) % list.Count];
        }

        /// <summary>Kafanin kendi kategorisindeki sirasi (1 tabanli) — "2/3" sayaci icin.</summary>
        public int IndexWithinGender(int current, bool female)
        {
            if (headOptions == null) return 0;
            int n = 0;
            for (int i = 0; i < headOptions.Length; i++)
            {
                if (headOptions[i] == null || headOptions[i].female != female) continue;
                n++;
                if (i == current) return n;
            }
            return 1;
        }

        public int JacketCount => jacketMaterials != null ? jacketMaterials.Length : 0;
        public int PantsCount  => pantsMaterials  != null ? pantsMaterials.Length  : 0;

        /// <summary>+1: 0. secenek YOK (aksesuarsiz kafa da bir secim).</summary>
        public int AccessoryCount => (accessories != null ? accessories.Length : 0) + 1;

        /// <summary>Secim ekranindaki satir yazisi. Sinir disi ya da 0 = "YOK".</summary>
        public string AccessoryLabel(int index) =>
            accessories == null || index <= 0 || index > accessories.Length
                ? "YOK"
                : accessories[index - 1].label;

        /// <summary>
        /// Verilen secimi giydirir. Indeksler LISTE SINIRINA KIRPILIR: ag karsisindan ya da
        /// eski bir kayittan tasan deger gelirse gorunum bozulmaz, en yakin gecerli secenek
        /// giyilir (bkz. CharacterProfile'daki indeks sozlesmesi).
        /// </summary>
        public void Apply(int head, int jacket, int pants, int accessory)
        {
            head      = ClampIndex(head, HeadCount);
            jacket    = ClampIndex(jacket, JacketCount);
            pants     = ClampIndex(pants, PantsCount);
            accessory = ClampIndex(accessory, AccessoryCount);

            bool female = IsFemale(head);
            bool hideBun = accessory > 0 && accessories != null &&
                           accessory <= accessories.Length &&
                           accessories[accessory - 1] != null &&
                           accessories[accessory - 1].hidesHair;

            ApplyHead(head, hideBun);
            Wear(jacketRenderer, jacketMaterials, jacket);
            Wear(pantsRenderer, pantsMaterials, pants);
            ApplyAccessory(accessory, female);
        }

        bool IsFemale(int head)
        {
            if (headOptions == null || headOptions.Length == 0 || femaleHeadPart == null) return false;
            var opt = headOptions[Mathf.Min(ClampIndex(head, HeadCount), headOptions.Length - 1)];
            return opt != null && opt.female;
        }

        void ApplyHead(int head, bool hideBun)
        {
            if (headOptions == null || headOptions.Length == 0) return;
            var opt = headOptions[Mathf.Min(head, headOptions.Length - 1)];
            if (opt == null) return;

            bool female = opt.female && femaleHeadPart != null;

            // Kadin kafasi kendi boynunu ve gozlerini tasir: erkek KAFA + BOYUN + GOZ
            // birlikte kapanir, yoksa ic ice iki boyun ve iki cift goz olurdu.
            if (headRenderer != null)
            {
                headRenderer.gameObject.SetActive(!female);
                if (!female && opt.material != null) headRenderer.sharedMaterial = opt.material;
            }
            if (neckRenderer != null)
            {
                neckRenderer.gameObject.SetActive(!female);
                if (!female && opt.neckMaterial != null) neckRenderer.sharedMaterial = opt.neckMaterial;
            }
            if (eyeRenderer != null) eyeRenderer.gameObject.SetActive(!female);

            // Topuzlu / topuzsuz varyanttan YALNIZCA BIRI acik olur. Topuzsuz varyant
            // yoksa (eski kurulum) topuzlu kafaya duser — baslik topuzu keser ama
            // oyuncu kafasiz kalmaz.
            bool noBunVar = femaleHeadNoBunPart != null;
            bool useNoBun = female && hideBun && noBunVar;

            if (femaleHeadPart != null)
            {
                femaleHeadPart.SetActive(female && !useNoBun);
                if (female && opt.material != null)
                {
                    var r = femaleHeadPart.GetComponent<SkinnedMeshRenderer>();
                    if (r != null) r.sharedMaterial = opt.material;
                }
            }
            if (noBunVar)
            {
                femaleHeadNoBunPart.SetActive(useNoBun);
                if (useNoBun && opt.material != null)
                {
                    var r = femaleHeadNoBunPart.GetComponent<SkinnedMeshRenderer>();
                    if (r != null) r.sharedMaterial = opt.material;
                }
            }
        }

        void ApplyAccessory(int accessory, bool female)
        {
            if (accessories == null) return;
            for (int i = 0; i < accessories.Length; i++)
            {
                var opt = accessories[i];
                if (opt == null || opt.part == null) continue;

                if (i + 1 == accessory)
                {
                    var r = opt.part.GetComponent<SkinnedMeshRenderer>();
                    if (r != null && opt.material != null) r.sharedMaterial = opt.material;
                    // Kadin varyanti VARSA onu tak, yoksa normale DON: ayni obje iki kafada
                    // da kullaniliyor, kaydirilmis mesh birakilirsa erkek kafasinda kask
                    // havada kalirdi.
                    if (r != null && opt.femaleMesh != null)
                        r.sharedMesh = female ? opt.femaleMesh : opt.maleMesh;
                    opt.part.SetActive(true);
                }
                // AYNI OBJE BIRDEN FAZLA SECENEKTE olabilir (ayni kask, farkli desen):
                // secili secenegin objesi listenin baska bir sirasinda diye KAPATILMAMALI.
                else if (!UsesPart(accessory, opt.part))
                {
                    opt.part.SetActive(false);
                }
            }
        }

        bool UsesPart(int accessory, GameObject part) =>
            accessory > 0 && accessory <= accessories.Length &&
            accessories[accessory - 1] != null && accessories[accessory - 1].part == part;

        static int ClampIndex(int v, int count) =>
            count <= 0 ? 0 : Mathf.Clamp(v, 0, count - 1);

        static void Wear(SkinnedMeshRenderer r, Material[] mats, int index)
        {
            if (r == null || mats == null || mats.Length == 0) return;
            var m = mats[Mathf.Min(index, mats.Length - 1)];
            if (m != null) r.sharedMaterial = m;
        }
    }
}
