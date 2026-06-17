using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
using Unity.Netcode;
using Unity.Collections;

public static class CL_ChunkSyncServer
{
    public const string CmmName = "CUSTOMLEVEL/Chunks";
    // How far past the chunk edge before triggering a reassignment (hysteresis band)
    private const float FlipOut = 20f;
    // Ticks to delay a chunk switch so the client receives the announcement before it takes effect
    private const int DeferTicks = 50;
    // Beyond this distance from the chunk center we snap immediately without deferring
    private const float TeleportSnap = 48f;

    private static Harmony _harmony;
    private static bool _enabled;
    private static readonly List<SynchronizedObject> _tracked = new List<SynchronizedObject>();
    private static FieldInfo _tickIdField;

    public static void Enable()
    {
        if (_enabled) return;
        if (!NetworkManager.Singleton.IsServer)
        {
            Debug.Log("[CustomLevel] ChunkSyncServer: not server, skipping.");
            return;
        }

        _enabled = true;
        _tickIdField = AccessTools.Field(typeof(SynchronizedObjectManager), "serverLastSentTickId");

        // Seed objects that were already spawned before Enable() was called
        SynchronizedObjectManager mgr = NetworkBehaviourSingleton<SynchronizedObjectManager>.Instance;
        if (mgr != null)
        {
            FieldInfo f = AccessTools.Field(typeof(SynchronizedObjectManager), "synchronizedObjects");
            if (f?.GetValue(mgr) is IEnumerable<SynchronizedObject> existing)
                foreach (var obj in existing)
                    if (obj != null && !_tracked.Contains(obj))
                    { _tracked.Add(obj); InitSlot(obj); }
        }

        EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectSpawned",
            new Action<Dictionary<string, object>>(OnSpawned));
        EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectDespawned",
            new Action<Dictionary<string, object>>(OnDespawned));

        _harmony = new Harmony("customlevel.chunksync.server");
        var gather = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_GatherSynchronizedObjectData");
        if (gather != null)
            _harmony.Patch(gather, new HarmonyMethod(typeof(CL_ChunkSyncServer),
                nameof(GatherPrefix)));

        var force = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_ForceSynchronizeClientId");
        if (force != null)
            _harmony.Patch(force, new HarmonyMethod(typeof(CL_ChunkSyncServer),
                nameof(ForceSyncPrefix)));

        Debug.Log($"[CustomLevel] ChunkSyncServer enabled ({_tracked.Count} objects).");
    }

    public static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        _tracked.Clear();
        EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectSpawned",
            new Action<Dictionary<string, object>>(OnSpawned));
        EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectDespawned",
            new Action<Dictionary<string, object>>(OnDespawned));
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
    }

    private static void OnSpawned(Dictionary<string, object> msg)
    {
        if (!_enabled) return;
        if (msg["synchronizedObject"] is SynchronizedObject obj && !_tracked.Contains(obj))
        { _tracked.Add(obj); InitSlot(obj); }
    }

    private static void OnDespawned(Dictionary<string, object> msg)
    {
        if (!_enabled) return;
        if (msg["synchronizedObject"] is SynchronizedObject obj)
        { _tracked.Remove(obj); CL_ChunkRegistry.Remove((ushort)obj.NetworkObjectId); }
    }

    internal static void InitSlot(SynchronizedObject obj)
    {
        if (!_enabled || obj == null) return;
        ushort id = (ushort)obj.NetworkObjectId;
        ChunkCoord c = WorldToChunk(obj.transform.position);
        // Immediate announce (ushort.MaxValue) so the client knows the chunk before any position data arrives
        CL_ChunkRegistry.ApplyAnnounce(id, c, ushort.MaxValue);
        BroadcastInstant(id, c);
    }

    // Prefix runs before the vanilla gather so CurrentEncodeTickId is set for all encode calls in that frame
    public static void GatherPrefix()
    {
        if (!_enabled) return;
        ushort tick = GetTickId();
        CL_ChunkRegistry.CurrentEncodeTickId = tick;

        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            SynchronizedObject obj = _tracked[i];
            if (obj == null) { _tracked.RemoveAt(i); continue; }

            ushort id = (ushort)obj.NetworkObjectId;
            Vector3 pos = obj.transform.position;

            if (!CL_ChunkRegistry.TryGet(id, out ChunkSlot slot))
            { InitSlot(obj); continue; }

            ChunkCoord cur = slot.ResolveAt(tick);
            float dx = pos.x - cur.X * CL_ChunkRegistry.ChunkSize;
            float dz = pos.z - cur.Z * CL_ChunkRegistry.ChunkSize;

            // Teleport: skip deferred transition and snap to the new chunk immediately
            if (Mathf.Abs(dx) > TeleportSnap || Mathf.Abs(dz) > TeleportSnap)
            {
                ChunkCoord nc = WorldToChunk(pos);
                if (nc != cur) { CL_ChunkRegistry.ApplyAnnounce(id, nc, ushort.MaxValue); BroadcastInstant(id, nc); }
                continue;
            }

            ChunkCoord hyst = HysteresisCheck(pos, cur);
            if (hyst == cur) continue;

            // Don't re-broadcast a pending transition that's already in flight for the same target
            if (slot.HasPending && slot.Pending == hyst && !TickGE(tick, slot.PendingTickId)) continue;

            ushort switchTick = (ushort)((tick + DeferTicks) % ushort.MaxValue);
            CL_ChunkRegistry.ApplyAnnounce(id, hyst, switchTick);
            BroadcastSwitch(id, hyst, switchTick);
        }
    }

    // Called when a late-joining client needs a full state snapshot
    public static void ForceSyncPrefix(ulong clientId)
    {
        if (!_enabled || CL_ChunkRegistry.Count == 0) return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;
        // Host's local client receives state through the in-process path, not CMM
        if (nm.IsHost && clientId == nm.LocalClientId) return;

        try
        {
            ushort tick = GetTickId();
            ushort entryCount = 0;
            // Objects with an active pending transition need two entries: current + pending
            foreach (var kvp in CL_ChunkRegistry.Snapshot())
                entryCount += (ushort)(kvp.Value.HasPending && !TickGE(tick, kvp.Value.PendingTickId) ? 2 : 1);

            int cap = 3 + entryCount * 12 + 64;
            using (var writer = new FastBufferWriter(cap, Allocator.Temp, cap * 4))
            {
                byte type = 1;
                writer.WriteValueSafe(type);
                writer.WriteValueSafe(entryCount);

                foreach (var kvp in CL_ChunkRegistry.Snapshot())
                {
                    ChunkSlot s = kvp.Value;
                    bool hasPending = s.HasPending && !TickGE(tick, s.PendingTickId);
                    ushort key = kvp.Key;

                    if (hasPending)
                    {
                        // Send current first so the client can apply it, then the pending switch on top
                        writer.WriteValueSafe(key);
                        writer.WriteValueSafe(s.Current.X);
                        writer.WriteValueSafe(s.Current.Z);
                        ushort noSwitch = ushort.MaxValue;
                        writer.WriteValueSafe(noSwitch);

                        writer.WriteValueSafe(key);
                        writer.WriteValueSafe(s.Pending.X);
                        writer.WriteValueSafe(s.Pending.Z);
                        writer.WriteValueSafe(s.PendingTickId);
                    }
                    else
                    {
                        ChunkCoord c = s.ResolveAt(tick);
                        writer.WriteValueSafe(key);
                        writer.WriteValueSafe(c.X);
                        writer.WriteValueSafe(c.Z);
                        ushort noSwitch = ushort.MaxValue;
                        writer.WriteValueSafe(noSwitch);
                    }
                }
                nm.CustomMessagingManager.SendNamedMessage(CmmName, clientId, writer, NetworkDelivery.Reliable);
            }
            Debug.Log($"[CustomLevel] ChunkSyncServer: bulk snapshot sent to client {clientId}.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CustomLevel] ChunkSyncServer.ForceSyncPrefix failed: " + e.Message);
        }
    }

    private static void BroadcastInstant(ushort id, ChunkCoord c) => BroadcastSingle(id, c, ushort.MaxValue);
    private static void BroadcastSwitch(ushort id, ChunkCoord c, ushort tick) => BroadcastSingle(id, c, tick);

    private static void BroadcastSingle(ushort id, ChunkCoord c, ushort switchTick)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;
        // Host client receives state through the in-process path; don't count it
        int clients = nm.IsHost ? nm.ConnectedClientsIds.Count - 1 : nm.ConnectedClientsIds.Count;
        if (clients <= 0) return;

        try
        {
            using (var writer = new FastBufferWriter(8, Allocator.Temp, 32))
            {
                byte type = 0;
                writer.WriteValueSafe(type);
                writer.WriteValueSafe(id);
                writer.WriteValueSafe(c.X);
                writer.WriteValueSafe(c.Z);
                writer.WriteValueSafe(switchTick);
                nm.CustomMessagingManager.SendNamedMessageToAll(CmmName, writer, NetworkDelivery.Reliable);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CustomLevel] ChunkSyncServer.BroadcastSingle failed: " + e.Message);
        }
    }

    private static ChunkCoord WorldToChunk(Vector3 pos)
    {
        int x = Mathf.Clamp(Mathf.RoundToInt(pos.x / CL_ChunkRegistry.ChunkSize), -128, 127);
        int z = Mathf.Clamp(Mathf.RoundToInt(pos.z / CL_ChunkRegistry.ChunkSize), -128, 127);
        return new ChunkCoord((sbyte)x, (sbyte)z);
    }

    // Returns the current chunk unless the object has crossed FlipOut past the edge, preventing
    // rapid back-and-forth reassignments when an object sits near a chunk boundary
    private static ChunkCoord HysteresisCheck(Vector3 pos, ChunkCoord cur)
    {
        float ox = cur.X * CL_ChunkRegistry.ChunkSize;
        float oz = cur.Z * CL_ChunkRegistry.ChunkSize;
        int nx = cur.X, nz = cur.Z;
        float dx = pos.x - ox, dz = pos.z - oz;
        if (dx > FlipOut) nx = Mathf.Clamp(nx + 1, -128, 127);
        else if (dx < -FlipOut) nx = Mathf.Clamp(nx - 1, -128, 127);
        if (dz > FlipOut) nz = Mathf.Clamp(nz + 1, -128, 127);
        else if (dz < -FlipOut) nz = Mathf.Clamp(nz - 1, -128, 127);
        return new ChunkCoord((sbyte)nx, (sbyte)nz);
    }

    private static ushort GetTickId()
    {
        SynchronizedObjectManager mgr = NetworkBehaviourSingleton<SynchronizedObjectManager>.Instance;
        if (mgr == null || _tickIdField == null) return 0;
        try { return (ushort)_tickIdField.GetValue(mgr); } catch { return 0; }
    }

    // Unsigned comparison that handles ushort wrap-around (correct even when a > b crosses 65535→0)
    private static bool TickGE(ushort a, ushort b) => (ushort)(a - b) < 32768;
}
