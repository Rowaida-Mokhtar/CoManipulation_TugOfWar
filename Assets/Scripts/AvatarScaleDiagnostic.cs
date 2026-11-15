
using UnityEngine;

public class AvatarScaleDiagnostic : MonoBehaviour
{
    [ContextMenu("Log Avatar World Height")]
    private void LogAvatarWorldHeight()
    {
        Renderer[] renderers =
            GetComponentsInChildren<Renderer>(true);

        if (renderers.Length == 0)
        {
            Debug.LogWarning("No renderers found under " + name);
            return;
        }

        Bounds bounds = renderers[0].bounds;

        foreach (Renderer renderer in renderers)
        {
            bounds.Encapsulate(renderer.bounds);
        }

        Debug.Log(
            $"Avatar: {name}\n" +
            $"World height: {bounds.size.y:F3} metres\n" +
            $"Root local scale: {transform.localScale}",
            this
        );
    }
}
