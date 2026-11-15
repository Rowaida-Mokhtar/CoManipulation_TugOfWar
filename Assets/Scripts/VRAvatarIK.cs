using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Drives a humanoid character (e.g. Hodaart Character 01/02, Mixamo rig) from
/// three tracked targets: head, left hand, right hand.
///  - Scales the character to the player's real height (so arms can reach the controllers).
///  - Body follows the head position and (smoothed) yaw.
///  - Legs play Idle / Walking depending on how fast the body moves (forwards or backwards).
///  - Head bone follows the head rotation.
///  - Each arm uses two-bone IK so the hand reaches its controller.
/// No extra packages needed.
/// </summary>
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(Animator))]
public class VRAvatarIK : MonoBehaviour
{
    [Header("Targets (set by NetworkAvatarPlayer)")]
    public Transform headTarget;
    public Transform leftHandTarget;
    public Transform rightHandTarget;

    [Tooltip("World height of the floor this player stands on (set by NetworkAvatarPlayer).")]
    public float floorHeight;

    [Header("First person")]
    [Tooltip("Hide the head (for the local player, so it doesn't block the camera).")]
    public bool hideHead = false;

    [Header("Height calibration")]
    [Tooltip("Scale the character so its eye height matches the player's real eye height.")]
    public bool matchPlayerHeight = true;
    [Tooltip("Seconds after spawning during which the player's height is measured.")]
    public float calibrationTime = 3f;
    public float minScaleFactor = 0.75f;
    public float maxScaleFactor = 1.4f;

    [Header("Body")]
    [Tooltip("Head bone position relative to the headset (eyes), in headset space.")]
    public Vector3 headBoneOffsetFromEyes = new Vector3(0f, -0.10f, -0.08f);
    [Tooltip("How far the body sits behind the head, in metres.")]
    public float bodyBackOffset = 0.05f;
    [Tooltip("How quickly the body turns to follow the head.")]
    public float bodyTurnSpeed = 5f;

    [Header("Loose body (feet stay planted, spine bends)")]
    [Tooltip("How quickly the feet catch up with the head. Lower = more lean.")]
    public float feetFollowSpeed = 3.5f;
    [Tooltip("Furthest the head may get from the feet before the feet step after it (metres).")]
    public float maxLean = 0.35f;
    [Tooltip("Most the spine may bend toward the head (degrees).")]
    public float maxSpineBend = 35f;

    [Header("Legs (Idle / Walking)")]
    public AnimationClip idleClip;
    public AnimationClip walkClip;
    [Tooltip("Body speed (m/s) below which the legs stay idle.")]
    public float walkStartSpeed = 0.12f;
    [Tooltip("Body speed (m/s) at which the walk animation plays at normal speed.")]
    public float walkClipSpeed = 1.2f;

    [Header("Jumping (head rises fast = jump)")]
    [Tooltip("Lift the body off the grass when the player really jumps. Otherwise the feet stay on the floor " +
             "(standing on tiptoe or standing taller does not make the avatar float).")]
    public bool detectJumps = true;
    [Tooltip("Upward head speed (m/s) that counts as jumping off the ground.")]
    public float jumpUpSpeed = 0.8f;
    [Tooltip("How far (m) the head must be above standing height to count as off the ground.")]
    public float jumpMinHeight = 0.03f;
    [Tooltip("How much the hips bend while in the air, at full height (degrees).")]
    public float jumpHipBend = 45f;
    [Tooltip("How much the knees bend while in the air, at full height (degrees).")]
    public float jumpKneeBend = 80f;
    [Tooltip("Lift (m) at which the legs are fully tucked.")]
    public float jumpFullTuckHeight = 0.25f;

    [Header("Hands")]
    [Tooltip("Distance from the controller (palm) back to the wrist, along the hand.")]
    public float gripToWrist = 0.08f;
    [Tooltip("Extra per-hand rotation (degrees), added on top of the shared calibration below.")]
    public Vector3 leftHandExtraRotation = Vector3.zero;
    public Vector3 rightHandExtraRotation = Vector3.zero;
    [Tooltip("If the arm is slightly too short, move the hand up to this many metres to reach the controller.")]
    public float maxHandStretch = 0.08f;

    [Header("Elbows")]
    [Tooltip("Where elbows point, relative to the shoulder in body space (x is mirrored for the left arm).")]
    public Vector3 elbowHint = new Vector3(0.3f, -0.5f, -0.3f);

    private Animator animator;
    private Transform hips, spine, chest, upperChest;
    private Vector3 plantedRoot;
    private bool hasPlantedRoot;
    private Transform head, leftUpper, leftLower, leftHand, rightUpper, rightLower, rightHand;
    private Transform leftThigh, leftShin, rightThigh, rightShin;

    // Jump detection
    private float lastHeadY;
    private bool hasLastHeadY;
    private float headVerticalSpeed;   // smoothed, m/s
    private bool jumping;
    private float hoverTime;           // time "in the air" without moving up or down
    private float bodyLift;            // current height of the feet above the floor

    private Quaternion leftUpperRest, leftLowerRest, rightUpperRest, rightLowerRest;
    private Quaternion leftHandOffset, rightHandOffset, headOffset;
    private Vector3 leftFingersLocal = Vector3.up, rightFingersLocal = Vector3.up;

    // Finger bones per hand, their resting rotation and the axis they curl around.
    private Transform[] leftFingers, rightFingers;
    private Quaternion[] leftFingerRest, rightFingerRest;
    private Vector3[] leftCurlAxis, rightCurlAxis;
    private float[] fingerCurlDegrees;

    // ---- Shared hand calibration (same for both hands), saved on this PC ----
    // Keys while playing: 1 / 2 / 3 = turn hands 90 degrees around X / Y / Z, 0 = reset.
    // v2 key so every PC starts from the tested default below.
    private const string CalibrationKey = "VRAvatarIK.HandCalibration.v2";
    // Found in VR testing (2026-11-03, Quest controllers): both hands line up with the controllers.
    private static readonly Vector3 DefaultCalibration = new Vector3(270f, 270f, 90f);
    public static Vector3 HandCalibration;
    private static bool calibrationLoaded;
    private static int calibrationFrame = -1;

    private static Vector3 LoadCalibration()
    {
        if (!PlayerPrefs.HasKey(CalibrationKey))
            return DefaultCalibration;
        string s = PlayerPrefs.GetString(CalibrationKey);
        string[] p = s.Split(',');
        if (p.Length == 3 &&
            float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) &&
            float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
            return new Vector3(x, y, z);
        return DefaultCalibration;
    }

    /// <summary>Same calibration for both hands (confirmed in VR testing).</summary>
    private static Quaternion CalibrationFor(bool leftHand) => Quaternion.Euler(HandCalibration);

    private static void SaveCalibration()
    {
        PlayerPrefs.SetString(CalibrationKey, string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0},{1},{2}", HandCalibration.x, HandCalibration.y, HandCalibration.z));
        PlayerPrefs.Save();
        Debug.Log($"Hand calibration saved: X={HandCalibration.x} Y={HandCalibration.y} Z={HandCalibration.z}");
    }

    // ---- Finger grip (curl the fingers around the controller), saved on this PC ----
    private const string GripKey = "VRAvatarIK.FingerGrip";
    public static float FingerGrip = 0.7f;      // 0 = open hand, 1 = fist
    public static float GripSign = -1f;         // flips the curl direction if fingers bend backwards

    private static void SaveGrip()
    {
        PlayerPrefs.SetFloat(GripKey, FingerGrip);
        PlayerPrefs.SetFloat(GripKey + ".Sign", GripSign);
        PlayerPrefs.Save();
        Debug.Log($"Finger grip saved: {FingerGrip:0.0} (direction {GripSign})");
    }

    private static void HandleCalibrationKeys()
    {
        if (calibrationFrame == Time.frameCount)
            return; // only once per frame, even with several avatars
        calibrationFrame = Time.frameCount;

        bool changed = false;
        if (Input.GetKeyDown(KeyCode.Alpha1)) { HandCalibration.x = (HandCalibration.x + 90f) % 360f; changed = true; }
        if (Input.GetKeyDown(KeyCode.Alpha2)) { HandCalibration.y = (HandCalibration.y + 90f) % 360f; changed = true; }
        if (Input.GetKeyDown(KeyCode.Alpha3)) { HandCalibration.z = (HandCalibration.z + 90f) % 360f; changed = true; }
        if (Input.GetKeyDown(KeyCode.Alpha0)) { HandCalibration = DefaultCalibration; changed = true; }
        if (changed) SaveCalibration();

        bool gripChanged = false;
        if (Input.GetKeyDown(KeyCode.Alpha4)) { FingerGrip = Mathf.Clamp01(FingerGrip - 0.1f); gripChanged = true; }
        if (Input.GetKeyDown(KeyCode.Alpha5)) { FingerGrip = Mathf.Clamp01(FingerGrip + 0.1f); gripChanged = true; }
        if (Input.GetKeyDown(KeyCode.Alpha6)) { GripSign = -GripSign; gripChanged = true; }
        if (gripChanged) SaveGrip();
    }
    private float restHeadHeight;      // at the original scale
    private Vector3 originalScale;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { calibrationLoaded = false; calibrationFrame = -1; }
    private float bodyYaw;
    private bool ready;

    private float spawnTime;
    private float maxEyeHeight;

    // Legs
    private PlayableGraph graph;
    private AnimationMixerPlayable mixer;
    private AnimationClipPlayable walkPlayable;
    private Vector3 lastRootPos;
    private float smoothedForwardSpeed;
    private bool hasLastRootPos;

    private void Awake()
    {
        // PlayerPrefs can't be read from a static initializer, so load here once.
        if (!calibrationLoaded)
        {
            HandCalibration = LoadCalibration();
            FingerGrip = PlayerPrefs.GetFloat(GripKey, 0.7f);
            GripSign = PlayerPrefs.GetFloat(GripKey + ".Sign", -1f);
            calibrationLoaded = true;
        }

        animator = GetComponent<Animator>();
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        head = animator.GetBoneTransform(HumanBodyBones.Head);
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        spine = animator.GetBoneTransform(HumanBodyBones.Spine);
        chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        upperChest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
        leftUpper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        leftLower = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        rightUpper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        rightLower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        leftThigh = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        leftShin = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        rightThigh = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        rightShin = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);

        if (head == null || leftUpper == null || leftLower == null || leftHand == null ||
            rightUpper == null || rightLower == null || rightHand == null)
        {
            Debug.LogError($"VRAvatarIK on {name}: character is not a humanoid rig.", this);
            enabled = false;
            return;
        }

        Quaternion root = transform.rotation;

        leftUpperRest = leftUpper.localRotation;
        leftLowerRest = leftLower.localRotation;
        rightUpperRest = rightUpper.localRotation;
        rightLowerRest = rightLower.localRotation;

        leftHandOffset = ComputeHandOffset(leftHand,
            animator.GetBoneTransform(HumanBodyBones.LeftIndexProximal),
            animator.GetBoneTransform(HumanBodyBones.LeftThumbProximal), out leftFingersLocal);
        rightHandOffset = ComputeHandOffset(rightHand,
            animator.GetBoneTransform(HumanBodyBones.RightIndexProximal),
            animator.GetBoneTransform(HumanBodyBones.RightThumbProximal), out rightFingersLocal);

        headOffset = Quaternion.Inverse(root) * head.rotation;

        originalScale = transform.localScale;
        restHeadHeight = head.position.y - transform.position.y;
        bodyYaw = transform.eulerAngles.y;
        SetUpFingerMuscles();
        ready = true;
    }

    private void Start()
    {
        spawnTime = Time.time;
        SetUpLegAnimation();
    }

    private void OnDestroy()
    {
        if (graph.IsValid())
            graph.Destroy();
    }

    // ---------------- Legs ----------------

    private void SetUpLegAnimation()
    {
        if (idleClip == null && walkClip == null)
            return; // keep whatever the character's own Animator Controller does

        // Our clips replace the character's own Animator Controller.
        animator.runtimeAnimatorController = null;

        graph = PlayableGraph.Create($"{name} Legs");
        graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        mixer = AnimationMixerPlayable.Create(graph, 3); // 0 idle, 1 walk, 2 emote (dance / jump / wave)
        AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "Legs", animator);
        output.SetSourcePlayable(mixer);

        if (idleClip != null)
        {
            AnimationClipPlayable idle = AnimationClipPlayable.Create(graph, idleClip);
            idle.SetApplyFootIK(true);
            graph.Connect(idle, 0, mixer, 0);
        }

        if (walkClip != null)
        {
            walkPlayable = AnimationClipPlayable.Create(graph, walkClip);
            walkPlayable.SetApplyFootIK(true);
            graph.Connect(walkPlayable, 0, mixer, 1);
        }

        mixer.SetInputWeight(0, 1f);
        mixer.SetInputWeight(1, 0f);
        mixer.SetInputWeight(2, 0f);
        graph.Play();
    }

    private void UpdateLegs(Vector3 rootPos, Quaternion bodyRotation)
    {
        if (!hasLastRootPos)
        {
            lastRootPos = rootPos;
            hasLastRootPos = true;
            return;
        }

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        Vector3 velocity = (rootPos - lastRootPos) / dt;
        velocity.y = 0f;
        lastRootPos = rootPos;

        // Forward/backward speed relative to where the body faces.
        float forwardSpeed = Vector3.Dot(velocity, bodyRotation * Vector3.forward);
        smoothedForwardSpeed = Mathf.Lerp(smoothedForwardSpeed, forwardSpeed, 1f - Mathf.Exp(-8f * dt));

        if (!graph.IsValid())
            return;

        if (EmoteActive)
        {
            mixer.SetInputWeight(0, 0f);
            mixer.SetInputWeight(1, 0f);
            mixer.SetInputWeight(2, 1f);
            return;
        }
        mixer.SetInputWeight(2, 0f);

        if (!walkPlayable.IsValid())
            return;

        float speed = Mathf.Abs(smoothedForwardSpeed);
        float walkWeight = Mathf.Clamp01((speed - walkStartSpeed) / walkStartSpeed);

        mixer.SetInputWeight(0, 1f - walkWeight);
        mixer.SetInputWeight(1, walkWeight);

        // Negative speed plays the walk backwards (e.g. the winner stepping back).
        float playback = Mathf.Clamp(smoothedForwardSpeed / walkClipSpeed, -1.5f, 1.5f);
        walkPlayable.SetSpeed(walkWeight > 0f ? playback : 0f);
    }

    // ---------------- Emotes (dance, wave) and falling into the pond ----------------
    // Only ever used on OTHER players' avatars: your own body always follows your real movement.

    private AnimationClipPlayable emotePlayable;
    private float emoteEndTime = -1f;
    private Quaternion emoteRotation;
    private Renderer[] renderers;

    private bool falling;
    private float fallStartTime;
    private Vector3 fallStart, fallEnd;

    [Header("Fall into the pond (seen by the other player)")]
    public float fallDuration = 1.8f;
    public float fallForward = 1.0f;
    public float fallDepth = 1.8f;

    public bool EmoteActive => falling || Time.time < emoteEndTime;

    /// <summary>The character's head bone.</summary>
    public Transform HeadBone => head;
    /// <summary>Head bone rotation relative to the character root in the rest pose.</summary>
    public Quaternion HeadRestOffset => headOffset;

    /// <summary>The mirror shows your own head while it draws (it is hidden from your own eyes).</summary>
    public void ShowOwnHead(bool show)
    {
        if (head != null && hideHead)
            head.localScale = show ? Vector3.one : Vector3.one * 0.0001f;
    }

    /// <summary>While falling, this player's end of the rope follows the sinking hands (set by the owner script).</summary>
    [HideInInspector] public Transform ropeEnd;

    /// <summary>Eye height of this character standing, in metres (at its current size).</summary>
    public float StandingEyeHeight =>
        (restHeadHeight - headBoneOffsetFromEyes.y) * transform.localScale.y / Mathf.Max(originalScale.y, 0.0001f);

    /// <summary>Play a full-body animation (e.g. a dance) for a few seconds, then go back to tracking.</summary>
    public void PlayEmote(AnimationClip clip, float seconds)
    {
        if (clip == null || !StartEmoteClip(clip))
            return;
        emoteEndTime = Time.time + seconds;
        emoteRotation = transform.rotation;
    }

    /// <summary>Tip forward and sink into the pond, then stay hidden until EndFall().</summary>
    public void PlayFall(AnimationClip clip)
    {
        if (clip != null)
            StartEmoteClip(clip);

        falling = true;
        fallStartTime = Time.time;
        fallStart = transform.position;
        fallEnd = fallStart + transform.rotation * Vector3.forward * fallForward + Vector3.down * fallDepth;
        emoteRotation = transform.rotation;
        emoteEndTime = float.MaxValue;
    }

    /// <summary>Come back out of the pond: visible again and following the player.</summary>
    public void EndFall()
    {
        falling = false;
        emoteEndTime = -1f;
        SetVisible(true);
    }

    private bool StartEmoteClip(AnimationClip clip)
    {
        if (!graph.IsValid())
            return false;

        if (emotePlayable.IsValid())
        {
            graph.Disconnect(mixer, 2);
            emotePlayable.Destroy();
        }

        emotePlayable = AnimationClipPlayable.Create(graph, clip);
        emotePlayable.SetApplyFootIK(true);
        graph.Connect(emotePlayable, 0, mixer, 2);
        emotePlayable.SetTime(0);
        return true;
    }

    private void SetVisible(bool visible)
    {
        renderers = GetComponentsInChildren<Renderer>(true); // includes a hat added later
        foreach (Renderer r in renderers)
            if (r != null) r.enabled = visible;
    }

    private void UpdateFall()
    {
        float k = Mathf.Clamp01((Time.time - fallStartTime) / Mathf.Max(fallDuration, 0.01f));
        FallPose(k, fallStart, emoteRotation, fallForward, fallDepth, out Vector3 pos, out Quaternion rot);
        transform.SetPositionAndRotation(pos, rot);
        UpdateLegs(transform.position, emoteRotation);
        if (ropeEnd != null)
            ropeEnd.position = (leftHand.position + rightHand.position) * 0.5f; // rope goes into the water too
        if (k >= 1f)
            SetVisible(false); // under water
    }

    /// <summary>
    /// A stumble into the pond: a small hop forward while tipping over (first 35%),
    /// then sinking with a slowing bob (rest). k goes 0..1.
    /// </summary>
    public static void FallPose(float k, Vector3 start, Quaternion startRot, float forward, float depth,
                                out Vector3 pos, out Quaternion rot)
    {
        Vector3 fwd = startRot * Vector3.forward; fwd.y = 0f; fwd.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, fwd);
        const float hop = 0.35f;

        if (k < hop)
        {
            float a = k / hop;
            pos = start + fwd * (forward * a) + Vector3.up * (0.3f * Mathf.Sin(Mathf.PI * a));
            rot = Quaternion.AngleAxis(70f * a * a, side) * startRot;
        }
        else
        {
            float b = (k - hop) / (1f - hop);
            float ease = 1f - (1f - b) * (1f - b);
            float bob = Mathf.Sin(b * Mathf.PI * 3f) * 0.08f * (1f - b);
            pos = start + fwd * (forward + 0.2f * b) + Vector3.down * (depth * ease) + Vector3.up * bob;
            rot = Quaternion.AngleAxis(70f + 20f * b, side) * startRot;
        }
    }

    // ---------------- Main update ----------------

    private void LateUpdate()
    {
        if (!ready || headTarget == null)
            return;

        if (hideHead) // only the local player's own avatar listens to the keys
            HandleCalibrationKeys();

        UpdateHeadVerticalSpeed();
        UpdateHeightCalibration();

        if (falling)
        {
            UpdateFall();
            return;
        }

        // ---- Body: stand under the head, turn toward where the head looks ----
        Vector3 headFlatForward = headTarget.forward;
        headFlatForward.y = 0f;
        if (headFlatForward.sqrMagnitude > 0.0001f)
        {
            float targetYaw = Quaternion.LookRotation(headFlatForward).eulerAngles.y;
            bodyYaw = Mathf.LerpAngle(bodyYaw, targetYaw, 1f - Mathf.Exp(-bodyTurnSpeed * Time.deltaTime));
        }

        Quaternion bodyRotation = Quaternion.Euler(0f, bodyYaw, 0f);
        Vector3 headBonePos = headTarget.TransformPoint(headBoneOffsetFromEyes);

        float scaleFactor = transform.localScale.y / Mathf.Max(originalScale.y, 0.0001f);
        Vector3 rootPos = headBonePos - bodyRotation * Vector3.forward * bodyBackOffset;
        rootPos.y = headBonePos.y - restHeadHeight * scaleFactor;

        if (EmoteActive)
        {
            // Full-body animation: stay where the player stands, keep facing the same way.
            transform.SetPositionAndRotation(rootPos, emoteRotation);
            UpdateLegs(rootPos, emoteRotation);
            head.localScale = hideHead ? Vector3.one * 0.0001f : Vector3.one;
            return;
        }

        // Feet stay planted and follow the head with a lag, so leaning moves the
        // upper body (the spine bends) instead of sliding the whole body like a block.
        if (!hasPlantedRoot) { plantedRoot = rootPos; hasPlantedRoot = true; }
        float follow = 1f - Mathf.Exp(-feetFollowSpeed * Time.deltaTime);
        plantedRoot.x = Mathf.Lerp(plantedRoot.x, rootPos.x, follow);
        plantedRoot.z = Mathf.Lerp(plantedRoot.z, rootPos.z, follow);
        Vector3 lean = rootPos - plantedRoot; lean.y = 0f;
        float maxLeanScaled = maxLean * scaleFactor;
        if (lean.magnitude > maxLeanScaled)
            plantedRoot = rootPos - lean.normalized * maxLeanScaled;
        // Never sink below the floor (crouching bends the spine). Only a real jump lifts the body.
        if (floorHeight == 0f)
            plantedRoot.y = rootPos.y; // floor unknown: follow the head as before
        else if (!detectJumps)
            plantedRoot.y = Mathf.Max(floorHeight, rootPos.y);
        else
            plantedRoot.y = floorHeight + UpdateJumpLift(rootPos.y - floorHeight, scaleFactor);

        transform.SetPositionAndRotation(plantedRoot, bodyRotation);
        UpdateLegs(plantedRoot, bodyRotation);
        ApplyJumpTuck(bodyRotation, scaleFactor);
        ApplySpineLean(headBonePos);

        // ---- Fingers wrapped around the controller ----
        ApplyFingerGrip();

        // ---- Arms ----
        if (leftHandTarget != null)
            SolveArm(leftUpper, leftLower, leftHand, leftUpperRest, leftLowerRest,
                     leftHandTarget, true, leftHandExtraRotation, leftHandOffset, leftFingersLocal,
                     new Vector3(-elbowHint.x, elbowHint.y, elbowHint.z), bodyRotation);

        if (rightHandTarget != null)
            SolveArm(rightUpper, rightLower, rightHand, rightUpperRest, rightLowerRest,
                     rightHandTarget, false, rightHandExtraRotation, rightHandOffset, rightFingersLocal,
                     elbowHint, bodyRotation);

        // ---- Head ----
        head.rotation = headTarget.rotation * headOffset;
        head.localScale = hideHead ? Vector3.one * 0.0001f : Vector3.one;
    }

    // ---------------- Jumping ----------------

    private void UpdateHeadVerticalSpeed()
    {
        float y = headTarget.position.y;
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        if (!hasLastHeadY) { lastHeadY = y; hasLastHeadY = true; return; }

        float raw = (y - lastHeadY) / dt;
        lastHeadY = y;

        // Big jumps (teleport, respawn, being moved with the match) are not head movement.
        if (Mathf.Abs(raw) > 6f) { headVerticalSpeed = 0f; return; }
        headVerticalSpeed = Mathf.Lerp(headVerticalSpeed, raw, 1f - Mathf.Exp(-20f * dt));
    }

    /// <summary>
    /// Returns how high the feet should be above the floor.
    /// headLift = how far the body would have to rise for the head bone to reach the real head.
    /// </summary>
    private float UpdateJumpLift(float headLift, float scaleFactor)
    {
        float dt = Time.deltaTime;
        float minHeight = jumpMinHeight * scaleFactor;

        if (!jumping)
        {
            // Take-off: the head shoots up past standing height.
            if (headVerticalSpeed > jumpUpSpeed && headLift > minHeight)
            {
                jumping = true;
                hoverTime = 0f;
            }
        }
        else
        {
            // A real jump never hangs still in the air; standing on tiptoe does.
            hoverTime = Mathf.Abs(headVerticalSpeed) < 0.15f ? hoverTime + dt : 0f;

            bool landed = headLift <= minHeight * 0.5f && headVerticalSpeed <= 0f;
            if (landed || hoverTime > 0.25f)
                jumping = false;
        }

        float target = jumping ? Mathf.Max(0f, headLift) : 0f;

        // Rise instantly with the head, come down quickly but smoothly (no snap on landing).
        if (target >= bodyLift)
            bodyLift = target;
        else
            bodyLift = Mathf.Lerp(bodyLift, target, 1f - Mathf.Exp(-20f * dt));

        if (bodyLift < 0.001f)
            bodyLift = 0f;
        return bodyLift;
    }

    /// <summary>Bend hips and knees while in the air, more the higher the jump.</summary>
    private void ApplyJumpTuck(Quaternion bodyRotation, float scaleFactor)
    {
        if (!detectJumps || bodyLift <= 0f)
            return;

        float tuck = Mathf.Clamp01(bodyLift / Mathf.Max(jumpFullTuckHeight * scaleFactor, 0.01f));
        tuck = tuck * (2f - tuck); // ease out: tucks quickly right after take-off
        Vector3 right = bodyRotation * Vector3.right;

        // Negative angle around the body's right axis swings a leg forward; positive swings it back.
        Quaternion hip = Quaternion.AngleAxis(-jumpHipBend * tuck, right);
        Quaternion knee = Quaternion.AngleAxis(jumpKneeBend * tuck, right);

        if (leftThigh != null) leftThigh.rotation = hip * leftThigh.rotation;
        if (rightThigh != null) rightThigh.rotation = hip * rightThigh.rotation;
        if (leftShin != null) leftShin.rotation = knee * leftShin.rotation;
        if (rightShin != null) rightShin.rotation = knee * rightShin.rotation;
    }

    private void UpdateHeightCalibration()
    {
        if (!matchPlayerHeight || Time.time - spawnTime > calibrationTime)
            return;

        // Don't measure mid-jump, or the avatar would be sized for a giant.
        if (Mathf.Abs(headVerticalSpeed) > 0.5f)
            return;

        float eyeHeight = headTarget.position.y - floorHeight;
        if (eyeHeight < 0.9f)
        {
            // Headset not on a standing person yet: keep waiting, don't use up the time window.
            spawnTime = Time.time;
            return;
        }
        if (eyeHeight <= maxEyeHeight)
            return;

        maxEyeHeight = eyeHeight;

        // Character eye height at original scale ~ head bone height + eye offset.
        float avatarEyeHeight = restHeadHeight - headBoneOffsetFromEyes.y;
        float factor = Mathf.Clamp(maxEyeHeight / Mathf.Max(avatarEyeHeight, 0.1f),
                                   minScaleFactor, maxScaleFactor);
        transform.localScale = originalScale * factor;
    }

    private void SolveArm(Transform upper, Transform lower, Transform hand,
                          Quaternion upperRest, Quaternion lowerRest,
                          Transform target, bool isLeft, Vector3 extraRotation,
                          Quaternion handOffset, Vector3 fingersLocal,
                          Vector3 hintLocal, Quaternion bodyRotation)
    {
        upper.localRotation = upperRest;
        lower.localRotation = lowerRest;

        // Final hand rotation first, so the wrist can be placed relative to it:
        // the palm sits on the controller and the wrist is behind it, along the hand.
        Quaternion handRotation = target.rotation *
                                  CalibrationFor(isLeft) *
                                  Quaternion.Euler(extraRotation) * handOffset;
        Vector3 fingersWorld = handRotation * fingersLocal;
        Vector3 wrist = target.position - fingersWorld * gripToWrist;

        Vector3 hint = upper.position + bodyRotation * hintLocal;
        SolveTwoBone(upper, lower, hand, wrist, hint);

        // Close any small remaining gap so the hand sits on the controller.
        Vector3 gap = wrist - hand.position;
        if (gap.sqrMagnitude > 0.000001f)
            hand.position += Vector3.ClampMagnitude(gap, maxHandStretch);

        hand.rotation = handRotation;
    }

    private void SetUpFingerMuscles()
    {
        // Low-poly rigs often only have index (= all four fingers) and thumb bones.
        HumanBodyBones[] l = { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
                               HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
                               HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
                               HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal };
        HumanBodyBones[] r = { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
                               HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
                               HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
                               HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal };
        float[] deg = { 55f, 70f, 45f, 55f, 70f, 45f, 55f, 70f, 45f, 55f, 70f, 45f };

        SetUpHand(leftHand, l, deg, HumanBodyBones.LeftThumbProximal, out leftFingers, out leftFingerRest, out leftCurlAxis);
        SetUpHand(rightHand, r, deg, HumanBodyBones.RightThumbProximal, out rightFingers, out rightFingerRest, out rightCurlAxis);
        fingerCurlDegrees = deg;
    }

    private void SetUpHand(Transform hand, HumanBodyBones[] bones, float[] deg, HumanBodyBones thumbBone,
                           out Transform[] fingers, out Quaternion[] rest, out Vector3[] axis)
    {
        fingers = new Transform[bones.Length];
        rest = new Quaternion[bones.Length];
        axis = new Vector3[bones.Length];

        Transform index = animator.GetBoneTransform(bones[0]);
        Transform thumb = animator.GetBoneTransform(thumbBone);
        if (index == null || thumb == null)
            return;

        // Fingers curl around the hand's side-to-side axis (from little finger toward the thumb).
        Vector3 fingersDir = (index.position - hand.position).normalized;
        Vector3 sideDir = thumb.position - hand.position;
        sideDir = (sideDir - fingersDir * Vector3.Dot(sideDir, fingersDir)).normalized;

        for (int i = 0; i < bones.Length; i++)
        {
            Transform b = animator.GetBoneTransform(bones[i]);
            if (b == null) continue;
            fingers[i] = b;
            rest[i] = b.localRotation;
            axis[i] = b.InverseTransformDirection(sideDir);
        }
    }

    private void ApplyFingerGrip()
    {
        if (fingerCurlDegrees == null)
            return;

        // Mirror images: the two hands curl in opposite directions around their side axis.
        CurlHand(leftFingers, leftFingerRest, leftCurlAxis, GripSign);
        CurlHand(rightFingers, rightFingerRest, rightCurlAxis, -GripSign);
    }

    private void CurlHand(Transform[] fingers, Quaternion[] rest, Vector3[] axis, float sign)
    {
        if (fingers == null)
            return;

        for (int i = 0; i < fingers.Length; i++)
        {
            if (fingers[i] == null) continue;
            float angle = sign * FingerGrip * fingerCurlDegrees[i];
            fingers[i].localRotation = rest[i] * Quaternion.AngleAxis(angle, axis[i]);
        }
    }

    /// <summary>Bend the spine so the head bone reaches where the real head is.</summary>
    private void ApplySpineLean(Vector3 headBonePos)
    {
        if (hips == null)
            return;

        Vector3 from = head.position - hips.position;
        Vector3 to = headBonePos - hips.position;
        if (from.sqrMagnitude < 0.0001f || to.sqrMagnitude < 0.0001f)
            return;

        Quaternion.FromToRotation(from, to).ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;
        angle = Mathf.Clamp(angle, -maxSpineBend, maxSpineBend);
        if (Mathf.Abs(angle) < 0.01f || float.IsNaN(axis.x))
            return;

        int n = (spine != null ? 1 : 0) + (chest != null ? 1 : 0) + (upperChest != null ? 1 : 0);
        if (n == 0) return;
        Quaternion part = Quaternion.AngleAxis(angle / n, axis);
        if (spine != null) spine.rotation = part * spine.rotation;
        if (chest != null) chest.rotation = part * chest.rotation;
        if (upperChest != null) upperChest.rotation = part * upperChest.rotation;
    }

    /// <summary>Start measuring the player's height again (e.g. after being placed on their spot).</summary>
    public void RestartHeightCalibration()
    {
        spawnTime = Time.time;
        maxEyeHeight = 0f;
    }

    private void OnGUI()
    {
        if (!hideHead) return; // local player only
        GUI.Label(new Rect(10, Screen.height - 30, 700, 25),
            $"Hand calibration  X={HandCalibration.x:0}  Y={HandCalibration.y:0}  Z={HandCalibration.z:0}" +
            "   Finger grip " + FingerGrip.ToString("0.0") +
            "   (keys 1/2/3 turn hands, 0 = default, 4/5 = less/more grip, 6 = flip grip — saved)");
    }

    /// <summary>
    /// Hand orientation from the finger/thumb bones, so it works with any rig axes.
    /// A held controller's Z axis runs along the handle (where the thumb rests),
    /// and the fingers/knuckles point along the controller's -Y.
    /// </summary>
    private static Quaternion ComputeHandOffset(Transform hand, Transform index, Transform thumb, out Vector3 fingersLocal)
    {
        fingersLocal = Vector3.up;
        if (index == null || thumb == null)
            return Quaternion.identity;

        fingersLocal = hand.InverseTransformDirection(index.position - hand.position).normalized;
        Vector3 thumbLocal = hand.InverseTransformDirection(thumb.position - hand.position);
        thumbLocal -= fingersLocal * Vector3.Dot(thumbLocal, fingersLocal);

        if (fingersLocal.sqrMagnitude < 0.0001f || thumbLocal.sqrMagnitude < 0.000001f)
            return Quaternion.identity;

        // Map: thumb side -> controller +Z, fingers -> controller -Y.
        Quaternion handFrame = Quaternion.LookRotation(thumbLocal.normalized, -fingersLocal);
        return Quaternion.Inverse(handFrame);
    }

    /// <summary>Classic two-bone IK (law of cosines), elbow bends toward the hint.</summary>
    private static void SolveTwoBone(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 hint)
    {
        Vector3 a = upper.position;
        float upperLen = Vector3.Distance(a, lower.position);
        float lowerLen = Vector3.Distance(lower.position, end.position);

        Vector3 toTarget = target - a;
        float dist = Mathf.Clamp(toTarget.magnitude, 0.01f, (upperLen + lowerLen) * 0.999f);
        Vector3 dir = toTarget.sqrMagnitude > 0.000001f ? toTarget.normalized : upper.forward;

        Vector3 toHint = hint - a;
        Vector3 bendDir = toHint - dir * Vector3.Dot(toHint, dir);
        if (bendDir.sqrMagnitude < 0.000001f)
            bendDir = Vector3.down - dir * Vector3.Dot(Vector3.down, dir);
        bendDir.Normalize();

        float cosA = (upperLen * upperLen + dist * dist - lowerLen * lowerLen) / (2f * upperLen * dist);
        cosA = Mathf.Clamp(cosA, -1f, 1f);
        float sinA = Mathf.Sqrt(1f - cosA * cosA);

        Vector3 elbow = a + dir * (cosA * upperLen) + bendDir * (sinA * upperLen);

        upper.rotation = Quaternion.FromToRotation(lower.position - a, elbow - a) * upper.rotation;
        lower.rotation = Quaternion.FromToRotation(end.position - lower.position, target - lower.position) * lower.rotation;
    }
}
