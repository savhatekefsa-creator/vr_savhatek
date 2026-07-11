#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// DEV-ONLY grip-pose capture. Workflow: drop a weapon so it rests FROZEN in the air, bring
    /// a real hand to the exact pose you want on it, then CLICK THAT HAND'S THUMBSTICK (the only
    /// free button — A/B/X/Y are taken by team/join/calibration). The tool records the hand's
    /// pose RELATIVE to the nearest weapon and produces the four profile values:
    ///   gripLocalPosition / gripLocalEuler   (weapon-local anchor = the controller pose)
    ///   wristLocalPosition / wristLocalEuler (wrist bone offset FROM that anchor)
    ///
    /// The HEADSET can't show OnGUI or logs, so a capture made on a CLIENT is sent to the server
    /// as a named message: it appears in the PC panel + Editor console (readable over MCP).
    /// Compiled out of release builds.
    /// </summary>
    public class WeaponGripCaptureTool : MonoBehaviour
    {
        const string Msg = "GripCapture";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("~WeaponGripCaptureTool");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponGripCaptureTool>();
        }

        bool _prevL, _prevR;
        bool _handlerRegistered;
        string _leftReport = "(sol el yakalanmadi)";
        string _rightReport = "(sag el yakalanmadi)";
        string _received = "(kulakliktan henuz veri gelmedi)";

        void Update()
        {
            RegisterServerHandler();

            bool l = ReadThumbstickClick(XRNode.LeftHand);
            bool r = ReadThumbstickClick(XRNode.RightHand);
            if (l && !_prevL) Capture(true);
            if (r && !_prevR) Capture(false);
            _prevL = l; _prevR = r;
        }

        // Server side: receive reports captured on headset clients.
        void RegisterServerHandler()
        {
            var nm = NetworkManager.Singleton;
            if (_handlerRegistered || nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
                return;
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Msg, (sender, reader) =>
            {
                reader.ReadValueSafe(out string report);
                _received = report;
                Debug.Log("[GripCapture][client " + sender + "] " + report);
            });
            _handlerRegistered = true;
        }

        static bool ReadThumbstickClick(XRNode node)
        {
            var dev = InputDevices.GetDeviceAtXRNode(node);
            if (!dev.isValid) return false;
            dev.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool click);
            return click;
        }

        void OnGUI()
        {
            const float w = 460f;
            GUILayout.BeginArea(new Rect(10f, Screen.height - 300f, w, 290f), GUI.skin.box);
            GUILayout.Label("GRIP YAKALAMA (dev) — silahi birak, eli konumlandir, THUMBSTICK'e BAS");

            // IsListening guard: before the session starts, SpawnManager exists but
            // GetLocalPlayerObject() throws internally.
            var nm = NetworkManager.Singleton;
            bool hasLocalPlayer = nm != null && nm.IsListening && nm.SpawnManager != null &&
                                  nm.SpawnManager.GetLocalPlayerObject() != null;
            if (hasLocalPlayer)
            {
                if (GUILayout.Button("SOL el yakala")) Capture(true);
                GUILayout.Label(_leftReport);
                if (GUILayout.Button("SAG el yakala")) Capture(false);
                GUILayout.Label(_rightReport);
            }
            else
            {
                GUILayout.Label("(bu makinede oyuncu yok — kulaklikta thumbstick'e bas,");
                GUILayout.Label(" degerler asagida ve Editor Console'da belirir)");
            }

            GUILayout.Label("--- Kulakliktan gelen son yakalama ---");
            GUILayout.Label(_received);
            GUILayout.EndArea();
        }

        void Capture(bool left)
        {
            var nm = NetworkManager.Singleton;
            var localPlayer = nm != null && nm.IsListening
                ? nm.SpawnManager?.GetLocalPlayerObject()
                : null;
            if (localPlayer == null) { Set(left, "HATA: bu makinede oyuncu yok (once host/join)"); return; }

            var grab = localPlayer.GetComponent<HandGrabber>();
            var anim = localPlayer.GetComponentInChildren<Animator>();
            if (grab == null || anim == null || !anim.isHuman) { Set(left, "HATA: HandGrabber/Animator yok"); return; }

            Transform anchor = left ? grab.LeftAnchor : grab.RightAnchor;               // controller carrier
            Transform bone = anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (anchor == null || bone == null) { Set(left, "HATA: anchor/bone bulunamadi"); return; }

            var weapon = NearestWeapon(bone.position);
            if (weapon == null) { Set(left, "HATA: yakinda silah yok (silahi elinin yanina birak)"); return; }
            Transform wt = weapon.transform;

            // Anchor (controller) in weapon-local space.
            Vector3 gripLocalPos = wt.InverseTransformPoint(anchor.position);
            Quaternion gripLocalRot = Quaternion.Inverse(wt.rotation) * anchor.rotation;

            // Wrist bone offset FROM the anchor (so the weld reproduces the exact bone pose).
            Vector3 wristLocalPos = Quaternion.Inverse(anchor.rotation) * (bone.position - anchor.position);
            Quaternion wristLocalRot = Quaternion.Inverse(anchor.rotation) * bone.rotation;

            var sb = new StringBuilder();
            sb.AppendLine($"[{(left ? "SOL" : "SAG")}] silah='{weapon.name}'");
            sb.AppendLine($"gripLocalPosition: {V(gripLocalPos)}");
            sb.AppendLine($"gripLocalEuler:    {V(gripLocalRot.eulerAngles)}");
            sb.AppendLine($"wristLocalPosition:{V(wristLocalPos)}");
            sb.AppendLine($"wristLocalEuler:   {V(wristLocalRot.eulerAngles)}");
            string report = sb.ToString();
            Set(left, report);
            Debug.Log("[GripCapture] " + report);

            // Headset client: ship the report to the server so it shows on the PC.
            if (nm.IsClient && !nm.IsServer && nm.CustomMessagingManager != null)
            {
                using var writer = new FastBufferWriter(2048, Allocator.Temp);
                writer.WriteValueSafe(report);
                nm.CustomMessagingManager.SendNamedMessage(Msg, NetworkManager.ServerClientId, writer);
            }
        }

        GrabbableObject NearestWeapon(Vector3 p)
        {
            GrabbableObject best = null;
            float bestD = float.MaxValue;
            foreach (var g in FindObjectsByType<GrabbableObject>(FindObjectsSortMode.None))
            {
                var col = g.GetComponentInChildren<Collider>();
                Vector3 c = col != null ? col.ClosestPointOnBounds(p) : g.transform.position;
                float d = Vector3.Distance(p, c);
                if (d < bestD) { bestD = d; best = g; }
            }
            return best;
        }

        void Set(bool left, string s) { if (left) _leftReport = s; else _rightReport = s; }
        static string V(Vector3 v) => $"{{x: {v.x:0.####}, y: {v.y:0.####}, z: {v.z:0.####}}}";
    }
}
#endif
