using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Collections;

public class PuckSpawnSync : MonoBehaviour
{
    private const string SpawnPuckMessage = "CustomLevel_SpawnPuck";
    private Dictionary<ulong, Puck> playerPucks = new Dictionary<ulong, Puck>();

    private void Start()
    {
        if (NetworkManager.Singleton.IsServer)
        {
            NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
                SpawnPuckMessage, OnSpawnPuckMessageReceived);
            Debug.Log("[CustomLevel] Registered message handlers");
        }
    }

    private void OnDestroy()
    {
        NetworkManager.Singleton?.CustomMessagingManager?
            .UnregisterNamedMessageHandler(SpawnPuckMessage);
        // Clean up any pucks we were protecting in case they weren't explicitly despawned
        foreach (var puck in playerPucks.Values)
            if (puck != null) CustomLevelPlugin.ProtectedPucks.Remove(puck);
    }

    private void OnSpawnPuckMessageReceived(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out float px); reader.ReadValueSafe(out float py); reader.ReadValueSafe(out float pz);
        reader.ReadValueSafe(out float fx); reader.ReadValueSafe(out float fy); reader.ReadValueSafe(out float fz);

        Vector3 position = new Vector3(px, py, pz);
        Vector3 forward = new Vector3(fx, fy, fz);

        // Despawn the player's previous puck so they never hold more than one at a time.
        // Remove from ProtectedPucks first so our own despawn call isn't blocked by the guard.
        if (playerPucks.TryGetValue(senderClientId, out Puck prev) && prev != null)
        {
            CustomLevelPlugin.ProtectedPucks.Remove(prev);
            MonoBehaviourSingleton<PuckManager>.Instance.Server_DespawnPuck(prev);
            Debug.Log($"[CustomLevel] Despawned previous puck for client {senderClientId}");
        }

        // Place the puck 2 m in front of the player, then raycast down to find the floor surface.
        // Without the raycast the puck spawns at player.y which can clip through geometry when
        // the player is crouching (ctrl held) or standing on an angled surface.
        Vector3 spawnPos = position + forward * 2f;
        spawnPos.y = position.y + 5f; // start the ray well above the player's feet
        if (Physics.Raycast(spawnPos, Vector3.down, out RaycastHit hit, 30f))
            spawnPos.y = hit.point.y + 0.05f; // just above whatever surface is below
        else
            spawnPos.y = position.y + 0.1f;   // fallback if nothing is underneath

        Puck newPuck = null;
        try
        {
            // Prefer the 3-param overload if it exists; fall back to the 2-param version below
            var m = MonoBehaviourSingleton<PuckManager>.Instance.GetType()
                .GetMethod("Server_SpawnPuck", new Type[] { typeof(Vector3), typeof(Quaternion), typeof(bool) });
            if (m != null)
                newPuck = m.Invoke(MonoBehaviourSingleton<PuckManager>.Instance,
                    new object[] { spawnPos, Quaternion.identity, false }) as Puck;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CustomLevel] 3-param spawn failed: {e.Message}");
        }

        if (newPuck == null)
            newPuck = MonoBehaviourSingleton<PuckManager>.Instance.Server_SpawnPuck(spawnPos, Quaternion.identity);

        if (newPuck == null)
        {
            Debug.LogWarning($"[CustomLevel] Failed to spawn puck for client {senderClientId}");
            return;
        }

        playerPucks[senderClientId] = newPuck;

        // Guard this puck from the game's out-of-bounds / face-off reset systems
        CustomLevelPlugin.ProtectedPucks.Add(newPuck);

        // Force-register the chunk slot immediately so the very first gather tick encodes
        // chunk-local instead of leaving a corrupted overflowed short in the packet
        SynchronizedObject syncObj = newPuck.GetComponent<SynchronizedObject>();
        if (syncObj != null)
            CL_ChunkSyncServer.InitSlot(syncObj);

        // Inherit the player's velocity so the puck doesn't appear stationary when spawned mid-skate
        Player player = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(senderClientId);
        if (player?.PlayerBody != null)
        {
            Rigidbody prb = player.PlayerBody.GetComponent<Rigidbody>();
            Rigidbody nrb = newPuck.GetComponent<Rigidbody>();
            if (prb != null && nrb != null) nrb.linearVelocity = prb.linearVelocity;
        }

        Debug.Log($"[CustomLevel] Spawned puck for client {senderClientId} at {spawnPos}");
    }

    private void Update()
    {
        if (UnityEngine.InputSystem.Keyboard.current == null) return;
        if (!UnityEngine.InputSystem.Keyboard.current.rKey.wasPressedThisFrame) return;
        if (!NetworkManager.Singleton.IsClient) return;

        // Don't spawn while the player is typing in any UI text field (e.g. chat)
        GameObject selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
        if (selected != null)
        {
            if (selected.GetComponent<UnityEngine.UI.InputField>() != null) return;
            if (selected.GetComponent<TMPro.TMP_InputField>() != null) return;
        }

        Player player = MonoBehaviourSingleton<PlayerManager>.Instance
            .GetPlayerByClientId(NetworkManager.Singleton.LocalClientId);
        if (player?.PlayerBody == null) return;

        Transform t = player.PlayerBody.transform;
        using (var writer = new FastBufferWriter(sizeof(float) * 6, Allocator.Temp))
        {
            writer.WriteValueSafe(t.position.x); writer.WriteValueSafe(t.position.y); writer.WriteValueSafe(t.position.z);
            writer.WriteValueSafe(t.forward.x); writer.WriteValueSafe(t.forward.y); writer.WriteValueSafe(t.forward.z);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                SpawnPuckMessage, NetworkManager.ServerClientId, writer);
        }
    }
}
