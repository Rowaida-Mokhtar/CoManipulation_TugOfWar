
using UnityEngine;

// Runs after the avatars have moved, so the rope ends never lag behind the hands.
[DefaultExecutionOrder(200)]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class CurvedRopeVisual : MonoBehaviour
{
    [Header("Rope Endpoints")]
    public Transform playerA;
    public Transform playerB;

    [Tooltip("Optional: if set, the rope end attaches exactly here (e.g. a player's hands) instead of the player root.")]
    public Transform handA;
    public Transform handB;

    [Header("Hanging Shape")]
    [Tooltip("Height of each rope end above the player root.")]
    public float endHeightOffset = 0.55f;

    [Tooltip("How far the middle of the rope sags downward.")]
    public float sagDepth = 0.35f;

    [Header("Rope Appearance")]
    [Min(0.005f)]
    public float radius = 0.025f;

    [Range(4, 16)]
    public int sides = 8;

    [Min(8)]
    public int segments = 32;

    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh ropeMesh;

    private void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();

        // Keep the existing rope material.
        Material originalMaterial = meshRenderer.sharedMaterial;

        // Replace the imported straight mesh with a generated curved mesh.
        ropeMesh = new Mesh();
        ropeMesh.name = "Generated Curved Rope";
        ropeMesh.MarkDynamic();

        meshFilter.sharedMesh = ropeMesh;

        // Remove the unusual imported scale from the visual object.
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
        transform.localScale = Vector3.one;

        if (originalMaterial != null)
            meshRenderer.sharedMaterial = originalMaterial;
    }

    private void LateUpdate()
    {
        if ((playerA == null && handA == null) || (playerB == null && handB == null))
            return;

        Vector3 start = handA != null ? handA.position : playerA.position + Vector3.up * endHeightOffset;
        Vector3 end = handB != null ? handB.position : playerB.position + Vector3.up * endHeightOffset;

        GenerateRope(start, end);
    }

    private void GenerateRope(Vector3 start, Vector3 end)
    {
        int ringCount = segments + 1;
        int vertexCount = ringCount * (sides + 1);

        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uv = new Vector2[vertexCount];
        int[] triangles = new int[segments * sides * 6];

        Vector3 direction = (end - start).normalized;

        Vector3 referenceAxis =
            Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.95f
                ? Vector3.right
                : Vector3.up;

        Vector3 right =
            Vector3.Cross(direction, referenceAxis).normalized;

        Vector3 forward =
            Vector3.Cross(direction, right).normalized;

        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;

            // A shallow U-shaped curve, with the centre hanging lower.
            Vector3 point = Vector3.Lerp(start, end, t);
            point.y -= 4f * sagDepth * t * (1f - t);

            for (int j = 0; j <= sides; j++)
            {
                float angle = j / (float)sides * Mathf.PI * 2f;

                Vector3 radial =
                    right * Mathf.Cos(angle) +
                    forward * Mathf.Sin(angle);

                int index = i * (sides + 1) + j;

                vertices[index] =
                    transform.InverseTransformPoint(
                        point + radial * radius
                    );

                uv[index] = new Vector2(
                    j / (float)sides,
                    t * 4f
                );
            }
        }

        int triangleIndex = 0;

        for (int i = 0; i < segments; i++)
        {
            for (int j = 0; j < sides; j++)
            {
                int current = i * (sides + 1) + j;
                int next = current + sides + 1;

                triangles[triangleIndex++] = current;
                triangles[triangleIndex++] = next;
                triangles[triangleIndex++] = current + 1;

                triangles[triangleIndex++] = current + 1;
                triangles[triangleIndex++] = next;
                triangles[triangleIndex++] = next + 1;
            }
        }

        ropeMesh.Clear();
        ropeMesh.vertices = vertices;
        ropeMesh.uv = uv;
        ropeMesh.triangles = triangles;
        ropeMesh.RecalculateNormals();
        ropeMesh.RecalculateBounds();
    }

    private void OnDestroy()
    {
        if (ropeMesh != null)
            Destroy(ropeMesh);
    }
}
