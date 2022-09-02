
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class VRCDynamicPosterTrigger : UdonSharpBehaviour
{
    [Header("Interact時のEvent名")]
    [SerializeField]
    string EventName = "";

    [Header("[変更不要] VRCDynamicPosterPrefabCoreを指定")]
    [SerializeField]
    public VRCDynamicPosterCore Core = null;

    void Start()
    {
    }

    public override void Interact()
    {
        if (Core == null || string.IsNullOrWhiteSpace(EventName))
        {
            return;
        }
        Core.SendCustomEvent(EventName);
    }
}
