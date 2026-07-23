using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Duman bombasinin efekt suresini <see cref="GrenadeConfig.smokeDuration"/> ile surer.
    /// WarFX duman prefablari kendi partikul surelerine gore yanip sonuyor; bu bilesen o sureyi
    /// disaridan yonetir, boylece duman suresi config'ten ayarlanabilir (prefab duzenlemeden).
    ///
    /// Isleyis: istenen sure boyunca emisyon surer, sonra yalnizca EMISYON durur — havadaki
    /// duman dogal olarak dagilir, aniden yok olmaz. Temizligi prefabin kendi
    /// CFX_AutoDestructShuriken'i yapar (sistem tamamen olunce yok eder); o bilesen yoksa
    /// buradaki guvenlik agi devreye girer.
    /// </summary>
    public class SmokeLifetime : MonoBehaviour
    {
        float _stopAt;
        float _hardDestroyAt;
        bool _stopped;

        /// <summary>Efekte istenen sureyi uygular. Sure <= 0 ise prefabin kendi davranisi kalir.</summary>
        public static void Apply(GameObject fx, float seconds)
        {
            if (fx == null || seconds <= 0f) return;

            var systems = fx.GetComponentsInChildren<ParticleSystem>(true);
            if (systems.Length == 0) return;

            float tail = 1f; // son parcacigin sonme payi
            foreach (var ps in systems)
            {
                var main = ps.main;
                tail = Mathf.Max(tail, main.startLifetime.constantMax + main.startDelay.constantMax);

                // Dogal suresi istenenden kisa kalan sistemler dongurulur ki duman istenen sure
                // boyunca beslensin. main.duration CALARKEN degistirilemez (Unity hata verir),
                // loop ise degistirilebilir — bu yuzden yol dongu.
                //
                // Yalniz SUREKLI yayan sistemler dongurulur: patlama anindaki tek seferlik
                // burst'u dongurmek dumanin icinde her N saniyede bir "pop" tekrari yaratirdi.
                var emission = ps.emission;
                bool continuous = emission.enabled && emission.rateOverTime.constantMax > 0f;
                if (!main.loop && continuous && main.duration < seconds) main.loop = true;
            }

            var life = fx.GetComponent<SmokeLifetime>();
            if (life == null) life = fx.AddComponent<SmokeLifetime>();
            life._stopAt = Time.time + seconds;
            life._hardDestroyAt = life._stopAt + tail + 2f;
            life._stopped = false;
        }

        void Update()
        {
            if (!_stopped && Time.time >= _stopAt)
            {
                _stopped = true;
                foreach (var ps in GetComponentsInChildren<ParticleSystem>(true))
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }

            // Guvenlik agi: prefabda auto-destruct yoksa duman sahnede sonsuza kadar kalmasin.
            if (Time.time >= _hardDestroyAt) Destroy(gameObject);
        }
    }
}
