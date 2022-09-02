
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class VRCDynamicPosterAvatarPedestal : UdonSharpBehaviour
{
    [SerializeField]
    VRC_AvatarPedestal TargetPedestal;

    void Start()
    {
    }

    public override void Interact()
    {
        // Unity Debug
        if (Networking.LocalPlayer == null) return;
        // Pedestal not ready
        if (TargetPedestal == null) return;

        TargetPedestal.SetAvatarUse(Networking.LocalPlayer);
    }
}
