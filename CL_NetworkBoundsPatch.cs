using System;
using UnityEngine;
using HarmonyLib;

public static class CL_NetworkBoundsPatch
{
    private static Harmony _harmony;
    private static bool _patched;

    // Safe to call early (before chunks activate); patches are installed once and stay in place
    public static void EnsurePatched()
    {
        if (_patched) return;
        try
        {
            _harmony = new Harmony("customlevel.networkbounds");

            // Postfix rewrites the already-encoded X/Z from world-space to chunk-local-space
            var gather = AccessTools.Method(typeof(SynchronizedObjectManager),
                "Server_GatherSynchronizedObjectData");
            if (gather != null)
            {
                _harmony.Patch(gather, null,
                    new HarmonyMethod(typeof(CL_NetworkBoundsPatch), nameof(GatherPostfix)));
                Debug.Log("[CustomLevel] Patched Server_GatherSynchronizedObjectData");
            }
            else
                Debug.LogWarning("[CustomLevel] Server_GatherSynchronizedObjectData not found");

            // Prefix expands chunk-local X/Z back to world-space before the vanilla sync reads them
            var sync = AccessTools.Method(typeof(SynchronizedObjectManager),
                "Client_SynchronizeObjects");
            if (sync != null)
            {
                _harmony.Patch(sync,
                    new HarmonyMethod(typeof(CL_NetworkBoundsPatch), nameof(SyncPrefix)));
                Debug.Log("[CustomLevel] Patched Client_SynchronizeObjects");
            }
            else
                Debug.LogWarning("[CustomLevel] Client_SynchronizeObjects not found");

            _patched = true;
            Debug.Log("[CustomLevel] NetworkBoundsPatch installed.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CustomLevel] NetworkBoundsPatch failed: " + e.Message);
        }
    }

    public static void EnableChunks()
    {
        CL_ChunkRegistry.ChunksActive = true;
        CL_ChunkRegistry.CurrentEncodeTickId = 0;
        CL_ChunkRegistry.CurrentDecodeTickId = 0;
        CL_ChunkSyncServer.Enable();
        CL_ChunkSyncClient.Enable();
        Debug.Log("[CustomLevel] Chunk sync ACTIVE.");
    }

    public static void Disable()
    {
        CL_ChunkSyncServer.Disable();
        CL_ChunkSyncClient.Disable();
        CL_ChunkRegistry.ChunksActive = false;
        CL_ChunkRegistry.Clear();
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
        _patched = false;
        Debug.Log("[CustomLevel] NetworkBoundsPatch disabled.");
    }

    // Vanilla encode: X = (short)(worldX * precision)
    // We decode that back to worldX, subtract the chunk origin, then re-encode as chunk-local
    public static void GatherPostfix(ref SynchronizedObjectData[] __result)
    {
        if (!CL_ChunkRegistry.ChunksActive || __result == null) return;
        ushort tick = CL_ChunkRegistry.CurrentEncodeTickId;
        for (int i = 0; i < __result.Length; i++)
        {
            ushort id = __result[i].NetworkObjectId;
            if (!CL_ChunkRegistry.TryGet(id, out ChunkSlot slot)) continue;
            ChunkCoord c = slot.ResolveAt(tick);
            float worldX = __result[i].X / CL_ChunkRegistry.Precision;
            float worldZ = __result[i].Z / CL_ChunkRegistry.Precision;
            float ox = c.X * CL_ChunkRegistry.ChunkSize;
            float oz = c.Z * CL_ChunkRegistry.ChunkSize;
            __result[i].X = (short)((worldX - ox) * CL_ChunkRegistry.Precision);
            __result[i].Z = (short)((worldZ - oz) * CL_ChunkRegistry.Precision);
        }
    }

    // Incoming X is chunk-local; add the chunk origin so vanilla sync places the object correctly
    public static void SyncPrefix(ref SynchronizedObjectData[] synchronizedObjectsData)
    {
        if (!CL_ChunkRegistry.ChunksActive || synchronizedObjectsData == null) return;
        ushort tick = CL_ChunkRegistry.CurrentDecodeTickId;
        for (int i = 0; i < synchronizedObjectsData.Length; i++)
        {
            ushort id = synchronizedObjectsData[i].NetworkObjectId;
            if (!CL_ChunkRegistry.TryGet(id, out ChunkSlot slot)) continue;
            ChunkCoord c = slot.ResolveAt(tick);
            float localX = synchronizedObjectsData[i].X / CL_ChunkRegistry.Precision;
            float localZ = synchronizedObjectsData[i].Z / CL_ChunkRegistry.Precision;
            float ox = c.X * CL_ChunkRegistry.ChunkSize;
            float oz = c.Z * CL_ChunkRegistry.ChunkSize;
            synchronizedObjectsData[i].X = (short)((localX + ox) * CL_ChunkRegistry.Precision);
            synchronizedObjectsData[i].Z = (short)((localZ + oz) * CL_ChunkRegistry.Precision);
        }
    }
}
