using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Sarjor degistirme EL HAREKETI dedektoru: silahi hizla ASAGI indirip geri kaldirma
    /// (flick). Saf durum makinesi — ag/mermi/RPC bilmez; hareket tamamlaninca true doner,
    /// istegi ve haptigi cagiran (NetworkWeapon) atar. Yalnizca silahi TUTAN istemcide islenir.
    /// </summary>
    public class WeaponReloadGesture
    {
        int _phase;              // 0 bekle, 1 asagi iniyor, 2 yukari donuyor
        float _prevY, _topY, _lowY;
        float _startedAt;

        /// <summary>Silah elde degilken de tazelenmeli: yoksa silahi kaptigin ILK karede
        /// eski/sifir yukseklikle devasa bir sahte hiz cikar ve hareket kendini tetikler.</summary>
        public void Reset(float y)
        {
            _phase = 0;
            _prevY = y;
        }

        /// <summary>Her kare cagrilir. eligible=false iken izleme sifirlanir ama y takibi
        /// surer (eski davranisla birebir: vy her kare hesaplanirdi). Hareket TAMAMLANINCA
        /// true doner.</summary>
        public bool Tick(float y, bool eligible, float speed, float travel, float window)
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) { _prevY = y; return false; }
            float vy = (y - _prevY) / dt;
            _prevY = y;

            if (!eligible)
            {
                _phase = 0;
                return false;
            }

            bool expired = Time.time - _startedAt > window;

            switch (_phase)
            {
                case 0: // bekle: yeterince hizli bir ASAGI hareket baslatir
                    if (vy <= -speed)
                    {
                        _phase = 1;
                        _topY = y;
                        _lowY = y;
                        _startedAt = Time.time;
                    }
                    break;

                case 1: // asagi iniyor: dip noktayi takip et, yeterince indiyse donusu bekle
                    if (expired) { _phase = 0; break; }
                    if (y < _lowY) _lowY = y;
                    if (_topY - _lowY >= travel && vy >= speed) _phase = 2;
                    break;

                case 2: // yukari donuyor: dipten yeterince kalktiysa hareket tamamlandi
                    if (expired) { _phase = 0; break; }
                    if (y - _lowY >= travel)
                    {
                        _phase = 0;
                        return true;
                    }
                    break;
            }
            return false;
        }
    }
}
