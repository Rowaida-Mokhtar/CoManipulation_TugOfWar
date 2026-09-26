using UnityEngine;

public class TugOfWarController : MonoBehaviour
{
    [Header("Rope")]
    public Transform rope;

    [Header("Balance Marker")]
    public Transform balanceMarker;

    [Header("Players")]
    public Transform playerA;
    public Transform playerB;

    [Header("Player Strength")]
    public float playerAStrength = 1f;
    public float playerBStrength = 1f;

    [Header("Movement")]
    public float ropeSpeed = 2f;
    public float maxRopeOffset = 2.5f;

    private float ropeOffset = 0f;

    void Update()
    {
        // -----------------------------
        // 1. READ PLAYER INPUT
        // -----------------------------

        float inputA = Input.GetKey(KeyCode.A)
            ? playerAStrength
            : 0f;

        float inputB = Input.GetKey(KeyCode.D)
            ? playerBStrength
            : 0f;


        // -----------------------------
        // 2. CALCULATE INPUT BALANCE
        // -----------------------------

        float balance = inputA - inputB;


        // -----------------------------
        // 3. UPDATE BALANCE
        // -----------------------------

        ropeOffset += balance * ropeSpeed * Time.deltaTime;

        ropeOffset = Mathf.Clamp(
            ropeOffset,
            -maxRopeOffset,
            maxRopeOffset
        );


        // -----------------------------
        // 4. KEEP ROPE CONNECTED
        // -----------------------------

        if (playerA != null &&
            playerB != null &&
            rope != null)
        {
            Vector3 positionA = playerA.position;
            Vector3 positionB = playerB.position;

            Vector3 direction =
                (positionB - positionA).normalized;

            float distance =
                Vector3.Distance(positionA, positionB);

            // Put the rope exactly halfway
            // between the two players.
            Vector3 ropeCenter =
                (positionA + positionB) / 2f;

            rope.position = ropeCenter;

            // Rotate cylinder so its length
            // points from A toward B.
            rope.rotation =
                Quaternion.FromToRotation(
                    Vector3.up,
                    direction
                );

            // Keep the rope exactly between
            // the two players.
            Vector3 scale = rope.localScale;
            scale.y = distance / 2f;
            rope.localScale = scale;
        }


        // -----------------------------
        // 5. MOVE BALANCE MARKER
        // -----------------------------

        if (balanceMarker != null &&
            playerA != null &&
            playerB != null)
        {
            Vector3 positionA = playerA.position;
            Vector3 positionB = playerB.position;

            Vector3 direction =
                (positionB - positionA).normalized;

            Vector3 center =
                (positionA + positionB) / 2f;

            // The marker moves according
            // to the input balance.
            center += direction * ropeOffset;

            // Keep marker at its existing height.
            center.y = balanceMarker.position.y;

            balanceMarker.position = center;
        }
    }
}