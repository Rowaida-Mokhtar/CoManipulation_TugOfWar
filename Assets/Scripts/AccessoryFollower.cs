using UnityEngine;

/// <summary>
/// Keeps an accessory on a character's head every frame (after the arm/head IK).
/// Added automatically by NetworkAvatarPlayer.
/// </summary>
[DefaultExecutionOrder(150)]
public class AccessoryFollower : MonoBehaviour
{
    public AvatarCatalog.Accessory settings;
    public VRAvatarIK ik;

    private static readonly System.Collections.Generic.List<AccessoryFollower> ownAccessories =
        new System.Collections.Generic.List<AccessoryFollower>();

    private Renderer[] renderers;
    private Vector3 baseScale;
    private Vector3 topOfHead;   // head-top offset in the head's frame, at character size 1
    private bool isOwn;

    /// <summary>Call once after creating it. Your own accessory is hidden from your eyes (like your head).</summary>
    public void Init(bool ownAvatar)
    {
        renderers = GetComponentsInChildren<Renderer>(true);
        baseScale = transform.localScale;
        foreach (Collider c in GetComponentsInChildren<Collider>(true))
            Destroy(c); // never push the player around

        // Measure the top of the head once, while the character is still in its rest pose.
        Transform head = ik.HeadBone;
        Transform top = null;
        foreach (Transform t in head.GetComponentsInChildren<Transform>(true))
            if (t.name.Contains("HeadTop")) { top = t; break; }
        float s = Mathf.Max(ik.transform.lossyScale.y, 0.0001f);
        topOfHead = top != null
            ? Quaternion.Inverse(HeadFrame()) * (top.position - head.position) / s
            : new Vector3(0f, 0.2f, 0f);

        isOwn = ownAvatar;
        if (isOwn)
        {
            ownAccessories.Add(this);
            SetRenderers(false);
        }
    }

    private void OnDestroy() => ownAccessories.Remove(this);

    /// <summary>The mirror shows your own accessory only while it draws.</summary>
    public static void SetOwnVisible(bool visible)
    {
        foreach (AccessoryFollower f in ownAccessories)
            if (f != null) f.SetRenderers(visible);
    }

    private void SetRenderers(bool visible)
    {
        if (renderers == null) return;
        foreach (Renderer r in renderers)
            if (r != null) r.enabled = visible;
    }

    // The head's rotation, but with the character's own axes (x right, y up, z forward).
    private Quaternion HeadFrame() => ik.HeadBone.rotation * Quaternion.Inverse(ik.HeadRestOffset);

    private void LateUpdate()
    {
        if (ik == null || ik.HeadBone == null || settings == null) return;

        Quaternion frame = HeadFrame();
        float s = ik.transform.lossyScale.y;
        Vector3 anchor = settings.attachTo == AvatarCatalog.AttachPoint.TopOfHead
            ? ik.HeadBone.position + frame * (topOfHead * s)
            : ik.HeadBone.position + frame * (new Vector3(0f, 0.07f, 0.11f) * s);

        transform.SetPositionAndRotation(anchor + frame * (settings.offset * s),
                                         frame * Quaternion.Euler(settings.rotation));
        // The accessory sits under the character, so its size is relative to the character.
        transform.localScale = baseScale * Mathf.Max(settings.size, 0.01f);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => ownAccessories.Clear();
}
