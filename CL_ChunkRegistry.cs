using System.Collections.Generic;

public static class CL_ChunkRegistry
{
    public const float ChunkSize = 32f;
    // 655 fits a full ±32 m chunk-local offset into a short (max 32*655 = 20960 < 32767)
    public const float Precision = 655f;

    public static bool ChunksActive;
    // Set once per gather/sync call so all objects in a batch use the same tick
    public static ushort CurrentEncodeTickId;
    public static ushort CurrentDecodeTickId;

    private static readonly Dictionary<ushort, ChunkSlot> _slots = new Dictionary<ushort, ChunkSlot>();

    public static bool TryGet(ushort id, out ChunkSlot slot) => _slots.TryGetValue(id, out slot);
    public static void Remove(ushort id) => _slots.Remove(id);
    public static void Clear() => _slots.Clear();
    public static int Count => _slots.Count;
    public static IEnumerable<KeyValuePair<ushort, ChunkSlot>> Snapshot() => _slots;

    // switchTickId == ushort.MaxValue means apply immediately (no deferred transition)
    public static void ApplyAnnounce(ushort id, ChunkCoord chunk, ushort switchTickId)
    {
        _slots.TryGetValue(id, out ChunkSlot slot);
        if (switchTickId == ushort.MaxValue)
        {
            slot.Current = chunk;
            slot.HasPending = false;
            slot.Pending = default;
            slot.PendingTickId = 0;
        }
        else
        {
            // Commit any in-flight pending before queuing a new one
            if (slot.HasPending) slot.Current = slot.Pending;
            slot.Pending = chunk;
            slot.PendingTickId = switchTickId;
            slot.HasPending = true;
        }
        _slots[id] = slot;
    }

    public static short EncodeX(float worldX, ushort id)
    {
        if (!ChunksActive) return (short)(worldX * Precision);
        float offset = 0f;
        if (_slots.TryGetValue(id, out ChunkSlot slot))
            offset = slot.ResolveAt(CurrentEncodeTickId).X * ChunkSize;
        return (short)((worldX - offset) * Precision);
    }

    public static short EncodeY(float worldY) => (short)(worldY * Precision);

    public static short EncodeZ(float worldZ, ushort id)
    {
        if (!ChunksActive) return (short)(worldZ * Precision);
        float offset = 0f;
        if (_slots.TryGetValue(id, out ChunkSlot slot))
            offset = slot.ResolveAt(CurrentEncodeTickId).Z * ChunkSize;
        return (short)((worldZ - offset) * Precision);
    }

    public static float DecodeX(float encoded, ushort id)
    {
        float v = encoded / Precision;
        if (!ChunksActive) return v;
        if (_slots.TryGetValue(id, out ChunkSlot slot))
            v += slot.ResolveAt(CurrentDecodeTickId).X * ChunkSize;
        return v;
    }

    public static float DecodeY(float encoded) => encoded / Precision;

    public static float DecodeZ(float encoded, ushort id)
    {
        float v = encoded / Precision;
        if (!ChunksActive) return v;
        if (_slots.TryGetValue(id, out ChunkSlot slot))
            v += slot.ResolveAt(CurrentDecodeTickId).Z * ChunkSize;
        return v;
    }
}
