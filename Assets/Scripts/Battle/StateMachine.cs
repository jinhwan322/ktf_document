using Cysharp.Threading.Tasks;
using Spine;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Spine 애니메이션에 들어 있는 이벤트(이름, 시간)</summary>
class CallEventData
{
    public string name;
    public float time;
}

/// <summary>
/// 유닛 FSM.
/// 상태 전환(Exit → Enter)과 함께, 공격 / 스킬 애니메이션의 Spine "callEffect" 이벤트를 고정 틱 시간으로 추적해
/// 이벤트 시점에 맞춰 스킬 판정(BaseUnit.TargetHit)을 호출한다.
/// 스킬 하나에 이벤트가 여러 개면 이벤트마다 다음 스킬 디테일(타격 단계)로 넘어가고,
/// 다음 타격까지 남은 시간 동안 범위 표시(Spell Indicator)를 띄운다.
/// </summary>
public class StateMachine<T> where T : BaseUnit
{
    private const string CallEffectEventName = "callEffect";

    private T baseUnit;
    private FSM_State<T> currState;
    private FSM_State<T> prevState;

    // 현재 재생 중인 애니메이션과 callEffect 이벤트 목록
    private float duration;
    private Spine.Animation animation;
    private readonly List<CallEventData> callEventDatas = new();
    private float time = 0;

    private int _callIndex = 0;

    /// <summary>다음에 처리할 이벤트 순번. 바뀔 때마다 스킬 디테일 단계와 범위 표시를 갱신한다.</summary>
    private int CallIndex
    {
        get => _callIndex;
        set
        {
            _callIndex = value;
            SetSkillDetailIndex();
        }
    }

    public void ChangeState(FSM_State<T> _newState)
    {
        prevState = currState;

        if (currState != null)
            currState.ExitState(baseUnit);

        currState = _newState;

        if (currState != null)
            currState.EnterState(baseUnit);
    }

    public void Init(T _owner, FSM_State<T> _initState)
    {
        baseUnit = _owner;
        ChangeState(_initState);
    }

    public void Update()
    {
        if (currState == null)
            return;

        currState.UpdateState(baseUnit);
    }

    /// <summary>직전 상태로 되돌린다.</summary>
    public void StateRevert()
    {
        if (prevState != null)
            ChangeState(prevState);
    }

    public FSM_State<T> GetCurState()
    {
        return currState;
    }

    /// <summary>재생할 애니메이션을 등록하고, 애니메이션에 들어 있는 callEffect 이벤트를 모아 둔다.</summary>
    public void SetAnimation(Spine.Animation anim)
    {
        if (anim == null)
            return;

        animation = anim;
        duration = animation.Duration;
        time = 0.0f;
        callEventDatas.Clear();

        foreach (Timeline timeline in animation.Timelines)
        {
            if (timeline is not EventTimeline eventTimeline)
                continue;

            foreach (Spine.Event sEvent in eventTimeline.Events)
            {
                if (sEvent != null && sEvent.Data.Name.Equals(CallEffectEventName))
                    callEventDatas.Add(new CallEventData { name = sEvent.Data.Name, time = sEvent.Time });
            }
        }
        CallIndex = 0;
    }

    /// <summary>
    /// 고정 틱마다 애니메이션 시간을 진행하고, callEffect 이벤트 시간에 도달하면 스킬 판정을 호출한다.
    /// 렌더링 프레임이 아닌 FixedUpdate 시간으로 판정해서 프레임 저하에도 타격 타이밍이 같다.
    /// </summary>
    /// <returns>애니메이션이 끝났으면 true (애니메이션이 없으면 바로 true)</returns>
    public bool SetFixedTime()
    {
        if (animation == null)
            return true;

        time += Time.fixedDeltaTime;

        if (CallIndex < callEventDatas.Count && time >= callEventDatas[CallIndex].time)
        {
            baseUnit.TargetHit();
            CallIndex++;
        }

        return time >= duration;
    }

    /// <summary>
    /// 현재 타격 단계를 유닛에 알리고, 직전 이벤트부터 이번 이벤트까지의 시간 동안 범위 표시를 띄운다.
    /// </summary>
    private void SetSkillDetailIndex()
    {
        if (CallIndex >= callEventDatas.Count)
            return;

        baseUnit.SetSkillDetailIndex(CallIndex);
        float startTime = CallIndex == 0 ? 0 : callEventDatas[CallIndex - 1].time;
        float endTime = callEventDatas[CallIndex].time;
        baseUnit.ActiveSkillPreview(endTime - startTime).Forget();
    }
}
