using UnityEngine;

/// <summary>
/// The characters and accessories players can choose at the mirror.
/// Put it on the TugOfWarManager object and drag in:
///  - Characters: the Hodaart prefabs (Assets/Hodaart/.../Prefabs/Character 01 ... 10).
///  - Accessories: any hat / glasses / crown model (prefab, .fbx or .glb).
/// The choice is shared over the network, so the other player sees it too.
/// Study note: each participant chooses once at the start and keeps the same look
/// in both conditions (the look must not differ between conditions).
/// </summary>
public class AvatarCatalog : MonoBehaviour
{
    public enum AttachPoint { TopOfHead, Eyes }

    [System.Serializable]
    public class Accessory
    {
        public string name = "Hat";
        [Tooltip("The model (prefab, .fbx or .glb).")]
        public GameObject model;
        [Tooltip("Top Of Head for hats/crowns, Eyes for glasses/masks.")]
        public AttachPoint attachTo = AttachPoint.TopOfHead;
        [Tooltip("Fine-tune while playing (metres): x = right, y = up, z = forward.")]
        public Vector3 offset = Vector3.zero;
        [Tooltip("Fine-tune while playing (degrees).")]
        public Vector3 rotation = Vector3.zero;
        [Tooltip("Fine-tune while playing.")]
        public float size = 1f;
    }

    [Tooltip("Drag the 10 Hodaart character prefabs here.")]
    public GameObject[] characters;

    [Tooltip("Accessories to choose from. The first choice at the mirror is always 'None'.")]
    public Accessory[] accessories;

    public static AvatarCatalog Instance { get; private set; }

    private void Awake() => Instance = this;
    private void OnDestroy() { if (Instance == this) Instance = null; }

    public int CharacterCount => characters != null ? characters.Length : 0;
    public int AccessoryCount => accessories != null ? accessories.Length : 0;

    public GameObject GetCharacter(int i) =>
        characters != null && i >= 0 && i < characters.Length ? characters[i] : null;

    public Accessory GetAccessory(int i) =>
        accessories != null && i >= 0 && i < accessories.Length && accessories[i] != null &&
        accessories[i].model != null ? accessories[i] : null;

    /// <summary>Index of the character with this name (e.g. the scene's "Character 01"), or -1.</summary>
    public int IndexOfName(string characterName)
    {
        if (characters == null || string.IsNullOrEmpty(characterName)) return -1;
        string n = Clean(characterName);
        for (int i = 0; i < characters.Length; i++)
            if (characters[i] != null && Clean(characters[i].name) == n)
                return i;
        return -1;
    }

    private static string Clean(string s)
    {
        s = s.Replace("(Clone)", "");
        int bracket = s.IndexOf(" (");
        if (bracket > 0) s = s.Substring(0, bracket);
        return s.Trim().ToLowerInvariant();
    }
}
