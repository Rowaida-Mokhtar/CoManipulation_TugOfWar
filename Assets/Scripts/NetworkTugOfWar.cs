using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Networked tug-of-war match. Put it on the TugOfWarManager object
/// (next to TugOfWarController) together with a NetworkObject component.
///
/// The HOST is the single authority: it reads both players' pull inputs,
/// runs the rope physics, decides the winner and shares the result.
/// Clients only send their own pull input and display what the host decides,
/// so the two PCs can never disagree about the outcome.
///
/// Flow: Waiting for players -> Countdown -> Playing -> Over -> (Play Again) -> Countdown ...
/// </summary>
[RequireComponent(typeof(TugOfWarController))]
public class NetworkTugOfWar : NetworkBehaviour
{
    public enum MatchState : byte { Waiting, Countdown, Playing, Over }

    [Header("Match flow")]
    public float countdownSeconds = 3f;
    [Tooltip("Let the host play alone (Player B input = 0, or D key in keyboard testing). For testing only.")]
    public bool allowSoloTest = false;
    [Tooltip("The countdown only starts when every player is holding the rope (grip pressed). " +
             "Letting go during the countdown cancels it.")]
    public bool requireBothHoldingToStart = true;

    [Tooltip("Seconds after a match ends before both players are put back on their spots.")]
    public float resultSeconds = 3.5f;
    [Tooltip("Show the old Play Again button. Off = the next match starts when both grab the rope again.")]
    public bool usePlayAgainButton = false;

    /// <summary>Raised on every PC when a match ends. Argument: loser (1 = side A, 2 = side B).</summary>
    public static event System.Action<int> MatchEnded;
    /// <summary>Raised on every PC when players are put back on their spots for the next match.</summary>
    public static event System.Action MatchReset;

    private bool overHandled;
    private float overStartTime;

    [Header("Display smoothing (clients)")]
    [Tooltip("How quickly clients catch up with the host's rope position. Higher = snappier.")]
    public float progressSmoothing = 15f;

    [Header("Shared rope")]
    [Tooltip("In a network session, draw the rope between the two players' hands.")]
    public bool ropeFollowsHands = true;
    [Tooltip("How far the middle of the hand-held rope sags.")]
    public float ropeSag = 0.2f;
    public float ropeRadius = 0.025f;

    private CurvedRopeVisual handRope;
    private Renderer[] oldRopeRenderers;

    private readonly NetworkVariable<MatchState> state = new NetworkVariable<MatchState>(MatchState.Waiting);
    private readonly NetworkVariable<float> progress = new NetworkVariable<float>(0f);
    private readonly NetworkVariable<byte> loser = new NetworkVariable<byte>(0);
    private readonly NetworkVariable<double> countdownEndTime = new NetworkVariable<double>(0);

    private TugOfWarController controller;
    private float displayedProgress;

    public MatchState State => state.Value;

    /// <summary>Seconds left in the countdown (negative once it has ended).</summary>
    public float CountdownRemaining =>
        IsSpawned ? (float)(countdownEndTime.Value - NetworkManager.ServerTime.Time) : 0f;

    /// <summary>Progress currently shown on this PC (smoothed on clients).</summary>
    public float DisplayedProgress => displayedProgress;

    public static NetworkTugOfWar Instance { get; private set; }

    private void Awake()
    {
        controller = GetComponent<TugOfWarController>();
    }

    public override void OnNetworkSpawn()
    {
        Instance = this;
        controller.externalControl = true;
        controller.SetMenuVisible(false);
        controller.SetPlayAgainVisible(false);

        state.OnValueChanged += OnStateChanged;
        loser.OnValueChanged += OnLoserChanged;

        if (IsServer)
        {
            controller.ResetForNewMatch();
            state.Value = MatchState.Waiting;
            progress.Value = 0f;
        }

        displayedProgress = progress.Value;
        controller.SetMatchProgress(displayedProgress);
        OnStateChanged(state.Value, state.Value);

        if (ropeFollowsHands)
            CreateHandRope();
    }

    // ---------------- Shared rope between the players' hands ----------------

    private void CreateHandRope()
    {
        Material ropeMaterial = null;
        if (controller.rope != null)
        {
            oldRopeRenderers = controller.rope.GetComponentsInChildren<Renderer>();
            if (oldRopeRenderers.Length > 0)
                ropeMaterial = oldRopeRenderers[0].sharedMaterial;
        }

        GameObject go = new GameObject("Shared Rope (hands)");
        handRope = go.AddComponent<CurvedRopeVisual>();
        handRope.playerA = controller.playerA;   // fallback until an avatar joins
        handRope.playerB = controller.playerB;
        handRope.sagDepth = ropeSag;
        handRope.radius = ropeRadius;

        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (ropeMaterial != null)
            mr.sharedMaterial = ropeMaterial;

        // Hide the old straight rope while the hand-held one is used.
        if (oldRopeRenderers != null)
            foreach (Renderer r in oldRopeRenderers)
                r.enabled = false;
    }

    /// <summary>Called by NetworkAvatarPlayer: attach one end of the rope to a player's hands.</summary>
    public void SetRopeEnd(bool sideA, Transform end)
    {
        if (handRope == null)
            return;

        if (sideA) handRope.handA = end;
        else handRope.handB = end;
    }

    public override void OnNetworkDespawn()
    {
        state.OnValueChanged -= OnStateChanged;
        loser.OnValueChanged -= OnLoserChanged;
        controller.externalControl = false;

        if (handRope != null)
            Destroy(handRope.gameObject);
        if (oldRopeRenderers != null)
            foreach (Renderer r in oldRopeRenderers)
                if (r != null) r.enabled = true;
        controller.ShowStatus("");
        if (Instance == this)
            Instance = null;
    }

    private void Update()
    {
        if (!IsSpawned)
            return;

        if (IsServer)
            ServerUpdate();

        UpdateDisplay();
    }

    // ---------------- Host (authority) ----------------

    private void ServerUpdate()
    {
        int playerCount = NetworkManager.ConnectedClientsIds.Count;
        bool enoughPlayers = playerCount >= 2 || (allowSoloTest && playerCount >= 1);

        switch (state.Value)
        {
            case MatchState.Waiting:
                if (enoughPlayers && EveryoneHolding())
                    StartCountdown();
                break;

            case MatchState.Countdown:
                if (!enoughPlayers || !EveryoneHolding())
                    state.Value = MatchState.Waiting;
                else if (NetworkManager.ServerTime.Time >= countdownEndTime.Value)
                    state.Value = MatchState.Playing;
                break;

            case MatchState.Playing:
                if (!enoughPlayers)
                {
                    state.Value = MatchState.Waiting;
                    break;
                }

                float inputA = NetworkAvatarPlayer.GetPullInput(sideA: true);
                float inputB = NetworkAvatarPlayer.GetPullInput(sideA: false);
                progress.Value = controller.SimulateStep(inputA, inputB, Time.deltaTime);

                int lost = controller.CheckLoser();
                if (lost != 0)
                {
                    loser.Value = (byte)lost;
                    state.Value = MatchState.Over;
                    overStartTime = Time.time;
                }
                break;

            case MatchState.Over:
                if (!usePlayAgainButton && Time.time - overStartTime >= resultSeconds)
                    RestartOnServer();
                break;
        }
    }

    private void StartCountdown()
    {
        overHandled = false;
        controller.ResetForNewMatch();
        progress.Value = 0f;
        loser.Value = 0;
        countdownEndTime.Value = NetworkManager.ServerTime.Time + countdownSeconds;
        state.Value = MatchState.Countdown;
    }

    /// <summary>Called by the Play Again button on any PC.</summary>
    public void RequestRestart()
    {
        if (!IsSpawned)
            return;

        if (IsServer)
            RestartOnServer();
        else
            RequestRestartServerRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestRestartServerRpc()
    {
        RestartOnServer();
    }

    private bool EveryoneHolding() =>
        !requireBothHoldingToStart || NetworkAvatarPlayer.AllHoldingRope();

    private void RestartOnServer()
    {
        if (state.Value != MatchState.Over)
            return;
        loser.Value = 0;

        // Back to the start positions; the countdown begins once everyone grabs the rope.
        controller.ResetForNewMatch();
        progress.Value = 0f;
        state.Value = MatchState.Waiting;
    }

    // ---------------- Everyone (display) ----------------

    private void UpdateDisplay()
    {
        float target = progress.Value;

        if (IsServer)
        {
            displayedProgress = target; // host already applied it in SimulateStep
        }
        else
        {
            // Snap on big jumps (new match), otherwise glide smoothly between network updates.
            if (Mathf.Abs(target - displayedProgress) > 0.5f)
                displayedProgress = target;
            else
                displayedProgress = Mathf.Lerp(displayedProgress, target,
                    1f - Mathf.Exp(-progressSmoothing * Time.deltaTime));

            controller.SetMatchProgress(displayedProgress);
        }

        if (state.Value == MatchState.Waiting)
            controller.ShowStatus(WaitingMessage());

        FaceTextToLocalPlayer();

        if (state.Value == MatchState.Countdown)
        {
            double remaining = countdownEndTime.Value - NetworkManager.ServerTime.Time;
            controller.ShowStatus(remaining > 0 ? Mathf.CeilToInt((float)remaining).ToString() : "PULL!");
        }
    }

    private void OnLoserChanged(byte previous, byte current) => TryHandleOver();

    // The state and the loser can arrive in either order on a client, so act once both are known.
    private void TryHandleOver()
    {
        if (overHandled || state.Value != MatchState.Over || loser.Value == 0)
            return;
        overHandled = true;

        if (!IsServer)
            controller.SetMatchProgress(progress.Value);
        controller.ShowLoss(loser.Value);
        // Each PC shows its own result ("You win" / "You lose"), not "Player A wins".
        NetworkAvatarPlayer me = NetworkAvatarPlayer.Local;
        if (me != null)
        {
            bool iLost = (loser.Value == 1) == me.IsSideA;
            controller.ShowStatus(iLost ? "SPLASH!\nYou fell into the pond" : "YOU WIN!");
        }
        controller.SetPlayAgainVisible(usePlayAgainButton);
        MatchEnded?.Invoke(loser.Value);

        // Solo test: the empty side is shown by the scene's stand-in character. Sink it too.
        bool loserIsA = loser.Value == 1;
        if (!NetworkAvatarPlayer.HasPlayerOnSide(loserIsA) &&
            !(SoloOpponentBot.Instance != null && !loserIsA)) // the practice opponent falls by itself
            StartCoroutine(SinkStandIn(loserIsA));
    }

    private Transform sunkStandIn;
    private Vector3 sunkLocalPos;
    private Quaternion sunkLocalRot;

    private System.Collections.IEnumerator SinkStandIn(bool sideA)
    {
        Transform anchor = sideA ? controller.playerA : controller.playerB;
        Animator character = anchor != null ? anchor.GetComponentInChildren<Animator>() : null;
        if (character == null || character.gameObject == anchor.gameObject)
            yield break;

        Transform c = character.transform;
        sunkStandIn = c;
        sunkLocalPos = c.localPosition;
        sunkLocalRot = c.localRotation;

        // Same stumble-hop and sink as the avatars, toward the pond.
        Vector3 toPond = sideA ? controller.PullAxis : -controller.PullAxis;
        Vector3 start = c.position;
        Quaternion facing = Quaternion.LookRotation(toPond, Vector3.up);

        for (float t = 0f; t < 1f; t += Time.deltaTime / 1.8f)
        {
            if (sunkStandIn != c) yield break;
            VRAvatarIK.FallPose(t, start, facing, 1.0f, 1.8f, out Vector3 pos, out Quaternion rot);
            c.SetPositionAndRotation(pos, rot);
            yield return null;
        }
        c.gameObject.SetActive(false);
    }

    private void RestoreStandIn()
    {
        if (sunkStandIn == null) return;
        sunkStandIn.localPosition = sunkLocalPos;
        sunkStandIn.localRotation = sunkLocalRot;
        // Only show it again if no real player has taken that side meanwhile.
        bool sideA = controller.playerA != null && sunkStandIn.IsChildOf(controller.playerA);
        if (!NetworkAvatarPlayer.HasPlayerOnSide(sideA))
            sunkStandIn.gameObject.SetActive(true);
        sunkStandIn = null;
    }

    // The text is one sign in the world; turn it toward the player on THIS PC,
    // so each player reads it the right way round (it was mirrored for one of them).
    private Transform textSign;
    private void FaceTextToLocalPlayer()
    {
        if (controller.losingMessage == null || Camera.main == null) return;
        if (textSign == null)
        {
            Canvas c = controller.losingMessage.canvas;
            textSign = c != null && c.renderMode == RenderMode.WorldSpace
                ? c.rootCanvas.transform : controller.losingMessage.transform;
        }
        Vector3 away = textSign.position - Camera.main.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude > 0.0001f)
            textSign.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
    }

    private string WaitingMessage()
    {
        // Only the host knows how many players are connected; a client is connected by definition.
        bool connected = !IsServer || allowSoloTest || NetworkManager.ConnectedClientsIds.Count >= 2;

        if (!connected)
            return "Waiting for the other player to join...";
        NetworkAvatarPlayer me = NetworkAvatarPlayer.Local;
        if (me != null && me.HeadTarget != null && !GardenLobby.IsOnSpot(me.IsSideA, me.HeadTarget.position))
            return "Walk to your glowing circle by the pond";
        if (!requireBothHoldingToStart)
            return "Get ready...";
        if (!NetworkAvatarPlayer.LocalHoldingRope())
            return "Grab the rope to start";
        return "Waiting for the other player to grab the rope...";
    }

    private void OnStateChanged(MatchState previous, MatchState current)
    {
        switch (current)
        {
            case MatchState.Waiting:
                controller.SetPlayAgainVisible(false);
                if (previous == MatchState.Over)
                {
                    if (!IsServer)
                        controller.ResetForNewMatch();
                    RestoreStandIn();
                    MatchReset?.Invoke();
                }
                overHandled = false;
                displayedProgress = progress.Value;
                break;

            case MatchState.Countdown:
                controller.SetPlayAgainVisible(false);
                if (!IsServer)
                    controller.ResetForNewMatch();
                displayedProgress = 0f;
                break;

            case MatchState.Playing:
                controller.ShowStatus("");
                break;

            case MatchState.Over:
                TryHandleOver();
                break;
        }
    }
}
