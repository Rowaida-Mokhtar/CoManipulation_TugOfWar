using System;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;

/// <summary>
/// Put this on the NetworkPlayer prefab (replaces NetworkPlayerPose).
///
/// - Host = side A (uses the character under "PlayerA"), client = side B ("PlayerB").
/// - The owner sends its headset + controller poses; everyone else sees a
///   full-body character whose head and arms follow those poses.
/// - The local player sees their own character body/arms (head hidden).
/// - On spawn, the local XR rig is placed at its side of the arena, facing the opponent.
/// </summary>
public class NetworkAvatarPlayer : NetworkBehaviour
{
    [Header("Scene anchors (existing objects in the arena)")]
    public string sideAAnchorName = "PlayerA";
    public string sideBAnchorName = "PlayerB";

    [Header("Local XR rig (found by name if left empty)")]
    public Transform localHead;
    public Transform localLeftController;
    public Transform localRightController;
    public string leftControllerName = "Left Controller";
    public string rightControllerName = "Right Controller";

    [Header("Behaviour")]
    [Tooltip("Move the local XR rig to this player's side of the arena on spawn.")]
    public bool placeRigAtSide = true;
    [Tooltip("Shortcut: press the RIGHT thumbstick in (click it) = teleport straight to your game spot (only before a match).")]
    public bool quickTeleportToSpot = true;
    [Tooltip("Show the local player's own character body/arms (head hidden).")]
    public bool showOwnBody = true;
    [Tooltip("Hide the white controller models (your character's hands replace them). " +
             "Untick while tuning hand rotation so you can compare.")]
    public bool hideControllerModels = true;
    [Tooltip("How quickly remote poses catch up (higher = snappier, lower = smoother).")]
    public float remoteSmoothing = 20f;

    [Header("Avatar animation (drag from Hodaart/.../Animations)")]
    public AnimationClip idleClip;
    public AnimationClip walkClip;

    [Header("Reactions (seen by the OTHER player; your own body always follows you)")]
    [Tooltip("Played by the loser's avatar as it tips into the pond (e.g. Jump).")]
    public AnimationClip fallClip;
    [Tooltip("The winner's avatar plays one of these at random (e.g. Dance 1, Dance 2).")]
    public AnimationClip[] winnerDanceClips;
    public float danceSeconds = 3f;
    [Tooltip("The other player's avatar waves when you meet (e.g. Greeting).")]
    public AnimationClip greetingClip;
    public float greetingSeconds = 2.5f;
    [Tooltip("The loser's view fades to underwater blue before they are put back on their spot.")]
    public bool underwaterFade = true;

    [Header("Match movement (players are moved with the rope)")]
    [Tooltip("Move this player's view with the match, so the loser is dragged toward the pond.")]
    public bool moveWithMatch = true;
    [Tooltip("Comfort: darken the edges of the view while being moved. " +
             "Drag Samples/XR Interaction Toolkit/3.6.1/Starter Assets/TunnelingVignette/TunnelingVignette here.")]
    public GameObject tunnelingVignettePrefab;
    [Tooltip("Speed (m/s) above which the comfort vignette turns on.")]
    public float vignetteSpeedThreshold = 0.05f;

    [Header("Rope vibration (controller haptics)")]
    [Tooltip("Vibrate the controllers while you hold the rope during a match.")]
    public bool ropeHaptics = true;
    [Tooltip("Shared rope: vibration follows the OPPONENT's pull (remote touch).")]
    [Range(0f, 1f)] public float hapticStrength = 0.6f;
    [Tooltip("Opponent pull value that gives full vibration. Tune if it feels too weak or too strong.")]
    public float hapticFullPull = 3f;
    [Tooltip("Never vibrate below this level while the rope is held, so players can feel the rope is 'live'.")]
    [Range(0f, 1f)] public float hapticIdleLevel = 0.05f;

    // ---- Pull input (owner writes, host reads) ----
    private readonly NetworkVariable<float> pullInput = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private static readonly List<NetworkAvatarPlayer> players = new List<NetworkAvatarPlayer>();

    // ---- Holding the rope (owner writes, everyone reads) ----
    private readonly NetworkVariable<bool> holdingRope = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>True when every connected player is holding the rope (and at least one is connected).</summary>
    public static bool AllHoldingRope()
    {
        int n = 0;
        foreach (NetworkAvatarPlayer p in players)
        {
            if (p == null || !p.IsSpawned) continue;
            if (!p.holdingRope.Value) return false;
            n++;
        }
        return n > 0;
    }

    /// <summary>True if the player on this PC is holding the rope.</summary>
    public static bool LocalHoldingRope()
    {
        foreach (NetworkAvatarPlayer p in players)
            if (p != null && p.IsSpawned && p.IsOwner)
                return p.holdingRope.Value;
        return false;
    }

    /// <summary>True if a real (networked) player is on the given side.</summary>
    public static bool HasPlayerOnSide(bool sideA)
    {
        foreach (NetworkAvatarPlayer p in players)
            if (p != null && p.IsSpawned && p.IsSideA == sideA)
                return true;
        return false;
    }

    /// <summary>All spawned players on this PC (for sound and display only).</summary>
    public static IReadOnlyList<NetworkAvatarPlayer> All => players;
    public float PullInput => pullInput.Value;
    public Transform HeadTarget => headTarget;

    /// <summary>Host: current pull input of the player on the given side (0 if none).</summary>
    public static float GetPullInput(bool sideA)
    {
        foreach (NetworkAvatarPlayer p in players)
            if (p != null && p.IsSpawned && p.IsSideA == sideA)
                return p.pullInput.Value;
        return SoloOpponentBot.PullFor(sideA); // practice opponent in solo tests, else 0
    }

    private TugOfWarController tugController;
    private XROrigin xrOrigin;
    private float appliedProgress;
    private TunnelingVignetteController vignette;
    private readonly MatchVignetteProvider vignetteProvider = new MatchVignetteProvider();
    private bool vignetteOn;

    private class MatchVignetteProvider : ITunnelingVignetteProvider
    {
        public VignetteParameters vignetteParameters => null; // use the vignette's own defaults
    }

    // A fully closed vignette in a chosen colour = a screen fade (smallest aperture wins).
    private class FadeProvider : ITunnelingVignetteProvider
    {
        public VignetteParameters vignetteParameters { get; }
        public FadeProvider(Color c, float easeIn, float easeOut)
        {
            vignetteParameters = new VignetteParameters
            {
                apertureSize = 0f,
                featheringEffect = 0.2f,
                easeInTime = easeIn,
                easeOutTime = easeOut,
                easeInTimeLock = true,
                easeOutDelayTime = 0f,
                vignetteColor = c,
                vignetteColorBlend = c
            };
        }
    }

    private readonly FadeProvider underwater = new FadeProvider(new Color(0.05f, 0.28f, 0.45f, 1f), 0.6f, 0.6f);
    private readonly FadeProvider blink = new FadeProvider(Color.black, 0.25f, 0.35f);
    private FadeProvider activeFade;

    // ---- Synced pose (owner writes, everyone reads) ----
    public struct PoseData : INetworkSerializable, IEquatable<PoseData>
    {
        public Vector3 headPos, leftPos, rightPos;
        public Quaternion headRot, leftRot, rightRot;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref headPos); s.SerializeValue(ref headRot);
            s.SerializeValue(ref leftPos); s.SerializeValue(ref leftRot);
            s.SerializeValue(ref rightPos); s.SerializeValue(ref rightRot);
        }

        public bool Equals(PoseData o) =>
            headPos == o.headPos && leftPos == o.leftPos && rightPos == o.rightPos &&
            headRot == o.headRot && leftRot == o.leftRot && rightRot == o.rightRot;
    }

    private readonly NetworkVariable<PoseData> pose = new NetworkVariable<PoseData>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ---- Chosen look at the mirror (owner writes, everyone reads) ----
    // Character: 255 = the scene's default character for this side; otherwise AvatarCatalog index.
    private readonly NetworkVariable<byte> lookChoice = new NetworkVariable<byte>(
        255, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    // Accessory: 0 = none; otherwise AvatarCatalog accessory index + 1.
    private readonly NetworkVariable<byte> accessoryChoice = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>The player on this PC (null until spawned).</summary>
    public static NetworkAvatarPlayer Local { get; private set; }
    public Transform LeftHandTarget => leftTarget;
    public Transform RightHandTarget => rightTarget;
    public VRAvatarIK AvatarBody => AvatarIK;

    /// <summary>Character number shown at the mirror (0-based), or -1 if unknown.</summary>
    public int CurrentCharacter
    {
        get
        {
            if (lookChoice.Value != 255) return lookChoice.Value;
            AvatarCatalog c = AvatarCatalog.Instance;
            GameObject t = GetTemplate(IsSideA);
            return c != null && t != null ? c.IndexOfName(t.name) : -1;
        }
    }

    /// <summary>Accessory number shown at the mirror: 0 = none, 1.. = accessories.</summary>
    public int CurrentAccessory => accessoryChoice.Value;

    /// <summary>Mirror buttons: next / previous character (only before a match).</summary>
    public void ChangeCharacter(int step)
    {
        AvatarCatalog c = AvatarCatalog.Instance;
        if (!IsOwner || c == null || c.CharacterCount == 0 || !InLobby()) return;
        int current = Mathf.Max(CurrentCharacter, 0);
        int next = ((current + step) % c.CharacterCount + c.CharacterCount) % c.CharacterCount;
        lookChoice.Value = (byte)next;
    }

    /// <summary>Character board: become character number i (0-based), only before a match.</summary>
    public void SetCharacter(int i)
    {
        AvatarCatalog c = AvatarCatalog.Instance;
        if (!IsOwner || c == null || i < 0 || i >= c.CharacterCount || !InLobby()) return;
        if (lookChoice.Value != (byte)i) lookChoice.Value = (byte)i;
    }

    /// <summary>Mirror buttons: next / previous accessory, including "none".</summary>
    public void ChangeAccessory(int step)
    {
        AvatarCatalog c = AvatarCatalog.Instance;
        if (!IsOwner || c == null || c.AccessoryCount == 0 || !InLobby()) return;
        int n = c.AccessoryCount + 1;
        accessoryChoice.Value = (byte)(((accessoryChoice.Value + step) % n + n) % n);
    }

    private static bool InLobby()
    {
        NetworkTugOfWar m = NetworkTugOfWar.Instance;
        return m == null || m.State == NetworkTugOfWar.MatchState.Waiting;
    }

    /// <summary>True if this player is side A (the host).</summary>
    public bool IsSideA => OwnerClientId == NetworkManager.ServerClientId;

    private Transform headTarget, leftTarget, rightTarget;
    private Transform ropeEnd;           // point between both hands where the rope is held
    private bool ropeEndRegistered;
    private GameObject avatar;
    private bool hasReceivedPose;

    // Character templates taken from the scene (shared by all network players).
    private static GameObject templateA, templateB;

    private bool ignored;          // true for a stray copy placed in the scene
    private bool rigPlacementPending;
    private float spawnTime;
    private float nextHideTime;
    private NetworkTugOfWar.MatchState lastMatchState = NetworkTugOfWar.MatchState.Waiting;

    public override void OnNetworkSpawn()
    {
        // Only the copy that NGO spawns for a connected player counts.
        // A NetworkPlayer left in the scene Hierarchy would otherwise become a second player.
        if (!NetworkObject.IsPlayerObject)
        {
            ignored = true;
            Debug.LogWarning("NetworkAvatarPlayer: ignoring a NetworkPlayer placed in the scene. " +
                             "Delete it from the Hierarchy (keep the prefab in Assets/Prefabs).");
            return;
        }

        players.Add(this);
        if (IsOwner) Local = this;
        spawnTime = Time.time;
        lookChoice.OnValueChanged += OnLookChanged;
        accessoryChoice.OnValueChanged += OnLookChanged;
        NetworkTugOfWar.MatchEnded += OnMatchEnded;
        NetworkTugOfWar.MatchReset += OnMatchReset;
        tugController = FindAnyObjectByType<TugOfWarController>();

        headTarget = new GameObject($"P{OwnerClientId} HeadTarget").transform;
        leftTarget = new GameObject($"P{OwnerClientId} LeftHandTarget").transform;
        rightTarget = new GameObject($"P{OwnerClientId} RightHandTarget").transform;
        ropeEnd = new GameObject($"P{OwnerClientId} RopeEnd").transform;

        if (IsOwner)
        {
            xrOrigin = FindAnyObjectByType<XROrigin>();
            FindLocalRig();
            // Wait until the headset is actually tracking before placing the player,
            // otherwise the real head offset is added afterwards and the player ends up off their spot.
            rigPlacementPending = placeRigAtSide;

            appliedProgress = tugController != null ? tugController.MatchProgress : 0f;
            SetUpVignette();
        }

        if (!IsOwner || showOwnBody)
            CreateAvatar();

        if (!IsOwner && greetingClip != null)
            StartCoroutine(WaveHello());

        Debug.Log($"NetworkAvatarPlayer spawned: client {OwnerClientId}, side {(IsSideA ? "A" : "B")}, owner={IsOwner}");
    }

    public override void OnNetworkDespawn()
    {
        if (ignored)
            return;

        players.Remove(this);
        if (Local == this) Local = null;
        lookChoice.OnValueChanged -= OnLookChanged;
        accessoryChoice.OnValueChanged -= OnLookChanged;
        NetworkTugOfWar.MatchEnded -= OnMatchEnded;
        NetworkTugOfWar.MatchReset -= OnMatchReset;
        if (activeFade != null && vignette != null) vignette.EndTunnelingVignette(activeFade);
        if (ropeEndRegistered && NetworkTugOfWar.Instance != null)
            NetworkTugOfWar.Instance.SetRopeEnd(IsSideA, null);
        if (ropeEnd != null) Destroy(ropeEnd.gameObject);
        if (vignette != null)
        {
            if (vignetteOn) vignette.EndTunnelingVignette(vignetteProvider);
            Destroy(vignette.gameObject);
        }
        if (avatar != null) Destroy(avatar);
        if (headTarget != null) Destroy(headTarget.gameObject);
        if (leftTarget != null) Destroy(leftTarget.gameObject);
        if (rightTarget != null) Destroy(rightTarget.gameObject);
    }

    private void LateUpdate()
    {
        if (ignored || !IsSpawned || headTarget == null)
            return;

        if (IsOwner)
        {
            if (localHead == null || localLeftController == null || localRightController == null)
                FindLocalRig();
            if (localHead == null)
                return;

            HandleRigPlacement();

            // Controller models can be created late (when a controller wakes up), so keep hiding them.
            if (hideControllerModels && showOwnBody && Time.time >= nextHideTime)
            {
                HideControllerModels(localLeftController);
                HideControllerModels(localRightController);
                nextHideTime = Time.time + 0.5f;
            }

            // Send this player's pull to the host.
            if (tugController != null)
            {
                pullInput.Value = tugController.ReadLocalPlayerInput(IsSideA);

                // Before a match the rope only counts as held while you stand in your circle.
                bool holding = tugController.IsLocalGripHeld() &&
                               (!InLobby() || GardenLobby.IsOnSpot(IsSideA, localHead.position));
                if (holdingRope.Value != holding)
                    holdingRope.Value = holding;
            }

            // Feel the rope: controllers vibrate with the opponent's pull.
            UpdateRopeHaptics();

            // Option (a): move this player's view together with the rope.
            MoveRigWithMatch();

            // Local targets follow tracking exactly (no network delay for yourself).
            Copy(localHead, headTarget);
            CopyHand(localLeftController, leftTarget, -1f);
            CopyHand(localRightController, rightTarget, 1f);

            pose.Value = new PoseData
            {
                headPos = headTarget.position, headRot = headTarget.rotation,
                leftPos = leftTarget.position, leftRot = leftTarget.rotation,
                rightPos = rightTarget.position, rightRot = rightTarget.rotation
            };
        }
        else
        {
            PoseData p = pose.Value;
            if (p.headRot.x == 0f && p.headRot.y == 0f && p.headRot.z == 0f && p.headRot.w == 0f)
                return; // nothing received yet

            float t = hasReceivedPose ? 1f - Mathf.Exp(-remoteSmoothing * Time.deltaTime) : 1f;
            hasReceivedPose = true;

            Follow(headTarget, p.headPos, p.headRot, t);
            Follow(leftTarget, p.leftPos, p.leftRot, t);
            Follow(rightTarget, p.rightPos, p.rightRot, t);
        }

        UpdateRopeEnd();
    }

    // The rope is held between both hands; attach this player's rope end there.
    private void UpdateRopeEnd()
    {
        if (ropeEnd == null)
            return;

        GardenLobby lobby = GardenLobby.Instance;
        bool away = lobby != null && lobby.Active && InLobby() &&
                    !GardenLobby.IsOnSpot(IsSideA, headTarget.position);
        ropeEnd.position = away
            ? lobby.RopeRestPoint(IsSideA)                                // lying on the grass
            : (leftTarget.position + rightTarget.position) * 0.5f;       // in your hands

        if (!ropeEndRegistered && NetworkTugOfWar.Instance != null)
        {
            NetworkTugOfWar.Instance.SetRopeEnd(IsSideA, ropeEnd);
            ropeEndRegistered = true;
        }
    }

    private static void Copy(Transform from, Transform to) =>
        to.SetPositionAndRotation(from.position, from.rotation);

    /// <summary>
    /// Copies a controller pose. If the controller is missing or switched off (not tracked yet),
    /// the hand rests beside the body instead of flying to the world origin.
    /// </summary>
    private void CopyHand(Transform controller, Transform target, float side)
    {
        if (controller != null && controller.gameObject.activeInHierarchy)
        {
            Copy(controller, target);
            return;
        }

        Vector3 flatForward = localHead.forward;
        flatForward.y = 0f;
        Quaternion yaw = flatForward.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(flatForward) : Quaternion.identity;

        target.SetPositionAndRotation(
            localHead.position + yaw * new Vector3(0.2f * side, -0.65f, 0.1f),
            yaw);
    }

    // ---------------- Placing the player on their spot ----------------

    private void HandleRigPlacement()
    {
        // 1) First placement, once the headset is tracking (or after 5 s without a headset).
        if (rigPlacementPending && (IsHeadsetTracking() || Time.time - spawnTime > 5f))
        {
            Transform gate = GardenLobby.Instance != null ? GardenLobby.Instance.SpawnFor(IsSideA) : null;
            if (gate != null) PlaceRigAt(gate);
            else PlaceRigAtSide();
            rigPlacementPending = false;
        }

        // 2) Put both players back on their spots at the start of every match.
        NetworkTugOfWar match = NetworkTugOfWar.Instance;
        if (match != null)
        {
            NetworkTugOfWar.MatchState state = match.State;
            if (placeRigAtSide && state == NetworkTugOfWar.MatchState.Countdown &&
                lastMatchState != NetworkTugOfWar.MatchState.Countdown)
                PlaceRigAtSide();
            lastMatchState = state;
        }

        // 3) Experimenter shortcut: R = put me back on my spot.
        if (Input.GetKeyDown(KeyCode.R))
            PlaceRigAtSide();

        // 4) Player shortcut: right thumbstick pressed in = teleport to my spot (before a match only).
        bool stickDown = IsStickClicked(UnityEngine.XR.XRNode.RightHand);
        bool waiting = match == null || match.State == NetworkTugOfWar.MatchState.Waiting;
        if (quickTeleportToSpot && stickDown && !stickWasDown && waiting && !rigPlacementPending)
            StartCoroutine(TeleportToSpotBehindFade());
        stickWasDown = stickDown;
    }

    private bool stickWasDown;

    private static bool IsStickClicked(UnityEngine.XR.XRNode node)
    {
        UnityEngine.XR.InputDevice device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
        return device.isValid &&
               device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxisClick, out bool down) && down;
    }

    private System.Collections.IEnumerator TeleportToSpotBehindFade()
    {
        StartFade(blink);
        yield return new WaitForSeconds(0.3f);
        PlaceRigAtSide();
        yield return new WaitForSeconds(0.2f);
        EndFade();
    }

    private static bool IsHeadsetTracking()
    {
        UnityEngine.XR.InputDevice head = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
        return head.isValid &&
               head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool tracked) && tracked;
    }

    private static void Follow(Transform tr, Vector3 pos, Quaternion rot, float t) =>
        tr.SetPositionAndRotation(Vector3.Lerp(tr.position, pos, t), Quaternion.Slerp(tr.rotation, rot, t));

    // ---------------- Match movement + comfort ----------------

    // ---------------- Reactions: dance, fall, respawn, wave ----------------

    private VRAvatarIK AvatarIK => avatar != null ? avatar.GetComponent<VRAvatarIK>() : null;

    private void OnMatchEnded(int loserSide)
    {
        bool iLost = (loserSide == 1) == IsSideA;

        if (!IsOwner)
        {
            // What THIS PC sees of the other player.
            VRAvatarIK ik = AvatarIK;
            if (ik == null) return;

            if (iLost)
                ik.PlayFall(fallClip);
            else if (winnerDanceClips != null && winnerDanceClips.Length > 0)
                ik.PlayEmote(winnerDanceClips[UnityEngine.Random.Range(0, winnerDanceClips.Length)], danceSeconds);
        }
        else if (iLost && underwaterFade)
        {
            // Your own view: sink into blue water.
            StartFade(underwater);
        }
    }

    private void OnMatchReset()
    {
        if (!IsOwner)
        {
            // Wait until that player has been put back on their spot, then show them again.
            StartCoroutine(EndFallLater());
            return;
        }

        // Back to your spot, hidden behind a short fade so the jump is not seen.
        StartCoroutine(RespawnBehindFade());
    }

    private System.Collections.IEnumerator RespawnBehindFade()
    {
        if (activeFade == null)
        {
            StartFade(blink);
            yield return new WaitForSeconds(0.3f);
        }

        if (placeRigAtSide)
            PlaceRigAtSide();

        yield return new WaitForSeconds(0.2f);
        EndFade();
    }

    private void StartFade(FadeProvider fade)
    {
        if (vignette == null) return;
        EndFade();
        vignette.BeginTunnelingVignette(fade);
        activeFade = fade;
    }

    private void EndFade()
    {
        if (vignette == null || activeFade == null) return;
        vignette.EndTunnelingVignette(activeFade);
        activeFade = null;
    }

    private System.Collections.IEnumerator EndFallLater()
    {
        yield return new WaitForSeconds(0.7f);
        AvatarIK?.EndFall();
    }

    private System.Collections.IEnumerator WaveHello()
    {
        yield return new WaitForSeconds(1.5f);
        AvatarIK?.PlayEmote(greetingClip, greetingSeconds);
    }

    // ---------------- Rope vibration ----------------

    private float nextHapticTime;

    /// <summary>
    /// How strongly the rope "tugs" at this player, 0..1.
    /// Shared rope: the opponent's current pull (remote touch).
    /// A baseline condition can later return its own resistance here instead.
    /// </summary>
    private float GetRopeTugAmount()
    {
        float opponentPull = GetPullInput(sideA: !IsSideA);
        return Mathf.Clamp01(opponentPull / Mathf.Max(hapticFullPull, 0.01f));
    }

    private void UpdateRopeHaptics()
    {
        if (!ropeHaptics || Time.time < nextHapticTime)
            return;

        NetworkTugOfWar match = NetworkTugOfWar.Instance;
        if (match == null || match.State != NetworkTugOfWar.MatchState.Playing)
            return;

        float amount = Mathf.Max(GetRopeTugAmount(), hapticIdleLevel) * hapticStrength;
        if (amount <= 0f)
            return;

        // Short pulses, renewed often, feel like continuous tension.
        const float pulse = 0.12f;
        SendHaptic(UnityEngine.XR.XRNode.LeftHand, amount, pulse);
        SendHaptic(UnityEngine.XR.XRNode.RightHand, amount, pulse);
        nextHapticTime = Time.time + 0.1f;
    }

    private static void SendHaptic(UnityEngine.XR.XRNode node, float amplitude, float duration)
    {
        UnityEngine.XR.InputDevice device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
        if (!device.isValid)
            return;

        // Only buzz the hand that is actually holding the rope (grip pressed).
        bool gripping = device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool grip) && grip;
        if (!gripping)
            return;

        if (device.TryGetHapticCapabilities(out UnityEngine.XR.HapticCapabilities caps) && caps.supportsImpulse)
            device.SendHapticImpulse(0, Mathf.Clamp01(amplitude), duration);
    }

    private void MoveRigWithMatch()
    {
        if (tugController == null || xrOrigin == null)
            return;

        float progress = tugController.MatchProgress;
        float delta = progress - appliedProgress;
        appliedProgress = progress;

        // A big jump means a new match started: the respawn (behind a fade) handles it.
        if (Mathf.Abs(delta) > 0.5f)
            return;

        if (moveWithMatch && Mathf.Abs(delta) > 0f)
        {
            // Same shift that moves PlayerA/PlayerB: positive progress = toward side A.
            Vector3 shift = -tugController.PullAxis * delta;
            shift.y = 0f;
            xrOrigin.Origin.transform.position += shift;
        }

        // Comfort vignette while being moved (not when snapping back for a new match).
        float speed = Mathf.Abs(delta) / Mathf.Max(Time.deltaTime, 0.0001f);
        bool shouldShow = moveWithMatch && speed > vignetteSpeedThreshold && Mathf.Abs(delta) < 0.5f;

        if (vignette != null && shouldShow != vignetteOn)
        {
            if (shouldShow) vignette.BeginTunnelingVignette(vignetteProvider);
            else vignette.EndTunnelingVignette(vignetteProvider);
            vignetteOn = shouldShow;
        }
    }

    private void SetUpVignette()
    {
        if (tunnelingVignettePrefab == null || localHead == null)
            return;

        GameObject v = Instantiate(tunnelingVignettePrefab, localHead);
        v.transform.localPosition = Vector3.zero;
        v.transform.localRotation = Quaternion.identity;
        vignette = v.GetComponent<TunnelingVignetteController>();
    }

    // ---------------- Local rig ----------------

    private void FindLocalRig()
    {
        if (localHead == null && Camera.main != null)
            localHead = Camera.main.transform;

        // Best source: the controllers already assigned on TugOfWarController.
        if (tugController != null)
        {
            if (localLeftController == null) localLeftController = tugController.controllerA;
            if (localRightController == null) localRightController = tugController.controllerB;
        }

        // Fallback: search the XR rig by name, including switched-off objects.
        if ((localLeftController == null || localRightController == null) && xrOrigin != null)
        {
            foreach (Transform t in xrOrigin.GetComponentsInChildren<Transform>(true))
            {
                if (localLeftController == null && t.name == leftControllerName) localLeftController = t;
                if (localRightController == null && t.name == rightControllerName) localRightController = t;
            }
        }
    }

    // Hides the 3D controller meshes only. Ray lines (LineRenderer) stay,
    // so players can still point at and click menus.
    private static void HideControllerModels(Transform controller)
    {
        if (controller == null)
            return;

        foreach (MeshRenderer r in controller.GetComponentsInChildren<MeshRenderer>(true))
            r.enabled = false;
        foreach (SkinnedMeshRenderer r in controller.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            r.enabled = false;
    }

    /// <summary>Put the player at a spawn point (e.g. the garden gate), facing its blue arrow.</summary>
    private void PlaceRigAt(Transform spot)
    {
        XROrigin origin = FindAnyObjectByType<XROrigin>();
        if (origin == null || origin.Camera == null) return;

        Vector3 facing = spot.forward; facing.y = 0f;
        if (facing.sqrMagnitude > 0.0001f)
            origin.MatchOriginUpCameraForward(Vector3.up, facing.normalized);
        float eyeHeight = origin.Camera.transform.position.y - origin.Origin.transform.position.y;
        origin.MoveCameraToWorldLocation(spot.position + Vector3.up * eyeHeight);

        if (tugController != null) appliedProgress = tugController.MatchProgress;
        AvatarIK?.RestartHeightCalibration();
        Debug.Log($"NetworkAvatarPlayer: player {(IsSideA ? "A" : "B")} starts at {spot.name}.");
    }

    private void PlaceRigAtSide()
    {
        XROrigin origin = FindAnyObjectByType<XROrigin>();
        Transform mySide = FindAnchor(IsSideA ? sideAAnchorName : sideBAnchorName);
        Transform otherSide = FindAnchor(IsSideA ? sideBAnchorName : sideAAnchorName);

        if (origin == null || origin.Camera == null || mySide == null || otherSide == null)
        {
            Debug.LogWarning("NetworkAvatarPlayer: could not place XR rig (missing XR Origin or PlayerA/PlayerB).");
            return;
        }

        Vector3 facing = otherSide.position - mySide.position;
        facing.y = 0f;
        if (facing.sqrMagnitude > 0.0001f)
            origin.MatchOriginUpCameraForward(Vector3.up, facing.normalized);

        // Keep the player's real eye height above the floor of their side.
        float eyeHeight = origin.Camera.transform.position.y - origin.Origin.transform.position.y;
        // Use the scene character's feet as the floor height, if there is one.
        Animator sceneCharacter = mySide.GetComponentInChildren<Animator>(true);
        Vector3 floorPoint = mySide.position;
        if (sceneCharacter != null && sceneCharacter.gameObject != mySide.gameObject)
            floorPoint.y = sceneCharacter.transform.position.y;

        origin.MoveCameraToWorldLocation(floorPoint + Vector3.up * eyeHeight);

        // The placement already accounts for the current match position.
        if (tugController != null)
            appliedProgress = tugController.MatchProgress;

        if (avatar != null)
        {
            VRAvatarIK ik = avatar.GetComponent<VRAvatarIK>();
            if (ik != null) ik.RestartHeightCalibration();
        }

        Debug.Log($"NetworkAvatarPlayer: placed player {(IsSideA ? "A" : "B")} on their spot.");
    }

    // ---------------- Avatar ----------------

    private void OnLookChanged(byte previous, byte current)
    {
        if (avatar != null || !IsOwner || showOwnBody)
            CreateAvatar(); // rebuild with the new look
    }

    private void CreateAvatar()
    {
        GameObject template = GetTemplate(IsSideA);
        if (template == null)
        {
            Debug.LogWarning("NetworkAvatarPlayer: no character found under PlayerA/PlayerB.");
            return;
        }

        // Rebuilding (new look chosen at the mirror): remove the old body first.
        Vector3 oldPos = template.transform.position;
        Quaternion oldRot = template.transform.rotation;
        if (avatar != null)
        {
            oldPos = avatar.transform.position;
            oldRot = avatar.transform.rotation;
            Destroy(avatar);
            avatar = null;
        }

        // The chosen character, or the scene's default one for this side.
        AvatarCatalog catalog = AvatarCatalog.Instance;
        GameObject source = lookChoice.Value != 255 && catalog != null ? catalog.GetCharacter(lookChoice.Value) : null;
        if (source == null) source = template;

        // Create it switched off (inside an inactive holder), set it up, then switch it on.
        GameObject holder = new GameObject("holder");
        holder.SetActive(false);
        avatar = Instantiate(source, oldPos, oldRot, holder.transform);
        avatar.SetActive(true); // still off, because the holder is off
        avatar.name = $"Avatar P{OwnerClientId} ({(IsSideA ? "A" : "B")})";
        avatar.transform.localScale = template.transform.lossyScale;

        VRAvatarIK ik = avatar.GetComponent<VRAvatarIK>();
        if (ik == null) ik = avatar.AddComponent<VRAvatarIK>();
        ik.headTarget = headTarget;
        ik.leftHandTarget = leftTarget;
        ik.rightHandTarget = rightTarget;
        ik.hideHead = IsOwner;
        ik.idleClip = idleClip;
        ik.walkClip = walkClip;
        ik.ropeEnd = ropeEnd;

        ik.floorHeight = template.transform.position.y; // character's feet = floor

        avatar.transform.SetParent(null, true); // switches it on
        Destroy(holder);
        AddAccessory(ik);
    }

    private void AddAccessory(VRAvatarIK ik)
    {
        AvatarCatalog catalog = AvatarCatalog.Instance;
        AvatarCatalog.Accessory a = catalog != null && accessoryChoice.Value > 0
            ? catalog.GetAccessory(accessoryChoice.Value - 1) : null;
        if (a == null || ik.HeadBone == null) return;

        GameObject item = Instantiate(a.model, avatar.transform);
        item.name = "Accessory: " + a.name;
        AccessoryFollower f = item.AddComponent<AccessoryFollower>();
        f.settings = a;
        f.ik = ik;
        f.Init(ownAvatar: IsOwner);
    }

    /// <summary>
    /// Uses the existing Character 01/02 under PlayerA/PlayerB as templates and hides
    /// them, so the network avatars replace the static scene characters.
    /// </summary>
    private GameObject GetTemplate(bool sideA)
    {
        if (sideA && templateA != null) return templateA;
        if (!sideA && templateB != null) return templateB;

        Transform anchor = FindAnchor(sideA ? sideAAnchorName : sideBAnchorName);
        if (anchor == null) return null;

        Animator animator = anchor.GetComponentInChildren<Animator>(true);
        if (animator == null || animator.gameObject == anchor.gameObject)
            return null; // need the character as a CHILD of the anchor

        GameObject template = animator.gameObject;
        template.SetActive(false);

        if (sideA) templateA = template; else templateB = template;
        return template;
    }

    private static Transform FindAnchor(string anchorName)
    {
        GameObject go = GameObject.Find(anchorName);
        return go != null ? go.transform : null;
    }

    // Static templates must be cleared if the scene reloads.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        players.Clear();
        templateA = null;
        templateB = null;
    }
}
