using Cysharp.Threading.Tasks;
using MarchingBytes;
using PolyNav;
using Spine;
using Spine.Unity;
using System;
using System.Collections.Generic;
using UnityEngine;
using static Define;
using Animation = Spine.Animation;

/// <summary>스킬 타겟 한 건: 대상 유닛, 스킬이 떨어질 위치, 범위 표시(Spell Indicator)</summary>
public class SkillTargetData
{
    public BaseUnit _skillTarget;
    public Vector2 _targetPosition;
    public SpellIndicator _spellIndicator;
}

/// <summary>
/// 필드 전투 유닛(아군 캐릭터 / 몬스터 / 채집물) 베이스. 우르르 용병단처럼 필드를 돌아다니며 실시간으로 싸우는 방식이다.
/// - 탐색: 이동 탐색 범위 안에서 가장 가까운 적(없으면 채집물)을 이동 타겟으로 정한다.
/// - 이동: PolyNav(2D 내비메시)로 타겟까지 경로 이동. 경로 재계산은 일정 프레임 주기로만 한다.
/// - 공격: 사용 가능한 스킬의 사거리 안에 타겟이 들어오면 멈추고 스킬 상태로 전환한다.
///   실제 판정은 StateMachine이 Spine "callEffect" 이벤트 시점에 TargetHit()을 호출해 처리한다.
/// - 지속 효과(버프 / 디버프 / 상태이상), 장판 효과, 상태이상 면역, 넉백, 부활을 관리한다.
///
/// 상속: PlayerUnit(아군 캐릭터), EnemyUnit(일반 몬스터), BossUnit(보스),
///       RankingBossUnit(랭킹 보스), GuildBossUnit(길드 보스), GrTree(채집물)
/// </summary>
public class BaseUnit : FieldObject
{
    [Header("GIZMO OPTION")] public bool MoveTargetSearchRange = false;
    public bool MoveTargetLine = false;
    public bool SkillTargetSearchRange = false;
    public bool SkillTargetLine = false;
    public bool SkillTargetAreaRange = false;

    public UserInfoData UserInfoData;

    public UIHpBar _hpUI = null;
    protected HurtFlashEffect _flashEffect;

    public BaseUnit _moveTarget;                                                // 따라가는 타겟
    public List<SkillTargetData> _skillTarget = new List<SkillTargetData>();    // 현재 스킬 사거리 안의 타겟
    public StateMachine<BaseUnit> _state = null;
    public Dictionary<int, List<DurationEffectBase>> _durationEffectDic = new();    // 지속 효과 (id별 중첩 목록)
    public Dictionary<int, GroundBuff> _groundEffectDic = new();                    // 밟고 있는 장판 효과
    public SkillObserver _skillObserver = new SkillObserver();                      // 스킬 쿨타임 / 사용 가능한 스킬 선택
    public PassiveSkillObserver _passiveSkillObserver = new PassiveSkillObserver(); // 조건 발동 패시브
    public BaseSkill _activeSkill;                                              // 이번에 사용할 스킬

    [Header("resurrect")] protected Vector2 _startPos;
    protected float resurrectionTime;
    protected float maxResurrectionTime;

    public ETeamType _teamType = ETeamType.None;
    public EUnitType UnitType = EUnitType.None;
    protected LayerMask _findMask; //검색해야하는 Layer

    #region Status

    public int _unitId;
    public UnitData _unitDataTable;
    protected StatusInfo _statusInfo = null;
    public bool IsStatusInfoReady => _statusInfo != null;
    public PlayStatus _playStatus => _statusInfo._playStatus;
    public UnitCombatStats _unitCombatStats; // 전투 정보 수집
    protected float _moveSpeed = 5;

    /// <summary>광고 버프 이동 속도 보너스 (아군만, 광고 버프가 켜져 있을 때)</summary>
    protected float _adMoveSpeed
    {
        get
        {
            if (_teamType == ETeamType.Enemy || !Managers.Instance.UserInfo().EnableAdBuff)
                return 0;

            return (float)int.Parse(ClientLocalDB_Simple.GetData<FieldConfig>(DBKey.FieldConfig, "MoveSpeed_Ad").Value) / 100;
        }
    }
    protected float _accSpeed = 1; // PolyNavi 가속도
    protected float _unitRadius = 0.5f;

    #endregion

    #region 한정적으로 사용될 변수들

    public bool IsInvincible = false; // 무적 - 랭킹 보스일때 사용
    public bool IsImmortal  = false; // 무적 - 체력이 1인 상태에서 죽지 않음
    public bool IsStatusImmune = false; // 상태이상 무시 (보스 들)
    public bool IsDamageImmune; // 특정 데미지만 무시

    #endregion

    #region KnockBack

    public bool IsKnockBack = false;
    public Vector2 knockbackForce = Vector2.zero;

    #endregion

    [SerializeField] protected ECharacterPositionType _characterPositionType;
    public bool IsDie => _state.GetCurState() == State_Die.Instance;

    /// <summary>전투 중인지: 적(채집물 제외)을 쫓으며 사거리 안에 타겟이 있거나, 조이스틱을 조작 중</summary>
    public bool IsBattle => (_moveTarget != null && !(_moveTarget as BaseGatherResUnit) && _skillTarget.Count > 0) || Managers.Instance.GetJoystick().JoystickState != EJoystickState.PointUp;
    protected bool IsMoving => _state.GetCurState() == State_Move.Instance;
    /// <summary>이동할 수 없는 상태: 사망 / 속박 / 스턴 / 빙결</summary>
    protected bool IsMoveBlocked => IsDie || GetStatusEffect(EStatusEffectType.Rooted) || GetStatusEffect(EStatusEffectType.Stun) || GetStatusEffect(EStatusEffectType.Frozen);

    #region StatusEffect
    private int StatusCount => Enum.GetValues(typeof(EStatusEffectType)).Length;
    public bool[] _statusEffects;           // 걸려 있는 상태이상
    public bool[] _statusImmuneEffects;     // 버프로 얻은 상태이상 면역
    public bool[] _passiveImmuneEffects;    // 패시브로 얻은 상태이상 면역
    #endregion
    
    protected MapManager MapManager => Managers.Instance.GetMapManager();
    protected int _zoneIndex;
    public bool LerpCellPosCompleted;

    protected Vector2 _movePos;

    [Header("PolyNav")] public PolyNavAgent _agent;

    protected DropItemData _dropItemData = new DropItemData();
    Bone _firePointBone = null;

    public override void Awake()
    {
        base.Awake();
        _characterPositionType = ECharacterPositionType.BasicIn;
        _flashEffect = Utils.GetOrAddComponent<HurtFlashEffect>(gameObject);
        _collider.offset = new Vector2(0, 0.5f);
        
        _statusEffects = new bool[StatusCount];
        _statusImmuneEffects = new bool[StatusCount];
        _passiveImmuneEffects = new bool[StatusCount];
    }

    public virtual async UniTask SetUnitId(UserInfoData userInfoData, int id)
    {
        UserInfoData = userInfoData;
        _unitId = id;

        _unitCombatStats = new UnitCombatStats(id);
        await SetSpine();
    }

    protected virtual async UniTask SetSpine()
    {
        string path = $"{GetSpineResourceName()}/{GetSpineResourceName()}_SkeletonData.asset";
        SkeletonDataAsset skeletonData = await Managers.Instance.GetResObjectManager().LoadAsync<SkeletonDataAsset>(path);
        if (skeletonData == null)
        {
            MyLogger.Log("Not Find Spine : " + path);
            return;
        }

        _spineAnimation.SetSpine(skeletonData);
        _spineAnimation.SetSortingOrder(SortingLayers.UNIT);
        
        _firePointBone = _spineAnimation.FindBone("FirePoint");
    }

    /// <summary>FSM, 이동 속도, PolyNav 에이전트, HP 바, 물리를 초기화한다. (스폰 / 부활 시)</summary>
    public virtual void Init()
    {
        if (_state == null)
            _state = new StateMachine<BaseUnit>();
        _state.Init(this, State_Idle.Instance);
        _unitCombatStats?.ResetStats();
        
        _moveSpeed = (float)_playStatus.MoveSpeed / 100;
        
        _agent = gameObject.GetOrAddComponent<PolyNavAgent>();
        _agent.maxForce = 30;
        _agent.repath = false; // 전투 중 매 프레임 내부 A* 재탐색 비활성화 — MoveToTargetNav의 프레임 주기로 직접 관리 (AI 모드 시 PlayerUnit에서 별도 활성화)
        _unitRadius = _statusInfo._statusData.Radius * 0.01f;

        // 오브젝트를 재사용할 때 예전 HP 바가 남지 않도록 새로 만든다
        if (_hpUI != null)
        {
            Managers.Instance.GetResObjectManager().Destroy(_hpUI.gameObject);
            _hpUI = null;
        }
        
        if (!_unitDataTable.HpBar.IsNull())
        {
            GameObject goHpBar = Managers.Instance.GetResObjectManager()
                .Instantiate($"Prefabs/UI/Common/{_unitDataTable.HpBar}", gameObject.transform);
            float hpBarHeight = _unitDataTable.HpBarY * 0.01f;               
            goHpBar.transform.localPosition = new Vector3(0, hpBarHeight, 0);
            _hpUI = goHpBar.GetComponent<UIHpBar>();                
        }

        if (_hpUI != null)
        {
            _hpUI.Init();
            _hpUI.InitData(this);
            _hpUI.gameObject.SetActive(false);
        }

        _rigidbody.simulated = true;
        _collider.enabled = true;
        _collider.isTrigger = false;
        LerpCellPosCompleted = true;
    }

    public void SetPosition(Vector2 pos)
    {
        _unitPos = pos;
        LerpCellPosCompleted = true;
    }

    public void SetMovePosition(Vector2 pos)
    {
        _movePos = pos;
    }

    protected virtual void LerpToPosition()
    {
    }

    /// <summary>에이전트의 다음 경로 지점 쪽을 바라보게 한다. (이동은 에이전트가 처리하므로 물리 속도는 0)</summary>
    private void AgentDirection()
    {
        _rigidbody.velocity = Vector2.zero;
        Vector2 dir = _agent.nextPoint - _rigidbody.position;
        LookAtDir(dir);
    }

    private float _lastNavMaxSpeed = -1f;
    private const int NavDestinationUpdateInterval = 10;   // 목적지 재설정 주기 (프레임)
    private int _navFrameCounter = 0;

    /// <summary>
    /// PolyNav 에이전트로 목표 지점까지 이동한다.
    /// 유닛이 많을 때 매 프레임 경로를 다시 찾으면 부하가 커서, 목적지는 NavDestinationUpdateInterval 프레임마다만 갱신한다.
    /// </summary>
    /// <param name="targetVec">목표 위치</param>
    /// <param name="immediate">true면 주기와 관계없이 바로 목적지 갱신</param>
    protected void MoveToTargetNav(Vector3 targetVec, bool immediate = false)
    {
        float maxSpeed = (_moveSpeed + _adMoveSpeed) * _accSpeed;
        if (_lastNavMaxSpeed != maxSpeed)
        {
            _agent.maxSpeed = maxSpeed;
            _lastNavMaxSpeed = maxSpeed;
        }

        Vector2 dest = targetVec;

        if (immediate)
        {
            _agent.SetDestination(dest);
        }
        else
        {
            _navFrameCounter++;
            if (_navFrameCounter >= NavDestinationUpdateInterval)
            {
                _navFrameCounter = 0;
                _agent.SetDestination(dest);
            }
        }

        AgentDirection();
    }

    public void MoveStop()
    {
        if (_agent != null)
            _agent.Stop();

        if (IsKnockBack)
            return;

        _rigidbody.velocity = Vector2.zero;
    }

    #region Attack

    /// <summary>
    /// 범위 안의 타겟 후보 중 가장 가까운 유닛을 찾는다. 같은 NavMap(구역)에 있는 유닛만 대상이며,
    /// 전투 유닛과 채집물이 함께 있으면 전투 유닛을 우선한다.
    /// 거리는 직선 거리(sqrMagnitude)로 비교해 A* 경로 계산을 생략한다.
    /// </summary>
    protected BaseUnit GetSearchUnit(float range, LayerMask layerMask)
    {
        ContactFilter2D contactFilter = new ContactFilter2D();
        contactFilter.SetLayerMask(layerMask);
        contactFilter.useLayerMask = true;
        contactFilter.useTriggers = true;
        List<Collider2D> colliders = new List<Collider2D>();
        Physics2D.OverlapCircle(CenterPosition, range, contactFilter, colliders);

        float rangeSqr = range * range;
        BaseUnit closestUnit = null;
        BaseUnit closestGather = null;
        float unitSqrDist = float.MaxValue;
        float gatherSqrDist = float.MaxValue;

        foreach (Collider2D hit in colliders)
        {
            if (hit == null)
                continue;

            BaseUnit candidate = hit.GetComponent<BaseUnit>();
            if (candidate == null || !IsTargetOnSameNavMap(candidate))
                continue;

            float sqrDist = (candidate._rigidbody.position - _rigidbody.position).sqrMagnitude;
            if (sqrDist > rangeSqr)
                continue;

            if (candidate._teamType == ETeamType.Gather)
            {
                if (sqrDist < gatherSqrDist)
                {
                    gatherSqrDist = sqrDist;
                    closestGather = candidate;
                }
            }
            else if (sqrDist < unitSqrDist)
            {
                unitSqrDist = sqrDist;
                closestUnit = candidate;
            }
        }

        return closestUnit != null ? closestUnit : closestGather;
    }

    /// <summary>
    /// 이동 상태에서 매 틱 호출: 사용할 스킬을 고르고 사거리 안에 타겟이 있으면 멈춘다. 없으면 이동 타겟 쪽으로 계속 이동.
    /// </summary>
    /// <returns>사거리 안에 타겟이 있어서 멈췄으면 true</returns>
    protected bool MoveToTargetUnit()
    {
        _activeSkill = _skillObserver.GetEnableSkill();
        AttackRangeCheck();

        if (_skillTarget.Count > 0)
        {
            MoveStop();
            return true;
        }

        MoveToTargetNav(_moveTarget._rigidbody.position);
        return false;
    }

    /// <summary>
    /// 현재 스킬의 사거리 / 타겟 타입으로 스킬 타겟을 찾는다. 채집물은 1m 안에 들어오면 채집한다.
    /// 자신 / 아군 대상 스킬은 주변에서 바로 찾을 수 있지만, 이동 타겟(적)이 사거리 밖이면 쓰지 않고 이동을 우선한다.
    /// </summary>
    private void AttackRangeCheck()
    {
        if(_moveTarget == null) return;
            
        if (_moveTarget as BaseGatherResUnit)
        {
            _activeSkill = null;
            _skillTarget = SentenceManager.SkillRangeTargetSearch(_moveTarget, this, 1f);
        }
        else
        {
            SkillDetailData skillDetailData = _activeSkill.GetMainDetailData();
            List<SkillTargetData> targets = SentenceManager.SkillRangeTargetSearch(this, skillDetailData, skillDetailData.HitLimit);

            // Self/Ally 타입은 자기 주변에서 즉시 찾지만, MoveTarget이 사거리 밖이면 이동 우선
            if (skillDetailData.TargetType1 == ESkillTargetType.Self ||
                skillDetailData.TargetType1 == ESkillTargetType.Ally ||
                skillDetailData.TargetType1 == ESkillTargetType.AllyAll)
            {
                var moveTargetCheck = SentenceManager.SkillRangeTargetSearch(_moveTarget, this, skillDetailData.RangeSize * 0.01f);
                if (moveTargetCheck.Count == 0)
                    targets = new List<SkillTargetData>();
            }

            _skillTarget = targets;
        }
    }

    private int _findTargetFrameCounter = 0;
    private const int FindTargetUpdateInterval = 5;    // 타겟 재탐색 주기 (프레임)

    /// <summary>이동 타겟 탐색. 현재 타겟이 살아 있으면 FindTargetUpdateInterval 프레임마다만 다시 찾는다.</summary>
    protected virtual void FindTarget()
    {
        // 현재 타겟이 살아있으면 재탐색 주기까지 대기
        if (_moveTarget != null && !_moveTarget.IsDie)
        {
            _findTargetFrameCounter++;
            if (_findTargetFrameCounter < FindTargetUpdateInterval)
                return;
        }

        _findTargetFrameCounter = 0;
        _moveTarget = GetSearchUnit((float)_playStatus.MoveTargetSearchRange * 0.01f, _findMask);
    }

    protected virtual bool IsTargetOnSameNavMap(BaseUnit target) => true;

    #endregion

    public void ChangeState(FSM_State<BaseUnit> state)
    {
        if (_state.GetCurState() != State_Die.Instance)
            _state.ChangeState(state);
    }

    /// <summary>Squad가 FixedUpdate마다 호출: 스킬 쿨타임 → 패시브 → FSM → 위치 타입 → 지속 효과 순서로 진행</summary>
    public virtual void BattleUpdate()
    {
        if (!IsMoving)
            MoveStop();

        _skillObserver.SkillUpdate();
        _passiveSkillObserver.SkillUpdate();

        if (_state != null && isActiveAndEnabled)
            _state.Update();

        SetUnitPositionType();
        UpdateDurationEffect();
    }

    public void BattleStop()
    {
        if (_state.GetCurState() == State_Die.Instance)
            return;

        _collider.enabled = false;

        if (_state.GetCurState() != State_Idle.Instance)
        {
            _state.ChangeState(State_Idle.Instance);
            MoveStop();
        }
    }

    public virtual void BattleStart()
    {
        if (_state != null && _state.GetCurState() == State_Die.Instance)
            return;

        _collider.enabled = true;
    }

    protected virtual bool GetLoopByState(EUnitState state)
    {
        return state switch
        {
            EUnitState.Idle => true,
            EUnitState.Move => true,
            EUnitState.Die => true,
            _ => false
        };
    }

    public virtual void SetAnimation(EUnitState state)
    {
        bool loop = GetLoopByState(state);
        string animationName = GetAnimationName(state);

        Animation anim = _spineAnimation.FindAnimation(animationName);
        if (anim != null)
        {
            _spineAnimation.SetAnimation(animationName, loop);
            _state.SetAnimation(anim);
        }
    }

    protected virtual string GetAnimationName(EUnitState state)
    {
        string animationName = "";
        switch (state)
        {
            case EUnitState.Idle:
                animationName = UnitType == EUnitType.PlayerCharacter ? CharacterAnimationName.IDLE : ObjectAnimationName.IDLE;
                break;
            case EUnitState.Move:
                animationName = UnitType == EUnitType.PlayerCharacter ? CharacterAnimationName.MOVE : ObjectAnimationName.MOVE;
                break;
            case EUnitState.Attack:
                animationName = UnitType == EUnitType.PlayerCharacter ? CharacterAnimationName.ATTACK : ObjectAnimationName.ATTACK;
                break;
            case EUnitState.Skill:
                animationName = UnitType == EUnitType.PlayerCharacter ? CharacterAnimationName.ACTIVE_SKILL : $"{ObjectAnimationName.ACTIVE_SKILL}{_activeSkill._index + 1}";
                break;
            case EUnitState.SpecialSkill:
                animationName = UnitType == EUnitType.PlayerCharacter ? CharacterAnimationName.Special_SKILL : $"{ObjectAnimationName.Special_SKILL}{_activeSkill._index + 1}";
                break;
            case EUnitState.Die:
                animationName = UnitType == EUnitType.PlayerCharacter ? CharacterAnimationName.DIE : ObjectAnimationName.DIE;
                break;
            case EUnitState.Axe:
                animationName = CharacterAnimationName.AXE;
                break;
            case EUnitState.Mine:
                animationName = CharacterAnimationName.MINE;
                break;
            case EUnitState.Portal_Start:
                animationName = CharacterAnimationName.PORTAL_START;
                break;
            case EUnitState.Portal_End:
                animationName = CharacterAnimationName.PORTAL_END;
                break;
            case EUnitState.Hit:
                animationName = UnitType == EUnitType.PlayerCharacter ? "" : ObjectAnimationName.HIT;
                break;
        }

        return animationName;
    }

    /// <summary>
    /// Idle 상태: 타겟을 찾아 사거리 안이면 공격, 밖이면 이동 상태로 전환한다. (속박 중이면 제자리)
    /// 타겟이 없으면 대열의 원래 자리로 돌아간다.
    /// </summary>
    public virtual void UpdateIdle()
    {
        FindTarget();

        if (_moveTarget != null)
        {
            _activeSkill = _skillObserver.GetEnableSkill();
            AttackRangeCheck();
            if (_skillTarget.Count > 0)
                AttackAction();
            else
            {
                if (GetStatusEffect(EStatusEffectType.Rooted)) // 속박 상태면 못움직임
                    return;

                _state.ChangeState(State_Move.Instance);
            }
        }
        else
        {
            if (_characterPositionType != ECharacterPositionType.BasicIn && LerpCellPosCompleted)
                ReturnMove();
        }
    }

    public virtual void UpdateMove()
    {
        UpdatePolyNavMap();
    }

    /// <summary>스킬 시작 이펙트를 띄우고 스킬 슬롯(기본 공격 / 액티브 / 스페셜)에 맞는 상태로 전환한다.</summary>
    protected virtual void AttackAction()
    {
        PlayStartEffect().Forget();
        
        ESkillSlotType skillType = _activeSkill._skillData.SkillSlotType;
        if (skillType == ESkillSlotType.DefaultSkill)
            _state.ChangeState(State_Attack.Instance);
        else if (skillType == ESkillSlotType.ActiveSkill)
            _state.ChangeState(State_Skill.Instance);
        else if (skillType == ESkillSlotType.SpecialSkill)
            _state.ChangeState(State_SpecialSkill.Instance);
    }

    public void SetSkillDetailIndex(int index)
    {
        if (_activeSkill == null)
            return;

        _activeSkill.SetDetailIndex(index);
    }

    /// <summary>
    /// 범위 표시(Spell Indicator)를 켠 스킬이면, 다음 타격까지 남은 시간(time) 동안 타겟 위치에 공격 범위를 미리 보여 준다.
    /// </summary>
    public async UniTask ActiveSkillPreview(float time)
    {
        if(_activeSkill == null)
            return;
        
        SkillDetailData detailData = _activeSkill.GetCurrentDetailData();
        if (detailData.Indicator)
        {
            // Spine Flip때문에 한 프레임 넘긴다.
            await UniTask.DelayFrame(2);
            foreach (var targetData in _skillTarget)
            {
                 SpellIndicator spellIndicator = SentenceManager.ActivePreviewType(targetData._targetPosition, this, detailData, time);
                 targetData._spellIndicator = spellIndicator;
            }
        }
    }

    /// <summary>사망 상태: 부활 대기 시간이 지나면 부활한다.</summary>
    public virtual void UpdateDie()
    {
        if (resurrectionTime < maxResurrectionTime)
        {
            resurrectionTime += Time.fixedDeltaTime;
        }
        else
        {
            Resurrection();
        }
    }

    /// <summary>싸울 타겟이 없을 때 대열의 원래 자리로 돌아가는 이동을 시작한다.</summary>
    protected virtual void ReturnMove()
    {
        if (IsMoveBlocked)
            return;

        _rigidbody.velocity = Vector2.zero;
        _agent.Stop();
        _navFrameCounter = NavDestinationUpdateInterval;
        LerpCellPosCompleted = false;
        _state.ChangeState(State_Move.Instance);
    }

    protected virtual void SetUnitPositionType()
    {
        
    }

    /// <summary>
    /// Spine "callEffect" 이벤트마다 호출: 타겟마다 현재 타격 단계의 스킬 효과(데미지, 버프, 상태이상 등)를 적용한다.
    /// 채집물은 1씩 채집한다.
    /// </summary>
    public void TargetHit()
    {
        foreach (var targetData in _skillTarget)
        {
            if (targetData._skillTarget as BaseGatherResUnit)
            {
                if (targetData._skillTarget.IsDie)
                    continue;

                targetData._skillTarget.OnDamage(1, DamageTextName.Gather, this);
            }
            else
                SentenceManager.ActiveSkillDetail(targetData, this, _activeSkill);
        }
    }

    #region DurationEffect

    /// <summary>지속 효과 추가. 면역이면 무시하고, 같은 효과는 최대 중첩 수까지만 쌓인다.</summary>
    public void AddDurationEffect(DurationEffectBase durationEffectBase)
    {
        if (EnableAddDurationEffect(durationEffectBase))
        {
            if (!_durationEffectDic.ContainsKey(durationEffectBase._id))
                _durationEffectDic.Add(durationEffectBase._id, new List<DurationEffectBase>());
            durationEffectBase.EnterState();
            _durationEffectDic[durationEffectBase._id].Add(durationEffectBase);
        }
    }

    private bool EnableAddDurationEffect(DurationEffectBase durationEffectBase)
    {
        if (durationEffectBase._statusType != EStatusEffectType.None)
        {
            EStatusEffectType statusEffectType = durationEffectBase._statusType;
            if(GetImmuneStatusEffect(statusEffectType) || GetPassiveImmuneStatusEffect(statusEffectType) || IsStatusImmune)
                return false;
        }
        
        if (_durationEffectDic.TryGetValue(durationEffectBase._id, out var buffList))
            return buffList.Count < durationEffectBase._overlapCount;

        return true;
    }

    public void RemoveDurationEffect(DurationEffectBase durationEffectBase)
    {
        if (_durationEffectDic.TryGetValue(durationEffectBase._id, out var buffList))
        {
            if (!buffList.Contains(durationEffectBase))
                return;

            buffList.Remove(durationEffectBase);
            durationEffectBase.ExitState();
        }
    }

    // 전부 삭제 (죽었을 때 및 캐릭터가 변경되었을 때)
    private void RemoveAllDurationEffect()
    {
        var keys = new List<int>(_durationEffectDic.Keys);
        foreach (var key in keys)
        {
            if (!_durationEffectDic.TryGetValue(key, out var list))
                continue;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (i >= list.Count)
                    continue;

                DurationEffectBase durationEffectBase = list[i];
                list.RemoveAt(i);
                durationEffectBase.ExitState();
            }
        }
    }

    private void UpdateDurationEffect()
    {
        if (IsDie || _durationEffectDic.Count == 0)
            return;

        var keys = new List<int>(_durationEffectDic.Keys);
        foreach (var key in keys)
        {
            if (!_durationEffectDic.TryGetValue(key, out var list))
                continue;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (i >= list.Count) continue;
                DurationEffectBase durationEffectBase = list[i];
                if (durationEffectBase != null)
                    durationEffectBase.UpdateState();
            }
        }
    }

    // 해당 상태이상을 주는 지속 효과가 하나라도 남아 있는지
    private bool IsStatusEffectActive(EStatusEffectType effectType)
    {
        foreach (var list in _durationEffectDic.Values)
        {
            foreach (var durationEffect in list)
            {
                if (durationEffect == null)
                    continue;
                
                if (durationEffect._statusType == effectType)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 상태이상 걸렸을시, 풀렸을시 변수 상태 변경
    /// </summary>
    /// <param name="effectType"></param>
    /// <param name="isActive"></param>
    public void ActiveStatus(EStatusEffectType effectType, bool isActive)
    {
        if (effectType == EStatusEffectType.None)
            return;

        _statusEffects[(int)effectType] = isActive;
    }

    /// <summary>상태이상 해제. 같은 상태이상을 주는 다른 효과가 남아 있으면 유지한다.</summary>
    public void DisableStatus(EStatusEffectType effectType)
    {
        bool existStatus = IsStatusEffectActive(effectType);
        if (existStatus) // 제거를 했는데도 같은 타입이 존재하면 상태를 바꾸지 않는다.
            return;

        ActiveStatus(effectType, false);
    }

    public bool GetStatusEffect(EStatusEffectType effectType)
    {
        return _statusEffects[(int)effectType];
    }
    
    public void ActiveImmuneStatus(EStatusEffectType effectType, bool isActive)
    {
        if (effectType == EStatusEffectType.None)
            return;

        _statusImmuneEffects[(int)effectType] = isActive;
    }
    
    public bool GetImmuneStatusEffect(EStatusEffectType effectType)
    {
        return _statusImmuneEffects[(int)effectType];
    }
    
    public void ActivePassiveImmuneStatus(EStatusEffectType effectType, bool isActive)
    {
        if (effectType == EStatusEffectType.None)
            return;

        _passiveImmuneEffects[(int)effectType] = isActive;
    }
    public bool GetPassiveImmuneStatusEffect(EStatusEffectType effectType)
    {
        return _passiveImmuneEffects[(int)effectType];
    }

    #endregion

    #region GroundEffect

    public void AddGroundEffect(GroundBuff groundEffectBase)
    {
        if (_groundEffectDic.ContainsKey(groundEffectBase._id))
            return;
        
        groundEffectBase.EnterState();
        _groundEffectDic.Add(groundEffectBase._id, groundEffectBase);
    }

    public void RemoveGroundEffect(int id)
    {
        if (!_groundEffectDic.ContainsKey(id))
            return;
        
        GroundBuff groundEffectBase = _groundEffectDic[id];
        _groundEffectDic.Remove(groundEffectBase._id);
        groundEffectBase.ExitState();
    }

    // 전부 삭제 (죽었을 때 및 캐릭터가 변경되었을 때)
    private void RemoveAllGroundEffect()
    {
        foreach (var effectBase in _groundEffectDic.Values)
            effectBase.ExitState();

        _groundEffectDic.Clear();
    }

    /// <summary>모든 효과 / 타겟 / 상태이상 / 면역을 지우고 스탯을 원래대로 되돌린다.</summary>
    protected void RemoveAllEffect()
    {
        RemoveAllGroundEffect();
        RemoveAllDurationEffect();
        ResetSkillTargetData();
        _statusInfo?.ResetPlayStatus();
        _statusInfo?.ResetSumStatus();
        Array.Clear(_statusEffects, 0, _statusEffects.Length);
        Array.Clear(_statusImmuneEffects, 0, _statusImmuneEffects.Length);
        Array.Clear(_passiveImmuneEffects, 0, _passiveImmuneEffects.Length);
    }
    
    #endregion

    /// <summary>스킬 시작 이펙트: 설정에 따라 타겟마다 또는 시전자에게 붙여서 재생한다.</summary>
    private async UniTask PlayStartEffect()
    {
        if (_activeSkill == null)
            return;

        if (_activeSkill.GetMainDetailData().StartEffect.IsNull())
            return;

        if (_activeSkill.GetMainDetailData().StartEffectTarget == ESentenceEffectType.Target)
        {
            foreach (var skillTargetData in _skillTarget)
            {
                BaseUnit target = skillTargetData._skillTarget;
                SentenceEffect effect = await SentenceManager.ActiveEffect(EPoolType.StartEffect, _activeSkill.GetMainDetailData().StartEffect);
                effect.PlayAttached(target, _activeSkill.GetMainDetailData().StartEffectTarget, target.LeftDir);   
            }
        }
        else
        {
            SentenceEffect effect = await SentenceManager.ActiveEffect(EPoolType.StartEffect, _activeSkill.GetMainDetailData().StartEffect);
            effect.PlayAttached(this, _activeSkill.GetMainDetailData().StartEffectTarget, LeftDir);   
        }
        
    }

    public async UniTask PlayHitEffect(string effectName)
    {
        if (effectName.IsNull())
            return;

        if (!Managers.Instance.UserInfo()._isDamageOn)
            return;
        
        SentenceEffect effect = await SentenceManager.ActiveEffect(EPoolType.CommonEffect, effectName);
        effect.SetTransform(CenterPosition, Quaternion.identity);
        effect.PlayAtPosition(LeftDir);
    }

    public void RefreshPlayStatus()
    {
        _statusInfo.ResetPlayStatus();
    }

    /// <summary>
    /// 피격 처리: 피격 플래시와 데미지 텍스트를 띄우고 HP를 깎는다.
    /// 무적이면 HP가 줄지 않고, 불사(IsImmortal)면 HP 1에서 멈춘다. HP 비율 조건 패시브를 확인하고 0이면 사망.
    /// </summary>
    public virtual void OnDamage(double damage, string damageTextName, BaseUnit attacker)
    {
        _flashEffect.Flash();
        Managers.Instance.GetObjectUnitManager().ShowDamageText(CenterPosition, damage, damageTextName).Forget();

        if (IsInvincible)
            return;

        double hp = Math.Max(_playStatus.Hp - damage, 0);
        if (IsImmortal)
            hp = Math.Max(hp, 1);

        _playStatus.Hp = hp;

        float ratio = _playStatus.HpRatio;
        if (_hpUI != null)
            _hpUI.UpdateUI(ratio);

        _passiveSkillObserver?.Trigger(EPassiveSkillTriggerType.HpPercent, this, ratio);
        if (_playStatus.Hp <= 0 && !IsDie)
            OnDie(attacker);
    }

    public virtual void OnHeal(double heal)
    {
        double hp = _playStatus.Hp + heal;
        if (hp > _playStatus.TotalMaxHp)
            hp = _playStatus.TotalMaxHp;
        else if (hp < 0)
            hp = 0;

        _playStatus.Hp = hp;

        Managers.Instance.GetObjectUnitManager().ShowDamageText(CenterPosition, heal, DamageTextName.Heal).Forget();

        float ratio = _playStatus.HpRatio;
        if (_hpUI != null)
            _hpUI.UpdateUI(ratio);
    }

    /// <summary>
    /// 사망 처리: 처치한 유닛의 킬 수 / 처치 패시브를 갱신하고, 모든 효과를 지운 뒤 충돌과 이동을 끈다.
    /// </summary>
    protected virtual void OnDie(BaseUnit attacker)
    {
        if (UnitType != EUnitType.Gather && attacker != null)
        {
            attacker._unitCombatStats.AddKillCount(1);
            attacker._passiveSkillObserver.Trigger(EPassiveSkillTriggerType.KillEnemy, attacker);
        }

        _state.ChangeState(State_Die.Instance);
        RemoveAllEffect();
        _collider.enabled = false;
        _rigidbody.simulated = false;
        _moveTarget = null;

        if (_agent != null)
            _agent.Stop();
    }

    public void LookAtTarget(Vector2 targetPosition)
    {
        Vector2 dir = targetPosition - _rigidbody.position;
        LookAtDir(dir);
    }

    protected void LookAtDir(Vector2 dir)
    {
        if (Mathf.Abs(dir.x) < 0.2f)
            return;

        LeftDir = dir.x < 0;
    }

    public virtual void Resurrection()
    {
        
    }

    /// <summary>넉백: 밀려나는 힘을 저장하고 넉백 상태로 전환한다. (이미 넉백 중이면 무시)</summary>
    public void SetPush(Vector2 pushVector)
    {
        if (IsKnockBack)
            return;

        IsKnockBack = true;
        knockbackForce = pushVector;

        _state.ChangeState(State_KnockBack.Instance);
    }

#if UNITY_EDITOR
    /// <summary>디버그용 범위 표시: 이동 탐색 범위(노랑), 스킬 사거리(빨강), 스킬 범위(파랑), 타겟 연결선</summary>
    void OnDrawGizmos()
    {
        if (_unitDataTable == null || _statusInfo == null)
            return;

        if (MoveTargetSearchRange)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(CenterPosition, (float)_playStatus.MoveTargetSearchRange * 0.01f);
        }

        if (SkillTargetSearchRange && _activeSkill != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(CenterPosition, (float)_activeSkill.GetMainDetailData().RangeSize * 0.01f);
        }

        if ((SkillTargetAreaRange || SkillTargetLine) && _activeSkill != null && _skillTarget.Count > 0)
        {
            var areaSize = _activeSkill.GetMainDetailData().AreaSize;
            float areaRadius = areaSize != null && areaSize.Length > 0 ? areaSize[areaSize.Length - 1] * 0.01f : 0f;
            foreach (var targetData in _skillTarget)
            {
                if (SkillTargetAreaRange)
                {
                    Gizmos.color = Color.blue;
                    Gizmos.DrawWireSphere(targetData._targetPosition, areaRadius);
                }

                if (SkillTargetLine)
                {
                    Gizmos.color = Color.black;
                    Gizmos.DrawLine(CenterPosition, targetData._targetPosition);
                }
            }
        }

        if (MoveTargetLine && _moveTarget != null)
        {
            Gizmos.color = Color.white;
            Gizmos.DrawLine(CenterPosition, _moveTarget.CenterPosition);
        }
    }
#endif

    public virtual void SetMass(bool isLock)
    {
    }

    public void SetRadiusScaling(float scaling)
    {
        _collider.radius = _unitRadius * scaling;
    }

    public bool InBattleState()
    {
        return _moveTarget != null || _skillTarget.Count > 0;
    }

    public virtual string GetSpineResourceName()
    {
        return _unitDataTable.Resource;
    }

    public Vector2 GetFirePoint()
    {
        if (_firePointBone == null)
            return _rigidbody.position;

        return _firePointBone.GetWorldPosition(transform);
    }

    public bool IsSkillTargetValid()
    {
        return _skillTarget.Count > 0 && _skillTarget[0]._skillTarget.IsValid();
    }

    /// <summary>이동 중 다른 구역(존)에 들어서면 그 구역의 PolyNav 맵으로 바꾼다. (마을은 Squad에서 처리)</summary>
    protected virtual void UpdatePolyNavMap()
    {
        int zoneIndex = MapManager.EnableEntrance(_rigidbody.position);

        if (MapManager.IsTownZone(zoneIndex)) // 마을은 Squad에서 세팅
            return;

        if (zoneIndex == -1 || _zoneIndex == zoneIndex)
            return;

        SetZoneIndex(zoneIndex);
    }

    public void SetZoneIndex(int zoneIndex)
    {
        _zoneIndex = zoneIndex;

        if (_agent)
            _agent.map = MapManager.GetPolyNavMap(_zoneIndex);
    }

    public void SetCoolDown(float time)
    {
        _skillObserver.ActiveCoolDown(time);
    }

    public void ResetSkillTargetData()
    {
        foreach (var skillTarget in _skillTarget)
        {
            if (skillTarget != null)
            {
                skillTarget._skillTarget = null;
                if(skillTarget._spellIndicator != null)
                    skillTarget._spellIndicator.Cancel();
            }
        }
        _skillTarget.Clear();
    }

    public StatusInfo GetStatusInfo()
    {
        return _statusInfo;
    }
}
