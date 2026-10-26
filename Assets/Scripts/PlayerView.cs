using UnityEngine;
using UnityEngine.InputSystem;

// Rotate the view before SwingDash reads the camera's forward direction.
[DefaultExecutionOrder(-100)]
public sealed class PlayerView : MonoBehaviour
{
    public Camera viewCamera;
    [Min(0f), Tooltip("每像素鼠标移动对应的视角旋转角度")]
    public float sensitivity = 0.12f;
    [Range(1f, 89f)] public float pitchLimit = 80f;
    public bool invertY;
    [Min(0f), Tooltip("刀在屏幕上跟随鼠标移动的倍率，与视角灵敏度分开调整")]
    public float weaponMouseSensitivity = 1f;

    public Vector2 WeaponViewportPosition { get; private set; } = new Vector2(0.5f, 0.5f);
    public Vector2 MouseDelta { get; private set; }

    private float yaw, pitch;
    private bool skipMouseFrame;
    private CursorLockMode previousLockMode;
    private bool previousCursorVisible, ownsCursor;

    private void OnEnable()
    {
        if (viewCamera == null) viewCamera = GetComponentInChildren<Camera>();
        yaw = transform.eulerAngles.y;
        pitch = viewCamera != null ? Mathf.DeltaAngle(0f, viewCamera.transform.localEulerAngles.x) : 0f;
        // Start at the existing mouse position, then continue with an unlocked virtual pointer.
        if (viewCamera != null && Mouse.current != null)
        {
            Rect rect = viewCamera.pixelRect;
            Vector2 mouse = Mouse.current.position.ReadValue();
            if (rect.width > 0f && rect.height > 0f)
                WeaponViewportPosition = new Vector2(Mathf.Clamp01((mouse.x - rect.x) / rect.width),
                    Mathf.Clamp01((mouse.y - rect.y) / rect.height));
        }
        CaptureCursor();
    }

    private void Update()
    {
        MouseDelta = Vector2.zero;
        if (viewCamera == null || Mouse.current == null || !Application.isFocused || Time.deltaTime <= 0f)
            return;
        if (skipMouseFrame) { skipMouseFrame = false; return; }
        Vector2 delta = Mouse.current.delta.ReadValue();
        MouseDelta = delta;
        yaw = Mathf.Repeat(yaw + delta.x * sensitivity, 360f);
        pitch = Mathf.Clamp(pitch + delta.y * sensitivity * (invertY ? 1f : -1f), -pitchLimit, pitchLimit);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        viewCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);

        Rect viewport = viewCamera.pixelRect;
        if (viewport.width <= 0f || viewport.height <= 0f) return;
        Vector2 pointer = WeaponViewportPosition;
        pointer.x = Mathf.Clamp01(pointer.x + delta.x * weaponMouseSensitivity / viewport.width);
        pointer.y = Mathf.Clamp01(pointer.y + delta.y * weaponMouseSensitivity / viewport.height);
        WeaponViewportPosition = pointer;
    }

    private void CaptureCursor()
    {
        if (!ownsCursor)
        {
            previousLockMode = Cursor.lockState;
            previousCursorVisible = Cursor.visible;
            ownsCursor = true;
        }
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        skipMouseFrame = true;
        MouseDelta = Vector2.zero;
    }

    private void ReleaseCursor()
    {
        if (!ownsCursor) return;
        Cursor.lockState = previousLockMode;
        Cursor.visible = previousCursorVisible;
        ownsCursor = false;
    }

    private void OnApplicationFocus(bool focused)
    {
        if (focused) CaptureCursor();
        else ReleaseCursor();
    }

    private void OnDisable()
    {
        ReleaseCursor();
    }
}
