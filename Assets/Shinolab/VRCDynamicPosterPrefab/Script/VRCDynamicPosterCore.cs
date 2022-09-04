#define DEBUG_ENABLE_PRINT_DEBUG_LOG
#define DEBUG_ENABLE_PRINT_DEBUG_WARN
#define DEBUG_ENABLE_PRINT_DEBUG_ERROR
#define DEBUG_ENABLE_SKIP_DELAY_LOAD_FIRST_TIME // Unity再生時はDelayLoadFirstTimeを無視する
// #define DEBUG_PRINT_DETAIL_PARSE_ID // Parse結果系の不具合が出たときに使う

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.SDK3.Video.Components;
using System.Text;


[AddComponentMenu("VRCDynamicPosterCore")]
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class VRCDynamicPosterCore : UdonSharpBehaviour
{
    /// <summary>
    /// VRCDynamicPosterの公開場所。最後のスラッシュ含む
    /// </summary>
    const string BaseUrl = "https://shino-hinaduki.github.io/VRCDynamicPoster/";

    #region 挙動設定
    [Header("読み込み開始までの秒数。 他VideoPlayer系と時間が空くように設定する")]
    [SerializeField]
    float DelayLoadFirstTime = 1.0f;

    [Header("[変更不要] 2回目のVideoPlayer起動までの時間。5s以上必須")]
    [SerializeField]
    float DelayLoadSecondTime = 6.0f;

    [Header("[変更不要] VideoPlayer関連で予期せぬエラーが発生して処理が止まっていた場合の検知までの時間")]
    [SerializeField]
    float AbortDuration = 20.0f;

    [Header("読み込み失敗時、Retryする回数上限。Poster, VideoPlayerが多いワールドの場合多めに設定する")]
    [SerializeField]
    int RetryLimitCount = 2;


    [Header("失敗判定からRetry実行までの時間")]
    [SerializeField]
    float DelayRetryTime = 15.0f;

    [Header("自動切り替え有効")]
    [SerializeField]
    bool IsEnableAutoUpdate = true;

    [Header("自動切り替え間隔")]
    [SerializeField]
    float AutoUpdateDuration = 10.0f;

    [Header("ポータル/ペデスタル生成有効")]
    [SerializeField]
    bool IsEnableMakePortal = true;

    [Header("[変更不要] ポータル生成対象")]
    [SerializeField]
    VRC_PortalMarker TargetPortal;

    [Header("[変更不要] ペデスタル生成対象")]
    [SerializeField]
    VRC_AvatarPedestal TargetPedestal;

    [Header("読み込み完了時にポータルを表示する")]
    [SerializeField]
    bool IsVisiblePortalOnInit = false;

    [Header("ポータル生成中は自動切り替え無効")]
    [SerializeField]
    bool IsPreferPortalOverAutoUpdate = true;

    [Header("ポータル生成中にポスターを切り替えた場合に、ポータルを消す")]
    [SerializeField]
    bool IsDismissPortalOnChange = true;

    [Header("読み込み失敗していても、他Playerが成功していた場合その情報をもとにポータル、ペデスタルを更新する")]
    [SerializeField]
    bool IsLeavingToOthers = true;

    [Header("[変更不要] VideoPlayer,Camera関連の待ちframe数")]
    [SerializeField]
    int EventDelayFrameCount = 2;
    #endregion

    #region Decode設定
    [Space(10.0f)]
    [Header("[変更不要] 色判定時のしきい値")]
    [SerializeField]
    float ColorThreshold = 0.5f;

    [Header("[変更不要] Image Decode用に一瞬再生するVideo Player")]
    [SerializeField]
    VRCUnityVideoPlayer DecodeVideoPlayer;

    [Header("[変更不要] Image Decode用に一瞬再生した映像を捉えるCamera")]
    [SerializeField]
    Camera DecodeCamera;

    [Header("[変更不要] DecodeCameraで映した画像")]
    [SerializeField]
    Texture2D DecodeTexture;
    #endregion

    #region 表示関連
    [Space(10.0f)]
    [Header("[変更不要] 実際に表示するVideo Player")]
    [SerializeField]
    VRCUnityVideoPlayer PosterVideoPlayer;
    #endregion

    #region 読み込み先
    [Space(10.0f)]
    [Header("WorldIdをエンコードした動画の配置先")]
    [SerializeField]
    VRCUrl WorldIdVideoUrl = new VRCUrl($"{BaseUrl}index.mp4");

    [Header("実際のポスターを動画化したものの配置先")]
    [SerializeField]
    VRCUrl PosterVideoUrl = new VRCUrl($"{BaseUrl}poster.mp4");
    #endregion

    #region 制御用
    /// <summary>
    /// Camera画角
    /// </summary>
    Rect cameraRect;

    /// <summary>
    /// Capture成功していたらtrue
    /// </summary>
    bool wasSuccessfulCapture = false;

    /// <summary>
    /// 読み込んだIdの配列。色々面倒なのでこれ自体は同期しない
    /// </summary>
    string[] idList = new string[maxEntryCount];

    /// <summary>
    /// Parse出来たエントリ数
    /// </summary>
    int availableEntryCount = 0;

    /// <summary>
    /// Poster動画をいじってよければtrue
    /// </summary>
    bool isAllowSeekPosterVideo = false;

    /// <summary>
    /// 自動切り替え用。最後に切り替えた時刻
    /// </summary>
    float autoUpdateLatestTime = 0.0f;
    /// <summary>
    /// Event発火後に、再度Event起こそうとしたときは素直に一回終わるまで諦める
    /// </summary>
    bool isRunningAutoUpdateEvent = false;
    /// <summary>
    /// Retry回数。この変数はResetVariablesの対象にならない
    /// </summary>
    int retryCount = 0;
    /// <summary>
    /// 読み込み失敗してRetryしない状態の場合にtrue。trueのときはManual Retryを受け付ける
    /// </summary>
    bool isLoadFailed = false;

    #endregion

    #region 制御用定数

    /// <summary>
    /// 1byteあたりのbit数
    /// </summary>
    readonly int bitPerData = 8;

    /// <summary>
    /// データあたりの幅
    /// </summary>
    readonly int widthPerBit = 4;

    /// <summary>
    ///  データあたりの高さ
    /// </summary>
    readonly int heightPerBit = 4;

    /// <summary>
    /// Decodeするエントリ数最大
    /// 変更する場合、生成する動画側の対応に加え、 RenterTexture, Texture, IndexVideoWithQuad のサイズ変更とCameraの調整が必要
    /// </summary>
    const int maxEntryCount = 128;

    /// <summary>
    /// 縦解像度
    /// </summary>
    int textureHeight => maxEntryCount * heightPerBit;
    /// <summary>
    /// WorldIDの先頭
    /// </summary>

    const string worldIdHeader = "wrld_";
    /// <summary>
    /// AvatarIDの先頭
    /// </summary>
    const string avatarIdHeader = "avtr_";

    #endregion

    #region 同期
    /// <summary>
    /// 現在表示中のPosterIndex
    /// </summary>
    [UdonSynced(UdonSyncMode.None), FieldChangeCallback(nameof(CurrentPosterIndex))]
    int currentPosterIndex = 0;
    public int CurrentPosterIndex
    {
        get => currentPosterIndex;
        set
        {
            PrintDebugLog($"[VDPP][FieldChangeCallback] {nameof(CurrentPosterIndex)}: {currentPosterIndex}=>{value}");
            currentPosterIndex = value;

            // UI反映
            SyncUI();
        }
    }


    /// <summary>
    /// Portal表示中ならtrue
    /// </summary>
    [UdonSynced(UdonSyncMode.None), FieldChangeCallback(nameof(IsVisiblePortal))]
    bool isVisiblePortal = false;
    public bool IsVisiblePortal
    {
        get => isVisiblePortal;
        set
        {
            PrintDebugLog($"[VDPP][FieldChangeCallback] {nameof(IsVisiblePortal)}: {isVisiblePortal}=>{value}");
            isVisiblePortal = value;

            // UI反映
            SyncUI();
        }
    }

    /// <summary>
    /// currentPosterIndexが示しているID
    /// </summary>
    [UdonSynced(UdonSyncMode.None), FieldChangeCallback(nameof(CurrentId))]
    string currentId = "";
    public string CurrentId
    {
        get => currentId;
        set
        {
            PrintDebugLog($"[VDPP][FieldChangeCallback] {nameof(CurrentId)}: {CurrentId}=>{value}");
            currentId = value;

            // UI反映
            SyncUI();
        }
    }
    #endregion

    public void Start()
    {
        // 事前チェック
        if (DecodeVideoPlayer == null)
        {
            PrintDebugWarn($"[VDPP] {nameof(DecodeVideoPlayer)} is null.");
            return;
        }
        if (string.IsNullOrWhiteSpace(WorldIdVideoUrl.Get()))
        {
            PrintDebugWarn($"[VDPP] {nameof(WorldIdVideoUrl)} is null or whitespace.");
            return;
        }
        if (DecodeCamera == null)
        {
            PrintDebugWarn($"[VDPP] {nameof(DecodeCamera)} is null.");
            return;
        }
        if (DecodeTexture == null)
        {
            PrintDebugWarn($"[VDPP] {nameof(DecodeTexture)} is null.");
            return;
        }
        if (PosterVideoPlayer == null)
        {
            PrintDebugWarn($"[VDPP] {nameof(PosterVideoPlayer)} is null.");
            return;
        }
        if (TargetPortal == null)
        {
            PrintDebugWarn($"[VDPP] {nameof(TargetPortal)} is null.");
            return;
        }
        if (TargetPedestal == null)
        {
            PrintDebugWarn($"[VDPP] {nameof(TargetPedestal)} is null.");
            return;
        }

        // 初期状態反映
        SyncUI();

        // Videoを再生してリストを取得
        StartLoadIdList();
    }

    #region リスト取得関連

    /// <summary>
    /// 変数初期化
    /// </summary>
    public void InitializeVariables()
    {
        // Videoは一旦止めておく
        StopIndexVideo();
        StopPosterVideo();
        // Cameraも止める
        DisableCamera();
        // 管理変数初期化
        cameraRect = DecodeCamera.pixelRect;
        wasSuccessfulCapture = false;
        idList = new string[maxEntryCount];
        availableEntryCount = 0;
        isAllowSeekPosterVideo = false;
        autoUpdateLatestTime = 0.0f;
        isRunningAutoUpdateEvent = false;
        // retryCountは処理しない
        isLoadFailed = false;
    }

    /// <summary>
    /// ポスターリスト取得処理
    /// </summary>
    public void StartLoadIdList()
    {
        PrintDebugLog($"[VDPP] {nameof(StartLoadIdList)}");

        // 変数初期化
        InitializeVariables();

        // 動画を流す
        var delayTime = DelayLoadFirstTime;
#if DEBUG_ENABLE_SKIP_DELAY_LOAD_FIRST_TIME
        // Unity Playで毎回30secも待ってられない
        if (!IsValidLocalPlayer)
        {
            PrintDebugLog($"[VDPP][DEBUG] detect DEBUG_ENABLE_SKIP_DELAY_LOAD_FIRST_TIME");
            delayTime = 1.0f;
        }
#endif
        PrintDebugLog($"[VDPP] Reserve {nameof(PlayIndexVideo)} for {delayTime}s later");
        SendCustomEventDelayedSeconds(nameof(PlayIndexVideo), delayTime);

    }

    /// <summary>
    /// 動画再生開始
    /// </summary>
    public void PlayIndexVideo()
    {
        PrintDebugLog($"[VDPP] {nameof(PlayIndexVideo)}");

        // 撮影時だけ表示
        DecodeVideoPlayer.gameObject.SetActive(true);
        DecodeVideoPlayer.PlayURL(WorldIdVideoUrl);

        // 失敗時のTimeout監視
        SendCustomEventDelayedSeconds(nameof(AbortLoadIdList), AbortDuration);

        // 再生完了後、StartCaptureが呼び出されて再開
    }

    /// <summary>
    /// Retry監視イベント
    /// </summary>
    public void AbortLoadIdList()
    {
        // ちゃんと読み込めた場合は何もしない
        if (isAllowSeekPosterVideo)
        {
            PrintDebugLog($"[VDPP] {nameof(AbortLoadIdList)}. already succeeded");
            return;
        }
        retryCount++;
        // Retry上限
        if (retryCount > RetryLimitCount)
        {
            isLoadFailed = true;
            PrintDebugLog($"[VDPP] {nameof(AbortLoadIdList)}. Abort. {retryCount}/{RetryLimitCount}");
            // 変数だけ初期化して、余計なものが動かないようにしておく
            InitializeVariables();
            // 救済ポータルが見える状況の場合は見せる
            SyncUI();
            return;
        }
        // Retryする
        PrintDebugLog($"[VDPP] {nameof(AbortLoadIdList)}. Retry. {retryCount}/{RetryLimitCount}");
        SendCustomEventDelayedSeconds(nameof(StartLoadIdList), DelayRetryTime);
    }

    /// <summary>
    /// 動画再生完了。カメラで映す。 IndexVideoPlayerから呼び出し想定
    /// </summary>
    public void StartCapture()
    {
        PrintDebugLog($"[VDPP] {nameof(StartCapture)}");

        // シーケンスでCameraOn->OnPostRenderで絵を確保->CameraOff->VideoOff->Decodeする
        SendCustomEvent(nameof(EnableCamera));
        SendCustomEventDelayedFrames(nameof(DisableCamera), EventDelayFrameCount);
        SendCustomEventDelayedFrames(nameof(StopIndexVideo), EventDelayFrameCount * 2);
        SendCustomEventDelayedFrames(nameof(ParseIdList), EventDelayFrameCount * 3);
    }
    /// <summary>
    /// カメラ有効化
    /// </summary>
    public void EnableCamera()
    {
        PrintDebugLog($"[VDPP] {nameof(EnableCamera)}");

        DecodeCamera.enabled = true;
    }
    /// <summary>
    /// カメラ無効化
    /// </summary>
    public void DisableCamera()
    {
        PrintDebugLog($"[VDPP] {nameof(DisableCamera)}");

        DecodeCamera.enabled = false;
    }
    /// <summary>
    /// Videoを非表示に
    /// </summary>
    public void StopIndexVideo()
    {
        PrintDebugLog($"[VDPP] {nameof(StopIndexVideo)}");

        // 全停止して消す
        DecodeVideoPlayer.Stop();
        DecodeVideoPlayer.gameObject.SetActive(false);
    }
    /// <summary>
    /// 映した画像をTextureに反映
    /// </summary>
    public void OnPostRender()
    {
        // Camera有効ならTexture2Dに転写しておく
        if (DecodeCamera.enabled)
        {
            PrintDebugLog($"[VDPP] {nameof(OnPostRender)}");

            DecodeTexture.ReadPixels(cameraRect, 0, 0, recalculateMipMaps: false);
            DecodeTexture.Apply(updateMipmaps: false);
            wasSuccessfulCapture = true;
        }
    }
    /// <summary>
    /// 取得した画像を解析してWorld一覧にする
    /// 参考: https://github.com/shino-hinaduki/VRCDynamicPoster/blob/master/TextImageGenerator.cs
    /// </summary>
    public void ParseIdList()
    {
        PrintDebugLog($"[VDPP] {nameof(ParseIdList)}");

        if (!wasSuccessfulCapture)
        {
            PrintDebugLog($"[VDPP] capture error. abort");
            return;
        }

        // 結果リスト初期化. List<string>.ToArray()したかった
        idList = new string[maxEntryCount];
        availableEntryCount = 0;

        // 1byte=>8bit*4pixel/bit=>32pixel
        var dataCount = cameraRect.width / bitPerData / widthPerBit;
        // 1entry=>1entry*4pixel/entry=>4pixel
        var entryCount = maxEntryCount;

        for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
        {
            // height/bitの中点を舐めていく。上下逆
            var pixelY = (int)cameraRect.height - (entryIndex * widthPerBit + widthPerBit / 2);
            for (int dataIndex = 0; dataIndex < dataCount; dataIndex++)
            {
                byte dstData = 0x0;
                for (int bitIndex = 0; bitIndex < bitPerData; bitIndex++)
                {
                    // byte単位でシフト + bit単位でシフト + width/pixelの中点
                    var pixelX =
                        (dataIndex * bitPerData * widthPerBit)
                        + (bitIndex * widthPerBit)
                        + (widthPerBit / 2);
                    var pixel = DecodeTexture.GetPixel(pixelX, pixelY);
#if DEBUG_PRINT_DETAIL_PARSE_ID
                    PrintDebugLog($"[VDPP] DecodeTexture.GetPixel({pixelX}, {pixelY}) = ({pixel.a}, {pixel.r}, {pixel.g}, {pixel.b})");
#endif

                    // 劣化する可能性があるので、しきい値を設けて判定したほうが良い
                    if (pixel.r < ColorThreshold && pixel.g > ColorThreshold && pixel.b < ColorThreshold)
                    {
                        // 緑。これ以上は空白なので処理不要
                        PrintDebugLog($"[VDPP] {nameof(ParseIdList)} done.");
                        SendCustomEventDelayedSeconds(nameof(PlayPosterVideo), DelayLoadSecondTime);
                        return;
                    }
                    else if (pixel.r < ColorThreshold && pixel.g < ColorThreshold && pixel.b < ColorThreshold)
                    {
                        // 黒
                    }
                    else if (pixel.r > ColorThreshold && pixel.g > ColorThreshold && pixel.b > ColorThreshold)
                    {
                        // 白
                        dstData |= (byte)(0x1 << bitIndex);
                    }
                    else
                    {
                        PrintDebugError($"[VDPP] {nameof(ParseIdList)} NONE, TRUE, FALSE以外のpixel値を検出. {nameof(pixelX)}={pixelX}, {nameof(pixelY)}={pixelY}, pixel={pixel}");
                        return;
                    }
                }
                // 出来たデータをとっておく
                idList[entryIndex] += $"{(char)dstData}";
#if DEBUG_PRINT_DETAIL_PARSE_ID
                PrintDebugLog($"[VDPP] ({entryIndex}, {dataIndex}) (byte):{dstData}, (char){(char)dstData}");
#endif
            }
            // wrldもしくはavtrでなければ止める
            if ((idList[entryIndex].IndexOf(worldIdHeader) == -1) && (idList[entryIndex].IndexOf(avatarIdHeader) == -1))
            {
                PrintDebugLog($"[VDPP] {nameof(ParseIdList)} invalid entry. parse done. availableEntryCount={availableEntryCount} list[{entryIndex}]={idList[entryIndex]}");
                break;
            }
            availableEntryCount++;
            PrintDebugLog($"[VDPP] {nameof(ParseIdList)} add. availableEntryCount={availableEntryCount} list[{entryIndex}]={idList[entryIndex]}");
        }

        // 全エントリ処理しきった場合
        PrintDebugLog($"[VDPP] {nameof(ParseIdList)} done.");
        SendCustomEventDelayedSeconds(nameof(PlayPosterVideo), DelayLoadSecondTime);
    }
    #endregion

    #region Poster表示関連
    /// <summary>
    /// 動画再生開始. IndexVideoPlayer起動から5秒経過が必要
    /// </summary>
    public void PlayPosterVideo()
    {
        PrintDebugLog($"[VDPP] {nameof(PlayPosterVideo)}");

        // 有効な要素をParse出来なかった場合は失敗扱いにしてRetryを待つ
        if (availableEntryCount == 0)
        {
            PrintDebugWarn($"[VDPP] {nameof(PlayPosterVideo)} failed. availableEntryCount=0");
            return;
        }

        // 動画再生してPauseしておく
        PosterVideoPlayer.gameObject.SetActive(true);
        PosterVideoPlayer.PlayURL(PosterVideoUrl);
        // Seek位置を同期はOnVideoReadyになった後, InitialSeekPosterVideoで実施
    }

    /// <summary>
    /// 動画準備完了時の初回シーク. PosterVideoPlayerから呼び出し想定
    /// </summary>
    public void InitialSeekPosterVideo()
    {
        PrintDebugLog($"[VDPP] {nameof(InitialSeekPosterVideo)}");

        // 以後同期許可
        isAllowSeekPosterVideo = true;

        // 初期状態.
        if (IsOwner)
        {
            currentPosterIndex = 0;
            IsVisiblePortal = IsVisiblePortalOnInit;
            currentId = idList[currentPosterIndex];
            RequestSerialization();
        }

        // 同期
        SyncUI();

        // 自動Update. PC Debug時かOwnerは仕込む
        if ((!IsValidLocalPlayer) || IsOwner)
        {
            // 自動更新有効なら予約しておく
            if (IsEnableAutoUpdate)
            {
                ReserveNextAutoUpdate();
            }
        }
    }

    /// <summary>
    /// Videoを非表示に
    /// </summary>
    public void StopPosterVideo()
    {
        PrintDebugLog($"[VDPP] {nameof(StopPosterVideo)}");
        if (PosterVideoPlayer == null) return; // Startで変数チェック前に使っているので念のため

        // 全停止して消す
        PosterVideoPlayer.Stop();
        PosterVideoPlayer.gameObject.SetActive(false);
        isAllowSeekPosterVideo = false;
    }

    /// <summary>
    /// 現在の同期変数をもとにPoster表示を変える
    /// </summary>
    public void SyncSeekPosterVideo()
    {
        // 準備できていない場合
        if (!isAllowSeekPosterVideo || (availableEntryCount == 0) || (CurrentPosterIndex >= availableEntryCount))
        {
            PrintDebugLog($"[VDPP] Postervideo is not available.");
            StopPosterVideo();
            return;
        }

        // 画像が一定時間で切り替わる動画になっているので、中央の時刻に移動
        var all = PosterVideoPlayer.GetDuration();
        var interval = all / availableEntryCount;
        var target = Mathf.Min((CurrentPosterIndex * interval) + (interval * 0.5f), all); // もしバグっても末尾超えないように対策
        PrintDebugLog($"[VDPP] {nameof(SyncSeekPosterVideo)} index={CurrentPosterIndex}, all={all}, interval={interval}, target={target}");

        PosterVideoPlayer.Pause();
        PosterVideoPlayer.SetTime(target);
    }

    #endregion

    #region Portal制御

    /// <summary>
    /// 現在の同期変数をもとにPortal更新
    /// </summary>
    public void SyncPortal()
    {
        string id = idList[CurrentPosterIndex];

        // 準備できていない場合
        if ((availableEntryCount == 0) || (CurrentPosterIndex >= availableEntryCount))
        {
            if (IsLeavingToOthers && !string.IsNullOrWhiteSpace(currentId))
            {
                // 他の人の同期結果を使って救済して続行
                PrintDebugLog($"[VDPP] Leaving to others. id={currentId}");
                id = currentId;
            }
            else
            {
                // 終了
                PrintDebugLog($"[VDPP] TargetPortal/Pedestal is not available.");
                TargetPortal.gameObject.SetActive(false);
                TargetPedestal.gameObject.SetActive(false);
                return;
            }
        }

        // 表示状態切替
        if (IsVisiblePortal)
        {
            if (id.IndexOf(worldIdHeader) != -1)
            {
                // World on 
                TargetPortal.roomId = id;
                TargetPortal.RefreshPortal();
                TargetPortal.gameObject.SetActive(true);
                // Avatar off
                TargetPedestal.gameObject.SetActive(false);

                PrintDebugLog($"[VDPP] TargetPortal active. index={CurrentPosterIndex} roomId={id}");
            }
            else if (id.IndexOf(avatarIdHeader) != -1)
            {
                // World off
                TargetPortal.gameObject.SetActive(false);
                // Avatar on
                TargetPedestal.blueprintId = id;
                TargetPedestal.gameObject.SetActive(true);

                PrintDebugLog($"[VDPP] TargetPedestal active. index={CurrentPosterIndex} roomId={id}");
            }
            else
            {
                TargetPortal.gameObject.SetActive(false);
                TargetPedestal.gameObject.SetActive(false);
                PrintDebugWarn($"[VDPP] TargetPortal deactive. invalid id={id}");
            }
        }
        else
        {
            // 非表示
            TargetPortal.gameObject.SetActive(false);
            TargetPedestal.gameObject.SetActive(false);
            PrintDebugLog($"[VDPP] TargetPortal/Pedestal deactive.");
        }
    }

    #endregion

    #region 操作関連
    /// <summary>
    /// Poster, Portal同期
    /// </summary>
    public void SyncUI()
    {
        SyncSeekPosterVideo();
        SyncPortal();
    }

    /// <summary>
    /// 事前にデータの準備ができているか確認してOwnerを映す
    /// </summary>
    /// <returns>データが準備できていないか、Owner移せなかった</returns>
    public bool MoveOwner()
    {
        // データ未準備
        if ((!isAllowSeekPosterVideo) || (availableEntryCount == 0))
        {
            PrintDebugLog($"[VDPP] {nameof(MoveOwner)}. data is not available.");
            return false;
        }
        // Owner奪う
        if (IsValidLocalPlayer && (!IsOwner))
        {
            Networking.SetOwner(Networking.LocalPlayer, this.gameObject);
            if (!IsOwner)
            {
                PrintDebugLog($"[VDPP] ownership does not moved. by={Networking.LocalPlayer.displayName}:{Networking.LocalPlayer.playerId}");
                return false;
            }
        }
        // 操作OK
        return true;
    }

    /// <summary>
    /// 次のPosterに送る
    /// </summary>
    public void MoveNext()
    {
        // 前提チェック
        if (!MoveOwner())
        {
            PrintDebugLog($"[VDPP] {nameof(MoveNext)}. data is not available.");
            return;
        }

        // 更新して反映
        currentPosterIndex = (currentPosterIndex + 1) % availableEntryCount;
        currentId = idList[currentPosterIndex];
        PrintDebugLog($"[VDPP] {nameof(MoveNext)}. {currentPosterIndex}/{availableEntryCount} id={currentId}");
        if (IsDismissPortalOnChange)
        {
            IsVisiblePortal = false;
        }
        RequestSerialization();

        // 自身のUIに反映。ほかはFieldChangeCallback発火
        SyncUI();

        // 自動更新有効なら予約しておく
        if (IsEnableAutoUpdate)
        {
            ReserveNextAutoUpdate();
        }
    }

    /// <summary>
    /// 前のポスターに戻る
    /// </summary>
    public void MovePrevious()
    {
        // 前提チェック
        if (!MoveOwner())
        {
            PrintDebugLog($"[VDPP] {nameof(MovePrevious)}. data is not available.");
            return;
        }

        // 更新して反映
        currentPosterIndex = (currentPosterIndex > 0) ? (currentPosterIndex - 1) : (availableEntryCount - 1);
        currentId = idList[currentPosterIndex];
        PrintDebugLog($"[VDPP] {nameof(MovePrevious)}. {currentPosterIndex}/{availableEntryCount} id={currentId}");
        if (IsDismissPortalOnChange)
        {
            IsVisiblePortal = false;
        }
        RequestSerialization();

        // 自身のUIに反映。ほかはFieldChangeCallback発火
        SyncUI();

        // 自動更新有効なら予約しておく
        if (IsEnableAutoUpdate)
        {
            ReserveNextAutoUpdate();
        }
    }

    /// <summary>
    /// Portal表示切替
    /// </summary>
    public void TogglePortal()
    {
        // 前提チェック
        if (!MoveOwner())
        {
            PrintDebugLog($"[VDPP] {nameof(TogglePortal)}. data is not available.");
            return;
        }

        // 更新して反映
        if (IsEnableMakePortal && !isVisiblePortal)
        {
            IsVisiblePortal = true;
        }
        else
        {
            isVisiblePortal = false;
        }
        PrintDebugLog($"[VDPP] {nameof(TogglePortal)}. visible:{isVisiblePortal}");
        RequestSerialization();
        // 自身のUIに反映。ほかはFieldChangeCallback発火
        SyncUI();

    }

    /// <summary>
    /// 次回のMoveNextを登録
    /// </summary>
    public void ReserveNextAutoUpdate()
    {
        // 前提チェック
        if (!MoveOwner())
        {
            PrintDebugLog($"[VDPP] {nameof(ReserveNextAutoUpdate)}. data is not available.");
            return;
        }
        // 現在時刻を保持して後は発火後のイベントに任せる。すでに開始していた場合LatestTimeとの差で一回捨てる
        autoUpdateLatestTime = Time.time;
        if (isRunningAutoUpdateEvent)
        {
            PrintDebugLog($"[VDPP] {nameof(ReserveNextAutoUpdate)}. already reserved. time={autoUpdateLatestTime}");
            return;
        }

        PrintDebugLog($"[VDPP] {nameof(ReserveNextAutoUpdate)}. reserved. time={autoUpdateLatestTime}");

        isRunningAutoUpdateEvent = true;
        SendCustomEventDelayedSeconds(nameof(OnNextAutoUpdate), AutoUpdateDuration);
    }

    /// <summary>
    /// ReserveNextAutoUpdateで次回更新トリガを予約したあとの処理。Owner保ったままならMoveNext
    /// </summary>
    public void OnNextAutoUpdate()
    {
        // ローカル管理フラグは落とす
        isRunningAutoUpdateEvent = false;

        // Owner移動済。後は現在のOwnerに任せる
        if (IsValidLocalPlayer && (!IsOwner))
        {
            PrintDebugLog($"[VDPP] {nameof(OnNextAutoUpdate)}. ownership moved");
            return;
        }
        // Portal召喚中は自動変更しない
        if (IsPreferPortalOverAutoUpdate && IsVisiblePortal)
        {
            PrintDebugLog($"[VDPP] {nameof(OnNextAutoUpdate)}. Portal visible.");
            return;
        }
        // 経過時間が適切なら発火
        var deltaTime = (Time.time - autoUpdateLatestTime);
        if (deltaTime > (AutoUpdateDuration * 0.9f)) // ぴったりだと判定辛いかもなので
        {
            PrintDebugLog($"[VDPP] {nameof(OnNextAutoUpdate)}. trigger. delta={deltaTime}");
            MoveNext();
        }
        else
        {
            PrintDebugLog($"[VDPP] {nameof(OnNextAutoUpdate)}. skip. delta={deltaTime}");
        }
        // 自動更新有効なら予約しておく
        if (IsEnableAutoUpdate)
        {
            ReserveNextAutoUpdate();
        }
    }
    #endregion

    #region 便利系
    /// <summary>
    /// LocalPlayerが有効ならtrue
    /// </summary>
    bool IsValidLocalPlayer => (Networking.LocalPlayer != null) && Networking.LocalPlayer.IsValid();

    /// <summary>
    /// 自身がOwnerならtrue
    /// </summary>
    bool IsOwner => IsValidLocalPlayer && Networking.IsOwner(Networking.LocalPlayer, this.gameObject);

    #region Debug.Log邪魔なときに切る用
    void PrintDebugLog(string msg)
    {
#if DEBUG_ENABLE_PRINT_DEBUG_LOG
        Debug.Log(msg);
#endif
    }
    void PrintDebugWarn(string msg)
    {
#if DEBUG_ENABLE_PRINT_DEBUG_WARN
        Debug.LogWarning(msg);
#endif
    }
    void PrintDebugError(string msg)
    {
#if DEBUG_ENABLE_PRINT_DEBUG_ERROR
        Debug.LogError(msg);
#endif
    }
    #endregion

    #endregion
}
