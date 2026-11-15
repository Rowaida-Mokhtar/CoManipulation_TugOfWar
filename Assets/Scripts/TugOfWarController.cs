
using UnityEngine;
using TMPro;
using UnityEngine.XR;

public class TugOfWarController : MonoBehaviour
{
    [Header("Rope")]
    public Transform rope;
    public float ropeHeightOffset = -0.2f;

    [Header("Players")]
    public Transform playerA;
    public Transform playerB;

    [Header("Balance Marker (optional)")]
    [Tooltip("Moves with the match progress so players can see who is winning.")]
    public Transform balanceMarker;

    [Header("Losing Lines (not used for scoring any more; kept for reference)")]
    public Transform losingLineA;
    public Transform losingLineB;

    [Header("VR Controllers")]
    public Transform controllerA;
    public Transform controllerB;

    [Header("Player Strength")]
    [Min(0f)] public float playerAStrength = 1f;
    [Min(0f)] public float playerBStrength = 1f;

    [Header("Tug-of-War Mechanics")]
    [Min(0f)] public float pullAcceleration = 1.5f;
    [Min(0f)] public float ropeResistance = 2f;
    [Min(0.01f)] public float maxRopeSpeed = 1.2f;
    [Min(0.1f)] public float maxRopeOffset = 2.5f;

    [Header("VR Pull Settings")]
    [Min(0f)] public float pullSensitivity = 2f;
    [Min(0f)] public float deadZone = 0.01f;

    public enum PullDirectionMode
    {
        // "Pull" = hand moves backward relative to where the headset faces.
        // Works when one person tests both hands, and when each player faces the opponent.
        HeadsetBackward,
        // "Pull" = hand moves along the rope away from the opponent (fixed world direction).
        RopeAxis
    }

    [Tooltip("HeadsetBackward: pulling back toward your body always counts, for both hands. " +
             "RopeAxis: Player A must pull toward its own side of the arena, Player B toward its side.")]
    public PullDirectionMode pullDirectionMode = PullDirectionMode.HeadsetBackward;

    [Tooltip("Optional. Leave empty to use Camera.main (the XR camera).")]
    public Transform headset;

    [Tooltip("Hand speeds above this (m/s) are treated as tracking glitches and capped.")]
    [Min(0.1f)] public float maxHandSpeed = 4f;

    [Tooltip("Extra braking applied when nobody is pulling, so the rope stops soon after release.")]
    [Min(0f)] public float releaseBraking = 6f;

    [Header("Debug")]
    public bool debugLogging = false;

    [Header("Keyboard Testing")]
    public bool keyboardTesting = false;

    [Header("VR Testing")]
    public bool startGameOnAwake = false;

    [Header("Name Entry")]
    public TMP_InputField player1NameInput;
    public TMP_InputField player2NameInput;
    public GameObject player1NameObject;
    public GameObject player2NameObject;
    public GameObject startGameButtonObject;

    [Header("Game Result")]
    public TMP_Text losingMessage;
    public GameObject playAgainButtonObject;

    [Header("Pond Loss Zone")]
    public BoxCollider pondLossZone;

    [Header("Pond Splash")]
    public AudioClip splashSound;
    public float splashLifetime = 2f;

    private float ropeVelocity;
    private float matchProgress;
    private Vector3 startingBalanceMarkerPosition;

    private Vector3 previousControllerAPosition;
    private Vector3 previousControllerBPosition;

    private Vector3 startingRopePosition;
    private Vector3 startingRopeScale;
    private Quaternion startingRopeRotation;

    private Vector3 startingPlayerAPosition;
    private Vector3 startingPlayerBPosition;

    private Vector3 pullAxis;

    private bool controllersInitialized;
    private bool gameStarted;
    private bool gameOver;
    private bool startingStateSaved;

    private string playerAName = "Player A";
    private string playerBName = "Player B";

    private void Start()
    {
        SaveStartingState();
        InitializeControllerPositions();

        gameStarted = startGameOnAwake;
        gameOver = false;

        if (startGameOnAwake)
        {
            if (player1NameObject != null)
                player1NameObject.SetActive(false);

            if (player2NameObject != null)
                player2NameObject.SetActive(false);

            if (startGameButtonObject != null)
                startGameButtonObject.SetActive(false);

            Debug.Log("Tug-of-war auto-started for VR testing.");
        }

        if (losingMessage != null)
        {
            losingMessage.text = "";
            losingMessage.gameObject.SetActive(false);
        }

        if (playAgainButtonObject != null)
            playAgainButtonObject.SetActive(false);

        UpdatePullAxis();
        UpdateRopeVisual();
    }

    private void SaveStartingState()
    {
        if (playerA != null)
            startingPlayerAPosition = playerA.position;

        if (playerB != null)
            startingPlayerBPosition = playerB.position;

        if (balanceMarker != null)
            startingBalanceMarkerPosition = balanceMarker.position;

        if (rope != null)
        {
            startingRopePosition = rope.position;
            startingRopeRotation = rope.rotation;
            startingRopeScale = rope.localScale;
        }

        startingStateSaved = true;
    }

    private void InitializeControllerPositions()
    {
        UpdatePreviousControllerPositions();

        controllersInitialized =
            controllerA != null && controllerB != null;
    }

    private void Update()
    {
        // In a network session, NetworkTugOfWar drives the match instead.
        if (externalControl)
            return;

        if (!gameStarted || gameOver)
            return;

        ReadPullInputs(out float inputA, out float inputB);
        SimulateStep(inputA, inputB, Time.deltaTime);

        int loser = CheckLoser();
        if (loser == 1)
            PlayerAFallsIntoPond();
        else if (loser == 2)
            PlayerBFallsIntoPond();
    }

    // =====================================================================
    // Public API used by NetworkTugOfWar (networked match)
    // =====================================================================

    /// <summary>When true, this script no longer runs the match by itself.</summary>
    [HideInInspector] public bool externalControl;

    /// <summary>Shared match progress in metres. Positive = Player A winning.</summary>
    public float MatchProgress => matchProgress;

    /// <summary>Direction from Player A's start to Player B's start (flat).</summary>
    public Vector3 PullAxis => pullAxis;

    public float MaxRopeOffset => maxRopeOffset;

    /// <summary>
    /// Physics step on the authority (host). Returns the new match progress.
    /// </summary>
    public float SimulateStep(float inputA, float inputB, float dt)
    {
        UpdatePullAxis();

        float netForce = inputA - inputB;

        ropeVelocity += netForce * pullAcceleration * dt;

        // Stronger braking when nobody is actively pulling, so the rope
        // does not keep drifting toward the pond after the grip is released.
        float resistance = ropeResistance;
        if (inputA <= 0f && inputB <= 0f)
            resistance += releaseBraking;

        ropeVelocity *= Mathf.Exp(-resistance * dt);
        ropeVelocity = Mathf.Clamp(ropeVelocity, -maxRopeSpeed, maxRopeSpeed);

        // Shared match progress (metres). Positive = Player A is winning.
        // The whole system (both players, rope, marker) shifts together
        // toward the winning side, like a real tug-of-war.
        matchProgress += ropeVelocity * dt;

        if (Mathf.Abs(matchProgress) >= maxRopeOffset)
        {
            matchProgress = Mathf.Sign(matchProgress) * maxRopeOffset;
            ropeVelocity = 0f;
        }

        ApplyMatchProgress();
        UpdateRopeVisual();
        return matchProgress;
    }

    /// <summary>0 = nobody lost yet, 1 = Player A lost, 2 = Player B lost.</summary>
    public int CheckLoser()
    {
        // Pond detection: the player dragged into the pond loses.
        if (pondLossZone != null)
        {
            if (playerA != null && IsInsidePond(playerA.position))
                return 1;
            if (playerB != null && IsInsidePond(playerB.position))
                return 2;
        }

        // Fallback: dragged the full distance (Max Rope Offset) without touching the pond.
        if (matchProgress <= -maxRopeOffset) return 1;
        if (matchProgress >= maxRopeOffset) return 2;
        return 0;
    }

    /// <summary>Show a progress value received from the host (clients).</summary>
    public void SetMatchProgress(float progress)
    {
        UpdatePullAxis();
        matchProgress = progress;
        ApplyMatchProgress();
        UpdateRopeVisual();
    }

    /// <summary>Play the splash and show the result for the given loser (1 = A, 2 = B).</summary>
    public void ShowLoss(int loser)
    {
        gameOver = false; // let the fall functions run once
        if (loser == 1)
            PlayerAFallsIntoPond();
        else if (loser == 2)
            PlayerBFallsIntoPond();
    }

    /// <summary>Show a status line (waiting / countdown) in the result text.</summary>
    public void ShowStatus(string message)
    {
        if (losingMessage == null)
            return;

        losingMessage.text = message;
        losingMessage.gameObject.SetActive(!string.IsNullOrEmpty(message));
    }

    /// <summary>Hide the local-only menu (name entry, start button) in network sessions.</summary>
    public void SetMenuVisible(bool visible)
    {
        if (player1NameObject != null) player1NameObject.SetActive(visible);
        if (player2NameObject != null) player2NameObject.SetActive(visible);
        if (startGameButtonObject != null) startGameButtonObject.SetActive(visible);
    }

    public void SetPlayAgainVisible(bool visible)
    {
        if (playAgainButtonObject != null)
            playAgainButtonObject.SetActive(visible);
    }

    /// <summary>Reset positions/velocity for a new networked match.</summary>
    public void ResetForNewMatch()
    {
        ResetGameState();
        gameOver = false;
        gameStarted = true;
        ShowStatus("");
        SetPlayAgainVisible(false);
    }

    /// <summary>
    /// The local player's pull strength this frame, using BOTH of this
    /// player's hands (the stronger pull counts). Used in network sessions,
    /// where each PC controls only its own side.
    /// </summary>
    /// <summary>
    /// True while this PC's player is holding the rope (grip on either controller).
    /// In keyboard testing there are no controllers, so the rope counts as held.
    /// </summary>
    public bool IsLocalGripHeld()
    {
        if (keyboardTesting)
            return true;
        return IsGripHeld(XRNode.LeftHand) || IsGripHeld(XRNode.RightHand);
    }

    public float ReadLocalPlayerInput(bool isSideA)
    {
        float strength = isSideA ? playerAStrength : playerBStrength;

        if (keyboardTesting)
        {
            UpdatePreviousControllerPositions();
            return (Input.GetKey(KeyCode.Space) ||
                    Input.GetKey(isSideA ? KeyCode.A : KeyCode.D)) ? strength : 0f;
        }

        if (controllerA == null || controllerB == null)
            return 0f;

        UpdatePullAxis();
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);

        // controllerA = left hand, controllerB = right hand of THIS player.
        Vector3 velocityLeft = GetHandVelocity(controllerA, previousControllerAPosition, dt);
        Vector3 velocityRight = GetHandVelocity(controllerB, previousControllerBPosition, dt);
        UpdatePreviousControllerPositions();

        Vector3 pullDir = GetLocalPlayerPullDirection(isSideA);

        float best = 0f;
        if (IsGripHeld(XRNode.LeftHand))
            best = Mathf.Max(best, Vector3.Dot(velocityLeft, pullDir));
        if (IsGripHeld(XRNode.RightHand))
            best = Mathf.Max(best, Vector3.Dot(velocityRight, pullDir));

        return best > deadZone ? best * pullSensitivity * strength : 0f;
    }

    private Vector3 GetLocalPlayerPullDirection(bool isSideA)
    {
        Vector3 ropeDir = isSideA ? -pullAxis : pullAxis;

        if (pullDirectionMode != PullDirectionMode.HeadsetBackward)
            return ropeDir;

        Transform head = headset;
        if (head == null && Camera.main != null)
            head = Camera.main.transform;
        if (head == null)
            return ropeDir;

        Vector3 backward = -head.forward;
        backward.y = 0f;
        return backward.sqrMagnitude > 0.0001f ? backward.normalized : ropeDir;
    }

    // Positions everything from the single shared progress value.
    // This is also the one value that will be synced over the network later.
    private void ApplyMatchProgress()
    {
        Vector3 shift = -pullAxis * matchProgress;

        if (playerA != null)
            playerA.position = startingPlayerAPosition + shift;

        if (playerB != null)
            playerB.position = startingPlayerBPosition + shift;

        if (balanceMarker != null)
            balanceMarker.position = startingBalanceMarkerPosition + shift;
    }

    // Fixed direction from Player A's start to Player B's start.
    private void UpdatePullAxis()
    {
        if (playerA == null || playerB == null)
            return;

        Vector3 difference =
            startingPlayerBPosition - startingPlayerAPosition;

        difference.y = 0f;

        if (difference.sqrMagnitude > 0.0001f)
            pullAxis = difference.normalized;
    }

    private void ReadPullInputs(
        out float inputA,
        out float inputB)
    {
        inputA = 0f;
        inputB = 0f;

        if (keyboardTesting)
        {
            if (Input.GetKey(KeyCode.A))
                inputA = playerAStrength;

            if (Input.GetKey(KeyCode.D))
                inputB = playerBStrength;

            UpdatePreviousControllerPositions();
            return;
        }

        if (!controllersInitialized ||
            controllerA == null || controllerB == null)
        {
            UpdatePreviousControllerPositions();
            return;
        }

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);

        // Hand velocity measured in tracking space (relative to the XR rig),
        // then turned into a world direction. This ignores rig movement from
        // locomotion/teleport, which previously counted as "pulling".
        Vector3 velocityA = GetHandVelocity(
            controllerA, previousControllerAPosition, dt);

        Vector3 velocityB = GetHandVelocity(
            controllerB, previousControllerBPosition, dt);

        bool grippingA = IsGripHeld(XRNode.LeftHand);
        bool grippingB = IsGripHeld(XRNode.RightHand);

        // Only movement in the pull direction counts.
        // Pushing the hands forward or sideways adds no force.
        GetPullDirections(out Vector3 pullDirA, out Vector3 pullDirB);

        float pullSpeedA = Vector3.Dot(velocityA, pullDirA);
        float pullSpeedB = Vector3.Dot(velocityB, pullDirB);

        if (grippingA && pullSpeedA > deadZone)
        {
            inputA =
                pullSpeedA * pullSensitivity * playerAStrength;
        }

        if (grippingB && pullSpeedB > deadZone)
        {
            inputB =
                pullSpeedB * pullSensitivity * playerBStrength;
        }

        if (debugLogging && (grippingA || grippingB))
        {
            Debug.Log(
                $"Grip L:{grippingA} R:{grippingB} | " +
                $"pullA:{pullSpeedA:F2} pullB:{pullSpeedB:F2} | " +
                $"inputA:{inputA:F2} inputB:{inputB:F2} | " +
                $"ropeVel:{ropeVelocity:F2} progress:{matchProgress:F2}"
            );
        }

        UpdatePreviousControllerPositions();
    }

    private void GetPullDirections(out Vector3 dirA, out Vector3 dirB)
    {
        // Default: rope axis. A pulls away from B, B pulls away from A.
        dirA = -pullAxis;
        dirB = pullAxis;

        if (pullDirectionMode != PullDirectionMode.HeadsetBackward)
            return;

        Transform head = headset;
        if (head == null && Camera.main != null)
            head = Camera.main.transform;

        if (head == null)
            return;

        Vector3 backward = -head.forward;
        backward.y = 0f;

        if (backward.sqrMagnitude < 0.0001f)
            return;

        backward.Normalize();
        dirA = backward;
        dirB = backward;
    }

    private Vector3 GetHandVelocity(
        Transform controller,
        Vector3 previousLocalPosition,
        float dt)
    {
        Vector3 localDelta =
            controller.localPosition - previousLocalPosition;

        Vector3 worldDelta = controller.parent != null
            ? controller.parent.TransformVector(localDelta)
            : localDelta;

        return Vector3.ClampMagnitude(worldDelta / dt, maxHandSpeed);
    }

    private bool IsGripHeld(XRNode node)
    {
        InputDevice device =
            InputDevices.GetDeviceAtXRNode(node);

        if (!device.isValid)
            return false;

        if (device.TryGetFeatureValue(
            CommonUsages.gripButton,
            out bool gripButton))
        {
            return gripButton;
        }

        return device.TryGetFeatureValue(
            CommonUsages.grip,
            out float gripValue)
            && gripValue > 0.5f;
    }

    // Stored in local (tracking) space; see GetHandVelocity.
    private void UpdatePreviousControllerPositions()
    {
        if (controllerA != null)
            previousControllerAPosition = controllerA.localPosition;

        if (controllerB != null)
            previousControllerBPosition = controllerB.localPosition;
    }

    private void UpdateRopeVisual()
    {
        if (rope == null ||
            playerA == null ||
            playerB == null)
            return;

        Vector3 midpoint =
            (playerA.position + playerB.position) * 0.5f;

        midpoint.y += ropeHeightOffset;
        rope.position = midpoint;

        Vector3 direction =
            playerB.position - playerA.position;

        direction.y = 0f;

        if (direction.sqrMagnitude > 0.0001f)
        {
            rope.rotation = Quaternion.FromToRotation(
                Vector3.up,
                direction.normalized
            );
        }

        if (startingStateSaved)
            rope.localScale = startingRopeScale;
    }

    private bool IsInsidePond(Vector3 worldPosition)
    {
        if (pondLossZone == null)
            return false;

        Vector3 localPosition =
            pondLossZone.transform.InverseTransformPoint(
                worldPosition
            );

        Vector3 center = pondLossZone.center;
        Vector3 halfSize = pondLossZone.size * 0.5f;

        return localPosition.x >= center.x - halfSize.x &&
               localPosition.x <= center.x + halfSize.x &&
               localPosition.z >= center.z - halfSize.z &&
               localPosition.z <= center.z + halfSize.z &&
               localPosition.y >= center.y - halfSize.y &&
               localPosition.y <=
                   center.y + halfSize.y + 1.5f;
    }

    private void PlayerAFallsIntoPond()
    {
        if (gameOver)
            return;

        gameOver = true;

        Debug.Log("PLAYER A FALL DETECTED!");

        CreatePondSplashAt(GetSplashPosition(playerA));

        ShowResult(
            playerBName + " WINS!\n" +
            playerAName + " fell into the pond!"
        );
    }

    private void PlayerBFallsIntoPond()
    {
        if (gameOver)
            return;

        gameOver = true;

        Debug.Log("PLAYER B FALL DETECTED!");

        CreatePondSplashAt(GetSplashPosition(playerB));

        ShowResult(
            playerAName + " WINS!\n" +
            playerBName + " fell into the pond!"
        );
    }

    private Vector3 GetSplashPosition(Transform player)
    {
        if (player == null)
            return Vector3.zero;

        Vector3 position = player.position;

        if (pondLossZone != null)
            position.y = pondLossZone.bounds.max.y;

        return position;
    }

    private void ShowResult(string message)
    {
        if (losingMessage != null)
        {
            losingMessage.text = message;
            losingMessage.gameObject.SetActive(true);
        }

        if (playAgainButtonObject != null)
            playAgainButtonObject.SetActive(true);

        Debug.Log(message);
    }

    private void CreatePondSplashAt(Vector3 position)
    {
        GameObject splash = new GameObject("PondSplash");
        splash.transform.position = position;

        ParticleSystem particles =
            splash.AddComponent<ParticleSystem>();

        particles.Stop(
            true,
            ParticleSystemStopBehavior.StopEmittingAndClear
        );

        ParticleSystem.MainModule main = particles.main;
        main.playOnAwake = false;
        main.duration = 1.2f;
        main.loop = false;
        main.startLifetime = 0.7f;
        main.startSpeed = 2.5f;
        main.startSize = 0.12f;
        main.maxParticles = 80;

        ParticleSystem.EmissionModule emission =
            particles.emission;

        emission.enabled = true;
        emission.rateOverTime = 0;

        emission.SetBursts(
            new[] { new ParticleSystem.Burst(0f, 45) }
        );

        ParticleSystem.ShapeModule shape = particles.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = 0.35f;

        ParticleSystem.ColorOverLifetimeModule color =
            particles.colorOverLifetime;

        color.enabled = true;

        Gradient gradient = new Gradient();

        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(
                    new Color(0.65f, 0.9f, 1f), 0f
                ),
                new GradientColorKey(Color.white, 1f)
            },
            new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0f, 1f)
            }
        );

        color.color = gradient;

        if (splashSound != null)
        {
            AudioSource audioSource =
                splash.AddComponent<AudioSource>();

            audioSource.clip = splashSound;
            audioSource.spatialBlend = 0f;
            audioSource.playOnAwake = false;
            audioSource.volume = 1f;
            audioSource.Play();
        }

        particles.Play();

        float lifetime = splashSound != null
            ? Mathf.Max(splashLifetime, splashSound.length)
            : splashLifetime;

        Destroy(splash, lifetime);
    }

    public void StartGame()
    {
        if (player1NameInput != null &&
            !string.IsNullOrWhiteSpace(
                player1NameInput.text))
        {
            playerAName = player1NameInput.text.Trim();
        }

        if (player2NameInput != null &&
            !string.IsNullOrWhiteSpace(
                player2NameInput.text))
        {
            playerBName = player2NameInput.text.Trim();
        }

        if (playerAName == "Player 1 Name")
            playerAName = "Player A";

        if (playerBName == "Player 2 Name")
            playerBName = "Player B";

        ResetGameState();

        if (player1NameObject != null)
            player1NameObject.SetActive(false);

        if (player2NameObject != null)
            player2NameObject.SetActive(false);

        if (startGameButtonObject != null)
            startGameButtonObject.SetActive(false);

        if (losingMessage != null)
        {
            losingMessage.text = "";
            losingMessage.gameObject.SetActive(false);
        }

        if (playAgainButtonObject != null)
            playAgainButtonObject.SetActive(false);

        gameStarted = true;
        gameOver = false;

        Debug.Log(
            "Game started: " +
            playerAName + " vs " + playerBName
        );
    }

    private void ResetGameState()
    {
        if (!startingStateSaved)
            SaveStartingState();

        if (playerA != null)
            playerA.position = startingPlayerAPosition;

        if (playerB != null)
            playerB.position = startingPlayerBPosition;

        if (rope != null)
        {
            rope.position = startingRopePosition;
            rope.rotation = startingRopeRotation;
            rope.localScale = startingRopeScale;
        }

        ropeVelocity = 0f;
        matchProgress = 0f;

        UpdatePullAxis();
        ApplyMatchProgress();
        InitializeControllerPositions();
        UpdateRopeVisual();
    }

    public void PlayAgain()
    {
        if (externalControl)
        {
            NetworkTugOfWar net = GetComponent<NetworkTugOfWar>();
            if (net != null)
                net.RequestRestart();
            return;
        }

        gameOver = false;
        gameStarted = false;

        ResetGameState();

        if (losingMessage != null)
        {
            losingMessage.text = "";
            losingMessage.gameObject.SetActive(false);
        }

        if (playAgainButtonObject != null)
            playAgainButtonObject.SetActive(false);

        if (player1NameObject != null)
            player1NameObject.SetActive(true);

        if (player2NameObject != null)
            player2NameObject.SetActive(true);

        if (startGameButtonObject != null)
            startGameButtonObject.SetActive(true);

        Debug.Log("Game reset. Ready to play again.");
    }
}
