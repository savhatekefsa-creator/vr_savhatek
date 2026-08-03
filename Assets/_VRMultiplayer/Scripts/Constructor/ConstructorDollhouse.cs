using UnityEngine;

namespace VRMultiplayer.Constructor
{
    /// <summary>
    /// Tabletop view of the whole arena: the map shrinks to a model in front of the player, who
    /// keeps building by pointing at the miniature.
    ///
    /// WHY IT EXISTS: once building past the room walls was allowed, the map became ~15 x 14 m
    /// while the physical room stayed ~5 x 5 m. Everything outside is unreachable on foot and
    /// only visible edge-on from inside. The miniature is the only way to actually see and
    /// arrange it.
    ///
    /// NO SECOND COPY. Instead of duplicating the map and keeping two versions in sync, the
    /// real map root is REPARENTED under a scaled transform — one map, one grid, no sync
    /// problem. That works because the map root and the grid container are already ODA UZAYI
    /// (their contents are stored in room coordinates), so scaling the parent scales everything
    /// consistently, and the cursor maths keeps running in room space untouched.
    /// </summary>
    public class ConstructorDollhouse : MonoBehaviour
    {
        [Tooltip("Maket olcegi. 0.1 = 1:10 — 15 m'lik bir harita 1.5 m'lik bir masa modeli olur.")]
        [Range(0.02f, 0.5f)] public float scale = 0.1f;

        [Tooltip("Maketin kafanin ONUNDEKI uzakligi (m).")]
        public float distance = 0.75f;

        [Tooltip("Maketin goz hizasina gore yuksekligi (m). Negatif = asagida, masa gibi.")]
        public float heightOffset = -0.45f;

        public bool Active { get; private set; }

        /// <summary>
        /// The room-space transform while the dollhouse is on, otherwise null. Callers convert
        /// rays into it and read positions out of it; null simply means "room space IS world
        /// space", so the same code path serves both modes.
        /// </summary>
        public Transform Space => Active ? _root : null;

        Transform _root;

        void OnDisable() => SetActive(false);

        public bool Toggle() { SetActive(!Active); return Active; }

        public void SetActive(bool on)
        {
            if (Active == on) return;

            var session = ConstructorSession.Instance;
            if (on && (session == null || !session.IsActive)) return;

            Active = on;

            if (on) PlaceInFrontOfPlayer(session);
            Reparent(session, on);

            if (!on && _root != null)
            {
                _root.localScale = Vector3.one;
                _root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            }
        }

        /// <summary>
        /// Anchors the model so the ROOM'S CENTRE lands on the spot in front of the player,
        /// not the room's origin — otherwise a map whose origin sits in a far corner would
        /// appear off to one side, mostly out of reach.
        ///
        /// Positioned ONCE, on activation, and then left alone: a model that followed the head
        /// could never be pointed at, because it would move with every glance.
        /// </summary>
        void PlaceInFrontOfPlayer(ConstructorSession session)
        {
            EnsureRoot();

            Transform head = XRRigReference.HeadOrCamera;
            Vector3 anchor;
            if (head != null)
            {
                Vector3 fwd = head.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                anchor = head.position + fwd.normalized * distance + Vector3.up * heightOffset;
            }
            else
            {
                anchor = new Vector3(0f, 1.2f, 1f);
            }

            var grid = session.Grid;
            var center = new Vector3(
                grid.Origin.x + grid.Cols * grid.CellSize * 0.5f,
                grid.FloorY,
                grid.Origin.y + grid.Rows * grid.CellSize * 0.5f);

            // Donduruyoruz DEGIL: maket dunya eksenleriyle hizali kalinca oyuncunun odadaki
            // yonu ile maketteki yon ayni olur — "sagimdaki duvar" makette de sagda durur.
            _root.rotation = Quaternion.identity;
            _root.localScale = Vector3.one * scale;
            _root.position = anchor - center * scale;
        }

        void EnsureRoot()
        {
            if (_root != null) return;
            _root = new GameObject("~ConstructorDollhouse").transform;
        }

        void Reparent(ConstructorSession session, bool intoModel)
        {
            Transform target = intoModel ? _root : null;

            if (session != null && session.Root != null) Adopt(session.Root, target);

            var view = GetComponent<ConstructorGridView>();
            if (view != null) Adopt(view.Container, target);
        }

        /// <summary>
        /// Hands a room-space root to a new parent and pins its local pose to identity.
        ///
        /// The identity reset is the point: these roots ARE room space, so their own transform
        /// must never contribute anything. Left to drift they would silently offset every prop
        /// from the cell it is recorded in.
        /// </summary>
        static void Adopt(Transform t, Transform parent)
        {
            t.SetParent(parent, false);
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
        }

        void OnDestroy()
        {
            if (_root != null) Destroy(_root.gameObject);
        }
    }
}
