using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;
using VRMultiplayer.Weapons;

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

        GrabbableObject _grab;
        WeaponGripProfile _profile;
        Vector3 _muzzleLocal;
        Vector3 _barrelLocal = Vector3.forward;
        float _nextFire;
        float _srvNextFire;
        bool _prevTrigger;

        // Profilsiz silah = bugunku sabitler; her erisim profili varsa oradan okur.
        bool IsAuto => _profile != null && _profile.fireMode == FireMode.Auto;
        float HapticAmplitude => _profile != null ? _profile.hapticAmplitude : 0.7f;
        float HapticDuration => _profile != null ? _profile.hapticDuration : 0.08f;
        float SupportHapticAmplitude => _profile != null ? _profile.supportHapticAmplitude : 0f;

        // Effects (created once, reused per shot)
        LineRenderer _tracer;
        Light _flash;
        Transform _impact;
        float _fxOffAt = -1f;

        void Awake()
        {
            _grab = GetComponent<GrabbableObject>();
            if (muzzle == null) muzzle = transform.Find("Muzzle");
            ApplyProfile();
            ComputeBarrel();
            CreateFx();
        }

        // Optional data-driven overrides from the same profile the grip system uses. Only the
        // firing NUMBERS and an optional muzzle spawn come from here — the FireServerRpc path,
        // hitscan and damage stay exactly as authored. A weapon with no profile is untouched.
        void ApplyProfile()
        {
            var profile = WeaponGripBinder.FindProfile(name);
            if (profile == null) return;
            _profile = profile;

            if (profile.overrideFire)
            {
                fireInterval = profile.fireInterval;
                range = profile.range;
            }

            if (muzzle == null && profile.createMuzzleIfMissing)
            {
                var m = new GameObject("Muzzle").transform;
                m.SetParent(transform, false);
                m.localPosition = profile.muzzleLocalPosition;
                m.localRotation = Quaternion.identity; // +Z = barrel by convention
                muzzle = m;
            }
        }

        void Update()
        {
            if (_fxOffAt > 0f && Time.time > _fxOffAt) HideFx();

            if (!IsSpawned || _grab == null || !_grab.IsHeld) { _prevTrigger = false; return; }
            if (NetworkManager == null || _grab.HolderClientId != NetworkManager.LocalClientId) return;

            // EITHER controller's trigger fires while you hold the weapon — grip hand or
            // support hand, so two-handed players can use their front-hand trigger too.
            bool trig = ReadTrigger(XRNode.RightHand, out var rDev);
            var firedDev = rDev;
            var firedNode = XRNode.RightHand;
            if (!trig)
            {
                trig = ReadTrigger(XRNode.LeftHand, out var lDev);
                firedDev = lDev;
                firedNode = XRNode.LeftHand;
            }

            // Semi: her atis tetigin yeniden cekilmesini ister. Auto: basili tutuldukca tarar.
            bool wantsFire = IsAuto ? trig : (trig && !_prevTrigger);
            if (wantsFire && Time.time >= _nextFire)
            {
                // Kadans kareye degil saate baglanir: taramada frame quantization birikip
                // atis hizini dusurmez. Uzun aradan sonra ise tam bir aralik beklenir —
                // yoksa geride kalmis _nextFire bir sonraki karede bedava ikinci atis verir.
                _nextFire += fireInterval;
                if (_nextFire < Time.time) _nextFire = Time.time + fireInterval;
                Fire(firedDev, firedNode);
            }
            _prevTrigger = trig;
        }

        void Fire(InputDevice firedDev, XRNode firedNode)
        {
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
            FireServerRpc(origin, dir);

            if (firedDev.isValid)
                firedDev.SendHapticImpulse(0, HapticAmplitude, HapticDuration);

            // Destek eli de silahta: ona da hafif bir vurus. Tetigi ceken el tam siddeti
            // zaten aldi — ayni kumandayi ikinci kez titretme.
            byte sup = _grab.SupportHand;
            if (sup != GrabbableObject.NoHand && SupportHapticAmplitude > 0f)
            {
                XRNode supNode = sup == 0 ? XRNode.LeftHand : XRNode.RightHand;
                if (supNode != firedNode)
                {
                    var supDev = InputDevices.GetDeviceAtXRNode(supNode);
                    if (supDev.isValid)
                        supDev.SendHapticImpulse(0, SupportHapticAmplitude, HapticDuration);
                }
            }
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

            // Kadansi istemciye guvenmeden sunucu zorlar: ele gecirilmis bir istemci
            // FireServerRpc'yi her karede cagirsa da atis hizi profilin uzerine cikamaz.
            // %15 tolerans, ag jitter'inda mesru atisin dusmesini onler.
            if (Time.time < _srvNextFire) return;
            _srvNextFire = Time.time + fireInterval * 0.85f;

            dir.Normalize();

            // Authoritative hit: nearest thing the ray touches that is neither the weapon nor
            // the shooter's own body. Player hitboxes are triggers, so include triggers.
            ulong shooter = _grab.HolderClientId;
            byte shooterTeam = TeamOf(shooter);

            Vector3 end = origin + dir * range;
            var hits = Physics.RaycastAll(origin + dir * 0.03f, dir, range,
                Physics.AllLayers, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            int hitboxesSeen = 0;
            foreach (var h in hits)
            {
                if (h.collider.transform.IsChildOf(transform)) continue; // own weapon

                // Regional damage: the ray hits a HitZone (head/torso/arm/leg); the per-region
                // amount is looked up in CombatConfig. GetComponentInParent reaches the zone whether
                // the collider carries it directly or on a child. The amount is resolved on the
                // SERVER (clients can't send damage values — security).
                var zone = h.collider.GetComponentInParent<HitZone>();
                if (zone != null && zone.health != null)
                {
                    var health = zone.health;
                    // Never hit yourself; keep going past your own body.
                    if (health.OwnerClientId == shooter) continue;
                    hitboxesSeen++;
                    byte t = health.TeamValue;
                    if (t != 0 && t == shooterTeam)
                    {
                        Debug.Log($"[Silah] Isabet ENGELLENDI (ayni takim {t}): atan {shooter} -> {health.OwnerClientId}");
                        end = h.point; break; // block, no damage (friendly fire off)
                    }
                    int dmg = CombatConfig.Instance.DamageFor(zone.zoneType);
                    Debug.Log($"[Silah] ISABET! atan {shooter} (takim {shooterTeam}) -> hedef {health.OwnerClientId} (takim {t}), bolge {zone.zoneName}, {dmg} hasar. Kalan: {Mathf.Max(0, health.Health.Value - dmg)}");
                    health.ServerApplyDamage(dmg, shooter);
                    end = h.point; break;
                }

                end = h.point; // first solid/non-player hit stops the ray
                break;
            }

            if (hitboxesSeen == 0)
                Debug.Log($"[Silah] Ates edildi ama HIC OYUNCU HITBOX'ina denk gelmedi. Toplam collider: {hits.Length}. Ilk carpan: {(hits.Length > 0 ? hits[0].collider.name : "hicbir sey")}");

            FireFxClientRpc(origin, end);
        }

        byte TeamOf(ulong clientId)
        {
            if (NetworkManager != null &&
                NetworkManager.ConnectedClients.TryGetValue(clientId, out var c) &&
                c.PlayerObject != null)
            {
                var id = c.PlayerObject.GetComponent<PlayerIdentity>();
                if (id != null) return id.Team.Value;
            }
            return 0;
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
    }
}
