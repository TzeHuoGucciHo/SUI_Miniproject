using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class DualTonfaThrower : MonoBehaviour
{
    public enum Side { None, Left, Right }

    [Header("References")]
    public Transform throwDirection; // Main Camera
    public TonfaSticky leftTonfa;
    public TonfaSticky rightTonfa;
    public Transform leftHoldPoint;
    public Transform rightHoldPoint;

    [Header("XR Input")]
    public InputActionProperty leftAction;
    public InputActionProperty rightAction;
    public InputActionProperty recallLeftAction;
    public InputActionProperty recallRightAction;
    public InputActionProperty releaseAnchorAction;

    [Header("Throw")]
    public float throwSpeed = 18f;
    public float minThrowClearance = 0.25f;

    [Header("Spin (wheel)")]
    public float spinDegPerSec = 720f;
    public Vector3 localSpinAxis = new Vector3(1, 0, 0);

    [Header("Pull + Anchor")]
    public float pullSpeed = 10f;          // constant pull speed (simple & reliable)
    public float aimConeAngleDeg = 18f;    // only required to START pulling
    public float lockDistance = 0.6f;      // when you "hit" the tonfa -> lock
    public float wallOffset = 0.55f;       // how far off the wall you anchor

    [Header("Momentum (simple)")]
    public float coastDragAir = 0.2f;      // how fast coast fades in air (0 = no fade)
    public float groundStopTime = 0.2f;    // kill coast over this time after touching ground
    public float maxCoastSpeed = 18f;      // cap coast speed
    public float coastStartDelay = 0.0f;   // optional tiny delay before coasting (usually 0)

    CharacterController cc;
    XRDesktopDebugSimple desktopMove;

    Rigidbody leftRb, rightRb;
    Collider leftCol, rightCol;
    Collider[] playerColliders;

    bool leftHeld = true;
    bool rightHeld = true;

    Vector3 leftHoldLocalPos, rightHoldLocalPos;
    Quaternion leftHoldLocalRot, rightHoldLocalRot;

    Side pullingSide = Side.None;
    Side lockedSide = Side.None;

    // --- Momentum state (HORIZONTAL ONLY so jump never breaks) ---
    Vector3 coastVelXZ;      // world XZ velocity
    float coastDelayTimer;   // optional
    float groundedTime;      // how long we've been grounded continuously

    InputAction leftActionFallback;
    InputAction rightActionFallback;
    InputAction recallLeftActionFallback;
    InputAction recallRightActionFallback;
    InputAction releaseAnchorActionFallback;

    void OnEnable() => EnableInputActions();
    void OnDisable() => DisableInputActions();
    void OnDestroy() => DisposeFallbackActions();

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        desktopMove = GetComponent<XRDesktopDebugSimple>();
        playerColliders = GetComponentsInParent<Collider>(true);
        SetupFallbackActions();

        if (leftTonfa)
        {
            leftRb = leftTonfa.GetComponent<Rigidbody>();
            leftCol = leftTonfa.GetComponent<Collider>();
        }

        if (rightTonfa)
        {
            rightRb = rightTonfa.GetComponent<Rigidbody>();
            rightCol = rightTonfa.GetComponent<Collider>();
        }
    }

    void Start()
    {
        if (leftTonfa && leftHoldPoint)
        {
            leftHoldLocalPos = leftHoldPoint.InverseTransformPoint(leftTonfa.transform.position);
            leftHoldLocalRot = Quaternion.Inverse(leftHoldPoint.rotation) * leftTonfa.transform.rotation;
            HoldStill(Side.Left);
        }

        if (rightTonfa && rightHoldPoint)
        {
            rightHoldLocalPos = rightHoldPoint.InverseTransformPoint(rightTonfa.transform.position);
            rightHoldLocalRot = Quaternion.Inverse(rightHoldPoint.rotation) * rightTonfa.transform.rotation;
            HoldStill(Side.Right);
        }
    }

    void Update()
    {
        // grounded timer for ground-stop behavior
        if (cc.isGrounded) groundedTime += Time.deltaTime;
        else groundedTime = 0f;

        // Release anchor
        if (lockedSide != Side.None && WasPressedThisFrame(releaseAnchorAction, releaseAnchorActionFallback))
        {
            lockedSide = Side.None;
            desktopMove?.ResetVerticalVelocity();
        }

        // Retrieve
        if (WasPressedThisFrame(recallLeftAction, recallLeftActionFallback)) Recall(Side.Left);
        if (WasPressedThisFrame(recallRightAction, recallRightActionFallback)) Recall(Side.Right);

        // --- LEFT ACTION (throw + pull) ---
        if (WasPressedThisFrame(leftAction, leftActionFallback))
        {
            if (leftHeld) Throw(Side.Left);
        }

        if (!leftHeld && leftTonfa && leftTonfa.IsStuck && lockedSide == Side.None)
        {
            if (IsPressed(leftAction, leftActionFallback))
            {
                if (pullingSide != Side.Left && IsAimingAtTonfa(leftTonfa))
                {
                    pullingSide = Side.Left;
                    // when we start pulling, stop any old coasting
                    coastVelXZ = Vector3.zero;
                    coastDelayTimer = coastStartDelay;
                }

                if (pullingSide == Side.Left)
                    PullStep(Side.Left);
            }
            else
            {
                // released: start coasting using last pull direction/speed (captured in PullStep)
                if (pullingSide == Side.Left) pullingSide = Side.None;
            }
        }
        else
        {
            if (pullingSide == Side.Left) pullingSide = Side.None;
        }

        // --- RIGHT ACTION (throw + pull) ---
        if (WasPressedThisFrame(rightAction, rightActionFallback))
        {
            if (rightHeld) Throw(Side.Right);
        }

        if (!rightHeld && rightTonfa && rightTonfa.IsStuck && lockedSide == Side.None)
        {
            if (IsPressed(rightAction, rightActionFallback))
            {
                if (pullingSide != Side.Right && IsAimingAtTonfa(rightTonfa))
                {
                    pullingSide = Side.Right;
                    coastVelXZ = Vector3.zero;
                    coastDelayTimer = coastStartDelay;
                }

                if (pullingSide == Side.Right)
                    PullStep(Side.Right);
            }
            else
            {
                if (pullingSide == Side.Right) pullingSide = Side.None;
            }
        }
        else
        {
            if (pullingSide == Side.Right) pullingSide = Side.None;
        }

        // If not pulling and not anchored -> apply coast
        if (lockedSide == Side.None && pullingSide == Side.None)
            ApplyCoast();
        else
            coastDelayTimer = coastStartDelay; // reset delay while actively pulling/anchored
    }

    void LateUpdate()
    {
        // Keep held tonfas stable
        if (leftHeld) SnapToHoldPose(Side.Left);
        if (rightHeld) SnapToHoldPose(Side.Right);

        // Keep anchored
        if (lockedSide != Side.None)
        {
            TonfaSticky t = GetTonfa(lockedSide);
            if (t != null && t.IsStuck)
            {
                desktopMove?.ResetVerticalVelocity();
                coastVelXZ = Vector3.zero;

                Vector3 anchor = GetAnchorPoint(t);
                cc.Move(anchor - transform.position);
            }
        }
    }

    // ---------- Core actions ----------

    void Throw(Side side)
    {
        TonfaSticky t = GetTonfa(side);
        if (t == null) return;

        if (lockedSide == side) lockedSide = Side.None;
        if (pullingSide == side) pullingSide = Side.None;

        // throwing cancels coast (optional, feels cleaner)
        coastVelXZ = Vector3.zero;

        SetHeld(side, false);

        t.ResetStick();
        t.transform.SetParent(null, true);

        IgnorePlayerCollisions(GetCol(side), true);

        Rigidbody rb = GetRb(side);
        if (!rb) return;

        rb.isKinematic = false;
        rb.detectCollisions = true;

        Vector3 dir = (throwDirection ? throwDirection.forward : transform.forward).normalized;
        t.transform.position += dir * minThrowClearance;

        rb.linearVelocity = dir * throwSpeed;

        rb.maxAngularVelocity = 200f;
        Vector3 spinAxisWorld = t.transform.TransformDirection(localSpinAxis.normalized);
        rb.angularVelocity = spinAxisWorld * (spinDegPerSec * Mathf.Deg2Rad);
    }

    void Recall(Side side)
    {
        TonfaSticky t = GetTonfa(side);
        if (t == null) return;

        if (lockedSide == side) lockedSide = Side.None;
        if (pullingSide == side) pullingSide = Side.None;

        coastVelXZ = Vector3.zero;

        IgnorePlayerCollisions(GetCol(side), false);
        HoldStill(side);

        desktopMove?.ResetVerticalVelocity();
    }

    void HoldStill(Side side)
    {
        TonfaSticky t = GetTonfa(side);
        Rigidbody rb = GetRb(side);
        if (t == null || rb == null) return;

        SetHeld(side, true);

        t.ResetStick();

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;
        rb.detectCollisions = false;

        SnapToHoldPose(side);
    }

    void PullStep(Side side)
    {
        TonfaSticky t = GetTonfa(side);
        if (t == null || !t.IsStuck) return;

        // Stop desktop gravity from accumulating while pulling
        desktopMove?.ResetVerticalVelocity();

        Vector3 target = t.StuckPoint;
        Vector3 toTarget = target - transform.position;
        float dist = toTarget.magnitude;

        if (dist <= lockDistance)
        {
            pullingSide = Side.None;
            lockedSide = side;
            desktopMove?.ResetVerticalVelocity();
            coastVelXZ = Vector3.zero;
            SnapPlayerTo(GetAnchorPoint(t));
            return;
        }

        // original straight-to-target step
        Vector3 step = toTarget.normalized * pullSpeed * Time.deltaTime;
        if (step.magnitude > dist) step = toTarget;

        cc.Move(step);

        // Capture momentum from THIS pull step (XZ only)
        Vector3 v = step / Mathf.Max(Time.deltaTime, 0.00001f);
        coastVelXZ = new Vector3(v.x, 0f, v.z);

        // Clamp coast speed
        float s = coastVelXZ.magnitude;
        if (s > maxCoastSpeed)
            coastVelXZ = coastVelXZ.normalized * maxCoastSpeed;

        // Reset grounded timer because we're actively moving
        groundedTime = 0f;
    }

    void ApplyCoast()
    {
        if (coastVelXZ.sqrMagnitude < 0.0001f) return;

        if (coastDelayTimer > 0f)
        {
            coastDelayTimer -= Time.deltaTime;
            return;
        }

        // Apply the coast movement (XZ only)
        cc.Move(coastVelXZ * Time.deltaTime);

        if (cc.isGrounded)
        {
            // Kill coast quickly on ground over groundStopTime
            if (groundStopTime <= 0f)
            {
                coastVelXZ = Vector3.zero;
                return;
            }

            float k = Mathf.Clamp01(Time.deltaTime / groundStopTime);
            coastVelXZ = Vector3.Lerp(coastVelXZ, Vector3.zero, k);
        }
        else
        {
            // Light air drag (optional)
            if (coastDragAir > 0f)
            {
                float k = Mathf.Clamp01(coastDragAir * Time.deltaTime);
                coastVelXZ = Vector3.Lerp(coastVelXZ, Vector3.zero, k);
            }
        }
    }

    // ---------- Helpers ----------

    bool IsAimingAtTonfa(TonfaSticky t)
    {
        if (!throwDirection) return true;

        Vector3 toTonfa = t.transform.position - throwDirection.position;
        if (toTonfa.sqrMagnitude < 0.0001f) return true;

        float dot = Vector3.Dot(throwDirection.forward.normalized, toTonfa.normalized);
        return dot >= Mathf.Cos(aimConeAngleDeg * Mathf.Deg2Rad);
    }

    Vector3 GetAnchorPoint(TonfaSticky t)
    {
        return t.StuckPoint + t.StuckNormal.normalized * wallOffset;
    }

    void SnapPlayerTo(Vector3 pos)
    {
        cc.enabled = false;
        transform.position = pos;
        cc.enabled = true;
    }

    void SnapToHoldPose(Side side)
    {
        TonfaSticky t = GetTonfa(side);
        Transform hp = (side == Side.Left) ? leftHoldPoint : rightHoldPoint;
        if (t == null || hp == null) return;

        t.transform.SetParent(hp, true);

        if (side == Side.Left)
        {
            t.transform.localPosition = leftHoldLocalPos;
            t.transform.localRotation = leftHoldLocalRot;
        }
        else
        {
            t.transform.localPosition = rightHoldLocalPos;
            t.transform.localRotation = rightHoldLocalRot;
        }
    }

    void SetHeld(Side side, bool held)
    {
        if (side == Side.Left) leftHeld = held;
        else if (side == Side.Right) rightHeld = held;
    }

    TonfaSticky GetTonfa(Side side)
        => side == Side.Left ? leftTonfa : (side == Side.Right ? rightTonfa : null);

    Rigidbody GetRb(Side side)
        => side == Side.Left ? leftRb : (side == Side.Right ? rightRb : null);

    Collider GetCol(Side side)
        => side == Side.Left ? leftCol : (side == Side.Right ? rightCol : null);

    void IgnorePlayerCollisions(Collider weaponCol, bool ignore)
    {
        if (weaponCol == null || playerColliders == null) return;
        foreach (var pc in playerColliders)
            if (pc) Physics.IgnoreCollision(weaponCol, pc, ignore);
    }

    void EnableInputActions()
    {
        EnableAction(leftAction, leftActionFallback);
        EnableAction(rightAction, rightActionFallback);
        EnableAction(recallLeftAction, recallLeftActionFallback);
        EnableAction(recallRightAction, recallRightActionFallback);
        EnableAction(releaseAnchorAction, releaseAnchorActionFallback);
    }

    void DisableInputActions()
    {
        DisableAction(leftAction, leftActionFallback);
        DisableAction(rightAction, rightActionFallback);
        DisableAction(recallLeftAction, recallLeftActionFallback);
        DisableAction(recallRightAction, recallRightActionFallback);
        DisableAction(releaseAnchorAction, releaseAnchorActionFallback);
    }

    void SetupFallbackActions()
    {
        leftActionFallback = CreateButtonAction("Left Tonfa Trigger", "<XRController>{LeftHand}/triggerPressed");
        rightActionFallback = CreateButtonAction("Right Tonfa Trigger", "<XRController>{RightHand}/triggerPressed");
        recallLeftActionFallback = CreateButtonAction("Recall Left Tonfa", "<XRController>{LeftHand}/primaryButton");
        recallRightActionFallback = CreateButtonAction("Recall Right Tonfa", "<XRController>{RightHand}/primaryButton");
        releaseAnchorActionFallback = CreateButtonAction("Release Anchor", "<XRController>{RightHand}/secondaryButton");
    }

    void DisposeFallbackActions()
    {
        DisposeAction(ref leftActionFallback);
        DisposeAction(ref rightActionFallback);
        DisposeAction(ref recallLeftActionFallback);
        DisposeAction(ref recallRightActionFallback);
        DisposeAction(ref releaseAnchorActionFallback);
    }

    static InputAction CreateButtonAction(string name, string binding)
    {
        return new InputAction(name, InputActionType.Button, binding);
    }

    static void DisposeAction(ref InputAction action)
    {
        if (action == null) return;
        action.Dispose();
        action = null;
    }

    static void EnableAction(InputActionProperty actionProperty, InputAction fallbackAction)
    {
        var action = actionProperty.action ?? fallbackAction;
        if (action != null) action.Enable();
    }

    static void DisableAction(InputActionProperty actionProperty, InputAction fallbackAction)
    {
        var action = actionProperty.action ?? fallbackAction;
        if (action != null) action.Disable();
    }

    static bool WasPressedThisFrame(InputActionProperty actionProperty, InputAction fallbackAction = null)
    {
        var action = actionProperty.action ?? fallbackAction;
        return action != null && action.WasPressedThisFrame();
    }

    static bool IsPressed(InputActionProperty actionProperty, InputAction fallbackAction = null)
    {
        var action = actionProperty.action ?? fallbackAction;
        return action != null && action.IsPressed();
    }
}
