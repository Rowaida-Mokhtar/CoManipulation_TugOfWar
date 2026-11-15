using UnityEngine;

/// <summary>
/// A practice opponent for SOLO tests (NetworkTugOfWar "Allow Solo Test" on, nobody on side B).
/// Put it on the TugOfWarManager object. It is only used on the host, only while side B is empty,
/// and it removes itself as soon as a real Player B joins.
///
/// It is a full avatar like a real player: it pulls with changing strength, leans, walks when
/// dragged, grunts, falls into the pond or dances. Its pull feeds the same match logic,
/// so losing, the underwater fade and the rope vibration can all be tested alone.
/// NOT part of the study: real participants always play each other.
/// </summary>
[RequireComponent(typeof(TugOfWarController))]
public class SoloOpponentBot : MonoBehaviour
{
    [Tooltip("Use the bot when testing alone.")]
    public bool useBotWhenAlone = true;

    [Header("How hard it pulls")]
    [Tooltip("Average pull strength (a real hard pull is about 2-3).")]
    public float strength = 1.6f;
    [Tooltip("How much the pull changes (0 = steady, 1 = very uneven).")]
    [Range(0f, 1f)] public float variation = 0.6f;
    public Vector2 changeEverySeconds = new Vector2(0.5f, 1.4f);

    /// <summary>The active bot (null when none).</summary>
    public static SoloOpponentBot Instance { get; private set; }

    /// <summary>The bot's current pull for the given side, or 0.</summary>
    public static float PullFor(bool sideA) =>
        Instance != null && Instance.active && !sideA ? Instance.currentPull : 0f;

    public Transform Head => head;
    public float CurrentPull => active ? currentPull : 0f;

    private TugOfWarController controller;
    private Transform head, leftHand, rightHand, ropeEnd;
    private GameObject avatar, template;
    private VRAvatarIK ik;
    private bool active;
    private float currentPull, targetPull, nextChange;

    private void Awake() => controller = GetComponent<TugOfWarController>();

    private void OnEnable()
    {
        NetworkTugOfWar.MatchEnded += OnMatchEnded;
        NetworkTugOfWar.MatchReset += OnMatchReset;
    }

    private void OnDisable()
    {
        NetworkTugOfWar.MatchEnded -= OnMatchEnded;
        NetworkTugOfWar.MatchReset -= OnMatchReset;
        Despawn();
    }

    private void Update()
    {
        NetworkTugOfWar match = NetworkTugOfWar.Instance;
        bool shouldRun = useBotWhenAlone && match != null && match.IsSpawned && match.IsServer &&
                         match.allowSoloTest && !NetworkAvatarPlayer.HasPlayerOnSide(false) &&
                         NetworkAvatarPlayer.HasPlayerOnSide(true);

        if (shouldRun && !active) Spawn();
        else if (!shouldRun && active) Despawn();
        if (!active) return;

        UpdatePull(match.State);
        UpdateTargets();
    }

    // ---------------- Pulling ----------------

    private void UpdatePull(NetworkTugOfWar.MatchState state)
    {
        if (state != NetworkTugOfWar.MatchState.Playing)
        {
            currentPull = 0f;
            return;
        }

        if (Time.time >= nextChange)
        {
            targetPull = strength * Random.Range(1f - variation, 1f + variation * 0.5f);
            if (Random.value < 0.15f) targetPull = 0f; // a short rest now and then
            nextChange = Time.time + Random.Range(changeEverySeconds.x, changeEverySeconds.y);
        }
        currentPull = Mathf.MoveTowards(currentPull, targetPull, Time.deltaTime * 4f);
    }

    // ---------------- Body ----------------

    private void UpdateTargets()
    {
        Transform anchor = controller.playerB, other = controller.playerA;
        if (anchor == null || other == null) return;

        Vector3 facing = other.position - anchor.position; facing.y = 0f;
        Quaternion look = facing.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(facing.normalized) : anchor.rotation;
        Vector3 fwd = look * Vector3.forward, right = look * Vector3.right;

        // Pulling harder = head further back (the spine leans) and hands closer to the body.
        float effort = Mathf.Clamp01(currentPull / Mathf.Max(strength * 1.5f, 0.01f));
        float floorY = template != null ? template.transform.position.y : anchor.position.y;
        Vector3 basePos = new Vector3(anchor.position.x, floorY, anchor.position.z);

        // Head at this character's own eye height, so it neither floats nor squashes.
        float eye = ik != null && ik.StandingEyeHeight > 0.8f ? ik.StandingEyeHeight : 1.6f;
        head.SetPositionAndRotation(basePos + Vector3.up * eye - fwd * (0.18f * effort), look);

        float handY = eye * 0.65f - 0.05f * effort; // about hip-to-chest height
        Vector3 hands = basePos + Vector3.up * handY + fwd * (0.42f - 0.15f * effort);
        Quaternion grip = look * Quaternion.Euler(-30f, 0f, 0f);
        leftHand.SetPositionAndRotation(hands - right * 0.12f, grip);
        rightHand.SetPositionAndRotation(hands + right * 0.12f, grip);
        ropeEnd.position = hands;
    }

    // ---------------- Spawn / despawn ----------------

    private void Spawn()
    {
        Transform anchor = controller.playerB;
        Animator a = anchor != null ? anchor.GetComponentInChildren<Animator>(true) : null;
        if (a == null || a.gameObject == anchor.gameObject) return;

        // Same look, clips and settings as the real players (taken from the host's player).
        NetworkAvatarPlayer host = null;
        foreach (NetworkAvatarPlayer p in NetworkAvatarPlayer.All)
            if (p != null && p.IsSpawned) { host = p; break; }

        template = a.gameObject;
        head = new GameObject("Bot HeadTarget").transform;
        leftHand = new GameObject("Bot LeftHandTarget").transform;
        rightHand = new GameObject("Bot RightHandTarget").transform;
        ropeEnd = new GameObject("Bot RopeEnd").transform;
        UpdateTargets();

        bool wasActive = template.activeSelf;
        template.SetActive(false);
        avatar = Instantiate(template);
        avatar.name = "Practice opponent (B)";
        avatar.transform.localScale = template.transform.lossyScale;

        ik = avatar.GetComponent<VRAvatarIK>(); // keep the character's own settings if it has them
        if (ik == null) ik = avatar.AddComponent<VRAvatarIK>();
        ik.ropeEnd = ropeEnd;
        ik.headTarget = head;
        ik.leftHandTarget = leftHand;
        ik.rightHandTarget = rightHand;
        ik.floorHeight = template.transform.position.y;
        ik.matchPlayerHeight = false;
        if (host != null)
        {
            ik.idleClip = host.idleClip;
            ik.walkClip = host.walkClip;
        }
        avatar.SetActive(true);
        if (!wasActive) template.SetActive(false);

        if (NetworkTugOfWar.Instance != null)
            NetworkTugOfWar.Instance.SetRopeEnd(false, ropeEnd);

        Instance = this;
        active = true;
        Debug.Log("Solo test: practice opponent added on side B.");
    }

    private void Despawn()
    {
        if (!active) return;
        active = false;
        if (Instance == this) Instance = null;

        if (NetworkTugOfWar.Instance != null)
            NetworkTugOfWar.Instance.SetRopeEnd(false, null);
        if (avatar != null) Destroy(avatar);
        if (head != null) Destroy(head.gameObject);
        if (leftHand != null) Destroy(leftHand.gameObject);
        if (rightHand != null) Destroy(rightHand.gameObject);
        if (ropeEnd != null) Destroy(ropeEnd.gameObject);
        // The scene character comes back only if no real Player B has taken its place.
        if (template != null && !NetworkAvatarPlayer.HasPlayerOnSide(false))
            template.SetActive(true);
    }

    // ---------------- Reactions ----------------

    private void OnMatchEnded(int loserSide)
    {
        if (!active || ik == null) return;

        NetworkAvatarPlayer host = null;
        foreach (NetworkAvatarPlayer p in NetworkAvatarPlayer.All)
            if (p != null && p.IsSpawned) { host = p; break; }
        if (host == null) return;

        if (loserSide == 2)
            ik.PlayFall(host.fallClip);
        else if (host.winnerDanceClips != null && host.winnerDanceClips.Length > 0)
            ik.PlayEmote(host.winnerDanceClips[Random.Range(0, host.winnerDanceClips.Length)], host.danceSeconds);
    }

    private void OnMatchReset()
    {
        if (active && ik != null)
            StartCoroutine(EndFallLater());
    }

    private System.Collections.IEnumerator EndFallLater()
    {
        yield return new WaitForSeconds(0.6f);
        if (ik != null) ik.EndFall();
    }
}
