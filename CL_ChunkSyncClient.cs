using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Unity.Netcode;

public static class CL_ChunkSyncClient
{
    // Positions that jump more than this in one tick are held at the last good value
    private const float RejectThreshold = 16f;
    // After this many consecutive drops we let the position through to avoid permanent stalls
    private const int MaxDrops = 20;

    private static Harmony _harmony;
    private static bool _enabled;
    private static readonly Dictionary<ushort, FilterState> _filter = new Dictionary<ushort, FilterState>();

    private struct FilterState
    {
        public Vector3 LastDecoded;
        public int ConsecutiveDrops;
        public bool Initialized;
    }

    public static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        _filter.Clear();

        _harmony = new Harmony("customlevel.chunksync.client");

        var rpcMethod = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_SynchronizeObjectsRpc");
        if (rpcMethod != null)
            _harmony.Patch(rpcMethod, new HarmonyMethod(typeof(CL_ChunkSyncClient), nameof(RpcPrefix)));

        var tick = AccessTools.Method(typeof(SynchronizedObject), "OnClientTick");
        if (tick != null)
            _harmony.Patch(tick, new HarmonyMethod(typeof(CL_ChunkSyncClient), nameof(OnClientTickPrefix)));

        var smoothTick = AccessTools.Method(typeof(SynchronizedObject), "OnClientSmoothTick");
        if (smoothTick != null)
            _harmony.Patch(smoothTick, new HarmonyMethod(typeof(CL_ChunkSyncClient), nameof(OnClientSmoothTickPrefix)));

        EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectDespawned",
            new Action<Dictionary<string, object>>(OnDespawned));

        Debug.Log("[CustomLevel] ChunkSyncClient enabled.");
    }

    public static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        _filter.Clear();
        EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectDespawned",
            new Action<Dictionary<string, object>>(OnDespawned));
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
    }

    private static void OnDespawned(Dictionary<string, object> msg)
    {
        if (msg["synchronizedObject"] is SynchronizedObject obj)
        {
            ushort id = (ushort)obj.NetworkObjectId;
            _filter.Remove(id);
            CL_ChunkRegistry.Remove(id);
        }
    }

    public static void OnChunkMessage(ulong senderId, FastBufferReader reader)
    {
        if (NetworkManager.Singleton.IsServer) return;
        try
        {
            reader.ReadValueSafe(out byte type);
            if (type == 0)
            {
                ReadSingle(reader);
            }
            else if (type == 1)
            {
                // Bulk snapshot sent on late join; apply all entries in sequence
                reader.ReadValueSafe(out ushort count);
                for (int i = 0; i < count; i++) ReadSingle(reader);
                Debug.Log($"[CustomLevel] ChunkSyncClient: applied bulk snapshot ({count} entries).");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CustomLevel] ChunkSyncClient.OnChunkMessage failed: " + e.Message);
        }
    }

    private static void ReadSingle(FastBufferReader reader)
    {
        reader.ReadValueSafe(out ushort id);
        reader.ReadValueSafe(out sbyte cx);
        reader.ReadValueSafe(out sbyte cz);
        reader.ReadValueSafe(out ushort switchTick);
        ChunkCoord coord = new ChunkCoord(cx, cz);
        CL_ChunkRegistry.ApplyAnnounce(id, coord, switchTick);
        SeedFilter(id, coord);
    }

    // Pre-populate the filter so the first decoded position isn't rejected as a large jump
    private static void SeedFilter(ushort id, ChunkCoord c)
    {
        if (_filter.TryGetValue(id, out FilterState fs) && fs.Initialized) return;
        fs.LastDecoded = new Vector3(c.X * CL_ChunkRegistry.ChunkSize, 0f, c.Z * CL_ChunkRegistry.ChunkSize);
        fs.Initialized = true;
        fs.ConsecutiveDrops = 0;
        _filter[id] = fs;
    }

    // Capture the tick id before any position decoding happens in the RPC so all objects in
    // the same batch resolve against the same snapshot of pending chunk transitions
    public static void RpcPrefix(ushort tickId)
    {
        CL_ChunkRegistry.CurrentDecodeTickId = tickId;
    }

    public static void OnClientTickPrefix(SynchronizedObject __instance, ref Vector3 position)
    {
        if (!_enabled || __instance == null) return;
        if (NetworkManager.Singleton?.IsServer == true) return;
        FilterAndReplace(__instance, ref position);
    }

    public static void OnClientSmoothTickPrefix(SynchronizedObject __instance, ref Vector3 position)
    {
        if (!_enabled || __instance == null) return;
        if (NetworkManager.Singleton?.IsServer == true) return;
        FilterAndReplace(__instance, ref position);
    }

    private static void FilterAndReplace(SynchronizedObject obj, ref Vector3 position)
    {
        ushort id = (ushort)obj.NetworkObjectId;

        // If chunks are active but we have no slot yet, hold the object in place until
        // the server's chunk announcement arrives; avoids applying an offset of zero
        if (CL_ChunkRegistry.ChunksActive && !CL_ChunkRegistry.TryGet(id, out _))
        { position = obj.transform.position; return; }

        // Vanilla decoded chunk-local shorts to a chunk-local float. Add the chunk origin here
        // to get the true world position. Doing this in the short layer (SyncPrefix) would
        // overflow the short again, so we expand at the float stage instead.
        if (CL_ChunkRegistry.TryGet(id, out ChunkSlot slot))
        {
            ChunkCoord c = slot.ResolveAt(CL_ChunkRegistry.CurrentDecodeTickId);
            position.x += c.X * CL_ChunkRegistry.ChunkSize;
            position.z += c.Z * CL_ChunkRegistry.ChunkSize;
        }

        _filter.TryGetValue(id, out FilterState fs);
        if (!fs.Initialized)
        {
            fs.LastDecoded = position; fs.Initialized = true; fs.ConsecutiveDrops = 0;
            _filter[id] = fs; return;
        }

        float dx = Mathf.Abs(position.x - fs.LastDecoded.x);
        float dz = Mathf.Abs(position.z - fs.LastDecoded.z);
        if ((dx > RejectThreshold || dz > RejectThreshold) && fs.ConsecutiveDrops < MaxDrops)
        {
            // Likely a chunk-offset glitch or a packet decoded against the wrong tick; suppress it
            position = fs.LastDecoded;
            fs.ConsecutiveDrops++;
        }
        else
        {
            fs.LastDecoded = position;
            fs.ConsecutiveDrops = 0;
        }
        _filter[id] = fs;
    }
}
