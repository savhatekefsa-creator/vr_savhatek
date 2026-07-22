using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// War FX (2017-donemi) prefablarinin BURST emisyon verisi Unity 6 yukseltmesinde
    /// kayboluyor: patlama gibi tek-atimlik efektler SIFIR partikul doguruyor (surekli-akisli
    /// alev/duman sistemleri saglam). Bu arac tum WarFX prefablarini tarar; hic emisyonu
    /// kalmamis (rate=0 + burst yok/bos) sistemlere maxParticles'tan turetilen tek burst yazar
    /// (orijinal tasarimda tek-atimlik sistemlerde burst sayisi ~maxParticles'tir).
    /// </summary>
    public static class GrenadeFxRepairTool
    {
        const string Root = "Assets/JMO Assets/WarFX";

        [MenuItem("Tools/VR Multiplayer/Bomba FX Tani (WarFX burst raporu)")]
        public static void Diagnose() => Walk(repair: false);

        /// <summary>Play modunda calisir: oyunun GERCEK yolu (Object.Instantiate) ile patlama
        /// efektini AGIR CEKIMDE (x0.1) dogurur — 0.5 sn'lik ates topu ~5 sn gorunur kalir,
        /// goz/ekran goruntusuyle dogrulamak kolaylasir. Kendini temizleme scripti sokulur.</summary>
        [MenuItem("Tools/VR Multiplayer/Bomba FX Test Patlat (agir cekim)")]
        public static void SlowMotionTest()
        {
            if (!Application.isPlaying) { Debug.LogWarning("[BombaFX] Once Play moduna gir."); return; }
            var cfg = Resources.Load<Weapons.GrenadeConfig>("GrenadeConfigs/Grenade 1");
            if (cfg == null || cfg.explodeFx == null) { Debug.LogError("[BombaFX] Grenade 1 config/explodeFx yok."); return; }

            var go = Object.Instantiate(cfg.explodeFx, new Vector3(0f, -29.5f, 0f),
                cfg.explodeFx.transform.rotation);
            go.name = "TMP_SLOWMO_PATLAMA";
            foreach (var d in go.GetComponentsInChildren<CFX_AutoDestructShuriken>(true))
                Object.Destroy(d);
            int n = 0;
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.simulationSpeed = 0.1f;
                n++;
            }
            Object.Destroy(go, 60f);
            Debug.Log($"[BombaFX] Agir cekim patlama dogdu: {n} particle sistemi, konum (0,-29.5,0).");
        }

        [MenuItem("Tools/VR Multiplayer/Bomba FX Onar (WarFX burst onarimi)")]
        public static void Repair() => Walk(repair: true);

        static void Walk(bool repair)
        {
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { Root });
            int broken = 0, fixedCount = 0, prefabsTouched = 0;

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var root = PrefabUtility.LoadPrefabContents(path);
                bool dirty = false;
                try
                {
                    foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        var em = ps.emission;
                        if (!em.enabled) continue;

                        bool hasRate = em.rateOverTime.constantMax > 0f || em.rateOverTime.mode != ParticleSystemCurveMode.Constant
                                    || em.rateOverDistance.constantMax > 0f;
                        var bursts = new ParticleSystem.Burst[em.burstCount];
                        em.GetBursts(bursts);
                        bool burstAlive = false;
                        foreach (var b in bursts)
                            if (b.count.constantMax > 0f || b.count.mode != ParticleSystemCurveMode.Constant)
                                burstAlive = true;

                        if (hasRate || burstAlive) continue; // saglam — dokunma

                        broken++;
                        int count = Mathf.Clamp(ps.main.maxParticles, 1, 40);
                        Debug.Log($"[BombaFX] {(repair ? "ONARILDI" : "BOZUK")}: {path} :: " +
                                  $"{ps.name} — rate=0, burst yok; maxParticles={ps.main.maxParticles} → burst count={count}");
                        if (!repair) continue;

                        em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
                        dirty = true;
                        fixedCount++;
                    }

                    if (dirty)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                        prefabsTouched++;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            Debug.Log($"[BombaFX] TARAMA BITTI — prefab: {guids.Length}, bozuk sistem: {broken}" +
                      (repair ? $", onarilan: {fixedCount}, kaydedilen prefab: {prefabsTouched}" : " (onarim icin Onar menusu)"));
        }
    }
}
