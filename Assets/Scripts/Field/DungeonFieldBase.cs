using Cysharp.Threading.Tasks;
using System.Collections;
using System.Linq;
using TMPro;
using UnityEngine;
using static Define;

/// <summary>
/// 던전 씬 공통 베이스. 던전 규칙(DB)에 따라 맵 / UI / 스쿼드 / 캐릭터를 준비하고,
/// 카운트다운 → 전투 → 승리 / 패배 → 결과 처리 흐름을 관리한다.
/// 던전 종류별 승리 조건(전멸, 시간 제한, 점수 등)은 상속한 클래스에서 구현한다.
///
/// 상속: AwakeDungeon(각성 던전), FieldDungeon(필드 던전), GuildBossDungeon(길드 보스),
///       RankingDungeon(랭킹 던전), TimeAttackDungeon(타임 어택)
/// </summary>
public class DungeonFieldBase : FieldBaseScene
{
    public float _gameTime;
    protected int MaxTime;
    protected bool IsTimeOverRule => MaxTime > 0;
    public float GetGameTime { get { return _gameTime; } }
    public bool _isPlaying;
    public bool _battleEnd;

    private const int CountTime = 3;
    private int _countTime;

    protected int _mapIndex;
    protected int _zoneIndex;
    protected int _resurrectionCount;
    protected int MaxResurrectionCount => _dungeonRule.Resurrection;
    protected DungeonRule _dungeonRule;
    protected BattleData _battleData;
    public DungeonInfoUI _dungeonInfoUI;
    protected double _totalPoint;
    protected int _phase;
    protected EDungeonType _dungeonType; // 던전 타입 (상속한 클래스에서 지정)
    protected TMP_Text _countDownText;

    private bool _delay = false;    // 결과 화면 버튼 중복 클릭 방지

    /// <summary>
    /// 던전 진입: 규칙 로드 → 맵 로드 → UI → 스쿼드 → 캐릭터 스폰 → 전투 세팅 → 로딩 화면 닫기.
    /// 안개 / 필드 던전은 시작 대화(카운트다운)로, 그 외 던전은 서버에 입장을 알린 뒤 시작한다.
    /// </summary>
    public virtual async UniTask Init(BattleData battleData)
    {
        var loadingUI = FindObjectOfType<LoadingUI>();
        _delay = false;
        _battleData = battleData;
        _countTime = CountTime;
        if (_countDownText == null)
        {
            GameObject countDownTextObj = GameObject.Find("CountDown");
            if (countDownTextObj != null)
            {
                _countDownText = countDownTextObj.GetComponent<TMP_Text>();
                _countDownText.text = _countTime.ToString();
                _countDownText.gameObject.SetActive(false);
            }
        }

        BattleData.Set(_battleData);
        Init();
        InitValue();
        DungeonDataSetting();
        await LoadDungeonMap(_battleData._contentType, _battleData._factionType, _mapIndex, _battleData._index, _dungeonRule.Joystick);
        DungeonUISetting();
        SquadSetting();
        await SpawnCharacter(_battleData._contentType, _battleData._factionType);
        BattleSetting();
        await CreateDamageText();
        Managers.Instance.GetEconomySystem().gameObject.SetActive(false);

        Managers.Instance.Sound.PlayBGMAsync(_dungeonRule.BGM).Forget();
        if (loadingUI != null)
            await loadingUI.FadeOutAndHideAsync();

        if (IsFieldDungeon)
            OpenStartDialogue();
        else
            BestHttpGameManager.OnPostDungeonRun(_battleData._contentType, _battleData._factionType, _battleData._index);
    }

    /// <summary>필드에서 들어오는 던전(안개 / 필드 던전)인지</summary>
    private bool IsFieldDungeon => _battleData._contentType == EContent.Fog || _battleData._contentType == EContent.FieldDungeon;

    // 변수 초기화
    protected virtual void InitValue()
    {
        _resurrectionCount = 0;
        _isPlaying = false;
        _battleEnd = false;
        _mapIndex = 1;
    }

    // 던전 규칙(DB) 로드: 제한 시간, 부활 횟수, 조이스틱 / 자동 전투 허용 등
    protected virtual void DungeonDataSetting()
    {
        _dungeonRule = ClientLocalDB_Simple.GetData<DungeonRule>(DBKey.DungeonRule, _dungeonType.ToString());
        MaxTime = _dungeonRule.TimeLimit / 100;
    }

    // 스쿼드 세팅: 시작 위치로 이동, 부활 비활성화, 대열 거리 설정, 전멸 시 패배 처리 연결
    protected virtual void SquadSetting()
    {
        Squad.Init(Managers.Instance.UserInfo());
        _zoneIndex = MapManager.GetZoneIndex(0);
        Squad._zoneIndex = _zoneIndex;
        Squad.ForceSquadMove(_zoneIndex, MapManager.GetStartPosition(_zoneIndex));
        Squad.SetResurrectionState(false);
        Squad.SetDistanceValue(_dungeonRule.BasicDistance, _dungeonRule.LimitDistance);
        Squad.BattleStop();
        Squad.SetGameOverAction(() => GameOver().Forget());
    }

    protected virtual void DungeonUISetting()
    {
        _dungeonInfoUI = UIManager.ShowUIBase<DungeonInfoUI>("DungeonInfoUI");
        _dungeonInfoUI.EnableTime(IsTimeOverRule);
        _dungeonInfoUI.SetAutoAction(SquadAutoMode);
        _dungeonInfoUI.OpenToStack();
    }

    /// <summary>
    /// 전투 세팅. 조이스틱을 막은 던전은 항상 자동 전투이고,
    /// 조이스틱과 자동 전투를 모두 허용하는 던전만 AUTO 버튼을 보여 준다.
    /// </summary>
    protected virtual void BattleSetting()
    {
        Squad.ForceSquadMove(_zoneIndex, MapManager.GetStartPosition(_zoneIndex));
        _dungeonInfoUI.ActiveAutoButton(_dungeonRule.Joystick && _dungeonRule.EnableAuto);
        _dungeonInfoUI.SetAutoMode(!_dungeonRule.Joystick || (_dungeonRule.EnableAuto && MapManager.IsDungeonAutoMode));
        FollowCamera.transform.position = Squad.transform.position;
        FollowCamera.LookAtNow();
        _gameTime = MaxTime;
    }

    private void SquadAutoMode(bool isAuto)
    {
        Squad.SetAutoMode(isAuto);
    }

    public virtual void OpenStartDialogue()
    {
        StartCountDown();
    }

    protected virtual void OpenEndDialogue()
    {
        OnDungeonClear();
    }

    protected void StartCountDown()
    {
        StartCoroutine(CountDown());
    }

    /// <summary>3초 카운트다운 후 전투 시작</summary>
    private IEnumerator CountDown()
    {
        SetCountDownVisible(true);
        for (int i = CountTime; i > 0; i--)
        {
            yield return new WaitForSeconds(1);
            _countTime--;
            if (_countDownText != null)
                _countDownText.text = _countTime.ToString();
        }

        SetCountDownVisible(false);
        StartGame();
    }

    private void SetCountDownVisible(bool visible)
    {
        if (_countDownText != null)
            _countDownText.gameObject.SetActive(visible);
    }

    protected virtual void StartGame()
    {
        FollowCamera.smoothSpeed = 0.1f;
        _isPlaying = true;
        Squad.BattleStart();
        MapManager.CheckMapData(_zoneIndex);
    }

    public void StopGame()
    {
        _isPlaying = false;
        Squad.BattleStop();
    }

    protected virtual async UniTaskVoid GameWin()
    {
        if (!_isPlaying)
            return;

        StopGame();
        await UniTask.WaitForSeconds(1);
        OpenEndDialogue();
    }

    /// <summary>
    /// 클리어 처리. 안개 / 필드 던전은 클리어 조건이 걸린 건물을 활성화하고,
    /// 그 외 던전은 점수 / 페이즈와 함께 서버에 클리어를 보낸다.
    /// </summary>
    protected void OnDungeonClear()
    {
        if (_dungeonRule.DungeonType == EDungeonType.Fog || _dungeonRule.DungeonType == EDungeonType.Field)
        {
            BuildingInfo buildingInfo = ClientLocalDB_Simple.GetDB<BuildingInfo>(DBKey.BuildingInfo).Values.FirstOrDefault(building =>
                building.BuildingConditionType == EBuildingConditionType.DungeonClear &&
                building.CurrencyList[0] == _battleData._index);

            if (buildingInfo == null)
            {
                MyLogger.LogWarning($"클리어 건물 정보 없음: dungeon {_battleData._index}");
                return;
            }

            BestHttpGameManager.ActiveBuilding(buildingInfo.ID);
        }
        else
        {
            BestHttpGameManager.OnPostDungeonClear(_battleData._contentType, _battleData._factionType, _battleData._index, _totalPoint, _phase, 1);
        }
    }

    public async UniTaskVoid GameOver()
    {
        if (!_isPlaying)
            return;

        StopGame();
        await UniTask.WaitForSeconds(1);
        _dungeonInfoUI.OpenBattleLosePopup(_battleData._contentType, _battleData._factionType, _battleData._index);
    }

    /// <summary>제한 시간이 있는 던전이면 남은 시간을 줄이고, 다 되면 true</summary>
    protected bool TimeOverCheck()
    {
        if (!IsTimeOverRule)
            return false;

        _gameTime -= Time.deltaTime;
        _dungeonInfoUI.UpdateTime((int)_gameTime);

        if (_gameTime < 0)
        {
            _gameTime = 0;
            _dungeonInfoUI.UpdateTime(0);
            UIManager.AllClosePopupUI();
            return true;
        }

        return false;
    }

    /// <summary>남은 적 수를 UI에 표시하고, 모두 처치했으면 true</summary>
    protected bool CheckEnemyAllDeath()
    {
        int unitCount = MapManager.GetLiveUnitCount();
        _dungeonInfoUI.UpdateCount(unitCount);

        return unitCount == 0;
    }

    #region 결과 화면 버튼
    public virtual void GoField()
    {
        if (_delay)
            return;

        _delay = true;
        Loading.Load(Loading.Field);
    }

    public virtual void NextStage()
    {
        if (_delay)
            return;

        _delay = true;
        int level = _battleData._index + 1;
        BestHttpGameManager.OnPostDungeonCurrencyCheck(_battleData._contentType, _battleData._factionType, level);
    }

    /// <summary>재도전. 안개 / 필드 던전은 서버 입장 확인 없이 바로 다시 로드한다.</summary>
    public virtual void ReTryStage()
    {
        if (_delay)
            return;

        _delay = true;
        if (IsFieldDungeon)
        {
            BattleData.Set(_battleData);
            Loading.Load(Loading.Dungeon);
        }
        else
        {
            BestHttpGameManager.OnPostDungeonCurrencyCheck(_battleData._contentType, _battleData._factionType, _battleData._index);
        }
    }
    #endregion

    public void GameWinClicked()
    {
        GameWin().Forget();
    }

    public void GameOverClicked()
    {
        GameOver().Forget();
    }
}
