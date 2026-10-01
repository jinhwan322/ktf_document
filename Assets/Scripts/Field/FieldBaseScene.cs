using Cysharp.Threading.Tasks;
using DG.Tweening;
using MarchingBytes;
using UnityEngine;
using static Define;

/// <summary>
/// 필드 / 던전 씬의 공통 베이스.
/// 플레이어 스쿼드 생성, 카메라 추적, 조이스틱 UI, 맵 로드, 덱 캐릭터 스폰, 데미지 텍스트 풀 생성을 담당한다.
/// </summary>
public class FieldBaseScene : MonoBehaviour
{
    protected MapManager MapManager => Managers.Instance.GetMapManager();
    protected ObjectUnitManager ObjectUnitManager => Managers.Instance.GetObjectUnitManager();
    protected Squad Squad => ObjectUnitManager.playerSquad;
    protected UIManager UIManager => Managers.Instance.GetUIManager();
    protected FollowCamera FollowCamera => Managers.Instance.GetCameraManager().FollowCam;
    protected UserInfoData UserInfo => Managers.Instance.UserInfo();
    protected BestHttp_GameManager BestHttpGameManager => Managers.Instance.GetServerManager();
    protected SyncCurrencyManager SyncCurrencyManager => Managers.Instance.GetSyncCurrencyManager();
    protected TreasureBoxManager TreasureBoxManager => Managers.Instance.GetTreasureBoxManager();

    /// <summary>스쿼드를 만들고 카메라가 따라가게 한 뒤, 조이스틱과 목적지 화살표를 준비한다.</summary>
    protected void Init()
    {
        ObjectUnitManager.playerSquad = ObjectUnitManager.SpawnSquad();

        // 카메라: 스쿼드 추적
        FollowCamera.SetTarget(Squad.transform);
        FollowCamera.SetSmooth(true);
        FollowCamera.smoothSpeed = 0.1f;
        FollowCamera.cameraFixZ = true;
        FollowCamera.LookAtNow();

        // 조이스틱 UI
        UIJoystick uIJoystick = UIManager.ShowUIBase<UIJoystick>("UIJoystick");
        uIJoystick.transform.SetParent(UIManager.UIJoyStickTrans.transform, false);
        uIJoystick.SetJoystick(Managers.Instance.GetJoystick(), Managers.Instance.GetCameraManager().UICam);
        uIJoystick.EnableJoystick(true);

        // 목적지 안내 화살표 (위아래로 계속 흔들리게)
        MapManager._arrowObject = GameObject.Find("ArrowPoint");
        if (MapManager._arrowObject != null)
        {
            MapManager._arrowObject.SetActive(false);
            Transform arrowTransform = MapManager._arrowObject.transform.Find("Arrow");
            arrowTransform.DOLocalMoveY(0.5f, 1f)
                .SetEase(Ease.OutCubic)
                .SetLoops(-1, LoopType.Yoyo);
        }
    }

    /// <summary>필드 맵 로드</summary>
    /// <param name="fieldInfo">필드 DB</param>
    /// <param name="fieldDetail">필드 난이도 정보</param>
    protected async UniTask LoadFieldMap(FieldInfo fieldInfo, FieldDetail fieldDetail)
    {
        MapManager.Init(EContent.Field, EFactionType.None, fieldInfo.ID, fieldDetail.FieldLevel);
        MapManager.SetFieldData(fieldInfo, fieldDetail);
        await MapManager.LoadMap(fieldInfo.Resource);
    }

    /// <summary>던전 맵 로드</summary>
    /// <param name="contentType">컨텐츠 타입</param>
    /// <param name="factionType">종족 타입</param>
    /// <param name="zoneIndex">맵 번호</param>
    /// <param name="dungeonLevel">던전 난이도</param>
    /// <param name="enableJoyStick">조이스틱 조작 허용 여부 (false면 자동 전투 전용)</param>
    protected async UniTask LoadDungeonMap(EContent contentType, EFactionType factionType, int zoneIndex, int dungeonLevel, bool enableJoyStick)
    {
        MapManager.Init(contentType, factionType, 0, dungeonLevel);
        MapManager.SetDungeonData(enableJoyStick);
        await MapManager.LoadMap($"{contentType}DungeonMap_{zoneIndex}");
    }

    /// <summary>전투 테스트용 맵 로드: 지정한 스폰 포인트의 적 레벨 / 스탯 타입을 바꿔서 배치한다.</summary>
    protected async UniTask LoadFieldMap(MapStageMeta mapStageMeta, BattleTestSpawnPointInfo spawnPointInfo)
    {
        await MapManager.LoadMap(mapStageMeta, spawnPointInfo.SpawnPointID);
        SpawnPointInfoUnit spawnPointInfoUnit = MapManager.FindSpawnPoint(spawnPointInfo.SpawnPointID);
        spawnPointInfoUnit._unitList.ForEach(unit => unit.SetLevel(spawnPointInfo.Level, spawnPointInfo.StatusType));
    }

    /// <summary>컨텐츠에 맞는 덱을 스쿼드에 넣고 캐릭터를 스폰한다. (안개 / 필드 던전은 필드 덱 사용)</summary>
    protected async UniTask SpawnCharacter(EContent contentType, EFactionType factionType)
    {
        EServerContentType serverContentType = contentType == EContent.Fog || contentType == EContent.FieldDungeon
            ? EServerContentType.Field
            : ReturnServerDungeonType(contentType, factionType);

        Squad.SetDeckData(UserInfo.GetDeckData(serverContentType));
        await Squad.RefreshSquadCharacter();
    }

    /// <summary>데미지 / 치명타 / 출혈 / 회복 / 중독 / 채집 텍스트 오브젝트 풀 생성</summary>
    protected async UniTask CreateDamageText()
    {
        string[] damageTextNames =
        {
            DamageTextName.Damage,
            DamageTextName.CriticalDamage,
            DamageTextName.Blood,
            DamageTextName.Heal,
            DamageTextName.Poison,
            DamageTextName.Gather,
        };

        foreach (string textName in damageTextNames)
            await EasyObjectPool.instance.CreatePoolInfo(EPoolType.DamageText, textName);
    }
}
