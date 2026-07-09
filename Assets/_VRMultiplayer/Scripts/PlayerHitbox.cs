using System.Collections.Generic;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Regional hitboxes for a player. Instead of a single capsule, this builds several child
    /// trigger colliders at runtime — head, torso, each hand/arm, and the legs — every one
    /// carrying a <see cref="HitZone"/> with its own damage multiplier. Each zone follows a
    /// networked carrier (Head / LeftHand / RightHand, or the root column) each frame, so hits
    /// register correctly on every client including the server that does the authoritative
    /// raycast. Nothing here edits the prefab: the zones are created in code, mirroring how the
    /// rest of this project builds its runtime objects.
    ///
    /// The prefab's original capsule (kept for RequireComponent) is disabled and replaced by the
    /// zones. All zones are disabled while the owner is down.
    ///
    /// Lives on a child of the NetworkPlayer root, wired by Tools > VR Multiplayer > 18.
    /// </summary>
    [RequireComponent(typeof(CapsuleCollider))]
    public class PlayerHitbox : MonoBehaviour
    {
        public PlayerHealth health;
        [Tooltip("Networked head carrier to follow.")]
        public Transform head;
        public float radius = 0.28f;
        [Tooltip("Head'in ustune ne kadar cikilsin (kafa vurusu payi, metre).")]
        public float headMargin = 0.12f;

        [Header("Bolge yaricaplari (metre)")]
        public float headRadius = 0.16f;
        public float torsoRadius = 0.20f;
        public float armRadius = 0.13f;
        public float legRadius = 0.16f;

        // Networked hand carriers (siblings of `head` under the player root), found at runtime.
        Transform _leftHand;
        Transform _rightHand;

        CapsuleCollider _cap;

        // What each zone tracks so LateUpdate can place it every frame.
        enum Follow { Head, LeftHand, RightHand, TorsoColumn, LegColumn }

        class Zone
        {
            public Follow follow;
            public Transform tf;
            public Collider col;
            public CapsuleCollider capsule; // set only for the column zones (torso, legs)
        }

        readonly List<Zone> _zones = new List<Zone>();

        void Awake()
        {
            _cap = GetComponent<CapsuleCollider>();
            _cap.isTrigger = true;
            _cap.enabled = false;   // replaced by the regional child zones built below

            ResolveCarriers();
            BuildZones();
        }

        // Head/LeftHand/RightHand are fixed-name siblings under the player root (general contract).
        void ResolveCarriers()
        {
            Transform root = head != null ? head.parent : transform.parent;
            if (root == null) return;
            if (_leftHand == null) _leftHand = root.Find("LeftHand");
            if (_rightHand == null) _rightHand = root.Find("RightHand");
        }

        void BuildZones()
        {
            AddSphereZone("Kafa", Follow.Head, headRadius, ZoneType.Head);
            AddCapsuleZone("Govde", Follow.TorsoColumn, torsoRadius, ZoneType.Torso);
            AddSphereZone("SolKol", Follow.LeftHand, armRadius, ZoneType.Arm);
            AddSphereZone("SagKol", Follow.RightHand, armRadius, ZoneType.Arm);
            AddCapsuleZone("Bacak", Follow.LegColumn, legRadius, ZoneType.Leg);
        }

        Zone NewZoneObject(string name, Follow follow, ZoneType zoneType)
        {
            var go = new GameObject("HitZone_" + name);
            go.transform.SetParent(transform, false);
            var hz = go.AddComponent<HitZone>();
            hz.health = health;
            hz.zoneType = zoneType;
            hz.zoneName = name;
            return new Zone { follow = follow, tf = go.transform };
        }

        void AddSphereZone(string name, Follow follow, float r, ZoneType zoneType)
        {
            var z = NewZoneObject(name, follow, zoneType);
            var sc = z.tf.gameObject.AddComponent<SphereCollider>();
            sc.isTrigger = true;
            sc.radius = r;
            z.col = sc;
            _zones.Add(z);
        }

        void AddCapsuleZone(string name, Follow follow, float r, ZoneType zoneType)
        {
            var z = NewZoneObject(name, follow, zoneType);
            var cc = z.tf.gameObject.AddComponent<CapsuleCollider>();
            cc.isTrigger = true;
            cc.direction = 1; // Y axis
            cc.radius = r;
            z.capsule = cc;
            z.col = cc;
            _zones.Add(z);
        }

        void LateUpdate()
        {
            if (head == null) return;

            bool alive = health == null || !health.IsDead;
            foreach (var z in _zones)
                if (z.col.enabled != alive) z.col.enabled = alive;
            if (!alive) return;

            // Player may have spawned before the carriers existed — retry until resolved.
            if (_leftHand == null || _rightHand == null) ResolveCarriers();

            Vector3 h = head.position;
            float ground = transform.parent != null ? transform.parent.position.y : 0f;
            float standH = Mathf.Max(0.6f, (h.y + headMargin) - ground);
            float hipY = ground + standH * 0.50f;
            float shoulderY = ground + standH * 0.82f;

            foreach (var z in _zones)
            {
                switch (z.follow)
                {
                    case Follow.Head:
                        z.tf.position = h;
                        break;

                    case Follow.LeftHand:
                        if (_leftHand != null) z.tf.position = _leftHand.position;
                        break;

                    case Follow.RightHand:
                        if (_rightHand != null) z.tf.position = _rightHand.position;
                        break;

                    case Follow.TorsoColumn:
                        PlaceColumn(z, h.x, h.z, hipY, shoulderY);
                        break;

                    case Follow.LegColumn:
                        PlaceColumn(z, h.x, h.z, ground, hipY);
                        break;
                }
            }
        }

        // Vertical capsule from bottomY to topY at (x, z), kept upright regardless of parent yaw.
        void PlaceColumn(Zone z, float x, float z0, float bottomY, float topY)
        {
            float height = Mathf.Max(2f * z.capsule.radius, topY - bottomY);
            z.tf.SetPositionAndRotation(new Vector3(x, (bottomY + topY) * 0.5f, z0), Quaternion.identity);
            z.capsule.height = height;
            z.capsule.center = Vector3.zero;
        }
    }
}
