using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;

/// <summary>
/// The garden before and between matches. Put it on the TugOfWarManager object.
///
/// - Players start at the garden gate (Spawn A / Spawn B) and can walk around
///   (thumbstick / teleport) while the game is waiting.
/// - A glowing circle by the pond marks each player's spot. The rope only counts as
///   "held" while you stand in your circle, so a match starts when BOTH players are
///   on their circles holding the rope.
/// - Walking and teleporting are switched off from the countdown until the result,
///   so nobody can walk away mid-match. After the result you may roam again.
/// - While a player is away from their circle, their end of the rope lies on the grass.
/// Same in every study condition (controlled variable).
/// </summary>
[RequireComponent(typeof(TugOfWarController))]
public class GardenLobby : MonoBehaviour
{
    [Header("Start at the gate")]
    [Tooltip("Use the lobby. Off = old behaviour (start on your spot, no circles).")]
    public bool useLobby = true;
    [Tooltip("Where Player A (host) appears. Blue arrow = the way they face. Empty = start on the spot.")]
    public Transform spawnA;
    [Tooltip("Where Player B appears. Blue arrow = the way they face. Empty = start on the spot.")]
    public Transform spawnB;

    [Header("Start circles by the pond")]
    [Tooltip("How close (metres) your head must be to the middle of your circle.")]
    public float spotRadius = 0.6f;
    public Color circleColorA = new Color(1f, 0.55f, 0.1f, 1f);
    public Color circleColorB = new Color(0.2f, 0.6f, 1f, 1f);
    public Color circleReadyColor = new Color(0.3f, 1f, 0.35f, 1f);
    [Tooltip("Height of the light beam above each circle, so it can be seen from the gate.")]
    public float beamHeight = 2.5f;

    [Header("Movement")]
    [Tooltip("Switch off thumbstick walking, turning and teleport from the countdown until the result.")]
    public bool lockMovementDuringMatch = true;

    public static GardenLobby Instance { get; private set; }

    private TugOfWarController controller;
    private Vector3 homeA, homeB;      // the two circles (floor level)
    private bool homesKnown;
    private LineRenderer ringA, ringB, beamA, beamB;
    private readonly List<Behaviour> movement = new List<Behaviour>();
    private bool movementAllowed = true;
    private float nextMovementSearch;

    private void Awake()
    {
        controller = GetComponent<TugOfWarController>();
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Start()
    {
        CaptureHomes();
        if (!useLobby) return;

        ringA = MakeLine("Start circle A", true);
        ringB = MakeLine("Start circle B", true);
        beamA = MakeLine("Start beam A", false);
        beamB = MakeLine("Start beam B", false);
        DrawCircle(ringA, homeA);
        DrawCircle(ringB, homeB);
        DrawBeam(beamA, homeA);
        DrawBeam(beamB, homeB);
    }

    // The circles sit where the players stand at the start of a match (match progress 0).
    private void CaptureHomes()
    {
        if (controller.playerA == null || controller.playerB == null) return;
        homeA = FloorUnder(controller.playerA);
        homeB = FloorUnder(controller.playerB);
        homesKnown = true;
    }

    private static Vector3 FloorUnder(Transform anchor)
    {
        Vector3 p = anchor.position;
        Animator character = anchor.GetComponentInChildren<Animator>(true);
        if (character != null && character.gameObject != anchor.gameObject)
            p.y = character.transform.position.y; // the character's feet = the floor
        return p;
    }

    // ---------------- Queries used by the players ----------------

    public bool Active => useLobby && homesKnown;

    /// <summary>The middle of a player's circle, at floor level.</summary>
    public Vector3 SpotFor(bool sideA) => sideA ? homeA : homeB;

    /// <summary>Is this head standing in that side's circle? (Always true without the lobby.)</summary>
    public static bool IsOnSpot(bool sideA, Vector3 headPosition)
    {
        GardenLobby l = Instance;
        if (l == null || !l.Active) return true;
        Vector3 d = headPosition - l.SpotFor(sideA);
        d.y = 0f;
        return d.magnitude <= l.spotRadius;
    }

    /// <summary>Where the rope end rests on the grass while that player is away.</summary>
    public Vector3 RopeRestPoint(bool sideA)
    {
        Vector3 me = SpotFor(sideA), other = SpotFor(!sideA);
        Vector3 toPond = other - me; toPond.y = 0f;
        return me + toPond.normalized * 0.5f + Vector3.up * 0.06f;
    }

    /// <summary>The gate spawn for that side, or null.</summary>
    public Transform SpawnFor(bool sideA) => !useLobby ? null : sideA ? spawnA : spawnB;

    // ---------------- Every frame ----------------

    private void Update()
    {
        NetworkTugOfWar match = NetworkTugOfWar.Instance;
        bool waiting = match == null || !match.IsSpawned || match.State == NetworkTugOfWar.MatchState.Waiting;

        // Circles: only while waiting; green when that player is standing in theirs.
        if (ringA != null)
        {
            bool show = waiting;
            ringA.enabled = ringB.enabled = beamA.enabled = beamB.enabled = show;
            if (show)
            {
                SetColor(ringA, beamA, SideReady(true) ? circleReadyColor : circleColorA);
                SetColor(ringB, beamB, SideReady(false) ? circleReadyColor : circleColorB);
            }
        }

        // Walking / teleport only while waiting.
        bool allow = !lockMovementDuringMatch || waiting;
        if (Time.time >= nextMovementSearch)
        {
            FindMovement();
            nextMovementSearch = Time.time + 2f; // controllers can wake up late
            movementAllowed = !allow;            // force re-apply to new components
        }
        if (allow != movementAllowed)
        {
            foreach (Behaviour b in movement)
                if (b != null) b.enabled = allow;
            movementAllowed = allow;
        }
    }

    private static bool SideReady(bool sideA)
    {
        foreach (NetworkAvatarPlayer p in NetworkAvatarPlayer.All)
            if (p != null && p.IsSpawned && p.IsSideA == sideA)
                return p.HeadTarget != null && IsOnSpot(sideA, p.HeadTarget.position);
        return !sideA && SoloOpponentBot.Instance != null; // the practice opponent stands on its spot
    }

    private void FindMovement()
    {
        movement.Clear();
        XROrigin origin = FindAnyObjectByType<XROrigin>();
        if (origin == null) return;

        // Thumbstick move, turn, teleport, climb ... every locomotion type.
        foreach (LocomotionProvider p in origin.GetComponentsInChildren<LocomotionProvider>(true))
            movement.Add(p);
        // The teleport ray itself.
        foreach (XRRayInteractor r in origin.GetComponentsInChildren<XRRayInteractor>(true))
            if (r.name.ToLower().Contains("teleport"))
                movement.Add(r);
    }

    // ---------------- Drawing ----------------

    private LineRenderer MakeLine(string lineName, bool loop)
    {
        LineRenderer lr = new GameObject(lineName).AddComponent<LineRenderer>();
        lr.transform.SetParent(transform, false);
        lr.useWorldSpace = true;
        lr.loop = loop;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.widthMultiplier = loop ? 0.05f : 0.12f;
        lr.numCapVertices = 2;
        return lr;
    }

    private void DrawCircle(LineRenderer lr, Vector3 centre)
    {
        const int n = 48;
        lr.positionCount = n;
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            lr.SetPosition(i, centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * spotRadius + Vector3.up * 0.03f);
        }
    }

    private void DrawBeam(LineRenderer lr, Vector3 centre)
    {
        lr.positionCount = 2;
        lr.SetPosition(0, centre + Vector3.up * 0.03f);
        lr.SetPosition(1, centre + Vector3.up * beamHeight);
    }

    private static void SetColor(LineRenderer ring, LineRenderer beam, Color c)
    {
        // Gentle pulse so the circle catches the eye.
        float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * 3f);
        Color ringColor = c * pulse; ringColor.a = 1f;
        ring.startColor = ring.endColor = ringColor;
        Color bottom = c; bottom.a = 0.45f;
        Color top = c; top.a = 0f;
        beam.startColor = bottom;
        beam.endColor = top;
    }
}
