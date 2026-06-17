using System;

// 32 m grid; sbyte gives ±127 chunks = ±4064 m range
public struct ChunkCoord : IEquatable<ChunkCoord>
{
    public sbyte X;
    public sbyte Z;
    public ChunkCoord(sbyte x, sbyte z) { X = x; Z = z; }
    public bool Equals(ChunkCoord other) => X == other.X && Z == other.Z;
    public static bool operator ==(ChunkCoord a, ChunkCoord b) => a.X == b.X && a.Z == b.Z;
    public static bool operator !=(ChunkCoord a, ChunkCoord b) => !(a == b);
    public override bool Equals(object obj) => obj is ChunkCoord c && Equals(c);
    public override int GetHashCode() => (X << 8) | (byte)Z;
    public override string ToString() => $"({X},{Z})";
}

public struct ChunkSlot
{
    public ChunkCoord Current;
    // Pending holds the next chunk assignment until its switch tick arrives
    public ChunkCoord Pending;
    public ushort PendingTickId;
    public bool HasPending;

    public ChunkCoord ResolveAt(ushort tickId)
    {
        if (!HasPending) return Current;
        // Unsigned subtraction wraps correctly when tickId crosses ushort.MaxValue
        return ((ushort)(tickId - PendingTickId) < 32768) ? Pending : Current;
    }
}
