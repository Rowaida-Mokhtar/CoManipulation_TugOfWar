
using UnityEngine;
using Unity.Netcode;
using System.Collections;

public class NetworkPlayerPose : NetworkBehaviour
{
    [SerializeField] private Transform poseSource;
    [SerializeField] private Vector3 positionOffset = new Vector3(0f, -1.2f, 0f);

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
            StartCoroutine(FindLocalCamera());
    }

    private IEnumerator FindLocalCamera()
    {
        while (poseSource == null)
        {
            Camera localCamera = Camera.main;

            if (localCamera != null)
                poseSource = localCamera.transform;

            if (poseSource == null)
                yield return null;
        }
    }

    private void LateUpdate()
    {
        if (!IsSpawned || !IsOwner || poseSource == null)
            return;

        transform.position = poseSource.position + positionOffset;
        transform.rotation = Quaternion.Euler(
            0f, poseSource.eulerAngles.y, 0f
        );
    }
}
