#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
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
    ///
    /// Every capture is ALSO appended to a log file on the SERVER (see <see cref="LogPath"/>) —
    /// a capture session is a long series of tries, and re-reading them off the console one
    /// screenshot at a time loses them. The file is append-only: nothing is ever overwritten, so
    /// you can capture the same hand ten times and keep all ten. Values are written in the exact
    /// `{x: .., y: .., z: ..}` shape a WeaponGripProfile .asset uses, so a good row can be pasted
    /// straight into the profile with no retyping.
    /// </summary>
    public class WeaponGripCaptureTool : MonoBehaviour
    {
        const string Msg = "GripCapture";
        const string LogFileName = "WeaponGripCaptures.md";

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
                reader.ReadValueSafe(out bool isCapture);
                reader.ReadValueSafe(out string report);
                _received = report;
                Debug.Log("[GripCapture][client " + sender + "] " + report);
                // Only real captures reach the file; a "no weapon nearby" error is console noise,
                // not a row someone will later paste into a profile.
                if (isCapture) AppendToLog(report);
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
            GUILayout.BeginArea(new Rect(10f, Screen.height - 370f, w, 360f), GUI.skin.box);
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
            GUILayout.Label($"--- Dosya: {_written} kayit yazildi ---");
            GUILayout.Label(LogPath);
            GUILayout.EndArea();
        }

        void Capture(bool left)
        {
            var nm = NetworkManager.Singleton;
            var localPlayer = nm != null && nm.IsListening
                ? nm.SpawnManager?.GetLocalPlayerObject()
                : null;
            if (localPlayer == null) { Fail(left, "HATA: bu makinede oyuncu yok (once host/join)"); return; }

            var grab = localPlayer.GetComponent<HandGrabber>();
            var anim = localPlayer.GetComponentInChildren<Animator>();
            if (grab == null || anim == null || !anim.isHuman) { Fail(left, "HATA: HandGrabber/Animator yok"); return; }

            Transform anchor = left ? grab.LeftAnchor : grab.RightAnchor;               // controller carrier
            Transform bone = anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (anchor == null || bone == null) { Fail(left, "HATA: anchor/bone bulunamadi"); return; }

            var weapon = NearestWeapon(bone.position);
            if (weapon == null) { Fail(left, "HATA: yakinda silah yok (silahi elinin yanina birak)"); return; }
            Transform wt = weapon.transform;

            // Anchor (controller) in weapon-local space.
            Vector3 gripLocalPos = wt.InverseTransformPoint(anchor.position);
            Quaternion gripLocalRot = Quaternion.Inverse(wt.rotation) * anchor.rotation;

            // Wrist bone offset FROM the anchor (so the weld reproduces the exact bone pose).
            Vector3 wristLocalPos = Quaternion.Inverse(anchor.rotation) * (bone.position - anchor.position);
            Quaternion wristLocalRot = Quaternion.Inverse(anchor.rotation) * bone.rotation;

            string hand = left ? "SOL" : "SAG";
            string role = Role(weapon, left);
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"## {hand} el — {weapon.name} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- rol: {role}");
            sb.AppendLine("```");
            sb.AppendLine($"  gripLocalPosition: {V(gripLocalPos)}");
            sb.AppendLine($"  gripLocalEuler: {V(gripLocalRot.eulerAngles)}");
            sb.AppendLine($"  wristLocalPosition: {V(wristLocalPos)}");
            sb.AppendLine($"  wristLocalEuler: {V(wristLocalRot.eulerAngles)}");
            sb.AppendLine("```");
            // Euler is a lossy, order-dependent view of the same rotation (Unity = ZXY). If a
            // pasted euler ever reproduces the wrong pose, these raw quaternions are the ground
            // truth the capture actually measured.
            sb.AppendLine($"<!-- ham quaternion — gripLocalRotation: {Q(gripLocalRot)} wristLocalRotation: {Q(wristLocalRot)} -->");
            string report = sb.ToString();

            Set(left, $"[{hand}] {weapon.name} — {role}");
            Debug.Log("[GripCapture] " + report);
            Deliver(report);
            Buzz(left, 2); // double pulse = SUCCESS, felt inside the headset
        }

        /// <summary>
        /// What this hand was DOING on the weapon — the whole point of capturing left and right
        /// separately. The same physical hand needs a different pose depending on whether it is
        /// wrapped around the pistol grip or steadying the handguard, and each lands in a
        /// different profile field (mainHand vs supportHand), so a bare "SOL/SAG" label is not
        /// enough to file the values later.
        /// </summary>
        static string Role(GrabbableObject weapon, bool left)
        {
            var nm = NetworkManager.Singleton;
            if (weapon == null || !weapon.IsHeld) return "TUTULMUYOR — silah bostayken olculdu";
            if (nm != null && nm.IsListening && weapon.HolderClientId != nm.LocalClientId)
                return "BASKA OYUNCUDA — bu deger profile YAZILMAZ";

            byte h = (byte)(left ? 0 : 1);
            if (weapon.HolderHand == h) return "ANA EL (kabza) -> profil: gripLocal* + mainHand.wristLocal*";
            if (weapon.SupportHand == h) return "DESTEK EL (kundak/ray) -> profil: supportHand.wristLocal*";
            return "BU EL SILAHI TUTMUYOR — silah diger elde";
        }

        // Errors are also felt (single short pulse) and shipped to the PC — the headset has no
        // screen for OnGUI/logs, so without this a failed capture is indistinguishable from a
        // dead button.
        void Fail(bool left, string message)
        {
            string tagged = $"[{(left ? "SOL" : "SAG")}] {message}";
            Set(left, tagged);
            Debug.Log("[GripCapture] " + tagged);
            SendToServer(tagged, false);
            Buzz(left, 1);
        }

        /// <summary>
        /// Route a finished capture. A client ships it to the PC (the headset has no console, and
        /// a file on the headset would need an adb pull to be useful); the server writes straight
        /// to disk. Exactly one branch runs per capture, so a host never logs its own record twice.
        /// </summary>
        void Deliver(string report)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening && !nm.IsServer) { SendToServer(report, true); return; }
            AppendToLog(report);
        }

        static void SendToServer(string report, bool isCapture)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.IsServer || nm.CustomMessagingManager == null)
                return;
            using var writer = new FastBufferWriter(4096, Allocator.Temp);
            writer.WriteValueSafe(isCapture);
            writer.WriteValueSafe(report);
            nm.CustomMessagingManager.SendNamedMessage(Msg, NetworkManager.ServerClientId, writer);
        }

        /// <summary>
        /// Where captures land. In the editor this is the PROJECT ROOT — deliberately NOT under
        /// Assets/, because a file there would be imported as an asset and trigger a database
        /// refresh on every single capture. A player build has no project folder, so it falls
        /// back to persistentDataPath.
        /// </summary>
        public static string LogPath =>
#if UNITY_EDITOR
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", LogFileName));
#else
            Path.Combine(Application.persistentDataPath, LogFileName);
#endif

        static int _written;

        /// <summary>Append-only. A capture session is dozens of tries and the keeper is usually
        /// not the last one, so nothing here ever overwrites what came before.</summary>
        static void AppendToLog(string block)
        {
            try
            {
                string path = LogPath;
                if (!File.Exists(path))
                    File.WriteAllText(path,
                        "# Silah tutus yakalamalari\n\n" +
                        "WeaponGripCaptureTool uretir. Her yakalama SONA eklenir, hicbir sey silinmez.\n" +
                        "Degerler WeaponGripProfile .asset icine oldugu gibi yapistirilabilir.\n");
                File.AppendAllText(path, block);
                _written++;
            }
            catch (Exception e)
            {
                // Never let a locked/read-only file kill the capture itself — the values are
                // still in the console and on the panel.
                Debug.LogWarning("[GripCapture] Dosyaya yazilamadi: " + e.Message);
            }
        }

        static void Buzz(bool left, int pulses)
        {
            var dev = InputDevices.GetDeviceAtXRNode(left ? XRNode.LeftHand : XRNode.RightHand);
            if (!dev.isValid) return;
            // Two immediate impulses read as one long+strong buzz vs the short single error blip.
            for (int i = 0; i < pulses; i++)
                dev.SendHapticImpulse(0, i == 0 ? 0.8f : 1f, pulses > 1 ? 0.25f : 0.08f);
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
        static string Q(Quaternion q) => $"{{x: {q.x:0.######}, y: {q.y:0.######}, z: {q.z:0.######}, w: {q.w:0.######}}}";
    }
}
#endif
