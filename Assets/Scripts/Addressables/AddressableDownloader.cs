using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

/// <summary>
/// Addressable 다운로드 수행 클래스.
/// 초기화 → 카탈로그 갱신 → 다운로드 크기 확인 → 번들 다운로드 순서로 진행하고,
/// 단계마다 DownloadEvents로 결과와 진행도를 알린다.
/// 번들 URL은 런타임에 현재 CDN 주소로 바꿔서 요청한다. (개발 / 라이브 CDN 전환)
/// </summary>
public class AddressableDownloader
{
    /// <summary>
    /// 다운로드 받을 URL.
    /// Addressable Profile의 LoadPath에 {AddressableDownloader.DownloadURL}로 설정한다.
    /// (클래스가 namespace 안에 있으면 Addressables가 읽지 못하니 주의)
    /// </summary>
    public static string DownloadURL => DownloadController.DownloadURL;

    private const string CatalogFileName = "catalog_0.0.1.json";

    DownloadEvents Events;

    /// <summary> 다운로드 받을 Label </summary>
    string LabelToDownload;

    /// <summary> 다운로드 받을 전체 사이즈 </summary>
    long TotalSize;

    /// <summary> 번들 다운로드 핸들 - 비동기로 진행되는 다운로드의 진행 상황을 이 핸들로 확인 </summary>
    AsyncOperationHandle DownloadHandle;

#if UNITY_EDITOR
    /// <summary>에디터에서 Play Mode Script가 "Use Existing Build"(실제 번들 사용)인지</summary>
    private static bool IsUseExistingBuild =>
        UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings.ActivePlayModeDataBuilderIndex == 2;
#endif

    /// <summary> Addressable 시스템 초기화하기 </summary>
    public DownloadEvents InitializedSystem(string label)
    {
        Events = new DownloadEvents();

#if UNITY_EDITOR
        if (IsUseExistingBuild)
            Addressables.ResourceManager.InternalIdTransformFunc = TransformURL;
#else
        Addressables.ResourceManager.InternalIdTransformFunc = TransformURL;
#endif

        Addressables.InitializeAsync().Completed += OnInitialized;
        LabelToDownload = label;

        // 여러 번 초기화해도 핸들러가 중복 등록되지 않게 한다
        ResourceManager.ExceptionHandler -= OnException;
        ResourceManager.ExceptionHandler += OnException;

        return Events;
    }

    /// <summary>
    /// 번들 요청 URL 변환: 빌드 시점에 박힌 CDN 주소(개발 / 라이브)를 떼어 내고 현재 DownloadURL로 바꾼다.
    /// 번들을 다시 빌드하지 않고 서버 설정만으로 CDN을 바꿀 수 있다.
    /// </summary>
    private string TransformURL(IResourceLocation location)
    {
        if (location.ResourceType != typeof(IAssetBundleResource))
            return location.InternalId;

        string internalId = location.InternalId;
        string relativePath = internalId;

        string[] knownBases = { DownloadController.DEV_CDN_URL, DownloadController.LIVE_CDN_URL };
        foreach (var baseUrl in knownBases)
        {
            if (internalId.StartsWith(baseUrl))
            {
                relativePath = internalId.Substring(baseUrl.Length);
                break;
            }
        }

        return DownloadURL.TrimEnd('/') + "/" + relativePath.TrimStart('/');
    }

    /// 다운로드 받을 크기 확인하기
    public void DownloadSize()
    {
        Addressables.GetDownloadSizeAsync(LabelToDownload).Completed += OnSizeDownloaded;
    }

    /// <summary>
    /// 카탈로그 업데이트하기.
    /// 현재 CDN의 플랫폼별 카탈로그를 직접 로드하고, 새 카탈로그와 ID가 다른 예전 로케이터는 제거한다.
    /// (에디터에서 실제 번들을 쓰지 않을 때는 기본 CheckForCatalogUpdates 흐름 사용)
    /// </summary>
    public async UniTask UpdateCatalog()
    {
#if UNITY_EDITOR
        if (!IsUseExistingBuild)
        {
            Addressables.CheckForCatalogUpdates().Completed += result =>
            {
                var catalogToUpdate = result.Result;

                if (catalogToUpdate.Count > 0)
                    Addressables.UpdateCatalogs(catalogToUpdate, true).Completed += OnCatalogUpdated;
                else
                    Events.NotifyCatalogUpdated();
            };
            return;
        }
#endif

#if UNITY_IOS
        const string platform = "iOS";
#else
        const string platform = "Android";
#endif
        string catalogURL = $"{DownloadURL}/{platform}/{CatalogFileName}";
        Debug.Log($"[Addressables] Loading catalog from: {catalogURL}");

        List<IResourceLocator> oldLocators = Addressables.ResourceLocators.ToList();
        var handle = Addressables.LoadContentCatalogAsync(catalogURL, false);

        try
        {
            await handle.ToUniTask();

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                Debug.LogError($"[Addressables] Catalog load failed: {catalogURL}");
                return;
            }

            IResourceLocator newLocator = handle.Result;
            foreach (var locator in oldLocators)
            {
                if (locator.LocatorId != newLocator.LocatorId)
                    Addressables.RemoveResourceLocator(locator);
            }

            Debug.Log($"[Addressables] Catalog loaded: {newLocator.LocatorId}");
            Events.NotifyCatalogUpdated();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Addressables] Catalog load exception: {ex.Message}");
        }
        finally
        {
            handle.Release();
        }
    }

    /// 번들 다운로드 시작하기
    public void StartDownload()
    {
        DownloadHandle = Addressables.DownloadDependenciesAsync(LabelToDownload, false);
        DownloadHandle.Completed += OnDependenciesDownloaded;
    }

    /// 매 프레임 호출 - 다운로드 진행 상황 보고
    public void Update()
    {
        if (!DownloadHandle.IsValid() || DownloadHandle.IsDone || DownloadHandle.Status == AsyncOperationStatus.Failed)
            return;

        var status = DownloadHandle.GetDownloadStatus();
        long remainedSize = TotalSize - status.DownloadedBytes;

        Events.NotifyDownloadProgress(new DownloadProgressStatus(
            status.DownloadedBytes,
            TotalSize,
            remainedSize,
            status.Percent));
    }

    //--------------------------------------------------------//
    /// 초기화 완료시 호출
    private void OnInitialized(AsyncOperationHandle<IResourceLocator> result)
    {
        Addressables.Release(result);
        Events.NotifyInitialized();
    }

    /// 카탈로그 업데이트 완료시 호출
    void OnCatalogUpdated(AsyncOperationHandle<List<IResourceLocator>> result)
    {
        Events.NotifyCatalogUpdated();
    }

    /// 다운로드 크기 확인 완료시 호출
    void OnSizeDownloaded(AsyncOperationHandle<long> result)
    {
        TotalSize = result.Result;
        Events.NotifySizeDownloaded(result.Result);
    }

    /// 번들 다운로드 완료시 호출
    void OnDependenciesDownloaded(AsyncOperationHandle result)
    {
        bool isSuccess = result.Status == AsyncOperationStatus.Succeeded;
        Addressables.Release(DownloadHandle);
        Events.NotifyDownloadFinished(isSuccess);
    }

    /// 예외 발생시 호출
    void OnException(AsyncOperationHandle handle, Exception exception)
    {
        Debug.LogError(exception.Message);
    }
}
