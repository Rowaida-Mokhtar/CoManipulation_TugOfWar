#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Menu: Tug Of War > Set Up Garden Lobby.
/// Places Spawn A / Spawn B just inside the garden gate (on the stone path) and the
/// mirror on the open grass beside the path, fills the Avatar Catalog with the
/// 10 Hodaart characters and connects everything. Positions were measured from garden.glb.
/// Safe to run again: it just moves the objects back to these spots.
/// </summary>
public static class SetUpGardenLobby
{
    private const string CharacterFolder = "Assets/Hodaart/HodaartLowPolyCharacterCollection3/Prefabs/";
    private const float Grass = 0.1f; // height of the grass (where the characters' feet are)

    [MenuItem("Tug Of War/Set Up Garden Lobby")]
    public static void Run()
    {
        TugOfWarController controller = Object.FindAnyObjectByType<TugOfWarController>();
        if (controller == null)
        {
            EditorUtility.DisplayDialog("Set Up Garden Lobby", "Open the TugOfWarPrototype_NetworkTest scene first.", "OK");
            return;
        }
        GameObject manager = controller.gameObject;

        // Gate spawns: on the stone path just inside the gate, facing into the garden.
        Transform spawnA = Place("Spawn A", new Vector3(-6.6f, Grass, 14.8f), 170f);
        Transform spawnB = Place("Spawn B", new Vector3(-6.4f, Grass, 13.6f), 170f);

        // Mirror: open grass to the left of the path (as you walk in), facing the path.
        Transform mirror = Place("Mirror", new Vector3(-10.5f, Grass, 11.2f), 90f);
        if (mirror.GetComponent<MirrorStation>() == null)
            Undo.AddComponent<MirrorStation>(mirror.gameObject);

        // Lobby on the manager, with the two spawns.
        GardenLobby lobby = manager.GetComponent<GardenLobby>();
        if (lobby == null) lobby = Undo.AddComponent<GardenLobby>(manager);
        Undo.RecordObject(lobby, "Set up lobby");
        lobby.spawnA = spawnA;
        lobby.spawnB = spawnB;

        // Character list.
        AvatarCatalog catalog = manager.GetComponent<AvatarCatalog>();
        if (catalog == null) catalog = Undo.AddComponent<AvatarCatalog>(manager);
        Undo.RecordObject(catalog, "Fill characters");
        GameObject[] chars = new GameObject[10];
        int found = 0;
        for (int i = 0; i < 10; i++)
        {
            chars[i] = AssetDatabase.LoadAssetAtPath<GameObject>($"{CharacterFolder}Character {i + 1:00}.prefab");
            if (chars[i] != null) found++;
        }
        catalog.characters = chars;

        EditorUtility.SetDirty(lobby);
        EditorUtility.SetDirty(catalog);
        EditorSceneManager.MarkSceneDirty(manager.scene);
        Selection.activeGameObject = mirror.gameObject;
        SceneView.lastActiveSceneView?.FrameSelected();

        EditorUtility.DisplayDialog("Set Up Garden Lobby",
            $"Done!\n\n- Spawn A and Spawn B are just inside the gate.\n- The mirror is beside the path.\n" +
            $"- {found} of 10 characters added.\n\nPress Ctrl+S to save the scene.", "OK");
    }

    private static Transform Place(string objectName, Vector3 position, float yaw)
    {
        GameObject go = GameObject.Find(objectName);
        if (go == null)
        {
            go = new GameObject(objectName);
            Undo.RegisterCreatedObjectUndo(go, "Create " + objectName);
        }
        Undo.RecordObject(go.transform, "Place " + objectName);
        go.transform.SetParent(null, true);
        go.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        return go.transform;
    }
}
#endif
