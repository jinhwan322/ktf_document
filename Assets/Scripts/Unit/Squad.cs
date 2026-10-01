using Cysharp.Threading.Tasks;
using DarkTonic.MasterAudio;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UniRx;
using UnityEngine;
using static Define;

/// <summary>
/// 플레이어 스쿼드(파티). 덱의 캐릭터들이 메인 캐릭터를 중심으로 대열을 이뤄 함께 이동하고, 적을 만나면 각자 자동으로 싸운다.
/// - 조이스틱: 누르는 순간 스쿼드 중심에 가장 가까운 캐릭터를 메인 캐릭터로 정하고, 메인 캐릭터가 이동 방향을 따라간다.
/// - 손을 떼면 메인 캐릭터 주변 대열 슬롯을 다시 계산해 나머지 캐릭터가 자리로 돌아간다.
/// - 자동 모드: 가장 가까운 적 스폰 포인트를 찾아 이동한다.
/// - 존(구역) 진입 체크로 마을 / 필드 전환, 대화 이벤트, 날씨 연출을 처리한다.
/// 캐릭터들의 전투 업데이트(BattleUpdate)는 FixedUpdate 고정 틱으로 호출한다.
/// </summary>
public class Squad : MonoBehaviour
{
    [Header("GIZMO OPTION")]
    public bool BasicDistanceRange = false;
    public bool LimitDistanceRange = false;
    public bool ResurrectionLimitDistanceRange = false;

    // 대열 거리 기준 (캐릭터 AI에서 사용). DB 값은 cm 단위라 SetDistanceValue에서 m로 바꿔 저장
    public float BasicDistance = 3.0f;
    public float LimitDistance = 8.0f;
    public float ResurrectionLimitDistance = 12.0f;

    public Vector2 moveDir = Vector2.zero;  // 조이스틱 입력 방향
    public Transform Pivot;                 // 입력 방향으로 회전하는 기준점
    public Transform Dest;                  // Pivot 앞쪽의 이동 목표 지점

    public CircleCollider2D _circleCollider;
    public Rigidbody2D circleRigidbody;

    public Transform pointTrans;    // 이동 방향으로 쏜 레이가 벽(Lock 레이어)에 닿은 지점

    // 대열 슬롯 오프셋 (메인 캐릭터 기준). 0번이 중심, 이후 상하좌우 → 대각선 → 두 칸 바깥 순서
    private static readonly Vector2[] SlotOffsets =
    {
        Vector2.zero,
        Vector2.up, Vector2.down, Vector2.left, Vector2.right,
        Vector2.left + Vector2.up, Vector2.right + Vector2.down, Vector2.right + Vector2.up, Vector2.left + Vector2.down,
        Vector2.up * 2, Vector2.down * 2, Vector2.right * 2,
    };
    private const float SlotSpacing = 1.5f;     // 대열 슬롯 간격
    public List<PlayerUnit> _playerUnits = new List<PlayerUnit>();
    public List<SquadCharacterPosition> _offsets = new List<SquadCharacterPosition>();
    private MapManager MapManager => Managers.Instance.GetMapManager();
    private FollowCamera GameCamera => Managers.Instance.GetCameraManager().FollowCam;
    private UIJoystick JoystickUI => Managers.Instance.GetUIManager().JoystickUI;

    public PlayerUnit mainCharacter;
    private bool _isPlaying;

    public ReactiveProperty<bool> IsTownZone;
    public bool IsTownPortalZone => MapManager.IsTownPortalZone(_zoneIndex);
    private int currentDialogueIndex = 0;   // 이미 본 대화(스토리) 진행도
    
    public int _zoneIndex = -1;
    private Action _gameOverAction;
    private bool _autoMode;
    public bool _enableResurrection;
    
    DeckData _deckData;
    UserInfoData _userInfo;
    List<FactionSynergy> _activeSynergies = new List<FactionSynergy>();     // 덱 구성으로 발동한 종족 시너지
    
    private void OnDestroy()
    {
        if (Managers.Instance == null)
            return;
        
        Managers.Instance.GetJoystick().OnJoystickTypeChange -= OnHandleJoystickTypeChange;
        Managers.Instance.GetJoystick().OnMoveDirChange -= OnHandleMoveDirChange;
    }

    public void Init(UserInfoData userInfo)
    {
        _userInfo = userInfo;
        for (int i = 0; i < _offsets.Count; i++)
            _offsets[i].SetLocalPosition(SlotOffsets[i]);

        _zoneIndex = -1;
        Dest.gameObject.SetActive(false);
        IsTownZone.Value = false;
        currentDialogueIndex = _userInfo.dialogKey.Value;

        Managers.Instance.GetJoystick().OnJoystickTypeChange += OnHandleJoystickTypeChange;
        Managers.Instance.GetJoystick().OnMoveDirChange += OnHandleMoveDirChange;
    }

    /// <summary>덱을 정하고 덱 구성으로 발동하는 종족 시너지를 계산한다.</summary>
    public void SetDeckData(DeckData deckData)
    {
        _deckData = deckData;
        _activeSynergies = ClientLocalDB_Simple.CalculateFactionSynergy(_deckData.idList);
    }

    public void SetAutoMode(bool autoMode)
    {
        _autoMode = autoMode;
        _playerUnits.ForEach(unit => unit.SetAutoMode(autoMode));
    }

    /// <summary>대열 거리 설정 (cm 단위 → m)</summary>
    public void SetDistanceValue(float basicDistance, float limitDistance, float resurrectionLimitDistance = 1200)
    {
        BasicDistance = basicDistance * 0.01f;
        LimitDistance = limitDistance * 0.01f;
        ResurrectionLimitDistance = resurrectionLimitDistance * 0.01f;
    }
    
    // 캐릭터가 전부 죽었을때 Action
    public void SetGameOverAction(Action action)
    {
        _gameOverAction = action;
    }

    public void GameOverCheck()
    {
        if (UnitAllDeathCheck())
            _gameOverAction?.Invoke();
    }

    private void Update()
    {
        if (!_isPlaying)
            return;

        JoyStickUpdate();
    }

    /// <summary>캐릭터 전투(이동 / 타겟팅 / 스킬)는 고정 틱으로 진행</summary>
    private void FixedUpdate()
    {
        if (!_isPlaying)
            return;

        _playerUnits.ForEach(unit => unit.BattleUpdate());
    }
 
    public void BattleStop()
    {
        _circleCollider.enabled = false;
        _isPlaying = false;
        _playerUnits.ForEach(unit => unit.BattleStop());
        JoystickUI.Close();
    }

    public void BattleStart()
    {
        _circleCollider.enabled = true;
        _isPlaying = true;
        _playerUnits.ForEach(unit => unit.BattleStart());
        if(MapManager._enableJoyStick)
            JoystickUI.Open();
        
        RefreshTownZone();
    }

    public void SetResurrectionState(bool state)
    {
        _enableResurrection = state;
    }

    public void PlayerResurrection()
    {
        CharacterResurrection();
        BattleStart();
    }

    /// <summary>
    /// 조이스틱 입력(또는 자동 모드) 중에 스쿼드 중심을 메인 캐릭터 위치로 옮기고,
    /// 대화 이벤트 지점과 존 경계를 확인한다.
    /// </summary>
    private void JoyStickUpdate()
    {
        if (moveDir == Vector2.zero && !_autoMode)
            return;

        if (MapManager == null || mainCharacter == null)
            return;

        SetRayCastPoint();
        transform.position = mainCharacter.CenterPosition;

        StartDialogue(MapManager.EnableDialogue(_zoneIndex, circleRigidbody.position));
        EntranceTileCheck();
    }

    /// <summary>필드에서 다른 존에 들어서면 날씨 연출, 마을 진입 / 이탈, 맵 데이터 갱신을 처리한다.</summary>

    private void EntranceTileCheck()
    {
        if (MapManager._contentType != EContent.Field)
            return;
        
        int zoneIndex = MapManager.EnableEntrance(circleRigidbody.position);
        if (zoneIndex == -1 || _zoneIndex == zoneIndex)
            return;
        
        _zoneIndex = zoneIndex;
        CheckShowMapToastUI(_zoneIndex);
        RefreshTownZone();
        MapManager.CheckMapData(_zoneIndex);
    }

    /// <summary>
    /// 아직 보지 않은 대화 지점에 들어서면 전투를 멈추고 스토리 대화를 재생한다.
    /// 대화가 끝나면 전투를 재개하고 서버에 진행도를 저장한다.
    /// </summary>
    public void StartDialogue(int id)
    {
        if (id < 0)
            return;

        if (currentDialogueIndex >= id)
            return;
        
        List<DialogueData> data = Managers.Instance.GetSimpleDBManager().GetDialogueDataList("Chapter_1", id);
        if (data == null || data.Count == 0)
            return;
        
        BattleStop();
        currentDialogueIndex = id;
        Managers.Instance.GetUIManager().MainInfoUI.Close();
        Managers.Instance.GetUIManager().DialogueUI.StartDialogue(data, DialogueEnd);
    }
    
    public void StartDialogue(string dbKey, int id)
    {
        List<DialogueData> data = Managers.Instance.GetSimpleDBManager().GetDialogueDataList(dbKey, id);
        if (data == null)
            return;
        
        Managers.Instance.GetUIManager().DialogueUI.StartDialogue(data);
    }

    private void DialogueEnd(int key)
    {
        Managers.Instance.GetUIManager().MainInfoUI.Open();
        BattleStart();
        Managers.Instance.GetServerManager().OnPostDialogue(key);
    }

    /// <summary>존에 들어설 때 해당 존의 날씨 이펙트를 만든다.</summary>
    public void CheckShowMapToastUI(int value)
    {
        MapManager.CreateWeatherEffect(value).Forget();
    }

    /// <summary>현재 존이 마을이면 마을 모드로, 마을에서 벗어났으면 필드 모드로 전환한다.</summary>
    private void RefreshTownZone()
    {
        if (MapManager.IsTownZone(_zoneIndex))
            InTownZone();
        else if (IsTownZone.Value)
            OutTownZone();
    }

    /// <summary>조이스틱 방향이 바뀌면 Pivot을 그 방향으로 돌려 이동 목표 지점(Dest)을 옮긴다.</summary>
    private void OnHandleMoveDirChange(Vector2 _dir)
    {
        moveDir = _dir;

        if (_dir != Vector2.zero)
        {
            float angle = Mathf.Atan2(-_dir.x, +_dir.y) * 180 / Mathf.PI;
            Pivot.eulerAngles = new Vector3(0, 0, angle);
        }
    }

    /// <summary>
    /// 조이스틱을 누르면 메인 캐릭터를 다시 정하고(마을 제외), 떼면 메인 캐릭터 기준으로 대열 슬롯을 다시 배치한다.
    /// </summary>
    void OnHandleJoystickTypeChange(Define.EJoystickState _state)
    {
        switch (_state)
        {
            case Define.EJoystickState.PointDown:
                if(!IsTownZone.Value)
                    MainCharacterCheck();
                Dest.gameObject.SetActive(true);
                break;
            case Define.EJoystickState.Dragging:
                break;
            case Define.EJoystickState.PointUp:
                circleRigidbody.velocity = Vector3.zero;
                Dest.gameObject.SetActive(false);
                ResetSquadCharacterPosition();
                SetSquadCharacterPosition();
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// 덱에 맞춰 캐릭터를 갱신한다. 덱에서 빠진 캐릭터는 제거하고, 남은 캐릭터는 시너지만 다시 적용하고,
    /// 새로 들어온 캐릭터는 스폰한 뒤 스킬 이펙트 풀을 만든다.
    /// </summary>
    public async UniTask RefreshSquadCharacter()
    {
        for (int i = _playerUnits.Count - 1; i >= 0; i--)
        {
            PlayerUnit unit = _playerUnits[i];
            if (_deckData.idList.Contains(unit._unitId))
                continue;

            RemoveCharacter(unit);
            Managers.Instance.GetObjectUnitManager().DestroyUnit(unit);
        }

        for (int i = 0; i < _deckData.idList.Length; i++)
        {
            int id = _deckData.idList[i];
            if (id <= 0)
                continue;

            PlayerUnit unit = _playerUnits.Find(unit => unit._unitId == id);
            if (unit != null)
            {
                unit.SetSynergyStatus(GetActiveSynergyStatus(unit._unitDataTable.Faction));
                continue;
            }
            
            await Managers.Instance.GetObjectUnitManager().SpawnCharacter(_userInfo, this, id, ETeamType.Player, _enableResurrection);
            await Utils.AddCharacterSkillEffectPool(DBKey.PlayerCharacter, id);
        }

        if (mainCharacter == null)
            MainCharacterCheck();
    }

    public void AddCharacter(PlayerUnit unit)
    {
        if (!_playerUnits.Contains(unit))
        {
            _playerUnits.Add(unit);
        }
    }

    private void RemoveCharacter(PlayerUnit unit)
    {
        if(mainCharacter == unit)
            mainCharacter = null;
        
        _playerUnits.Remove(unit);
    }

    public void Remove()
    {
        for (int i = _playerUnits.Count - 1; i >= 0; i--)
        {
            PlayerUnit unit = _playerUnits[i];
            RemoveCharacter(unit);
        }
    }


    public bool InBattleState()
    {
        for(int i = _playerUnits.Count - 1; i >= 0; i--)
        {
            BaseUnit baseUnit = _playerUnits[i];
            if (baseUnit.InBattleState())
                return true;
        }
        return false;
    }

    public bool UnitAllDeathCheck()
    {
        for (int i = 0; i < _playerUnits.Count; i++)
        {
            PlayerUnit unit = _playerUnits[i];
            if (!unit.IsDie)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 스쿼드 중심에 가장 가까운 살아 있는 캐릭터를 메인 캐릭터(조이스틱으로 조종하는 캐릭터)로 정한다.
    /// 메인 캐릭터는 AI 이동을 멈추고 입력을 따라가며, 나머지는 메인 캐릭터를 따라 대열을 유지한다.
    /// </summary>
    public void MainCharacterCheck()
    {
        PlayerUnit closest = null;
        float closestDistance = float.MaxValue;
        foreach (var unit in _playerUnits)
        {
            unit.isMainCharacter = false;
            unit.SetMass(false);
            if (!unit.IsValid())
                continue;

            float distance = ((Vector2)transform.position - unit.CenterPosition).magnitude;
            if (distance < closestDistance)
            {
                closest = unit;
                closestDistance = distance;
            }
        }

        if (closest == null)
            return;

        mainCharacter = closest;
        mainCharacter.isMainCharacter = true;
        mainCharacter.SetMass(false);
        mainCharacter.StopAiMode();
        transform.position = mainCharacter.CenterPosition;
    }

    /// <summary>메인 캐릭터에서 이동 목표 방향으로 레이를 쏴 벽(Lock 레이어)에 닿는 지점을 기록한다.</summary>
    private void SetRayCastPoint()
    {
        if (mainCharacter == null)
            return;

        Vector2 gap = (Vector2)Dest.transform.position - mainCharacter.CenterPosition;
        RaycastHit2D hit = Physics2D.Raycast(transform.position, gap.normalized, 300f, LayerMask.GetMask("Lock"));
        pointTrans.position = hit.point;
    }

    public void ResetSquadCharacterPosition()
    {
        foreach (var squadCharacterPosition in _offsets)
        {
            squadCharacterPosition.SetLocalPosition(Vector2.zero);
        }
    }
    
    /// <summary>현재 스쿼드 위치·방향 기준으로 대열 슬롯 위치를 다시 계산한다. (캐릭터는 슬롯으로 이동)</summary>
    public void SetSquadCharacterPosition()
    {
        for (int i = 0; i < _offsets.Count; i++)
        {
            SquadCharacterPosition squadCharacterPosition = _offsets[i];
            squadCharacterPosition.SetPosition(transform.TransformPoint(SlotOffsets[i] * SlotSpacing));
        }
    }
    
    /// <summary>대열 슬롯을 즉시 배치한다. (순간 이동용)</summary>
    public void SetSquadCharacterForcePosition()
    {
        for (int i = 0; i < _offsets.Count; i++)
        {
            SquadCharacterPosition squadCharacterPosition = _offsets[i];
            squadCharacterPosition.SetForcePosition(transform.TransformPoint(SlotOffsets[i] * SlotSpacing));
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if(mainCharacter != null)
        {
            Vector2 gap = (Vector2)Dest.transform.position - mainCharacter.CenterPosition;
            Debug.DrawRay(mainCharacter.CenterPosition, gap.normalized * 300f, Color.red);
        }

        if (BasicDistanceRange)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(circleRigidbody.position, BasicDistance);
        }

        if (LimitDistanceRange)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(circleRigidbody.position, LimitDistance);
        }

        if (ResurrectionLimitDistanceRange)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(circleRigidbody.position, ResurrectionLimitDistance);
        }
    }
#endif

    public Vector2 GetRayCastPoint()
    {
        return pointTrans.position;
    }

    public int ReturnPlayerCount()
    {
        return _playerUnits.Count;
    }
    
    /// <summary>
    /// 스쿼드를 다른 존의 위치로 옮긴다. 대열 슬롯을 먼저 옮기고, 맵 정보가 갱신될 시간을 둔 뒤(30프레임) 캐릭터를 슬롯에 배치한다.
    /// </summary>
    public async UniTaskVoid SquadMove(int zoneIndex, Vector3 position)
    {
        _zoneIndex = zoneIndex;
        transform.position = position;
        
        SetSquadCharacterPosition();
        CheckShowMapToastUI(_zoneIndex);
        if (_playerUnits.Count == 0)
            return;
        
        await UniTask.DelayFrame(30);
        ForceCharacterMove();
    }
    
    /// <summary>스쿼드와 캐릭터를 즉시 다른 존의 위치로 옮긴다. (던전 시작 위치 등)</summary>
    public void ForceSquadMove(int zoneIndex, Vector3 position)
    {
        _zoneIndex = zoneIndex;
        transform.position = position;
        
        CheckShowMapToastUI(_zoneIndex);
        SetSquadCharacterForcePosition();
        ForceCharacterMove();
    }

    private void ForceCharacterMove()
    {
        for(int i = 0; i < _playerUnits.Count; i++)
        {
            PlayerUnit playerUnit = _playerUnits[i];
            playerUnit.transform.position = GetUnitPosition(i);
            playerUnit.SetZoneIndex(_zoneIndex);
            playerUnit.SetPosition(playerUnit.transform.position);
        }
    }

    public void CharacterInit()
    {
        _playerUnits.ForEach(playerUnit => playerUnit.Init());
    }
    
    public void CharacterResurrection()
    {
        _playerUnits.ForEach(playerUnit => playerUnit.Resurrection());
    }

    public BaseUnit GetSquadLeader()
    {
        return mainCharacter;
    }
    
    /// <summary>다음에 들어올 캐릭터의 대열 슬롯 위치</summary>
    public Vector2 GetUnitPosition()
    {
        return _offsets[_playerUnits.Count].GetWorldPosition();
    }

    public Vector2 GetUnitPosition(int index)
    {
        return _offsets[index].GetWorldPosition();
    }

    private void InTownZone()
    {
        IsTownZone.Value = true;
        foreach (var unit in _playerUnits)
        {
            unit.TownIn(_zoneIndex);
        }
    }

    private void OutTownZone()
    {
        IsTownZone.Value = false;
        foreach (var unit in _playerUnits)
        {
            unit.TownOut(_zoneIndex);
        }
    }

    /// <summary>
    /// 워프 연출: 전투를 멈추고 카메라를 줌아웃한 뒤 캐릭터가 차례로 사라지고(Portal_Start),
    /// 목적지로 옮긴 뒤 차례로 나타난다(Portal_End). 끝나면 캐릭터를 초기화하고 전투를 재개한다.
    /// </summary>
    public IEnumerator TeleportHeroesCoroutine(int zoneIndex, Vector3 position)
    {
        BattleStop();
        GameCamera.SetZoomOut();

        for (int i = 0; i < _playerUnits.Count; i++)
        {
            BaseUnit unit = _playerUnits[i];
            unit.SetAnimation(Define.EUnitState.Portal_Start);
            yield return new WaitForSeconds(0.2f);
        }

        yield return new WaitForSeconds(1.5f);
        SquadMove(zoneIndex, position).Forget();
        MapManager.CheckMapData(_zoneIndex);
        Managers.Instance.GetUIManager()._mainInfoUI.SetPortalDelay();
        yield return new WaitForSeconds(1f);

        for (int i = 0; i < _playerUnits.Count; i++)
        {
            BaseUnit unit = _playerUnits[i];
            unit.SetAnimation(Define.EUnitState.Portal_End);
            yield return new WaitForSeconds(0.2f);
        }

        yield return new WaitForSeconds(0.5f);
        GameCamera.ResetZoom();
        yield return new WaitForSeconds(1f);
        CharacterInit();
        MainCharacterCheck();
        BattleStart();
    }

    public void TeleportHeroes(int mapIndex, Vector3 position)
    {
        StartCoroutine(TeleportHeroesCoroutine(mapIndex, position));
    }

    /// <summary>한 캐릭터가 액티브 스킬을 쓰면 다른 캐릭터들의 "아군 스킬 사용" 패시브를 발동한다.</summary>
    public void ActivePassiveSkill(BaseUnit baseUnit)
    {
        foreach (var playerUnit in _playerUnits)
        {
            if (playerUnit == baseUnit)
                continue;

            playerUnit._passiveSkillObserver.Trigger(EPassiveSkillTriggerType.AllyUseActiveSkill, playerUnit);
        }
    }

    public bool IsBattleCheck()
    {
        return _playerUnits.Exists(player => player.IsBattle);
    }

    /// <summary>해당 종족에 적용되는 시너지 스탯(공격력 / 방어력 / 최대 HP %) 합계</summary>
    public Status GetActiveSynergyStatus(EFactionType factionType)
    {
        Status synergyStatus = new Status();
        foreach (var synergy in _activeSynergies)
        {
            if (synergy.Faction == factionType)
            {
                if (synergy.AttackPercent > 0)
                    synergyStatus.Plus(EStatus.AttackPercent, synergy.AttackPercent);
                if (synergy.DefPercent > 0)
                    synergyStatus.Plus(EStatus.DefPercent, synergy.DefPercent);
                if (synergy.MaxHealthPointPercent > 0)
                    synergyStatus.Plus(EStatus.MaxHealthPointPercent, synergy.MaxHealthPointPercent);
            }
        }
        
        return synergyStatus;
    }

    #region AutoMode
    /// <summary>자동 모드 이동 목표: 아직 적이 남아 있는 스폰 포인트 중 가장 가까운 곳</summary>
    public SpawnPointInfoUnit FindSpawnPoint()
    {
        List<SpawnPointInfoUnit> spawnPointInfoUnits = MapManager.GetSpawnPoint();
        
        SpawnPointInfoUnit spawnPointInfoUnit = null;
        float bestSqrDist = float.MaxValue;
        foreach (var spawnPoint in spawnPointInfoUnits)
        {
            if (spawnPoint.AllDieUnit())
                continue;
            
            float sqrDist = ((Vector2)circleRigidbody.position - (Vector2)spawnPoint.transform.position).sqrMagnitude;

            if (sqrDist < bestSqrDist)
            {
                bestSqrDist = sqrDist;
                spawnPointInfoUnit = spawnPoint;
            }
        }
        
        return spawnPointInfoUnit;
    }
    #endregion
}
