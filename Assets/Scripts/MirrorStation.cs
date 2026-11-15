using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The "Choose your look" board by the garden gate.
/// It shows a picture of every character (from AvatarCatalog) in a wooden frame.
/// Touch a picture with either hand to become that character; the chosen one is highlighted.
/// A small side board changes the accessory (only shown when accessories are added).
/// Changes are only allowed before a match.
///
/// Put it on an EMPTY object where the board should stand:
///  - position = the bottom middle of the board (on the grass),
///  - blue arrow (Z) = the way the board faces, i.e. toward where the player stands.
/// </summary>
public class MirrorStation : MonoBehaviour
{
    [Header("Board")]
    public float width = 1.0f;
    public float height = 1.9f;
    [Tooltip("Height of the bottom of the picture area above the grass.")]
    public float bottom = 0.15f;
    public int columns = 2;

    [Header("Look")]
    [Tooltip("Optional: a material for the wood. Empty = plain colour.")]
    public Material frameMaterial;
    public Color frameColor = new Color(0.55f, 0.36f, 0.2f);
    public Color pictureBackground = new Color(0.85f, 0.93f, 1f);
    public Color highlightColor = new Color(1f, 0.85f, 0.2f);
    public Color buttonColor = new Color(0.95f, 0.85f, 0.55f);
    public Color pressedColor = new Color(0.4f, 0.9f, 0.4f);
    public AudioClip pressSound;

    [Header("Touch")]
    [Tooltip("How close (metres) a hand must come to touch a picture or button.")]
    public float pressDistance = 0.08f;

    private class Tile
    {
        public Vector3 centre;   // board-local
        public Vector2 half;
        public bool armed = true;
    }

    private class Button
    {
        public Transform t;
        public Renderer r;
        public Vector3 restLocal;
        public System.Action action;
        public bool armed = true;
        public float pressedUntil;
    }

    private Tile[] tiles;
    private Transform highlight;
    private Material highlightMat;
    private float flashUntil;
    private RenderTexture atlas;
    private TextMeshPro accessoryLabel;
    private Transform sideBoard;
    private Button[] buttons;
    private AudioSource audioSource;

    // ---------------- Build ----------------

    private void Start()
    {
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 1f;
        audioSource.playOnAwake = false;

        BuildFrame();
        BuildSideBoard();

        AvatarCatalog c = AvatarCatalog.Instance;
        if (c != null && c.CharacterCount > 0)
            StartCoroutine(BuildPictures(c));
    }

    private void OnDestroy()
    {
        if (atlas != null) atlas.Release();
    }

    private Transform Box(string boxName, Transform parent, Vector3 localPos, Vector3 size, Color c, bool keepCollider)
    {
        GameObject g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        g.name = boxName;
        g.transform.SetParent(parent, false);
        g.transform.localPosition = localPos;
        g.transform.localScale = size;
        Renderer r = g.GetComponent<Renderer>();
        // Copy the project's default material (always works in builds), or the one you chose.
        Material m = new Material(frameMaterial != null ? frameMaterial : r.sharedMaterial);
        m.color = c;
        r.sharedMaterial = m;
        if (!keepCollider) Destroy(g.GetComponent<Collider>());
        return g.transform;
    }

    private void BuildFrame()
    {
        float t = 0.07f, d = 0.06f, top = bottom + height;
        Box("Frame left", transform, new Vector3(-(width + t) / 2f, (bottom + top) / 2f, -0.02f), new Vector3(t, height + 2 * t, d), frameColor, true);
        Box("Frame right", transform, new Vector3((width + t) / 2f, (bottom + top) / 2f, -0.02f), new Vector3(t, height + 2 * t, d), frameColor, true);
        Box("Frame top", transform, new Vector3(0f, top + t / 2f, -0.02f), new Vector3(width, t, d), frameColor, true);
        Box("Frame bottom", transform, new Vector3(0f, bottom - t / 2f, -0.02f), new Vector3(width, t, d), frameColor, true);
        Box("Back", transform, new Vector3(0f, (bottom + top) / 2f, -0.06f), new Vector3(width, height, 0.02f), frameColor, true);
        Box("Foot", transform, new Vector3(0f, 0.04f, -0.05f), new Vector3(width + 0.3f, 0.08f, 0.45f), frameColor, true);

        TextMeshPro title = Label("Title", transform, new Vector3(0f, top + 0.22f, 0f), 1.4f);
        title.text = "Choose your look";
        TextMeshPro hint = Label("Hint", transform, new Vector3(0f, top + 0.1f, 0f), 0.6f);
        hint.text = "touch a picture";
    }

    // Side board with < > for the accessory, to the player's right.
    private void BuildSideBoard()
    {
        sideBoard = new GameObject("Accessory board").transform;
        sideBoard.SetParent(transform, false);
        sideBoard.localPosition = new Vector3(-(width / 2f + 0.45f), 0f, 0.15f);
        sideBoard.localRotation = Quaternion.Euler(0f, 20f, 0f); // turned a little toward the player

        Box("Post", sideBoard, new Vector3(0f, 0.45f, -0.03f), new Vector3(0.06f, 0.9f, 0.06f), frameColor, true);
        Box("Board", sideBoard, new Vector3(0f, 1.0f, -0.03f), new Vector3(0.42f, 0.3f, 0.03f), frameColor, false);
        accessoryLabel = Label("Accessory label", sideBoard, new Vector3(0f, 1.08f, 0f), 0.45f);

        // The player looks along -Z, so their left is +X: "<" goes on +X.
        buttons = new[]
        {
            MakeButton(sideBoard, new Vector3(0.12f, 0.96f, 0.01f), "<", () => NetworkAvatarPlayer.Local?.ChangeAccessory(-1)),
            MakeButton(sideBoard, new Vector3(-0.12f, 0.96f, 0.01f), ">", () => NetworkAvatarPlayer.Local?.ChangeAccessory(1)),
        };
    }

    private Button MakeButton(Transform parent, Vector3 localPos, string symbol, System.Action action)
    {
        Transform b = Box("Button " + symbol, parent, localPos, new Vector3(0.09f, 0.09f, 0.04f), buttonColor, false);
        TextMeshPro t = Label("Symbol", parent, localPos + new Vector3(0f, 0f, 0.025f), 0.7f);
        t.text = symbol;
        t.color = Color.black;
        t.outlineWidth = 0f;
        return new Button { t = b, r = b.GetComponent<Renderer>(), restLocal = localPos, action = action };
    }

    private static TextMeshPro Label(string labelName, Transform parent, Vector3 localPos, float fontSize)
    {
        TextMeshPro t = new GameObject(labelName).AddComponent<TextMeshPro>();
        t.transform.SetParent(parent, false);
        t.transform.localPosition = localPos;
        t.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // readable from the front
        t.alignment = TextAlignmentOptions.Center;
        t.fontSize = fontSize;
        t.rectTransform.sizeDelta = new Vector2(1.2f, 0.3f);
        t.color = Color.white;
        t.outlineWidth = 0.2f;
        t.outlineColor = Color.black;
        return t;
    }

    // ---------------- Character pictures ----------------

    private IEnumerator BuildPictures(AvatarCatalog c)
    {
        int n = c.CharacterCount;
        int cols = Mathf.Max(1, columns);
        int rows = Mathf.CeilToInt(n / (float)cols);
        const int tilePxW = 512, tilePxH = 384;

        atlas = new RenderTexture(tilePxW * cols, tilePxH * rows, 24) { name = "Character pictures", antiAliasing = 4 };

        // Take a "photo" of each character, far below the garden where nobody can see it.
        GameObject[] models = new GameObject[n];
        Camera[] cams = new Camera[n];
        for (int i = 0; i < n; i++)
        {
            GameObject prefab = c.GetCharacter(i);
            if (prefab == null) continue;

            Vector3 spot = new Vector3(i * 6f, -300f, 0f);
            models[i] = Instantiate(prefab, spot, Quaternion.identity); // faces +Z
            models[i].name = "Picture model " + (i + 1);

            Bounds b = new Bounds(spot + Vector3.up, Vector3.one);
            Renderer[] rs = models[i].GetComponentsInChildren<Renderer>();
            if (rs.Length > 0) { b = rs[0].bounds; foreach (Renderer r in rs) b.Encapsulate(r.bounds); }

            // Head and shoulders: aim at the upper part of the body.
            float h = b.size.y;
            Vector3 look = new Vector3(b.center.x, b.max.y - 0.28f * h, b.center.z);
            float frameHeight = 0.6f * h;
            const float fov = 30f;
            float dist = frameHeight / 2f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);

            int col = i % cols, rowFromTop = i / cols;
            Camera cam = new GameObject("Picture camera " + (i + 1)).AddComponent<Camera>();
            cam.transform.SetPositionAndRotation(look + Vector3.forward * dist + Vector3.up * 0.05f * h,
                                                 Quaternion.LookRotation(Vector3.back));
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = dist + 3f * h;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = pictureBackground;
            cam.targetTexture = atlas;
            cam.rect = new Rect(col / (float)cols, (rows - 1 - rowFromTop) / (float)rows, 1f / cols, 1f / rows);
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.depth = -20;
            UniversalAdditionalCameraData data = cam.GetUniversalAdditionalCameraData();
            data.allowXRRendering = false;
            data.renderShadows = false;
            cams[i] = cam;
        }

        // Let the characters settle into their idle pose, then keep the photos.
        for (int f = 0; f < 4; f++) yield return null;
        yield return new WaitForEndOfFrame();

        for (int i = 0; i < n; i++)
        {
            if (cams[i] != null) Destroy(cams[i].gameObject);
            if (models[i] != null) Destroy(models[i]);
        }

        BuildTiles(n, cols, rows);
    }

    private void BuildTiles(int n, int cols, int rows)
    {
        const float gap = 0.04f;
        float tileW = (width - (cols + 1) * gap) / cols;
        float tileH = (height - (rows + 1) * gap) / rows;
        float top = bottom + height;
        const float z = -0.035f; // just in front of the back board, inside the frame

        Material pictureMat = new Material(Shader.Find("Sprites/Default")) { mainTexture = atlas };
        highlightMat = new Material(Shader.Find("Sprites/Default")) { color = highlightColor };

        tiles = new Tile[n];
        for (int i = 0; i < n; i++)
        {
            int col = i % cols, rowFromTop = i / cols;
            // The player looks along -Z, so their left is +X: the first column goes on +X.
            float x = width / 2f - gap - tileW / 2f - col * (tileW + gap);
            float y = top - gap - tileH / 2f - rowFromTop * (tileH + gap);
            Vector3 centre = new Vector3(x, y, z);

            float u0 = col / (float)cols, u1 = (col + 1) / (float)cols;
            float v0 = (rows - 1 - rowFromTop) / (float)rows, v1 = (rows - rowFromTop) / (float)rows;
            Quad("Picture " + (i + 1), centre, tileW, tileH, pictureMat, u0, u1, v0, v1);

            tiles[i] = new Tile { centre = centre, half = new Vector2(tileW / 2f, tileH / 2f) };
        }

        highlight = Quad("Chosen", Vector3.zero, tileW + 0.03f, tileH + 0.03f, highlightMat, 0f, 1f, 0f, 1f);
    }

    // A flat picture facing the player (+Z side), not mirrored.
    private Transform Quad(string quadName, Vector3 localCentre, float w, float h, Material mat,
                           float u0, float u1, float v0, float v1)
    {
        Transform q = new GameObject(quadName).transform;
        q.SetParent(transform, false);
        q.localPosition = localCentre;

        float hw = w / 2f, hh = h / 2f;
        Mesh m = new Mesh { name = quadName };
        // Seen from the front, the player's left is +X.
        m.vertices = new[] { new Vector3(hw, -hh, 0f), new Vector3(-hw, -hh, 0f), new Vector3(hw, hh, 0f), new Vector3(-hw, hh, 0f) };
        m.uv = new[] { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u0, v1), new Vector2(u1, v1) };
        m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        m.triangles = new[] { 0, 1, 2, 2, 1, 3 };
        m.RecalculateBounds();
        q.gameObject.AddComponent<MeshFilter>().sharedMesh = m;
        MeshRenderer mr = q.gameObject.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return q;
    }

    // ---------------- Every frame ----------------

    private void LateUpdate()
    {
        NetworkAvatarPlayer me = NetworkAvatarPlayer.Local;
        AvatarCatalog c = AvatarCatalog.Instance;

        // Highlight the chosen character (flashes green for a moment after a touch).
        if (highlight != null && tiles != null)
        {
            int chosen = me != null ? me.CurrentCharacter : -1;
            bool show = chosen >= 0 && chosen < tiles.Length;
            highlight.gameObject.SetActive(show);
            if (show) highlight.localPosition = tiles[chosen].centre + new Vector3(0f, 0f, -0.005f);
            highlightMat.color = Time.time < flashUntil ? pressedColor : highlightColor;
        }

        // Accessory board only when accessories exist.
        bool accessories = c != null && c.AccessoryCount > 0;
        if (sideBoard.gameObject.activeSelf != accessories) sideBoard.gameObject.SetActive(accessories);
        if (accessories)
        {
            int acc = me != null ? me.CurrentAccessory : 0;
            AvatarCatalog.Accessory a = acc > 0 ? c.GetAccessory(acc - 1) : null;
            accessoryLabel.text = "Accessory: " + (a != null ? a.name : "None");
        }

        if (me == null || me.LeftHandTarget == null || me.RightHandTarget == null) return;
        UpdatePictureTouch(me);
        if (accessories) UpdateButtons(me);
    }

    private void UpdatePictureTouch(NetworkAvatarPlayer me)
    {
        if (tiles == null) return;
        Vector3 l = transform.InverseTransformPoint(me.LeftHandTarget.position);
        Vector3 r = transform.InverseTransformPoint(me.RightHandTarget.position);

        for (int i = 0; i < tiles.Length; i++)
        {
            Tile t = tiles[i];
            bool lt = Touching(t, l), rt = Touching(t, r);
            if (t.armed && (lt || rt))
            {
                t.armed = false;
                me.SetCharacter(i);
                flashUntil = Time.time + 0.3f;
                if (pressSound != null) audioSource.PlayOneShot(pressSound);
                Buzz(lt);
            }
            else if (!t.armed && !Near(t, l) && !Near(t, r))
            {
                t.armed = true; // hand moved away: ready for the next touch
            }
        }
    }

    private bool Touching(Tile t, Vector3 p) =>
        Mathf.Abs(p.x - t.centre.x) < t.half.x && Mathf.Abs(p.y - t.centre.y) < t.half.y &&
        p.z - t.centre.z < pressDistance && p.z - t.centre.z > -0.1f;

    private bool Near(Tile t, Vector3 p) =>
        Mathf.Abs(p.x - t.centre.x) < t.half.x + 0.05f && Mathf.Abs(p.y - t.centre.y) < t.half.y + 0.05f &&
        p.z - t.centre.z < pressDistance * 2f && p.z - t.centre.z > -0.15f;

    private void UpdateButtons(NetworkAvatarPlayer me)
    {
        Transform l = me.LeftHandTarget, r = me.RightHandTarget;
        foreach (Button b in buttons)
        {
            float dl = Vector3.Distance(l.position, b.t.position), dr = Vector3.Distance(r.position, b.t.position);
            float dist = Mathf.Min(dl, dr);

            if (b.armed && dist < pressDistance)
            {
                b.armed = false;
                b.pressedUntil = Time.time + 0.25f;
                b.action?.Invoke();
                if (pressSound != null) audioSource.PlayOneShot(pressSound);
                Buzz(dl < dr);
            }
            else if (!b.armed && dist > pressDistance * 1.8f)
            {
                b.armed = true;
            }

            bool down = Time.time < b.pressedUntil;
            b.t.localPosition = b.restLocal + (down ? new Vector3(0f, 0f, -0.015f) : Vector3.zero);
            b.r.sharedMaterial.color = down ? pressedColor : buttonColor;
        }
    }

    private static void Buzz(bool left)
    {
        UnityEngine.XR.InputDevice d = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(
            left ? UnityEngine.XR.XRNode.LeftHand : UnityEngine.XR.XRNode.RightHand);
        if (d.isValid) d.SendHapticImpulse(0, 0.5f, 0.06f);
    }
}
