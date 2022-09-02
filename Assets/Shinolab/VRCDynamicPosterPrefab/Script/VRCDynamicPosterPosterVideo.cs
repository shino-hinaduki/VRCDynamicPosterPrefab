
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// Core componentへのイベント通知用
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class VRCDynamicPosterPosterVideo : UdonSharpBehaviour
{
    [Header("[変更不要] VRCDynamicPosterPrefabCoreを指定")]
    [SerializeField]
    public VRCDynamicPosterCore Core = null;

    void Start()
    {
    }

    public override void OnVideoEnd()
    {
        if (Core == null)
        {
            Debug.LogWarning("[VDPV] PosterVideo.Core is null");
            return;
        }
        Core.SendCustomEvent("InitialSeekPosterVideo");
    }

    public override void OnVideoReady()
    {
        if (Core == null)
        {
            Debug.LogWarning("[VDPV] PosterVideo.Core is null");
            return;
        }
        Core.SendCustomEvent("InitialSeekPosterVideo");
    }
}
