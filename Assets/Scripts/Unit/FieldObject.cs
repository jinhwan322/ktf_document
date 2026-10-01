using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 필드에 놓이는 모든 오브젝트(유닛, 건물)의 베이스.
/// 2D 물리(원형 콜라이더 + Rigidbody2D), Spine 애니메이션, 좌우 방향, 정렬 그룹을 공통으로 세팅한다.
/// </summary>
public class FieldObject : MonoBehaviour
{
    [SerializeField] protected string _name;

    public CircleCollider2D _collider;
    public Rigidbody2D _rigidbody;
    protected float ColliderRadius => _collider.radius;
    /// <summary>콜라이더 중심(발밑이 아닌 몸통 중앙). 거리 계산, 이펙트 / 데미지 텍스트 위치로 사용</summary>
    public Vector2 CenterPosition => (Vector2)transform.position + new Vector2(0, _collider.offset.y);
    public SpineAnimation _spineAnimation;
    protected SortingGroup tSort = null;
    private bool leftDir = true;

    protected SyncCurrencyManager SyncCurrencyManager => Managers.Instance.GetSyncCurrencyManager();

    /// <summary>왼쪽을 보고 있는지. 바꾸면 Spine 스켈레톤을 좌우 반전한다.</summary>
    public bool LeftDir
    {
        get => leftDir;
        set
        {
            leftDir = value;
            _spineAnimation.Skeleton.ScaleX = value ? 1 : -1;
        }
    }

    public Vector2 _unitPos;

    public virtual void Awake()
    {
        _collider = gameObject.GetOrAddComponent<CircleCollider2D>();
        _collider.isTrigger = false;
        _rigidbody = gameObject.GetOrAddComponent<Rigidbody2D>();
        _rigidbody.collisionDetectionMode = CollisionDetectionMode2D.Discrete;
        _rigidbody.sleepMode = RigidbodySleepMode2D.StartAwake;
        _rigidbody.interpolation = RigidbodyInterpolation2D.Interpolate;
        _spineAnimation = gameObject.GetOrAddComponent<SpineAnimation>();

        // Spine SkeletonAnimation은 SpriteRenderer가 아닌 MeshRenderer로 그려서 2D Sort Axis(y축 정렬)가 적용되지 않는다.
        // SortingGroup을 붙이면 SpriteRenderer와 MeshRenderer를 같은 기준으로 정렬할 수 있다.
        tSort = Utils.GetOrAddComponent<SortingGroup>(gameObject);
        tSort.sortingOrder = Define.SortingLayers.UNIT;
    }
}
