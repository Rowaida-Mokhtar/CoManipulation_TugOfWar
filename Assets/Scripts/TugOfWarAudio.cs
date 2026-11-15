using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Game sounds for the networked tug-of-war. Put it on the TugOfWarManager object.
/// Every PC plays the sounds itself from the match state that is already shared,
/// so nothing extra is sent over the network.
///
/// - Garden ambience (birds), looping quietly.
/// - Countdown voice: "3", "2", "1", "Go!".
/// - Effort grunts while a player pulls hard, coming FROM that player's avatar (3D sound).
/// - "Look out!" from a player who is close to being dragged into the pond.
/// - Rope creak that gets louder the harder both players pull.
/// - "You win" / "You lose" for the player on this PC.
/// (The splash itself is TugOfWarController's existing "Splash Sound" slot.)
///
/// Every slot may be left empty: that sound is simply skipped.
/// Same sounds in every study condition (controlled variable).
/// </summary>
[RequireComponent(typeof(TugOfWarController))]
public class TugOfWarAudio : MonoBehaviour
{
    [Header("Ambience")]
    public AudioClip ambienceLoop;
    [Range(0f, 1f)] public float ambienceVolume = 0.25f;

    [Header("Countdown")]
    public AudioClip count3;
    public AudioClip count2;
    public AudioClip count1;
    public AudioClip go;
    [Range(0f, 1f)] public float announcerVolume = 0.9f;

    [Header("Effort grunts (from the pulling player)")]
    public AudioClip[] grunts;
    [Tooltip("Pull strength above which a player grunts.")]
    public float gruntPullThreshold = 1.2f;
    public Vector2 gruntGapSeconds = new Vector2(1.0f, 2.2f);
    [Range(0f, 1f)] public float gruntVolume = 0.8f;
    [Tooltip("Your own grunts are a bit quieter than your opponent's.")]
    [Range(0f, 1f)] public float ownGruntVolume = 0.45f;

    [Header("Danger shout (close to the pond)")]
    public AudioClip[] lookOutShouts;
    [Tooltip("How close to losing (0 = start, 1 = fully dragged) before the shout. Once per match.")]
    [Range(0f, 1f)] public float dangerLevel = 0.55f;
    [Range(0f, 1f)] public float shoutVolume = 0.9f;

    [Header("Rope creak (loop)")]
    public AudioClip ropeCreakLoop;
    [Tooltip("Combined pull of both players that gives full rope volume.")]
    public float ropeFullPull = 5f;
    [Range(0f, 1f)] public float ropeMaxVolume = 0.6f;

    [Header("Result (for the player on this PC)")]
    public AudioClip youWin;
    public AudioClip youLose;
    public float resultDelay = 0.8f;

    private TugOfWarController controller;
    private AudioSource ambience, announcer, rope;
    private readonly Dictionary<NetworkAvatarPlayer, AudioSource> voices = new Dictionary<NetworkAvatarPlayer, AudioSource>();
    private readonly Dictionary<NetworkAvatarPlayer, float> nextGrunt = new Dictionary<NetworkAvatarPlayer, float>();
    private bool shoutedA, shoutedB;
    private AudioSource botVoice;
    private float botNextGrunt;

    private AudioSource BotVoice()
    {
        SoloOpponentBot bot = SoloOpponentBot.Instance;
        if (bot == null || bot.Head == null) return null;
        if (botVoice == null || botVoice.transform != bot.Head)
        {
            botVoice = bot.Head.gameObject.AddComponent<AudioSource>();
            botVoice.playOnAwake = false;
            botVoice.spatialBlend = 1f;
            botVoice.minDistance = 1.5f;
            botVoice.maxDistance = 25f;
        }
        return botVoice;
    }
    private int lastCount = -1;
    private NetworkTugOfWar.MatchState lastState = NetworkTugOfWar.MatchState.Waiting;

    private void Awake()
    {
        controller = GetComponent<TugOfWarController>();

        ambience = Make2DSource("Ambience", true);
        announcer = Make2DSource("Announcer", false);

        rope = new GameObject("Rope creak").AddComponent<AudioSource>();
        rope.transform.SetParent(transform, false);
        rope.loop = true;
        rope.playOnAwake = false;
        rope.spatialBlend = 1f;
        rope.minDistance = 2f;
        rope.maxDistance = 20f;
        rope.volume = 0f;
    }

    private void OnEnable() => NetworkTugOfWar.MatchEnded += OnMatchEnded;
    private void OnDisable() => NetworkTugOfWar.MatchEnded -= OnMatchEnded;

    private void Start()
    {
        if (ambienceLoop != null)
        {
            ambience.clip = ambienceLoop;
            ambience.volume = ambienceVolume;
            ambience.Play();
        }

        if (ropeCreakLoop != null)
        {
            rope.clip = ropeCreakLoop;
            rope.Play();
        }
    }

    private AudioSource Make2DSource(string sourceName, bool loop)
    {
        AudioSource s = new GameObject(sourceName).AddComponent<AudioSource>();
        s.transform.SetParent(transform, false);
        s.loop = loop;
        s.playOnAwake = false;
        s.spatialBlend = 0f;
        return s;
    }

    private void Update()
    {
        NetworkTugOfWar match = NetworkTugOfWar.Instance;
        if (match == null || !match.IsSpawned)
        {
            if (rope.isPlaying) rope.volume = 0f;
            return;
        }

        NetworkTugOfWar.MatchState state = match.State;
        UpdateCountdown(match, state);

        if (state == NetworkTugOfWar.MatchState.Playing)
        {
            UpdateGrunts();
            UpdateDangerShouts();
        }

        UpdateRope(state);

        if (state != lastState && state == NetworkTugOfWar.MatchState.Countdown)
        {
            shoutedA = shoutedB = false;
            lastCount = -1;
        }
        lastState = state;
    }

    // ---------------- Countdown ----------------

    private void UpdateCountdown(NetworkTugOfWar match, NetworkTugOfWar.MatchState state)
    {
        if (state == NetworkTugOfWar.MatchState.Countdown)
        {
            int n = Mathf.CeilToInt(match.CountdownRemaining);
            if (n != lastCount && n >= 1 && n <= 3)
            {
                lastCount = n;
                Announce(n == 3 ? count3 : n == 2 ? count2 : count1);
            }
        }
        else if (state == NetworkTugOfWar.MatchState.Playing && lastState == NetworkTugOfWar.MatchState.Countdown)
        {
            Announce(go);
        }
    }

    private void Announce(AudioClip clip)
    {
        if (clip != null)
            announcer.PlayOneShot(clip, announcerVolume);
    }

    // ---------------- Grunts and shouts from the avatars ----------------

    private AudioSource VoiceFor(NetworkAvatarPlayer p)
    {
        if (voices.TryGetValue(p, out AudioSource s) && s != null)
            return s;
        if (p.HeadTarget == null)
            return null;

        s = p.HeadTarget.gameObject.AddComponent<AudioSource>();
        s.playOnAwake = false;
        // Your own voice sits "in your head"; the opponent's comes from across the pond.
        s.spatialBlend = p.IsOwner ? 0f : 1f;
        s.minDistance = 1.5f;
        s.maxDistance = 25f;
        voices[p] = s;
        return s;
    }

    private void UpdateGrunts()
    {
        if (grunts == null || grunts.Length == 0)
            return;

        foreach (NetworkAvatarPlayer p in NetworkAvatarPlayer.All)
        {
            if (p == null || !p.IsSpawned || p.PullInput < gruntPullThreshold)
                continue;
            if (nextGrunt.TryGetValue(p, out float t) && Time.time < t)
                continue;

            AudioSource s = VoiceFor(p);
            if (s == null) continue;

            s.pitch = Random.Range(0.92f, 1.08f);
            s.PlayOneShot(grunts[Random.Range(0, grunts.Length)], p.IsOwner ? ownGruntVolume : gruntVolume);
            nextGrunt[p] = Time.time + Random.Range(gruntGapSeconds.x, gruntGapSeconds.y);
        }

        // Practice opponent (solo tests)
        SoloOpponentBot bot = SoloOpponentBot.Instance;
        if (bot != null && bot.CurrentPull >= gruntPullThreshold && Time.time >= botNextGrunt)
        {
            AudioSource v = BotVoice();
            if (v != null)
            {
                v.pitch = Random.Range(0.85f, 1.0f);
                v.PlayOneShot(grunts[Random.Range(0, grunts.Length)], gruntVolume);
            }
            botNextGrunt = Time.time + Random.Range(gruntGapSeconds.x, gruntGapSeconds.y);
        }
    }

    private void UpdateDangerShouts()
    {
        if (lookOutShouts == null || lookOutShouts.Length == 0)
            return;

        // Positive progress = A winning, so B is in danger; negative = A in danger.
        float danger = controller.MatchProgress / Mathf.Max(controller.MaxRopeOffset, 0.01f);

        if (!shoutedA && -danger >= dangerLevel) { Shout(sideA: true); shoutedA = true; }
        if (!shoutedB && danger >= dangerLevel) { Shout(sideA: false); shoutedB = true; }
    }

    private void Shout(bool sideA)
    {
        foreach (NetworkAvatarPlayer p in NetworkAvatarPlayer.All)
        {
            if (p == null || !p.IsSpawned || p.IsSideA != sideA) continue;
            AudioSource s = VoiceFor(p);
            if (s == null) return;
            s.pitch = 1f;
            s.PlayOneShot(lookOutShouts[Random.Range(0, lookOutShouts.Length)], shoutVolume);
            return;
        }

        // No real player on that side: the practice opponent shouts (solo tests).
        if (!sideA)
        {
            AudioSource v = BotVoice();
            if (v != null)
            {
                v.pitch = 1f;
                v.PlayOneShot(lookOutShouts[Random.Range(0, lookOutShouts.Length)], shoutVolume);
            }
        }
    }

    // ---------------- Rope ----------------

    private void UpdateRope(NetworkTugOfWar.MatchState state)
    {
        if (!rope.isPlaying)
            return;

        float target = 0f;
        if (state == NetworkTugOfWar.MatchState.Playing)
        {
            float pull = 0f;
            foreach (NetworkAvatarPlayer p in NetworkAvatarPlayer.All)
                if (p != null && p.IsSpawned) pull += p.PullInput;
            if (SoloOpponentBot.Instance != null) pull += SoloOpponentBot.Instance.CurrentPull;
            target = Mathf.Clamp01(pull / Mathf.Max(ropeFullPull, 0.01f)) * ropeMaxVolume;
        }

        rope.volume = Mathf.MoveTowards(rope.volume, target, Time.deltaTime * 2f);
        if (controller.rope != null)
            rope.transform.position = controller.rope.position; // middle of the rope
    }

    // ---------------- Result ----------------

    private void OnMatchEnded(int loserSide)
    {
        foreach (NetworkAvatarPlayer p in NetworkAvatarPlayer.All)
        {
            if (p == null || !p.IsSpawned || !p.IsOwner) continue;
            bool iLost = (loserSide == 1) == p.IsSideA;
            AudioClip clip = iLost ? youLose : youWin;
            if (clip != null)
                StartCoroutine(PlayLater(clip, resultDelay));
            return;
        }
    }

    private System.Collections.IEnumerator PlayLater(AudioClip clip, float delay)
    {
        yield return new WaitForSeconds(delay);
        announcer.PlayOneShot(clip, announcerVolume);
    }
}
