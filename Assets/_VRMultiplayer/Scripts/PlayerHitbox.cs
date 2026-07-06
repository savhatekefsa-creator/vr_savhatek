using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// The body a weapon ray can hit. A capsule trigger that follows the networked HEAD carrier
    /// each frame (so it is correctly placed on every client, including the server that does the
    /// authoritative raycast), spanning from the floor up to the head. Disabled while the owner
    /// is down. The weapon reads <see cref="health"/> off this to apply damage.
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

        CapsuleCollider _cap;

        void Awake()
        {
            _cap = GetComponent<CapsuleCollider>();
            _cap.isTrigger = true;      // never pushes physics objects around
            _cap.direction = 1;         // Y axis
            _cap.radius = radius;
        }

        void LateUpdate()
        {
            if (head == null) return;

            bool alive = health == null || !health.IsDead;
            if (_cap.enabled != alive) _cap.enabled = alive;
            if (!alive) return;

            // Body spans ground (root Y) to just above the head, following head XZ.
            Vector3 h = head.position;
            float ground = transform.parent != null ? transform.parent.position.y : 0f;
            float top = h.y + headMargin;
            float height = Mathf.Max(0.6f, top - ground);

            transform.position = new Vector3(h.x, ground + height * 0.5f, h.z);
            _cap.height = height;
            _cap.center = Vector3.zero;
        }
    }
}
