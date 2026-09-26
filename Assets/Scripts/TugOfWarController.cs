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
        // Player A pulls with A
        inputA = Keyboard.current.aKey.isPressed
            ? playerAStrength
            : 0f;

        // Player B pulls with D
        inputB = Keyboard.current.dKey.isPressed
            ? playerBStrength
            : 0f;

        // Input balance
        balance = inputA - inputB;

        // Move the shared rope according to the balance
        ropeOffset += balance * ropeSpeed * Time.deltaTime;

        ropeOffset = Mathf.Clamp(
            ropeOffset,
            -maxRopeOffset,
            maxRopeOffset
        );

        Vector3 position = rope.position;

        // Players are separated along Z
        position.z = ropeOffset;

        rope.position = position;
    }

    void OnGUI()
    {
        GUI.Box(
            new Rect(20, 20, 260, 130),
            "TUG OF WAR - INPUT BALANCE"
        );

        GUI.Label(
            new Rect(40, 55, 220, 25),
            "Player A: " + inputA.ToString("F1")
        );

        GUI.Label(
            new Rect(40, 80, 220, 25),
            "Player B: " + inputB.ToString("F1")
        );

        GUI.Label(
            new Rect(40, 105, 220, 25),
            "Balance: " + balance.ToString("F1")
        );
    }
}