using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// Turns a held <see cref="GrabbableObject"/> into a firing weapon: while YOU hold it, the
    /// holding hand's TRIGGER fires a hitscan ray from the muzzle along the barrel. The server
    /// validates (only the holder may fire, rate-limited) and raycasts authoritatively, then
    /// everyone sees the same tracer + muzzle flash + impact spark; the shooter's controller
    /// buzzes. The muzzle and barrel direction are auto-detected from the mesh (longest axis),
    /// matching how HandGrabber aligns the weapon in the hand.
    /// </summary>
    [RequireComponent(typeof(GrabbableObject))]
    public class NetworkWeapon : NetworkBehaviour
    {
        [Tooltip("Iki atis arasi minimum sure (saniye).")]
        public float fireInterval = 0.18f;
        [Tooltip("Isinin maksimum menzili (metre).")]
        public float range = 60f;

        [Tooltip("Namlu ucu noktasi (ates izi buradan, bakis yonunde cikar). Bos birakilirsa 'Muzzle' adli cocuk aranir, o da yoksa otomatik hesaplanir.")]
        public Transform muzzle;

        [Tooltip("GECICI teshis paneli — sorun cozulunce kaldirilacak.")]
        public bool debugHud = true;

        GrabbableObject _grab;
        Vector3 _muzzleLocal;
        Vector3 _barrelLocal = Vector3.forward;
        float _nextFire;
        bool _prevTrigger;

        // debug
        TextMesh _dbg;
        int _shotsSent, _fxShown;

        // Effects (created once, reused per shot)
        LineRenderer _tracer;
        Light _flash;
        Transform _impact;
        float _fxOffAt = -1f;

        void Awake()
        {
            _grab = GetComponent<GrabbableObject>();
            if (muzzle == null) muzzle = transform.Find("Muzzle");
            ComputeBarrel();
            CreateFx();
        }

        void Update()
        {
            if (_fxOffAt > 0f && Time.time > _fxOffAt) HideFx();
            if (debugHud) UpdateDebugHud();

            if (!IsSpawned || _grab == null || !_grab.IsHeld) { _prevTrigger = false; return; }
            if (NetworkManager == null || _grab.HolderClientId != NetworkManager.LocalClientId) return;

            // EITHER controller's trigger fires while you hold the weapon — grip hand or
            // support hand, so two-handed players can use their front-hand trigger too.
            bool trig = ReadTrigger(XRNode.RightHand, out var rDev);
            var firedDev = rDev;
            if (!trig)
            {
                trig = ReadTrigger(XRNode.LeftHand, out var lDev);
                firedDev = lDev;
            }

            if (trig && !_prevTrigger && Time.time >= _nextFire)
            {
                _nextFire = Time.time + fireInterval;
                Vector3 origin;
                Vector3 dir;
                if (muzzle != null)
                {
                    origin = muzzle.position;   // precise barrel tip placed in the editor
                    dir = muzzle.forward;
                }
                else
                {
                    origin = transform.TransformPoint(_muzzleLocal);
                    dir = (transform.rotation * _barrelLocal).normalized;
                }
                _shotsSent++;
                FireServerRpc(origin, dir);

                if (firedDev.isValid)
                    firedDev.SendHapticImpulse(0, 0.7f, 0.08f);
            }
            _prevTrigger = trig;
        }

        static bool ReadTrigger(XRNode node, out InputDevice dev)
        {
            dev = InputDevices.GetDeviceAtXRNode(node);
            if (!dev.isValid) return false;
            dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool trig);
            if (!trig && dev.TryGetFeatureValue(CommonUsages.trigger, out float t))
                trig = t > 0.6f;
            return trig;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void FireServerRpc(Vector3 origin, Vector3 dir, RpcParams p = default)
        {
            if (p.Receive.SenderClientId != _grab.HolderClientId) return; // only the holder fires
            if (dir.sqrMagnitude < 0.5f) return;
            dir.Normalize();

            // Authoritative hit: first thing the ray touches that is not the weapon itself.
            Vector3 end = origin + dir * range;
            var hits = Physics.RaycastAll(origin + dir * 0.03f, dir, range,
                Physics.AllLayers, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.collider.transform.IsChildOf(transform)) continue; // skip own body
                if (h.distance < best) { best = h.distance; end = h.point; }
            }

            FireFxClientRpc(origin, end);
        }

        [Rpc(SendTo.Everyone)]
        void FireFxClientRpc(Vector3 origin, Vector3 end)
        {
            ShowShot(origin, end);
        }

        // ------------------------------------------------------------- barrel

        // Longest mesh axis = barrel line; the sign toward the mesh's bulk = muzzle side.
        // Same convention as HandGrabber's snap alignment, so the shot goes where you aim.
        void ComputeBarrel()
        {
            MeshFilter biggest = null;
            float biggestSize = 0f;
            foreach (var mf in GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                float s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).sqrMagnitude;
                if (s > biggestSize) { biggestSize = s; biggest = mf; }
            }
            if (biggest == null) { _muzzleLocal = Vector3.forward * 0.4f; return; }

            Bounds mb = biggest.sharedMesh.bounds;
            Vector3 size = Vector3.Scale(mb.size, biggest.transform.lossyScale);
            Vector3 axis = Vector3.right;
            float extent = mb.extents.x;
            float len = Mathf.Abs(size.x);
            if (Mathf.Abs(size.y) > len) { axis = Vector3.up; extent = mb.extents.y; len = Mathf.Abs(size.y); }
            if (Mathf.Abs(size.z) > len) { axis = Vector3.forward; extent = mb.extents.z; }

            float sign = Mathf.Sign(Vector3.Dot(mb.center, axis));
            if (sign == 0f) sign = 1f;

            Vector3 muzzleChild = mb.center + axis * (sign * extent);
            _muzzleLocal = transform.InverseTransformPoint(biggest.transform.TransformPoint(muzzleChild));
            _barrelLocal = (Quaternion.Inverse(transform.rotation) * biggest.transform.rotation) * (axis * sign);
        }

        // ------------------------------------------------------------- effects

        // Runtime-created materials need a shader that is guaranteed to be IN the build.
        // URP/Unlit may get stripped (nothing in the scene references it); URP/Lit always
        // ships because the room materials use it.
        static Shader FindShaderSafe()
        {
            var s = Shader.Find("Universal Render Pipeline/Unlit");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
            if (s == null) s = Shader.Find("Unlit/Color");
            return s;
        }

        void CreateFx()
        {
            var tracerGo = new GameObject("Tracer");
            tracerGo.transform.SetParent(transform, false);
            _tracer = tracerGo.AddComponent<LineRenderer>();
            _tracer.useWorldSpace = true;
            _tracer.positionCount = 2;
            _tracer.widthMultiplier = 0.01f;
            var mat = new Material(FindShaderSafe());
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(1f, 0.9f, 0.4f));
            else mat.color = new Color(1f, 0.9f, 0.4f);
            _tracer.material = mat;
            _tracer.enabled = false;

            var flashGo = new GameObject("Muzzle Flash");
            flashGo.transform.SetParent(transform, false);
            _flash = flashGo.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.color = new Color(1f, 0.8f, 0.4f);
            _flash.intensity = 3f;
            _flash.range = 4f;
            _flash.enabled = false;

            var impactGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            impactGo.name = "Impact Spark";
            Destroy(impactGo.GetComponent<Collider>());
            impactGo.transform.SetParent(transform, false);
            impactGo.transform.localScale = Vector3.one * 0.06f;
            var imat = new Material(FindShaderSafe());
            if (imat.HasProperty("_BaseColor")) imat.SetColor("_BaseColor", new Color(1f, 0.55f, 0.15f));
            else imat.color = new Color(1f, 0.55f, 0.15f);
            impactGo.GetComponent<MeshRenderer>().sharedMaterial = imat;
            _impact = impactGo.transform;
            impactGo.SetActive(false);
        }

        void ShowShot(Vector3 origin, Vector3 end)
        {
            _fxShown++;
            if (_tracer != null)
            {
                _tracer.SetPosition(0, origin);
                _tracer.SetPosition(1, end);
                _tracer.enabled = true;
            }
            if (_flash != null)
            {
                _flash.transform.position = origin;
                _flash.enabled = true;
            }
            if (_impact != null)
            {
                _impact.position = end;
                _impact.gameObject.SetActive(true);
            }
            _fxOffAt = Time.time + 0.07f;
        }

        void HideFx()
        {
            _fxOffAt = -1f;
            if (_tracer != null) _tracer.enabled = false;
            if (_flash != null) _flash.enabled = false;
            if (_impact != null) _impact.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------- debug (GECICI)

        void UpdateDebugHud()
        {
            if (NetworkManager == null || !NetworkManager.IsClient) return; // not on the PC server
            if (_dbg == null)
            {
                var go = new GameObject("Weapon Debug HUD");
                go.transform.localScale = Vector3.one * 0.16f;
                _dbg = go.AddComponent<TextMesh>();
                _dbg.characterSize = 0.06f;
                _dbg.fontSize = 60;
                _dbg.anchor = TextAnchor.UpperLeft;
                _dbg.alignment = TextAlignment.Left;
                _dbg.color = new Color(1f, 0.85f, 0.3f);
            }

            bool rt = ReadTrigger(XRNode.RightHand, out _);
            bool lt = ReadTrigger(XRNode.LeftHand, out _);
            InputDevices.GetDeviceAtXRNode(XRNode.RightHand).TryGetFeatureValue(CommonUsages.trigger, out float ra);
            InputDevices.GetDeviceAtXRNode(XRNode.LeftHand).TryGetFeatureValue(CommonUsages.trigger, out float la);
            bool mine = _grab != null && _grab.HolderClientId == NetworkManager.LocalClientId;

            _dbg.text =
                $"SILAH: spawn={IsSpawned} held={(_grab != null && _grab.IsHeld)} bende={mine} el={(_grab != null ? _grab.HolderHand : 255)}\n" +
                $"tetik SAG={rt}({ra:0.0}) SOL={lt}({la:0.0})\n" +
                $"gonderilen atis={_shotsSent}  gosterilen efekt={_fxShown}";

            var rig = XRRigReference.Instance;
            if (rig != null && rig.head != null)
            {
                Vector3 fwd = rig.head.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();
                _dbg.transform.position = rig.head.position + fwd * 1.2f + Vector3.up * 0.35f;
                _dbg.transform.rotation = Quaternion.LookRotation(fwd);
            }
        }
    }
}
