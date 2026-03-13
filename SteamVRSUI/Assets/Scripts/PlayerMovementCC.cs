using UnityEngine;
using UnityEngine.InputSystem;
using Unity.XR.CoreUtils;

[RequireComponent(typeof(CharacterController))]
public class XRDesktopDebugSimple : MonoBehaviour
{
    public float moveSpeed = 3f;
    public float mouseSensitivity = 0.15f;

    [Header("XR Input")]
    public InputActionProperty jumpAction;

    [Header("Jump")]
    public float jumpHeight = 1.2f;
    public float gravity = -20f;

    CharacterController cc;
    Transform cam;

    float yVel;
    float pitch;

    InputAction move;
    InputAction look;
    InputAction jump;
    InputAction jumpFallback;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        cam = GetComponent<XROrigin>().Camera.transform;

        move = new InputAction(type: InputActionType.Value);
        move.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");

        look = new InputAction(type: InputActionType.Value, binding: "<Mouse>/delta");
        jump = new InputAction(type: InputActionType.Button, binding: "<Keyboard>/space");
        jumpFallback = new InputAction("Jump XR", InputActionType.Button, "<XRController>{RightHand}/gripPressed");
    }

    void OnEnable()
    {
        move.Enable();
        look.Enable();
        jump.Enable();
        (jumpAction.action ?? jumpFallback)?.Enable();
        LockCursor();
    }

    void OnDisable()
    {
        (jumpAction.action ?? jumpFallback)?.Disable();
    }

    void OnDestroy()
    {
        jumpFallback?.Dispose();
    }

    void Update()
    {
        if (Mouse.current.leftButton.wasPressedThisFrame)
            LockCursor();

        // --- Mouse look ---
        Vector2 mouse = look.ReadValue<Vector2>() * mouseSensitivity;

        pitch = Mathf.Clamp(pitch - mouse.y, -85f, 85f);
        cam.localRotation = Quaternion.Euler(pitch, 0, 0);
        transform.Rotate(Vector3.up * mouse.x);

        // --- Jump & gravity ---
        if (cc.isGrounded && yVel < 0)
            yVel = -2f;

        if (cc.isGrounded && (jump.WasPressedThisFrame() || ((jumpAction.action ?? jumpFallback)?.WasPressedThisFrame() ?? false)))
            yVel = Mathf.Sqrt(jumpHeight * -2f * gravity);

        yVel += gravity * Time.deltaTime;

        // --- Movement ---
        Vector2 input = move.ReadValue<Vector2>();
        Vector3 dir = transform.TransformDirection(new Vector3(input.x, 0, input.y));

        Vector3 motion = (dir * moveSpeed + Vector3.up * yVel) * Time.deltaTime;
        cc.Move(motion);
    }

    void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    // ⭐ QUICK FIX: called by TonfaThrower when releasing lock
    public void ResetVerticalVelocity()
    {
        yVel = 0f;
    }
}
