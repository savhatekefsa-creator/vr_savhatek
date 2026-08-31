using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Karakter ekraninda secilen GORUNUMUN ag senkronu (bkz. <see cref="CharacterProfile"/> /
    /// <see cref="CharacterCustomizer"/>). <see cref="PlayerIdentity"/>'nin isim/takim
    /// deseniyle birebir ayni akis: sahip spawn'da secimini ServerRpc ile bildirir, SUNUCU
    /// SON SOZU SOYLER (indeksler customizer listelerine kirpilir) ve NetworkVariable'lar
    /// sayesinde GEC KATILAN da dogru gorunumu gorur — ilk senkron otomatik.
    ///
    /// AYRI BILESEN, PlayerIdentity'ye eklenmedi: kimlik (isim/takim/skor) oyun boyunca
    /// yasayan veri, gorunum ise spawn'da bir kez giyilen kozmetik. Ayrica bu bilesen
    /// prefabda CharacterCustomizer ile birlikte CharacterSetupTool tarafindan kurulur.
    /// </summary>
    [RequireComponent(typeof(CharacterCustomizer))]
    public class PlayerAppearance : NetworkBehaviour
    {
        public NetworkVariable<byte> Head      = new NetworkVariable<byte>(0);
        public NetworkVariable<byte> Jacket    = new NetworkVariable<byte>(0);
        public NetworkVariable<byte> Pants     = new NetworkVariable<byte>(0);
        public NetworkVariable<byte> Accessory = new NetworkVariable<byte>(0);

        CharacterCustomizer _customizer;
        PlayerIdentity _identity;

        void Awake()
        {
            _customizer = GetComponent<CharacterCustomizer>();
            _identity = GetComponent<PlayerIdentity>();
        }

        public override void OnNetworkSpawn()
        {
            Head.OnValueChanged      += OnChanged;
            Jacket.OnValueChanged    += OnChanged;
            Pants.OnValueChanged     += OnChanged;
            Accessory.OnValueChanged += OnChanged;

            // Ilk senkron OnValueChanged tetiklemez (PlayerIdentity'deki ayni kural):
            // gec katilan, karsisindaki oyuncunun secimini burada giyer.
            ApplyNow();

            // Sahip, karakter ekraninda sectigini sunucuya bildirir. Byte'a kirpma
            // gonderirken de yapiliyor ama SON SOZ sunucunun (bkz. SetAppearanceServerRpc).
            if (IsOwner)
                SetAppearanceServerRpc(
                    (byte)Mathf.Clamp(CharacterProfile.Head, 0, byte.MaxValue),
                    (byte)Mathf.Clamp(CharacterProfile.Jacket, 0, byte.MaxValue),
                    (byte)Mathf.Clamp(CharacterProfile.Pants, 0, byte.MaxValue),
                    (byte)Mathf.Clamp(CharacterProfile.Accessory, 0, byte.MaxValue));
        }

        public override void OnNetworkDespawn()
        {
            Head.OnValueChanged      -= OnChanged;
            Jacket.OnValueChanged    -= OnChanged;
            Pants.OnValueChanged     -= OnChanged;
            Accessory.OnValueChanged -= OnChanged;
        }

        void OnChanged(byte _, byte __) => ApplyNow();

        void ApplyNow()
        {
            _customizer.Apply(Head.Value, Jacket.Value, Pants.Value, Accessory.Value);

            // Gorunum malzemeleri DEGISTI: olum/dirilis onbellegi eski malzemeleri
            // tutuyorsa dirilis oyuncunun secimini geri alirdi. Onbellek, renderer'daki
            // yeni malzemeler CANLI kabul edilerek yeniden kurulur.
            if (_identity != null) _identity.RefreshMaterialCache();
        }

        /// <summary>
        /// [ServerRpc] varsayilani RequireOwnership = true — kimse BASKASININ gorunumunu
        /// degistiremez. Kirpma SUNUCUDA yapilir: istemciden gelen degere guvenilmez
        /// (PlayerIdentity.SetNameServerRpc'deki ayni kural).
        /// </summary>
        [ServerRpc]
        void SetAppearanceServerRpc(byte head, byte jacket, byte pants, byte accessory)
        {
            Head.Value      = ClampToCount(head, _customizer.HeadCount);
            Jacket.Value    = ClampToCount(jacket, _customizer.JacketCount);
            Pants.Value     = ClampToCount(pants, _customizer.PantsCount);
            Accessory.Value = ClampToCount(accessory, _customizer.AccessoryCount);
        }

        static byte ClampToCount(byte v, int count) =>
            (byte)(count <= 0 ? 0 : Mathf.Min(v, count - 1));
    }
}
