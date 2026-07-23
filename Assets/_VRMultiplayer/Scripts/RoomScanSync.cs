using System;
using System.Collections;
using System.IO;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.OpenXR.Features.Meta;

namespace VRMultiplayer
{
    /// <summary>
    /// "ODAYI PC'YE GONDER" — reads the Quest 3 Space Setup scan (Scene Model) via AR Foundation,
    /// converts it to the shared A/B calibration frame and ships it to the PC server over the
    /// existing LAN link. The server saves Assets/_VRMultiplayer/RoomPlans/RoomPlan.json, which
    /// the editor menus "Import Room Plan" / "Build Walls From Plan" consume.
    ///
    /// Usage (owner only): calibrate first, then press X (left controller). One headset doing
    /// this once is enough. Requires the Meta OpenXR "Planes" feature + USE_SCENE permission;
    /// if the room was never scanned, the system Space Setup flow is launched automatically.
    /// </summary>
    public class RoomScanSync : NetworkBehaviour
    {
        const string ScenePermission = "com.oculus.permission.USE_SCENE";
        const int ChunkSize = 3000; // stays well under the transport payload limit

        TextMesh _panel;
        bool _busy;
        bool _prevX;
        float _hidePanelAt = -1f;

        // Server-side reassembly (one buffer per sender's player object = this instance).
        byte[][] _rxChunks;
        int _rxReceived;

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) enabled = false; // RPCs still arrive on the server instance
        }

        public override void OnNetworkDespawn()
        {
            if (_panel != null) Destroy(_panel.gameObject);
        }

        void Update()
        {
            FollowHead();
            if (_hidePanelAt > 0f && Time.time > _hidePanelAt) { HidePanel(); }

            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            bool x = false;
            if (left.isValid)
                left.TryGetFeatureValue(CommonUsages.primaryButton, out x);

            if (x && !_prevX && !_busy)
                StartCoroutine(ScanAndSend());
            _prevX = x;
        }

        IEnumerator ScanAndSend()
        {
            _busy = true;

            if (!CalibrationManager.Calibrated)
            {
                Show("ODA GONDERME\n\nOnce KALIBRASYON yapmalisin\n(A/B noktalari + tetik).", 4f);
                _busy = false;
                yield break;
            }

            // 1) Spatial-data permission (runtime, per app).
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(ScenePermission))
            {
                Show("ODA GONDERME\n\nIzin isteniyor...\n(cikan soruya IZIN VER de)");
                UnityEngine.Android.Permission.RequestUserPermission(ScenePermission);
                float permDeadline = Time.time + 20f;
                while (Time.time < permDeadline &&
                       !UnityEngine.Android.Permission.HasUserAuthorizedPermission(ScenePermission))
                    yield return null;
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(ScenePermission))
                {
                    Show("Izin verilmedi.\nAyarlar > Uygulamalar'dan\n'uzamsal veri' iznini ac.", 6f);
                    _busy = false;
                    yield break;
                }
            }
#endif

            // 2) Wait for scene planes; if the room was never scanned, launch Space Setup.
            var planeMgr = FindFirstObjectByType<ARPlaneManager>();
            if (planeMgr == null)
            {
                Show("HATA: ARPlaneManager yok.\nEditorde menu 11'i calistir.", 6f);
                _busy = false;
                yield break;
            }
            planeMgr.enabled = true;

            Show("ODA GONDERME\n\nOda tarama verisi okunuyor...");
            float deadline = Time.time + 5f;
            while (Time.time < deadline && !HasAnyPlane(planeMgr))
                yield return null;

            if (!HasAnyPlane(planeMgr))
            {
                // No Scene Model on this device -> ask the OS to run Space Setup now.
                Show("Oda taramasi bulunamadi.\nSpace Setup baslatiliyor —\ntaramayi bitirip geri don.");
                var arSession = FindFirstObjectByType<ARSession>();
                var meta = arSession != null ? arSession.subsystem as MetaOpenXRSessionSubsystem : null;
                bool requested = false;
                if (meta != null)
                {
                    try { requested = meta.TryRequestSceneCapture(); }
                    catch (Exception e) { Debug.LogWarning("[RoomScan] Scene capture request failed: " + e.Message); }
                }
                if (!requested)
                {
                    Show("Space Setup baslatilamadi.\nGozluk ayarlarindan 'Alan Kurulumu'\nyapip tekrar dene (X).", 8f);
                    _busy = false;
                    yield break;
                }
                // The app pauses during Space Setup and resumes afterwards.
                deadline = Time.time + 15f;
                while (Time.time < deadline && !HasAnyPlane(planeMgr))
                    yield return null;
            }

            // 3) Extract in world space — thanks to calibration the rig transform already maps
            //    device space into the shared frame, and trackables live under the rig.
            var plan = ExtractPlan(planeMgr);
            if (plan == null || plan.floorPolygon.Length < 3)
            {
                Show("Zemin poligonu okunamadi.\nSpace Setup'ta zemin/duvarlari\ntarayip tekrar dene (X).", 8f);
                _busy = false;
                yield break;
            }

            // 4) Ship to the PC in chunks over the reliable channel.
            plan.sentBy = name;
            var ident = GetComponent<PlayerIdentity>();
            if (ident != null) plan.sentBy = ident.DisplayName;

            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(plan));
            int total = (bytes.Length + ChunkSize - 1) / ChunkSize;
            for (int i = 0; i < total; i++)
            {
                int len = Mathf.Min(ChunkSize, bytes.Length - i * ChunkSize);
                var chunk = new byte[len];
                Buffer.BlockCopy(bytes, i * ChunkSize, chunk, 0, len);
                SendRoomChunkServerRpc(i, total, chunk);
            }

            Show($"GONDERILDI\n\n{plan.floorPolygon.Length} kose • {Mathf.Abs(plan.Area()):0.0} m2\n" +
                 $"{plan.walls.Length} duvar • {plan.furniture.Length} esya\nPC onayi bekleniyor...", 20f);
            _busy = false;
        }

        static bool HasAnyPlane(ARPlaneManager mgr)
        {
            foreach (var _ in mgr.trackables) return true;
            return false;
        }

        RoomPlan ExtractPlan(ARPlaneManager planeMgr)
        {
            var plan = new RoomPlan();

            // Floor = the largest Floor-classified plane (fallback: lowest horizontal one).
            ARPlane floor = null;
            float bestArea = 0f;
            var walls = new System.Collections.Generic.List<RoomWall>();
            float ceilingY = float.NaN;

            foreach (var pl in planeMgr.trackables)
            {
                var cls = pl.classifications;
                if (cls.HasFlag(PlaneClassifications.Floor) ||
                    (cls == PlaneClassifications.None && pl.alignment == PlaneAlignment.HorizontalUp
                     && pl.transform.position.y < 0.5f))
                {
                    float area = pl.size.x * pl.size.y;
                    if (area > bestArea) { bestArea = area; floor = pl; }
                }
                else if (cls.HasFlag(PlaneClassifications.Ceiling))
                {
                    ceilingY = pl.center.y;
                }
                else if (cls.HasFlag(PlaneClassifications.WallFace) ||
                         cls.HasFlag(PlaneClassifications.InvisibleWallFace) ||
                         (cls == PlaneClassifications.None && pl.alignment == PlaneAlignment.Vertical))
                {
                    walls.Add(new RoomWall
                    {
                        center = pl.center,
                        normal = pl.transform.up, // ARPlane +Y = surface normal (points into room)
                        width = Mathf.Max(pl.size.x, pl.size.y),
                        height = Mathf.Min(pl.size.x, pl.size.y),
                    });
                }
            }

            if (floor == null) return null;

            plan.floorY = floor.center.y;
            plan.ceilingY = float.IsNaN(ceilingY) ? plan.floorY + 2.5f : ceilingY;
            plan.walls = walls.ToArray();

            // Boundary: plane-space 2D -> world XZ, dropping near-duplicate points.
            var boundary = floor.boundary;
            var poly = new System.Collections.Generic.List<Vector2>(boundary.Length);
            Vector2 prev = new Vector2(float.MaxValue, float.MaxValue);
            for (int i = 0; i < boundary.Length; i++)
            {
                Vector3 w = floor.transform.TransformPoint(new Vector3(boundary[i].x, 0f, boundary[i].y));
                var p = new Vector2(w.x, w.z);
                if ((p - prev).sqrMagnitude < 0.05f * 0.05f) continue;
                poly.Add(p);
                prev = p;
            }
            plan.floorPolygon = poly.ToArray();

            // Furniture (couch/table/... as oriented boxes), if the feature is enabled.
            var boxMgr = FindFirstObjectByType<ARBoundingBoxManager>();
            if (boxMgr != null)
            {
                var boxes = new System.Collections.Generic.List<RoomBox>();
                foreach (var bb in boxMgr.trackables)
                {
                    boxes.Add(new RoomBox
                    {
                        label = bb.classifications.ToString(),
                        center = bb.transform.position,
                        rotation = bb.transform.rotation,
                        size = bb.size,
                    });
                }
                plan.furniture = boxes.ToArray();
            }

            // Normalize vertically: the scanned floor DEFINES y=0 of the shared frame. Some
            // headsets deliver scene data with an arbitrary vertical origin (e.g. after redoing
            // Space Setup / the boundary), and our A/B calibration only corrects horizontally —
            // pinning the floor to 0 makes the plan immune to that.
            float yShift = -plan.floorY;
            if (Mathf.Abs(yShift) > 0.001f)
            {
                plan.floorY = 0f;
                plan.ceilingY += yShift;
                foreach (var w in plan.walls) w.center.y += yShift;
                foreach (var f in plan.furniture) f.center.y += yShift;
            }

            return plan;
        }

        // ------------------------------------------------------------ network

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void SendRoomChunkServerRpc(int index, int total, byte[] data, RpcParams p = default)
        {
            // Yalnizca bu oyuncu objesinin SAHIBI oda gonderebilir. Kontrolsuz halde herhangi
            // bir istemci baskasinin objesi uzerinden chunk basabiliyordu: farkli 'total' ile
            // devam eden transferi sifirlayip bozmak ya da kurbanin adiyla sahte RoomPlan.json
            // yazdirmak mumkundu (FireServerRpc'deki tutan-el kontrolunun buradaki karsiligi).
            if (p.Receive.SenderClientId != OwnerClientId) return;
            if (total <= 0 || total > 512 || index < 0 || index >= total) return;
            if (_rxChunks == null || _rxChunks.Length != total)
            {
                _rxChunks = new byte[total][];
                _rxReceived = 0;
            }
            if (_rxChunks[index] == null) _rxReceived++;
            _rxChunks[index] = data;
            if (_rxReceived < total) return;

            int size = 0;
            foreach (var c in _rxChunks) size += c.Length;
            var all = new byte[size];
            int at = 0;
            foreach (var c in _rxChunks)
            {
                Buffer.BlockCopy(c, 0, all, at, c.Length);
                at += c.Length;
            }
            _rxChunks = null;

            bool ok = SaveOnServer(Encoding.UTF8.GetString(all));
            ConfirmSavedOwnerRpc(ok);
        }

        bool SaveOnServer(string json)
        {
            try
            {
                var plan = JsonUtility.FromJson<RoomPlan>(json);
                if (plan == null || plan.floorPolygon == null || plan.floorPolygon.Length < 3)
                {
                    Debug.LogWarning("[RoomScan] Gecersiz oda plani geldi, kaydedilmedi.");
                    return false;
                }
                plan.capturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

#if UNITY_EDITOR
                string dir = Application.dataPath + "/_VRMultiplayer/RoomPlans";
#else
                string dir = Application.persistentDataPath + "/RoomPlans";
#endif
                Directory.CreateDirectory(dir);
                string path = dir + "/RoomPlan.json";
                File.WriteAllText(path, JsonUtility.ToJson(plan, true));
#if UNITY_EDITOR
                UnityEditor.AssetDatabase.Refresh();
#endif
                Debug.Log($"[RoomScan] ODA PLANI KAYDEDILDI: {path}\n" +
                          $"  {plan.floorPolygon.Length} kose, {Mathf.Abs(plan.Area()):0.0} m2, " +
                          $"{plan.walls.Length} duvar, {plan.furniture.Length} esya (gonderen: {plan.sentBy})");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[RoomScan] Kaydetme hatasi: " + e);
                return false;
            }
        }

        [Rpc(SendTo.Owner)]
        void ConfirmSavedOwnerRpc(bool ok)
        {
            Show(ok
                ? "PC'YE KAYDEDILDI ✓\n\nUnity: Tools > VR Multiplayer >\n12. Import Room Plan"
                : "PC kaydi BASARISIZ.\nPC konsolundaki hataya bak.", 8f);
        }

        // ------------------------------------------------------------ panel

        void Show(string text, float hideAfter = -1f)
        {
            if (_panel == null)
            {
                var go = new GameObject("Room Scan Panel");
                go.transform.localScale = Vector3.one * 0.16f;
                _panel = go.AddComponent<TextMesh>();
                _panel.characterSize = 0.1f;
                _panel.fontSize = 60;
                _panel.anchor = TextAnchor.MiddleCenter;
                _panel.alignment = TextAlignment.Center;
                _panel.color = new Color(0.5f, 1f, 0.6f);
            }
            _panel.gameObject.SetActive(true);
            _panel.text = text;
            _hidePanelAt = hideAfter > 0f ? Time.time + hideAfter : -1f;
        }

        void HidePanel()
        {
            if (_panel != null) _panel.gameObject.SetActive(false);
            _hidePanelAt = -1f;
        }

        void FollowHead()
        {
            if (_panel == null || !_panel.gameObject.activeSelf) return;
            var rig = XRRigReference.Instance;
            if (rig == null || rig.head == null) return;
            Vector3 fwd = rig.head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            _panel.transform.position = rig.head.position + fwd * 1.4f;
            _panel.transform.rotation = Quaternion.LookRotation(fwd);
        }
    }
}
