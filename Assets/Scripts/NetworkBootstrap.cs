using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

/// <summary>
/// Simple Host / Join panel shown on the PC monitor (not inside the headset).
/// Put it on the NetworkManager object.
///  - Host PC: click "Host (Player A)". It listens on all network cards.
///  - Other PC: type the host PC's IP address, click "Join (Player B)".
/// Keyboard shortcuts: H = host, J = join, Esc = disconnect.
/// </summary>
public class NetworkBootstrap : MonoBehaviour
{
    [Tooltip("Address the client connects to. 127.0.0.1 = same computer.")]
    public string hostAddress = "127.0.0.1";
    public ushort port = 7777;
    public bool showPanel = true;

    private string status = "Not connected";

    private void Update()
    {
        if (NetworkManager.Singleton == null)
            return;

        bool connected = NetworkManager.Singleton.IsListening;

        if (!connected && Input.GetKeyDown(KeyCode.H)) StartHost();
        if (!connected && Input.GetKeyDown(KeyCode.J)) StartClient();
        if (connected && Input.GetKeyDown(KeyCode.Escape)) Disconnect();
    }

    public void StartHost()
    {
        UnityTransport transport = GetTransport();
        if (transport == null) return;

        // Listen on every network interface so the other PC can reach us.
        transport.SetConnectionData("127.0.0.1", port, "0.0.0.0");
        status = NetworkManager.Singleton.StartHost()
            ? $"Hosting on port {port} — tell Player B this PC's IP address"
            : "Failed to start host (port in use?)";
        Debug.Log(status);
    }

    public void StartClient()
    {
        UnityTransport transport = GetTransport();
        if (transport == null) return;

        transport.SetConnectionData(hostAddress.Trim(), port);
        status = NetworkManager.Singleton.StartClient()
            ? $"Connecting to {hostAddress}:{port}..."
            : "Failed to start client";
        Debug.Log(status);
    }

    public void Disconnect()
    {
        NetworkManager.Singleton.Shutdown();
        status = "Disconnected";
    }

    private UnityTransport GetTransport()
    {
        if (NetworkManager.Singleton == null)
        {
            status = "No NetworkManager in scene";
            return null;
        }

        UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport == null)
            status = "NetworkManager has no UnityTransport";
        return transport;
    }

    private void OnGUI()
    {
        if (!showPanel || NetworkManager.Singleton == null)
            return;

        NetworkManager nm = NetworkManager.Singleton;

        GUILayout.BeginArea(new Rect(10, 10, 320, 220), GUI.skin.box);

        if (!nm.IsListening)
        {
            if (GUILayout.Button("Host (Player A)  [H]", GUILayout.Height(36)))
                StartHost();

            GUILayout.Space(6);
            GUILayout.Label("Host IP address:");
            hostAddress = GUILayout.TextField(hostAddress);

            if (GUILayout.Button("Join (Player B)  [J]", GUILayout.Height(36)))
                StartClient();
        }
        else
        {
            string role = nm.IsHost ? "HOST (Player A)" : "CLIENT (Player B)";
            GUILayout.Label($"Running as {role}");
            if (nm.IsServer)
                GUILayout.Label($"Connected players: {nm.ConnectedClientsIds.Count}");
            else
                GUILayout.Label(nm.IsConnectedClient ? "Connected to host" : "Connecting...");

            if (GUILayout.Button("Disconnect  [Esc]", GUILayout.Height(30)))
                Disconnect();
        }

        GUILayout.Label(status);
        GUILayout.EndArea();
    }
}
