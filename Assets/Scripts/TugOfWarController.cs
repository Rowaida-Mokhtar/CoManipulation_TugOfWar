using UnityEngine;
using UnityEngine.InputSystem;

public class TugOfWarController : MonoBehaviour
{
    [Header("Rope")]
    public Transform rope;

    [Header("Player Strength")]
    public float playerAStrength = 1f;
    public float playerBStrength = 1f;

    [Header("Movement")]
    public float ropeSpeed = 2f;
    public float maxRopeOffset = 2.5f;

    private float ropeOffset = 0f;

    private float inputA = 0f;
    private float inputB = 0f;
    private float balance = 0f;

    void Update()
    {
        // Player A pulls with the A key
        inputA = Keyboard.current.aKey.isPressed
            ? playerAStrength
            : 0f;

        // Player B pulls with the D key
        inputB = Keyboard.current.dKey.isPressed
            ? playerBStrength
            : 0f;

        // Calculate input balance
        // Positive = Player A is pulling harder
        // Negative = Player B is pulling harder
        // Zero = balanced
        balance = inputA - inputB;

        // Move the shared rope according to the input balance
        ropeOffset += balance * ropeSpeed * Time.deltaTime;

        // Prevent the rope from moving too far
        ropeOffset = Mathf.Clamp(
            ropeOffset,
            -maxRopeOffset,
            maxRopeOffset
        );

        // Update the rope position
        Vector3 position = rope.position;

        // Players are separated along the Z axis
        position.z = ropeOffset;

        rope.position = position;
    }

    void OnGUI()
    {
        // Main information panel
        GUI.Box(
            new Rect(20, 20, 300, 190),
            "CO-MANIPULATION TUG OF WAR"
        );

        // Player A input
        GUI.Label(
            new Rect(40, 55, 250, 25),
            "Player A - Hold A: " + inputA.ToString("F1")
        );

        // Player B input
        GUI.Label(
            new Rect(40, 80, 250, 25),
            "Player B - Hold D: " + inputB.ToString("F1")
        );

        // Input balance
        GUI.Label(
            new Rect(40, 110, 250, 25),
            "Input Balance: " + balance.ToString("F1")
        );

        // Status message
        string status;

        if (balance > 0)
        {
            status = "<- PLAYER A PULLING";
        }
        else if (balance < 0)
        {
            status = "PLAYER B PULLING ->";
        }
        else
        {
            status = "CENTER / BALANCED";
        }

        GUI.Label(
            new Rect(40, 145, 250, 25),
            status
        );
    }
}