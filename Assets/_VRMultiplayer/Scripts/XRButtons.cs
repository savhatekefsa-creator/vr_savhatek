using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// XR kumanda okuma yardimcilari. Ayni "cihazi al + bool butonu dene + analog eksene
    /// dus" kalibi 7 ayri dosyada elle kopyaliydi. Mekanik burada tekillesti; ESIK degerleri
    /// bilerek cagiran tarafta kaldi — HandGrabber'in kavrama histerezisi (0.55 bas / 0.35
    /// birak) gibi bilincli farklar bir "standart" ugruna ezilmemeli.
    /// </summary>
    public static class XRButtons
    {
        /// <summary>Bool buton okumasi; cihaz gecersizse false.</summary>
        public static bool Button(XRNode node, InputFeatureUsage<bool> usage)
        {
            var dev = InputDevices.GetDeviceAtXRNode(node);
            return dev.isValid && dev.TryGetFeatureValue(usage, out bool v) && v;
        }

        /// <summary>Buton + analog fallback: bazi OpenXR etkilesim profilleri bool butonu hic
        /// vermez, yalnizca float ekseni verir. Once buton denenir; yoksa eksen esikle okunur.</summary>
        public static bool HeldWithAxisFallback(InputDevice dev,
            InputFeatureUsage<bool> button, InputFeatureUsage<float> axis, float threshold)
        {
            if (!dev.isValid) return false;
            if (dev.TryGetFeatureValue(button, out bool b) && b) return true;
            return dev.TryGetFeatureValue(axis, out float v) && v > threshold;
        }

        /// <summary>0..1 analog okuma; eksen sifirsa bool butondan 0/1 uretilir (ag uzerine
        /// yazilan grip/tetik degerleri boyle okunur).</summary>
        public static float Axis01WithButtonFallback(InputDevice dev,
            InputFeatureUsage<float> axis, InputFeatureUsage<bool> button)
        {
            if (!dev.isValid) return 0f;
            dev.TryGetFeatureValue(axis, out float v);
            if (v <= 0f && dev.TryGetFeatureValue(button, out bool b) && b) v = 1f;
            return Mathf.Clamp01(v);
        }
    }
}
