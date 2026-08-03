using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Marks a spot on a rack where one weapon belongs. Position and rotation come from this
    /// transform, so a rack's layout is authored by MOVING these, never by editing code.
    ///
    /// WHY A MARKER AND NOT THE WEAPON ITSELF. A rack placed in build mode is scenery: every
    /// peer builds its own copy locally from the map layout, and nothing about it travels over
    /// the network. That is exactly wrong for a weapon — two players would each get their own
    /// private rifle, pick "the same" one at the same time, and watch it not move in the other's
    /// hands. So the rack carries a promise ("a Rifle 1 goes here") and the SERVER keeps it, by
    /// spawning one real networked weapon per marker.
    ///
    /// <see cref="WeaponRackRespawner"/> then treats these exactly like the hand-placed weapons
    /// it already refills, so a rack built in VR behaves like the ones standing in the scene.
    /// </summary>
    public class WeaponRackSlot : MonoBehaviour
    {
        [Tooltip("Resources/WeaponPrefabs altindaki kalibin ADI, orn. \"Weapon_Rifle 1\".\n\n" +
                 "Isim, indeks DEGIL: Resources.LoadAll'un sirasi platformlar arasi sozlesmeli " +
                 "degil ve indeks anahtar olsaydi Editor host ile Android istemci farkli silah " +
                 "anlardi (WeaponPrefabRegistrar'daki ayni ders).")]
        public string weaponPrefabName;

        /// <summary>Resolves the prefab, or null when the name matches nothing registered.</summary>
        public GameObject Resolve() => WeaponPrefabRegistrar.FindByName(weaponPrefabName);

        void OnDrawGizmosSelected()
        {
            // Yuvanin YONU de onemli: silah bu eksene gore yatiyor. Ok olmadan raf duzenlerken
            // hangi yone bakacagini ancak Play'e girip gorebilirdin.
            Gizmos.color = new Color(0.3f, 1f, 0.45f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 0.04f);
            Gizmos.DrawRay(transform.position, transform.forward * 0.25f);
        }
    }
}
