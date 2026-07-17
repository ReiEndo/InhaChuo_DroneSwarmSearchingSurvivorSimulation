using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class RightClickDragCamera : MonoBehaviour
{
    [Header("Drag")]
    public float lookSensitivity = 0.15f;
    public float moveSensitivity = 0.01f;
    public float zoomSensitivity = 3f;

    [Header("Keys")]
    public float keyboardMoveSpeed = 4f;
    public float fastMultiplier = 3f;

    private float yaw;
    private float pitch;
    private bool ownsCursorLock;
    private bool cursorOwnershipLostUntilRelease;
    private CursorLockMode previousCursorLockState;
    private bool previousCursorVisibility;

    private void Awake()
    {
        InitializeLookAngles();
    }

    private void OnEnable()
    {
        InitializeLookAngles();
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    private void Update()
    {
        bool rightMouseHeld = IsRightMouseHeld();
        Vector2 mouseDelta = GetMouseDelta();

        if (rightMouseHeld)
        {
            LockCursor();

            yaw += mouseDelta.x * lookSensitivity;
            pitch -= mouseDelta.y * lookSensitivity;
            pitch = Mathf.Clamp(pitch, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }
        else
        {
            ReleaseCursor();
            cursorOwnershipLostUntilRelease = false;
        }

        if (IsMiddleMouseHeld())
        {
            float x = -mouseDelta.x * moveSensitivity;
            float y = -mouseDelta.y * moveSensitivity;
            transform.Translate(new Vector3(x, y, 0f), Space.Self);
        }

        float scroll = GetScrollY();
        if (Mathf.Abs(scroll) > 0.001f)
        {
            transform.Translate(Vector3.forward * scroll * zoomSensitivity, Space.Self);
        }

        if (rightMouseHeld)
        {
            float speed = keyboardMoveSpeed * (IsFastHeld() ? fastMultiplier : 1f);
            Vector3 move = Vector3.zero;
            if (IsKeyHeld(KeyCode.W)) move += Vector3.forward;
            if (IsKeyHeld(KeyCode.S)) move += Vector3.back;
            if (IsKeyHeld(KeyCode.A)) move += Vector3.left;
            if (IsKeyHeld(KeyCode.D)) move += Vector3.right;
            if (IsKeyHeld(KeyCode.E)) move += Vector3.up;
            if (IsKeyHeld(KeyCode.Q)) move += Vector3.down;
            transform.Translate(move.normalized * speed * Time.deltaTime, Space.Self);
        }
    }

    private void OnDisable()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        ReleaseCursor();
    }

    private void OnDestroy()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        ReleaseCursor();
    }

    private void OnSceneUnloaded(Scene scene)
    {
        ReleaseCursor();
    }

    private void InitializeLookAngles()
    {
        Vector3 euler = transform.eulerAngles;
        yaw = ToSignedAngle(euler.y);
        pitch = ToSignedAngle(euler.x);
    }

    private static float ToSignedAngle(float angle)
    {
        return Mathf.DeltaAngle(0f, angle);
    }

    private void LockCursor()
    {
        if (ownsCursorLock)
        {
            if (!HasAppliedCursorState())
            {
                // Do not reclaim cursor state changed by another controller mid-drag.
                ownsCursorLock = false;
                cursorOwnershipLostUntilRelease = true;
            }

            return;
        }

        if (cursorOwnershipLostUntilRelease) return;

        previousCursorLockState = Cursor.lockState;
        previousCursorVisibility = Cursor.visible;
        ownsCursorLock = true;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void ReleaseCursor()
    {
        if (!ownsCursorLock) return;

        if (!HasAppliedCursorState())
        {
            // Preserve cursor state changed by another controller.
            ownsCursorLock = false;
            return;
        }

        Cursor.lockState = previousCursorLockState;
        Cursor.visible = previousCursorVisibility;
        ownsCursorLock = false;
    }

    private static bool HasAppliedCursorState()
    {
        return Cursor.lockState == CursorLockMode.Locked && !Cursor.visible;
    }

    private static bool IsRightMouseHeld()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null && Mouse.current.rightButton.isPressed;
#else
        return Input.GetMouseButton(1);
#endif
    }

    private static bool IsMiddleMouseHeld()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null && Mouse.current.middleButton.isPressed;
#else
        return Input.GetMouseButton(2);
#endif
    }

    private static Vector2 GetMouseDelta()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current == null ? Vector2.zero : Mouse.current.delta.ReadValue();
#else
        return new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
#endif
    }

    private static float GetScrollY()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current == null ? 0f : Mouse.current.scroll.ReadValue().y * 0.01f;
#else
        return Input.mouseScrollDelta.y;
#endif
    }

    private static bool IsFastHeld()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null &&
               (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
#else
        return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
#endif
    }

    private static bool IsKeyHeld(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current == null) return false;

        return key switch
        {
            KeyCode.W => Keyboard.current.wKey.isPressed,
            KeyCode.A => Keyboard.current.aKey.isPressed,
            KeyCode.S => Keyboard.current.sKey.isPressed,
            KeyCode.D => Keyboard.current.dKey.isPressed,
            KeyCode.Q => Keyboard.current.qKey.isPressed,
            KeyCode.E => Keyboard.current.eKey.isPressed,
            _ => false
        };
#else
        return Input.GetKey(key);
#endif
    }
}
