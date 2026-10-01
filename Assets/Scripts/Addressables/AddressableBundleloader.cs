using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

/// <summary>
/// Addressable 번들 로드 수행 클래스.
/// 키(라벨)로 에셋 묶음(DB, 아틀라스 등)을 로드해 콜백으로 넘기고, 로딩 진행도를 알린다.
/// 로드가 끝나면 같은 번들의 예전 캐시 버전을 지워 디스크 사용량을 줄인다.
/// </summary>
public class AddressableBundleloader
{
    public float progress => (float)currentCount / totalCount;
    public int currentCount;
    public int totalCount = 2;

    private Action action;

    /// <summary>진행도가 바뀔 때마다 호출할 콜백을 등록하고 진행도를 초기화한다.</summary>
    public void SetAction(Action updateAction)
    {
        currentCount = 0;
        action = updateAction;
    }

    /// <summary>
    /// assetKey에 해당하는 에셋을 모두 로드해 onLoaded로 넘긴다.
    /// 로드한 에셋은 외부(DB / 아틀라스)에서 계속 참조하므로 여기서 Release하지 않는다.
    /// </summary>
    public async UniTask LoadAssetsAddressableData<T>(string assetKey, Action<List<T>> onLoaded)
    {
        AsyncOperationHandle<IList<T>> handle = Addressables.LoadAssetsAsync<T>(assetKey, _ => { });
        await handle.ToUniTask();

        if (handle.Status == AsyncOperationStatus.Failed || handle.Result == null)
        {
            Addressables.Release(handle);
            OnLoadFailed(assetKey);
            return;
        }

        onLoaded?.Invoke(new List<T>(handle.Result));

        PlusAction();
        CleanBundleCache(assetKey);
    }

    public void PlusAction()
    {
        currentCount++;
        action?.Invoke();
    }

    private void OnLoadFailed(string assetKey)
    {
        Debug.LogError($"[Addressables] Load failed: {assetKey}");
    }

    /// <summary>로드 후 같은 번들의 다른(예전) 캐시 버전을 지운다.</summary>
    private void CleanBundleCache(string key)
    {
        Addressables.LoadResourceLocationsAsync(key).Completed += handle =>
        {
            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                MyLogger.LogWarning($"LoadResourceLocations failed: {handle.OperationException?.Message}");
                Addressables.Release(handle);
                return;
            }

            List<IResourceLocation> dependencies = GatherDependenciesFromLocations(handle.Result);
            Addressables.Release(handle);

            foreach (var dependency in dependencies)
            {
                if (dependency.Data is not AssetBundleRequestOptions requestOption)
                    continue;

                if (!Caching.ClearOtherCachedVersions(requestOption.BundleName, Hash128.Parse(requestOption.Hash)))
                    MyLogger.LogWarning($"ClearOtherCachedVersions failed for {requestOption.BundleName} {requestOption.Hash}");
            }
        };
    }

    /// <summary>로케이션과 그 의존성 중 에셋 번들인 것만 중복 없이 모은다.</summary>
    private List<IResourceLocation> GatherDependenciesFromLocations(IList<IResourceLocation> locations)
    {
        var locHash = new HashSet<IResourceLocation>();
        foreach (var loc in locations)
        {
            if (loc.ResourceType == typeof(IAssetBundleResource))
                locHash.Add(loc);

            if (!loc.HasDependencies)
                continue;

            foreach (var dep in loc.Dependencies)
            {
                if (dep.ResourceType == typeof(IAssetBundleResource))
                    locHash.Add(dep);
            }
        }

        return new List<IResourceLocation>(locHash);
    }
}
