using Unity.Netcode.Components;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// An owner-authoritative NetworkTransform: the client that owns the object decides its
    /// position/rotation, instead of the server. This is the standard "ClientNetworkTransform"
    /// pattern for VR avatars — the player who wears the headset is the source of truth for
    /// their own head and hands, so movement is instant for them and replicated to everyone else.
    ///
    /// In NGO 2.x this is done by overriding <see cref="OnIsServerAuthoritative"/> to return
    /// false. Works in the standard host/client (client-server) topology used by LAN play.
    /// </summary>
    [DisallowMultipleComponent]
    public class ClientNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative() => false;
    }
}
