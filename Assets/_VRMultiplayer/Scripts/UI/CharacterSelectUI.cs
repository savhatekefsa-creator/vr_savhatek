using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// KARAKTER DUZENI ekraninin akisi: giris ekranindaki KARAKTER DÜZENİ tusuyla acilir
    /// (bkz. <see cref="PlayerEntryUI"/>), TAMAM ile ayni ekrana geri doner. OYUNA SOKMAZ —
    /// maca girisi giris ekranindaki KATIL tusu yapar. Oyuncunun karsisina manken diker
    /// (Resources/CharacterMannequin — CharacterSetupTool uretir), yanina ok panelini koyar
    /// (<see cref="CharacterSelectPanel"/>).
    ///
    /// BAGLANTIYA HIC DOKUNMAZ: bu ekran acikken <see cref="PlayerProfile.Confirmed"/>
    /// hala false, dolayisiyla LanBootstrap/PlayerFlowUI bekliyor ve harita yuklenmiyor.
    /// Oyuncu gorunumunu diledigi kadar kurcalayabilir.
    ///
    /// EKRAN DUNYAYA SABIT, kafayi TAKIP ETMEZ (PlayerEntryUI'nin tembel takibi de yok):
    /// zeminde duran bir mankenin oyuncuyla birlikte kaymasi "vitrindeki karakter" hissini
    /// oldurur; oyuncu mankenin etrafinda kafasini rahatca oynatabilmeli.
    ///
    /// SECIM ANINDA KALICI (her degisiklikte <see cref="CharacterProfile.Set"/>): ayri bir
    /// kaydet adimi yok; TAMAM yalnizca ekrani kapatir, uygulamayi kapatan oyuncu bile
    /// sectigini kaybetmez.
    /// </summary>
    public class CharacterSelectUI : MonoBehaviour
    {
        const float MannequinDistance = 1.9f;
        const float PanelDistance = 1.45f;

        /// <summary>Mankenin kendi etrafinda salinimi. TAM TUR DONDURULMUYOR: yuz secimi
        /// yapilirken yuzun yarim periyot boyunca gorunmez olmasi cileden cikariyor.
        /// +-55 derece iki yani da gosterir, yuz hep geri gelir.</summary>
        const float SwayDegrees = 55f, SwayPeriod = 9f;

        static byte _pendingTeam;

        CharacterSelectPanel _panel;
        VRPointer _pointer;
        CharacterCustomizer _mannequin;
        Transform _mannequinRoot;
        bool _placed;

        byte _team;
        bool _female;

        /// <summary>Ekrani acar. Takim yalnizca GORUNTULEME icin gelir (mankendeki takim
        /// rengi onizlemesi); isim/takim onayi giris ekranindaki KATIL tusunun isi.</summary>
        public static void Create(byte team)
        {
            _pendingTeam = team;
            var go = new GameObject("~CharacterSelectUI");
            DontDestroyOnLoad(go);
            go.AddComponent<CharacterSelectUI>();
        }

        void Start()
        {
            _team = _pendingTeam;

            var panelGo = new GameObject("Character Select Panel");
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.AddComponent<CharacterSelectPanel>();
            _panel.StepPressed += OnStep;
            _panel.ReadyPressed += OnDone;
            _panel.RandomPressed += OnRandom;
            _panel.GenderPressed += OnGender;

            var pointerGo = new GameObject("UI Pointer");
            pointerGo.transform.SetParent(transform, false);
            _pointer = pointerGo.AddComponent<VRPointer>();

            SpawnMannequin();

            // Acilistaki sekme, KAYITLI kafanin cinsiyetine gore secilir: oyuncu ekrani
            // en son biraktigi gorunumle bulur.
            _female = _mannequin != null && _mannequin.IsFemaleOption(CharacterProfile.Head);
            RefreshAll();
        }

        void SpawnMannequin()
        {
            // Manken yoksa (kurulum araci calistirilmamis) ekran YINE ACILIR: oklar ve
            // TAMAM calisir, yalnizca onizleme eksik kalir. Akisi kilitlemek, kozmetik bir
            // eksik icin oyuncuyu oyundan etmek olurdu.
            var prefab = Resources.Load<GameObject>("CharacterMannequin");
            if (prefab == null)
            {
                Debug.LogWarning("[CharacterSelectUI] CharacterMannequin bulunamadi — " +
                                 "Tools > Karakter > Kur calistirilmali. Onizlemesiz devam.");
                return;
            }

            var go = Instantiate(prefab, transform);
            go.SetActive(true);
            _mannequinRoot = go.transform;
            _mannequin = go.GetComponentInChildren<CharacterCustomizer>(true);

            // Takim rengi onizlemesi: oyun icinde takim tonunu kemer tasiyor
            // (PlayerIdentity.avatarRenderer = Belt) — manken de ayni parcayi boyar ki
            // oyuncu "takimimin isareti bu" diye gorsun.
            var belt = FindDeep(_mannequinRoot, "Belt");
            var renderer = belt != null ? belt.GetComponent<SkinnedMeshRenderer>() : null;
            if (renderer != null)
            {
                var mpb = new MaterialPropertyBlock();
                Color c = _team == PlayerProfile.TeamBlue
                    ? PlayerIdentity.TeamAColor : PlayerIdentity.TeamBColor;
                renderer.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", c);   // URP Lit
                mpb.SetColor("_Color", c);       // fallback
                renderer.SetPropertyBlock(mpb);
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var hit = FindDeep(root.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        // ------------------------------------------------------------------ secim

        int Count(int slot) => _mannequin == null ? 1 : slot switch
        {
            CharacterSelectPanel.SlotAccessory => _mannequin.AccessoryCount,
            CharacterSelectPanel.SlotHead      => _mannequin.HeadCount,
            CharacterSelectPanel.SlotJacket    => _mannequin.JacketCount,
            _                                  => _mannequin.PantsCount,
        };

        static int Index(int slot) => slot switch
        {
            CharacterSelectPanel.SlotAccessory => CharacterProfile.Accessory,
            CharacterSelectPanel.SlotHead      => CharacterProfile.Head,
            CharacterSelectPanel.SlotJacket    => CharacterProfile.Jacket,
            _                                  => CharacterProfile.Pants,
        };

        void OnStep(int slot, int dir)
        {
            int next;
            if (slot == CharacterSelectPanel.SlotHead && _mannequin != null)
            {
                // KAFA oklari SECILI KATEGORININ icinde kalir: erkek kafalarda ilerlerken
                // kadina atlamak, sekmeyi anlamsiz kilardi.
                next = _mannequin.StepWithinGender(CharacterProfile.Head, dir, _female);
            }
            else
            {
                int count = Mathf.Max(1, Count(slot));
                // SARMALI gezinme: son secenekten ileri = basa don. 8 secenekli listede "geri
                // geri geri..." ile sona gitmek zorunda birakmak ok tusunun yarisini israf eder.
                next = (Index(slot) + dir + count) % count;
            }

            int head = CharacterProfile.Head, jacket = CharacterProfile.Jacket,
                pants = CharacterProfile.Pants, accessory = CharacterProfile.Accessory;
            switch (slot)
            {
                case CharacterSelectPanel.SlotAccessory: accessory = next; break;
                case CharacterSelectPanel.SlotHead:      head = next; break;
                case CharacterSelectPanel.SlotJacket:    jacket = next; break;
                default:                                 pants = next; break;
            }
            CharacterProfile.Set(head, jacket, pants, accessory);
            RefreshAll();
        }

        /// <summary>
        /// Sekme degistirildi. Kafa, o kategorinin ILK secenegine atlar: oyuncu KADIN
        /// deyip erkek kafayla kalmaz. Kategori bos ise (or. kadin FBX kurulmamis)
        /// sekme sessizce eski haline doner.
        /// </summary>
        void OnGender(bool female)
        {
            if (_mannequin == null || _female == female) return;

            int first = _mannequin.FirstOptionOfGender(female);
            if (first < 0) return;

            _female = female;
            CharacterProfile.Set(first, CharacterProfile.Jacket,
                                 CharacterProfile.Pants, CharacterProfile.Accessory);
            RefreshAll();
        }

        void OnRandom()
        {
            // Kafa SECILI KATEGORIDE kalir — rastgele tusu oyuncunun cinsiyet secimini
            // bozmamali. Kategori icinde rastgele adim atarak seciyoruz.
            int head = CharacterProfile.Head;
            if (_mannequin != null)
            {
                int n = Mathf.Max(1, _mannequin.CountOfGender(_female));
                head = _mannequin.FirstOptionOfGender(_female);
                if (head < 0) head = CharacterProfile.Head;
                else head = _mannequin.StepWithinGender(head, Random.Range(0, n), _female);
            }

            CharacterProfile.Set(head,
                Random.Range(0, Mathf.Max(1, Count(CharacterSelectPanel.SlotJacket))),
                Random.Range(0, Mathf.Max(1, Count(CharacterSelectPanel.SlotPants))),
                Random.Range(0, Mathf.Max(1, Count(CharacterSelectPanel.SlotAccessory))));
            RefreshAll();
        }

        void RefreshAll()
        {
            if (_mannequin != null)
                _mannequin.Apply(CharacterProfile.Head, CharacterProfile.Jacket,
                                 CharacterProfile.Pants, CharacterProfile.Accessory);

            for (int slot = 0; slot < 4; slot++)
            {
                if (slot == CharacterSelectPanel.SlotHead && _mannequin != null)
                {
                    // Kafa sayaci KATEGORI ICINDE: "2/3" kadin kafalari arasindaki sira.
                    _panel.SetCounter(slot,
                        _mannequin.IndexWithinGender(CharacterProfile.Head, _female) - 1,
                        Mathf.Max(1, _mannequin.CountOfGender(_female)));
                    continue;
                }
                _panel.SetCounter(slot, Index(slot), Count(slot));
            }

            _panel.SetGender(_female);

            _panel.SetAccessoryName(_mannequin != null
                ? _mannequin.AccessoryLabel(CharacterProfile.Accessory) : "YOK");
        }

        // ------------------------------------------------------------------ cikislar

        /// <summary>
        /// TAMAM — secim bitti, giris ekranina don. ONAY VERMEZ ve BAGLANMAZ: oyuna girisi
        /// giris ekranindaki KATIL tusu yapiyor. Secimler zaten her degisiklikte
        /// <see cref="CharacterProfile"/>'a yazildigi icin burada kaydedilecek bir sey yok.
        /// </summary>
        void OnDone()
        {
            // Isim/takim PlayerProfile'da HATIRLI duruyor (Remember) — giris ekrani dolu acilir.
            PlayerEntryUI.Create();
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (_panel == null) return;
            _panel.StepPressed -= OnStep;
            _panel.ReadyPressed -= OnDone;
            _panel.RandomPressed -= OnRandom;
            _panel.GenderPressed -= OnGender;
        }

        // ------------------------------------------------------------------ yerlesim

        void Update()
        {
            // Mod degistiyse bu ekran yok (PlayerEntryUI'deki ayni kural).
            if (!AppMode.IsPlayer) { Destroy(gameObject); return; }

            if (!_placed) Place();

            if (_placed && _mannequinRoot != null)
            {
                // Yavas salinim: yerlesimdeki taban donusun uzerine bindirilir.
                float sway = SwayDegrees * Mathf.Sin(Time.unscaledTime * 2f * Mathf.PI / SwayPeriod);
                _mannequinRoot.localRotation = Quaternion.Euler(0f, 180f + sway, 0f);
            }

            _panel.Tick(_pointer);
        }

        /// <summary>
        /// Manken + paneli oyuncunun onune, ZEMINE yerlestirir. Kafa transformu ilk karede
        /// hazir olmayabilir (PlayerEntryUI'deki ayni sebep) — hazir olana kadar denenir,
        /// yerlesince bir daha OYNATILMAZ (dunyaya sabit ekran).
        /// </summary>
        void Place()
        {
            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();

            // Zemin: rig koku Floor izleme merkezinde zeminde durur; rig yoksa (Editor'de
            // duz kamera) kafadan asagi ortalama boy kadar inilir.
            float floorY = XRRigReference.Instance != null
                ? XRRigReference.Instance.transform.position.y
                : head.position.y - 1.6f;

            Vector3 headFlat = new Vector3(head.position.x, floorY, head.position.z);

            // Manken oyuncuya BAKAR (taban 180 — salinim Update'te uzerine biner).
            transform.SetPositionAndRotation(headFlat + fwd * MannequinDistance,
                Quaternion.LookRotation(fwd));

            if (_mannequinRoot != null) _mannequinRoot.localRotation = Quaternion.Euler(0f, 180f, 0f);

            // Panel manken ile oyuncu ARASINDA ayri bir dunya konumunda: mankenle ayni
            // transformda olsalardi oklar mankenin govdesinin icinden gecerdi.
            _panel.transform.SetParent(null, true);
            _panel.transform.SetPositionAndRotation(headFlat + fwd * PanelDistance,
                Quaternion.LookRotation(fwd));
            _panel.transform.SetParent(transform, true);

            _placed = true;
        }

        // ------------------------------------------------------- masaustu yedegi (gozluksuz)
        void OnGUI()
        {
            if (Application.isMobilePlatform || _panel == null) return;

            GUILayout.BeginArea(new Rect(20, 120, 320, 210), GUI.skin.box);
            GUILayout.Label("Karakter secimi");

            DesktopRow("Aksesuar", CharacterSelectPanel.SlotAccessory);
            DesktopRow("Kafa", CharacterSelectPanel.SlotHead);   // kategori icinde gezer
            DesktopRow("Govde", CharacterSelectPanel.SlotJacket);
            DesktopRow("Pantolon", CharacterSelectPanel.SlotPants);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_female ? "[KADIN]" : "KADIN")) OnGender(true);
            if (GUILayout.Button(!_female ? "[ERKEK]" : "ERKEK")) OnGender(false);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Rastgele")) OnRandom();
            if (GUILayout.Button("TAMAM")) OnDone();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void DesktopRow(string label, int slot)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(28))) OnStep(slot, -1);
            GUILayout.Label(label + " " + (Index(slot) + 1) + "/" + Count(slot));
            if (GUILayout.Button(">", GUILayout.Width(28))) OnStep(slot, +1);
            GUILayout.EndHorizontal();
        }
    }
}
