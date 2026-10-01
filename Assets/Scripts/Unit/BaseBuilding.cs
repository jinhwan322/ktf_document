using Cysharp.Threading.Tasks;
using Spine.Unity;
using System;
using UniRx;
using UnityEngine;
using UnityEngine.Rendering;
using static Define;

public enum EBuildingState
{
    Idle,
    Action,
    Clear
}

/// <summary>
/// 유저가 설치한 건물 정보. 건설 여부, 레벨, 생산 재화 수량, 마지막 생산 정산 시간을 가진다.
/// </summary>
public class BuildingData
{
    public BuildingInfo _data;
    public ReactiveProperty<bool> _isBuild = new ReactiveProperty<bool>();
    public int _level;
    public ReactiveProperty<int> _currencyCount = new ReactiveProperty<int>();
    public DateTime _syncTime;
    public int _storageTime;
    public bool _isOpening = false; // 오픈 연출 대기

    /// <summary>건설됐고 오픈 조건도 만족해서 사용할 수 있는지</summary>
    public bool isOpen => _isBuild.Value && isCondition;

    /// <summary>오픈 조건 (유저 레벨 / 선행 건물 / 가이드 퀘스트 / 대화 진행도)</summary>
    public bool isCondition
    {
        get
        {
            UserInfoData userInfoData = Managers.Instance.UserInfo();
            int conditionValue = _data.BuildOpenConditionValue;
            switch (_data.BuildOpenConditionType)
            {
                case EBuildingOpenConditionType.UserLevel:
                    return userInfoData.userLevel.Value >= conditionValue;
                case EBuildingOpenConditionType.BuildingOpen:
                    return userInfoData.GetInstallationBuilding(conditionValue)._isBuild.Value;
                case EBuildingOpenConditionType.GuideQuestClearID:
                case EBuildingOpenConditionType.DungeonQuestClearID:
                    return userInfoData._currentGuideQuestId.Value > conditionValue;
                case EBuildingOpenConditionType.Dialogue:
                    return userInfoData.dialogKey.Value >= conditionValue;
                default:
                    return true;
            }
        }
    }

    /// <summary>서버에서 받은 마지막 정산 시간으로 맞춘다.</summary>
    public void UpdateSyncTime(DateTime syncTime)
    {
        _syncTime = syncTime;
    }

    /// <summary>
    /// 마지막 정산 이후 지난 시간만큼 생산 재화를 쌓는다. (생산 주기마다 BuildingProductionItemCount개, 최대치까지)
    /// 생산 주기에 못 미친 남은 시간은 다음 정산으로 넘긴다.
    /// </summary>
    public void UpdateSyncTime()
    {
        BuildingLevelInfo buildingLevelInfo = ClientLocalDB_Simple.GetData<BuildingLevelInfo>(DBKey.BuildingLevelInfo, $"{_data.ID}_{_level}");
        int productTime = buildingLevelInfo.BuildingProductionTime / 100;
        if (productTime <= 0)
            return;

        DateTime now = ServerTime.Instance.CurrentTime();
        TimeSpan remain = now - _syncTime;
        if (remain.TotalSeconds < productTime)
            return;

        int tick = (int)remain.TotalSeconds / productTime;
        int addCount = tick * buildingLevelInfo.BuildingProductionItemCount;
        int maxCount = buildingLevelInfo.BuildingProductionItemCountMax;
        _currencyCount.Value = Mathf.Min(maxCount, _currencyCount.Value + addCount);

        // 가득 찼으면 지금부터 다시 계산하고, 아니면 생산 주기에 맞춰 정산 시간만 앞으로 옮긴다
        _syncTime = _currencyCount.Value >= maxCount ? now : _syncTime.AddSeconds(tick * productTime);
    }
}

/// <summary>
/// 필드에 설치되는 건물(워프 포인트, 훈련소, 석상, 유물, 주점, 제작소, 던전 입구 등).
/// 스쿼드가 범위 안에 들어오면 말풍선 버튼을 띄우고, 주변에 적이 있으면 버튼을 막는다.
/// 버튼을 누르면 건물 타입에 맞는 UI를 열거나 던전으로 이동한다.
///
/// 상속: InstallationBuilding(설치 건물), StorageBuilding(생산 / 저장), PortalBuilding(포털),
///       FieldDungeonBuilding / FogDungeonBuilding(던전 입구), FogPointBuilding(안개 지점),
///       FieldQuestBuilding(필드 퀘스트), RewardBoxBuilding / TreasureBoxBuilding(보상 / 보물 상자)
/// </summary>
public class BaseBuilding : FieldObject
{
    public BuildingData BuildingData => UserInfoData.GetInstallationBuilding(_idx);

    public int _idx;
    protected UISpeechBox _SpeechBox;
    protected int _zoneIndex;
    protected EBuildingState _buildingState;
    protected EBuildingType BuildingType => BuildingData._data.BuildingType;
    protected Squad Squad => Managers.Instance.GetObjectUnitManager().playerSquad;
    protected MapManager MapManager => Managers.Instance.GetMapManager();
    protected UserInfoData UserInfoData => Managers.Instance.UserInfo();
    protected UIManager UIManager => Managers.Instance.GetUIManager();

    public int _enemyCount;     // 건물 범위 안의 적 수
    protected bool _enterSquad; // 스쿼드가 건물 범위 안에 있는지

    public override void Awake()
    {
        base.Awake();
        _rigidbody.constraints = RigidbodyConstraints2D.FreezeAll;
        _rigidbody.freezeRotation = true;
        _collider.radius = 8.0f;    // 상호작용 범위
        gameObject.transform.SetParent(Managers.Instance.GetObjectUnitManager().BuildingTrans);
    }

    public virtual async UniTask Init(int idx)
    {
        _idx = idx;
        _collider.isTrigger = true;

        // FieldObject와 같은 이유로 SortingGroup 사용 (Spine MeshRenderer 정렬)
        SortingGroup tSort = Utils.GetOrAddComponent<SortingGroup>(gameObject);
        tSort.sortingOrder = SortingLayers.UNIT;

        await SetSpine();
    }

    protected virtual async UniTask SetSpine()
    {
        string spineResource = BuildingData._data.Resource;
        SkeletonDataAsset skeletonData = await Managers.Instance.GetResObjectManager().LoadAsync<SkeletonDataAsset>(
                $"{spineResource}/{spineResource}_SkeletonData.asset");
        if (skeletonData == null)
            return;

        _spineAnimation.SetSpine(skeletonData);
    }

    /// <summary>말풍선 버튼 클릭: 건물 타입에 맞는 UI를 열거나 던전으로 이동한다.</summary>
    public virtual void SuccessSpeechBtnClick()
    {
        switch (BuildingData._data.BuildingType)
        {
            case EBuildingType.WarpPoint:
                UIManager.UIWarpPointPopup.OpenToStack();
                break;
            case EBuildingType.Training:
                UIManager.TrainingUI.OpenToStack();
                break;
            case EBuildingType.Statue:
                UIManager.UIEquipmentSetting.OpenToStack();
                break;
            case EBuildingType.Relic:
                UIManager.UIRelic.OpenToStack();
                break;
            case EBuildingType.Tavern:
                UIManager.OpenGachaUI(EGachaType.PickUp);
                break;
            case EBuildingType.Craft:
                UIManager.UICraft.Init();
                UIManager.UICraft.OpenToStack();
                break;
            case EBuildingType.Dungeon:
                EnterDungeon(EContent.Fog);
                break;
            case EBuildingType.FieldDungeon:
                EnterDungeon(EContent.FieldDungeon);
                break;
        }
    }

    /// <summary>
    /// 필드에서 던전으로 이동한다. 돌아올 위치(존 / 스쿼드 위치)를 저장하고,
    /// 필드 전용 타이머(재화 싱크, 보물상자)를 멈춘 뒤 던전 씬을 로드한다.
    /// </summary>
    private void EnterDungeon(EContent contentType)
    {
        UserInfoData.zoneId = Squad._zoneIndex;
        UserInfoData.squadPosition = Squad.transform.position;

        BattleData battleData = BattleData.Create();
        battleData._contentType = contentType;
        battleData._factionType = EFactionType.None;
        battleData._index = BuildingData._data.CurrencyList[0];
        BattleData.Set(battleData);

        Managers.Instance.GetSyncCurrencyManager().Stop();
        Managers.Instance.GetTreasureBoxManager().Stop();

        Loading.Load(Loading.Dungeon);
    }

    public void SetZoneIndex(int zoneIndex)
    {
        _zoneIndex = zoneIndex;
    }

    public void Save()
    {
        UserInfoData.SaveBuilding();
    }

    public int GetZoneIndex()
    {
        return _zoneIndex;
    }

    protected virtual void OnTriggerEnter2D(Collider2D collision)
    {
        if (_SpeechBox == null)
            return;

        if (collision.CompareTag("Squad"))
            _enterSquad = true;
        else if (collision.CompareTag("Enemy"))
            _enemyCount++;
    }

    protected virtual void OnTriggerExit2D(Collider2D collision)
    {
        if (_SpeechBox == null)
            return;

        if (collision.CompareTag("Squad"))
            _enterSquad = false;
        else if (collision.CompareTag("Enemy"))
            _enemyCount--;
    }

    /// <summary>스쿼드가 범위 안에 있고 건물이 동작 중이 아니면 말풍선을 띄운다. 주변에 적이 있으면 버튼 비활성화</summary>
    private void FixedUpdate()
    {
        if (_SpeechBox == null)
            return;

        if (!_enterSquad || _buildingState == EBuildingState.Action)
        {
            if (_SpeechBox.gameObject.activeSelf)
                _SpeechBox.Close();
        }
        else
        {
            _SpeechBox.Open();
            _SpeechBox.EnableButtonCheck(_enemyCount <= 0);
        }
    }

    private void OnDisable()
    {
        if (_SpeechBox == null)
            return;

        _SpeechBox.Close();
    }

    public virtual void ActiveBuilding()
    {
    }
}
