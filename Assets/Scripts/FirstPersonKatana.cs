using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(BoxCollider))]
public sealed class FirstPersonKatana : MonoBehaviour
{
    [SerializeField] private Camera viewCamera;
    [Header("Grip / 握柄")]
    [Tooltip("握柄在相机空间中的位置（米）")]
    public Vector3 gripPosition = new Vector3(0f, -0.65f, 1.6f);
    [Tooltip("握柄主动跟随鼠标的幅度（米）；超出刀长可达范围时仍会自动补偿")]
    public Vector2 handMovementRange = new Vector2(0.1f, 0.06f);
    [Tooltip("模型局部坐标中的握持点；当前 Cube 的底端是 (0, -0.5, 0)")]
    public Vector3 localGripPoint = new Vector3(0f, -0.5f, 0f);
    [Tooltip("模型局部坐标中的刀尖；替换模型后可修改这两个点")]
    public Vector3 localTipPoint = new Vector3(0f, 0.5f, 0f);

    [Header("Mouse tracking / 鼠标跟随")]
    [Min(0f), Tooltip("仅平滑握柄的主动位移；刀尖始终立即对齐鼠标。0 为握柄立即跟随")]
    public float handFollowSharpness = 30f;

    private Vector2 smoothedPointer;
    private bool pointerInitialized;
    private PlayerView playerView;
    private void OnEnable()
    {
        pointerInitialized = false;
        playerView = GetComponentInParent<PlayerView>();
    }

    private void OnApplicationFocus(bool focused)
    {
        pointerInitialized = false;
    }

    private void LateUpdate()
    {
        if (Mouse.current == null || viewCamera == null || !Application.isFocused)
        {
            return;
        }

        Vector2 mousePosition = Mouse.current.position.ReadValue();
        Rect viewport = viewCamera.pixelRect;
        if (viewport.width <= 0f || viewport.height <= 0f)
            return;

        float x = Mathf.Clamp((mousePosition.x - viewport.x) / viewport.width * 2f - 1f, -1f, 1f);
        float y = Mathf.Clamp((mousePosition.y - viewport.y) / viewport.height * 2f - 1f, -1f, 1f);
        if (playerView != null && playerView.isActiveAndEnabled)
        {
            // A locked OS cursor stays centered; the weapon instead uses the virtual pointer.
            x = playerView.WeaponViewportPosition.x * 2f - 1f;
            y = playerView.WeaponViewportPosition.y * 2f - 1f;
        }
        Vector2 pointer = new Vector2(x, y);
        float blend = handFollowSharpness <= 0f ? 1f : 1f - Mathf.Exp(-handFollowSharpness * Time.deltaTime);
        smoothedPointer = pointerInitialized ? Vector2.Lerp(smoothedPointer, pointer, blend) : pointer;
        pointerInitialized = true;

        // Smooth only the preferred grip. The tip uses the current, unsmoothed mouse ray.
        Vector3 grip = gripPosition + new Vector3(
            smoothedPointer.x * handMovementRange.x, smoothedPointer.y * handMovementRange.y, 0f);
        // Include parent scale when computing the physical grip-to-tip length.
        Vector3 parentScale = transform.parent == null ? Vector3.one : transform.parent.lossyScale;
        Vector3 worldScale = Vector3.Scale(transform.localScale, parentScale);
        Vector3 scaledAxis = Vector3.Scale(localTipPoint - localGripPoint, worldScale);
        Vector3 modelAxis = Vector3.Scale(localTipPoint - localGripPoint, transform.localScale);
        float bladeLength = scaledAxis.magnitude;
        if (bladeLength < 0.0001f || modelAxis.sqrMagnitude < 0.000001f)
            return;

        Vector3 screenPoint = new Vector3(
            viewport.x + (x + 1f) * 0.5f * viewport.width,
            viewport.y + (y + 1f) * 0.5f * viewport.height, 0f);
        Ray mouseRay = viewCamera.ScreenPointToRay(screenPoint);
        Vector3 worldGrip = viewCamera.transform.TransformPoint(grip);

        // Intersect the mouse ray with a sphere centered at the grip, radius = blade length.
        // If unreachable, shift the grip toward the ray just enough to keep the blade rigid.
        float closestDistance = Mathf.Max(0f, Vector3.Dot(worldGrip - mouseRay.origin, mouseRay.direction));
        Vector3 closestPoint = mouseRay.GetPoint(closestDistance);
        Vector3 lateralOffset = worldGrip - closestPoint;
        float reachableRadius = bladeLength * 0.98f;
        if (lateralOffset.sqrMagnitude > reachableRadius * reachableRadius)
            worldGrip = closestPoint + lateralOffset.normalized * reachableRadius;

        Vector3 toGrip = worldGrip - mouseRay.origin;
        float alongRay = Vector3.Dot(toGrip, mouseRay.direction);
        Vector3 perpendicular = toGrip - mouseRay.direction * alongRay;
        float halfChord = Mathf.Sqrt(Mathf.Max(0f, bladeLength * bladeLength - perpendicular.sqrMagnitude));
        Vector3 worldTip = mouseRay.GetPoint(Mathf.Max(0f, alongRay + halfChord));
        Vector3 direction = (worldTip - worldGrip).normalized;
        Quaternion rotation = viewCamera.transform.rotation * Quaternion.FromToRotation(
            modelAxis.normalized, viewCamera.transform.InverseTransformDirection(direction));

        // Anchor the actual model tip to the ray, not merely its forward direction.
        transform.SetPositionAndRotation(worldTip - rotation * Vector3.Scale(localTipPoint, worldScale), rotation);
    }

}
