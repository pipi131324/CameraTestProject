using StarterAssets;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Third-person movement. Clips always play at 1x. BaseSpeed applies while a foot is down and
/// AirSpeed while both feet are in the air, where extra speed cannot show as sliding; both climb
/// to their maximums the longer a move key is held, and other directions scale from them. The character faces the camera while moving; standing still
/// it stays planted and plays a turn clip once the camera is far enough off. Shift dashes in the
/// held direction, starting fast and easing out.
/// </summary>
[RequireComponent(typeof(CharacterController), typeof(Animator), typeof(PlayerInput))]
[RequireComponent(typeof(StarterAssetsInputs))]
#pragma warning disable CS0618 // the Starter Assets camera prefab still uses the legacy follow component
public class PlayerMovement : MonoBehaviour
{
    [Header("Movement")]
    [Tooltip("Starting speed going forward (W) while a foot is on the ground, m/s.")]
    [Min(0f)] public float BaseSpeed = 5f;
    [Tooltip("Starting speed going forward (W) while both feet are off the ground, m/s.")]
    [Min(0f)] public float AirSpeed = 8f;
    [Tooltip("Top speed going forward (W) while a foot is on the ground, m/s.")]
    [Min(0f)] public float MaxBaseSpeed = 14f;
    [Tooltip("Top speed going forward (W) while both feet are off the ground, m/s.")]
    [Min(0f)] public float MaxAirSpeed = 21f;
    [Tooltip("Seconds of holding a move key to climb from the starting speeds to the top speeds. Letting go of every key starts over.")]
    [Min(0f)] public float AccelerationTime = 2f;
    [Tooltip("Letting go at or above this speed plays the braking animation; below it the character just stops. Measured as if running forward (W), so every direction reaches it at the same moment. m/s.")]
    [Min(0f)] public float RunStopSpeed = 12f;
    [Tooltip("Each direction's share of the two speeds above, order W, W+D, D, S+D, S, S+A, A, W+A. Ratios come from the least-foot-sliding speed measured per direction.")]
    [SerializeField] private float[] directionScale = { 1f, 1f, .778f, .6f, .6f, .6f, .778f, 1.111f };
    [Tooltip("How fast the ground speed rises and falls when keys are pressed or released, m/s per second.")]
    public float SpeedChangeRate = 30f;
    [Range(0f, 0.3f)] public float RotationSmoothTime = 0.12f;
    [Tooltip("How quickly the Animator's MoveX and MoveY follow the keys, in seconds.")]
    public float InputDampTime = 0.1f;
    public float Gravity = -15f;

    [Header("Turn In Place")]
    [Tooltip("How many degrees the camera can be from the body while standing before the character turns to it.")]
    [Range(10f, 90f)] public float TurnThreshold = 45f;

    [Header("Shift Dash")]
    [Tooltip("How far one dash travels, m. A wall can stop it sooner.")]
    [Min(0f)] public float DashDistance = 8f;
    [Tooltip("Seconds one dash takes.")]
    [Min(0.01f)] public float DashDuration = 0.3f;
    [Tooltip("How front-loaded the dash is. 1 is constant speed; higher starts faster and ends slower.")]
    [Min(1f)] public float DashEasePower = 3f;
    [Tooltip("Seconds after a dash before Shift works again.")]
    [Min(0f)] public float DashCooldown = 1f;
    [Tooltip("Camera follow damping during a dash, roughly the seconds it takes to catch up. Higher lets the character pull further ahead.")]
    public Vector3 DashCameraDamping = new Vector3(0.2f, 0.05f, 0.2f);
    [Tooltip("Seconds after a dash before the camera returns to its normal damping, so it finishes catching up first.")]
    [Min(0f)] public float DashCameraRestoreDelay = 0.15f;
    [Tooltip("How far the camera pulls ahead of the character when a backward dash starts, m. Fades out as the dash slows.")]
    [Min(0f)] public float BackDashCameraLead = 0.5f;
    [Tooltip("Forward-axis camera damping during a backward dash. 0 means the character can never close in on the camera.")]
    [Min(0f)] public float BackDashCameraDamping = 0f;
    [Tooltip("Sideways camera damping during a pure left or right dash. 0 moves the camera together with the character.")]
    [Min(0f)] public float SideDashCameraDamping = 0f;

    [Header("Shift Dash Crouch")]
    [Tooltip("Seconds to reach the full extra bend.")]
    [Min(0.001f)] public float DashCrouchInTime = 0.05f;
    [Tooltip("Seconds to straighten up after the dash.")]
    [Min(0.001f)] public float DashCrouchOutTime = 0.12f;
    [Tooltip("Extra forward bend added on top of the run while dashing, degrees per bone.")]
    public float SpineLean = 8f;
    public float ChestLean = 9f;
    public float UpperChestLean = 8f;
    public float NeckLean = 5f;

    [Header("Ground")]
    public bool Grounded = true;
    public float GroundedOffset = -0.14f;
    public float GroundedRadius = 0.28f;
    public LayerMask GroundLayers;

    [Header("Camera")]
    public GameObject CinemachineCameraTarget;
    public float TopClamp = 70f;
    public float BottomClamp = -30f;
    public float CameraAngleOverride;
    public bool LockCameraPosition;

    private const float AirborneFootHeight = 0.06f;
    private const float AirBlendTime = 0.06f;
    private const float FootFloorRecovery = 0.05f;
    private const float TerminalVelocity = 53f;
    private const float LookThreshold = 0.01f;

    private const float BlockedMoveFraction = 0.3f;
    private const float CameraLeadReleaseTime = 0.1f;
    private const int DirectionRight = 2, DirectionLeft = 6; // indices into directionScale
    private const float TurnDoneTolerance = 6f;
    private const float DirectionSettleTime = 0.12f;

    // Which pair of braking clips each direction uses, indexed like directionScale.
    private static readonly int[] StopClipRowByDirection = { 0, 2, 4, 7, 5, 6, 3, 1 };
    // Where in the run stride each braking clip picks up, as a fraction of the stride, measured.
    // Left foot then right foot, for F, FL, FR, LL, RR, B, BL, BR. Matches StopId - 1.
    private static readonly float[] StopClipStridePhase = { .83f, .35f, .63f, .23f, .56f, .02f, .29f, .88f, .31f, .81f, .71f, .25f, .19f, .71f, .21f, .96f };
    private const int TurnClipsPerSide = 4;

    // Where each turn clip ends up once blended in by the Animator, measured in Play. Left clips first, smallest first, matching TurnId - 1.
    private static readonly float[] TurnClipDegrees = { -38f, -74f, -107f, -145f, 41f, 86f, 118f, 159f };
    // Once the clip itself has gone quiet, whatever it left short is closed at this rate, degrees per second.
    private const float TurnCatchUpRate = 40f;

    private static readonly int IsMovingHash = Animator.StringToHash("IsMoving");
    private static readonly int MoveXHash = Animator.StringToHash("MoveX");
    private static readonly int MoveYHash = Animator.StringToHash("MoveY");
    private static readonly int TurnHash = Animator.StringToHash("Turn");
    private static readonly int TurnIdHash = Animator.StringToHash("TurnId");
    private static readonly int TurnTag = Animator.StringToHash("Turn");
    private static readonly int StopHash = Animator.StringToHash("Stop");
    private static readonly int StopIdHash = Animator.StringToHash("StopId");
    private static readonly int StopTag = Animator.StringToHash("Stop");

    private Animator _animator;
    private CharacterController _controller;
    private StarterAssetsInputs _input;
    private PlayerInput _playerInput;

    private Vector3 _moveDirection = Vector3.forward;
    private int _direction;
    private float _baseSpeed, _airWeight, _footFloor = float.MaxValue;
    private bool _hasInput, _sprintHeld, _turning;
    private float _turnScale = 1f, _turnTargetYaw;
    private bool _hadInput, _stopping;
    private float _runTime, _directionHeld;
    private int _heldDirection, _settledDirection;

    private float _dashElapsed, _dashProgress, _dashCooldown, _leanWeight;
    private float _cameraRestoreTimer, _cameraNormalDistance, _cameraLead, _backDashWeight;
    private Vector2 _localInput, _dashLocalDirection = Vector2.up;
    private Vector3 _dashDirection, _cameraNormalDamping;
    private Cinemachine3rdPersonFollow _cameraFollow;

    public bool IsDashing { get; private set; }
    public float LastDashDistance { get; private set; }
    public float CurrentSpeed { get; private set; }
    public bool IsAirborne { get; private set; }
    private float _verticalVelocity, _cameraYaw, _cameraPitch, _rotationVelocity;

    private bool IsCurrentDeviceMouse => _playerInput.currentControlScheme == "KeyboardMouse";

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _animator.applyRootMotion = true;
        _controller = GetComponent<CharacterController>();
        _input = GetComponent<StarterAssetsInputs>();
        _playerInput = GetComponent<PlayerInput>();
    }

    private void Start()
    {
        _cameraYaw = CinemachineCameraTarget.transform.rotation.eulerAngles.y;

        _cameraFollow = FindAnyObjectByType<Cinemachine3rdPersonFollow>();
        if (_cameraFollow != null)
        {
            _cameraNormalDamping = _cameraFollow.Damping;
            _cameraNormalDistance = _cameraFollow.CameraDistance;
        }
    }

    private void Update()
    {
        var p = transform.position;
        Grounded = Physics.CheckSphere(new Vector3(p.x, p.y - GroundedOffset, p.z), GroundedRadius, GroundLayers, QueryTriggerInteraction.Ignore);

        if (Grounded && _verticalVelocity < 0f) _verticalVelocity = -2f;
        if (_verticalVelocity < TerminalVelocity) _verticalVelocity += Gravity * Time.deltaTime;

        var move = _input.move;
        _hasInput = move.sqrMagnitude > .01f;
        var local = _hasInput ? move.normalized : Vector2.zero;
        if (_hasInput)
        {
            _direction = ((Mathf.RoundToInt(Mathf.Atan2(local.x, local.y) * Mathf.Rad2Deg / 45f) % 8) + 8) % 8;
            _moveDirection = Quaternion.Euler(0f, _cameraYaw, 0f) * new Vector3(local.x, 0f, local.y);
        }
        _localInput = local;

        bool released = _hadInput && !_hasInput;
        _hadInput = _hasInput;
        _runTime = _hasInput ? _runTime + Time.deltaTime : 0f;
        UpdateSettledDirection();

        UpdateAirWeight();
        UpdateDash();

        // The legs keep running through a dash, even one started from a standstill.
        var animDirection = IsDashing ? _dashLocalDirection : local;
        _animator.SetBool(IsMovingHash, _hasInput || IsDashing);
        _animator.SetFloat(MoveXHash, animDirection.x, InputDampTime, Time.deltaTime);
        _animator.SetFloat(MoveYHash, animDirection.y, InputDampTime, Time.deltaTime);

        if (!_hasInput && !IsDashing && Grounded)
        {
            // The Animator only takes the stop trigger after this Update, so a stop started here is checked from next frame on.
            if (_stopping) UpdateStop();
            if (released) TryStartStop();
            if (!_stopping) UpdateTurnInPlace();
        }
        else
        {
            CancelStop();
            CancelTurn();
            float yaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, _cameraYaw, ref _rotationVelocity, RotationSmoothTime);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
    }

    // The two keys of a diagonal never come up on the same frame, so a direction only counts once it has been held briefly.
    private void UpdateSettledDirection()
    {
        if (!_hasInput) return;
        if (_direction != _heldDirection)
        {
            _heldDirection = _direction;
            _directionHeld = 0f;
        }
        _directionHeld += Time.deltaTime;
        if (_directionHeld >= DirectionSettleTime) _settledDirection = _direction;
    }

    private bool IsAnimatorHeadingTo(int tag)
    {
        var layer = _animator.IsInTransition(0) ? _animator.GetNextAnimatorStateInfo(0) : _animator.GetCurrentAnimatorStateInfo(0);
        return layer.tagHash == tag;
    }

    private void TryStartStop()
    {
        // Compared as if running forward, otherwise the slower directions could never reach the threshold.
        if (_baseSpeed / directionScale[_settledDirection] < RunStopSpeed) return;

        int row = StopClipRowByDirection[_settledDirection];

        float stride = Mathf.Repeat(_animator.GetCurrentAnimatorStateInfo(0).normalizedTime, 1f);
        bool useLeftFoot = StrideDistance(stride, StopClipStridePhase[row * 2]) <= StrideDistance(stride, StopClipStridePhase[row * 2 + 1]);
        int index = row * 2 + (useLeftFoot ? 0 : 1);

        _stopping = true;
        _baseSpeed = 0f;
        _animator.SetInteger(StopIdHash, index + 1);
        _animator.SetTrigger(StopHash);
    }

    private static float StrideDistance(float a, float b)
    {
        float d = Mathf.Abs(a - b);
        return Mathf.Min(d, 1f - d);
    }

    private void UpdateStop()
    {
        _rotationVelocity = 0f;
        if (!IsAnimatorHeadingTo(StopTag)) _stopping = false;
    }

    private void CancelStop()
    {
        if (!_stopping) return;
        _stopping = false;
        _animator.ResetTrigger(StopHash);
    }

    // The braking clip moves the character at its own speed. Its root rotation is left out so the
    // character still faces the camera once it has stopped.
    private void ApplyStopMotion(float dt)
    {
        var planar = _animator.deltaPosition;
        planar.y = 0f;
        CurrentSpeed = dt > 0f ? planar.magnitude / dt : 0f;
        _controller.Move(planar + new Vector3(0f, _verticalVelocity, 0f) * dt);
    }

    private float RunBlend => AccelerationTime > 0f ? Mathf.Clamp01(_runTime / AccelerationTime) : 1f;
    private float RunGroundSpeed => Mathf.Lerp(BaseSpeed, MaxBaseSpeed, RunBlend);
    private float RunAirSpeed => Mathf.Lerp(AirSpeed, MaxAirSpeed, RunBlend);

    // Standing still the body stays put until the camera is TurnThreshold away, then a turn clip
    // walks it round. Once the body has arrived the clip's settling steps can be cut for the next turn.
    private void UpdateTurnInPlace()
    {
        _rotationVelocity = 0f;
        float facing = transform.eulerAngles.y;
        float cameraOffset = Mathf.DeltaAngle(facing, _cameraYaw);

        if (_turning)
        {
            if (!IsAnimatorHeadingTo(TurnTag)) _turning = false;
        }

        bool bodyArrived = !_turning || Mathf.Abs(Mathf.DeltaAngle(facing, _turnTargetYaw)) <= TurnDoneTolerance;
        if (bodyArrived && Mathf.Abs(cameraOffset) >= TurnThreshold) StartTurn(cameraOffset);
    }

    private void StartTurn(float degrees)
    {
        int first = degrees < 0f ? 0 : TurnClipsPerSide;
        int index = first;
        for (int i = first + 1; i < first + TurnClipsPerSide; i++)
        {
            if (Mathf.Abs(degrees - TurnClipDegrees[i]) < Mathf.Abs(degrees - TurnClipDegrees[index])) index = i;
        }
        _turning = true;
        _turnScale = degrees / TurnClipDegrees[index];
        _turnTargetYaw = _cameraYaw;
        _animator.SetInteger(TurnIdHash, index + 1);
        _animator.SetTrigger(TurnHash);
    }

    private void CancelTurn()
    {
        if (!_turning) return;
        _turning = false;
        _animator.ResetTrigger(TurnHash);
    }

    // Follows the clip's own rotation, overshoot and swing-back included, because the feet are
    // animated against it; it is only stretched slightly to end on the camera direction.
    private void ApplyTurnMotion(float dt)
    {
        float clipStep = Mathf.DeltaAngle(0f, _animator.deltaRotation.eulerAngles.y) * _turnScale;
        float remaining = Mathf.DeltaAngle(transform.eulerAngles.y, _turnTargetYaw);
        float quietStep = TurnCatchUpRate * dt;
        float step = Mathf.Abs(clipStep) >= quietStep ? clipStep : Mathf.Sign(remaining) * Mathf.Min(quietStep, Mathf.Abs(remaining));
        transform.Rotate(0f, step, 0f);

        var planar = _animator.deltaPosition;
        planar.y = 0f;
        CurrentSpeed = 0f;
        _controller.Move(planar + new Vector3(0f, _verticalVelocity, 0f) * dt);
    }

    // Root motion stays on so the Animator takes the clips' travel out of the pose; leaving its
    // result unused keeps the body on the player while this script does the moving.
    private void OnAnimatorMove()
    {
        float dt = Time.deltaTime;
        if (IsDashing)
        {
            MoveDash(dt);
            return;
        }
        if (_turning)
        {
            ApplyTurnMotion(dt);
            return;
        }
        if (_stopping)
        {
            ApplyStopMotion(dt);
            return;
        }

        float groundSpeed = RunGroundSpeed;
        float target = _hasInput ? groundSpeed * directionScale[_direction] : 0f;
        _baseSpeed = Mathf.MoveTowards(_baseSpeed, target, SpeedChangeRate * dt);
        float airMultiplier = groundSpeed > .01f ? RunAirSpeed / groundSpeed : 1f;
        float speed = _baseSpeed * Mathf.Lerp(1f, airMultiplier, _airWeight);
        CurrentSpeed = speed;
        _controller.Move(_moveDirection * (speed * dt) + new Vector3(0f, _verticalVelocity, 0f) * dt);
    }

    private void UpdateDash()
    {
        float dt = Time.deltaTime;
        if (_dashCooldown > 0f) _dashCooldown -= dt;

        bool pressed = _input.sprint && !_sprintHeld;
        _sprintHeld = _input.sprint;
        if (pressed && !IsDashing && Grounded && _dashCooldown <= 0f) BeginDash();

        if (_cameraRestoreTimer > 0f)
        {
            _cameraRestoreTimer -= dt;
            if (_cameraRestoreTimer <= 0f && _cameraFollow != null) _cameraFollow.Damping = _cameraNormalDamping;
        }

        float leanTarget = IsDashing ? 1f : 0f;
        _leanWeight = Mathf.MoveTowards(_leanWeight, leanTarget, dt / (IsDashing ? DashCrouchInTime : DashCrouchOutTime));
    }

    private void BeginDash()
    {
        IsDashing = true;
        _dashElapsed = _dashProgress = 0f;
        _dashDirection = _hasInput ? _moveDirection : Quaternion.Euler(0f, _cameraYaw, 0f) * Vector3.forward;
        _dashLocalDirection = _hasInput ? _localInput : Vector2.up;
        _cameraRestoreTimer = 0f;
        _backDashWeight = Mathf.Clamp01(-_dashLocalDirection.y);
        if (_cameraFollow != null)
        {
            var damping = DashCameraDamping;
            damping.z = Mathf.Lerp(damping.z, BackDashCameraDamping, _backDashWeight);
            if (_hasInput && (_direction == DirectionRight || _direction == DirectionLeft)) damping.x = SideDashCameraDamping;
            _cameraFollow.Damping = damping;
        }
    }

    // Eases out: the distance covered follows 1 - (1 - t)^power, so most of it happens early.
    private void MoveDash(float dt)
    {
        _dashElapsed += dt;
        float t = Mathf.Clamp01(_dashElapsed / DashDuration);
        float progress = 1f - Mathf.Pow(1f - t, DashEasePower);
        float step = (progress - _dashProgress) * DashDistance;
        _dashProgress = progress;

        var before = transform.position;
        var flags = _controller.Move(_dashDirection * step + new Vector3(0f, _verticalVelocity, 0f) * dt);
        CurrentSpeed = dt > 0f ? step / dt : 0f;

        var moved = transform.position - before;
        moved.y = 0f;
        bool blocked = (flags & CollisionFlags.Sides) != 0 && moved.magnitude < step * BlockedMoveFraction;
        if (t >= 1f || blocked) EndDash();
    }

    private void EndDash()
    {
        IsDashing = false;
        LastDashDistance = _dashProgress * DashDistance;
        _dashCooldown = DashCooldown;
        _cameraRestoreTimer = DashCameraRestoreDelay;
        // pick the run straight back up instead of accelerating from nothing
        _baseSpeed = _hasInput ? RunGroundSpeed * directionScale[_direction] : 0f;
    }

    // A backward dash runs at the camera, so the camera backs off first. The lead follows the
    // dash's own speed: largest on the first frame, gone by the time the dash ends.
    private void UpdateCameraLead()
    {
        if (_cameraFollow == null) return;

        float lead;
        if (IsDashing)
        {
            float t = Mathf.Clamp01(_dashElapsed / DashDuration);
            lead = BackDashCameraLead * _backDashWeight * Mathf.Pow(1f - t, DashEasePower - 1f);
        }
        else
        {
            // only a dash stopped early by a wall still has lead left here
            lead = Mathf.MoveTowards(_cameraLead, 0f, BackDashCameraLead / CameraLeadReleaseTime * Time.deltaTime);
        }

        if (Mathf.Approximately(lead, _cameraLead)) return;
        _cameraLead = lead;
        _cameraFollow.CameraDistance = _cameraNormalDistance + lead;
    }

    // Added after the Animator has posed the body, so the bend stacks on top of the run.
    private void ApplyDashLean()
    {
        if (_leanWeight <= 0f) return;
        Lean(HumanBodyBones.Spine, SpineLean);
        Lean(HumanBodyBones.Chest, ChestLean);
        Lean(HumanBodyBones.UpperChest, UpperChestLean);
        Lean(HumanBodyBones.Neck, NeckLean);
    }

    private void Lean(HumanBodyBones bone, float degrees)
    {
        var t = _animator.GetBoneTransform(bone);
        if (t != null) t.rotation = Quaternion.AngleAxis(degrees * _leanWeight, transform.right) * t.rotation;
    }

    // Both feet off the ground means nothing can visibly slide, so that is when the character surges.
    private void UpdateAirWeight()
    {
        var left = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        var right = _animator.GetBoneTransform(HumanBodyBones.RightFoot);
        if (left == null || right == null) return;

        float baseY = transform.position.y;
        float l = left.position.y - baseY, r = right.position.y - baseY;
        // The floor height differs per model and footwear, so track the lowest recent foot height.
        _footFloor = Mathf.Min(_footFloor + FootFloorRecovery * Time.deltaTime, Mathf.Min(l, r));

        bool airborne = _hasInput && !IsDashing && l > _footFloor + AirborneFootHeight && r > _footFloor + AirborneFootHeight;
        IsAirborne = airborne;
        _airWeight = Mathf.MoveTowards(_airWeight, airborne ? 1f : 0f, Time.deltaTime / AirBlendTime);
    }

    private void LateUpdate()
    {
        ApplyDashLean();
        UpdateCameraLead();

        if (_input.look.sqrMagnitude >= LookThreshold && !LockCameraPosition)
        {
            float multiplier = IsCurrentDeviceMouse ? 1f : Time.deltaTime;
            _cameraYaw += _input.look.x * multiplier;
            _cameraPitch += _input.look.y * multiplier;
        }
        _cameraYaw = ClampAngle(_cameraYaw, float.MinValue, float.MaxValue);
        _cameraPitch = ClampAngle(_cameraPitch, BottomClamp, TopClamp);
        CinemachineCameraTarget.transform.rotation = Quaternion.Euler(_cameraPitch + CameraAngleOverride, _cameraYaw, 0f);
    }

    private static float ClampAngle(float a, float min, float max)
    {
        if (a < -360f) a += 360f;
        if (a > 360f) a -= 360f;
        return Mathf.Clamp(a, min, max);
    }
}
